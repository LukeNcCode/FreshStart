using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Software;

namespace FreshStart.App.Startup;

/// <summary>
/// 非强制 winget 更新窗口。只在安装包内存在离线 App Installer 时调用，
/// 用户可以继续进入主界面，更新过程也不会联网。
/// </summary>
public partial class WingetUpdateWindow : Window, INotifyPropertyChanged
{
    private readonly WingetService _winget;
    private readonly Version _bundledVersion;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public WingetUpdateWindow(
        WingetService winget,
        bool available,
        Version? currentVersion,
        Version bundledVersion)
    {
        InitializeComponent();
        _winget = winget;
        _bundledVersion = bundledVersion;
        DataContext = this;

        VersionText = available && currentVersion is not null
            ? $"v{currentVersion} → v{bundledVersion}"
            : $"未安装 → v{bundledVersion}";
        SummaryText = available
            ? $"当前 winget 为 v{currentVersion?.ToString() ?? "未知"}，安装包内提供了 v{bundledVersion} 离线更新。"
            : "当前系统未检测到 winget，安装包内提供了可直接安装的离线版本。";
        StatusText = "可以选择更新，也可以稍后继续使用 FreshStart。";
        Loaded += (_, _) => Activate();
        Closed += (_, _) => CancelWork();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string SummaryText { get; }
    public string VersionText { get; }

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private string _actionText = "安装离线更新";
    public string ActionText
    {
        get => _actionText;
        private set => SetField(ref _actionText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value)) OnPropertyChanged(nameof(CanInstall));
        }
    }

    public bool CanInstall => !IsBusy;

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        IsBusy = true;
        ActionText = "正在安装…";
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(message => StatusText = message);
            OperationResult result = await _winget.TryInstallBundledAsync(progress, _cts.Token);
            StatusText = result.Message;
            if (result.Success)
            {
                ActionText = "更新完成";
                IsBusy = false;
                await Task.Delay(800);
                DialogResult = true;
                Close();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消更新，FreshStart 可以继续使用。";
        }
        catch (Exception ex)
        {
            StatusText = $"离线更新失败：{ex.Message}";
            Log.Warn($"winget 离线更新窗口失败: {ex.Message}");
        }
        finally
        {
            _busy = false;
            IsBusy = false;
            ActionText = "重试";
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Later_Click(object sender, RoutedEventArgs e)
    {
        CancelWork();
        Close();
    }

    private void CancelWork()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
