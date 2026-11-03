using System.Windows;

namespace FreshStart.Modules.UiTuning;

public partial class UiTuningPage
{
    public UiTuningPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as UiTuningViewModel)?.LoadCommand.Execute(null);
    }
}
