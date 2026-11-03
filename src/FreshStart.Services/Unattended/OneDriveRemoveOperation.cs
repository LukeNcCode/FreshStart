using System.Diagnostics;
using System.IO;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.Unattended;

/// <summary>
/// 卸载 OneDrive（Win32 客户端）：结束进程后调用系统自带的 OneDriveSetup.exe /uninstall。
/// 卸载不可回滚；卸载器可能保留用户数据目录，这里只负责调用官方卸载程序。
/// </summary>
public sealed class OneDriveRemoveOperation : OperationBase
{
    public override string Id => "unattended.onedrive.remove";
    public override string Name => "卸载 OneDrive";
    public override string Description => "结束 OneDrive 进程并调用 OneDriveSetup.exe /uninstall；不可回滚";
    public override RiskLevel RiskLevel => RiskLevel.High;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = "OneDrive",
            Kind = "Win32 应用",
            Before = "已安装",
            After = "卸载（保留本地用户数据目录）",
            Impact = "调用官方卸载程序，可能需要重新登录才能恢复；不可自动回滚"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        => Task.FromResult(CreateSnapshot(new[]
        {
            new SnapshotEntry
            {
                Kind = SnapshotKind.Other,
                Target = "OneDrive",
                Existed = IsInstalled
            }
        }));

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在结束 OneDrive 进程…", Percent = 30 });
        foreach (var running in Process.GetProcessesByName("OneDrive"))
        {
            try { running.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Warn($"结束 OneDrive 进程失败: {ex.Message}"); }
        }

        var setup = FindSetup();
        if (setup is null)
        {
            return OperationResult.Fail("未找到 OneDriveSetup.exe，可能本机没有安装 OneDrive");
        }

        progress?.Report(new OperationStep { Message = $"正在运行 {Path.GetFileName(setup)} /uninstall…", Percent = 60 });

        var startInfo = new ProcessStartInfo(setup, "/uninstall")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process is null) return OperationResult.Fail("无法启动 OneDrive 卸载程序");

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return OperationResult.Fail(timeout.IsCancellationRequested
                ? "OneDrive 卸载程序超时（5 分钟）"
                : "已取消 OneDrive 卸载");
        }

        Log.Info($"OneDrive 卸载程序退出码: {process.ExitCode}");
        return process.ExitCode == 0
            ? OperationResult.Ok("OneDrive 已卸载（本地数据目录可能仍保留）")
            : OperationResult.Fail(
                $"OneDrive 卸载程序返回 {process.ExitCode}（0x{unchecked((uint)process.ExitCode):X8}）；" +
                $"可重启后手动运行 \"{setup}\" /uninstall，或到「设置 → 应用」里卸载 OneDrive 后重试");
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Fail("OneDrive 卸载不可回滚，请从官网重新安装"));

    public static bool IsInstalled
    {
        get
        {
            if (FindSetup() is not null) return true;
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "OneDrive");
            return Directory.Exists(local);
        }
    }

    private static string? FindSetup()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OneDriveSetup.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "OneDrive", "OneDriveSetup.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
