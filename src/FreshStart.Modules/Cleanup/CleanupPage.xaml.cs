using System.Windows;

namespace FreshStart.Modules.Cleanup;

public partial class CleanupPage
{
    public CleanupPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as CleanupViewModel)?.LoadCommand.Execute(null);
    }
}
