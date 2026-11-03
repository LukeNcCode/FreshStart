using System.Net;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Appx;
using FreshStart.Services.Cleanup;
using FreshStart.Services.ContextMenu;
using FreshStart.Services.Defender;
using FreshStart.Services.Driver;
using FreshStart.Services.Features;
using FreshStart.Services.Firewall;
using FreshStart.Services.Network;
using FreshStart.Services.Power;
using FreshStart.Services.Registry;
using FreshStart.Services.Software;
using FreshStart.Services.Startup;
using FreshStart.Services.System;
using FreshStart.Services.Tuning;
using FreshStart.Services.Uninstall;
using FreshStart.Services.WindowsUpdate;

namespace FreshStart.Services.Unattended;

/// <summary>方案里的一步。Kind 决定 Target 的含义。</summary>
public sealed class UnattendedStep
{
    /// <summary>preset | tuning | ctxmenu | update | install | uninstall | appx</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>预设 key / 调控 key / 更新模式 / winget ID / 程序名 / 包名。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>可读名称，方便报告和排查（可留空，运行时会补全）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>附加参数：服务启动类型 / DNS 预设 / Appx 包名等。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>调控项 / 右键菜单：true 应用或经典，false 还原或新版。</summary>
    public bool Apply { get; set; } = true;

    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// 无人值守方案。v2 用 Steps 描述所有步骤；v1 的 presets/options 仍可读入并自动迁移。
/// </summary>
public sealed class UnattendedJob
{
    public int SchemaVersion { get; set; } = 2;
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<UnattendedStep> Steps { get; set; } = new();

    // ---- v1 兼容字段 ----
    public List<string> Presets { get; set; } = new();
    public List<UnattendedOption> Options { get; set; } = new();

    public bool IsBuiltIn { get; set; }
    public string? FilePath { get; set; }

    /// <summary>单步失败时继续执行其余步骤（默认开启，适合长方案）。</summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>方案执行完成后重启资源管理器，让界面/右键菜单设置立即生效。</summary>
    public bool RestartExplorer { get; set; } = true;

    public bool HasSteps => Steps.Count > 0 || Presets.Count > 0 || Options.Count > 0;
}

public sealed class UnattendedOption
{
    public string Key { get; set; } = string.Empty;
    public bool Apply { get; set; } = true;
}

/// <summary>预览里的一步（不执行写入）。</summary>
public sealed class UnattendedPlanItem
{
    public string Kind { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string RiskText { get; init; } = string.Empty;
    public bool Irreversible { get; init; }
    public string SkipReason { get; init; } = string.Empty;

    /// <summary>对应的方案步骤，便于导入后删除/调整顺序。</summary>
    public UnattendedStep? SourceStep { get; set; }

    public bool Skipped => !string.IsNullOrEmpty(SkipReason);
}

public sealed class UnattendedPlan
{
    public required UnattendedJob Job { get; init; }
    public List<UnattendedPlanItem> Items { get; init; } = new();
    public List<string> Warnings { get; init; } = new();

    public bool IsValid => Items.Any(item => !item.Skipped);
    public int RunnableCount => Items.Count(item => !item.Skipped);
    public bool HasIrreversible => Items.Any(item => !item.Skipped && item.Irreversible);
    public bool RequiresRestart => Items.Any(item =>
        !item.Skipped && item.Kind is "preset" or "tuning" && item.Detail.Contains("重启", StringComparison.Ordinal));
}

public sealed class PreparedUnattended
{
    public List<ISystemOperation> Operations { get; } = new();
    public List<string> Notes { get; } = new();
}

public sealed class UnattendedRunResult
{
    public OperationExecution? Execution { get; init; }
    public string? ReportPath { get; init; }
    public List<string> Notes { get; init; } = new();
    public List<string> Failures { get; init; } = new();
    public bool NothingToDo => Execution is null;
}

/// <summary>
/// 无人值守：把方案解析成操作序列，包成一个复合操作执行。
/// 方案只引用 FreshStart 已有的封装：注册表调控、右键菜单风格、Windows 更新、
/// winget 安装、ARP 卸载、Appx 移除；不执行任意脚本或命令。
/// </summary>
public sealed class UnattendedService
{
    private static readonly (string Key, string Title)[] UpdateModes =
    {
        ("normal", "恢复正常更新"),
        ("paused", "暂停更新 7 天"),
        ("extended", "超长延期"),
        ("disabled", "彻底关闭更新")
    };

    private readonly TuningService _tuning;
    private readonly RegistryService _registry;
    private readonly WindowsUpdateService _windowsUpdate;
    private readonly WingetService _winget;
    private readonly InstalledProgramService _uninstall;
    private readonly AppxService _appx;
    private readonly ServiceControlService _services;
    private readonly ScheduledTaskService _tasks;
    private readonly NetworkService _network;
    private readonly PowerService _power;
    private readonly CleanupService _cleanup;
    private readonly DriverService _drivers;
    private readonly ExplorerService _explorer;
    private readonly DefenderService _defender;
    private readonly FirewallService _firewall;
    private readonly OptionalFeatureService _features;

    /// <summary>受 Windows 保护、无法通过服务配置修改启动类型的服务。</summary>
    public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "wscsvc", "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "WaaSMedicSvc", "MsMpSvc"
    };

