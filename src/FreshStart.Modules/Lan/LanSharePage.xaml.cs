using System.Windows;

namespace FreshStart.Modules.Lan;

public partial class LanSharePage
{
    public LanSharePage()
    {
        InitializeComponent();
        Unloaded += (_, _) => (DataContext as LanShareViewModel)?.StopCommand.Execute(null);
    }
}
