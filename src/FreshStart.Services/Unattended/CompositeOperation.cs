using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.Unattended;

/// <summary>
/// 把多个操作串成一个原子批次：统一快照、一次确认、顺序执行，
/// 任一步失败时按逆序回滚（不可回滚的步骤会跳过并记入日志）。
/// </summary>
public sealed class CompositeOperation : OperationBase
{
    private readonly IReadOnlyList<ISystemOperation> _operations;
    private readonly bool _continueOnError;

    public CompositeOperation(
        string id,
        string name,
        string description,
        RiskLevel risk,
        IReadOnlyList<ISystemOperation> operations,
        bool continueOnError = false)
    {
        if (operations.Count == 0) throw new ArgumentException("没有可执行的操作", nameof(operations));

        Id = id;
        Name = name;
        Description = description;
        RiskLevel = risk;
        _operations = operations;
        _continueOnError = continueOnError;
    }

    /// <summary>继续执行模式下收集的失败步骤（步骤名 + 原因）。</summary>
    public List<string> Failures { get; } = new();

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override RiskLevel RiskLevel { get; }
    public override bool CanRollback => _operations.Any(operation => operation.CanRollback);

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges()
        => _operations.SelectMany(operation => operation.DescribeChanges()).ToList();

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entries = new List<SnapshotEntry>();
        foreach (var operation in _operations)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await operation.CaptureCurrentStateAsync(ct).ConfigureAwait(false);
            entries.Add(new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = operation.Id,
                Existed = true,
                DataJson = JsonSerializer.Serialize(snapshot, JsonConfig.Options)
            });
        }

        return CreateSnapshot(entries);
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var snapshots = DeserializeSnapshots(state);
        var applied = 0;

        for (var i = 0; i < _operations.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var operation = _operations[i];
            var snapshot = snapshots[i];

            progress?.Report(new OperationStep
            {
                Message = $"（{i + 1}/{_operations.Count}）{operation.Name}",
                Percent = (int)(i * 100.0 / _operations.Count)
            });

            var result = await operation.ApplyAsync(snapshot, progress, ct).ConfigureAwait(false);
            if (result.Success)
            {
                applied++;
                continue;
            }

            var message = $"{operation.Name}：{result.Message}";
            if (!_continueOnError) throw new InvalidOperationException(message);

            Failures.Add(message);
            Log.Warn($"步骤失败（继续执行）: {message}");
            progress?.Report(new OperationStep
            {
                Message = $"跳过失败步骤：{operation.Name}",
                Percent = (int)((i + 1) * 100.0 / _operations.Count),
                IsError = true
            });
        }

        if (Failures.Count > 0)
        {
            // 一步都没成功时整体算失败：不回滚也没有副作用，但不能对外报"成功"
            if (applied == 0)
            {
                return OperationResult.Fail(
                    $"全部 {Failures.Count} 步都失败：{Failures[0]}");
            }

            return OperationResult.Ok(
                $"已完成 {applied} 步，{Failures.Count} 步失败并跳过（成功步骤保持应用）");
        }

        return OperationResult.Ok($"已执行 {_operations.Count} 个步骤");
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var snapshots = DeserializeSnapshots(state);
        var restored = 0;
        var skipped = 0;

        for (var i = _operations.Count - 1; i >= 0; i--)
        {
            var operation = _operations[i];
            if (!operation.CanRollback)
            {
                skipped++;
                continue;
            }

            progress?.Report(new OperationStep
            {
                Message = $"正在回滚：{operation.Name}",
                Percent = 70
            });

            var result = await operation.RollbackAsync(snapshots[i], progress, CancellationToken.None)
                .ConfigureAwait(false);
            if (result.Success) restored++;
            else Log.Warn($"复合操作回滚失败: {operation.Id} - {result.Message}");
        }

        return OperationResult.Ok(skipped > 0
            ? $"已回滚 {restored} 步，{skipped} 步不可回滚（如卸载/安装）"
            : $"已回滚 {restored} 步");
    }

    private List<OperationSnapshot> DeserializeSnapshots(OperationSnapshot state)
    {
        if (state.Entries.Count != _operations.Count)
        {
            throw new InvalidOperationException("复合操作的快照数量与步骤不一致");
        }

        var list = new List<OperationSnapshot>(_operations.Count);
        for (var i = 0; i < _operations.Count; i++)
        {
            var entry = state.Entries[i];
            if (entry.DataJson is null)
            {
                throw new InvalidOperationException($"缺少步骤快照：{_operations[i].Id}");
            }

            list.Add(JsonSerializer.Deserialize<OperationSnapshot>(entry.DataJson, JsonConfig.Options)
                     ?? throw new InvalidOperationException($"步骤快照损坏：{_operations[i].Id}"));
        }

        return list;
    }
}
