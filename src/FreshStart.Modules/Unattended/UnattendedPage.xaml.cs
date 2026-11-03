using System.Windows;

namespace FreshStart.Modules.Unattended;

public partial class UnattendedPage
{
    public UnattendedPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as UnattendedViewModel)?.LoadCommand.Execute(null);
    }
}
