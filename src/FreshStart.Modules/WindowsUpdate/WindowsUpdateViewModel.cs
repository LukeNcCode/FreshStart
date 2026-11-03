using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.WindowsUpdate;

namespace FreshStart.Modules.WindowsUpdate;

public sealed partial class WindowsUpdateViewModel : ObservableObject
{
    private readonly WindowsUpdateService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;

    public WindowsUpdateViewModel(WindowsUpdateService service, OperationRunner runner, IToastService toast)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
    }

    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _modeText = "—";

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _pauseText = "—";

    [ObservableProperty]
    private bool _isPolicyDisabled;

    [ObservableProperty]
    private IReadOnlyList<UpdateServiceState> _serviceStates = Array.Empty<UpdateServiceState>();

    [ObservableProperty]
    private bool _isLoadingHistory;

    [ObservableProperty]
    private bool _isHistoryLoaded;

    [ObservableProperty]
    private bool _hasHistory;

    [ObservableProperty]
    private ObservableCollection<UpdateHistoryItem> _historyItems = new();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (HasLoaded) return;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var status = await Task.Run(() => _service.GetStatusAsync()).ConfigureAwait(true);
            ModeText = status.ModeText;
            Description = status.Description;
            PauseText = status.PauseExpiryText;
            IsPolicyDisabled = status.PolicyDisabled;
            ServiceStates = status.Services;
            HasLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("读取 Windows 更新状态失败", ex);
            _toast.Show("读取更新状态失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task PauseAsync() => ExecuteAsync(WindowsUpdateMode.Paused, "已暂停 Windows 更新 7 天");

    [RelayCommand]
    private Task ExtendedPauseAsync() =>
        ExecuteAsync(WindowsUpdateMode.ExtendedPause, "已通过注册表超长延期（至 2099-12-31）");

    [RelayCommand]
    private Task DisableAsync() => ExecuteAsync(WindowsUpdateMode.Disabled, "Windows 更新已彻底关闭");

    [RelayCommand]
    private Task RestoreAsync() => ExecuteAsync(WindowsUpdateMode.Normal, "Windows 更新已恢复默认");

    private async Task ExecuteAsync(WindowsUpdateMode mode, string successMessage)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var operation = _service.CreateOperation(mode);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show(successMessage, ToastKind.Success);
                await RefreshAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("修改 Windows 更新状态失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        if (IsLoadingHistory) return;
        IsLoadingHistory = true;
        try
        {
            var items = await _service.GetHistoryAsync().ConfigureAwait(true);
            HistoryItems = new ObservableCollection<UpdateHistoryItem>(items);
            HasHistory = items.Count > 0;
            IsHistoryLoaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("读取更新历史失败", ex);
            _toast.Show("读取更新历史失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsLoadingHistory = false;
        }
    }
}
