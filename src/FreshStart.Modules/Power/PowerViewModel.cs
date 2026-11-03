using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Power;
using FreshStart.Services.Tuning;

namespace FreshStart.Modules.Power;

public sealed partial class PowerViewModel : ObservableObject
{
    private readonly PowerService _service;
    private readonly TuningService _tuning;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private TuningOption? _fastStartupOption;
    private bool _originalFastStartup;

    public PowerViewModel(PowerService service, TuningService tuning, OperationRunner runner, IToastService toast)
    {
        _service = service;
        _tuning = tuning;
        _runner = runner;
        _toast = toast;
        _fastStartupOption = tuning.Find("faststartup");
    }

    public ObservableCollection<PowerPlanInfo> Plans { get; } = new();

    [ObservableProperty]
    private PowerPlanInfo? _selectedPlan;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取电源计划…";

    [ObservableProperty]
    private bool _fastStartupEnabled;

    [ObservableProperty]
    private bool _fastStartupDirty;

    partial void OnSelectedPlanChanged(PowerPlanInfo? value)
    {
        if (value is not null) StatusText = $"已选择「{value.Name}」，点击“应用计划”切换";
    }

    partial void OnFastStartupEnabledChanged(bool value)
    {
        FastStartupDirty = _fastStartupOption is not null && value != _originalFastStartup;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var plans = await _service.GetPlansWithBuiltInsAsync().ConfigureAwait(true);
            Plans.Clear();
            foreach (var plan in plans) Plans.Add(plan);

            SelectedPlan = Plans.FirstOrDefault(plan => plan.IsActive) ?? Plans.FirstOrDefault();
            StatusText = Plans.Count == 0
                ? "未读取到电源计划（可尝试以管理员身份运行）"
                : "切换电源计划会影响 CPU 调度与睡眠策略；带「内置」标记的方案会在应用时自动创建";

            if (_fastStartupOption is not null)
            {
                _originalFastStartup = await Task.Run(() => _tuning.IsApplied(_fastStartupOption)).ConfigureAwait(true);
                FastStartupEnabled = _originalFastStartup;
                FastStartupDirty = false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取电源设置失败", ex);
            StatusText = "读取电源设置失败，请查看日志";
            _toast.Show("读取电源设置失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyPlanAsync()
    {
        if (IsBusy || SelectedPlan is null || SelectedPlan.IsActive) return;
        IsBusy = true;
        try
        {
            var operation = new PowerPlanOperation(_service, SelectedPlan.Guid, SelectedPlan.Name);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"已切换到「{SelectedPlan.Name}」", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换电源计划失败", ex);
            _toast.Show($"切换失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyFastStartupAsync()
    {
        if (IsBusy || _fastStartupOption is null || !FastStartupDirty) return;
        IsBusy = true;
        try
        {
            var operation = _tuning.CreateBatch(
                "power.faststartup",
                FastStartupEnabled ? "开启快速启动" : "关闭快速启动",
                "写入 HiberbootEnabled；写入前备份，失败自动回滚",
                RiskLevel.Caution,
                new[] { new TuningChange(_fastStartupOption, FastStartupEnabled) });

            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show(FastStartupEnabled ? "已开启快速启动（重启生效）" : "已关闭快速启动（重启生效）", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("修改快速启动失败", ex);
            _toast.Show($"修改失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenPowerOptions()
    {
        try
        {
            Process.Start(new ProcessStartInfo("control.exe", "powercfg.cpl") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开电源选项失败: {ex.Message}");
            _toast.Show("无法打开电源选项", ToastKind.Warning);
        }
    }
}
