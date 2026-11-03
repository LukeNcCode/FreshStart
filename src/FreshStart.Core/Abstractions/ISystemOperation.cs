namespace FreshStart.Core.Operations;

/// <summary>
/// 所有写操作的统一契约。
/// 实现方只负责"读当前值 / 执行 / 回滚"三件事，备份落盘、锁、审计由 OperationRunner 统一完成。
/// </summary>
public interface ISystemOperation
{
    /// <summary>稳定标识，写入历史与配置文件，发布后不要改。</summary>
    string Id { get; }

    string Name { get; }

    string Description { get; }

    RiskLevel RiskLevel { get; }

    /// <summary>是否支持回滚。不可回滚的操作必须在 UI 上明示。</summary>
    bool CanRollback { get; }

    /// <summary>列出将发生的变更（用于预演弹窗）。此方法必须只读、快速、不抛异常。</summary>
    IReadOnlyList<ChangeDescriptor> DescribeChanges();

    /// <summary>读取当前系统状态。必须在校验通过后、写入前调用。</summary>
    Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct);

    /// <summary>执行写入。实现必须幂等，并在写入后读回校验。</summary>
    Task<OperationResult> ApplyAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct);

    /// <summary>按快照恢复。实现必须能够在"部分写入"的情况下尽可能恢复。</summary>
    Task<OperationResult> RollbackAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct);
}
