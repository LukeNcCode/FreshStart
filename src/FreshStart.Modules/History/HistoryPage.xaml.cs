using System.Windows;

namespace FreshStart.Modules.History;

public partial class HistoryPage
{
    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as HistoryViewModel)?.LoadCommand.Execute(null);
    }
}
