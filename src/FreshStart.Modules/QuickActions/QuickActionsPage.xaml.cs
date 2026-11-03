using System.Windows.Controls;

namespace FreshStart.Modules.QuickActions;

public partial class QuickActionsPage
{
    public QuickActionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is QuickActionsViewModel vm) _ = vm.LoadScoreAsync();
        };
    }
}
