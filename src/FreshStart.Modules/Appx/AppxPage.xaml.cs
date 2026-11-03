using System.Windows;

namespace FreshStart.Modules.Appx;

public partial class AppxPage
{
    public AppxPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as AppxViewModel)?.LoadCommand.Execute(null);
        Unloaded += (_, _) => (DataContext as AppxViewModel)?.Cancel();
    }
}
