using System.Windows;

namespace FreshStart.Modules.Defender;

public partial class DefenderPage
{
    public DefenderPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as DefenderViewModel)?.LoadCommand.Execute(null);
    }
}
