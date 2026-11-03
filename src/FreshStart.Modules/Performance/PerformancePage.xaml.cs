using System.Windows;

namespace FreshStart.Modules.Performance;

public partial class PerformancePage
{
    public PerformancePage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as PerformanceViewModel)?.Start();
        Unloaded += (_, _) => (DataContext as PerformanceViewModel)?.Stop();
    }
}
