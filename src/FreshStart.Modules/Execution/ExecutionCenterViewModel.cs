using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Modules.Unattended;
using FreshStart.Services.Unattended;

namespace FreshStart.Modules.Execution;

/// <summary>
/// 用户级执行中心：配置工作流、生成执行计划、运行、查看报告集中在一个入口。
/// 底层继续复用 UnattendedService、CompositeOperation、OperationRunner 和旧方案格式。
/// </summary>
public sealed partial class ExecutionCenterViewModel : ObservableObject
{
    public ExecutionCenterViewModel(
        ScriptEditorViewModel builder,
        UnattendedViewModel runner)
    {
        Builder = builder;
        Runner = runner;
        Builder.JobPrepared += OnJobPrepared;
    }

    public ScriptEditorViewModel Builder { get; }
    public UnattendedViewModel Runner { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string _statusText = "在一个连续工作流中配置、预览并执行系统变更";

    private void OnJobPrepared(object? sender, FreshStart.Services.Unattended.UnattendedJob job)
    {
        Runner.LoadJob(job);
        SelectedTabIndex = 1;
        StatusText = $"已生成执行计划：{job.Name} · {job.Steps.Count} 步";
    }

    [RelayCommand]
    private void NewBlank()
    {
        Builder.ClearRecordedCommand.Execute(null);
        SelectedTabIndex = 0;
        StatusText = "已新建空白工作流";
    }

    [RelayCommand]
    private void UseMinimalTemplate() => ApplyTemplate(
        "清爽新机",
        "隐私与界面精简、任务栏瘦身、常用软件和基础搜索工具一次配置",
        new UnattendedStep { Kind = "preset", Target = "minimal", Name = "极简清爽" },
        new UnattendedStep { Kind = "tuning", Target = "taskbar-left", Name = "任务栏左对齐" },
        new UnattendedStep { Kind = "tuning", Target = "widgets", Name = "隐藏小组件按钮" },
        new UnattendedStep { Kind = "tuning", Target = "taskview", Name = "隐藏任务视图按钮" },
        new UnattendedStep { Kind = "tuning", Target = "bing-search", Name = "关闭搜索网络建议" },
        new UnattendedStep { Kind = "tuning", Target = "recent-files", Name = "关闭最近使用文件" },
        new UnattendedStep { Kind = "update", Target = "normal", Name = "恢复正常更新" },
        Software("7zip.7zip", "7-Zip"),
        Software("Google.Chrome", "Google Chrome"),
        Software("voidtools.Everything", "Everything"));

    [RelayCommand]
    private void UsePrivacyTemplate() => ApplyTemplate(
        "隐私优先",
        "减少遥测、后台上报和推荐内容，配置隐私浏览器并暂停自动更新",
        new UnattendedStep { Kind = "preset", Target = "privacy", Name = "隐私优先" },
        Service("DiagTrack", "诊断跟踪服务", "disabled"),
        Service("dmwappushservice", "设备管理 WAP 推送服务", "disabled"),
        new UnattendedStep { Kind = "update", Target = "paused", Name = "暂停更新 7 天" },
        Software("Mozilla.Firefox", "Mozilla Firefox"));

    [RelayCommand]
    private void UseDeveloperTemplate() => ApplyTemplate(
        "开发环境",
        "减少打扰、启用长路径，准备 WSL/虚拟机平台并安装常用开发软件",
        new UnattendedStep { Kind = "preset", Target = "developer", Name = "开发环境" },
        Feature("Microsoft-Windows-Subsystem-Linux", "WSL"),
        Feature("VirtualMachinePlatform", "虚拟机平台"),
        Feature("OpenSSH.Client~~~~0.0.1.0", "OpenSSH 客户端"),
        Software("7zip.7zip", "7-Zip"),
        Software("Google.Chrome", "Google Chrome"),
        Software("Microsoft.VisualStudioCode", "Visual Studio Code"),
        Software("Git.Git", "Git"),
        Software("Microsoft.WindowsTerminal", "Windows Terminal"),
        Power("381b4222-f694-41f0-9685-ff5bb260df2e", "平衡"));

    [RelayCommand]
    private void UseGamingTemplate() => ApplyTemplate(
        "游戏优先",
        "关闭后台录制与干扰，启用游戏模式和 GPU 调度，切换到高性能电源",
        new UnattendedStep { Kind = "preset", Target = "gaming", Name = "游戏优先" },
        new UnattendedStep { Kind = "tuning", Target = "gamemode", Name = "启用游戏模式" },
        new UnattendedStep { Kind = "tuning", Target = "hags", Name = "硬件加速 GPU 调度" },
        Power("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "高性能"),
        Software("7zip.7zip", "7-Zip"),
        Software("Google.Chrome", "Google Chrome"),
        Software("OBSProject.OBSStudio", "OBS Studio"));

    private void ApplyTemplate(string name, string description, params UnattendedStep[] steps)
    {
        if (Builder.IsBusy)
        {
            StatusText = "正在读取系统状态，请稍后再应用模板";
            return;
        }

        Builder.LoadJob(new UnattendedJob
        {
            SchemaVersion = 3,
            Key = "template." + Guid.NewGuid().ToString("N")[..8],
            Name = name,
            Description = description,
            RestartExplorer = true,
            ContinueOnError = true,
            Steps = UnattendedService.OrderSteps(steps).ToList()
        });
        SelectedTabIndex = 0;
        StatusText = $"已载入模板「{name}」：{steps.Length} 个起始步骤，可继续调整";
    }

    private static UnattendedStep Software(string id, string name)
        => new() { Kind = "install", Target = id, Name = name };

    private static UnattendedStep Feature(string name, string displayName)
        => new() { Kind = "feature", Target = name, Name = displayName, Apply = true };

    private static UnattendedStep Service(string name, string displayName, string mode)
        => new() { Kind = "service", Target = name, Name = displayName, Value = mode };

    private static UnattendedStep Power(string guid, string name)
        => new() { Kind = "power", Target = guid, Name = name };
}
