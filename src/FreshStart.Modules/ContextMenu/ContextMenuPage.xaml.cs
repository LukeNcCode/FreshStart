using System.Windows;

namespace FreshStart.Modules.ContextMenu;

public partial class ContextMenuPage
{
    private ContextMenuViewModel? _viewModel;

    public ContextMenuPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ContextMenuViewModel viewModel) return;
        _viewModel = viewModel;
        viewModel.LoadCommand.Execute(null);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _viewModel?.Cancel();
        _viewModel = null;
    }
}
