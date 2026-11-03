using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Residue;
using FreshStart.Services.Uninstall;
using FreshStart.Modules.Residue;

namespace FreshStart.Modules.Uninstall;

public sealed partial class UninstallViewModel : ObservableObject
{
    private readonly InstalledProgramService _service;
    private readonly ResidueService _residue;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private List<InstalledProgram> _all = new();
    private ResidueScanResult? _lastScan;

    public UninstallViewModel(
        InstalledProgramService service,
        ResidueService residue,
        OperationRunner runner,
        IToastService toast,
        IUserPromptService prompts)
    {
        _service = service;
        _residue = residue;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
    }

    public ObservableCollection<ResidueRow> ResidueItems { get; } = new();

    [ObservableProperty]
    private bool _hasResidue;

    [ObservableProperty]
    private string _residueSummary = "";

    public ObservableCollection<InstalledProgram> Programs { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "正在读取已安装程序…";

    [ObservableProperty]
    private string _countText = "—";

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
        IsBusy = true;
        try
        {
            _all = await Task.Run(() => _service.List().ToList()).ConfigureAwait(true);
            ApplyFilter();
            CountText = $"{_all.Count} 个已安装程序";
            StatusText = "列表来自注册表卸载项；卸载调用程序自带命令，不可自动回滚";
        }
        catch (Exception ex)
        {
            Log.Error("读取已安装程序失败", ex);
            StatusText = "读取失败，请查看日志";
            _toast.Show("读取已安装程序失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();
        var rows = string.IsNullOrEmpty(search)
            ? _all
            : _all.Where(program => program.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                    || program.Publisher.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();

        Programs.Clear();
        foreach (var program in rows.Take(500)) Programs.Add(program);
    }

    [RelayCommand]
    private async Task UninstallAsync(InstalledProgram? program)
    {
        if (program is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(new UninstallProgramOperation(_service, program));
            if (execution.Success)
            {
                _service.RecordUninstalled(program);
                ResidueScanResult? scan = null;
                try
                {
                    scan = await _residue.ScanAsync(program);
                }
                catch (Exception ex)
                {
                    Log.Warn($"卸载后残留扫描失败: {ex.Message}");
                }

                if (scan is not null)
                {
                    _lastScan = scan;
                    ResidueItems.Clear();
                    foreach (var item in scan.Items)
                    {
                        ResidueItems.Add(new ResidueRow(item, item.DefaultSelected && item.CanClean));
                    }
                    HasResidue = ResidueItems.Count > 0;

                    var cleanable = scan.Items.Where(item => item.CanClean).ToList();
                    if (cleanable.Count > 0)
                    {
                        StatusText = $"正在自动清理 {cleanable.Count} 项残留…";
                        try
                        {
                            var (html, _) = await _residue.CleanAsync(scan, cleanable, CancellationToken.None);
                            ResidueItems.Clear();
                            HasResidue = false;
                            ResidueSummary = $"已自动清理 {cleanable.Count} 项残留";
                            StatusText = $"卸载并清理完成：{html}";
                            _toast.Show($"已卸载「{program.DisplayName}」，自动清理 {cleanable.Count} 项残留", ToastKind.Success);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("自动清理卸载残留失败", ex);
                            ResidueSummary = "自动清理失败，可在下方手动选择清理";
                            _toast.Show("自动清理残留失败，请手动清理", ToastKind.Warning);
                        }
                    }
                    else
                    {
                        ResidueSummary = "未发现残留";
                        _toast.Show($"已卸载「{program.DisplayName}」", ToastKind.Success);
                    }
                }
                else
                {
                    HasResidue = false;
                    ResidueSummary = "残留扫描失败，详情见日志";
                    _toast.Show($"已卸载「{program.DisplayName}」", ToastKind.Success);
                }

                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            Log.Error("卸载程序失败", ex);
            _toast.Show($"卸载失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ScanLastResidueAsync()
    {
        if (IsBusy) return;
        var target = _residue.GetTargets().FirstOrDefault();
        if (target is null)
        {
            _toast.Show("没有已卸载程序记录；先卸载一个程序再扫描", ToastKind.Warning);
            return;
        }

        IsBusy = true;
        StatusText = $"正在扫描「{target.DisplayName}」残留…";
        try
        {
            var scan = await _residue.ScanAsync(target);
            _lastScan = scan;
            ResidueItems.Clear();
            foreach (var item in scan.Items)
            {
                ResidueItems.Add(new ResidueRow(item, item.DefaultSelected && item.CanClean));
            }
            HasResidue = ResidueItems.Count > 0;
            ResidueSummary = HasResidue
                ? $"发现 {ResidueItems.Count} 项残留，默认勾选安全项；可立即清理"
                : "未发现残留";
            StatusText = ResidueSummary;
        }
        catch (Exception ex)
        {
            Log.Error("扫描上次卸载残留失败", ex);
            _toast.Show("扫描残留失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CleanResidueAsync()
    {
        if (_lastScan is null || IsBusy) return;
        var selected = ResidueItems
            .Where(row => row.IsSelected && row.CanClean)
            .Select(row => row.Item)
            .ToList();
        if (selected.Count == 0)
        {
            _toast.Show("请先勾选要清理的残留", ToastKind.Warning);
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"清理「{_lastScan.Target.DisplayName}」残留？",
            Message = $"将清理 {selected.Count} 项残留，文件移入隔离区，注册表键先快照。",
            Details = new[]
            {
                "默认只勾选安全项，危险项需手动选择",
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
            var (html, _) = await _residue.CleanAsync(_lastScan, selected, CancellationToken.None);
            ResidueItems.Clear();
            HasResidue = false;
            ResidueSummary = "残留已清理";
            StatusText = $"残留清理完成：{html}";
            _toast.Show("残留清理完成", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("清理卸载残留失败", ex);
            _toast.Show($"清理残留失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenLocation(InstalledProgram? program)
    {
        if (program is null || string.IsNullOrWhiteSpace(program.InstallLocation)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{program.InstallLocation}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开安装目录失败: {ex.Message}");
            _toast.Show("无法打开安装目录", ToastKind.Warning);
        }
    }
}
