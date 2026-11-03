using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Modules.Controls;
using FreshStart.Services.Appx;
using FreshStart.Services.Cleanup;
using FreshStart.Services.Driver;
using FreshStart.Services.Features;
using FreshStart.Services.Firewall;
using FreshStart.Services.Network;
using FreshStart.Services.Power;
using FreshStart.Services.Software;
using FreshStart.Services.Startup;
using FreshStart.Services.Tuning;
using FreshStart.Services.Unattended;
using Microsoft.Win32;

namespace FreshStart.Modules.Unattended;

/// <summary>
/// 一行可循环切换的配置项：不修改 → 选项 A → 选项 B …
/// 切换后立即记录/更新对应的方案步骤。
/// </summary>
public sealed class ModeRow : ObservableObject
{
    private readonly string[] _values;
    private readonly string[] _labels;
    private readonly Func<string, UnattendedStep?> _build;
    private int _index;

    public ModeRow(
        string title,
        string detail,
        string[] values,
        string[] labels,
        Func<string, UnattendedStep?> build)
    {
        Title = title;
        Detail = detail;
        _values = values;
        _labels = labels;
        _build = build;
    }

    public string Title { get; }
    public string Detail { get; }
    public string StateText => DisabledText ?? _labels[_index];
    public bool IsRecorded => _index > 0;

    /// <summary>false 表示该条目不可选（例如推荐软件已安装）。</summary>
    public bool CanSelect { get; private set; } = true;

    public void Disable(string stateText)
    {
        CanSelect = false;
        _index = 0;
        LastKey = null;
        DisabledText = stateText;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRecorded));
        OnPropertyChanged(nameof(CanSelect));
    }

    private string? DisabledText { get; set; }

    /// <summary>上一次记录到方案里的 key，用于切换/取消时移除旧项。</summary>
    public string? LastKey { get; set; }

    public void Cycle()
    {
        if (!CanSelect) return;
        _index = (_index + 1) % _values.Length;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRecorded));
    }

    public UnattendedStep? ToStep() => _index == 0 ? null : _build(_values[_index]);

    /// <summary>按步骤 key（kind|target）把行切到匹配的状态，用于导入方案时回填。</summary>
    public bool Select(string key)
    {
        if (!CanSelect) return false;
        for (var i = 1; i < _values.Length; i++)
        {
            var step = _build(_values[i]);
            if (step is null) continue;
            if ($"{step.Kind}|{step.Target}" == key)
            {
                _index = i;
                LastKey = key;
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(IsRecorded));
                return true;
            }
        }

        return false;
    }

    public void Reset()
    {
        _index = 0;
        LastKey = null;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsRecorded));
    }
}

public sealed class RecordedStepRow
{
    public RecordedStepRow(UnattendedStep step)
    {
        Step = step;
        Title = step.Kind switch
        {
            "preset" => $"预设「{step.Name}」",
            "tuning" => $"{step.Name} → {(step.Apply ? "应用" : "还原")}",
            "ctxmenu" => step.Apply ? "右键菜单 → 经典" : "右键菜单 → 新版",
            "update" => $"Windows 更新 → {step.Name}",
            "service" => $"服务 {step.Target} → {ModeText(step.Value)}",
            "task" => $"计划任务 {step.Name} → {(step.Apply ? "启用" : "禁用")}",
            "dns" => $"DNS → {step.Name}",
            "power" => $"电源计划 → {step.Name}",
            "install" => $"安装「{step.Name}」",
            "uninstall" => $"卸载「{step.Target}」",
            "appx" => $"移除「{step.Name}」",
            "appxforce" => $"强制卸载「{step.Name}」",
            "cleanup" => $"清理「{step.Name}」",
            "driver" => "导出驱动",
            "driver-install" => $"安装驱动「{step.Name}」",
            "driver-remove" => $"删除驱动「{step.Name}」",
            "firewall-profile" => $"防火墙 {step.Name} → {(step.Apply ? "开启" : "关闭")}",
            "firewall-rule" => $"新增防火墙规则「{step.Name}」",
            "firewall-rule-remove" => $"删除防火墙规则「{step.Name}」",
            "feature" => $"{(step.Apply ? "启用" : "禁用")}功能「{step.Name}」",
            "defender-remove" => "彻底移除 Defender 引擎",
            "net-ip" => $"IP 配置 → {step.Target}（{step.Name}）",
            _ => step.Kind
        };
    }

    public UnattendedStep Step { get; }
    public string Title { get; }

    private static string ModeText(string value) => value switch
    {
        "auto" => "自动",
        "manual" => "手动",
        "disabled" => "禁用",
        _ => value
    };
}

