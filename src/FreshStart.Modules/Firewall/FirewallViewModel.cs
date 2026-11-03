using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Modules.Controls;
using FreshStart.Services.Firewall;
using FreshStart.Services.Unattended;

namespace FreshStart.Modules.Firewall;

public sealed partial class FirewallViewModel : ObservableObject
{
    private readonly FirewallService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;

    private (bool Domain, bool Private, bool Public) _loaded;
    private int _direction;
    private int _action;
    private int _protocol;

    public FirewallViewModel(
        FirewallService service,
        OperationRunner runner,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
    }

    public PagedList<FirewallRuleInfo> Rules { get; } = new();

    [ObservableProperty] private bool _domainEnabled;
    [ObservableProperty] private bool _privateEnabled;
    [ObservableProperty] private bool _publicEnabled;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "正在读取防火墙状态…";
    [ObservableProperty] private string _ruleSearch = string.Empty;
    [ObservableProperty] private string _newRuleName = string.Empty;
    [ObservableProperty] private string _newRulePort = string.Empty;
    [ObservableProperty] private string _newRuleProgram = string.Empty;
    [ObservableProperty] private string _directionText = "入站";
    [ObservableProperty] private string _actionText = "允许";
    [ObservableProperty] private string _protocolText = "TCP";

    [RelayCommand]
    private async Task LoadAsync()
    {
        await ReloadProfilesAsync();
        await RefreshRulesAsync();
    }

    private async Task ReloadProfilesAsync()
    {
        try
        {
            _loaded = await _service.GetProfilesAsync().ConfigureAwait(true);
            DomainEnabled = _loaded.Domain;
            PrivateEnabled = _loaded.Private;
            PublicEnabled = _loaded.Public;
            StatusText = "已读取三个防火墙配置文件状态";
        }
        catch (Exception ex)
        {
            Log.Error("读取防火墙状态失败", ex);
            StatusText = "读取防火墙状态失败，请查看日志";
        }
    }

    [RelayCommand]
    private async Task ApplyProfilesAsync()
    {
        if (IsBusy) return;

        var changes = new List<ISystemOperation>();
        if (DomainEnabled != _loaded.Domain)
            changes.Add(new FirewallProfileOperation(_service, "Domain", DomainEnabled, _loaded));
        if (PrivateEnabled != _loaded.Private)
            changes.Add(new FirewallProfileOperation(_service, "Private", PrivateEnabled, _loaded));
        if (PublicEnabled != _loaded.Public)
            changes.Add(new FirewallProfileOperation(_service, "Public", PublicEnabled, _loaded));

        if (changes.Count == 0)
        {
            _toast.Show("防火墙配置没有变化", ToastKind.Info);
            return;
        }

        IsBusy = true;
        try
        {
            var operation = changes.Count == 1
                ? changes[0]
                : new CompositeOperation("firewall.profiles", "修改防火墙配置", $"{changes.Count} 个配置文件", RiskLevel.Caution, changes);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show("防火墙配置已更新", ToastKind.Success);
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }

            await ReloadProfilesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshRulesAsync()
    {
        if (IsBusy) return;
        try
        {
            var rules = await _service.ListRulesAsync(RuleSearch).ConfigureAwait(true);
            Rules.Reset(rules);
            StatusText = $"共 {Rules.Total} 条规则";
        }
        catch (Exception ex)
        {
            Log.Error("读取防火墙规则失败", ex);
            StatusText = "读取防火墙规则失败，请查看日志";
        }
    }

    [RelayCommand]
    private void CycleDirection()
    {
        _direction = (_direction + 1) % 2;
        DirectionText = _direction == 0 ? "入站" : "出站";
    }

    [RelayCommand]
    private void CycleAction()
    {
        _action = (_action + 1) % 2;
        ActionText = _action == 0 ? "允许" : "阻止";
    }

    [RelayCommand]
    private void CycleProtocol()
    {
        _protocol = (_protocol + 1) % 2;
        ProtocolText = _protocol == 0 ? "TCP" : "UDP";
    }

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        var name = NewRuleName.Trim();
        if (name.Length == 0)
        {
            _toast.Show("请填写规则名称", ToastKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var spec = new FirewallRuleSpec
            {
                DisplayName = name,
                Direction = _direction == 0 ? "Inbound" : "Outbound",
                Action = _action == 0 ? "Allow" : "Block",
                Protocol = _protocol == 0 ? "TCP" : "UDP",
                LocalPort = NewRulePort.Trim(),
                Program = NewRuleProgram.Trim()
            };

            var execution = await _runner.ExecuteAsync(new FirewallRuleAddOperation(_service, spec));
            if (execution.Success)
            {
                _toast.Show($"已创建规则「{name}」", ToastKind.Success);
                NewRuleName = string.Empty;
                NewRulePort = string.Empty;
                NewRuleProgram = string.Empty;
                await RefreshRulesAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(FirewallRuleInfo? rule)
    {
        if (rule is null || IsBusy) return;

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"删除防火墙规则「{rule.DisplayName}」？",
            Message = "删除前会读取规则定义，撤销时可重建。",
            Details = new[] { rule.Summary, $"配置文件：{rule.Profile}" },
            ConfirmText = "删除",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(new FirewallRuleRemoveOperation(_service, rule.DisplayName));
            if (execution.Success)
            {
                _toast.Show("规则已删除", ToastKind.Success);
                await RefreshRulesAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
