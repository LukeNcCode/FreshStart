using System.Diagnostics;
using System.Text.Json;

namespace FreshStart.Core.Diagnostics;

/// <summary>
/// 启动耗时埋点。所有节点都写入 JSON，供 PerfHarness 统计 P50/P95。
/// 时间基准为进程内 Stopwatch（接近 CLR 启动完成时刻）。
/// </summary>
public static class StartupProbe
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    private static readonly List<ProbeMark> Marks = new();
    private static readonly object Gate = new();

    /// <summary>进程真实创建时间（用于计算端到端冷启动，而不是只算 App.OnStartup 之后）。</summary>
    public static DateTime ProcessStart { get; } = Process.GetCurrentProcess().StartTime;

    public static DateTime? FirstInteractiveAt { get; private set; }

    public readonly record struct ProbeMark(string Name, double Ms);

    public static double NowMs => Watch.Elapsed.TotalMilliseconds;

    public static void Mark(string name)
    {
        lock (Gate)
        {
            Marks.Add(new ProbeMark(name, Math.Round(Watch.Elapsed.TotalMilliseconds, 2)));
            if (name == "interactive") FirstInteractiveAt = DateTime.Now;
        }
    }

    public static IReadOnlyList<ProbeMark> Snapshot()
    {
        lock (Gate) return Marks.ToList();
    }

    public static string ToJson(IReadOnlyDictionary<string, object?>? extra = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.Now,
            ["processId"] = Environment.ProcessId,
            ["os"] = Environment.OSVersion.VersionString,
            ["marks"] = Snapshot(),
            ["elapsedMs"] = Math.Round(NowMs, 2),
            ["processStartMs"] = Math.Round((FirstInteractiveAt is null ? 0 : (FirstInteractiveAt.Value - ProcessStart).TotalMilliseconds), 2),
            ["firstInteractiveAt"] = FirstInteractiveAt
        };
        if (extra is not null)
        {
            foreach (var pair in extra) payload[pair.Key] = pair.Value;
        }
        return JsonSerializer.Serialize(payload, JsonConfig.Options);
    }

    public static void WriteFile(string path, IReadOnlyDictionary<string, object?>? extra = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson(extra));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入启动耗时失败: {ex.Message}");
        }
    }
}
