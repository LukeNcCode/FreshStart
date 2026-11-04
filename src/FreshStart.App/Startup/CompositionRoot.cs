using FreshStart.App.Dialogs;
using FreshStart.App.Services;
using FreshStart.App.Shell;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Backup;
using FreshStart.Core.Operations;
using FreshStart.Core.Settings;
using FreshStart.Modules.Appx;
using FreshStart.Modules.Cleanup;
using FreshStart.Modules.ContextMenu;
using FreshStart.Modules.Defender;
using FreshStart.Modules.Driver;
using FreshStart.Modules.Execution;
using FreshStart.Modules.Features;
using FreshStart.Modules.Firewall;
using FreshStart.Modules.GameOptimize;
using FreshStart.Modules.Lan;
using FreshStart.Modules.Migration;
using FreshStart.Modules.History;
using FreshStart.Modules.Network;
using FreshStart.Modules.Performance;
using FreshStart.Modules.Power;
using FreshStart.Modules.Presets;
using FreshStart.Modules.Privacy;
using FreshStart.Modules.QuickActions;
using FreshStart.Modules.Residue;
using FreshStart.Modules.Restore;
using FreshStart.Modules.Score;
using FreshStart.Modules.ServiceTasks;
using FreshStart.Modules.Settings;
using FreshStart.Modules.Software;
using FreshStart.Modules.Startup;
using FreshStart.Modules.SystemInfo;
using FreshStart.Modules.UiTuning;
using FreshStart.Modules.Uninstall;
using FreshStart.Modules.Unattended;
using FreshStart.Modules.WindowsUpdate;
using FreshStart.Services.Appx;
using FreshStart.Services.Cleanup;
using FreshStart.Services.ContextMenu;
using FreshStart.Services.Defender;
using FreshStart.Services.Driver;
using FreshStart.Services.Features;
using FreshStart.Services.Firewall;
using FreshStart.Services.History;
using FreshStart.Services.Lan;
using FreshStart.Services.Migration;
using FreshStart.Services.Network;
using FreshStart.Services.Power;
using FreshStart.Services.Registry;
using FreshStart.Services.Residue;
using FreshStart.Services.Restore;
using FreshStart.Services.Score;
using FreshStart.Services.Startup;
using FreshStart.Services.Software;
using FreshStart.Services.System;
using FreshStart.Services.Tuning;
using FreshStart.Services.Uninstall;
using FreshStart.Services.Unattended;
using FreshStart.Services.WindowsUpdate;

namespace FreshStart.App.Startup;

/// <summary>
/// 手写组合根：不使用反射扫描/容器，启动路径可控且可预测。
/// 所有单例在这里创建，页面 ViewModel 由 NavigationService 懒加载。
/// </summary>
public sealed class CompositionRoot
{
    public SettingsService Settings { get; }
    public ThemeService Theme { get; }
    public MotionService Motion { get; }
    public ToastService Toast { get; }
    public DialogService Dialogs { get; }
    public RegistryService Registry { get; }
    public OperationRunner Runner { get; }
    public NavigationService Navigation { get; }
    public MainViewModel MainViewModel { get; }
    public UnattendedService Unattended { get; }
    public WingetService Winget { get; }

