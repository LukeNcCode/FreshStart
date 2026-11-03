using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreshStart.Core;

/// <summary>
/// 全局路径约定。所有数据都落在当前用户的 %AppData%\FreshStart 下。
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var custom = Environment.GetEnvironmentVariable("FRESHSTART_DATA_ROOT");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            try { return Path.GetFullPath(custom); }
            catch { /* 回退到默认目录 */ }
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreshStart");
    }

    public static string BackupRoot => Path.Combine(Root, "backup");
    public static string LogRoot => Path.Combine(Root, "logs");
    public static string CacheRoot => Path.Combine(Root, "cache");
    public static string PresetRoot => Path.Combine(Root, "presets");
    public static string StateRoot => Path.Combine(Root, "state");
    public static string HistoryRoot => Path.Combine(Root, "history");
    public static string ReportsRoot => Path.Combine(Root, "reports");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string HistoryFile => Path.Combine(HistoryRoot, "operations.jsonl");
    public static string BaselineRoot => Path.Combine(StateRoot, "baseline");
    public static string BaselineFile => Path.Combine(BaselineRoot, "baseline.json");

    /// <summary>启动时创建必需目录；失败只记录日志，不阻塞启动。</summary>
    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, BackupRoot, LogRoot, CacheRoot, PresetRoot, StateRoot, HistoryRoot, ReportsRoot, BaselineRoot })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) { Log.Warn($"创建目录失败: {dir} - {ex.Message}"); }
        }
    }
}

/// <summary>统一 JSON 配置，保证备份文件、配置、历史格式一致。</summary>
public static class JsonConfig
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>紧凑格式，用于 JSONL 历史文件。</summary>
    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
