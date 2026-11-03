using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Tuning;

namespace FreshStart.Modules.Privacy;

public sealed partial class PrivacyOptionItem : ObservableObject
{
    public PrivacyOptionItem(TuningOption definition, bool applied, string currentText)
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

public sealed partial class PrivacyViewModel : ObservableObject
{
    private readonly TuningService _tuning;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;

    public PrivacyViewModel(TuningService tuning, OperationRunner runner, IToastService toast)
    {
        _tuning = tuning;
        _runner = runner;
        _toast = toast;
    }

    public ObservableCollection<PrivacyOptionItem> Items { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取隐私设置…";

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
            var result = await Task.Run(() =>
            {
                var rows = new List<PrivacyOptionItem>();
                foreach (var option in _tuning.Options.Where(o => o.Category == "隐私"))
                {
                    rows.Add(new PrivacyOptionItem(option, _tuning.IsApplied(option), _tuning.CurrentText(option)));
                }
                return rows;
            }).ConfigureAwait(true);

            foreach (var item in Items) item.PropertyChanged -= OnItemChanged;
            Items.Clear();
            foreach (var item in result)
            {
                item.PropertyChanged += OnItemChanged;
                Items.Add(item);
            }

            var applied = Items.Count(item => item.IsApplied);
            AppliedText = $"{applied} / {Items.Count}";
            StatusText = Items.Count == 0 ? "未读取到隐私设置" : "开关仅修改本地草稿；点击“应用修改”后才会写入并备份";
            PendingText = "未修改";
            IsDirty = false;
        }
        catch (Exception ex)
        {
            Log.Error("读取隐私设置失败", ex);
            StatusText = "读取失败，请查看日志";
            _toast.Show("读取隐私设置失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PrivacyOptionItem.IsApplied)) return;
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
                "privacy.apply",
                $"应用隐私设置（{changes.Count} 项）",
                "写入隐私相关注册表值；每项写入前备份，失败自动回滚",
                RiskLevel.Caution,
                changes.Select(item => new TuningChange(item.Definition, item.IsApplied)).ToList());

            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"已应用 {changes.Count} 项隐私设置", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("应用隐私设置失败", ex);
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
}
