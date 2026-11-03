using System.Windows;

namespace FreshStart.Modules.Firewall;

public partial class FirewallPage
{
    public FirewallPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as FirewallViewModel)?.LoadCommand.Execute(null);
    }
}
