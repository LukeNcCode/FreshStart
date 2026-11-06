using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Software;
using Microsoft.Win32;

namespace FreshStart.Modules.Software;

public sealed partial class SoftwareItem : ObservableObject
{
    public SoftwareItem(SoftwareDefinition definition) => Definition = definition;

    public SoftwareDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Description => Definition.Description;
    public string Id => Definition.Id;
    public string Category => Definition.Category;
    public bool IsCustom => Definition.IsCustom;

    /// <summary>该软件是否有「官方直链」兜底（winget 失败时改用厂商安装包）。</summary>
    public bool HasDirectFallback => DirectInstallerCatalog.HasFallback(Definition.Id);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private string _installState = "未检测";

    /// <summary>可升级到的新版本（空 = 已是最新或无法判断）。</summary>
    [ObservableProperty]
    private string _availableVersion = string.Empty;

    [ObservableProperty]
    private bool _hasUpdate;

    /// <summary>升级按钮文案：带上目标版本。</summary>
    public string UpdateText => AvailableVersion.Length > 0 ? $"更新到 {AvailableVersion}" : "更新";

    partial void OnAvailableVersionChanged(string value) => OnPropertyChanged(nameof(UpdateText));

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && IsInstalled)
        {
            IsSelected = false;
        }
    }
}

/// <summary>
/// 推荐软件页：winget 为主通道，失败时对已登记的高频软件走「官方直链 + 静默参数」兜底。
/// </summary>
public sealed partial class SoftwareViewModel : ObservableObject
{
    private readonly WingetService _winget;
    private readonly DirectInstallerService _direct;
    private readonly SoftwareCustomStore _custom = new();
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly List<SoftwareItem> _all = new();
    private readonly List<SoftwareItem> _lastFailed = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wingetCts;
    private CancellationTokenSource? _loadCts;
    private int _selectedCount;

    public SoftwareViewModel(
        WingetService winget, DirectInstallerService direct, OperationRunner runner, IToastService toast)
    {
        _winget = winget;
        _direct = direct;
        _runner = runner;
        _toast = toast;
        Filters = new ObservableCollection<string> { "全部" };
        _selectedFilter = "全部";
    }

    public ObservableCollection<string> Filters { get; }

    [ObservableProperty]
    private string _selectedFilter = "全部";

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isWingetAvailable;

    [ObservableProperty]
    private bool _canInstallSoftware;

    [ObservableProperty]
    private bool _isWingetOutdated;

    [ObservableProperty]
    private string _wingetVersionText = "";

    [ObservableProperty]
    private bool _showWingetSetup;

    [ObservableProperty]
    private bool _isWingetInstalling;

    [ObservableProperty]
    private string _wingetInstallText = "";

    [ObservableProperty]
    private string _statusText = "正在检测 winget…";

    [ObservableProperty]
    private string _selectedText = "0";

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _hasFailed;

    [ObservableProperty]
    private string _customId = string.Empty;

    [ObservableProperty]
    private string _customName = string.Empty;

    [ObservableProperty]
    private string _customDescription = string.Empty;

