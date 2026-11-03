using System.Windows;

namespace FreshStart.Modules.Presets;

public partial class PresetsPage
{
    public PresetsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as PresetsViewModel)?.LoadCommand.Execute(null);
    }
}
