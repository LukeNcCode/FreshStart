using System.Windows;
using System.Windows.Threading;

namespace FreshStart.Modules.Software;

public partial class SoftwarePage
{
    public SoftwarePage()
    {
        InitializeComponent();
        // 先完成页面切换与首帧，再开始加载，避免导航动画被首次模板实例化拖住
        Loaded += (_, _) => Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => (DataContext as SoftwareViewModel)?.LoadCommand.Execute(null)));
        Unloaded += (_, _) => (DataContext as SoftwareViewModel)?.Cancel();
    }
}