    public UnattendedService(
        TuningService tuning,
        RegistryService registry,
        WindowsUpdateService windowsUpdate,
        WingetService winget,
        InstalledProgramService uninstall,
        AppxService appx,
        ServiceControlService services,
        ScheduledTaskService tasks,
        NetworkService network,
        PowerService power,
        CleanupService cleanup,
        DriverService drivers,
        ExplorerService explorer,
        DefenderService defender,
        FirewallService firewall,
        OptionalFeatureService features)
    {
        _tuning = tuning;
        _registry = registry;
        _windowsUpdate = windowsUpdate;
        _winget = winget;
        _uninstall = uninstall;
        _appx = appx;
        _services = services;
        _tasks = tasks;
        _network = network;
        _power = power;
        _cleanup = cleanup;
        _drivers = drivers;
        _explorer = explorer;
        _defender = defender;
        _firewall = firewall;
        _features = features;
    }

    public static IReadOnlyList<(string Key, string Title)> AvailableUpdateModes => UpdateModes;

    private static readonly HashSet<string> ExplorerTuningKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "searchbox", "transparency", "taskbar-left", "clock-seconds", "widgets",
        "taskview", "bing-search", "recent-files", "visualfx", "menudelay"
    };

    /// <summary>方案里是否有需要重启资源管理器才能生效的步骤（右键菜单、任务栏/资源管理器外观等）。</summary>
    public bool RequiresExplorerRestart(UnattendedJob job)
    {
        foreach (var step in job.Steps)
        {
            switch (step.Kind.ToLowerInvariant())
            {
                case "ctxmenu":
                    return true;

                case "tuning" when ExplorerTuningKeys.Contains(step.Target ?? string.Empty):
                    return true;

                case "preset":
                {
                    var preset = FindPreset(step.Target ?? string.Empty);
                    if (preset is not null && preset.Options.Any(ExplorerTuningKeys.Contains)) return true;
                    break;
                }
            }
        }

        return false;
    }

    /// <summary>执行优先级：数值小的先执行。</summary>
    public static int Priority(string kind) => kind.ToLowerInvariant() switch
    {
        "update" => 10,
        "service" => 20,
        "firewall-profile" => 22,
        "firewall-rule" => 24,
        "firewall-rule-remove" => 26,
        "feature" => 28,
        "task" => 30,
        "ctxmenu" => 40,
        "preset" => 50,
        "tuning" => 55,
        "dns" => 60,
        "net-ip" => 62,
        "power" => 70,
        "driver-install" => 74,
        "driver-remove" => 76,
        "install" => 80,
        "uninstall" => 90,
        "appx" => 100,
        "driver" => 110,
        "cleanup" => 120,
        "defender-remove" => 200,
        _ => 130
    };

    /// <summary>把步骤按依赖顺序重排（稳定排序，同类型保持原有顺序）。</summary>
    public static List<UnattendedStep> OrderSteps(IEnumerable<UnattendedStep> steps)
        => steps.Select((step, index) => (step, index))
            .OrderBy(pair => Priority(pair.step.Kind))
            .ThenBy(pair => pair.index)
            .Select(pair => pair.step)
            .ToList();

    // ---------------- 读写 ----------------

    public UnattendedJob LoadFile(string path)
    {
        var job = JsonSerializer.Deserialize<UnattendedJob>(
                      File.ReadAllText(path, Encoding.UTF8), JsonConfig.Options)
                  ?? throw new InvalidOperationException("方案文件为空或格式不正确");

        job.IsBuiltIn = false;
        job.FilePath = path;
        if (string.IsNullOrWhiteSpace(job.Key)) job.Key = "file." + Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(job.Name)) job.Name = Path.GetFileNameWithoutExtension(path);

