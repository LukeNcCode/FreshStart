using System.Runtime.InteropServices;

namespace FreshStart.App.Services;

/// <summary>把已有窗口带回前台。提权进程受前台锁限制，失败时退化为闪烁任务栏。</summary>
public static class WindowActivation
{
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    public static void SetForegroundWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (SetForegroundWindowNative(hwnd)) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = hwnd,
            dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG
            uCount = 3,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }
}
