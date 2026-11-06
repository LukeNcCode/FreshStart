using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FreshStart.App.Animation;
using FreshStart.Core;
using FreshStart.Core.UI;

namespace FreshStart.App.Startup;

/// <summary>
/// 首次启动的欢迎引导：介绍工作台、说明安全边界并展示启动环境状态。
/// 由设置中的欢迎版本控制，产品更新后可重新展示一次。
/// </summary>
public partial class WelcomeView : UserControl
{
    private readonly bool _isAdministrator;
    private readonly string _wingetVersion;
    private readonly string _backupDirectory;
    private int _page;

    public WelcomeView(bool isAdministrator, string wingetVersion, string backupDirectory)
    {
        InitializeComponent();
        _isAdministrator = isAdministrator;
        _wingetVersion = wingetVersion;
        _backupDirectory = backupDirectory;

        VersionText.Text = $"v{GetDisplayVersion()} · 本地开荒工具";
        AdministratorText.Text = isAdministrator ? "已以管理员身份运行" : "标准权限（部分操作会请求提权）";
        AdministratorText.Foreground = (Brush)FindResource(
            isAdministrator ? "SuccessBrush" : "WarningBrush");
        WingetText.Text = string.IsNullOrWhiteSpace(wingetVersion)
            ? "未检测到 winget"
            : $"v{wingetVersion}";
        BackupText.Text = backupDirectory;

        Loaded += (_, _) =>
        {
            Focus();
            PlayIntro();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        ShowPage(0);
    }

    public event EventHandler? Completed;

    private void ShowPage(int page, int direction = 1)
    {
        var next = Math.Clamp(page, 0, 2);
        var incoming = next switch
        {
            0 => Page1,
            1 => Page2,
            _ => Page3
        };
        var outgoing = _page switch
        {
            0 => Page1,
            1 => Page2,
            _ => Page3
        };
        _page = next;

        incoming.Visibility = Visibility.Visible;
        if (!ReferenceEquals(incoming, outgoing))
        {
            AnimatePageChange(incoming, outgoing, direction);
        }
        else
        {
            incoming.Opacity = 1;
        }

        PageIndex.Text = $"第 {_page + 1} 步，共 3 步";
        BackButton.Visibility = _page == 0 ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Content = _page == 2 ? "开始使用" : "下一步";

        SetStepState(0, StepPill1, StepDot1, StepNumber1, StepTitle1);
        SetStepState(1, StepPill2, StepDot2, StepNumber2, StepTitle2);
        SetStepState(2, StepPill3, StepDot3, StepNumber3, StepTitle3);
    }

    private void PlayIntro()
    {
        var duration = MotionPreferences.Duration(280);
        if (duration <= TimeSpan.Zero)
        {
            LayoutRoot.Opacity = 1;
            RootScale.ScaleX = 1;
            RootScale.ScaleY = 1;
            RootTranslate.Y = 0;
            return;
        }

        var easing = new CubicBezierEase();
        LayoutRoot.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(duration)) { EasingFunction = easing });
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.99, 1, new Duration(duration)) { EasingFunction = easing });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.99, 1, new Duration(duration)) { EasingFunction = easing });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, new Duration(duration)) { EasingFunction = easing });

        AnimateOrb(HeroOrb1, -10, TimeSpan.FromSeconds(3.6));
        AnimateOrb(HeroOrb2, 8, TimeSpan.FromSeconds(4.4));
    }

    private static void AnimateOrb(FrameworkElement orb, double distance, TimeSpan duration)
    {
        var translate = new TranslateTransform();
        orb.RenderTransform = translate;
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(0, distance, new Duration(duration))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private static void AnimatePageChange(
        FrameworkElement incoming,
        FrameworkElement outgoing,
        int direction)
    {
        var duration = MotionPreferences.Duration(210);
        var translate = new TranslateTransform(direction * 16, 0);
        incoming.RenderTransform = translate;
        if (duration <= TimeSpan.Zero)
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            incoming.Opacity = 1;
            translate.X = 0;
            return;
        }

        var easing = new CubicBezierEase();
        incoming.Opacity = 0;
        incoming.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, new Duration(duration)) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 16, 0, new Duration(duration))
            {
                EasingFunction = easing
            });

        var exitTranslate = new TranslateTransform();
        outgoing.RenderTransform = exitTranslate;
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(120)))
        {
            EasingFunction = easing
        };
        fade.Completed += (_, _) =>
        {
            outgoing.Visibility = Visibility.Collapsed;
            outgoing.Opacity = 1;
            outgoing.RenderTransform = Transform.Identity;
        };
        outgoing.BeginAnimation(OpacityProperty, fade);
        exitTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, direction * -10, new Duration(TimeSpan.FromMilliseconds(120)))
            {
                EasingFunction = easing
            });
    }

    private void SetStepState(
        int index,
        Border pill,
        Border dot,
        TextBlock number,
        TextBlock title)
    {
        var active = index == _page;
        var completed = index < _page;
        pill.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;
        pill.BorderBrush = active
            ? new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF))
            : Brushes.Transparent;
        dot.Background = active
            ? (Brush)FindResource("AccentGradientBrush")
            : completed
                ? new SolidColorBrush(Color.FromArgb(0x2B, 0x34, 0xD3, 0x99))
                : new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        number.Foreground = active || completed
            ? Brushes.White
            : (Brush)FindResource("HeroMutedTextBrush");
        title.Foreground = active
            ? (Brush)FindResource("HeroTextBrush")
            : completed
                ? (Brush)FindResource("SuccessBrush")
                : (Brush)FindResource("HeroMutedTextBrush");

        if (!active) return;
        var duration = MotionPreferences.Duration(180);
        if (duration <= TimeSpan.Zero) return;
        dot.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = dot.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        dot.RenderTransform = scale;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.86, 1, new Duration(duration))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.86, 1, new Duration(duration))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            });
    }

    private static string GetDisplayVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informational)
            ? "0.2.0"
            : informational.Split('+')[0];
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowPage(_page - 1, -1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page >= 2)
        {
            Complete();
            return;
        }

        ShowPage(_page + 1, 1);
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
        Complete();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Exit_Click(sender, e);
        if (e.Key == Key.Right && _page < 2) ShowPage(_page + 1, 1);
        if (e.Key == Key.Left && _page > 0) ShowPage(_page - 1, -1);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            Window.GetWindow(this)?.DragMove();
        }
    }

    private void Complete()
    {
        var duration = MotionPreferences.Duration(230);
        if (duration <= TimeSpan.Zero)
        {
            Completed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var easing = new CubicBezierEase();
        var fade = new DoubleAnimation(LayoutRoot.Opacity, 0, new Duration(duration))
        {
            EasingFunction = easing
        };
        fade.Completed += (_, _) => Completed?.Invoke(this, EventArgs.Empty);
        LayoutRoot.BeginAnimation(OpacityProperty, fade);
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(RootScale.ScaleX, 0.985, new Duration(duration)) { EasingFunction = easing });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(RootScale.ScaleY, 0.985, new Duration(duration)) { EasingFunction = easing });
    }

    /// <summary>当前状态摘要，供日志/诊断使用。</summary>
    public string Summary => $"admin={_isAdministrator}, winget={_wingetVersion}";
}
