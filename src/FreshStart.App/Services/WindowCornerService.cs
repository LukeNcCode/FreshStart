using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FreshStart.App.Services;

/// <summary>使用 Windows 11 DWM 原生圆角；旧系统仍由 WPF 外框兜底。</summary>
public static class WindowCornerService
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    public static void Apply(Window window)
    {
        try
        {
            if (Environment.OSVersion.Version.Build < 22000) return;
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var preference = DwmWindowCornerRound;
            _ = DwmSetWindowAttribute(
                handle,
                DwmWindowCornerPreference,
                ref preference,
                Marshal.SizeOf<int>());
        }
        catch
        {
            // WPF 圆角外框仍是兼容回退。
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
