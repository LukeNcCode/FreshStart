using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Modules.Firewall;
using FreshStart.Services.Appx;
using FreshStart.Services.Defender;
using FreshStart.Services.System;

namespace FreshStart.Modules.Defender;

public sealed partial class DefenderViewModel : ObservableObject
{
    private readonly DefenderService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly AppxService _appx;
    private readonly bool _isElevated = ElevationService.IsAdministrator;
    private bool _suppress;

    public DefenderViewModel(
        DefenderService service,
        OperationRunner runner,
        IToastService toast,
        AppxService appx,
        FirewallViewModel firewall)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        _appx = appx;
        Firewall = firewall;
    }

    public FirewallViewModel Firewall { get; }

    [ObservableProperty]
    private bool _showDefender = true;

    [ObservableProperty]
    private bool _showFirewall;

    partial void OnShowDefenderChanged(bool value)
    {
        if (value) ShowFirewall = false;
    }

    partial void OnShowFirewallChanged(bool value)
    {
        if (value) ShowDefender = false;
    }

    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _tamperProtected;

    [ObservableProperty]
    private bool _antivirusEnabled;

    [ObservableProperty]
    private bool _securityCenterInstalled = true;

    [ObservableProperty]
    private bool _engineAvailable = true;

    [ObservableProperty]
    private bool _acceptRemovalRisk;

    [ObservableProperty]
    private string _removalStatusText = string.Empty;

    [ObservableProperty]
    private double _removalProgress;

    [ObservableProperty]
    private bool _removalDone;

    [ObservableProperty]
    private bool _trayAutostart;

    [ObservableProperty]
    private bool _trayRunning;

    public bool SecurityCenterRemoved => !SecurityCenterInstalled;
    public bool ShowTrayCleanup => SecurityCenterRemoved && (TrayAutostart || TrayRunning);
    public bool IsElevated => _isElevated;
    public bool CanRemoveEngine => _isElevated && EngineAvailable && AcceptRemovalRisk && !IsBusy && !RemovalDone;
    public bool ShowRemovalForm => EngineAvailable;
    public bool ShowRemovalResult => RemovalDone || RemovalStatusText.Length > 0;

    public string TrayStateText => !SecurityCenterRemoved
        ? string.Empty
        : TrayAutostart || TrayRunning
            ? $"托盘图标状态：{(TrayRunning ? "进程运行中" : "进程未运行")}{(TrayAutostart ? "，仍有登录自启动" : "，无自启动")}"
            : "托盘图标：已清理";

    partial void OnSecurityCenterInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(SecurityCenterRemoved));
        OnPropertyChanged(nameof(ShowTrayCleanup));
        OnPropertyChanged(nameof(TrayStateText));
    }

    partial void OnTrayAutostartChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTrayCleanup));
        OnPropertyChanged(nameof(TrayStateText));
    }

    partial void OnTrayRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowTrayCleanup));
        OnPropertyChanged(nameof(TrayStateText));
    }

    partial void OnEngineAvailableChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveEngine));
        OnPropertyChanged(nameof(ShowRemovalForm));
        OnPropertyChanged(nameof(CanModify));
    }

    partial void OnAcceptRemovalRiskChanged(bool value) => OnPropertyChanged(nameof(CanRemoveEngine));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanRemoveEngine));

    partial void OnRemovalDoneChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowRemovalResult));
        OnPropertyChanged(nameof(CanRemoveEngine));
    }

    partial void OnRemovalStatusTextChanged(string value) => OnPropertyChanged(nameof(ShowRemovalResult));

    [ObservableProperty]
    private string _statusText = "正在读取 Defender 状态…";

    [ObservableProperty]
    private string _newExclusion = string.Empty;

    [ObservableProperty]
    private bool _hasExclusions;

    [ObservableProperty]
    private ObservableCollection<DefenderExclusion> _exclusions = new();

    private bool _realTimeEnabled;
    public bool RealTimeEnabled
    {
        get => _realTimeEnabled;
        set
        {
            if (_suppress || value == _realTimeEnabled) return;
            _realTimeEnabled = value;
            OnPropertyChanged();
            _ = ChangeRealTimeAsync(value);
        }
    }

    private bool _cloudEnabled;
    public bool CloudEnabled
    {
        get => _cloudEnabled;
        set
        {
            if (_suppress || value == _cloudEnabled) return;
            _cloudEnabled = value;
            OnPropertyChanged();
            _ = ChangeCloudAsync(value);
        }
    }

    public bool CanModify => EngineAvailable && !TamperProtected && _isElevated;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (HasLoaded) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        IsLoading = true;
        try
        {
            var status = await _service.GetStatusAsync().ConfigureAwait(true);
            _suppress = true;
            _realTimeEnabled = status.RealTimeEnabled;
            _cloudEnabled = status.CloudEnabled;
            TamperProtected = status.TamperProtected;
            AntivirusEnabled = status.AntivirusEnabled;
            EngineAvailable = status.EngineAvailable;
            SecurityCenterInstalled = status.SecurityCenterInstalled;
            TrayAutostart = status.TrayAutostart;
            TrayRunning = status.TrayRunning;
            _suppress = false;
            OnPropertyChanged(nameof(RealTimeEnabled));
            OnPropertyChanged(nameof(CloudEnabled));
            OnPropertyChanged(nameof(CanModify));

            Exclusions = new ObservableCollection<DefenderExclusion>(status.Exclusions);
            HasExclusions = Exclusions.Count > 0;
            StatusText = !_isElevated
                ? "当前未以管理员身份运行，Defender 设置只读"
                : !status.EngineAvailable
                    ? "未检测到 Defender 引擎（可能已移除）"
                    : !status.SecurityCenterInstalled
                        ? "Windows 安全中心应用已移除 · Defender 引擎仍在后台运行（不绕过篡改保护）"
                        : status.TamperProtected
                            ? "篡改保护已开启：实时防护等设置可能被 Windows 拒绝修改"
                            : "Defender 由 Windows 安全中心管理";
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("读取 Defender 状态失败", ex);
            StatusText = "读取失败，请确认 Windows Defender 服务可用";
            _toast.Show("读取 Defender 状态失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            _suppress = false;
            IsLoading = false;
        }
    }

    private async Task ChangeRealTimeAsync(bool enable)
    {
        if (IsBusy) { ResetRealTime(!enable); return; }
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(_service.CreateRealTimeOperation(enable));
            if (execution.Success)
            {
                _toast.Show(enable ? "实时防护已开启" : "实时防护已关闭", ToastKind.Success);
                await ReloadAsync();
            }
            else
            {
                ResetRealTime(!enable);
                if (!execution.Cancelled) _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换实时防护失败", ex);
            ResetRealTime(!enable);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ChangeCloudAsync(bool enable)
    {
        if (IsBusy) { ResetCloud(!enable); return; }
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(_service.CreateCloudOperation(enable));
            if (execution.Success)
            {
                _toast.Show(enable ? "云保护已开启" : "云保护已关闭", ToastKind.Success);
                await ReloadAsync();
            }
            else
            {
                ResetCloud(!enable);
                if (!execution.Cancelled) _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换云保护失败", ex);
            ResetCloud(!enable);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetRealTime(bool value)
    {
        _suppress = true;
        _realTimeEnabled = value;
        _suppress = false;
        OnPropertyChanged(nameof(RealTimeEnabled));
    }

    private void ResetCloud(bool value)
    {
        _suppress = true;
        _cloudEnabled = value;
        _suppress = false;
        OnPropertyChanged(nameof(CloudEnabled));
    }

    [RelayCommand]
    private async Task AddExclusionAsync(string? kindText)
    {
        if (string.IsNullOrWhiteSpace(NewExclusion) || IsBusy) return;
        if (!Enum.TryParse<DefenderExclusionKind>(kindText, out var kind)) return;
        var value = NewExclusion.Trim();
        if (kind == DefenderExclusionKind.Extension && !value.StartsWith('.')) value = "." + value;

        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(
                _service.CreateExclusionOperation(new DefenderExclusion { Kind = kind, Value = value }, add: true));
            if (execution.Success)
            {
                NewExclusion = string.Empty;
                _toast.Show("已添加 Defender 排除项", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("添加 Defender 排除项失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveExclusionAsync(DefenderExclusion? exclusion)
    {
        if (exclusion is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(_service.CreateExclusionOperation(exclusion, add: false));
            if (execution.Success)
            {
                _toast.Show("已移除排除项", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("移除 Defender 排除项失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task TempDisableAsync()
    {
        if (IsBusy || !CanModify) return;
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(_service.CreateTimedDisableOperation(15));
            if (execution.Success)
            {
                _toast.Show("已临时关闭实时防护，15 分钟后自动恢复", ToastKind.Warning);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("临时关闭 Defender 失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveEngineAsync()
    {
        if (!CanRemoveEngine) return;

        IsBusy = true;
        RemovalDone = false;
        RemovalProgress = 0;
        RemovalStatusText = "正在准备移除 Defender…";
        try
        {
            var operation = _service.CreateRemovalOperation(_appx);
            var progress = new Progress<OperationStep>(step =>
            {
                RemovalStatusText = step.Message;
                if (step.Percent is not null) RemovalProgress = step.Percent.Value;
            });

            var execution = await _runner.ExecuteAsync(operation, progress);
            if (execution.Success)
            {
                RemovalDone = true;
                AcceptRemovalRisk = false;
                RemovalStatusText = execution.Result.Message;
                _toast.Show("Defender 移除已执行，请重启后确认", ToastKind.Success);
            }
            else if (!execution.Cancelled)
            {
                RemovalStatusText = execution.Result.Message;
                _toast.Show("Defender 移除未完全成功，请查看日志", ToastKind.Error);
            }

            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("彻底移除 Defender 失败", ex);
            RemovalStatusText = "执行失败：" + ex.Message;
            _toast.Show($"移除失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveTrayAsync()
    {
        if (IsBusy || !_isElevated) return;
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(_service.CreateSecurityCenterTrayOperation());
            if (execution.Success)
            {
                _toast.Show(execution.Result.Message, ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("移除安全中心托盘失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenSecurityCenter()
    {
        if (!SecurityCenterInstalled)
        {
            _toast.Show("Windows 安全中心应用未安装；Defender 设置可在此页继续管理", ToastKind.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:windowsdefender") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开 Windows 安全中心失败: {ex.Message}");
            _toast.Show("无法打开 Windows 安全中心", ToastKind.Error);
        }
    }
}
