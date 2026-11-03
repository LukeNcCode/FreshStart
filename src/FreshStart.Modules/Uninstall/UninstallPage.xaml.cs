using System.Windows;

namespace FreshStart.Modules.Uninstall;

public partial class UninstallPage
{
    public UninstallPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as UninstallViewModel)?.LoadCommand.Execute(null);
    }
}
