using System.Globalization;

namespace FreshStart.Core.Backup;

/// <summary>
/// 备份与日志的保留策略。此前 BackupRetentionDays 只是设置项、从未生效，
/// 长期使用后 backup 目录会无限增长，这里补齐实际清理。
/// </summary>
public static class BackupRetention
{
    /// <summary>日志保留天数（日志主要用于排查最近的问题）。</summary>
    public const int LogRetentionDays = 14;

    /// <summary>无论多旧都至少保留的备份份数，保证"撤销最近几次"始终可用。</summary>
    public const int KeepAtLeastBackups = 20;

    /// <summary>删除超过保留期的备份快照目录，返回删除数量。失败只记日志。</summary>
    public static int PurgeBackups(int retentionDays, int keepAtLeast = KeepAtLeastBackups)
    {
        try
        {
            var root = AppPaths.BackupRoot;
            if (!Directory.Exists(root)) return 0;

            var days = Math.Clamp(retentionDays, 1, 3650);
            var cutoff = DateTime.Now.AddDays(-days);
            var snapshots = new List<(string Path, DateTime CreatedAt)>();

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                snapshots.Add((directory, ResolveCreatedAt(directory)));
            }

            var removed = 0;
            var ordered = snapshots.OrderByDescending(item => item.CreatedAt).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                if (index < Math.Max(0, keepAtLeast)) continue;
                if (ordered[index].CreatedAt >= cutoff) continue;

                try
                {
                    Directory.Delete(ordered[index].Path, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理旧备份失败: {ordered[index].Path} - {ex.Message}");
                }
            }

            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描备份目录失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>删除超过保留期的按天日志文件，返回删除数量。</summary>
    public static int PurgeLogs(int retentionDays = LogRetentionDays)
    {
        try
        {
            var root = AppPaths.LogRoot;
            if (!Directory.Exists(root)) return 0;

            var cutoff = DateTime.Now.Date.AddDays(-Math.Clamp(retentionDays, 1, 3650));
            var removed = 0;

            foreach (var file in Directory.EnumerateFiles(root, "freshstart-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var stamp = name.StartsWith("freshstart-", StringComparison.OrdinalIgnoreCase)
                    ? name["freshstart-".Length..]
                    : string.Empty;

                if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                {
                    continue;
                }

                if (date >= cutoff) continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理旧日志失败: {file} - {ex.Message}");
                }
            }

            return removed;
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描日志目录失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>目录名形如 {yyyyMMdd-HHmmss}-{opId}-{guid}；解析失败时退回创建时间。</summary>
    private static DateTime ResolveCreatedAt(string directory)
    {
        var name = Path.GetFileName(directory);
        var dash = name.IndexOf('-');
        if (dash > 0 && DateTime.TryParseExact(name[..dash], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            var timePart = name[(dash + 1)..];
            var nextDash = timePart.IndexOf('-');
            var timeText = nextDash > 0 ? timePart[..nextDash] : timePart;
            if (DateTime.TryParseExact(timeText, "HHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
            {
                return date.Date + time.TimeOfDay;
            }

            return date;
        }

        try { return Directory.GetCreationTime(directory); }
        catch { return DateTime.MinValue; }
    }
}
