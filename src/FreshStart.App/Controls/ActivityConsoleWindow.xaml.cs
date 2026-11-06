using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using FreshStart.App.Services;

namespace FreshStart.App.Controls;

public partial class ActivityConsoleWindow : Window
{
    private readonly ActivityConsoleService _console;

    public ActivityConsoleWindow(ActivityConsoleService console)
    {
        _console = console;
        InitializeComponent();
        DataContext = console;
        SourceInitialized += (_, _) => WindowCornerService.Apply(this);
        console.Lines.CollectionChanged += OnLinesChanged;
        Closed += (_, _) => console.Lines.CollectionChanged -= OnLinesChanged;
        Loaded += (_, _) => ScrollToEnd();
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add
            or NotifyCollectionChangedAction.Reset)
        {
            Dispatcher.BeginInvoke(ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void ScrollToEnd() => LogScroll.ScrollToEnd();

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
