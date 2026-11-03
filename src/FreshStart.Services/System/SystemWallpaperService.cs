using System.IO;
using System.Runtime.InteropServices;
using FreshStart.Core;
using FreshStart.Core.Operations;

namespace FreshStart.Services.System;

/// <summary>
/// 把内置壁纸设置为 **Windows 桌面壁纸**（不修改 FreshStart 自己的界面）。
///
/// Windows 会把 jpg 转码成 <c>%AppData%\Microsoft\Windows\Themes\TranscodedWallpaper</c>，
/// 原图一旦被移动或删除，桌面就会变黑——所以先把图片复制到
/// <c>%LocalAppData%\FreshStart\wallpapers\</c> 这个稳定位置再设置。
/// </summary>
public sealed class SystemWallpaperService
{
    /// <summary>随包壁纸的键（与 assets\wallpapers 下的文件名一致）。</summary>
    public static IReadOnlyList<string> Keys { get; } = new[] { "clouds", "butterfly", "flower" };

    /// <summary>随包壁纸目录（发布包里是 assets\wallpapers）。</summary>
    public static string SourceDirectory => Path.Combine(AppContext.BaseDirectory, "assets", "wallpapers");

    /// <summary>设置壁纸时使用的稳定副本目录。</summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreshStart", "wallpapers");

    private const string DesktopKey = @"Control Panel\Desktop";
    private const uint SpiSetDesktopWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x0001;
    private const uint SpifSendWinIniChange = 0x0002;

    /// <summary>某个键对应的随包图片路径；不存在返回 false。</summary>
    public static bool TryGetSourceFile(string key, out string path)
    {
        path = Path.Combine(SourceDirectory, $"{key}.jpg");
        return Keys.Contains(key, StringComparer.OrdinalIgnoreCase) && File.Exists(path);
    }

    /// <summary>当前桌面壁纸路径（读注册表）；未设置返回空字符串。</summary>
    public string GetCurrentPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopKey);
            return key?.GetValue("Wallpaper") as string ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取当前壁纸失败: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>应用内置壁纸（会先复制到稳定目录）。</summary>
    public OperationResult Apply(string key, string displayName)
    {
        if (!TryGetSourceFile(key, out var source))
        {
            return OperationResult.Fail("壁纸图片不在安装包里（缺少 assets\\wallpapers）");
        }

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            var target = Path.Combine(CacheDirectory, $"{key.ToLowerInvariant()}.jpg");
            if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(source, target, overwrite: true);
            }

            var applied = SetWallpaper(target);
            return applied.Success
                ? OperationResult.Ok($"桌面壁纸已设为「{displayName}」")
                : applied;
        }
        catch (Exception ex)
        {
            Log.Error($"设置内置壁纸失败: {key}", ex);
            return OperationResult.Fail($"设置失败：{ex.Message}");
        }
    }

    /// <summary>把任意图片设为桌面壁纸（路径必须是已存在的文件）。</summary>
    public OperationResult SetWallpaper(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return OperationResult.Fail("壁纸文件不存在");
        }

        try
        {
            ApplyFillStyle();
            if (!SystemParametersInfoW(SpiSetDesktopWallpaper, 0, imagePath, SpifUpdateIniFile | SpifSendWinIniChange))
            {
                var code = Marshal.GetLastWin32Error();
                Log.Warn($"SystemParametersInfo 设置壁纸失败，错误码 {code}");
                return OperationResult.Fail($"设置失败（错误码 {code}）");
            }

            return OperationResult.Ok("桌面壁纸已更新");
        }
        catch (Exception ex)
        {
            Log.Error("设置系统壁纸失败", ex);
            return OperationResult.Fail($"设置失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把**当前显示的桌面壁纸**备份到稳定目录。
    /// 原图路径经常失效（从下载目录设的壁纸删了原文件、Windows 聚焦每日换图），
    /// 所以应用内置壁纸前先存一份 Windows 的 TranscodeWallpaper 副本作为兜底。
    /// </summary>
    public string? BackupCurrent()
    {
        try
        {
            var transcoded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Themes", "TranscodedWallpaper");
            if (!File.Exists(transcoded)) return null;

            Directory.CreateDirectory(CacheDirectory);
            var target = Path.Combine(CacheDirectory, $"previous-{DateTime.Now:yyyyMMdd-HHmmss}.jpg");
            File.Copy(transcoded, target, overwrite: true);
            PruneBackups();
            Log.Info($"已备份当前桌面壁纸: {target}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn($"备份当前壁纸失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>只保留最近 3 份备份，避免无限增长。</summary>
    private static void PruneBackups()
    {
        try
        {
            var backups = Directory.GetFiles(CacheDirectory, "previous-*.jpg")
                .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var old in backups.Skip(3))
            {
                try { File.Delete(old); } catch { /* 忽略：可能正被占用 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理壁纸备份失败: {ex.Message}");
        }
    }

    /// <summary>恢复到应用内置壁纸之前的原壁纸。</summary>
    public OperationResult Restore(string? originalPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath) || !File.Exists(originalPath))
        {
            return OperationResult.Fail("原来的壁纸文件已不存在（可能被移动或删除），请在系统设置里重新选择");
        }

        var result = SetWallpaper(originalPath);
        return result.Success ? OperationResult.Ok("已恢复原来的桌面壁纸") : result;
    }

    /// <summary>用「填充」方式显示，和现代 Windows 默认一致。</summary>
    private static void ApplyFillStyle()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(DesktopKey, writable: true);
            key?.SetValue("WallpaperStyle", "10", Microsoft.Win32.RegistryValueKind.String);
            key?.SetValue("TileWallpaper", "0", Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warn($"设置壁纸显示方式失败: {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);
}
