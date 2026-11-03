namespace FreshStart.Services.Software;

/// <summary>官方直链安装器（winget 失败时的兜底通道）。</summary>
public sealed class DirectInstaller
{
    /// <summary>关联的 winget ID（用于在批量安装里对应到清单条目）。</summary>
    public string WingetId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>官方下载地址（必须 HTTPS）。</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>下载后的文件名（决定安装器扩展名）。</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>静默安装参数（仅接受这里登记的固定值，不接收外部输入）。</summary>
    public string SilentArguments { get; init; } = string.Empty;

    /// <summary>可选 SHA256 固定值校验；所有条目仍必须通过 Authenticode 签名校验。</summary>
    public string? Sha256 { get; init; }

    /// <summary>安装后检测关键词（匹配 ARP 显示名或安装目录）。</summary>
    public string DetectPattern { get; init; } = string.Empty;
}

/// <summary>
/// 官方直链清单。只收录**实测可下载**的条目（2026-11 实测：Chrome 437ms、
/// Everything 824ms、Bandizip 1.2s、7-Zip 1.9s、PotPlayer 3.6s）。
/// 与 <see cref="SoftwareCatalog"/> 保持一致：Firefox / OBS 没有稳定直链（mozilla CDN 与 GitHub
/// 在国内不可用或过慢），继续依赖 winget。
/// </summary>
public static class DirectInstallerCatalog
{
    public static IReadOnlyList<DirectInstaller> All { get; } = new List<DirectInstaller>
    {
        new()
        {
            WingetId = "Google.Chrome",
            Name = "Google Chrome",
            Url = "https://dl.google.com/chrome/install/latest/chrome_installer.exe",
            FileName = "chrome_installer.exe",
            SilentArguments = "/silent /install",
            DetectPattern = "Google Chrome"
        },
        new()
        {
            WingetId = "7zip.7zip",
            Name = "7-Zip",
            Url = "https://www.7-zip.org/a/7z2501-x64.exe",
            FileName = "7z2501-x64.exe",
            SilentArguments = "/S",
            DetectPattern = "7-Zip"
        },
        new()
        {
            WingetId = "voidtools.Everything",
            Name = "Everything",
            Url = "https://www.voidtools.com/Everything-1.4.1.1026.x64-Setup.exe",
            FileName = "EverythingSetup-x64.exe",
            SilentArguments = "/S",
            DetectPattern = "Everything"
        },
        new()
        {
            WingetId = "Bandisoft.Bandizip",
            Name = "Bandizip",
            Url = "https://www.bandisoft.com/bandizip/dl.php?latest",
            FileName = "BandizipSetup.exe",
            SilentArguments = "/S",
            DetectPattern = "Bandizip"
        },
        new()
        {
            WingetId = "Daum.PotPlayer",
            Name = "PotPlayer",
            Url = "https://t1.daumcdn.net/potplayer/PotPlayer/Version/Latest/PotPlayerSetup64.exe",
            FileName = "PotPlayerSetup64.exe",
            SilentArguments = "/S",
            DetectPattern = "PotPlayer"
        }
    };

    /// <summary>按 winget ID 查找可用的直链安装器。</summary>
    public static DirectInstaller? Find(string? wingetId)
    {
        if (string.IsNullOrWhiteSpace(wingetId)) return null;
        var id = wingetId.Trim();
        return All.FirstOrDefault(item => item.WingetId.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>该软件是否有直链兜底。</summary>
    public static bool HasFallback(string? wingetId) => Find(wingetId) is not null;
}
