using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Tuning;

namespace FreshStart.Modules.Presets;

public sealed partial class PresetItem : ObservableObject
{
    public PresetItem(TuningPreset preset, IReadOnlyList<TuningOption> options, int applied)
    {
        Preset = preset;
        Options = options;
        AppliedCount = applied;
        _isFullyApplied = applied >= options.Count;
    }

    public TuningPreset Preset { get; }
    public IReadOnlyList<TuningOption> Options { get; }
    public string Name => Preset.Name;
    public string Description => Preset.Description;
    public string Tag => Preset.Tag;
    public string CountText => $"{Options.Count} 项设置";
    public string DetailText => string.Join(" · ", Options.Take(4).Select(o => o.Title))
                                + (Options.Count > 4 ? $" 等 {Options.Count} 项" : string.Empty);

    [ObservableProperty]
    private int _appliedCount;

    [ObservableProperty]
    private bool _isFullyApplied;

    public string AppliedText => IsFullyApplied ? "已全部应用" : $"已应用 {AppliedCount} / {Options.Count}";

    partial void OnIsFullyAppliedChanged(bool value) => OnPropertyChanged(nameof(AppliedText));
    partial void OnAppliedCountChanged(int value) => OnPropertyChanged(nameof(AppliedText));
}

public sealed partial class PresetsViewModel : ObservableObject
{
    private readonly TuningService _tuning;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;

    public PresetsViewModel(TuningService tuning, OperationRunner runner, IToastService toast)
    {
        _tuning = tuning;
        _runner = runner;
        _toast = toast;
    }

    public ObservableCollection<PresetItem> Items { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取当前预设状态…";

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (Items.Count > 0) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var items = await Task.Run(() => _tuning.Presets.Select(preset =>
            {
                var options = preset.Options
                    .Select(_tuning.Find)
                    .Where(option => option is not null)
                    .Cast<TuningOption>()
                    .ToList();
                var applied = options.Count(_tuning.IsApplied);
                return new PresetItem(preset, options, applied);
            }).ToList()).ConfigureAwait(true);

            Items.Clear();
            foreach (var item in items) Items.Add(item);
            StatusText = "预设会一次性写入多项设置；执行前会显示完整变更清单，可回滚";
        }
        catch (Exception ex)
        {
            Log.Error("读取预设状态失败", ex);
            StatusText = "读取预设状态失败，请查看日志";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ApplyAsync(PresetItem? item)
    {
        if (item is null || IsBusy || item.Options.Count == 0) return;

        IsBusy = true;
        try
        {
            var operation = _tuning.CreateBatch(
                $"preset.{item.Preset.Key}",
                $"一键开荒：{item.Name}",
                $"{item.Description}（共 {item.Options.Count} 项）",
                RiskLevel.Caution,
                item.Options);

            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"已应用「{item.Name}」预设", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("应用预设失败", ex);
            _toast.Show($"应用预设失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevertAsync(PresetItem? item)
    {
        if (item is null || IsBusy || item.Options.Count == 0) return;

        IsBusy = true;
        try
        {
            var operation = _tuning.CreateBatch(
                $"preset.{item.Preset.Key}.revert",
                $"还原预设：{item.Name}",
                $"把 {item.Options.Count} 项设置恢复为各自的关闭/默认值",
                RiskLevel.Caution,
                item.Options,
                apply: false);

            var execution = await _runner.ExecuteAsync(operation);
            if (execution.Success)
            {
                _toast.Show($"已还原「{item.Name}」预设", ToastKind.Success);
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("还原预设失败", ex);
            _toast.Show($"还原失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
