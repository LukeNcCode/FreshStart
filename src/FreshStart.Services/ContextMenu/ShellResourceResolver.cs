using System.Runtime.InteropServices;
using System.Text;

namespace FreshStart.Services.ContextMenu;

/// <summary>
/// 解析注册表里的间接字符串（如 @shell32.dll,-8518），让系统菜单项显示中文名称。
/// </summary>
internal static class ShellResourceResolver
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source, StringBuilder output, int outputLength, IntPtr reserved);

    public static string Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (!value.StartsWith('@')) return value.Trim();

        try
        {
            var buffer = new StringBuilder(1024);
            if (SHLoadIndirectString(value, buffer, buffer.Capacity, IntPtr.Zero) == 0)
            {
                return buffer.ToString().Trim();
            }
        }
        catch
        {
            // 忽略资源解析失败，退回原始值
        }

        return value.Trim();
    }
}
