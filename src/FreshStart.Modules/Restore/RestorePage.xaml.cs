using System.Windows;

namespace FreshStart.Modules.Restore;

public partial class RestorePage
{
    public RestorePage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as RestoreViewModel)?.LoadCommand.Execute(null);
    }
}