        Migrate(job);
        if (!job.HasSteps) throw new InvalidOperationException("方案里没有任何步骤");
        return job;
    }

    public string Export(UnattendedJob job, string path)
    {
        job.SchemaVersion = 2;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(job, JsonConfig.Options), new UTF8Encoding(false));
        return path;
    }

    private static void Migrate(UnattendedJob job)
    {
        if (job.Steps.Count > 0) return;

        foreach (var preset in job.Presets)
        {
            job.Steps.Add(new UnattendedStep { Kind = "preset", Target = preset });
        }

        foreach (var option in job.Options)
        {
            job.Steps.Add(new UnattendedStep { Kind = "tuning", Target = option.Key, Apply = option.Apply });
        }

        job.SchemaVersion = 2;
    }

    // ---------------- 解析 ----------------

    public UnattendedPlan BuildPlan(UnattendedJob job)
    {
        var items = new List<UnattendedPlanItem>();
        var warnings = new List<string>();

        foreach (var step in job.Steps)
        {
            switch (step.Kind.ToLowerInvariant())
            {
                case "preset":
                {
                    var preset = FindPreset(step.Target ?? string.Empty);
                    if (preset is null)
                    {
                        warnings.Add($"未找到预设「{step.Target}」");
                        items.Add(Skip(step, $"预设不存在：{step.Target}"));
                        break;
                    }

                    var restart = preset.Options
                        .Select(key => _tuning.Find(key ?? string.Empty))
                        .Any(option => option is { RequiresRestart: true });

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "preset",
                        Title = $"应用预设「{preset.Name}」",
                        Detail = $"{preset.Options.Count} 项{(restart ? " · 含需重启项" : string.Empty)}",
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "tuning":
                {
                    var option = _tuning.Find(step.Target ?? string.Empty);
                    if (option is null)
                    {
                        warnings.Add($"未找到调控项「{step.Target}」");
                        items.Add(Skip(step, $"调控项不存在：{step.Target}"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "tuning",
                        Title = $"{option.Title} → {(step.Apply ? "应用" : "还原")}",
                        Detail = $"{option.Category}{(option.RequiresRestart ? " · 需重启" : string.Empty)}",
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "ctxmenu":
                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "ctxmenu",
                        Title = step.Apply ? "右键菜单切换到 Win10 经典" : "右键菜单切回 Win11 新版",
                        Detail = "需重启资源管理器",
                        RiskText = "谨慎"
                    });
                    break;

                case "update":
                {
                    var mode = UpdateModes.FirstOrDefault(m => m.Key.Equals(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                    if (mode.Key is null)
                    {
                        warnings.Add($"未知的更新模式「{step.Target}」");
                        items.Add(Skip(step, $"未知更新模式：{step.Target}"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "update",
                        Title = $"Windows 更新：{mode.Title}",
                        Detail = mode.Key == "disabled" ? "会改服务与策略，风险较高" : "策略级设置",
                        RiskText = mode.Key == "disabled" ? "高风险" : "谨慎"
                    });
                    break;
                }

                case "install":
                {
                    var name = string.IsNullOrWhiteSpace(step.Name) ? step.Target : step.Name;
                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "install",
                        Title = $"安装「{name}」",
                        Detail = $"winget · {step.Target}",
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "uninstall":
                {
                    var program = FindProgram(step.Target ?? string.Empty);
                    if (program is null)
                    {
                        warnings.Add($"未找到已安装程序「{step.Target}」，执行时会跳过");
                        items.Add(Skip(step, "当前未检测到该程序"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "uninstall",
                        Title = $"卸载「{program.DisplayName}」",
                        Detail = $"{program.Version} · {program.SizeText}",
                        RiskText = "高风险",
                        Irreversible = true
                    });
                    break;
                }

                case "appx":
                {
                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "appx",
                        Title = $"移除应用包「{(string.IsNullOrWhiteSpace(step.Name) ? step.Target : step.Name)}」",
                        Detail = "执行时匹配本机安装状态，未安装会跳过",
                        RiskText = "高风险",
                        Irreversible = true
                    });
                    break;
                }

                case "appxforce":
                {
                    var isDefender = (step.Target ?? string.Empty).Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase)
                                     || step.Name.Contains("安全中心", StringComparison.OrdinalIgnoreCase);

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = isDefender ? "defender-remove" : "appxforce",
                        Title = isDefender
                            ? "彻底移除 Defender 引擎与安全中心"
                            : $"强制卸载「{(string.IsNullOrWhiteSpace(step.Name) ? step.Target : step.Name)}」",
                        Detail = isDefender
                            ? "不可回滚；会关闭 VBS/SmartScreen 等，必须重启"
                            : "受系统保护的组件，使用 takeown / 删除目录方式移除，不可回滚",
                        RiskText = isDefender ? "极高" : "高风险",
                        Irreversible = true
                    });
                    break;
                }

                case "onedrive":
                {
                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "onedrive",
                        Title = "卸载 OneDrive",
                        Detail = "结束进程并调用官方卸载程序，不可回滚",
                        RiskText = "高风险",
                        Irreversible = true
                    });
                    break;
                }

                case "service":
                {
                    if (ProtectedServices.Contains(step.Target ?? string.Empty))
                    {
                        items.Add(Skip(step, "受系统保护的服务，无法通过服务配置修改（可用 Defender 步骤或强制卸载安全中心）"));
                        break;
                    }

                    if (!_services.Capture(step.Target ?? string.Empty).Exists)
                    {
                        items.Add(Skip(step, $"服务「{step.Target}」不存在"));
                        break;
                    }

                    var mode = step.Value.ToLowerInvariant();
                    var modeText = mode switch
                    {
                        "auto" => "自动",
                        "manual" => "手动",
                        "disabled" => "禁用",
                        _ => "未知"
                    };
                    if (string.IsNullOrWhiteSpace(step.Target) || modeText == "未知")
                    {
                        items.Add(Skip(step, "服务名或启动类型无效"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "service",
                        Title = $"服务「{Display(step)}」→ {modeText}",
                        Detail = step.Target,
                        RiskText = mode == "disabled" ? "高风险" : "谨慎"
                    });
                    break;
                }

                case "task":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "任务名无效"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "task",
                        Title = $"计划任务「{Display(step)}」→ {(step.Apply ? "启用" : "禁用")}",
                        Detail = step.Target,
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "dns":
                {
                    if (string.IsNullOrWhiteSpace(step.Value))
                    {
                        items.Add(Skip(step, "未指定 DNS 预设"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "dns",
                        Title = $"DNS → {step.Name}",
                        Detail = $"适配器：{step.Target} · 预设 {step.Value}",
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "power":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定电源计划"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "power",
                        Title = $"电源计划 → {Display(step)}",
                        Detail = step.Target,
                        RiskText = "安全"
                    });
                    break;
                }

                case "cleanup":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定清理类别"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "cleanup",
                        Title = $"清理「{Display(step)}」",
                        Detail = "删除可再生成的缓存，不可回滚",
                        RiskText = "谨慎",
                        Irreversible = true
                    });
                    break;
                }

                case "driver":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定导出目录"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "driver",
                        Title = "导出第三方驱动",
                        Detail = step.Target,
                        RiskText = "安全"
                    });
                    break;
                }

                case "defender":
                {
                    var text = step.Value.ToLowerInvariant() switch
                    {
                        "realtime-on" => "开启实时防护",
                        "realtime-off" => "关闭实时防护",
                        "cloud-on" => "开启云保护",
                        "cloud-off" => "关闭云保护",
                        _ => null
                    };
                    if (text is null)
                    {
                        items.Add(Skip(step, "未知的 Defender 动作"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "defender",
                        Title = $"Defender：{text}",
                        Detail = "受篡改保护限制；必要时需先在 Windows 安全中心手动关闭篡改保护",
                        RiskText = "高风险"
                    });
                    break;
                }

                case "defender-remove":
                {
                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "defender-remove",
                        Title = "彻底移除 Defender 引擎与安全组件",
                        Detail = "不可回滚；会关闭 VBS/SmartScreen 等，必须重启",
                        RiskText = "极高",
                        Irreversible = true
                    });
                    break;
                }

                case "firewall-profile":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定防火墙配置文件"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "firewall-profile",
                        Title = $"防火墙 {step.Target} → {(step.Apply ? "开启" : "关闭")}",
                        Detail = "Set-NetFirewallProfile",
                        RiskText = step.Apply ? "谨慎" : "高风险"
                    });
                    break;
                }

                case "firewall-rule":
                {
                    var spec = ParseRuleSpec(step.Value);
                    if (spec is null)
                    {
                        items.Add(Skip(step, "防火墙规则参数无效"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "firewall-rule",
                        Title = $"添加防火墙规则「{spec.DisplayName}」",
                        Detail = $"{spec.Direction} · {spec.Action} · {spec.Protocol} {spec.LocalPort}".Trim(),
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "firewall-rule-remove":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定规则名称"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "firewall-rule-remove",
                        Title = $"删除防火墙规则「{step.Target}」",
                        Detail = "删除前读取规则定义，可回滚",
                        RiskText = "高风险"
                    });
                    break;
                }

                case "driver-install":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定驱动路径"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "driver-install",
                        Title = $"安装驱动：{Path.GetFileName(step.Target.TrimEnd('\\'))}",
                        Detail = step.Target,
                        RiskText = "高风险"
                    });
                    break;
                }

                case "driver-remove":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定驱动包"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "driver-remove",
                        Title = $"删除驱动包 {step.Target}",
                        Detail = step.Name,
                        RiskText = "高风险"
                    });
                    break;
                }

                case "feature":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        items.Add(Skip(step, "未指定功能名称"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "feature",
                        Title = $"{(step.Apply ? "启用" : "禁用")}可选功能「{(string.IsNullOrWhiteSpace(step.Name) ? step.Target : step.Name)}」",
                        Detail = "可能需要重启",
                        RiskText = "谨慎"
                    });
                    break;
                }

                case "net-ip":
                {
                    var config = ParseIpConfig(step.Value);
                    if (config is null)
                    {
                        items.Add(Skip(step, "IP 参数无效"));
                        break;
                    }

                    items.Add(new UnattendedPlanItem
                    {
                        Kind = "net-ip",
                        Title = $"IP 配置：{step.Target} → {config.ModeText}",
                        Detail = config.Summary,
                        RiskText = "谨慎"
                    });
                    break;
                }

                default:
                    warnings.Add($"未知步骤类型「{step.Kind}」");
                    items.Add(Skip(step, $"未知步骤类型：{step.Kind}"));
                    break;
            }

            if (items.Count > 0) items[^1].SourceStep = step;
        }

        return new UnattendedPlan { Job = job, Items = items, Warnings = warnings };
    }

    private static UnattendedPlanItem Skip(UnattendedStep step, string reason) => new()
    {
        Kind = step.Kind,
        Title = string.IsNullOrWhiteSpace(step.Name) ? step.Target : step.Name,
        Detail = "已跳过",
        SkipReason = reason
    };

    private static string Display(UnattendedStep step)
        => string.IsNullOrWhiteSpace(step.Name) ? step.Target : step.Name;

    private static FirewallRuleSpec? ParseRuleSpec(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<FirewallRuleSpec>(json, JsonConfig.Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析防火墙规则参数失败: {ex.Message}");
            return null;
        }
    }

    private static AdapterIpConfig? ParseIpConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<AdapterIpConfig>(json, JsonConfig.Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"解析 IP 配置参数失败: {ex.Message}");
            return null;
        }
    }

    private TuningPreset? FindPreset(string key) => _tuning.Presets.FirstOrDefault(preset =>
        preset.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    private InstalledProgram? FindProgram(string name)
    {
        try
        {
            var programs = _uninstall.List();
            return programs.FirstOrDefault(p =>
                       p.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? programs.FirstOrDefault(p =>
                       p.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已安装程序失败: {ex.Message}");
            return null;
        }
    }

    // ---------------- 执行 ----------------

    public async Task<PreparedUnattended> PrepareAsync(
        UnattendedPlan plan, CancellationToken ct, IProgress<OperationStep>? progress = null)
    {
        var prepared = new PreparedUnattended();
        IReadOnlyList<AppxPackageInfo>? appxCache = null;
        IReadOnlyList<NetworkAdapterInfo>? adapterCache = null;
        IReadOnlyList<PowerPlanInfo>? powerPlanCache = null;
        var totalSteps = Math.Max(1, plan.Job.Steps.Count);
        var stepIndex = 0;

        async Task<IReadOnlyList<AppxPackageInfo>> GetAppxPackagesAsync()
            => appxCache ??= await _appx.EnumerateAsync(ct).ConfigureAwait(false);

        async Task<IReadOnlyList<NetworkAdapterInfo>> GetAdaptersAsync()
            => adapterCache ??= await _network.GetAdaptersAsync(ct).ConfigureAwait(false);

        async Task<IReadOnlyList<PowerPlanInfo>> GetPowerPlansAsync()
            => powerPlanCache ??= await _power.GetPlansAsync(ct).ConfigureAwait(false);

        foreach (var step in plan.Job.Steps)
        {
            stepIndex++;
            progress?.Report(new OperationStep
            {
                Message = $"正在预检第 {stepIndex}/{totalSteps} 步：{step.Kind}",
                Percent = Math.Min(8, 1 + stepIndex * 7 / totalSteps)
            });
            ct.ThrowIfCancellationRequested();

            switch (step.Kind.ToLowerInvariant())
            {
                case "preset":
                {
                    var preset = FindPreset(step.Target ?? string.Empty);
                    if (preset is null) continue;

                    var options = preset.Options.Select(key => _tuning.Find(key ?? string.Empty)).OfType<TuningOption>().ToList();
                    if (options.Count == 0) continue;

                    prepared.Operations.Add(_tuning.CreateBatch(
                        $"unattended.preset.{preset.Key}",
                        $"预设「{preset.Name}」",
                        $"应用预设「{preset.Name}」中的 {options.Count} 项调控",
                        RiskLevel.Caution,
                        options));
                    break;
                }

                case "tuning":
                {
                    var option = _tuning.Find(step.Target ?? string.Empty);
                    if (option is null) continue;

                    prepared.Operations.Add(_tuning.CreateBatch(
                        $"unattended.tuning.{option.Key}",
                        $"{option.Title}（{(step.Apply ? "应用" : "还原")}）",
                        option.Description,
                        RiskLevel.Caution,
                        new List<TuningChange> { new(option, step.Apply) }));
                    break;
                }

                case "ctxmenu":
                    prepared.Operations.Add(new ContextMenuStyleOperation(_registry, step.Apply));
                    break;

                case "update":
                {
                    var mode = (step.Target ?? string.Empty).ToLowerInvariant() switch
                    {
                        "normal" => WindowsUpdateMode.Normal,
                        "paused" => WindowsUpdateMode.Paused,
                        "extended" => WindowsUpdateMode.ExtendedPause,
                        "disabled" => WindowsUpdateMode.Disabled,
                        _ => (WindowsUpdateMode?)null
                    };
                    if (mode is null) continue;
                    prepared.Operations.Add(_windowsUpdate.CreateOperation(mode.Value));
                    break;
                }

                case "install":
                {
                    if (string.IsNullOrWhiteSpace(step.Target))
                    {
                        prepared.Notes.Add("跳过空的 winget ID");
                        continue;
                    }

                    var definition = new SoftwareDefinition
                    {
                        Id = step.Target.Trim(),
                        Name = string.IsNullOrWhiteSpace(step.Name) ? step.Target.Trim() : step.Name.Trim(),
                        Description = step.Note,
                        Category = "方案",
                        IsCustom = true
                    };
                    // 0.29.0-beta：方案里的 install 步骤改用 Chocolatey 安装；Target 填 choco 包名
                    prepared.Operations.Add(new WingetInstallBatchOperation(_winget, new[] { definition }));
                    break;
                }

                case "uninstall":
                {
                    var program = FindProgram(step.Target ?? string.Empty);
                    if (program is null)
                    {
                        prepared.Notes.Add($"未找到程序「{step.Target}」，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new UninstallProgramOperation(_uninstall, program));
                    break;
                }

                case "appx":
                {
                    var packages = await GetAppxPackagesAsync();
                    var needle = string.IsNullOrWhiteSpace(step.Name) ? (step.Target ?? string.Empty) : (step.Name ?? string.Empty);

                    var match = packages.FirstOrDefault(p =>
                                    p.Name.Equals(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                ?? packages.FirstOrDefault(p =>
                                    p.DisplayName.Equals(needle, StringComparison.OrdinalIgnoreCase))
                                ?? packages.FirstOrDefault(p =>
                                    p.Name.Contains(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                ?? packages.FirstOrDefault(p =>
                                    p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase));

                    if (match is null)
                    {
                        prepared.Notes.Add($"本机未安装「{needle}」，已跳过");
                        continue;
                    }

                    if (match.NonRemovable || match.IsProtected)
                    {
                        prepared.Notes.Add($"「{needle}」受系统保护，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new AppxRemoveBatchOperation(
                        _appx, new[] { match }, RiskLevel.Caution));
                    break;
                }

                case "appxforce":
                {
                    var packages = await GetAppxPackagesAsync();
                    var needle = string.IsNullOrWhiteSpace(step.Name) ? (step.Target ?? string.Empty) : (step.Name ?? string.Empty);

                    // 安全中心不再只做 Appx 强制卸载，改为完整移除 Defender 引擎
                    if ((step.Target ?? string.Empty).Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase)
                        || needle.Contains("安全中心", StringComparison.OrdinalIgnoreCase))
                    {
                        prepared.Operations.Add(_defender.CreateRemovalOperation(_appx));
                        break;
                    }

                    var match = packages.FirstOrDefault(p =>
                                    p.Name.Equals(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                ?? packages.FirstOrDefault(p =>
                                    p.DisplayName.Equals(needle, StringComparison.OrdinalIgnoreCase))
                                ?? packages.FirstOrDefault(p =>
                                    p.Name.Contains(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                ?? packages.FirstOrDefault(p =>
                                    p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase));

                    if (match is null)
                    {
                        prepared.Notes.Add($"本机未安装「{needle}」，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new AppxForceRemoveOperation(_appx, match));
                    break;
                }

                case "service":
                {
                    if (ProtectedServices.Contains(step.Target ?? string.Empty))
                    {
                        prepared.Notes.Add($"服务「{step.Target}」受系统保护，已跳过");
                        continue;
                    }

                    var state = _services.Capture(step.Target ?? string.Empty);
                    if (!state.Exists)
                    {
                        prepared.Notes.Add($"服务「{step.Target}」不存在，已跳过");
                        continue;
                    }

                    var startType = step.Value.ToLowerInvariant() switch
                    {
                        "auto" => 2,
                        "manual" => 3,
                        "disabled" => 4,
                        _ => -1
                    };
                    if (startType < 0 || string.IsNullOrWhiteSpace(step.Target)) continue;

                    prepared.Operations.Add(new ServiceStartModeOperation(
                        _services,
                        step.Target,
                        startType,
                        false,
                        startType == 4 ? RiskLevel.High : RiskLevel.Caution));
                    break;
                }

                case "task":
                {
                    var full = (step.Target ?? string.Empty).Trim('\\');
                    if (full.Length == 0) continue;

                    var separator = full.LastIndexOf('\\');
                    var path = separator <= 0 ? "\\" : "\\" + full[..separator];
                    var name = separator < 0 ? full : full[(separator + 1)..];
                    if (name.Length == 0) continue;

                    var tasksUnderPath = await _tasks.QueryTasksUnderPathAsync(path, ct).ConfigureAwait(false);
                    if (!tasksUnderPath.Any(task => task.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        prepared.Notes.Add($"计划任务「{full}」不存在，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new ScheduledTaskToggleOperation(_tasks, path, name, step.Apply));
                    break;
                }

                case "dns":
                {
                    var preset = NetworkService.Presets.FirstOrDefault(p =>
                        p.Key.Equals(step.Value, StringComparison.OrdinalIgnoreCase));
                    var adapters = await GetAdaptersAsync();
                    var adapter = int.TryParse(step.Target, out var interfaceIndex)
                        ? adapters.FirstOrDefault(a => a.Index == interfaceIndex)
                        : adapters.FirstOrDefault(a =>
                              a.Name.Equals(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                          ?? adapters.FirstOrDefault(a =>
                              a.Name.Contains(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase));

                    // 网卡改名后按记录时的 InterfaceIndex 回退匹配
                    if (adapter is null && int.TryParse(step.Note, out var recordedIndex))
                    {
                        adapter = adapters.FirstOrDefault(a => a.Index == recordedIndex);
                    }

                    if (preset is null || adapter is null)
                    {
                        prepared.Notes.Add($"DNS 步骤无法匹配适配器或预设（{step.Target} / {step.Value}），已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new NetworkDnsOperation(_network, adapter, preset.Servers, preset.Name));
                    break;
                }

                case "power":
                {
                    if (string.IsNullOrWhiteSpace(step.Target) && string.IsNullOrWhiteSpace(step.Name)) continue;

                    var plans = await GetPowerPlansAsync();
                    var target = PowerService.NormalizeGuid(step.Target ?? string.Empty);
                    var requestedName = (step.Name ?? string.Empty).Trim();

                    var matchedPlan = plans.FirstOrDefault(item =>
                        item.Guid.Equals(target, StringComparison.OrdinalIgnoreCase));
                    if (matchedPlan is null && requestedName.Length > 0)
                    {
                        matchedPlan = plans.FirstOrDefault(item =>
                                   item.Name.Equals(requestedName, StringComparison.CurrentCultureIgnoreCase))
                               ?? plans.FirstOrDefault(item =>
                                   item.Name.Contains(requestedName, StringComparison.CurrentCultureIgnoreCase));
                    }

                    var effectiveGuid = matchedPlan?.Guid ?? target;
                    var effectiveName = matchedPlan?.Name ??
                                        (requestedName.Length > 0 ? requestedName : target);

                    if (matchedPlan is null && !PowerService.IsKnownScheme(effectiveGuid))
                    {
                        if (PowerService.TryGetKnownSchemeByName(requestedName, out var knownGuid))
                        {
                            effectiveGuid = knownGuid;
                            effectiveName = requestedName.Length > 0 ? requestedName : knownGuid;
                        }
                        else
                        {
                            prepared.Notes.Add(
                                $"电源计划「{requestedName}」({step.Target}) 不存在，已跳过");
                            continue;
                        }
                    }

                    prepared.Operations.Add(new PowerPlanOperation(
                        _power, effectiveGuid, effectiveName));
                    break;
                }

                case "cleanup":
                {
                    var category = _cleanup.GetCategories().FirstOrDefault(c =>
                        c.Key.Equals(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                    if (category is null)
                    {
                        prepared.Notes.Add($"未找到清理类别「{step.Target}」，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new CleanupStepOperation(_cleanup, category));
                    break;
                }

                case "driver":
                {
                    if (string.IsNullOrWhiteSpace(step.Target)) continue;
                    prepared.Operations.Add(new DriverExportOperation(_drivers, step.Target));
                    break;
                }

                case "defender":
                {
                    ISystemOperation? operation = step.Value.ToLowerInvariant() switch
                    {
                        "realtime-on" => _defender.CreateRealTimeOperation(true),
                        "realtime-off" => _defender.CreateRealTimeOperation(false),
                        "cloud-on" => _defender.CreateCloudOperation(true),
                        "cloud-off" => _defender.CreateCloudOperation(false),
                        _ => null
                    };

                    if (operation is null)
                    {
                        prepared.Notes.Add($"未知的 Defender 动作「{step.Value}」，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(operation);
                    break;
                }

                case "defender-remove":
                {
                    prepared.Operations.Add(_defender.CreateRemovalOperation(_appx));
                    break;
                }

                case "onedrive":
                {
                    if (!OneDriveRemoveOperation.IsInstalled)
                    {
                        prepared.Notes.Add("本机未检测到 OneDrive，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new OneDriveRemoveOperation());
                    break;
                }

                case "firewall-profile":
                {
                    if (string.IsNullOrWhiteSpace(step.Target)) continue;
                    prepared.Operations.Add(new FirewallProfileOperation(_firewall, step.Target, step.Apply));
                    break;
                }

                case "firewall-rule":
                {
                    var spec = ParseRuleSpec(step.Value);
                    if (spec is null)
                    {
                        prepared.Notes.Add("防火墙规则参数无效，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new FirewallRuleAddOperation(_firewall, spec));
                    break;
                }

                case "firewall-rule-remove":
                {
                    if (string.IsNullOrWhiteSpace(step.Target)) continue;
                    prepared.Operations.Add(new FirewallRuleRemoveOperation(_firewall, step.Target));
                    break;
                }

                case "driver-install":
                {
                    var path = step.Target;
                    if (string.IsNullOrWhiteSpace(path)
                        || (!File.Exists(path) && !Directory.Exists(path)))
                    {
                        prepared.Notes.Add($"驱动路径不存在：{path}，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new DriverInstallOperation(path));
                    break;
                }

                case "driver-remove":
                {
                    if (string.IsNullOrWhiteSpace(step.Target)) continue;
                    prepared.Operations.Add(new DriverRemoveOperation(step.Target ?? string.Empty, step.Name ?? string.Empty));
                    break;
                }

                case "feature":
                {
                    if (string.IsNullOrWhiteSpace(step.Target)) continue;
                    prepared.Operations.Add(new OptionalFeatureOperation(_features, step.Target, step.Apply));
                    break;
                }

                case "net-ip":
                {
                    var config = ParseIpConfig(step.Value);
                    if (config is null)
                    {
                        prepared.Notes.Add("IP 参数无效，已跳过");
                        continue;
                    }

                    var adapters = await GetAdaptersAsync();
                    var adapter = adapters.FirstOrDefault(a =>
                                      a.Name.Equals(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                                  ?? adapters.FirstOrDefault(a =>
                                      a.Name.Contains(step.Target ?? string.Empty, StringComparison.OrdinalIgnoreCase));

                    // 网卡改名后按记录时的 InterfaceIndex 回退匹配
                    if (adapter is null && int.TryParse(step.Note, out var recordedIndex) && recordedIndex > 0)
                    {
                        adapter = adapters.FirstOrDefault(a => a.Index == recordedIndex);
                    }

                    if (adapter is null)
                    {
                        prepared.Notes.Add($"未找到网络适配器「{step.Target}」，已跳过");
                        continue;
                    }

                    prepared.Operations.Add(new NetworkIpOperation(
                        _network, adapter, config.Dhcp, config.Address, config.Prefix, config.Gateway));
                    break;
                }
            }
        }

        return prepared;
    }

    public async Task<UnattendedRunResult> RunAsync(
        UnattendedPlan plan,
        OperationRunner runner,
        IProgress<OperationStep>? progress,
        CancellationToken ct)
    {
        var prepared = await PrepareAsync(plan, ct, progress).ConfigureAwait(false);
        if (prepared.Operations.Count == 0)
        {
            return new UnattendedRunResult { Notes = prepared.Notes };
        }

        var risk = prepared.Operations.Max(operation => operation.RiskLevel);
        var composite = new CompositeOperation(
            $"unattended.{plan.Job.Key}",
            $"执行中心 · {plan.Job.Name}",
            $"{prepared.Operations.Count} 个步骤，按顺序执行；{(plan.Job.ContinueOnError ? "单步失败会跳过并继续" : "失败时按逆序回滚可回滚的部分")}",
            risk,
            prepared.Operations,
            plan.Job.ContinueOnError);

        var execution = await runner.ExecuteAsync(composite, progress, ct).ConfigureAwait(false);

        if (execution.Success && plan.Job.RestartExplorer && RequiresExplorerRestart(plan.Job))
        {
            progress?.Report(new OperationStep { Message = "正在重启资源管理器…", Percent = 98 });
            try
            {
                await _explorer.RestartAsync(CancellationToken.None).ConfigureAwait(false);
                prepared.Notes.Add("已重启资源管理器，界面与右键菜单设置已生效");
                Log.Info("执行中心任务完成，已重启资源管理器");
            }
            catch (Exception ex)
            {
                prepared.Notes.Add($"重启资源管理器失败：{ex.Message}");
                Log.Warn($"重启资源管理器失败: {ex.Message}");
            }
        }

        var report = WriteReport(plan, execution, prepared.Notes, composite.Failures);

        return new UnattendedRunResult
        {
            Execution = execution,
            ReportPath = report,
            Notes = prepared.Notes,
            Failures = composite.Failures
        };
    }

    /// <summary>生成 HTML + JSON 报告，返回 HTML 路径。</summary>
    public string WriteReport(
        UnattendedPlan plan,
        OperationExecution execution,
        IReadOnlyList<string>? notes = null,
        IReadOnlyList<string>? failures = null)
    {
        var directory = AppPaths.ReportsRoot;
        Directory.CreateDirectory(directory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"execution-{stamp}-{Sanitize(plan.Job.Key)}";
        var htmlPath = Path.Combine(directory, baseName + ".html");
        var jsonPath = Path.Combine(directory, baseName + ".json");

        var status = execution.Cancelled
            ? "已取消"
            : execution.Success
                ? "成功"
                : execution.RolledBack ? "失败（已自动回滚）" : "失败";

        var rows = new StringBuilder();
        foreach (var change in execution.Preview.Changes)
        {
            rows.AppendLine($"""
                    <tr>
                      <td>{Encode(change.Kind)}</td>
                      <td class="mono">{Encode(change.Target)}</td>
                      <td class="mono">{Encode(change.Before ?? "（未设置）")}</td>
                      <td class="mono">{Encode(change.After ?? "（删除）")}</td>
                    </tr>
            """);
        }

        var duration = execution.FinishedAt - execution.StartedAt;
        var notesText = notes is { Count: > 0 }
            ? "<p class=\"sub\">" + string.Join("<br>", notes.Select(Encode)) + "</p>"
            : string.Empty;

        var failuresText = failures is { Count: > 0 }
            ? "<div class=\"cell\"><b>失败步骤（已跳过）</b><span class=\"bad\">" +
              string.Join("<br>", failures.Select(Encode)) + "</span></div>"
            : string.Empty;

        var html = ReportTemplate
            .Replace("{{NAME}}", Encode(plan.Job.Name))
            .Replace("{{DESCRIPTION}}", Encode(plan.Job.Description))
            .Replace("{{STATUS}}", Encode(status))
            .Replace("{{STATUS_CLASS}}", execution.Success ? "ok" : "bad")
            .Replace("{{TIME}}", Encode(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")))
            .Replace("{{DURATION}}", $"{duration.TotalSeconds:0.0} 秒")
            .Replace("{{COUNT}}", execution.Preview.Changes.Count.ToString())
            .Replace("{{NOTES}}", notesText)
            .Replace("{{FAILURES}}", failuresText)
            .Replace("{{REPORT_ID}}", Encode(plan.Job.Key))
            .Replace("{{BACKUP}}", Encode(execution.BackupDirectory ?? "—"))
            .Replace("{{MESSAGE}}", Encode(execution.Result.Message))
            .Replace("{{ROWS}}", rows.ToString().TrimEnd());

        File.WriteAllText(htmlPath, html, new UTF8Encoding(false));

        var jsonReport = new
        {
            schemaVersion = 2,
            job = plan.Job.Key,
            name = plan.Job.Name,
            finishedAt = execution.FinishedAt,
            success = execution.Success,
            cancelled = execution.Cancelled,
            rolledBack = execution.RolledBack,
            rollbackFailed = execution.RollbackFailed,
            message = execution.Result.Message,
            backupDirectory = execution.BackupDirectory,
            notes,
            failures,
            steps = plan.Job.Steps,
            changes = execution.Preview.Changes
        };

        File.WriteAllText(jsonPath,
            JsonSerializer.Serialize(jsonReport, JsonConfig.Options),
            new UTF8Encoding(false));

        Log.Info($"执行报告已生成: {htmlPath}");
        return htmlPath;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.Length == 0 ? "job" : builder.ToString();
    }

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private const string ReportTemplate = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>FreshStart 执行报告 · {{NAME}}</title>
<style>
  :root { color-scheme: light dark; }
  body {
    margin: 0; padding: 40px; font-family: -apple-system, "Segoe UI", "Microsoft YaHei UI", sans-serif;
    background: #0B0C0E; color: #F2F3F5; line-height: 1.6;
  }
  main { max-width: 960px; margin: 0 auto; }
  h1 { font-size: 22px; margin: 0 0 4px; }
  .sub { color: #9A9DA5; font-size: 13px; margin: 0 0 18px; }
  .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(180px, 1fr)); gap: 12px; margin-bottom: 24px; }
  .cell { background: rgba(255,255,255,.04); border: 1px solid rgba(255,255,255,.06); border-radius: 12px; padding: 14px 16px; }
  .cell b { display: block; font-size: 12px; color: #9A9DA5; font-weight: 400; margin-bottom: 4px; }
  .cell span { font-size: 14px; }
  .ok { color: #4ADE80; }
  .bad { color: #FF6B6B; }
  table { width: 100%; border-collapse: collapse; font-size: 12.5px; }
  th, td { text-align: left; padding: 10px 8px; border-bottom: 1px solid rgba(255,255,255,.06); vertical-align: top; }
  th { color: #9A9DA5; font-weight: 500; }
  .mono { font-family: ui-monospace, Consolas, monospace; word-break: break-all; color: #C9CCD3; }
  footer { margin-top: 28px; color: #5A5D66; font-size: 12px; }
</style>
</head>
<body>
<main>
  <h1>FreshStart 执行报告 · {{NAME}}</h1>
  <p class="sub">{{DESCRIPTION}}</p>

  <div class="grid">
    <div class="cell"><b>结果</b><span class="{{STATUS_CLASS}}">{{STATUS}}</span></div>
    <div class="cell"><b>完成时间</b><span>{{TIME}}</span></div>
    <div class="cell"><b>耗时</b><span>{{DURATION}}</span></div>
    <div class="cell"><b>变更数</b><span>{{COUNT}}</span></div>
  </div>

  <div class="grid">
    <div class="cell"><b>方案</b><span class="mono">{{REPORT_ID}}</span></div>
    <div class="cell"><b>消息</b><span>{{MESSAGE}}</span></div>
  </div>

  <p class="sub mono">备份目录：{{BACKUP}}</p>
  {{NOTES}}
  {{FAILURES}}

  <table>
    <thead>
      <tr><th>类型</th><th>目标</th><th>修改前</th><th>修改后</th></tr>
    </thead>
    <tbody>
{{ROWS}}
    </tbody>
  </table>

  <footer>由 FreshStart 生成 · 所有写入均经过备份与校验 · 不采集、不上传任何数据</footer>
</main>
</body>
</html>
""";
}
