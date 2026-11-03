using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace FreshStart.Services.System;

/// <summary>
/// 系统通知（托盘气泡，Win10/11 会进入通知中心）。
/// 在独立 STA 线程上短时间持有 NotifyIcon，不依赖 WPF 消息循环，界面与命令行都能用。
/// </summary>
public static class WindowsNotifier
{
    /// <summary>气泡在托盘上停留的时间。</summary>
    private const int BalloonHoldMs = 9000;

    /// <summary>
    /// 显示系统通知。
    /// <paramref name="waitFor"/> 用于"进程马上要退出"的场景（如命令行无人值守）：
    /// 通知线程是后台线程，进程退出会立刻移除托盘图标并吞掉通知，
    /// 因此调用方可以等一小段时间，让通知至少显示出来。
    /// </summary>
    public static void Show(string title, string message, bool isError = false, TimeSpan? waitFor = null)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var icon = new NotifyIcon
                {
                    Icon = isError ? SystemIcons.Error : SystemIcons.Information,
                    Text = "FreshStart",
                    Visible = true
                };

                icon.BalloonTipTitle = title;
                icon.BalloonTipText = message;
                icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
                icon.ShowBalloonTip(BalloonHoldMs - 1000);
                Thread.Sleep(BalloonHoldMs);
                icon.Visible = false;
            }
            catch (Exception ex)
            {
                Core.Log.Warn($"系统通知失败: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "FreshStart.Notify"
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (waitFor is { } timeout && timeout > TimeSpan.Zero)
        {
            try { thread.Join(timeout); }
            catch (Exception ex) { Core.Log.Warn($"等待通知显示失败: {ex.Message}"); }
        }
    }
}
