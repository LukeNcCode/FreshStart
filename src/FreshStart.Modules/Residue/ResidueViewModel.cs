using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Residue;
using FreshStart.Services.Uninstall;

namespace FreshStart.Modules.Residue;

public sealed partial class ResidueRow : ObservableObject
{
    public ResidueRow(ResidueItem item, bool selected)
    {
        Item = item;
        _isSelected = selected;
    }

    public ResidueItem Item { get; }
    public string Kind => Item.Kind;
    public string Path => Item.Path;
    public string Evidence => Item.Evidence;
    public string RiskText => Item.RiskText;
    public string SizeText => Item.SizeText;
    public bool CanClean => Item.CanClean;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class ResidueViewModel : ObservableObject
{
    private readonly ResidueService _service;
    private readonly IUserPromptService _prompts;
    private readonly IToastService _toast;
    private ResidueScanResult? _result;

    public ResidueViewModel(ResidueService service, IUserPromptService prompts, IToastService toast)
    {
        _service = service;
        _prompts = prompts;
        _toast = toast;
    }

    public ObservableCollection<InstalledProgram> Targets { get; } = new();
    public ObservableCollection<ResidueRow> Items { get; } = new();

    [ObservableProperty]
    private InstalledProgram? _selectedTarget;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "选择已卸载的程序后扫描残留";

    [ObservableProperty]
    private string _summaryText = "—";

    [ObservableProperty]
    private bool _hasItems;

    [RelayCommand]
    private void Load()
    {
        Targets.Clear();
        foreach (var target in _service.GetTargets()) Targets.Add(target);
        SelectedTarget = Targets.FirstOrDefault();
        StatusText = Targets.Count == 0
            ? "暂无已卸载程序记录；通过“软件卸载”卸载后会自动记录"
            : "选择程序后点击“扫描残留”；清理前会二次确认";
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsBusy || SelectedTarget is null) return;
        IsBusy = true;
        StatusText = "正在扫描安装目录、快捷方式、卸载键与服务…";
        try
        {
            _result = await _service.ScanAsync(SelectedTarget);
            foreach (var row in Items) row.PropertyChanged -= OnRowChanged;
            Items.Clear();
            foreach (var item in _result.Items)
            {
                var row = new ResidueRow(item, item.DefaultSelected && item.CanClean);
                row.PropertyChanged += OnRowChanged;
                Items.Add(row);
            }
            HasItems = Items.Count > 0;
            UpdateSummary();
            StatusText = Items.Count == 0 ? "未发现残留" : "默认只勾选安全项；危险项需要手动勾选";
        }
        catch (Exception ex)
        {
            Log.Error("扫描卸载残留失败", ex);
            StatusText = $"扫描失败：{ex.Message}";
            _toast.Show("扫描卸载残留失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResidueRow.IsSelected)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = Items.Count(row => row.IsSelected && row.CanClean);
        SummaryText = $"{Items.Count} 项残留 · 已选 {selected} 项";
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (IsBusy || _result is null) return;
        var selected = Items.Where(row => row.IsSelected && row.CanClean).Select(row => row.Item).ToList();
        if (selected.Count == 0)
        {
            _toast.Show("请先勾选要清理的残留", ToastKind.Warning);
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"清理「{_result.Target.DisplayName}」残留？",
            Message = $"将清理 {selected.Count} 项残留，文件移入隔离区，注册表键先快照。",
            Details = new[]
            {
                "默认只包含安全项；危险项需手动勾选",
                "服务不会被自动停止或删除",
                "清理后生成 HTML/JSON 报告"
            },
            ConfirmText = "清理",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var (html, json) = await _service.CleanAsync(_result, selected, CancellationToken.None);
            StatusText = $"清理完成：{html}";
            _toast.Show("残留清理完成", ToastKind.Success);
            await ScanAsync();
        }
        catch (Exception ex)
        {
            Log.Error("清理卸载残留失败", ex);
            StatusText = $"清理失败：{ex.Message}";
            _toast.Show("清理卸载残留失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
