using System.Windows;

namespace FreshStart.Modules.WindowsUpdate;

public partial class WindowsUpdatePage
{
    public WindowsUpdatePage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as WindowsUpdateViewModel)?.LoadCommand.Execute(null);
    }
}
