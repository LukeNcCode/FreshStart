using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace FreshStart.Modules.SystemInfo;

public partial class SystemInfoPage
{
    private SystemInfoViewModel? _viewModel;

    public SystemInfoPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemInfoViewModel viewModel) return;

        if (!ReferenceEquals(_viewModel, viewModel))
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel = viewModel;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        viewModel.LoadCommand.Execute(null);
        Dispatcher.BeginInvoke(() => RootScroll.ScrollToTop(), DispatcherPriority.Loaded);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 骨架切换为数据时，强制回到页面顶部，避免焦点导致 ScrollViewer 偏移
        if (e.PropertyName == nameof(SystemInfoViewModel.IsLoading) && _viewModel is { IsLoading: false })
        {
            Dispatcher.BeginInvoke(() => RootScroll.ScrollToTop(), DispatcherPriority.Background);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Cancel();
        }
    }
}