public sealed partial class ScriptEditorViewModel : ObservableObject
{
    private static readonly (string Value, string Title)[] UpdateModes =
    {
        ("normal", "恢复正常"),
        ("paused", "暂停 7 天"),
        ("extended", "超长延期"),
        ("disabled", "彻底关闭")
    };

    private readonly UnattendedService _unattended;
    private readonly TuningService _tuning;
    private readonly ServiceControlService _services;
    private readonly ScheduledTaskService _tasks;
    private readonly NetworkService _network;
    private readonly PowerService _power;
    private readonly CleanupService _cleanup;
    private readonly DriverService _drivers;
    private readonly FirewallService _firewall;
    private readonly OptionalFeatureService _features;
    private readonly AppxService _appx;
    private readonly IToastService _toast;

    private readonly Dictionary<string, UnattendedStep> _recorded = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModeRow> _serviceRows = new();
    private readonly List<ModeRow> _taskRows = new();
    private readonly List<ModeRow> _appxRows = new();
    private readonly List<ModeRow> _firewallRuleRows = new();
    private readonly List<ModeRow> _driverRows = new();
    private int _ruleDirection;
    private int _ruleAction;
    private int _ruleProtocol;

    private List<NetworkAdapterInfo> _adapters = new();
    private List<PowerPlanInfo> _plans = new();
    private int _adapterIndex;
    private int _dnsPresetIndex;
    private int _planIndex;
    private bool _loaded;

    public ScriptEditorViewModel(
        UnattendedService unattended,
        TuningService tuning,
        ServiceControlService services,
        ScheduledTaskService tasks,
        NetworkService network,
        PowerService power,
        CleanupService cleanup,
        DriverService drivers,
        AppxService appx,
        FirewallService firewall,
        OptionalFeatureService features,
        IToastService toast)
    {
        _unattended = unattended;
        _tuning = tuning;
        _services = services;
        _tasks = tasks;
        _network = network;
        _power = power;
        _cleanup = cleanup;
        _drivers = drivers;
        _appx = appx;
        _firewall = firewall;
        _features = features;
        _toast = toast;
    }

    public event EventHandler<UnattendedJob>? JobPrepared;

    public ObservableCollection<ModeRow> TuningRows { get; } = new();
    public ObservableCollection<ModeRow> InstallRows { get; } = new();
    public ObservableCollection<ModeRow> CleanupRows { get; } = new();
    public ObservableCollection<RecordedStepRow> Recorded { get; } = new();

    public ObservableCollection<ModeRow> UpdateRows { get; } = new();
    public ObservableCollection<ModeRow> CtxRows { get; } = new();
    public ObservableCollection<ModeRow> DefenderRows { get; } = new();
    public ObservableCollection<ModeRow> FirewallProfileRows { get; } = new();

    // 大列表分页：默认 30 项，「加载更多」每次追加 30 项
    public PagedList<ModeRow> ServiceList { get; } = new(MatchesRow);
    public PagedList<ModeRow> TaskList { get; } = new(MatchesRow);
    public PagedList<ModeRow> AppxList { get; } = new(MatchesRow);
    public PagedList<ModeRow> FirewallRuleList { get; } = new(MatchesRow);
    public PagedList<ModeRow> DriverList { get; } = new(MatchesRow);
    public PagedList<ModeRow> FeatureList { get; } = new(MatchesRow);

    private readonly List<ModeRow> _featureRows = new();

    [ObservableProperty] private string _jobName = "我的开荒方案";
    [ObservableProperty] private string _jobDescription = string.Empty;
    [ObservableProperty] private string _statusText = "正在读取系统状态…";
    [ObservableProperty] private string _recordedSummary = "0 项";
    [ObservableProperty] private string _serviceSearch = string.Empty;
    [ObservableProperty] private string _taskSearch = string.Empty;
    [ObservableProperty] private string _appxSearch = string.Empty;
    [ObservableProperty] private string _adapterText = "（未读取）";
    [ObservableProperty] private string _dnsPresetText = "（未读取）";
    [ObservableProperty] private string _powerPlanText = "（未读取）";
    [ObservableProperty] private string _driverFolder = string.Empty;
    [ObservableProperty] private string _firewallRuleSearch = string.Empty;
    [ObservableProperty] private string _driverSearch = string.Empty;
    [ObservableProperty] private string _featureSearch = string.Empty;
    [ObservableProperty] private string _newRuleName = string.Empty;
    [ObservableProperty] private string _newRulePort = string.Empty;
    [ObservableProperty] private string _newRuleProgram = string.Empty;
    [ObservableProperty] private string _ruleDirectionText = "入站";
    [ObservableProperty] private string _ruleActionText = "允许";
    [ObservableProperty] private string _ruleProtocolText = "TCP";
    [ObservableProperty] private string _driverInstallPath = string.Empty;
    [ObservableProperty] private string _driverInstallText = "记录安装";
    [ObservableProperty] private string _dnsActionText = "记录 DNS";
    [ObservableProperty] private string _powerActionText = "记录电源方案";
    [ObservableProperty] private string _driverActionText = "记录导出";
    [ObservableProperty] private string _ipModeText = "自动（DHCP）";
    [ObservableProperty] private bool _ipStatic;
    [ObservableProperty] private string _ipAddress = string.Empty;
    [ObservableProperty] private string _ipPrefix = "24";
    [ObservableProperty] private string _ipGateway = string.Empty;
    [ObservableProperty] private string _ipActionText = "记录 IP";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _restartExplorer = true;

