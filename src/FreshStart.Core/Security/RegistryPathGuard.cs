namespace FreshStart.Core.Security;

/// <summary>
/// 注册表写入白名单。任何操作在落笔前都必须经过这里，
/// 防止"一键优化"因为路径拼接错误误删系统键。
/// </summary>
public static class RegistryPathGuard
{
    // 只允许写入这些前缀。注意：这是"允许改"而不是"鼓励改"。
    private static readonly string[] AllowedPrefixes =
    {
        @"HKCU\Software\FreshStart",
        @"HKCU\Software\Classes",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion",
        @"HKCU\Software\Microsoft\Windows\DWM",
        @"HKCU\Software\Microsoft\GameBar",
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes",
        @"HKCU\Control Panel",
        @"HKCU\System",
        @"HKCU\Environment",
        @"HKCU\Software\Policies\Microsoft",
        @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"HKLM\SOFTWARE\Microsoft\WindowsUpdate",
        @"HKLM\SOFTWARE\Microsoft\Windows Defender",
        @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        @"HKLM\SOFTWARE\Policies\Microsoft",
        @"HKLM\SOFTWARE\Classes",
        @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion",
        @"HKLM\SYSTEM\CurrentControlSet\Services",
        @"HKLM\SYSTEM\CurrentControlSet\Control"
    };

    /// <summary>裸根键永远禁止写入（例如把 HKLM\SOFTWARE 整个删掉）。</summary>
    private static readonly HashSet<string> ForbiddenRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "HKCU", "HKLM", "HKCR", "HKU", "HKCC",
        @"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SYSTEM", @"HKLM\SYSTEM\CurrentControlSet",
        @"HKLM\SOFTWARE\Microsoft", @"HKLM\SOFTWARE\Policies", @"HKCU\Software\Microsoft"
    };

    public static bool IsAllowed(string hive, string subKey)
    {
        var full = Normalize(hive, subKey);
        if (ForbiddenRoots.Contains(full)) return false;

        // 必须是"前缀本身"或"前缀 + 分隔符"开头，
        // 否则 HKCU\EnvironmentXYZ 这类同前缀兄弟键会被误判为白名单内
        foreach (var prefix in AllowedPrefixes)
        {
            if (full.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static void EnsureWritable(string hive, string subKey)
    {
        var full = Normalize(hive, subKey);
        if (!IsAllowed(hive, subKey))
        {
            throw new UnauthorizedAccessException($"注册表路径不在白名单内，已拒绝写入：{full}");
        }
    }

    public static string Normalize(string hive, string subKey)
    {
        var h = hive.Trim().TrimEnd('\\').ToUpperInvariant();
        var s = (subKey ?? string.Empty).Trim().Trim('\\');
        return s.Length == 0 ? h : $"{h}\\{s}";
    }
}
