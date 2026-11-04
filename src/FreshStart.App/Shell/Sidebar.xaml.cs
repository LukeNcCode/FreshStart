using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FreshStart.App.Animation;
using FreshStart.App.Services;
using FreshStart.Core.UI;

namespace FreshStart.App.Shell;

public partial class Sidebar
{
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectedKey;

    public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
        nameof(IsCollapsed), typeof(bool), typeof(Sidebar),
        new PropertyMetadata(false, OnIsCollapsedChanged));

    public Sidebar()
    {
        InitializeComponent();
        VersionText.Text = GetVersionText();
        NavSearchPlaceholder.Visibility = Visibility.Visible;
        DataContextChanged += (_, _) => Dispatcher.BeginInvoke(RepositionPill,
            DispatcherPriority.Loaded);
    }

    private static string GetVersionText()
    {
        var assembly = Assembly.GetEntryAssembly();
        var informational = assembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];
        var version = assembly?.GetName().Version;
        var text = !string.IsNullOrWhiteSpace(informational)
            ? informational
            : version is null ? "dev" : $"{version.Major}.{version.Minor}.{version.Build}";
        return $"v{text} · 本地开荒工具";
    }

    public bool IsCollapsed
    {
        get => (bool)GetValue(IsCollapsedProperty);
        set => SetValue(IsCollapsedProperty, value);
    }

    /// <summary>点击任一导航项（包括未实现的模块，由上层提示）。</summary>
    public event EventHandler<string>? ItemActivated;

    public void Select(string key, bool animate)
    {
        _selectedKey = key;
        if (DataContext is MainViewModel viewModel) viewModel.SetSelected(key);

        Dispatcher.BeginInvoke(() =>
        {
            if (_buttons.TryGetValue(key, out var button))
            {
                // 导航到列表下方的页面时，让侧边栏把选中项滚进视野
                button.BringIntoView();
                PositionPill(button, animate);
            }
        }, DispatcherPriority.Loaded);
    }

    public void SetCollapsed(bool collapsed) => IsCollapsed = collapsed;

    private void NavButton_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not SidebarItem item) return;
        _buttons[item.Key] = button;

        if (string.Equals(item.Key, _selectedKey, StringComparison.OrdinalIgnoreCase))
        {
            PositionPill(button, animate: false);
        }
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SidebarItem item })
        {
            ItemActivated?.Invoke(this, item.Key);
        }
    }

    private void NavSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        NavSearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(NavSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        Dispatcher.BeginInvoke(() =>
        {
            if (_selectedKey is not null
                && _buttons.TryGetValue(_selectedKey, out var button)
                && button.IsVisible)
            {
                PositionPill(button, animate: false);
            }
            else
            {
                SelectionPill.Visibility = Visibility.Collapsed;
            }
        }, DispatcherPriority.Loaded);
    }

    private static void OnIsCollapsedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Sidebar sidebar)
        {
            sidebar.ApplyCollapsedLayout();
        }
    }

    /// <summary>
    /// 收起时显式收紧导航内容宽度并复位水平滚动。
    /// 只动画外层 Width 会让 ScrollViewer 保留旧的横向布局宽度，导致图标被裁掉。
    /// </summary>
    private void ApplyCollapsedLayout()
    {
        var contentWidth = IsCollapsed ? 72d : 248d;
        GroupItems.Width = contentWidth;
        GroupItems.HorizontalAlignment = HorizontalAlignment.Left;
        // 收起时隐藏滚动条（仍可滚轮滚动），给 64px 图标栏留出完整宽度
        NavScroll.VerticalScrollBarVisibility = IsCollapsed ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        NavScroll.ScrollToHorizontalOffset(0);
        NavScroll.UpdateLayout();
        Dispatcher.BeginInvoke(RepositionPill, DispatcherPriority.Loaded);
    }

    private void RepositionPill()
    {
        if (_selectedKey is not null && _buttons.TryGetValue(_selectedKey, out var button))
        {
            PositionPill(button, animate: false);
        }
    }

    private void PositionPill(Button target, bool animate)
    {
        if (!target.IsLoaded || !target.IsVisible) return;
        var point = target.TranslatePoint(new Point(0, 0), PillCanvas);
        if (double.IsNaN(point.Y) || target.ActualWidth <= 0) return;

        SelectionPill.Visibility = Visibility.Visible;
        var duration = MotionPreferences.Duration(200);
        var left = point.X;
        var top = point.Y;
        var height = target.ActualHeight;
        var width = target.ActualWidth;

        if (!animate || duration <= TimeSpan.Zero)
        {
            Canvas.SetLeft(SelectionPill, left);
            Canvas.SetTop(SelectionPill, top);
            SelectionPill.Height = height;
            SelectionPill.Width = width;
            return;
        }

        var easing = new CubicBezierEase();
        var currentLeft = Canvas.GetLeft(SelectionPill);
        if (double.IsNaN(currentLeft))
        {
            Canvas.SetLeft(SelectionPill, left);
            currentLeft = left;
        }
        var currentTop = Canvas.GetTop(SelectionPill);
        if (double.IsNaN(currentTop))
        {
            Canvas.SetTop(SelectionPill, top);
            currentTop = top;
        }

        SelectionPill.BeginAnimation(Canvas.LeftProperty,
            new DoubleAnimation(currentLeft, left, new Duration(duration)) { EasingFunction = easing });
        SelectionPill.BeginAnimation(Canvas.TopProperty,
            new DoubleAnimation(currentTop, top, new Duration(duration)) { EasingFunction = easing });
        SelectionPill.BeginAnimation(HeightProperty,
            new DoubleAnimation(SelectionPill.ActualHeight, height, new Duration(duration)) { EasingFunction = easing });
        SelectionPill.BeginAnimation(WidthProperty,
            new DoubleAnimation(SelectionPill.ActualWidth, width, new Duration(duration)) { EasingFunction = easing });
    }
}
