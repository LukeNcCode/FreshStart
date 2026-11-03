using System.Windows;

namespace FreshStart.Modules.Startup;

public partial class StartupPage
{
    public StartupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as StartupViewModel)?.LoadCommand.Execute(null);
        Unloaded += (_, _) => (DataContext as StartupViewModel)?.Cancel();
    }
}