    public string DriverFolderDisplay => string.IsNullOrWhiteSpace(DriverFolder) ? "未选择目录" : DriverFolder;

    partial void OnDriverFolderChanged(string value) => OnPropertyChanged(nameof(DriverFolderDisplay));

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        IsBusy = true;

        try
        {
            BuildTuningRows();
            BuildUpdateRows();
            BuildCtxRows();
            BuildDefenderRows();
            BuildFirewallProfileRows();
            BuildCleanupRows();

            StatusText = "正在读取服务与计划任务…";
            var serviceList = _services.List();
            foreach (var service in serviceList)
            {
                var protectedText = UnattendedService.ProtectedServices.Contains(service.Name)
                    ? " · 受系统保护，脚本无法修改"
                    : string.Empty;

                var row = new ModeRow(
                    string.IsNullOrWhiteSpace(service.DisplayName) ? service.Name : service.DisplayName,
                    $"{service.Name} · 当前 {StartTypeText(service.StartType)}{protectedText}",
                    new[] { string.Empty, "auto", "manual", "disabled" },
                    new[] { "不修改", "自动", "手动", "禁用" },
                    value => new UnattendedStep
                    {
                        Kind = "service", Target = service.Name, Name = service.DisplayName, Value = value
                    });
                _serviceRows.Add(row);
            }

            var tasks = await _tasks.QueryLogonTasksAsync(CancellationToken.None);
            foreach (var task in tasks)
            {
                var row = new ModeRow(
                    task.Name,
                    $"{task.FullName} · 当前 {(task.IsEnabled ? "启用" : "禁用")}",
                    new[] { string.Empty, "enable", "disable" },
                    new[] { "不修改", "启用", "禁用" },
                    value => new UnattendedStep
                    {
                        Kind = "task", Target = task.FullName, Name = task.Name, Apply = value == "enable"
                    });
                _taskRows.Add(row);
            }

            StatusText = "正在检查原生应用安装状态…";
            HashSet<string> installed;
            try
            {
                installed = (await _appx.EnumerateAsync())
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn($"读取本机 Appx 状态失败，仍显示静态清单: {ex.Message}");
                installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var app in NativeAppCatalog.All
                         .OrderByDescending(app => app.RecommendedRemove)
                         .ThenBy(app => app.Category)
                         .ThenBy(app => app.DisplayName))
            {
                var isInstalled = app.StepKind == "onedrive"
                    ? OneDriveRemoveOperation.IsInstalled
                    : installed.Contains(app.Name);
                var state = isInstalled ? "已安装" : "未检测到";
                var kind = app.StepKind == "appx" && app.ForceRemove ? "appxforce" : app.StepKind;
                var actionLabel = kind switch
                {
                    "appxforce" => "强制卸载",
                    "defender-remove" => "彻底移除",
                    "onedrive" => "卸载",
                    _ => "移除"
                };

                var detail = kind == "defender-remove"
                    ? $"{app.Category} · {state} · 彻底移除 Defender 引擎（不可回滚，需重启）"
                    : $"{app.Category} · {state} · {app.Name}";

                var row = new ModeRow(
                    app.DisplayName,
                    detail,
                    new[] { string.Empty, "record" },
                    new[] { "不修改", actionLabel },
                    _ => new UnattendedStep
                    {
                        Kind = kind,
                        Target = app.Name,
                        Name = app.DisplayName
                    });
                _appxRows.Add(row);
            }

            var allAdapters = await _network.GetAdaptersAsync();
            _adapters = allAdapters
                .OrderByDescending(adapter => adapter.IsUp)
                .ThenBy(adapter => adapter.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _plans = (await _power.GetPlansWithBuiltInsAsync()).ToList();

            _adapterIndex = 0;
            _dnsPresetIndex = 0;
            _planIndex = Math.Max(0, _plans.FindIndex(plan => plan.IsActive));
            SyncDnsRows();
            SyncPowerRows();

            BuildInstallRows();

            ApplyServiceFilter();
            ApplyTaskFilter();
            ApplyAppxFilter();

            await RefreshFirewallRulesAsync();
            await RefreshDriversAsync();
            await RefreshFeaturesAsync();

            StatusText = "配置会实时记录到下方的「已记录」列表";
        }
        catch (Exception ex)
        {
            Log.Error("读取方案工作台数据失败", ex);
            StatusText = $"读取失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildTuningRows()
    {
        foreach (var option in _tuning.Options)
        {
            TuningRows.Add(new ModeRow(
                option.Title,
                $"{option.Category} · 当前 {_tuning.CurrentText(option)}",
                new[] { string.Empty, "apply", "restore" },
                new[] { "不修改", "应用", "还原" },
                value => new UnattendedStep
                {
                    Kind = "tuning", Target = option.Key, Name = option.Title, Apply = value == "apply"
                }));
        }
    }

    private void BuildUpdateRows()
    {
        UpdateRows.Add(new ModeRow(
            "Windows 更新",
            "恢复 / 暂停 / 超长延期 / 彻底关闭（服务 + 策略 + 任务）",
            new[] { string.Empty }.Concat(UpdateModes.Select(mode => mode.Value)).ToArray(),
            new[] { "不修改" }.Concat(UpdateModes.Select(mode => mode.Title)).ToArray(),
            value => new UnattendedStep
            {
                Kind = "update",
                Target = value,
                Name = UpdateModes.First(mode => mode.Value == value).Title
            }));
    }

    private void BuildCtxRows()
    {
        CtxRows.Add(new ModeRow(
            "右键菜单",
            "Win11 新版 / Win10 经典（切换后需重启资源管理器）",
            new[] { string.Empty, "classic", "win11" },
            new[] { "不修改", "Win10 经典", "Win11 新版" },
            value => new UnattendedStep { Kind = "ctxmenu", Apply = value == "classic", Name = "右键菜单风格" }));
    }

    private void BuildDefenderRows()
    {
        DefenderRows.Add(new ModeRow(
            "实时防护",
            "关闭后 Defender 不再实时扫描；受篡改保护限制，必要时需先手动关闭篡改保护",
            new[] { string.Empty, "realtime-on", "realtime-off" },
            new[] { "不修改", "开启", "关闭" },
            value => new UnattendedStep { Kind = "defender", Value = value, Name = "实时防护" }));

        DefenderRows.Add(new ModeRow(
            "云保护",
            "MAPS 云查询与样本上报",
            new[] { string.Empty, "cloud-on", "cloud-off" },
            new[] { "不修改", "开启", "关闭" },
            value => new UnattendedStep { Kind = "defender", Value = value, Name = "云保护" }));

        DefenderRows.Add(new ModeRow(
            "彻底移除 Defender 引擎",
            "按 defender-remover 13 移除引擎、安全中心、SmartScreen、VBS 等；不可回滚，必须重启",
            new[] { string.Empty, "remove" },
            new[] { "不修改", "彻底移除" },
            _ => new UnattendedStep { Kind = "defender-remove", Name = "彻底移除 Defender 引擎" }));
    }

    private void BuildFirewallProfileRows()
    {
        AddFirewallProfile("Domain", "域网络", "公司/域环境下的防火墙配置");
        AddFirewallProfile("Private", "专用网络", "家庭/可信网络");
        AddFirewallProfile("Public", "公用网络", "公共 Wi-Fi 等不可信网络");
    }

    private void AddFirewallProfile(string profile, string title, string detail)
    {
        FirewallProfileRows.Add(new ModeRow(
            title,
            detail,
            new[] { string.Empty, "on", "off" },
            new[] { "不修改", "开启", "关闭" },
            value => new UnattendedStep
            {
                Kind = "firewall-profile",
                Target = profile,
                Name = title,
                Apply = value == "on"
            }));
    }

    private async Task RefreshFirewallRulesAsync()
    {
        try
        {
            var rules = await _firewall.ListRulesAsync(FirewallRuleSearch).ConfigureAwait(true);
            _firewallRuleRows.Clear();
            foreach (var rule in rules)
            {
                var display = string.IsNullOrWhiteSpace(rule.DisplayName) ? rule.Name : rule.DisplayName;
                _firewallRuleRows.Add(new ModeRow(
                    display,
                    $"{rule.Summary} · {rule.Profile}",
                    new[] { string.Empty, "record" },
                    new[] { "不修改", "删除" },
                    _ => new UnattendedStep
                    {
                        Kind = "firewall-rule-remove",
                        Target = display,
                        Name = display
                    }));
            }

            ApplyFirewallRuleFilter();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取防火墙规则失败: {ex.Message}");
        }
    }

    private async Task RefreshDriversAsync()
    {
        try
        {
            var drivers = await _drivers.ListAsync().ConfigureAwait(true);
            _driverRows.Clear();
            foreach (var driver in drivers)
            {
                _driverRows.Add(new ModeRow(
                    driver.Name,
                    $"{driver.InfName} · {driver.Provider} · {driver.Version}",
                    new[] { string.Empty, "record" },
                    new[] { "不修改", "删除" },
                    _ => new UnattendedStep
                    {
                        Kind = "driver-remove",
                        Target = driver.InfName,
                        Name = driver.Name
                    }));
            }

            ApplyDriverFilter();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取驱动列表失败: {ex.Message}");
        }
    }

    private async Task RefreshFeaturesAsync()
    {
        try
        {
            var features = await _features.ListAsync().ConfigureAwait(true);
            _featureRows.Clear();
            foreach (var feature in features
                         .OrderByDescending(feature => feature.Enabled)
                         .ThenBy(feature => feature.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                var current = feature.Enabled ? "已启用" : "未启用";
                _featureRows.Add(new ModeRow(
                    feature.DisplayName,
                    $"{feature.Name} · 当前 {current} · 修改后可能需要重启",
                    new[] { string.Empty, "enable", "disable" },
                    new[] { "不修改", "启用", "禁用" },
                    value => new UnattendedStep
                    {
                        Kind = "feature",
                        Target = feature.Name,
                        Name = feature.DisplayName,
                        Apply = value == "enable"
                    }));
            }

            ApplyFeatureFilter();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 Windows 可选功能失败: {ex.Message}");
        }
    }

    private void BuildCleanupRows()
    {
        foreach (var category in _cleanup.GetCategories())
        {
            CleanupRows.Add(new ModeRow(
                category.Title,
                category.Description,
                new[] { string.Empty, "record" },
                new[] { "不修改", "清理" },
                _ => new UnattendedStep { Kind = "cleanup", Target = category.Key, Name = category.Title }));
        }
    }

    private void BuildInstallRows()
    {
        foreach (var software in SoftwareCatalog.All)
        {
            // 方案要跨机器复用：这里不判断本机是否已安装，保持全部可选
            InstallRows.Add(new ModeRow(
                software.Name,
                $"{software.Id} · {software.Description}",
                new[] { string.Empty, "record" },
                new[] { "不修改", "安装" },
                _ => new UnattendedStep
                {
                    Kind = "install", Target = software.Id, Name = software.Name, Note = software.Description
                }));
        }
    }

    private void SyncDnsRows()
    {
        AdapterText = _adapters.Count == 0
            ? "（未找到适配器）"
            : $"{_adapters[Math.Min(_adapterIndex, _adapters.Count - 1)].Name}（{_adapters[Math.Min(_adapterIndex, _adapters.Count - 1)].StatusText}）";
        DnsPresetText = NetworkService.Presets[Math.Min(_dnsPresetIndex, NetworkService.Presets.Count - 1)].Name;

        if (_adapters.Count > 0)
        {
            var adapter = _adapters[Math.Min(_adapterIndex, _adapters.Count - 1)];
            IpActionText = _recorded.ContainsKey($"net-ip|{adapter.Name}") ? "取消记录" : "记录 IP";
        }
    }

    private void SyncPowerRows()
    {
        PowerPlanText = _plans.Count == 0
            ? "（未找到电源计划）"
            : _plans[Math.Clamp(_planIndex, 0, _plans.Count - 1)].Name;
    }

    [RelayCommand]
    private void CycleAdapter()
    {
        if (_adapters.Count == 0) return;
        _adapterIndex = (_adapterIndex + 1) % _adapters.Count;
        SyncDnsRows();
    }

    [RelayCommand]
    private void CycleDnsPreset()
    {
        _dnsPresetIndex = (_dnsPresetIndex + 1) % NetworkService.Presets.Count;
        SyncDnsRows();
    }

    [RelayCommand]
    private void CyclePowerPlan()
    {
        if (_plans.Count == 0) return;
        _planIndex = (_planIndex + 1) % _plans.Count;
        SyncPowerRows();
    }

    [RelayCommand]
    private void ChooseDriverFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择驱动导出目录" };
        if (dialog.ShowDialog() != true) return;
        DriverFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void ToggleDns()
    {
        if (_adapters.Count == 0) return;
        var adapter = _adapters[_adapterIndex];
        var preset = NetworkService.Presets[_dnsPresetIndex];
        Toggle(new UnattendedStep
        {
            Kind = "dns",
            Target = adapter.Name,
            Name = preset.Name,
            Value = preset.Key,
            Note = adapter.Index.ToString()
        });
    }

    [RelayCommand]
    private void TogglePower()
    {
        if (_plans.Count == 0) return;
        var plan = _plans[_planIndex];
        Toggle(new UnattendedStep { Kind = "power", Target = plan.Guid, Name = plan.Name });
    }

    [RelayCommand]
    private void CycleIpMode()
    {
        IpStatic = !IpStatic;
        IpModeText = IpStatic ? "静态" : "自动（DHCP）";
    }

    [RelayCommand]
    private void ToggleIp()
    {
        if (_adapters.Count == 0)
        {
            _toast.Show("未找到网络适配器", ToastKind.Warning);
            return;
        }

        var prefix = int.TryParse(IpPrefix, out var parsed) ? parsed : 24;
        if (IpStatic)
        {
            if (!global::System.Net.IPAddress.TryParse(IpAddress.Trim(), out _))
            {
                _toast.Show("静态 IP 地址格式无效", ToastKind.Warning);
                return;
            }

            if (prefix is < 1 or > 32)
            {
                _toast.Show("子网前缀需要在 1–32 之间", ToastKind.Warning);
                return;
            }
        }

        var adapter = _adapters[_adapterIndex];
        var config = new AdapterIpConfig
        {
            Dhcp = !IpStatic,
            Address = IpStatic ? IpAddress.Trim() : string.Empty,
            Prefix = prefix,
            Gateway = IpStatic ? IpGateway.Trim() : string.Empty
        };

        Toggle(new UnattendedStep
        {
            Kind = "net-ip",
            Target = adapter.Name,
            Name = config.ModeText,
            Value = System.Text.Json.JsonSerializer.Serialize(config, JsonConfig.Options),
            Note = adapter.Index.ToString()
        });
    }

    [RelayCommand]
    private void ToggleDriver()
    {
        if (string.IsNullOrWhiteSpace(DriverFolder))
        {
            _toast.Show("先选择驱动导出目录", ToastKind.Warning);
            return;
        }

        Toggle(new UnattendedStep { Kind = "driver", Target = DriverFolder });
    }

    [RelayCommand]
    private void CycleRuleDirection()
    {
        _ruleDirection = (_ruleDirection + 1) % 2;
        RuleDirectionText = _ruleDirection == 0 ? "入站" : "出站";
    }

    [RelayCommand]
    private void CycleRuleAction()
    {
        _ruleAction = (_ruleAction + 1) % 2;
        RuleActionText = _ruleAction == 0 ? "允许" : "阻止";
    }

    [RelayCommand]
    private void CycleRuleProtocol()
    {
        _ruleProtocol = (_ruleProtocol + 1) % 2;
        RuleProtocolText = _ruleProtocol == 0 ? "TCP" : "UDP";
    }

    [RelayCommand]
    private void AddFirewallRule()
    {
        var name = NewRuleName.Trim();
        if (name.Length == 0)
        {
            _toast.Show("请填写防火墙规则名称", ToastKind.Warning);
            return;
        }

        var spec = new FirewallRuleSpec
        {
            DisplayName = name,
            Direction = _ruleDirection == 0 ? "Inbound" : "Outbound",
            Action = _ruleAction == 0 ? "Allow" : "Block",
            Protocol = _ruleProtocol == 0 ? "TCP" : "UDP",
            LocalPort = NewRulePort.Trim(),
            Program = NewRuleProgram.Trim()
        };

        var step = new UnattendedStep
        {
            Kind = "firewall-rule",
            Target = name,
            Name = name,
            Value = System.Text.Json.JsonSerializer.Serialize(spec, JsonConfig.Options)
        };

        _recorded[KeyOf(step)] = step;
        RefreshRecorded();
        NewRuleName = string.Empty;
        NewRulePort = string.Empty;
        NewRuleProgram = string.Empty;
        _toast.Show($"已记录防火墙规则「{name}」", ToastKind.Success);
    }

    [RelayCommand]
    private async Task ReloadFirewallRulesAsync() => await RefreshFirewallRulesAsync();

    [RelayCommand]
    private void ChooseDriverFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择驱动 INF 文件",
            Filter = "驱动信息 (*.inf)|*.inf|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        DriverInstallPath = dialog.FileName;
    }

    [RelayCommand]
    private void ChooseDriverInstallFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择驱动目录（含 INF）" };
        if (dialog.ShowDialog() != true) return;
        DriverInstallPath = dialog.FolderName;
    }

    [RelayCommand]
    private void ToggleDriverInstall()
    {
        var path = DriverInstallPath.Trim();
        if (path.Length == 0)
        {
            _toast.Show("先选择驱动 INF 文件或目录", ToastKind.Warning);
            return;
        }

        Toggle(new UnattendedStep
        {
            Kind = "driver-install",
            Target = path,
            Name = Path.GetFileName(path.TrimEnd('\\'))
        });
    }

    [RelayCommand]
    private void Cycle(ModeRow? row)
    {
        if (row is null) return;

        var previousKey = row.LastKey;
        row.Cycle();
        var step = row.ToStep();

        if (previousKey is not null && _recorded.ContainsKey(previousKey))
        {
            _recorded.Remove(previousKey);
        }

        if (step is null)
        {
            row.LastKey = null;
            RefreshRecorded();
            return;
        }

        var key = KeyOf(step);
        _recorded[key] = step;
        row.LastKey = key;
        RefreshRecorded();
    }

    private void Toggle(UnattendedStep step)
    {
        var key = KeyOf(step);
        if (_recorded.ContainsKey(key)) _recorded.Remove(key);
        else _recorded[key] = step;
        RefreshRecorded();
    }

    private static string KeyOf(UnattendedStep step) => $"{step.Kind}|{step.Target}";

    private static UnattendedStep CloneStep(UnattendedStep step)
        => JsonSerializer.Deserialize<UnattendedStep>(
               JsonSerializer.Serialize(step, JsonConfig.Options), JsonConfig.Options)
           ?? throw new InvalidOperationException("方案步骤复制失败");

    private void RefreshRecorded()
    {
        Recorded.Clear();
        foreach (var step in UnattendedService.OrderSteps(_recorded.Values))
        {
            Recorded.Add(new RecordedStepRow(step));
        }

        RecordedSummary = $"{Recorded.Count} 项";

        if (_adapters.Count > 0)
        {
            DnsActionText = _recorded.ContainsKey($"dns|{_adapters[_adapterIndex].Name}") ? "取消记录" : "记录 DNS";
            IpActionText = _recorded.ContainsKey($"net-ip|{_adapters[_adapterIndex].Name}") ? "取消记录" : "记录 IP";
        }

        if (_plans.Count > 0)
        {
            PowerActionText = _recorded.ContainsKey($"power|{_plans[_planIndex].Guid}") ? "取消记录" : "记录电源方案";
        }

        DriverActionText = !string.IsNullOrWhiteSpace(DriverFolder)
                           && _recorded.ContainsKey($"driver|{DriverFolder}")
            ? "取消记录"
            : "记录导出";

        DriverInstallText = !string.IsNullOrWhiteSpace(DriverInstallPath)
                            && _recorded.ContainsKey($"driver-install|{DriverInstallPath}")
            ? "取消记录"
            : "记录安装";
    }

    [RelayCommand]
    private void RemoveRecorded(RecordedStepRow? row)
    {
        if (row is null) return;
        _recorded.Remove(KeyOf(row.Step));
        RefreshRecorded();
    }

    [RelayCommand]
    private void ClearRecorded()
    {
        _recorded.Clear();
        foreach (var row in TuningRows.Concat(UpdateRows).Concat(CtxRows).Concat(DefenderRows).Concat(CleanupRows)
                     .Concat(InstallRows).Concat(_serviceRows).Concat(_taskRows).Concat(_appxRows)
                     .Concat(FirewallProfileRows).Concat(_firewallRuleRows).Concat(_driverRows).Concat(_featureRows))
        {
            while (row.IsRecorded) row.Cycle();
            row.LastKey = null;
        }

        RefreshRecorded();
    }

    [RelayCommand]
    private void ImportJob()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入执行方案",
            Filter = "FreshStart 方案 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var job = _unattended.LoadFile(dialog.FileName);
            LoadJob(job);
            StatusText = $"已导入「{job.Name}」：{job.Steps.Count} 步，可在各分区查看并调整";
            _toast.Show("方案已导入工作台", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("导入方案失败", ex);
            _toast.Show($"导入失败：{ex.Message}", ToastKind.Error);
        }
    }

