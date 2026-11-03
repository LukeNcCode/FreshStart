using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using Microsoft.Win32;

namespace FreshStart.Services.Tuning;

/// <summary>一个注册表调控项的写入目标。OnValue 表示“优化/保护已生效”，OffValue 为关闭时的值。</summary>
public sealed class TuningTarget
{
    public string Hive { get; set; } = "HKCU";
    public string View { get; set; } = "64";
    public string SubKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public RegistryValueKind Kind { get; set; } = RegistryValueKind.DWord;
    public object? OnValue { get; set; }
    public object? OffValue { get; set; }
}

public sealed class TuningOption
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string Category { get; set; } = "隐私";
    public bool RequiresRestart { get; set; }
    public TuningTarget Target { get; set; } = new();
}

public sealed class TuningPreset
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tag { get; set; } = string.Empty;
    public List<string> Options { get; set; } = new();
}

/// <summary>
/// 内置调控清单。只使用白名单内的注册表路径，全部为值级写入，可逐项回滚。
/// 隐私项与性能项共用一套定义，供“隐私清理”和“一键开荒”复用。
/// </summary>
public static class TuningCatalog
{
    public static IReadOnlyList<TuningOption> Options { get; } = new List<TuningOption>
    {
        new()
        {
            Key = "telemetry", Title = "诊断数据", Category = "隐私",
            Description = "把诊断数据策略设为“仅必需”，减少遥测上报",
            Impact = "策略级设置，可能需要重启或重新登录生效",
            Target = new() { Hive = "HKLM", SubKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", Name = "AllowTelemetry", OnValue = 0 }
        },
        new()
        {
            Key = "ads", Title = "广告 ID", Category = "隐私",
            Description = "关闭应用使用广告 ID 进行个性化推荐",
            Impact = "商店/内置应用不再基于广告 ID 推荐",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", Name = "Enabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "activity", Title = "活动历史", Category = "隐私",
            Description = "停止向微软上传活动历史（时间线）",
            Impact = "任务视图不再显示跨设备历史",
            Target = new() { Hive = "HKLM", SubKey = @"SOFTWARE\Policies\Microsoft\Windows\System", Name = "PublishUserActivities", OnValue = 0 }
        },
        new()
        {
            Key = "feedback", Title = "反馈请求", Category = "隐私",
            Description = "不再主动弹出 Windows 反馈调查",
            Impact = "只影响反馈邀请，不影响手动提交反馈",
            Target = new() { Hive = "HKLM", SubKey = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", Name = "DoNotShowFeedbackNotifications", OnValue = 1 }
        },
        new()
        {
            Key = "location", Title = "位置服务", Category = "隐私",
            Description = "关闭系统位置服务（策略）",
            Impact = "地图、天气等应用可能无法定位",
            Target = new() { Hive = "HKLM", SubKey = @"SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", Name = "DisableLocation", OnValue = 1 }
        },
        new()
        {
            Key = "tailored", Title = "定制体验", Category = "隐私",
            Description = "停止使用诊断数据生成个性化提示与建议",
            Impact = "Windows 提示不再基于使用习惯定制",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Privacy", Name = "TailoredExperiencesWithDiagnosticDataEnabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "speech", Title = "在线语音识别", Category = "隐私",
            Description = "关闭输入个性化与在线语音学习（策略）",
            Impact = "听写/语音输入可能需要重新开启",
            Target = new() { Hive = "HKLM", SubKey = @"SOFTWARE\Policies\Microsoft\InputPersonalization", Name = "AllowInputPersonalization", OnValue = 0 }
        },
        new()
        {
            Key = "recommendations", Title = "开始菜单推荐", Category = "隐私",
            Description = "关闭开始菜单中的建议内容与推广",
            Impact = "开始菜单只显示已固定的内容",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", Name = "SubscribedContent-338388Enabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "cloudcontent", Title = "Windows 消费级推送", Category = "隐私",
            Description = "关闭自动安装推广应用与消费级提示（策略）",
            Impact = "不影响商店手动安装；重新开启需改回策略",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Policies\Microsoft\Windows\CloudContent", Name = "DisableWindowsConsumerFeatures", OnValue = 1 }
        },

        new()
        {
            Key = "gamedvr", Title = "Game DVR", Category = "游戏",
            Description = "关闭后台游戏录制，释放部分 GPU/CPU 开销",
            Impact = "Win+G 录制功能需要重新开启",
            Target = new() { Hive = "HKCU", SubKey = @"System\GameConfigStore", Name = "GameDVR_Enabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "appcapture", Title = "后台录屏", Category = "游戏",
            Description = "关闭应用后台捕获（AppCapture）",
            Impact = "与 Game DVR 一起关闭后录制入口不可用",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR", Name = "AppCaptureEnabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "gamebar", Title = "游戏栏快捷键", Category = "游戏",
            Description = "关闭游戏栏的 Game DVR 控制器",
            Impact = "Win+G 仍可打开游戏栏，但不再后台录制",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\GameBar", Name = "AllowGameDVR", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "gamemode", Title = "游戏模式", Category = "游戏",
            Description = "开启 Windows 游戏模式，优先分配 CPU/GPU 给前台游戏",
            Impact = "后台任务可能被延后；对部分老游戏无感",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\GameBar", Name = "AutoGameModeEnabled", OnValue = 1, OffValue = 0 }
        },
        new()
        {
            Key = "hags", Title = "硬件加速 GPU 调度", Category = "游戏",
            Description = "开启 HAGS，让 GPU 自行管理显存调度，降低部分游戏延迟",
            Impact = "需要重启；极少数驱动/游戏可能出现兼容问题",
            RequiresRestart = true,
            Target = new() { Hive = "HKLM", SubKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", Name = "HwSchMode", OnValue = 2, OffValue = 1 }
        },
        new()
        {
            Key = "visualfx", Title = "视觉效果优先性能", Category = "界面",
            Description = "把窗口动画/阴影调为“最佳性能”",
            Impact = "界面过渡会明显变快，观感更朴素",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", Name = "VisualFXSetting", OnValue = 2, OffValue = 0 }
        },
        new()
        {
            Key = "menudelay", Title = "菜单展开加速", Category = "界面",
            Description = "把菜单展开延迟从 400ms 降到 200ms",
            Impact = "资源管理器/右键菜单响应更快",
            Target = new() { Hive = "HKCU", SubKey = @"Control Panel\Desktop", Name = "MenuShowDelay", Kind = RegistryValueKind.String, OnValue = "200", OffValue = "400" }
        },
        new()
        {
            Key = "searchbox", Title = "任务栏搜索图标", Category = "界面",
            Description = "把任务栏搜索框收起为图标，释放任务栏空间",
            Impact = "搜索功能仍可用，点击图标展开",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Search", Name = "SearchboxTaskbarMode", OnValue = 1, OffValue = 2 }
        },
        new()
        {
            Key = "transparency", Title = "禁用透明效果", Category = "界面",
            Description = "关闭窗口/任务栏的亚克力透明效果",
            Impact = "界面不透明，低端设备更流畅",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", Name = "EnableTransparency", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "taskbar-left", Title = "任务栏左对齐", Category = "界面",
            Description = "把任务栏图标从居中改为靠左",
            Impact = "需要重启资源管理器或重新登录",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = "TaskbarAl", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "clock-seconds", Title = "时钟显示秒", Category = "界面",
            Description = "在任务栏时钟中显示秒数",
            Impact = "时钟区域略宽，几乎无性能影响",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = "ShowSecondsInSystemClock", OnValue = 1, OffValue = 0 }
        },
        new()
        {
            Key = "widgets", Title = "隐藏小组件按钮", Category = "界面",
            Description = "从任务栏移除小组件入口",
            Impact = "Win+W 仍可打开小组件，但任务栏不再显示",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = "TaskbarDa", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "taskview", Title = "隐藏任务视图按钮", Category = "界面",
            Description = "从任务栏移除任务视图图标",
            Impact = "Win+Tab 快捷键仍可用",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = "TaskbarMn", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "bing-search", Title = "关闭搜索网络建议", Category = "界面",
            Description = "开始菜单搜索不再显示 Bing 网络结果",
            Impact = "只搜索本地内容，搜索更干净",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Search", Name = "BingSearchEnabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "recent-files", Title = "关闭最近使用文件", Category = "界面",
            Description = "资源管理器快速访问不再记录最近文件",
            Impact = "快速访问更清爽，但少了快捷入口",
            Target = new() { Hive = "HKCU", SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", Name = "Start_TrackDocs", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "faststartup", Title = "关闭快速启动", Category = "性能",
            Description = "关闭休眠式快速启动，避免关机后状态残留",
            Impact = "关机/开机略慢，但双系统与硬件变更更稳定",
            RequiresRestart = true,
            Target = new() { Hive = "HKLM", SubKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", Name = "HiberbootEnabled", OnValue = 0, OffValue = 1 }
        },
        new()
        {
            Key = "longpaths", Title = "启用长路径", Category = "性能",
            Description = "允许超过 260 字符的文件路径",
            Impact = "部分旧程序可能不兼容超长路径",
            RequiresRestart = true,
            Target = new() { Hive = "HKLM", SubKey = @"SYSTEM\CurrentControlSet\Control\FileSystem", Name = "LongPathsEnabled", OnValue = 1, OffValue = 0 }
        }
    };

    public static IReadOnlyList<TuningPreset> Presets { get; } = new List<TuningPreset>
    {
        new()
        {
            Key = "minimal", Name = "极简清爽", Tag = "推荐",
            Description = "关掉推广、遥测与花哨动效，适合日常办公",
            Options = new() { "telemetry", "ads", "recommendations", "cloudcontent", "visualfx", "menudelay", "searchbox", "transparency" }
        },
        new()
        {
            Key = "gaming", Name = "游戏优先", Tag = "性能",
            Description = "关闭录制与透明效果，缩短菜单延迟，减少后台开销",
            Options = new() { "gamedvr", "appcapture", "gamebar", "visualfx", "menudelay", "faststartup" }
        },
        new()
        {
            Key = "privacy", Name = "隐私优先", Tag = "安全",
            Description = "关闭诊断数据、广告 ID、活动历史、位置与推荐内容",
            Options = new() { "telemetry", "ads", "activity", "feedback", "location", "tailored", "speech", "recommendations", "cloudcontent" }
        },
        new()
        {
            Key = "developer", Name = "开发环境", Tag = "进阶",
            Description = "减少打扰、启用长路径，并保留必要的系统功能",
            Options = new() { "telemetry", "activity", "feedback", "tailored", "longpaths", "menudelay" }
        }
    };
}

/// <summary>读取当前状态；所有写入都交给 TuningBatchOperation，页面不直接碰注册表。</summary>
public sealed class TuningService
{
    private readonly RegistryService _registry;

    public TuningService(RegistryService registry) => _registry = registry;

    public IReadOnlyList<TuningOption> Options => TuningCatalog.Options;
    public IReadOnlyList<TuningPreset> Presets => TuningCatalog.Presets;

    public TuningOption? Find(string key)
        => TuningCatalog.Options.FirstOrDefault(option =>
            string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>由服务统一创建批量操作，避免页面直接持有 RegistryService。</summary>
    public TuningBatchOperation CreateBatch(
        string id,
        string name,
        string description,
        RiskLevel risk,
        IReadOnlyList<TuningOption> options,
        bool apply = true)
        => new(_registry, id, name, description, risk, options, apply);

    /// <summary>同一批次里每项可以有独立的开/关目标值。</summary>
    public TuningBatchOperation CreateBatch(
        string id,
        string name,
        string description,
        RiskLevel risk,
        IReadOnlyList<TuningChange> changes)
        => new(_registry, id, name, description, risk, changes);

    public bool IsApplied(TuningOption option)
    {
        var state = _registry.CaptureValue(option.Target.Hive, option.Target.View, option.Target.SubKey, option.Target.Name);
        return Matches(state, option.Target.OnValue);
    }

    public string CurrentText(TuningOption option)
    {
        var state = _registry.CaptureValue(option.Target.Hive, option.Target.View, option.Target.SubKey, option.Target.Name);
        if (!state.Existed) return "未设置";
        return state.ValueType switch
        {
            nameof(RegistryValueKind.DWord) => state.DwordData?.ToString() ?? "0",
            nameof(RegistryValueKind.QWord) => state.QwordData?.ToString() ?? "0",
            _ => state.StringData ?? string.Empty
        };
    }

    private static bool Matches(RegistryValueState state, object? expected)
    {
        if (expected is null) return !state.Existed;
        return expected switch
        {
            int i => state.Existed && state.DwordData == i,
            long l => state.Existed && state.QwordData == l,
            string s => state.Existed && string.Equals(state.StringData, s, StringComparison.Ordinal),
            _ => false
        };
    }
}

/// <summary>批量操作中的单项：option + 目标状态（true 应用，false 还原）。</summary>
public sealed class TuningChange
{
    public TuningChange(TuningOption option, bool apply)
    {
        Option = option;
        Apply = apply;
    }

    public TuningOption Option { get; }
    public bool Apply { get; }
}

/// <summary>
/// 批量写入调控项：先快照全部目标值，再逐项写入并校验；任一项失败由 Runner 统一回滚。
/// 同一批次中每一项都可以独立选择应用或关闭，解决“只能开不能关”的问题。
/// </summary>
public sealed class TuningBatchOperation : OperationBase
{
    private readonly RegistryService _registry;
    private readonly IReadOnlyList<TuningChange> _changes;

    public TuningBatchOperation(
        RegistryService registry,
        string id,
        string name,
        string description,
        RiskLevel risk,
        IReadOnlyList<TuningOption> options,
        bool apply = true)
        : this(registry, id, name, description, risk,
            options.Select(option => new TuningChange(option, apply)).ToList())
    {
    }

    public TuningBatchOperation(
        RegistryService registry,
        string id,
        string name,
        string description,
        RiskLevel risk,
        IReadOnlyList<TuningChange> changes)
    {
        _registry = registry;
        _changes = changes;
        Id = id;
        Name = name;
        Description = description;
        RiskLevel = risk;
        foreach (var change in changes)
        {
            FreshStart.Core.Security.RegistryPathGuard.EnsureWritable(
                change.Option.Target.Hive, change.Option.Target.SubKey);
        }
    }

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override RiskLevel RiskLevel { get; }

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => _changes.Select(change =>
    {
        var option = change.Option;
        var state = _registry.CaptureValue(option.Target.Hive, option.Target.View, option.Target.SubKey, option.Target.Name);
        var desired = BuildDesired(option, change.Apply);
        return new ChangeDescriptor
        {
            Target = state.FullPath,
            Kind = "注册表值",
            Before = state.Existed ? Format(state) : "（不存在）",
            After = desired.Existed ? Format(desired) : "（删除）",
            Impact = option.Impact
        };
    }).ToList();

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entries = _changes.Select(change =>
        {
            var option = change.Option;
            var state = _registry.CaptureValue(option.Target.Hive, option.Target.View, option.Target.SubKey, option.Target.Name);
            return new SnapshotEntry
            {
                Kind = SnapshotKind.RegistryValue,
                Target = state.FullPath,
                ValueName = option.Target.Name,
                Existed = state.Existed,
                ValueType = state.ValueType,
                Sddl = state.Sddl,
                DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
            };
        }).ToList();
        return Task.FromResult(CreateSnapshot(entries));
    }

    protected override Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        for (var i = 0; i < _changes.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var change = _changes[i];
            progress?.Report(new OperationStep
            {
                Message = change.Apply
                    ? $"正在应用「{change.Option.Title}」…"
                    : $"正在关闭「{change.Option.Title}」…",
                Percent = (int)((i + 1) * 90.0 / _changes.Count)
            });

            var desired = BuildDesired(change.Option, change.Apply);
            _registry.ApplyValue(desired);
            Ensure(_registry.ValueEquals(desired), $"写入后校验失败：{desired.FullPath}");
        }
        return Task.FromResult(OperationResult.Ok($"已更新 {_changes.Count} 项设置"));
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var restored = 0;
        foreach (var entry in state.Entries.Where(e => e.Kind == SnapshotKind.RegistryValue))
        {
            var original = JsonSerializer.Deserialize<RegistryValueState>(entry.DataJson!, JsonConfig.Options);
            if (original is null) continue;
            progress?.Report(new OperationStep { Message = $"正在回滚 {original.FullPath}…", Percent = 60 });
            _registry.ApplyValue(original);
            restored++;
        }
        Log.Info($"调控批量操作回滚完成：{restored} 项");
        return Task.FromResult(OperationResult.Ok($"已回滚 {restored} 项设置"));
    }

    private static RegistryValueState BuildDesired(TuningOption option, bool apply)
    {
        var value = apply ? option.Target.OnValue : option.Target.OffValue;
        return new RegistryValueState
        {
            Hive = option.Target.Hive,
            View = option.Target.View,
            SubKey = option.Target.SubKey,
            Name = option.Target.Name,
            Existed = value is not null,
            ValueType = value is null ? null : option.Target.Kind.ToString(),
            DwordData = value is int i ? i : null,
            QwordData = value is long l ? l : null,
            StringData = value as string
        };
    }

    private static string Format(RegistryValueState state) => state.ValueType switch
    {
        nameof(RegistryValueKind.DWord) => state.DwordData?.ToString() ?? "0",
        nameof(RegistryValueKind.QWord) => state.QwordData?.ToString() ?? "0",
        _ => state.StringData ?? string.Empty
    };
}
