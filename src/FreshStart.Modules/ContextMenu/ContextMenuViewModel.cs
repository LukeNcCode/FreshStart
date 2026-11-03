using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.ContextMenu;
using FreshStart.Services.System;

namespace FreshStart.Modules.ContextMenu;

/// <summary>
/// 右键菜单页。枚举在后台线程完成；所有写操作交给 OperationRunner，保留备份与回滚。
/// </summary>
public sealed partial class ContextMenuViewModel : ObservableObject
{
    private readonly ContextMenuService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private readonly QuickActionService _quickActions;
    private CancellationTokenSource? _cts;

    public ContextMenuViewModel(
        ContextMenuService service,
        OperationRunner runner,
        IToastService toast,
        IUserPromptService prompts,
        QuickActionService quickActions)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
        _quickActions = quickActions;
        _styleStatus = "正在读取…";
    }

    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isClassicStyle;

    [ObservableProperty]
    private string _styleStatus = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private IReadOnlyList<ContextMenuEntry> _shellVerbs = Array.Empty<ContextMenuEntry>();

    [ObservableProperty]
    private IReadOnlyList<ContextMenuEntry> _shellExtensions = Array.Empty<ContextMenuEntry>();

    [ObservableProperty]
    private IReadOnlyList<ContextMenuEntry> _sendToItems = Array.Empty<ContextMenuEntry>();

    [ObservableProperty]
    private IReadOnlyList<ContextMenuEntry> _newMenuItems = Array.Empty<ContextMenuEntry>();

    [ObservableProperty]
    private IReadOnlyList<QuarantineEntry> _trashItems = Array.Empty<QuarantineEntry>();

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
        var token = _cts.Token;
        IsLoading = true;

        try
        {
            var result = await Task.Run(() => (_service.Load(), _service.LoadTrash()), token).ConfigureAwait(true);
            var snapshot = result.Item1;
            var trash = result.Item2;

            IsClassicStyle = snapshot.IsClassicStyle;
            StyleStatus = snapshot.IsClassicStyle ? "Win10 经典菜单（当前生效）" : "Win11 新版菜单（当前生效）";
            ShellVerbs = snapshot.ShellVerbs;
            ShellExtensions = snapshot.ShellExtensions;
            SendToItems = snapshot.SendToItems;
            NewMenuItems = snapshot.NewMenuItems;
            TrashItems = trash;
            SummaryText = $"经典 {snapshot.ShellVerbs.Count} · 扩展 {snapshot.ShellExtensions.Count} · 发送到 {snapshot.SendToItems.Count} · 新建 {snapshot.NewMenuItems.Count} · 回收站 {trash.Count}";
            HasLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // 页面离开
        }
        catch (Exception ex)
        {
            Log.Error("右键菜单加载失败", ex);
            _toast.Show("读取右键菜单失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ToggleVerbAsync(ContextMenuEntry? entry)
    {
        if (entry is null) return;
        var enable = !entry.IsEnabled;
        var operation = _service.CreateVerbToggleOperation(entry, enable);
        var ok = await RunOperationAsync(operation, $"已{(enable ? "启用" : "禁用")}「{entry.DisplayName}」");
        if (ok) await ReloadAsync();
    }

    [RelayCommand]
    private async Task ToggleHandlerAsync(ContextMenuEntry? entry)
    {
        if (entry is null) return;
        var enable = !entry.IsEnabled;
        try
        {
            var operation = _service.CreateHandlerToggleOperation(entry, enable);
            var ok = await RunOperationAsync(operation, $"已{(enable ? "启用" : "禁用")}扩展「{entry.DisplayName}」");
            if (ok) await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("切换 Shell 扩展失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ContextMenuEntry? entry)
    {
        if (entry is null || !entry.CanDelete) return;

        var (operation, quarantine) = _service.CreateDeleteOperation(entry);
        var execution = await _runner.ExecuteAsync(operation);
        if (execution.Success)
        {
            _toast.Show($"已移除「{entry.DisplayName}」，可在回收站恢复", ToastKind.Success);
            await ReloadAsync();
            return;
        }

        // 操作未完成：清理预创建的隔离索引，避免留下空记录。
        // 如果回滚也失败，保留索引，用户仍可从隔离区找回数据。
        if (!execution.RollbackFailed) _service.RemoveQuarantineIndex(quarantine);
        if (!execution.Cancelled)
        {
            _toast.Show(execution.Result.Message, ToastKind.Error);
        }
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RestoreAsync(QuarantineEntry? entry)
    {
        if (entry is null) return;
        try
        {
            var operation = _service.CreateRestoreOperation(entry);
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _service.RemoveQuarantineIndex(entry);
                _toast.Show($"已恢复「{entry.DisplayName}」", ToastKind.Success);
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("恢复菜单项失败", ex);
            _toast.Show($"恢复失败：{ex.Message}", ToastKind.Error);
        }
    }

    [RelayCommand]
    private Task SwitchToClassicAsync() => SwitchStyleAsync(classic: true);

    [RelayCommand]
    private Task SwitchToWin11Async() => SwitchStyleAsync(classic: false);

    private async Task SwitchStyleAsync(bool classic)
    {
        if (IsClassicStyle == classic) return;

        var operation = _service.CreateStyleOperation(classic);
        var execution = await _runner.ExecuteAsync(operation);
        if (!execution.Success)
        {
            if (!execution.Cancelled) _toast.Show(execution.Result.Message, ToastKind.Error);
            return;
        }

        IsClassicStyle = _service.IsClassicStyle();
        StyleStatus = IsClassicStyle ? "Win10 经典菜单（当前生效）" : "Win11 新版菜单（当前生效）";
        _toast.Show("右键菜单风格已切换", ToastKind.Success);

        var restart = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = "重启资源管理器？",
            Message = "菜单风格需要重启资源管理器后生效。",
            Details = new[] { "任务栏和桌面会短暂消失约 1–3 秒", "不会影响其他正在运行的应用程序" },
            ConfirmText = "立即重启",
            CancelText = "稍后手动"
        });
        if (restart)
        {
            try
            {
                var action = QuickActionCatalog.All.First(a => a.Kind == QuickActionKind.RestartExplorer);
                await _quickActions.ExecuteAsync(action);
                _toast.Show("资源管理器已重启", ToastKind.Success);
            }
            catch (Exception ex)
            {
                Log.Error("重启资源管理器失败", ex);
                _toast.Show("重启资源管理器失败，请手动重启", ToastKind.Error);
            }
        }

        await ReloadAsync();
    }

    private async Task<bool> RunOperationAsync(ISystemOperation operation, string successMessage)
    {
        try
        {
            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show(successMessage, ToastKind.Success);
                return true;
            }
            if (!execution.Cancelled) _toast.Show(execution.Result.Message, ToastKind.Error);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("右键菜单操作失败", ex);
            _toast.Show($"操作失败：{ex.Message}", ToastKind.Error);
            return false;
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }
}
