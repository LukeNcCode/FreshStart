using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Tuning;

namespace FreshStart.Modules.UiTuning;

public sealed partial class UiOptionItem : ObservableObject
{
    public UiOptionItem(TuningOption definition, bool applied, string currentText)
    {
        Definition = definition;
        OriginalApplied = applied;
        _isApplied = applied;
        CurrentText = currentText;
    }

    public TuningOption Definition { get; }
    public string Title => Definition.Title;
    public string Description => Definition.Description;
    public bool OriginalApplied { get; }
    public bool RequiresRestart => Definition.RequiresRestart;
    public string RestartText => Definition.RequiresRestart ? "需要重启" : string.Empty;

    [ObservableProperty]
    private bool _isApplied;

    [ObservableProperty]
    private string _currentText;
}

/// <summary>界面调优：复用 TuningService 的分类，草稿式修改后一次批量写入。游戏优化复用同一页面。</summary>
public partial class UiTuningViewModel : ObservableObject
{
    private readonly TuningService _tuning;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly string _category;

    public UiTuningViewModel(TuningService tuning, OperationRunner runner, IToastService toast)
        : this(tuning, runner, toast, "界面", "界面调优",
            "任务栏、资源管理器与视觉效果；草稿式修改，一次备份、失败回滚",
            "已启用界面优化", "正在读取界面设置…")
    {
    }

    protected UiTuningViewModel(
        TuningService tuning,
        OperationRunner runner,
        IToastService toast,
        string category,
        string title,
        string subtitle,
        string appliedLabel,
        string loadingText)
    {
        _tuning = tuning;
        _runner = runner;
        _toast = toast;
        _category = category;
        Title = title;
        Subtitle = subtitle;
        AppliedLabel = appliedLabel;
        StatusText = loadingText;
        ShowExplorerTools = category == "界面";
        FooterText = category == "界面"
            ? "说明：任务栏对齐、时钟秒数、小组件/任务视图按钮等项目需要重启资源管理器或重新登录才会生效；“重启资源管理器”按钮会短暂闪屏。"
            : "说明：Game DVR/游戏模式修改后立即生效；硬件加速 GPU 调度（HAGS）需要重启。关闭录制后 Win+G 仍可打开游戏栏。";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string AppliedLabel { get; }
    public string FooterText { get; }
    public bool ShowExplorerTools { get; }

    public ObservableCollection<UiOptionItem> Items { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取设置…";

    [ObservableProperty]
    private string _appliedText = "—";

    [ObservableProperty]
    private string _pendingText = "未修改";

    [ObservableProperty]
    private bool _isDirty;

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
            var rows = await Task.Run(() => _tuning.Options
                .Where(option => option.Category == _category)
                .Select(option => new UiOptionItem(option, _tuning.IsApplied(option), _tuning.CurrentText(option)))
                .ToList()).ConfigureAwait(true);

            foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var item in rows)
            {
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }

            AppliedText = $"{Items.Count(item => item.IsApplied)} / {Items.Count}";
            StatusText = ShowExplorerTools
                ? "开关只修改本地草稿；点击“应用修改”后写入并备份。任务栏/资源管理器项目可能需要重启资源管理器。"
                : "开关只修改本地草稿；点击“应用修改”后写入并备份。HAGS 等部分项目需要重启生效。";
            PendingText = "未修改";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            Log.Error("读取界面设置失败", ex);
            StatusText = "读取失败，请查看日志";
            _toast.Show("读取界面设置失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UiOptionItem.IsApplied)) return;
        var pending = Items.Count(item => item.IsApplied != item.OriginalApplied);
        IsDirty = pending > 0;
        PendingText = pending == 0 ? "未修改" : $"{pending} 项待应用";
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (IsBusy || !IsDirty) return;
        var changes = Items.Where(item => item.IsApplied != item.OriginalApplied).ToList();
        if (changes.Count == 0) return;

        IsBusy = true;
        try
        {
            var operation = _tuning.CreateBatch(
                $"tuning.{_category}.apply",
                $"应用{Title}（{changes.Count} 项）",
                $"{Subtitle}；执行前备份，失败自动回滚",
                RiskLevel.Caution,
                changes.Select(item => new TuningChange(item.Definition, item.IsApplied)).ToList());

            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"已应用 {changes.Count} 项{Title}设置", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("应用界面设置失败", ex);
            _toast.Show($"应用失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Discard()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
            item.IsApplied = item.OriginalApplied;
            item.PropertyChanged += OnItemChanged;
        }
        IsDirty = false;
        PendingText = "未修改";
    }

    [RelayCommand]
    private void RestartExplorer()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("explorer"))
            {
                try { process.Kill(); } catch { /* ignore */ }
                process.Dispose();
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            _toast.Show("已重启资源管理器", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Warn($"重启资源管理器失败: {ex.Message}");
            _toast.Show("重启资源管理器失败", ToastKind.Warning);
        }
    }
}
