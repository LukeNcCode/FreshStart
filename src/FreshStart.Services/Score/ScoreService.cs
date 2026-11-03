using System.IO;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Services.Interop;
using FreshStart.Services.Registry;
using FreshStart.Services.Software;
using FreshStart.Services.Startup;
using FreshStart.Services.Tuning;
using FreshStart.Services.Uninstall;
using Microsoft.Win32;

namespace FreshStart.Services.Score;

public sealed class ScoreMetric
{
    public string Name { get; init; } = string.Empty;
    public double Score { get; init; }
    public int Max { get; init; }
    public string Evidence { get; init; } = string.Empty;
    public double Percent => Max <= 0 ? 0 : Math.Clamp(Score * 100 / Max, 0, 100);
    public string ScoreText => $"{Score:0} / {Max}";
}

public sealed class ScoreSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public double Total { get; init; }
    public List<ScoreMetric> Metrics { get; init; } = new();
}

/// <summary>
/// 只读开荒评分：采集启动项、服务、任务、磁盘、内存、隐私与软件数量，按固定权重算分。
/// 评分只用于展示，不触发任何修改。
/// </summary>
public sealed class ScoreService
{
    private readonly ServiceControlService _services;
    private readonly ScheduledTaskService _tasks;
    private readonly TuningService _tuning;
    private readonly InstalledProgramService _programs;

    public ScoreService(
        ServiceControlService services,
        ScheduledTaskService tasks,
        TuningService tuning,
        InstalledProgramService programs)
    {
        _services = services;
        _tasks = tasks;
        _tuning = tuning;
        _programs = programs;
    }

    public async Task<ScoreSnapshot> CaptureAsync(CancellationToken ct = default)
    {
        var metrics = new List<ScoreMetric>();

        var startupCount = await Task.Run(CountStartupEntries, ct).ConfigureAwait(false);
        metrics.Add(new ScoreMetric
        {
            Name = "启动项",
            Max = 25,
            Score = 25 * (1 - Math.Min(startupCount, 20) / 20.0),
            Evidence = $"{startupCount} 个启动项（Run/RunOnce/启动文件夹）"
        });

        var services = await Task.Run(() => _services.List(), ct).ConfigureAwait(false);
        var autoServices = services.Count(service => service.StartType == 2);
        metrics.Add(new ScoreMetric
        {
            Name = "自动服务",
            Max = 15,
            Score = 15 * (1 - Math.Min(autoServices, 60) / 60.0),
            Evidence = $"{autoServices} 个自动服务（共 {services.Count} 个 Win32 服务）"
        });

        var tasks = await _tasks.QueryLogonTasksAsync(ct).ConfigureAwait(false);
        metrics.Add(new ScoreMetric
        {
            Name = "登录/开机任务",
            Max = 10,
            Score = 10 * (1 - Math.Min(tasks.Count, 15) / 15.0),
            Evidence = $"{tasks.Count} 个非 Microsoft 登录/开机任务"
        });

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var disk = new DriveInfo(systemDrive);
        var freePercent = disk.TotalSize > 0 ? disk.AvailableFreeSpace * 100.0 / disk.TotalSize : 0;
        metrics.Add(new ScoreMetric
        {
            Name = "系统盘空间",
            Max = 15,
            Score = 15 * Math.Clamp(freePercent / 30.0, 0, 1),
            Evidence = $"{systemDrive} 可用 {freePercent:0.0}%"
        });

        var memory = NativeMethods.GetMemoryStatus();
        var usedPercent = memory.TotalBytes > 0
            ? (memory.TotalBytes - memory.AvailableBytes) * 100.0 / memory.TotalBytes
            : 0;
        metrics.Add(new ScoreMetric
        {
            Name = "内存占用",
            Max = 10,
            Score = 10 * Math.Clamp((100 - usedPercent) / 60.0, 0, 1),
            Evidence = $"已用 {usedPercent:0.0}%"
        });

        var privacyOptions = _tuning.Options.Where(option => option.Category == "隐私").ToList();
        var privacyApplied = privacyOptions.Count(_tuning.IsApplied);
        metrics.Add(new ScoreMetric
        {
            Name = "隐私开关",
            Max = 15,
            Score = privacyOptions.Count == 0 ? 0 : 15.0 * privacyApplied / privacyOptions.Count,
            Evidence = $"已启用 {privacyApplied} / {privacyOptions.Count} 项"
        });

        var programs = await Task.Run(() => _programs.List(), ct).ConfigureAwait(false);
        metrics.Add(new ScoreMetric
        {
            Name = "已安装软件",
            Max = 10,
            Score = 10 * (1 - Math.Min(programs.Count, 120) / 120.0),
            Evidence = $"{programs.Count} 个 ARP 程序"
        });

        return new ScoreSnapshot
        {
            Total = Math.Round(metrics.Sum(metric => metric.Score), 1),
            Metrics = metrics
        };
    }

    public async Task<(string HtmlPath, string JsonPath)> SaveReportAsync(ScoreSnapshot snapshot)
    {
        var reportsRoot = AppPaths.ReportsRoot;
        Directory.CreateDirectory(reportsRoot);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var htmlPath = Path.Combine(reportsRoot, $"score-{stamp}.html");
        var jsonPath = Path.Combine(reportsRoot, $"score-{stamp}.json");

        await File.WriteAllTextAsync(jsonPath,
            JsonSerializer.Serialize(snapshot, JsonConfig.Options)).ConfigureAwait(false);

        var builder = new StringBuilder();
        builder.AppendLine("<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\">");
        builder.AppendLine("<title>FreshStart 开荒评分</title>");
        builder.AppendLine("<style>body{font-family:Segoe UI,sans-serif;max-width:760px;margin:40px auto;color:#222}" +
                           "h1{font-size:22px}table{border-collapse:collapse;width:100%}td,th{padding:8px;border-bottom:1px solid #eee;text-align:left}" +
                           ".score{font-size:42px;font-weight:600;color:#007AFF}</style>");
        builder.AppendLine($"<h1>FreshStart 开荒评分</h1><div class=\"score\">{snapshot.Total:0} / 100</div>");
        builder.AppendLine($"<p>采集时间：{snapshot.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}</p>");
        builder.AppendLine("<table><tr><th>维度</th><th>得分</th><th>依据</th></tr>");
        foreach (var metric in snapshot.Metrics)
        {
            builder.AppendLine($"<tr><td>{Encode(metric.Name)}</td><td>{Encode(metric.ScoreText)}</td><td>{Encode(metric.Evidence)}</td></tr>");
        }
        builder.AppendLine("</table><p>评分只用于展示，不会自动修改任何设置。</p></html>");
        await File.WriteAllTextAsync(htmlPath, builder.ToString()).ConfigureAwait(false);
        return (htmlPath, jsonPath);
    }

    private static string Encode(string? value) => global::System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    private static int CountStartupEntries()
    {
        var count = 0;
        (RegistryHive Hive, string Path)[] keys =
        {
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run")
        };

        foreach (var (hive, path) in keys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(path);
                if (key is not null) count += key.ValueCount;
            }
            catch { /* ignore */ }
        }

        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (Directory.Exists(startup))
            {
                count += Directory.EnumerateFiles(startup).Count(file =>
                    !file.EndsWith(".ini", StringComparison.OrdinalIgnoreCase));
            }
        }
        catch { /* ignore */ }

        return count;
    }
}