    private IEnumerable<ModeRow> AllModeRows()
        => TuningRows.Concat(UpdateRows).Concat(CtxRows).Concat(DefenderRows).Concat(CleanupRows)
            .Concat(InstallRows).Concat(_serviceRows).Concat(_taskRows).Concat(_appxRows)
            .Concat(FirewallProfileRows).Concat(_firewallRuleRows).Concat(_driverRows).Concat(_featureRows);

    public void LoadJob(UnattendedJob job)
    {
        job.IsBuiltIn = false;
        _recorded.Clear();
        foreach (var row in AllModeRows()) row.Reset();
        foreach (var step in job.Steps) _recorded[KeyOf(step)] = CloneStep(step);

        JobName = job.Name;
        JobDescription = job.Description;
        RestartExplorer = job.RestartExplorer;

        foreach (var step in job.Steps)
        {
            var key = KeyOf(step);
            foreach (var row in AllModeRows())
            {
                if (row.Select(key)) break;
            }
        }

        RefreshRecorded();
    }

    public UnattendedJob BuildJob(string keyPrefix = "execution")
    {
        if (_recorded.Count == 0)
        {
            throw new InvalidOperationException("还没有记录任何配置");
        }

        return new UnattendedJob
        {
            SchemaVersion = 3,
            Key = $"{keyPrefix}.{Guid.NewGuid():N}"[..Math.Min(32, keyPrefix.Length + 9)],
            Name = string.IsNullOrWhiteSpace(JobName) ? "未命名执行方案" : JobName.Trim(),
            Description = JobDescription.Trim(),
            ContinueOnError = true,
            RestartExplorer = RestartExplorer,
            Steps = UnattendedService.OrderSteps(_recorded.Values.Select(CloneStep))
        };
    }

