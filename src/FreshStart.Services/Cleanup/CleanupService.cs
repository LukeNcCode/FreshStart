using System.IO;
using FreshStart.Core;

namespace FreshStart.Services.Cleanup;

public sealed class CleanupCategory
{
    public string Key { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string[] Directories { get; init; } = Array.Empty<string>();
    public string[] FilePatterns { get; init; } = Array.Empty<string>();
    public bool Recursive { get; init; } = true;

    public long Bytes { get; set; }
    public int FileCount { get; set; }
    public string SizeText => Bytes <= 0 ? "—" : Format(Bytes);
    public string CountText => FileCount <= 0 ? "0 个文件" : $"{FileCount:N0} 个文件";

    public static string Format(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.##} {units[index]}";
    }
}

/// <summary>
/// 临时文件清理：只处理系统/用户临时目录、缩略图缓存等可再生成的缓存文件。
/// 删除不可回滚，页面必须二次确认；正在使用的文件会被跳过。
/// </summary>
public sealed class CleanupService
{
    private readonly FreshStart.Core.Abstractions.IWriteLock _writeLock;

    public CleanupService(FreshStart.Core.Abstractions.IWriteLock? writeLock = null)
        => _writeLock = writeLock ?? new FreshStart.Core.Threading.WriteLock();

    public IReadOnlyList<CleanupCategory> GetCategories()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new List<CleanupCategory>
        {
            new()
            {
                Key = "user-temp",
                Title = "用户临时文件",
                Description = "%TEMP% 中的缓存、安装临时文件；正在使用的会被跳过",
                Directories = new[] { Path.GetTempPath() }
            },
            new()
            {
                Key = "windows-temp",
                Title = "Windows 临时文件",
                Description = "%SystemRoot%\\Temp 中的系统临时文件",
                Directories = new[] { Path.Combine(windows, "Temp") }
            },
            new()
            {
                Key = "thumbnails",
                Title = "缩略图缓存",
                Description = "资源管理器的 thumbcache_*.db；删除后会自动重建",
                Directories = new[] { Path.Combine(local, "Microsoft", "Windows", "Explorer") },
                FilePatterns = new[] { "thumbcache_*.db", "iconcache_*.db" },
                Recursive = false
            }
        };
    }

    public Task ScanAsync(CleanupCategory category, CancellationToken ct)
        => Task.Run(() => Scan(category, ct), ct);

    /// <summary>页面直接调用：自己取全局写锁。</summary>
    public async Task<OperationResultLike> CleanAsync(CleanupCategory category, CancellationToken ct)
    {
        // 与其他写入共享全局写锁，避免与无人值守等操作并发删文件
        using var lease = await _writeLock.EnterAsync(ct).ConfigureAwait(false);
        return await Task.Run(() => Clean(category, ct), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 已被 <c>OperationRunner</c> / 复合操作持有写锁时调用：不再重复取锁（写锁不可重入）。
    /// </summary>
    internal Task<OperationResultLike> CleanInsideWriteLockAsync(CleanupCategory category, CancellationToken ct)
        => Task.Run(() => Clean(category, ct), ct);

    private static void Scan(CleanupCategory category, CancellationToken ct)
    {
        long bytes = 0;
        var files = 0;
        foreach (var file in EnumerateFiles(category, ct))
        {
            try { bytes += file.Length; files++; } catch { /* ignore */ }
        }
        category.Bytes = bytes;
        category.FileCount = files;
    }

    private static OperationResultLike Clean(CleanupCategory category, CancellationToken ct)
    {
        var deleted = 0;
        long bytes = 0;
        var skipped = 0;
        foreach (var file in EnumerateFiles(category, ct).ToList())
        {
            try
            {
                bytes += file.Length;
                file.Delete();
                deleted++;
            }
            catch
            {
                skipped++;
            }
        }
        Log.Info($"清理「{category.Title}」：删除 {deleted} 个文件，{CleanupCategory.Format(bytes)}，跳过 {skipped} 个");
        return new OperationResultLike
        {
            Success = deleted > 0 || skipped == 0,
            Message = deleted == 0 && skipped > 0
                ? $"没有文件被删除，{skipped} 个文件被占用或无权限"
                : $"已删除 {deleted} 个文件（{CleanupCategory.Format(bytes)}），跳过 {skipped} 个使用中的文件"
        };
    }

    private static IEnumerable<FileInfo> EnumerateFiles(CleanupCategory category, CancellationToken ct)
    {
        foreach (var directory in category.Directories)
        {
            if (!Directory.Exists(directory)) continue;

            List<string> paths;
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = category.FilePatterns.Length == 0,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                    ReturnSpecialDirectories = false
                };
                paths = Directory.EnumerateFiles(directory, "*", options).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"枚举清理目录失败: {directory} - {ex.Message}");
                continue;
            }

            if (category.FilePatterns.Length > 0)
            {
                foreach (var pattern in category.FilePatterns)
                {
                    foreach (var path in paths.Where(path => MatchesPattern(Path.GetFileName(path), pattern)))
                    {
                        ct.ThrowIfCancellationRequested();
                        FileInfo info;
                        try { info = new FileInfo(path); } catch { continue; }
                        yield return info;
                    }
                }
                continue;
            }

            foreach (var path in paths)
            {
                ct.ThrowIfCancellationRequested();
                FileInfo info;
                try { info = new FileInfo(path); } catch { continue; }
                yield return info;
            }
        }
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        var star = pattern.IndexOf('*');
        if (star < 0) return string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase);

        var prefix = pattern[..star];
        var suffix = pattern[(star + 1)..];
        return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class OperationResultLike
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}
