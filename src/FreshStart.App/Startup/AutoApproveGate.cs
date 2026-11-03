using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;

namespace FreshStart.App.Startup;

/// <summary>
/// 命令行无人值守专用的网关：剧本本身就是用户授权，因此不再逐条弹窗。
/// 仍然会走备份、快照、校验与回滚流程。
/// </summary>
public sealed class AutoApproveGate : IOperationGate
{
    public Task<OperationConsent> RequestConsentAsync(
        OperationPreview preview, bool askRestorePoint, CancellationToken ct = default)
        => Task.FromResult(new OperationConsent
        {
            Approved = true,
            RestorePoint = RestorePointChoice.NotDecided
        });
}