    [RelayCommand]
    private void SendToExecution()
    {
        try
        {
            var job = BuildJob();
            JobPrepared?.Invoke(this, job);
            StatusText = $"已将 {job.Steps.Count} 步发送到执行中心，请查看计划后执行";
            _toast.Show("方案已发送到执行中心", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, ToastKind.Warning);
        }
    }

    [RelayCommand]
    private void Export()
    {
        if (_recorded.Count == 0)
        {
            _toast.Show("还没有记录任何配置", ToastKind.Warning);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出执行方案",
            Filter = "FreshStart 方案 (*.json)|*.json",
            FileName = string.IsNullOrWhiteSpace(JobName) ? "freshstart-script.json" : $"{JobName}.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var job = BuildJob("workbench");
            job.Key = "workbench." + Path.GetFileNameWithoutExtension(dialog.FileName);
            _unattended.Export(job, dialog.FileName);
            StatusText = $"已导出：{dialog.FileName}（{job.Steps.Count} 步，已按依赖顺序排列）";
            _toast.Show("方案已导出，可导入执行中心或通过 --execute 运行", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("导出方案失败", ex);
            _toast.Show($"导出失败：{ex.Message}", ToastKind.Error);
        }
    }

    // ---------------- 搜索过滤 ----------------

    partial void OnServiceSearchChanged(string value) => ApplyServiceFilter();
    partial void OnTaskSearchChanged(string value) => ApplyTaskFilter();
    partial void OnAppxSearchChanged(string value) => ApplyAppxFilter();
    partial void OnFirewallRuleSearchChanged(string value) => ApplyFirewallRuleFilter();
    partial void OnDriverSearchChanged(string value) => ApplyDriverFilter();
    partial void OnFeatureSearchChanged(string value) => ApplyFeatureFilter();

    private void ApplyFirewallRuleFilter()
        => FirewallRuleList.Reset(_firewallRuleRows, FirewallRuleSearch);

    private void ApplyDriverFilter()
        => DriverList.Reset(_driverRows, DriverSearch);

    private void ApplyFeatureFilter()
        => FeatureList.Reset(_featureRows, FeatureSearch);

    private void ApplyServiceFilter()
        => ServiceList.Reset(_serviceRows, ServiceSearch);

    private void ApplyTaskFilter()
        => TaskList.Reset(_taskRows, TaskSearch);

    private void ApplyAppxFilter()
        => AppxList.Reset(_appxRows, AppxSearch);

    private static bool MatchesRow(ModeRow row, string keyword)
        => string.IsNullOrWhiteSpace(keyword)
           || row.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || row.Detail.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private static string StartTypeText(int startType) => startType switch
    {
        0 => "引导",
        1 => "系统",
        2 => "自动",
        3 => "手动",
        4 => "禁用",
        _ => "未知"
    };
}
