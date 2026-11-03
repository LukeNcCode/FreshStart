using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Startup;

namespace FreshStart.Modules.ServiceTasks;

public sealed class ServiceRow
{
    public ServiceRow(ServiceState state) => State = state;

    public ServiceState State { get; }
    public string Name => State.Name;
    public string DisplayName => string.IsNullOrWhiteSpace(State.DisplayName) ? State.Name : State.DisplayName;
    public int StartType => State.StartType;
    public bool Delayed => State.DelayedAutoStart;

    public string StartModeText => State.StartType switch
    {
        2 => State.DelayedAutoStart ? "自动（延迟）" : "自动",
        4 => "禁用",
        _ => "手动"
    };

    public string Subtitle => $"{State.Name} · {StartModeText}";
}

public sealed partial class ServiceTasksViewModel : ObservableObject
{
    private readonly ServiceControlService _services;
    private readonly ScheduledTaskService _tasks;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private List<ServiceRow> _allServices = new();

    public ServiceTasksViewModel(
        ServiceControlService services,
        ScheduledTaskService tasks,
        OperationRunner runner,
        IToastService toast)
    {
        _services = services;
        _tasks = tasks;
        _runner = runner;
        _toast = toast;
    }

    public ObservableCollection<ServiceRow> Services { get; } = new();
    public ObservableCollection<ScheduledTaskInfo> Tasks { get; } = new();

    [ObservableProperty]
    private string _serviceSearch = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取服务与计划任务…";

    [ObservableProperty]
    private string _serviceCountText = "—";

    [ObservableProperty]
    private string _taskCountText = "—";

    partial void OnServiceSearchChanged(string value) => ApplyServiceFilter();

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
            var services = await Task.Run(() => _services.List()).ConfigureAwait(true);
            _allServices = services.Select(state => new ServiceRow(state)).ToList();
            ApplyServiceFilter();
            ServiceCountText = $"{_allServices.Count} 个服务";

            var tasks = await _tasks.QueryLogonTasksAsync(CancellationToken.None).ConfigureAwait(true);
            Tasks.Clear();
            foreach (var task in tasks) Tasks.Add(task);
            TaskCountText = $"{tasks.Count} 个登录/开机任务";

            StatusText = "修改服务启动类型需要管理员权限；禁用运行中的服务通常重启后完全生效";
        }
        catch (Exception ex)
        {
            Log.Error("读取服务与任务失败", ex);
            StatusText = "读取失败，请查看日志";
            _toast.Show("读取服务与任务失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyServiceFilter()
    {
        var search = ServiceSearch.Trim();
        var rows = string.IsNullOrEmpty(search)
            ? _allServices
            : _allServices.Where(row =>
                row.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || row.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        Services.Clear();
        foreach (var row in rows.Take(400)) Services.Add(row);
    }

    [RelayCommand]
    private Task SetAutoAsync(ServiceRow? row) => SetModeAsync(row, 2);

    [RelayCommand]
    private Task SetManualAsync(ServiceRow? row) => SetModeAsync(row, 3);

    [RelayCommand]
    private Task SetDisabledAsync(ServiceRow? row) => SetModeAsync(row, 4);

    private async Task SetModeAsync(ServiceRow? row, int targetStart)
    {
        if (row is null || IsBusy || row.StartType == targetStart) return;
        IsBusy = true;
        try
        {
            var risk = targetStart == 4 ? RiskLevel.High : RiskLevel.Caution;
            var operation = new ServiceStartModeOperation(_services, row.Name, targetStart, targetDelayed: false, risk);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"服务「{row.DisplayName}」启动类型已更新", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("修改服务启动类型失败", ex);
            _toast.Show($"修改失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(ScheduledTaskInfo? task)
    {
        if (task is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var enable = !task.IsEnabled;
            var operation = new ScheduledTaskToggleOperation(_tasks, task.Path, task.Name, enable);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show(enable ? "计划任务已启用" : "计划任务已禁用", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("切换计划任务失败", ex);
            _toast.Show($"切换失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
