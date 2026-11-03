using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using FreshStart.Core;

namespace FreshStart.App.Startup;

/// <summary>
/// 首次启动的新手引导：三步讲清「怎么用」「有哪些风险」「环境是否就绪」。
/// 只在 <c>AppSettings.HasSeenWelcome</c> 为 false 时由 App 调起；设置页可重新打开。
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly bool _isAdministrator;
    private readonly string _wingetVersion;
    private readonly string _backupDirectory;
    private int _page;

    public WelcomeWindow(bool isAdministrator, string wingetVersion, string backupDirectory)
    {
        InitializeComponent();
        _isAdministrator = isAdministrator;
        _wingetVersion = wingetVersion;
        _backupDirectory = backupDirectory;

        AdministratorText.Text = isAdministrator ? "已以管理员身份运行" : "未以管理员运行（部分操作会失败）";
        AdministratorText.Foreground = (System.Windows.Media.Brush)FindResource(
            isAdministrator ? "SuccessBrush" : "WarningBrush");
        WingetText.Text = string.IsNullOrWhiteSpace(wingetVersion) ? "未检测到 winget" : $"winget: {wingetVersion}";
        BackupText.Text = backupDirectory;

        Loaded += (_, _) => Activate();
        ShowPage(0);
    }

    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, 2);
        Page1.Visibility = _page == 0 ? Visibility.Visible : Visibility.Collapsed;
        Page2.Visibility = _page == 1 ? Visibility.Visible : Visibility.Collapsed;
        Page3.Visibility = _page == 2 ? Visibility.Visible : Visibility.Collapsed;

        PageIndex.Text = $"{_page + 1} / 3";
        BackButton.Visibility = _page == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _page == 2 ? "开始使用" : "下一步";

        // 第三步才需要体检结论，提前算一次也无害
        if (_page == 2)
        {
            BackupText.Text = _backupDirectory;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(_page - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page >= 2)
        {
            DialogResult = true;
            Close();
            return;
        }

        ShowPage(_page + 1);
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_backupDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_backupDirectory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开备份目录失败: {ex.Message}");
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    /// <summary>当前状态摘要，供日志/诊断使用。</summary>
    public string Summary => $"admin={_isAdministrator}, winget={_wingetVersion}";
}