    public CompositionRoot(SettingsService settings, IOperationGate? gateOverride = null)
    {
        Settings = settings;
        Theme = new ThemeService(settings);
        Theme.ApplyInitial();

        Motion = new MotionService();
        Toast = new ToastService();
        Dialogs = new DialogService();
        Registry = new RegistryService();

        var backupStore = new JsonBackupStore();
        var history = new OperationHistoryStore();
        var restorePoints = new RestorePointService();
        IOperationGate gate = gateOverride ?? new OperationGate(Dialogs);

        // 全局写锁：OperationRunner 与少数不经过 Runner 的写路径（清理/残留/历史回滚）共用
        var writeLock = new FreshStart.Core.Threading.WriteLock();

        Runner = new OperationRunner(backupStore, gate, restorePoints, history, writeLock)
        {
            AskRestorePointOnFirstWrite = settings.Current.AskRestorePointOnFirstWrite
        };

        var systemInfo = new SystemInfoService();
        var quickActions = new QuickActionService();
        var contextMenu = new ContextMenuService(Registry);
        var scheduledTasks = new ScheduledTaskService();
        var serviceControl = new ServiceControlService();
        var startup = new StartupService(Registry, scheduledTasks, serviceControl);
        var windowsUpdate = new WindowsUpdateService(Registry, serviceControl, scheduledTasks);
        var defender = new DefenderService();
        var appx = new AppxService();
        var winget = new WingetService();
        Winget = winget;
        // Chocolatey 后端保留代码但不注册（0.29.0-beta 的实验；主线仍用 winget）
        var tuning = new TuningService(Registry);
        var network = new NetworkService();
        var power = new PowerService();
        var uninstall = new InstalledProgramService();
        var drivers = new DriverService();
        var firewall = new FirewallService();
        var features = new OptionalFeatureService();
        var cleanup = new CleanupService(writeLock);
        var restore = new GlobalRestoreService(Registry);
        // 首次启动要读 26 个调优键，放到线程池，避免拖慢 UI 线程上的启动路径
        _ = Task.Run(() => restore.EnsureBaselineAsync());
        var historyRestore = new HistoryRestoreService(Registry, serviceControl, writeLock);
        var lanShare = new LanShareService();
        var unattended = new UnattendedService(
            tuning, Registry, windowsUpdate, winget, uninstall, appx,
            serviceControl, scheduledTasks, network, power, cleanup, drivers, new ExplorerService(), defender, firewall, features);
        Unattended = unattended;
        var residue = new ResidueService(uninstall, Registry, writeLock);
        var score = new ScoreService(serviceControl, scheduledTasks, tuning, uninstall);
        var environmentMigration = new EnvironmentMigrationService(winget);

        MainViewModel = new MainViewModel(settings, Theme);

        var factories = new Dictionary<string, Func<object>>(StringComparer.OrdinalIgnoreCase)
        {
            ["home"] = () => new QuickActionsViewModel(quickActions, score, Toast, Dialogs),
            ["system-info"] = () => new SystemInfoViewModel(systemInfo, Toast),
            ["context-menu"] = () => new ContextMenuViewModel(contextMenu, Runner, Toast, Dialogs, quickActions),
            ["startup"] = () => new StartupViewModel(startup, Runner, Toast),
            ["windows-update"] = () => new WindowsUpdateViewModel(windowsUpdate, Runner, Toast),
            ["defender"] = () => new DefenderViewModel(
                defender, Runner, Toast, appx,
                new FirewallViewModel(firewall, Runner, Toast, Dialogs)),
            ["performance"] = () => new PerformanceViewModel(Toast, Dialogs),
            ["appx"] = () => new AppxViewModel(appx, defender, Runner, Toast, Dialogs),
            ["software"] = () => new SoftwareViewModel(winget, new DirectInstallerService(), Runner, Toast),
            ["presets"] = () => new PresetsViewModel(tuning, Runner, Toast),
            ["execution"] = () => new ExecutionCenterViewModel(
                new ScriptEditorViewModel(
                    unattended, tuning, serviceControl, scheduledTasks, network, power,
                    cleanup, drivers, appx, firewall, features, Toast),
                new UnattendedViewModel(unattended, Runner, Toast)),
            ["features"] = () => new FeaturesViewModel(features, Runner, Toast, Dialogs),
            ["privacy"] = () => new PrivacyViewModel(tuning, Runner, Toast),
            ["ui-tuning"] = () => new UiTuningViewModel(tuning, Runner, Toast),
            ["network"] = () => new NetworkViewModel(
                network, Runner, Toast,
                new LanShareViewModel(lanShare, Toast)),
            ["power"] = () => new PowerViewModel(power, tuning, Runner, Toast),
            ["service-tasks"] = () => new ServiceTasksViewModel(serviceControl, scheduledTasks, Runner, Toast),
            ["game-optimize"] = () => new GameOptimizeViewModel(tuning, Runner, Toast),
            ["uninstall"] = () => new UninstallViewModel(uninstall, residue, Runner, Toast, Dialogs),
            ["residue"] = () => new ResidueViewModel(residue, Dialogs, Toast),
            ["driver"] = () => new DriverViewModel(drivers, Runner, Toast, Dialogs),
            ["cleanup"] = () => new CleanupViewModel(cleanup, Dialogs, Toast),
            ["restore"] = () => new RestoreViewModel(restore, Runner, Toast, Dialogs),
            ["history"] = () => new HistoryViewModel(historyRestore, Dialogs, Toast),
            ["migration"] = () => new EnvironmentMigrationViewModel(environmentMigration, Toast, Dialogs),
            ["score"] = () => new ScoreViewModel(score, Toast),
            ["settings"] = () => new SettingsViewModel(
                settings, Theme, Motion, Toast, Dialogs, Runner, Registry,
                new FreshStart.Services.System.SystemWallpaperService())
        };

        Navigation = new NavigationService(factories, Toast);
    }

    public MainWindow CreateMainWindow(string initialPage)
    {
        var window = new MainWindow();
        window.Initialize(MainViewModel, Navigation, Settings, Dialogs, Toast, Theme, initialPage);
        return window;
    }
}
