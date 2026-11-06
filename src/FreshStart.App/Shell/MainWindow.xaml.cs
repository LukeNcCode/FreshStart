using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using FreshStart.App.Animation;
using FreshStart.App.Dialogs;
using FreshStart.App.Services;
using FreshStart.App.Startup;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Diagnostics;
using FreshStart.Core.Settings;
using FreshStart.Core.UI;

namespace FreshStart.App.Shell;

public partial class MainWindow
{
    private SettingsService _settings = null!;
    private MainViewModel _viewModel = null!;
    private INavigationService _navigation = null!;
    private IThemeService _theme = null!;
    private bool _hasShownPage;
    private bool _closed;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            WindowMaximizeFix.Attach(this);
            WindowCornerService.Apply(this);
        };
        StateChanged += (_, _) => UpdateMaximizeVisual();
    }

    public void Initialize(
        MainViewModel viewModel,
        INavigationService navigation,
        SettingsService settings,
        DialogService dialogs,
        ToastService toasts,
        IThemeService theme,
        string initialPage)
    {
        _viewModel = viewModel;
        _navigation = navigation;
        _settings = settings;
        _theme = theme;

        DataContext = viewModel;
        dialogs.Attach(ModalHostControl);
        toasts.Attach(ToastHostControl, Dispatcher);

        _navigation.Navigated += OnNavigated;
        SidebarControl.ItemActivated += OnSidebarItemActivated;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        SidebarControl.SetCollapsed(viewModel.IsSidebarCollapsed);
        SidebarRoot.Width = viewModel.IsSidebarCollapsed ? 72 : 248;
        UpdateCollapseGlyph();
        ApplyWindowBounds();

        // 上次停留的页面可能在新版本里被移除（例如已下线的模块），静默回落到首页
        if (FreshStart.Modules.Navigation.NavCatalog.Find(initialPage) is null)
        {
            initialPage = "home";
        }

        if (!_navigation.Navigate(initialPage))
        {
            _navigation.Navigate("home");
        }

        Loaded += OnLoaded;
        ContentRendered += OnContentRendered;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    public void RestoreAndActivate()
    {
        if (_closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowActivation.SetForegroundWindow(hwnd);
    }

    public async Task ShowStartupIntroAsync()
    {
        if (_viewModel.IsSidebarCollapsed)
        {
            _viewModel.ToggleSidebar();
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var intro = new StartupIntroView();
        intro.Completed += (_, _) => completion.TrySetResult(null);
        intro.ExitStarted += (_, _) => BeginContentReveal();
        intro.TargetWordmarkProvider = () =>
        {
            var target = SidebarControl.GetBrandWordmarkBounds(WindowFrame);
            if (target.IsEmpty) return null;
            var origin = intro.TransformToAncestor(WindowFrame).Transform(new Point(0, 0));
            return new Rect(
                target.X - origin.X,
                target.Y - origin.Y,
                target.Width,
                target.Height);
        };

        StartupIntroHost.Children.Clear();
        StartupIntroHost.Children.Add(intro);
        StartupIntroHost.Visibility = Visibility.Visible;
        SidebarRoot.Opacity = 0;
        TitleBar.Opacity = 0;
        ContentHost.Opacity = 0;

        await completion.Task.ConfigureAwait(true);
        StartupIntroHost.Children.Clear();
        StartupIntroHost.Visibility = Visibility.Collapsed;
        if (!intro.HasMotion)
        {
            SidebarRoot.Opacity = 1;
            TitleBar.Opacity = 1;
            ContentHost.Opacity = 1;
        }
        PageHostControl.Focus();
    }

    public void CancelStartupIntro()
    {
        StartupIntroHost.Children.Clear();
        StartupIntroHost.Visibility = Visibility.Collapsed;
        SidebarRoot.Opacity = 1;
        TitleBar.Opacity = 1;
        ContentHost.Opacity = 1;
    }

    private void BeginContentReveal()
    {
        foreach (var element in new FrameworkElement[] { SidebarRoot, TitleBar, ContentHost })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        }
    }

    private void ApplyWindowBounds()
    {
        var current = _settings.Current;
        Width = Math.Max(MinWidth, current.WindowWidth);
        Height = Math.Max(MinHeight, current.WindowHeight);

        if (double.IsNaN(current.WindowLeft) || double.IsNaN(current.WindowTop)) return;
        if (!current.WindowPositionSaved) return;

        var saved = new Rect(current.WindowLeft, current.WindowTop, Width, Height);
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        if (virtualScreen.IntersectsWith(saved))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = current.WindowLeft;
            Top = current.WindowTop;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartupProbe.Mark("window-loaded");
        if (_settings.Current.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        StartupProbe.Mark("first-frame");
        Dispatcher.BeginInvoke(() => StartupProbe.Mark("interactive"), DispatcherPriority.Input);
    }

    private void OnNavigated(object? sender, NavigationChangedEventArgs e)
    {
        PageHostControl.Content = e.ViewModel;
        _viewModel.SetTitle(e.Title);
        SidebarControl.Select(e.Key, animate: _hasShownPage);
        _hasShownPage = true;
        _settings.Current.LastPage = e.Key;
    }

    private void OnSidebarItemActivated(object? sender, string key)
    {
        _navigation.Navigate(key);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCollapsed))
        {
            AnimateSidebar();
            UpdateCollapseGlyph();
        }
    }

    private void AnimateSidebar()
    {
        var collapsed = _viewModel.IsSidebarCollapsed;
        var target = collapsed ? 72d : 248d;
        SidebarControl.SetCollapsed(collapsed);

        var duration = MotionPreferences.Duration(200);
        if (duration <= TimeSpan.Zero)
        {
            SidebarRoot.Width = target;
            return;
        }

        var animation = new DoubleAnimation(SidebarRoot.ActualWidth, target, new Duration(duration))
        {
            EasingFunction = new CubicBezierEase()
        };
        animation.Completed += (_, _) =>
        {
            SidebarRoot.Width = target;
            SidebarRoot.BeginAnimation(WidthProperty, null);
        };
        SidebarRoot.BeginAnimation(WidthProperty, animation);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSidebar();

    private void UpdateCollapseGlyph()
    {
        if (CollapseGlyph is null) return;
        CollapseGlyph.Data = (Geometry)FindResource(_viewModel.IsSidebarCollapsed ? "ChevronRightIcon" : "ChevronLeftIcon");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* 窗口状态切换瞬间可能抛异常 */ }
        }
    }

    /// <summary>空白区域也可拖拽整窗；控件（按钮、滚动条、输入）上的点击不触发拖动。</summary>
    private void Background_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<ButtonBase>(source) is not null) return;
        if (FindAncestor<ScrollBar>(source) is not null) return;
        if (FindAncestor<Controls.ModalHost>(source) is not null) return;

        try { DragMove(); }
        catch (InvalidOperationException) { /* ignore */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeVisual()
    {
        if (MaxButton is null) return;
        var maximized = WindowState == WindowState.Maximized;
        WindowFrame.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(13);
        WindowFrame.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null) chrome.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(13);
        MaxButton.Tag = FindResource(maximized ? "RestoreGlyphIcon" : "MaxGlyphIcon");
        MaxButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!ModalHostControl.IsOpen) return;

        if (e.Key == Key.Escape)
        {
            ModalHostControl.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ModalHostControl.ConfirmPrimary();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        try
        {
            var bounds = WindowState is WindowState.Maximized or WindowState.Minimized
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);

            if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
            {
                _settings.Current.WindowLeft = bounds.Left;
                _settings.Current.WindowTop = bounds.Top;
                _settings.Current.WindowWidth = bounds.Width;
                _settings.Current.WindowHeight = bounds.Height;
                _settings.Current.WindowPositionSaved = true;
            }
            _settings.Current.WindowMaximized = WindowState == WindowState.Maximized;
            _settings.SaveNow();

            StartupProbe.WriteFile(Path.Combine(AppPaths.CacheRoot, "last-startup.json"),
                new Dictionary<string, object?>
                {
                    ["page"] = _navigation.CurrentKey,
                    ["theme"] = _theme.IsDark ? "Dark" : "Light",
                    ["workingSetMb"] = Environment.WorkingSet / 1024d / 1024d
                });
        }
        catch (Exception ex)
        {
            Log.Error("保存窗口状态失败", ex);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _navigation.Navigated -= OnNavigated;
        try { Application.Current?.Shutdown(); } catch { /* ignore */ }
        // 某些单文件 + WPF 组合会在关闭时抛 System.Diagnostics.Tracing 缺失，
        // 导致进程留在后台并持续接收二次启动请求；兜底强制退出。
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => Environment.Exit(0)));
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T result) return result;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
