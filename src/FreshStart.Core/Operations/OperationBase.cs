using FreshStart.Core.Operations;

namespace FreshStart.Core.Operations;

/// <summary>
/// 操作基类：统一异常处理与快照构造，子类只实现 Core 逻辑。
/// 写入后必须读回校验，这是"不允许裸写"的最后一道保险。
/// </summary>
public abstract class OperationBase : ISystemOperation
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract RiskLevel RiskLevel { get; }
    public virtual bool CanRollback => true;

    public abstract IReadOnlyList<ChangeDescriptor> DescribeChanges();

    public OperationPreview BuildPreview() => new()
    {
        OperationId = Id,
        Name = Name,
        Description = Description,
        Risk = RiskLevel,
        CanRollback = CanRollback,
        Changes = DescribeChanges().ToList()
    };

    public abstract Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct);

    public async Task<OperationResult> ApplyAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        try
        {
            var result = await ApplyCoreAsync(state, progress, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"操作 {Id} 执行失败", ex);
            return OperationResult.Fail($"执行失败：{ex.Message}", ex);
        }
    }

    public async Task<OperationResult> RollbackAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        try
        {
            return await RollbackCoreAsync(state, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"操作 {Id} 回滚失败", ex);
            return OperationResult.Fail($"回滚失败：{ex.Message}", ex);
        }
    }

    protected abstract Task<OperationResult> ApplyCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct);
    protected abstract Task<OperationResult> RollbackCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct);

    protected OperationSnapshot CreateSnapshot(IEnumerable<SnapshotEntry> entries) => new()
    {
        OperationId = Id,
        OperationName = Name,
        Risk = RiskLevel,
        Entries = entries.ToList()
    };

    /// <summary>写入后读回比对，失败时抛出，由 Runner 触发回滚。</summary>
    protected static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
