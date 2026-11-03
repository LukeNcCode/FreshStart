using System.Windows;

namespace FreshStart.Modules.Privacy;

public partial class PrivacyPage
{
    public PrivacyPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as PrivacyViewModel)?.LoadCommand.Execute(null);
    }
}
