using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using FreshStart.Services.Tuning;

namespace FreshStart.Services.Restore;

public sealed class RestoreBaseline
{
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public List<RegistryValueState> Values { get; set; } = new();
}

/// <summary>
/// 全局还原：首次使用时记录 FreshStart 管理的注册表调控项基准，
/// 之后可一键恢复到该基准。覆盖范围仅限 TuningCatalog 中的注册表值。
/// </summary>
public sealed class GlobalRestoreService
{
    private readonly RegistryService _registry;

    /// <summary>旧版本把基准放在数据根目录，读取时迁移到 state\baseline\。</summary>
    private static readonly string LegacyPath = Path.Combine(AppPaths.Root, "baseline.json");

    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public GlobalRestoreService(RegistryService registry)
    {
        _registry = registry;
        _path = MigrateLegacyBaseline();
    }

    private static string MigrateLegacyBaseline()
    {
        var target = AppPaths.BaselineFile;
        try
        {
            if (!File.Exists(target) && File.Exists(LegacyPath))
            {
                Directory.CreateDirectory(AppPaths.BaselineRoot);
                File.Move(LegacyPath, target);
                Log.Info("全局基准已迁移到 state\\baseline\\baseline.json");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"迁移全局基准失败，继续使用旧位置: {ex.Message}");
            return LegacyPath;
        }

        return target;
    }

    public RestoreBaseline? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonSerializer.Deserialize<RestoreBaseline>(File.ReadAllText(_path), JsonConfig.Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取全局基准失败: {ex.Message}");
            return null;
        }
    }

    public async Task EnsureBaselineAsync()
    {
        try
        {
            if (File.Exists(_path)) return;
            await CreateBaselineAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("自动创建全局还原基准失败", ex);
        }
    }

    public async Task<RestoreBaseline> CreateBaselineAsync()
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var baseline = new RestoreBaseline { CreatedAt = DateTimeOffset.Now };
            foreach (var option in TuningCatalog.Options)
            {
                baseline.Values.Add(_registry.CaptureValue(
                    option.Target.Hive, option.Target.View, option.Target.SubKey, option.Target.Name));
            }
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(baseline, JsonConfig.Options));
            Log.Info($"全局基准已创建：{baseline.Values.Count} 项");
            return baseline;
        }
        finally
        {
            _lock.Release();
        }
    }

    public GlobalRestoreOperation CreateOperation(RestoreBaseline baseline)
        => new(_registry, baseline);
}

/// <summary>把 TuningCatalog 的所有注册表值恢复到基准；失败自动回滚到还原前状态。</summary>
public sealed class GlobalRestoreOperation : OperationBase
{
    private readonly RegistryService _registry;
    private readonly RestoreBaseline _baseline;

    public GlobalRestoreOperation(RegistryService registry, RestoreBaseline baseline)
    {
        _registry = registry;
        _baseline = baseline;
    }

    public override string Id => "global.restore";
    public override string Name => "全局还原到首次基准";
    public override string Description => "把 FreshStart 管理的注册表调控项恢复到基准；不包含服务、计划任务、驱动与已卸载程序";
    public override RiskLevel RiskLevel => RiskLevel.High;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => _baseline.Values.Select(value =>
        new ChangeDescriptor
        {
            Target = value.FullPath,
            Kind = "注册表值",
            Before = "当前值",
            After = value.Existed ? Format(value) : "（删除）",
            Impact = "恢复到基准值；重启或重新登录后生效"
        }).ToList();

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entries = _baseline.Values.Select(value =>
        {
            var current = _registry.CaptureValue(value.Hive, value.View, value.SubKey, value.Name);
            return new SnapshotEntry
            {
                Kind = SnapshotKind.RegistryValue,
                Target = current.FullPath,
                ValueName = current.Name,
                Existed = current.Existed,
                ValueType = current.ValueType,
                Sddl = current.Sddl,
                DataJson = JsonSerializer.Serialize(current, JsonConfig.Options)
            };
        }).ToList();
        return Task.FromResult(CreateSnapshot(entries));
    }

    protected override Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        for (var i = 0; i < _baseline.Values.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var value = _baseline.Values[i];
            progress?.Report(new OperationStep
            {
                Message = $"正在还原 {value.FullPath}…",
                Percent = (int)((i + 1) * 95.0 / Math.Max(1, _baseline.Values.Count))
            });
            _registry.ApplyValue(value);
            Ensure(_registry.ValueEquals(value), $"还原后校验失败：{value.FullPath}");
        }
        return Task.FromResult(OperationResult.Ok($"已还原 {_baseline.Values.Count} 项注册表设置"));
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var restored = 0;
        foreach (var entry in state.Entries.Where(item => item.Kind == SnapshotKind.RegistryValue))
        {
            var original = JsonSerializer.Deserialize<RegistryValueState>(entry.DataJson!, JsonConfig.Options);
            if (original is null) continue;
            _registry.ApplyValue(original);
            restored++;
        }
        return Task.FromResult(OperationResult.Ok($"已回滚 {restored} 项"));
    }

    private static string Format(RegistryValueState value) => value.ValueType switch
    {
        nameof(Microsoft.Win32.RegistryValueKind.DWord) => value.DwordData?.ToString() ?? "0",
        nameof(Microsoft.Win32.RegistryValueKind.QWord) => value.QwordData?.ToString() ?? "0",
        _ => value.StringData ?? string.Empty
    };
}
