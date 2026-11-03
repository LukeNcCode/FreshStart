using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Appx;
using FreshStart.Services.Defender;

namespace FreshStart.Modules.Appx;

public sealed partial class AppxPackageItem : ObservableObject
{
    public AppxPackageItem(AppxPackageInfo package) => Package = package;

    public AppxPackageInfo Package { get; }
    public string Name => Package.LocalizedName;
    public string Publisher => string.IsNullOrWhiteSpace(Package.Publisher) ? "未知发布者" : Package.Publisher;
    public bool HasStartEntry => Package.HasStartEntry;
    public string PackageName => Package.Name;
    public string VersionText => Package.VersionText;
    public bool IsSystem => Package.IsSystem;
    public bool IsProtected => Package.IsProtected;
    public bool CanRemove => Package.CanRemove;
    public bool IsSecurityCenter => Package.Name.Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase);
    public string ProtectedActionText => IsSecurityCenter ? "彻底移除 Defender" : "强制卸载";
    public string StateText => Package.IsProtected
        ? "系统组件"
        : Package.IsFramework
            ? "框架"
            : Package.IsResourcePackage ? "资源包"
            : !Package.HasStartEntry ? "内部组件"
            : Package.IsSystem ? "系统应用" : "用户应用";

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class AppxFilter
{
    public AppxFilter(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }
}

public sealed partial class AppxViewModel : ObservableObject
{
    private readonly AppxService _service;
    private readonly DefenderService _defender;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private readonly List<AppxPackageItem> _all = new();
    private CancellationTokenSource? _cts;
    private int _selectedCount;

    public AppxViewModel(
        AppxService service,
        DefenderService defender,
        OperationRunner runner,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _defender = defender;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
        Filters = new List<AppxFilter>
        {
            new("apps", "应用"),
            new("removable", "可卸载"),
            new("system", "系统应用"),
            new("internal", "内部组件")
        };
        _selectedFilter = Filters[0];
    }

    public IReadOnlyList<AppxFilter> Filters { get; }

    [ObservableProperty]
    private AppxFilter? _selectedFilter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _totalText = "—";

    [ObservableProperty]
    private string _systemText = "—";

    [ObservableProperty]
    private string _selectedText = "0";

    [ObservableProperty]
    private string _removedText = "0";

    [ObservableProperty]
    private bool _hasRemoved;

    [ObservableProperty]
    private IReadOnlyList<AppxPackageItem> _packages = Array.Empty<AppxPackageItem>();

    [ObservableProperty]
    private IReadOnlyList<RemovedAppxInfo> _removed = Array.Empty<RemovedAppxInfo>();

    partial void OnSelectedFilterChanged(AppxFilter? value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_all.Count > 0) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var packages = await _service.EnumerateAsync(_cts.Token).ConfigureAwait(true);
            foreach (var item in _all) item.PropertyChanged -= OnItemPropertyChanged;
            _all.Clear();
            foreach (var package in packages)
            {
                // GUID 命名的系统组件/框架包通常不可卸载且不可读，不当作可见应用
                if (Guid.TryParse(package.Name, out _)) continue;
                // 框架/资源包不是用户可见应用，默认隐藏，避免列表被 GUID 包名淹没
                if (package.IsFramework || package.IsResourcePackage) continue;
                var item = new AppxPackageItem(package);
                item.PropertyChanged += OnItemPropertyChanged;
                _all.Add(item);
            }

