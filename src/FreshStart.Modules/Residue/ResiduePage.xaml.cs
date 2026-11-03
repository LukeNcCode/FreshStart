using System.Windows;

namespace FreshStart.Modules.Residue;

public partial class ResiduePage
{
    public ResiduePage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ResidueViewModel)?.LoadCommand.Execute(null);
    }
}
