using FreshStart.App.Dialogs;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;

namespace FreshStart.App.Services;

/// <summary>
/// 写入前确认 + 本次启动首次写入的还原点决策。
/// 安全级且不需要询问还原点时直接放行，避免无意义弹窗。
/// </summary>
public sealed class OperationGate : IOperationGate
{
    private readonly DialogService _dialogs;

    public OperationGate(DialogService dialogs) => _dialogs = dialogs;

    public async Task<OperationConsent> RequestConsentAsync(
        OperationPreview preview, bool askRestorePoint, CancellationToken ct = default)
    {
        if (!askRestorePoint && preview.Risk == RiskLevel.Safe)
        {
            return new OperationConsent { Approved = true, RestorePoint = RestorePointChoice.NotDecided };
        }

        var details = new List<string>
        {
            $"操作：{preview.Name}",
            $"风险等级：{RiskText(preview.Risk)}",
            $"可回滚：{(preview.CanRollback ? "是（使用写入前保存的 JSON 快照）" : "否，请谨慎操作")}"
        };

        if (!string.IsNullOrWhiteSpace(preview.Description)) details.Add(preview.Description);

        foreach (var change in preview.Changes.Take(8))
        {
            details.Add($"{change.Kind}：{change.Target}\n{change.Before ?? "（不存在）"} → {change.After ?? "（删除）"}\n影响：{change.Impact}");
        }
        if (preview.Changes.Count > 8)
        {
            details.Add($"……以及另外 {preview.Changes.Count - 8} 项变更");
        }
        if (preview.RequiresRestart) details.Add("此操作需要重启资源管理器或系统后生效。");

        if (preview.Risk == RiskLevel.Safe)
        {
            details.Add("本次为安全级写入：不会创建系统还原点，仅生成 JSON 配置快照。");
        }
        else if (askRestorePoint)
        {
            details.Add("系统还原点通常需要 2–10 秒创建；Windows 默认 24 小时内只允许创建一次。");
        }

        var footer = "无论是否创建系统还原点，都会先生成 JSON 配置快照。";
        if (askRestorePoint)
        {
            footer += " 选择只在本次运行内有效，下次启动会重新询问。";
        }
        footer += " 点击弹窗外区域不会取消；取消请点击「取消」或按 Esc。";

        var request = new DialogRequest
        {
            Title = askRestorePoint ? "写入前确认：还原点会话策略" : "确认执行此操作？",
            Message = askRestorePoint
                ? "这是本次启动后的第一次修改。请确认变更内容，并选择是否为本次会话启用系统还原点（仅高风险操作会实际创建）。"
                : "请确认下面的变更内容。",
            Risk = preview.Risk,
            Details = details,
            Footer = footer,
            Actions = askRestorePoint
                ? new[]
                {
                    new DialogAction("restore-on", "启用并继续", isPrimary: true),
                    new DialogAction("restore-off", "不启用，继续"),
                    new DialogAction("cancel", "取消", isCancel: true)
                }
                : new[]
                {
                    new DialogAction("confirm", "继续", isPrimary: true, isDestructive: preview.Risk == RiskLevel.High),
                    new DialogAction("cancel", "取消", isCancel: true)
                }
        };

        var result = await _dialogs.ShowAsync(request, ct).ConfigureAwait(true);
        if (result.Cancelled)
        {
            Log.Info("用户在确认弹窗中取消了操作（已取消，未写入任何内容）");
            return new OperationConsent { Approved = false };
        }

        return new OperationConsent
        {
            Approved = true,
            RestorePoint = result.Key switch
            {
                "restore-on" => RestorePointChoice.EnabledForSession,
                "restore-off" => RestorePointChoice.DisabledForSession,
                _ => RestorePointChoice.NotDecided
            }
        };
    }

    private static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.High => "高风险",
        RiskLevel.Caution => "谨慎",
        _ => "安全"
    };
}
