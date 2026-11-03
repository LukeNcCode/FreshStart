using System.Windows;

namespace FreshStart.Modules.Features;

public partial class FeaturesPage
{
    public FeaturesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as FeaturesViewModel)?.LoadCommand.Execute(null);
    }
}
