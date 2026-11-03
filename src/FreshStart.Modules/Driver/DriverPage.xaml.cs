using System.Windows;

namespace FreshStart.Modules.Driver;

public partial class DriverPage
{
    public DriverPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as DriverViewModel)?.LoadCommand.Execute(null);
    }
}
