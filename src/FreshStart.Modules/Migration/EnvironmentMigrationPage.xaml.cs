using System.Windows.Controls;

namespace FreshStart.Modules.Migration;

public partial class EnvironmentMigrationPage : UserControl
{
    public EnvironmentMigrationPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is EnvironmentMigrationViewModel viewModel)
            {
                await viewModel.LoadAsync();
            }
        };
    }
}
