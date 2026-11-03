using System.Windows;

namespace FreshStart.Modules.ServiceTasks;

public partial class ServiceTasksPage
{
    public ServiceTasksPage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as ServiceTasksViewModel)?.LoadCommand.Execute(null);
    }
}
