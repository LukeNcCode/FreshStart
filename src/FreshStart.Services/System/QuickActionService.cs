using System.Diagnostics;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.System;

public enum QuickActionKind
{
    Launch,
    RestartExplorer
}

public sealed record QuickActionDefinition(
    string Id,
    string Title,
    string Description,
    string IconKey,
    string FileName,
    string Arguments,
    QuickActionKind Kind,
    RiskLevel Risk);

/// <summary>快捷操作清单。全部是"打开某个系统工具"，不改任何配置。</summary>
public static class QuickActionCatalog
{
    public static IReadOnlyList<QuickActionDefinition> All { get; } = new List<QuickActionDefinition>
    {
        new("restart-explorer", "重启资源管理器", "任务栏/桌面卡死时使用，会短暂黑屏", "explorer", string.Empty, string.Empty, QuickActionKind.RestartExplorer, RiskLevel.Caution),
        new("power-options", "电源选项", "打开电源计划与合盖行为设置", "power", "control.exe", "powercfg.cpl", QuickActionKind.Launch, RiskLevel.Safe),
        new("control-panel", "控制面板", "经典控制面板入口", "sliders", "control.exe", string.Empty, QuickActionKind.Launch, RiskLevel.Safe),
        new("device-manager", "设备管理器", "查看与更新硬件驱动", "chip", "devmgmt.msc", string.Empty, QuickActionKind.Launch, RiskLevel.Safe),
        new("disk-management", "磁盘管理", "分区、盘符与虚拟磁盘", "disk", "diskmgmt.msc", string.Empty, QuickActionKind.Launch, RiskLevel.Safe),
        new("services", "服务", "管理系统服务启动类型", "gear", "services.msc", string.Empty, QuickActionKind.Launch, RiskLevel.Caution),
        new("regedit", "注册表编辑器", "高级用户使用，修改前请先备份", "terminal", "regedit.exe", string.Empty, QuickActionKind.Launch, RiskLevel.Caution),
        new("env-vars", "环境变量", "编辑用户/系统 PATH 等变量", "code", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables", QuickActionKind.Launch, RiskLevel.Caution),
        new("system-properties", "系统属性", "计算机名、性能选项与远程设置", "info", "sysdm.cpl", string.Empty, QuickActionKind.Launch, RiskLevel.Safe),
        new("cmd", "命令提示符", "以管理员身份打开 CMD", "terminal", "cmd.exe", string.Empty, QuickActionKind.Launch, RiskLevel.Caution),
        new("powershell", "PowerShell", "以管理员身份打开 PowerShell", "terminal", "powershell.exe", string.Empty, QuickActionKind.Launch, RiskLevel.Caution)
    };
}

public sealed class QuickActionService
{
    private readonly ExplorerService _explorer = new();

    public Task<QuickActionDefinition> ExecuteAsync(QuickActionDefinition action, CancellationToken ct = default)
    {
        if (action.Kind == QuickActionKind.RestartExplorer)
        {
            return _explorer.RestartAsync(ct).ContinueWith(_ => action, ct);
        }

        // 提权进程里 control.exe powercfg.cpl 有时不会显示窗口；
        // 改由 explorer 打开 shell 命名空间，走用户会话更可靠。
        if (string.Equals(action.Id, "power-options", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo(
                    "explorer.exe", "shell:::{025A5937-A6BE-4686-A844-36FE4BEC8B6D}")
                {
                    UseShellExecute = true
                });
                Log.Info("快捷操作: 电源选项 -> explorer shell:::{025A5937-A6BE-4686-A844-36FE4BEC8B6D}");
                return Task.FromResult(action);
            }
            catch (Exception ex)
            {
                Log.Warn($"通过 explorer 打开电源选项失败，回退 control.exe: {ex.Message}");
            }
        }

        var info = new ProcessStartInfo
        {
            FileName = action.FileName,
            Arguments = action.Arguments,
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        Process.Start(info);
        Log.Info($"快捷操作: {action.Title} -> {action.FileName} {action.Arguments}".Trim());
        return Task.FromResult(action);
    }
}

/// <summary>
/// 重启资源管理器。这里不额外启动进程：结束 shell 后 Windows 会自动以用户身份重启，
/// 从提权进程启动 explorer.exe 反而会得到高完整性级别的外壳。
/// </summary>
public sealed class ExplorerService
{
    public async Task RestartAsync(CancellationToken ct = default)
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            try
            {
                process.Kill(entireProcessTree: false);
                killed++;
            }
            catch (Exception ex)
            {
                Log.Warn($"结束 explorer 失败: {ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }

        Log.Info($"已结束 {killed} 个 explorer 进程，等待系统自动重启");

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
            if (Process.GetProcessesByName("explorer").Length > 0) return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            Log.Info("系统未自动重启 explorer，已手动启动");
        }
        catch (Exception ex)
        {
            Log.Error("启动 explorer 失败", ex);
            throw;
        }
    }
}