            Removed = _service.LoadRemoved();
            HasRemoved = Removed.Count > 0;
            RemovedText = Removed.Count.ToString();
            TotalText = _all.Count(item => item.HasStartEntry).ToString();
            SystemText = _all.Count(item => item.IsSystem && item.HasStartEntry).ToString();
            SelectedText = "0";
            _selectedCount = 0;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            // 页面离开
        }
        catch (Exception ex)
        {
            Log.Error("枚举 Appx 失败", ex);
            _toast.Show("读取 Appx 列表失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppxPackageItem.IsSelected)) return;
        _selectedCount = _all.Count(item => item.IsSelected);
        SelectedText = _selectedCount.ToString();
    }

    private void ApplyFilter()
    {
        var key = SelectedFilter?.Key ?? "apps";
        var search = SearchText.Trim();
        Packages = _all
            .Where(item => key switch
            {
                "removable" => item.HasStartEntry && item.CanRemove && !item.IsSystem,
                "system" => item.HasStartEntry && item.IsSystem,
                "internal" => !item.HasStartEntry,
                _ => item.HasStartEntry
            })
            .Where(item => string.IsNullOrEmpty(search)
                           || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                           || item.PackageName.Contains(search, StringComparison.OrdinalIgnoreCase)
                           || item.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    [RelayCommand]
    private async Task UninstallSelectedAsync()
    {
        if (IsBusy) return;
        var selected = _all.Where(item => item.IsSelected && item.CanRemove).ToList();
        if (selected.Count == 0)
        {
            _toast.Show("请先勾选要卸载的应用", ToastKind.Warning);
            return;
        }

        var system = selected.Where(item => item.IsSystem).ToList();
        if (system.Count > 0)
        {
            var confirmed = await _prompts.ConfirmAsync(new UserPrompt
            {
                Title = "卸载系统应用？",
                Message = $"选中的 {selected.Count} 个应用中有 {system.Count} 个系统应用。",
                Details = system.Select(item => $"• {item.Name}").Concat(new[]
                {
                    "卸载 Store、照片、计算器等系统应用可能导致开始菜单、设置或系统功能异常",
                    "可以通过“已卸载应用”尝试重新注册，但不保证成功",
                    "建议一次只卸载一个系统应用并先创建还原点"
                }).ToList(),
                ConfirmText = "继续卸载",
                CancelText = "取消",
                IsDangerous = true
            });
            if (!confirmed) return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = "准备卸载…";
        try
        {
            var progress = new Progress<OperationStep>(step =>
            {
                ProgressText = step.Message;
                if (step.Percent is not null) ProgressPercent = step.Percent.Value;
            });
            var operation = new AppxRemoveBatchOperation(
                _service,
                selected.Select(item => item.Package).ToList(),
                system.Count > 0 ? RiskLevel.High : RiskLevel.Caution);
            var execution = await _runner.ExecuteAsync(operation, progress, _cts.Token);
            if (execution.Success)
            {
                _service.AddRemoved(selected.Select(item => item.Package));
                _toast.Show($"已卸载 {selected.Count} 个应用", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _toast.Show("已取消卸载", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("卸载 Appx 失败", ex);
            _toast.Show($"卸载失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private void CancelUninstall()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task ForceRemoveAsync(AppxPackageItem? item)
    {
        if (item is null || IsBusy || !item.IsProtected) return;

        var isDefender = item.IsSecurityCenter;
        var details = isDefender
            ? new List<string>
            {
                "将执行 defender-remover 13 的完整移除：Defender 服务/驱动、安全中心、SmartScreen、WebThreat、VBS/凭据保护、驱动阻止列表、系统缓解、计划任务、启动项、右键菜单、策略和安装目录",
                "不可自动回滚；建议先创建系统还原点",
                "会降低系统安全防护，可能导致 WSL2 / Hyper-V / Windows 沙盒不可用、Windows 更新或商店组件需要修复",
                "执行后必须重启；24H2 上仍可能有 MsMpEng / MpDefenderCoreService 残留",
                "仅在确认知道风险时继续"
            }
            : new List<string>
            {
                "官方不支持此操作，强制删除可能破坏安全中心、商店或系统更新",
                "不可自动回滚，Windows 更新可能重新安装该组件",
                "需要先创建系统还原点；失败可能留下半删除状态",
                "仅在确认知道风险时继续"
            };

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = isDefender
                ? "彻底移除 Defender 引擎与 Windows 安全中心？"
                : $"强制卸载系统组件「{item.Name}」？",
            Message = isDefender
                ? "这不是只卸载安全中心应用，而是按 windows-defender-remover 的范围移除 Defender 引擎。"
                : "将删除包注册、预置包、WindowsApps 安装目录与当前用户包数据。",
            Details = details,
            ConfirmText = isDefender ? "彻底移除" : "强制卸载",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        ProgressPercent = 0;
        ProgressText = isDefender ? "正在彻底移除 Defender…" : "正在强制卸载…";
        try
        {
            var progress = new Progress<OperationStep>(step =>
            {
                ProgressText = step.Message;
                if (step.Percent is not null) ProgressPercent = step.Percent.Value;
            });
            var operation = isDefender
                ? _defender.CreateRemovalOperation(_service)
                : new AppxForceRemoveOperation(_service, item.Package);
            var execution = await _runner.ExecuteAsync(operation, progress);
            if (execution.Success)
            {
                _toast.Show(
                    isDefender ? "Defender 移除已执行，请重启后确认" : $"已强制卸载「{item.Name}」，建议重启",
                    ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error(isDefender ? "彻底移除 Defender 失败" : "强制卸载系统组件失败", ex);
            _toast.Show($"{(isDefender ? "移除" : "强制卸载")}失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(RemovedAppxInfo? item)
    {
        if (item is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(new AppxRestoreOperation(_service, item));
            if (execution.Success)
            {
                _service.RemoveRemoved(item.Id);
                _toast.Show($"已恢复「{item.Name}」", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("恢复 Appx 失败", ex);
            _toast.Show($"恢复失败：{ex.Message}", ToastKind.Error);
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
