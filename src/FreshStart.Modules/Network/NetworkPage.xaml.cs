using System.Windows;

namespace FreshStart.Modules.Network;

public partial class NetworkPage
{
    public NetworkPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as NetworkViewModel)?.LoadCommand.Execute(null);
    }
}
