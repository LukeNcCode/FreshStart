using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Services.Driver;
using Microsoft.Win32;

namespace FreshStart.Modules.Driver;

public sealed partial class DriverViewModel : ObservableObject
{
    private readonly DriverService _service;
    private readonly OperationRunner _runner;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private CancellationTokenSource? _exportCts;

    public DriverViewModel(DriverService service, OperationRunner runner, IToastService toast, IUserPromptService prompts)
    {
        _service = service;
        _runner = runner;
        _toast = toast;
        _prompts = prompts;
    }

    public ObservableCollection<DriverInfo> Drivers { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "点击“读取驱动”开始枚举第三方驱动";

    [ObservableProperty]
    private string _countText = "—";

    private bool _loaded;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_loaded) return;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync();

    private async Task ReloadAsync()
    {
        IsBusy = true;
        StatusText = "正在枚举第三方驱动（需要管理员权限，通常需要几秒）…";
        try
        {
            var drivers = await _service.ListAsync().ConfigureAwait(true);
            Drivers.Clear();
            foreach (var driver in drivers) Drivers.Add(driver);
            CountText = $"{drivers.Count} 个第三方驱动";
            StatusText = drivers.Count == 0
                ? "未读取到第三方驱动（或当前环境不允许 Get-WindowsDriver）"
                : "导出会把当前系统所有第三方驱动保存到指定目录，可离线安装或备份";
            _loaded = true;
        }
        catch (Exception ex)
        {
            Log.Error("读取驱动失败", ex);
            StatusText = "读取驱动失败，请查看日志";
            _toast.Show("读取驱动失败", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (IsBusy) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择驱动导出目录",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true) return;

        var destination = Path.Combine(dialog.FolderName, $"FreshStart-Drivers-{DateTime.Now:yyyyMMdd-HHmm}");
        _exportCts?.Cancel();
        _exportCts = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "正在导出驱动…（可点击取消）";
        try
        {
            var result = await _service.ExportAsync(destination, _exportCts.Token);
            if (result.Success)
            {
                _toast.Show("驱动已导出", ToastKind.Success);
                StatusText = result.Message;
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{destination}\"") { UseShellExecute = true });
            }
            else
            {
                StatusText = result.Message;
                _toast.Show(result.Message, ToastKind.Error);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "驱动导出已取消";
            _toast.Show("驱动导出已取消", ToastKind.Warning);
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelExport()
    {
        try { _exportCts?.Cancel(); } catch { /* ignore */ }
    }

    [ObservableProperty]
    private string _installPath = string.Empty;

    [RelayCommand]
    private void ChooseInstallFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择驱动 INF 文件",
            Filter = "驱动信息 (*.inf)|*.inf|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        InstallPath = dialog.FileName;
    }

    [RelayCommand]
    private void ChooseInstallFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择驱动目录（含 INF）" };
        if (dialog.ShowDialog() != true) return;
        InstallPath = dialog.FolderName;
    }

    [RelayCommand]
    private async Task InstallDriverAsync()
    {
        var path = InstallPath.Trim();
        if (path.Length == 0)
        {
            _toast.Show("先选择驱动 INF 文件或目录", ToastKind.Warning);
            return;
        }

        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(new DriverInstallOperation(path));
            if (execution.Success)
            {
                _toast.Show(execution.Result.Message, ToastKind.Success);
                StatusText = execution.Result.Message;
                _loaded = false;
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteDriverAsync(DriverInfo? driver)
    {
        if (driver is null || IsBusy) return;

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"删除驱动包「{driver.Name}」？",
            Message = "删除前会导出驱动备份，撤销时可重新安装。",
            Details = new[] { $"INF：{driver.InfName}", driver.Subtitle },
            ConfirmText = "删除",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var execution = await _runner.ExecuteAsync(new DriverRemoveOperation(driver.InfName, driver.Name));
            if (execution.Success)
            {
                _toast.Show(execution.Result.Message, ToastKind.Success);
                _loaded = false;
                await ReloadAsync();
            }
            else if (!execution.Cancelled)
            {
                _toast.Show(execution.Result.Message, ToastKind.Error);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
