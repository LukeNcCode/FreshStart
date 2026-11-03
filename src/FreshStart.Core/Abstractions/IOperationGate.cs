using FreshStart.Core.Operations;

namespace FreshStart.Core.Abstractions;

/// <summary>会话内是否创建系统还原点的决策。只在进程内有效，重启后重置。</summary>
public enum RestorePointChoice
{
    NotDecided = 0,
    EnabledForSession = 1,
    DisabledForSession = 2
}

public sealed class OperationConsent
{
    public bool Approved { get; set; }
    public RestorePointChoice RestorePoint { get; set; } = RestorePointChoice.NotDecided;
}

/// <summary>
/// 写入前的用户确认入口，由 UI 层实现（自绘模态弹窗）。
/// 合并两件事：操作确认 + 本次启动首次写入时的还原点决策。
/// </summary>
public interface IOperationGate
{
    Task<OperationConsent> RequestConsentAsync(OperationPreview preview, bool askRestorePoint, CancellationToken ct = default);
}

public sealed class RestorePointResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>系统还原点。srclient.dll 调用可能阻塞数秒，实现必须在线程池执行。</summary>
public interface IRestorePointService
{
    bool SystemProtectionEnabled { get; }
    Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default);
}
