using System.Diagnostics;
using FreshStart.Core;

namespace FreshStart.Services.Software;

/// <summary>
/// Clash Verge 的进程处理。它用 NSIS 装的是**用户态 exe**，升级安装器覆盖文件时必须先退出，
/// 否则会失败在「文件被占用」。这里只处理用户态进程：`clash-verge`（界面）、
/// `verge-mihomo` / `mihomo`（内核）。服务模式（clash-verge-service，TUN）由安装器自己处理。
/// </summary>
public static class ClashVergeProcess
{
    public const string PackageId = "ClashVergeRev.ClashVergeRev";

    private static readonly string[] ProcessNames = { "clash-verge", "verge-mihomo", "mihomo" };

    public static bool IsRunning => Collect().Count > 0;

    /// <summary>
    /// 升级前停止：先 CloseMainWindow 优雅退出，超时后强制结束整棵进程树。
    /// 返回 true 表示已无相关进程（可以安全覆盖安装）。
    /// </summary>
    public static async Task<bool> StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var running = Collect();
        if (running.Count == 0) return true;

        foreach (var process in running)
        {
            try { process.CloseMainWindow(); }
            catch (Exception ex) { Log.Warn($"关闭 Clash Verge 窗口失败: {ex.Message}"); }
            finally { process.Dispose(); }
        }

        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            if (!ct.CanBeCanceled || !ct.IsCancellationRequested)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                if (Collect().Count == 0) return true;
            }
        }

        Log.Warn("Clash Verge 未在超时内退出，强制结束进程树");
        foreach (var process in Collect())
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Warn($"结束 Clash Verge 进程失败: {ex.Message}"); }
            finally { process.Dispose(); }
        }

        await Task.Delay(400, ct).ConfigureAwait(false);
        return Collect().Count == 0;
    }

    private static List<Process> Collect()
    {
        var list = new List<Process>();
        foreach (var name in ProcessNames)
        {
            try { list.AddRange(Process.GetProcessesByName(name)); }
            catch (Exception ex) { Log.Warn($"枚举 {name} 进程失败: {ex.Message}"); }
        }
        return list;
    }
}
