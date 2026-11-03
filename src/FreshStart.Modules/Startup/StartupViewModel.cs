using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Startup;

namespace FreshStart.Modules.Startup;

public sealed class StartupFilter
{
    public StartupFilter(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }
}

public sealed partial class StartupViewModel : ObservableObject
{
    private readonly StartupService _service;
    private readonly Core.Operations.OperationRunner _runner;
    private readonly IToastService _toast;
    private List<StartupEntry> _all = new();
    private CancellationTokenSource? _cts;

    public StartupViewModel(
        StartupService service,
        Core.Operations.OperationRunner runner,
        IToastService toast)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        Filters = new List<StartupFilter>
        {
            new StartupFilter("all", "全部"),
            new StartupFilter("run", "注册表"),
            new StartupFilter("folder", "启动文件夹"),
            new StartupFilter("task", "计划任务"),
            new StartupFilter("service", "服务")
        };
        SelectedFilter = Filters[0];
    }

    public IReadOnlyList<StartupFilter> Filters { get; }
    public ObservableCollection<StartupEntry> VisibleEntries { get; } = new();

    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    private StartupFilter? _selectedFilter;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _totalText = "—";

    [ObservableProperty]
    private string _enabledText = "—";

    [ObservableProperty]
    private string _highImpactText = "—";

    [ObservableProperty]
    private string _taskText = "—";

    [ObservableProperty]
    private string _statusText = "正在枚举启动项…";

    partial void OnSelectedFilterChanged(StartupFilter? value) => ApplyFilter();

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
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsLoading = true;
        StatusText = "正在枚举启动项（计划任务扫描可能需要 1–2 秒）…";

        try
        {
            var snapshot = await Task.Run(() => _service.LoadAsync(_cts.Token), _cts.Token).ConfigureAwait(true);
            _all = snapshot.Entries;
            TotalText = _all.Count.ToString();
            EnabledText = _all.Count(e => e.IsEnabled).ToString();
            HighImpactText = _all.Count(e => e.ImpactRank >= 3).ToString();
            TaskText = _all.Count(e => e.Kind == StartupKind.ScheduledTask).ToString();
            StatusText = $"扫描于 {snapshot.ScannedAt:HH:mm:ss} · Microsoft 系统计划任务与系统服务已隐藏";
            HasLoaded = true;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // 页面离开
        }
        catch (Exception ex)
        {
            Log.Error("启动项加载失败", ex);
            StatusText = "读取启动项失败，请查看日志";
            _toast.Show("读取启动项失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        VisibleEntries.Clear();
        var key = SelectedFilter?.Key ?? "all";
        foreach (var entry in _all.Where(entry => key switch
                 {
                     "run" => entry.Kind == StartupKind.RegistryRun,
                     "folder" => entry.Kind == StartupKind.StartupFolder,
                     "task" => entry.Kind == StartupKind.ScheduledTask,
                     "service" => entry.Kind == StartupKind.Service,
                     _ => true
                 }))
        {
            VisibleEntries.Add(entry);
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(StartupEntry? entry)
    {
        if (entry is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var operation = _service.CreateToggleOperation(entry);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                var nowEnabled = !entry.IsEnabled;
                _service.MarkServiceEnabled(entry, nowEnabled);
                _toast.Show($"已{(nowEnabled ? "启用" : "禁用")}「{entry.Name}」", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换启动项失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleDelayAsync(StartupEntry? entry)
    {
        if (entry is null || IsBusy || !entry.CanDelay) return;
        IsBusy = true;
        try
        {
            var operation = _service.CreateDelayOperation(entry);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show(entry.IsDelayed ? "已取消延迟启动" : "已设置为延迟启动", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换延迟启动失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }
}
