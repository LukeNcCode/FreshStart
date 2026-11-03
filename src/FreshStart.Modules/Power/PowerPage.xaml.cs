using System.Windows;

namespace FreshStart.Modules.Power;

public partial class PowerPage
{
    public PowerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as PowerViewModel)?.LoadCommand.Execute(null);
    }
}