    /// <summary>自定义安装路径（可选）；空 = 安装器默认目录，透传 winget --location。</summary>
    [ObservableProperty]
    private string _installLocation = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<SoftwareItem> _packages = Array.Empty<SoftwareItem>();

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_all.Count > 0) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        var result = await _winget.ClearCacheAsync();
        StatusText = result.Message;
        _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Warning);
    }

    /// <summary>刷新源索引：索引滞后会导致「源里找不到该包」（0x8A150014）。</summary>
    [RelayCommand]
    private async Task UpdateSourceIndexAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusText = "正在刷新 winget 源索引…";
        try
        {
            var result = await _winget.UpdateSourcesAsync(
                line => StatusText = line, CancellationToken.None);
            if (result.Success)
            {
                StatusText = "源索引已更新";
                _toast.Show("winget 源索引已更新", ToastKind.Success);
            }
            else
            {
                var lastLine = result.Output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .LastOrDefault(line => line.Length > 0) ?? $"exit {result.ExitCode}";
                StatusText = "源索引更新失败：" + lastLine;
                _toast.Show(StatusText, ToastKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RepairWingetAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        StatusText = "正在修复 winget（重置源 + 清理缓存）…";
        try
        {
            var result = await _winget.RepairAsync(line => StatusText = line, CancellationToken.None);
            StatusText = result.Message;
            _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Error);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("修复 winget 失败", ex);
            StatusText = $"修复失败：{ex.Message}";
            _toast.Show(StatusText, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        try
        {
            var custom = _custom.Load();
            var definitions = SoftwareCatalog.All.Concat(custom).ToList();
            foreach (var item in _all) item.PropertyChanged -= OnItemPropertyChanged;
            _all.Clear();
            foreach (var definition in definitions)
            {
                var item = new SoftwareItem(definition);
                item.PropertyChanged += OnItemPropertyChanged;
                _all.Add(item);
            }

            var categories = new[] { "全部" }.Concat(definitions.Select(d => d.Category).Distinct().OrderBy(c => c)).ToList();
            Filters.Clear();
            foreach (var category in categories) Filters.Add(category);
            if (!categories.Contains(SelectedFilter)) SelectedFilter = "全部";

            SelectedText = "0";
            _selectedCount = 0;
            ApplyFilter();
            StatusText = "正在检测 winget 与已安装状态…";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error("加载推荐软件失败", ex);
            _toast.Show("加载推荐软件失败，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsLoading = false;
        }

        if (ct.IsCancellationRequested) return;
        try
        {
            StatusText = "正在探测 winget…";
            IsWingetAvailable = await Task.Run(() => _winget.IsAvailableAsync(ct), ct).ConfigureAwait(true);
            WingetVersionText = _winget.Version?.ToString() ?? "未知";
            IsWingetOutdated = IsWingetAvailable && !WingetService.MeetsRequiredVersion(_winget.Version);
            CanInstallSoftware = IsWingetAvailable && !IsWingetOutdated;
            ShowWingetSetup = !IsWingetAvailable || IsWingetOutdated;
            StatusText = !IsWingetAvailable
                ? "未检测到 winget，安装功能不可用"
                : IsWingetOutdated
                    ? $"winget 版本过低（v{WingetVersionText}），请先更新 App Installer"
                    : $"winget 可用（v{WingetVersionText}）";
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await DetectInstalledAsync(ct).ConfigureAwait(true);
    }

    private async Task DetectInstalledAsync(CancellationToken ct)
    {
        if (!IsWingetAvailable || _all.Count == 0) return;
        foreach (var item in _all) item.InstallState = "检测中…";
        StatusText = "winget 可用 · 正在检测已安装软件…";

        try
        {
            var index = await Task.Run(() => _winget.GetInstalledIndexAsync(ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            if (index is null)
            {
                foreach (var item in _all) item.InstallState = "未检测";
                StatusText = "winget 可用 · 已安装状态检测失败，可点“刷新”重试";
                return;
            }

            var count = 0;
            foreach (var item in _all)
            {
                var isInstalled = index.Matches(item.Definition);
                item.IsInstalled = isInstalled;
                item.InstallState = isInstalled ? "已安装" : "未安装";
                if (isInstalled)
                {
                    item.IsSelected = false;
                    count++;
                }
            }
            StatusText = $"winget 可用 · 已安装 {count} / {_all.Count}";
            await DetectUpdatesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // 页面离开，保留已有状态
        }
        catch (Exception ex)
        {
            Log.Warn($"检测已安装软件失败: {ex.Message}");
            foreach (var item in _all) item.InstallState = "未检测";
            StatusText = "winget 可用 · 已安装状态检测失败，可点“刷新”重试";
        }
    }

    /// <summary>查询可升级版本：只在已安装的条目上显示「更新」按钮。</summary>
    private async Task DetectUpdatesAsync(CancellationToken ct)
    {
        if (!IsWingetAvailable || IsWingetOutdated) return;

        try
        {
            var upgrades = await _winget.GetUpgradeInfoAsync(ct).ConfigureAwait(true);
            if (upgrades is null) return;   // 查询失败：保留已有状态
            foreach (var item in _all)
            {
                var has = item.IsInstalled
                          && upgrades.TryGetValue(item.Id, out var available)
                          && available.Length > 0;
                item.HasUpdate = has;
                item.AvailableVersion = has ? upgrades[item.Id] : string.Empty;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面离开：忽略
        }
        catch (Exception ex)
        {
            Log.Warn($"检测软件更新失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 独立更新单个软件。用户态程序直接在 winget 中就地升级，保留现有配置。
    /// </summary>
    [RelayCommand]
    private async Task UpgradeSingleAsync(SoftwareItem? item)
    {
        if (item is null || !item.HasUpdate || IsBusy || !CanInstallSoftware) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsCancelling = false;
        var ct = _cts.Token;
        try
        {
            ProgressText = $"正在更新 {item.Name}…";
            var result = await _winget.UpgradeAsync(item.Id, line => ProgressText = line, ct);

            if (result.Canceled)
            {
                StatusText = "已取消更新";
                _toast.Show("已取消更新", ToastKind.Warning);
                return;
            }

            if (result.Success)
            {
                item.HasUpdate = false;
                item.AvailableVersion = string.Empty;
                item.InstallState = "已安装";
                StatusText = $"已更新 {item.Name}";
                _toast.Show($"{item.Name} 已更新", ToastKind.Success);
                await ReloadAsync();
                return;
            }

            StatusText = $"更新 {item.Name} 失败";
            _toast.Show(
                $"更新失败：{FirstLine(result.Output)}",
                ToastKind.Error);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消更新";
            _toast.Show("已取消更新（进程已结束，可能只完成了一部分）", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error($"升级 {item.Name} 失败", ex);
            StatusText = "更新失败";
            _toast.Show($"更新失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            IsCancelling = false;
            ProgressText = string.Empty;
        }
    }

    /// <summary>取 winget 输出里第一条有意义的行，用于 toast。</summary>
    private static string FirstLine(string output)
    {
        var line = output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0 && !value.StartsWith("-", StringComparison.Ordinal));
        return line is null || line.Length <= 80 ? line ?? "未知原因" : line[..80] + "…";
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SoftwareItem.IsSelected)) return;
        _selectedCount = _all.Count(item => item.IsSelected);
        SelectedText = _selectedCount.ToString();
    }

    private void ApplyFilter()
    {
        var showAll = string.Equals(SelectedFilter, "全部", StringComparison.OrdinalIgnoreCase);
        var search = SearchText.Trim();
        Packages = _all
            .Where(item => (showAll || string.Equals(item.Category, SelectedFilter, StringComparison.OrdinalIgnoreCase))
                           && (string.IsNullOrWhiteSpace(search)
                               || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                               || item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    [RelayCommand]
    private async Task InstallSelectedAsync()
    {
        var selected = _all.Where(item => item.IsSelected).ToList();
        await InstallItemsAsync(selected);
    }

    /// <summary>卡片上的「安装」按钮：只装这一个。</summary>
    [RelayCommand]
    private async Task InstallSingleAsync(SoftwareItem? item)
    {
        if (item is null) return;
        if (item.IsInstalled)
        {
            _toast.Show($"{item.Name} 已安装", ToastKind.Info);
            return;
        }
        await InstallItemsAsync(new[] { item });
    }

    /// <summary>一键安装清单里所有未安装的软件。</summary>
    [RelayCommand]
    private async Task InstallAllMissingAsync()
    {
        if (!IsWingetAvailable || IsWingetOutdated) return;
        var pending = _all.Where(item => !item.IsInstalled).ToList();
        if (pending.Count == 0)
        {
            _toast.Show("推荐清单里的软件都已安装", ToastKind.Success);
            return;
        }
        await InstallItemsAsync(pending);
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        await InstallItemsAsync(_lastFailed.ToList());
    }

    private async Task InstallItemsAsync(IReadOnlyList<SoftwareItem> items)
    {
        if (IsBusy || items.Count == 0) return;
        if (!IsWingetAvailable)
        {
            _toast.Show("winget 不可用，无法安装", ToastKind.Warning);
            return;
        }
        if (IsWingetOutdated)
        {
            _toast.Show(
                $"winget v{WingetVersionText} 无法校验当前源索引（0x8A15005E），安装会失败。" +
                "请先更新 App Installer（或用「修复 winget」重置源）",
                ToastKind.Warning);
            return;
        }

        var pending = items.Where(item => item.InstallState != "已安装").ToList();
        if (pending.Count == 0)
        {
            _toast.Show("选中的软件均已安装", ToastKind.Success);
            return;
        }
        items = pending;

        var installLocation = InstallLocation?.Trim() ?? string.Empty;
        if (installLocation.Length > 0 && !IsValidInstallLocation(installLocation, out var locationError))
        {
            _toast.Show(locationError, ToastKind.Warning);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        HasFailed = false;
        ProgressPercent = 0;
        ProgressText = "准备安装…";

        try
        {
            var progress = new Progress<OperationStep>(step =>
            {
                ProgressText = step.Message;
                if (step.Percent is not null) ProgressPercent = step.Percent.Value;
            });

            var operation = new WingetInstallBatchOperation(
                _winget,
                items.Select(item => item.Definition).ToList(),
                installLocation.Length == 0 ? null : installLocation,
                _direct);
            var execution = await _runner.ExecuteAsync(operation, progress, _cts.Token);
            if (execution.Success)
            {
                foreach (var item in items) item.InstallState = "已安装";
                _lastFailed.Clear();
                HasFailed = false;
                StatusText = $"winget 可用 · 已安装 {_all.Count(item => item.InstallState == "已安装")} / {_all.Count}";
                _toast.Show($"安装流程完成：{items.Count} 个软件", ToastKind.Success);
            }
            else if (!execution.Cancelled)
            {
                var installedIds = operation.InstalledPackages
                    .Select(package => package.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (installedIds.Contains(item.Definition.Id)) item.InstallState = "已安装";
                }

                var retryItems = operation.FailedPackages
                    .Select(failed => items.FirstOrDefault(item =>
                        string.Equals(item.Definition.Id, failed.Id, StringComparison.OrdinalIgnoreCase)))
                    .Where(item => item is not null)
                    .Cast<SoftwareItem>()
                    .ToList();
                if (retryItems.Count == 0)
                {
                    retryItems = items
                        .Where(item => !installedIds.Contains(item.Definition.Id))
                        .ToList();
                }

                _lastFailed.Clear();
                _lastFailed.AddRange(retryItems);
                HasFailed = retryItems.Count > 0;
                StatusText = $"winget 可用 · 已安装 {_all.Count(item => item.InstallState == "已安装")} / {_all.Count}";
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
            else if (execution.Cancelled)
            {
                // 取消不是失败：已装成功的保留，状态如实反映，并明确告诉用户当前结果
                var installedIds = operation.InstalledPackages
                    .Select(package => package.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    if (installedIds.Contains(item.Definition.Id)) item.InstallState = "已安装";
                }

                StatusText = $"已取消 · 已安装 {_all.Count(item => item.InstallState == "已安装")} / {_all.Count}";
                _toast.Show(installedIds.Count > 0
                    ? $"已取消：{installedIds.Count} 个已装好的软件保留，其余未执行"
                    : "已取消：本次未写入任何变更", ToastKind.Warning);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            _toast.Show("已取消安装", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("winget 安装失败", ex);
            _lastFailed.Clear();
            _lastFailed.AddRange(items);
            HasFailed = true;
            _toast.Show($"安装失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            IsCancelling = false;
            ProgressText = string.Empty;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    private bool _isCancelling;

    /// <summary>winget 自更新/本地安装的取消状态（与软件批量安装的取消分开）。</summary>
    [ObservableProperty]
    private bool _isWingetCancelling;

    /// <summary>取消按钮文案：点过一次后变成「取消中…」，避免重复点击却看不到反应。</summary>
    public string CancelButtonText => IsCancelling ? "取消中…" : "取消";

    public bool CanCancel => !IsCancelling;

    [RelayCommand]
    private void CancelInstall() => Cancel();


    [RelayCommand]
    private async Task AddCustomAsync()
    {
        var id = CustomId.Trim();
        var name = CustomName.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            _toast.Show("请填写 winget ID 和名称", ToastKind.Warning);
            return;
        }
        if (_all.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            _toast.Show("该 winget ID 已存在", ToastKind.Warning);
            return;
        }

        await _custom.AddAsync(new SoftwareDefinition
        {
            Id = id,
            Name = name,
            Description = string.IsNullOrWhiteSpace(CustomDescription) ? "自定义来源" : CustomDescription.Trim(),
            Category = "自定义",
            IsCustom = true
        });
        CustomId = CustomName = CustomDescription = string.Empty;
        _toast.Show("已添加自定义软件源", ToastKind.Success);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RemoveCustomAsync(SoftwareItem? item)
    {
        if (item is null || !item.IsCustom) return;
        await _custom.RemoveAsync(item.Id);
        _toast.Show("已移除自定义来源", ToastKind.Success);
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task InstallWingetAsync()
    {
        if (IsWingetInstalling) return;
        IsWingetInstalling = true;
        IsWingetCancelling = false;
        _wingetCts?.Dispose();
        _wingetCts = new CancellationTokenSource();
        WingetInstallText = "正在准备下载…";
        try
        {
            var progress = new Progress<string>(text => WingetInstallText = text);
            var result = await _winget.InstallOrUpdateAsync(progress, _wingetCts.Token);
            WingetInstallText = result.Message;
            _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Error);
            if (result.Success) await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            WingetInstallText = "已取消下载。需要时可重新点击「安装 / 更新 winget」。";
            _toast.Show("已取消 winget 下载", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            WingetInstallText = $"安装 winget 失败：{ex.Message}";
            _toast.Show(WingetInstallText, ToastKind.Error);
        }
        finally
        {
            IsWingetInstalling = false;
            IsWingetCancelling = false;
        }
    }

    /// <summary>取消正在进行的 winget 下载/安装（HTTP 流与 InstallPackage 都尊重 token）。</summary>
    [RelayCommand]
    private void CancelWingetInstall()
    {
        if (_wingetCts is null || _wingetCts.IsCancellationRequested) return;
        IsWingetCancelling = true;
        WingetInstallText = "正在取消…";
        try { _wingetCts.Cancel(); } catch { /* ignore */ }
    }

    [RelayCommand]
    private void BrowseInstallLocation()
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择安装目录（留空则用安装器默认目录）",
                Multiselect = false
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                InstallLocation = dialog.FolderName;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"选择安装目录失败: {ex.Message}");
            _toast.Show("无法打开目录选择框，可直接手动输入路径", ToastKind.Warning);
        }
    }

    /// <summary>安装路径基本校验：非空、绝对路径、不含非法字符（不要求目录已存在）。</summary>
    private static bool IsValidInstallLocation(string path, out string error)
    {
        error = string.Empty;
        if (!Path.IsPathFullyQualified(path))
        {
            error = "安装路径需要是绝对路径，例如 D:\\Apps";
            return false;
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            error = "安装路径包含非法字符";
            return false;
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetPathRoot(trimmed)?.TrimEnd(Path.DirectorySeparatorChar) == trimmed)
        {
            error = "不能把盘符根目录作为安装路径";
            return false;
        }

        return true;
    }

    [RelayCommand]
    private async Task InstallWingetFromFileAsync()
    {
        if (IsWingetInstalling) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择已下载的 App Installer",
            Filter = "App Installer (*.msixbundle;*.appx;*.msix)|*.msixbundle;*.appx;*.msix|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        IsWingetInstalling = true;
        IsWingetCancelling = false;
        _wingetCts?.Dispose();
        _wingetCts = new CancellationTokenSource();
        WingetInstallText = "正在安装本地 App Installer…";
        try
        {
            var progress = new Progress<string>(text => WingetInstallText = text);
            var result = await _winget.InstallFromFileAsync(dialog.FileName, progress, _wingetCts.Token);
            WingetInstallText = result.Message;
            _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Error);
            if (result.Success) await ReloadAsync();
        }
        catch (OperationCanceledException)
        {
            WingetInstallText = "已取消本地安装";
            _toast.Show(WingetInstallText, ToastKind.Warning);
        }
        catch (Exception ex)
        {
            WingetInstallText = $"安装失败：{ex.Message}";
            _toast.Show(WingetInstallText, ToastKind.Error);
        }
        finally
        {
            IsWingetInstalling = false;
            IsWingetCancelling = false;
        }
    }

    [RelayCommand]
    private void OpenAppInstallerStore()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-windows-store://pdp/?productid=9NBLGGH4NNS1")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开 App Installer 商店页失败: {ex.Message}");
            _toast.Show("无法打开 Microsoft Store", ToastKind.Warning);
        }
    }

    /// <summary>
    /// 请求取消（按钮与页面离开都会走这里）。先给 UI 立即反馈再触发 token：
    /// winget 被杀后安装器进程可能还要几秒收尾，而回滚阶段由 OperationRunner 提示且不可中断。
    /// </summary>
    public void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested)
        {
            try { _loadCts?.Cancel(); } catch { /* ignore */ }
            return;
        }

        IsCancelling = true;
        ProgressText = "正在取消…（结束安装器进程，随后回滚本次已应用的变更）";
        try { _cts.Cancel(); } catch { /* ignore */ }
    }
}
