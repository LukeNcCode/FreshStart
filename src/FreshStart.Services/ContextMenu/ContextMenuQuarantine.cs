using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Settings;
using Microsoft.Win32;

namespace FreshStart.Services.ContextMenu;

public sealed class QuarantineEntry
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DeletedAt { get; set; } = string.Empty;
    public bool IsRegistry { get; set; }

    public string? OriginalHive { get; set; }
    public string? OriginalView { get; set; }
    public string? OriginalSubKey { get; set; }
    public string? OriginalFilePath { get; set; }

    public string? QuarantineHive { get; set; }
    public string? QuarantineView { get; set; }
    public string? QuarantineSubKey { get; set; }
    public string? QuarantineFilePath { get; set; }

    public string KindText => Kind switch
    {
        "ShellVerb" => "经典菜单",
        "ShellExtension" => "Shell 扩展",
        "SendTo" => "发送到",
        "NewMenu" => "新建菜单",
        _ => Kind
    };

    public string OriginalDisplay => !string.IsNullOrWhiteSpace(OriginalFilePath)
        ? OriginalFilePath
        : (string.IsNullOrWhiteSpace(OriginalHive) ? string.Empty : $"{OriginalHive}\\{OriginalSubKey}");

    public string DeletedAtText => DateTimeOffset.TryParse(DeletedAt, out var value)
        ? value.ToLocalTime().ToString("MM-dd HH:mm")
        : DeletedAt;
}

/// <summary>
/// 隔离区：删除的菜单项不直接销毁，而是移动到 FreshStart 自己的目录/注册表索引，
/// 支持跨重启恢复。索引本身不是系统设置，直接写 HKCU\Software\FreshStart。
/// </summary>
public sealed class ContextMenuQuarantine
{
    private const string IndexRegistryPath = @"Software\FreshStart\Quarantine\Index";
    private const string RegistryQuarantinePath = @"Software\FreshStart\Quarantine\Items";
    private static readonly string Root = Path.Combine(AppPaths.Root, "quarantine");
    private static readonly string FileQuarantineRoot = Path.Combine(Root, "Files");
    private static readonly string FileIndexRoot = Path.Combine(Root, "Index");

    public IReadOnlyList<QuarantineEntry> List()
    {
        var entries = new List<QuarantineEntry>();
        entries.AddRange(ListRegistry());
        entries.AddRange(ListFiles());
        return entries.OrderByDescending(e => e.DeletedAt, StringComparer.Ordinal).ToList();
    }

    public QuarantineEntry CreateRegistryEntry(
        string kind, string displayName, string originalHive, string originalView, string originalSubKey)
    {
        var id = Guid.NewGuid().ToString("N");
        var entry = new QuarantineEntry
        {
            Id = id,
            Kind = kind,
            DisplayName = displayName,
            DeletedAt = DateTimeOffset.Now.ToString("O"),
            IsRegistry = true,
            OriginalHive = originalHive,
            OriginalView = originalView,
            OriginalSubKey = originalSubKey,
            QuarantineHive = "HKCU",
            QuarantineView = "64",
            QuarantineSubKey = $@"{RegistryQuarantinePath}\{id}"
        };

        WriteRegistryIndex(entry);
        return entry;
    }

    public QuarantineEntry CreateFileEntry(string kind, string displayName, string originalFilePath)
    {
        var id = Guid.NewGuid().ToString("N");
        var quarantinePath = Path.Combine(FileQuarantineRoot, id, Path.GetFileName(originalFilePath));
        var entry = new QuarantineEntry
        {
            Id = id,
            Kind = kind,
            DisplayName = displayName,
            DeletedAt = DateTimeOffset.Now.ToString("O"),
            IsRegistry = false,
            OriginalFilePath = originalFilePath,
            QuarantineFilePath = quarantinePath
        };

        Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
        WriteFileIndex(entry);
        return entry;
    }

    public void Remove(QuarantineEntry entry)
    {
        if (entry.IsRegistry)
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree($@"{IndexRegistryPath}\{entry.Id}", throwOnMissingSubKey: false);
            return;
        }

        try
        {
            if (File.Exists(GetFileIndexPath(entry.Id))) File.Delete(GetFileIndexPath(entry.Id));
            var directory = Path.GetDirectoryName(entry.QuarantineFilePath ?? string.Empty);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"清理隔离索引失败: {ex.Message}");
        }
    }

    private static IEnumerable<QuarantineEntry> ListRegistry()
    {
        var entries = new List<QuarantineEntry>();
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var index = baseKey.OpenSubKey(IndexRegistryPath);
            if (index is null) return entries;

            foreach (var id in index.GetSubKeyNames())
            {
                using var item = index.OpenSubKey(id);
                if (item is null) continue;
                var entry = new QuarantineEntry
                {
                    Id = id,
                    Kind = item.GetValue("Kind") as string ?? "Unknown",
                    DisplayName = item.GetValue("DisplayName") as string ?? id,
                    DeletedAt = item.GetValue("DeletedAt") as string ?? string.Empty,
                    IsRegistry = true,
                    OriginalHive = item.GetValue("OriginalHive") as string,
                    OriginalView = item.GetValue("OriginalView") as string,
                    OriginalSubKey = item.GetValue("OriginalSubKey") as string,
                    QuarantineHive = item.GetValue("QuarantineHive") as string,
                    QuarantineView = item.GetValue("QuarantineView") as string,
                    QuarantineSubKey = item.GetValue("QuarantineSubKey") as string
                };
                entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取隔离索引失败: {ex.Message}");
        }
        return entries;
    }

    private static IEnumerable<QuarantineEntry> ListFiles()
    {
        var entries = new List<QuarantineEntry>();
        try
        {
            if (!Directory.Exists(FileIndexRoot)) return entries;
            foreach (var file in Directory.EnumerateFiles(FileIndexRoot, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<QuarantineEntry>(File.ReadAllText(file), JsonConfig.Options);
                    if (entry is not null) entries.Add(entry);
                }
                catch { /* 跳过损坏索引 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取文件隔离索引失败: {ex.Message}");
        }
        return entries;
    }

    private static void WriteRegistryIndex(QuarantineEntry entry)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey($@"{IndexRegistryPath}\{entry.Id}", writable: true)
            ?? throw new IOException("无法创建隔离索引");
        key.SetValue("Kind", entry.Kind);
        key.SetValue("DisplayName", entry.DisplayName);
        key.SetValue("DeletedAt", entry.DeletedAt);
        key.SetValue("OriginalHive", entry.OriginalHive ?? string.Empty);
        key.SetValue("OriginalView", entry.OriginalView ?? string.Empty);
        key.SetValue("OriginalSubKey", entry.OriginalSubKey ?? string.Empty);
        key.SetValue("QuarantineHive", entry.QuarantineHive ?? string.Empty);
        key.SetValue("QuarantineView", entry.QuarantineView ?? string.Empty);
        key.SetValue("QuarantineSubKey", entry.QuarantineSubKey ?? string.Empty);
    }

    private static void WriteFileIndex(QuarantineEntry entry)
    {
        Directory.CreateDirectory(FileIndexRoot);
        AtomicFile.WriteAllText(GetFileIndexPath(entry.Id), JsonSerializer.Serialize(entry, JsonConfig.Options));
    }

    private static string GetFileIndexPath(string id) => Path.Combine(FileIndexRoot, $"{id}.json");
}
