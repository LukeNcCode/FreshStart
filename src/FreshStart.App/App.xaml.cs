using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using FreshStart.App.Services;
using FreshStart.App.Shell;
using FreshStart.App.Startup;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Diagnostics;
using FreshStart.Core.Settings;
using FreshStart.Core.UI;
using FreshStart.Services.Software;
using FreshStart.Services.System;

namespace FreshStart.App;

public partial class App
{
    private const int CurrentWelcomeRevision = 1;
    private SingleInstanceHost? _singleInstance;
    private CompositionRoot? _composition;
    private StartupOptions _options = new();
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupProbe.Mark("app-start");

        _options = StartupOptions.Parse(e.Args);
        AppPaths.EnsureCreated();
        Log.Init();
        Log.Info($"FreshStart 启动，参数: {string.Join(' ', _options.RawArgs)}");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("AppDomain 未处理异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        try
        {
            await StartCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("启动失败", ex);
            MessageBox.Show($"FreshStart 启动失败：{ex.Message}\n\n日志：{AppPaths.LogRoot}", "FreshStart",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartCoreAsync()
    {
        var headless = !string.IsNullOrWhiteSpace(_options.UnattendedPath);

        if (!headless)
        {
            _singleInstance = new SingleInstanceHost();
            if (!_singleInstance.TryAcquire())
            {
                Log.Info("检测到已运行的实例，尝试激活");
                var activated = await _singleInstance.ActivateExistingAsync(_options.RawArgs);
                _singleInstance.Dispose();
                Shutdown(activated ? 0 : 2);
                return;
            }

            _singleInstance.StartServer();
            _singleInstance.ActivationReceived += OnActivationReceived;
        }

        var settings = new SettingsService();
        settings.Load();
        var persistedTheme = settings.Current.Theme;
        if (_options.Theme is not null)
        {
            // 仅命令行覆盖本次运行，不写回配置
            settings.Current.Theme = _options.Theme;
        }
        MotionPreferences.SetEnabled(settings.Current.AnimationsEnabled);
        StartupProbe.Mark("settings-loaded");

        // 后台按保留策略清理旧备份与日志，不阻塞启动
        var retentionDays = settings.Current.BackupRetentionDays;
        _ = Task.Run(() =>
        {
            var backups = FreshStart.Core.Backup.BackupRetention.PurgeBackups(retentionDays);
            var logs = FreshStart.Core.Backup.BackupRetention.PurgeLogs();
            if (backups > 0 || logs > 0)
            {
                Log.Info($"保留策略已清理：备份 {backups} 份、日志 {logs} 个（保留 {retentionDays} 天）");
            }
        });

        _composition = headless
            ? new CompositionRoot(settings, new AutoApproveGate())
            : new CompositionRoot(settings);
        StartupProbe.Mark("composition-ready");

        if (headless)
        {
            await RunUnattendedCliAsync(_options.UnattendedPath!);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var initialPage = _options.Page ?? settings.Current.LastPage;
        _mainWindow = _composition.CreateMainWindow(initialPage);
        MainWindow = _mainWindow;
        StartupProbe.Mark("shell-built");

        var welcomeTask = _options.SmokeTest
            ? null
            : PrepareWelcomeIfNeeded(settings, _options.ShowWelcome);
        _mainWindow.Show();
        _composition.ActivityConsoleHost.ApplyInitial(settings.Current.TaskConsoleEnabled);
        // ThemeService 已按命令行主题初始化；恢复持久化值，避免一次预览覆盖用户偏好。
        settings.Current.Theme = persistedTheme;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        StartupProbe.Mark("window-shown");
        WarnOtherInstances();
        if (welcomeTask is not null)
        {
            await welcomeTask;
        }
        if (!_options.SmokeTest)
        {
            _ = PromptForBundledWingetUpdateAsync();
        }

        if (_options.Preset is not null)
        {
            _ = Dispatcher.BeginInvoke(() => _composition.Toast.Show(
                $"预设「{_options.Preset}」将在第四批交付", ToastKind.Info), DispatcherPriority.Background);
        }

        if (_options.SmokeTest)
        {
            _ = RunSmokeTestAsync();
        }
    }

    /// <summary>首次启动展示新手引导（讲清用法、风险与环境状态）；看过后写回设置。</summary>
    private Task? PrepareWelcomeIfNeeded(SettingsService settings, bool force = false)
    {
        if (!force
            && settings.Current.HasSeenWelcome
            && settings.Current.WelcomeRevision >= CurrentWelcomeRevision)
        {
            return null;
        }

        if (_mainWindow is null) return null;
        return CompleteWelcomeAsync(settings);
    }

    private async Task CompleteWelcomeAsync(SettingsService settings)
    {
        try
        {
            await _mainWindow!.ShowWelcomeAsync(
                ElevationService.IsAdministrator,
                _composition?.Winget.Version?.ToString() ?? "按需检测",
                AppPaths.BackupRoot);

            settings.Current.HasSeenWelcome = true;
            settings.Current.WelcomeRevision = CurrentWelcomeRevision;
            settings.SaveNow();
            Log.Info("欢迎引导已完成并记录");
        }
        catch (Exception ex)
        {
            Log.Warn($"显示欢迎引导失败: {ex.Message}");
            if (_mainWindow is not null)
            {
                _mainWindow.WelcomeHost.Visibility = Visibility.Collapsed;
                _mainWindow.WelcomeHost.Children.Clear();
            }
        }
    }

    /// <summary>
    /// 主界面显示后提供可选的 winget 离线更新。没有随包离线组件时完全不提示；
    /// 用户选择“稍后”不会影响任何功能，也不会触发联网下载。
    /// </summary>
    private async Task PromptForBundledWingetUpdateAsync()
    {
        try
        {
            var winget = _composition?.Winget;
            var bundledVersion = WingetService.ReadBundledBundleVersion();
            if (winget is null || bundledVersion is null || _mainWindow is null) return;

            var available = await winget.IsAvailableAsync();
            Log.Info($"可选 winget 更新检查：本机={(available ? winget.Version?.ToString() ?? "未知" : "未安装")}，" +
                     $"离线包=v{bundledVersion}");
            if (available && WingetService.MeetsRequiredVersion(winget.Version)) return;
            if (!_mainWindow.IsVisible) return;

            Log.Info("显示可选 winget 离线更新提示");
            var dialog = new WingetUpdateWindow(
                winget, available, winget.Version, bundledVersion)
            {
                Owner = _mainWindow
            };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Warn($"无法显示可选 winget 更新提示: {ex.Message}");
        }
    }

    private async Task RunUnattendedCliAsync(string path)
    {
        try
        {
            if (!_options.Yes && !_options.Silent)
            {
                Log.Warn("执行命令行需要显式确认（--yes 或 --silent）");
                MessageBox.Show(
                    "执行命令行需要在无界面模式自动运行，请追加 --yes 或 --silent 明确确认。",
                    "FreshStart", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(2);
                return;
            }

            if (!File.Exists(path))
            {
                Log.Error($"执行方案不存在: {path}");
                MessageBox.Show($"找不到方案文件：{path}", "FreshStart",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(3);
                return;
            }

            Log.Info($"执行命令行启动: {path}");
            var job = _composition!.Unattended.LoadFile(path);
            var plan = _composition.Unattended.BuildPlan(job);
            if (!plan.IsValid)
            {
                Log.Warn("方案没有可执行步骤");
                Shutdown(3);
                return;
            }

            // 命令行模式进程随后立刻退出，等一小会儿让通知真正显示出来
            var notifyWait = TimeSpan.FromSeconds(3);
            WindowsNotifier.Show("FreshStart 执行开始", $"{job.Name} · {plan.RunnableCount} 步", waitFor: notifyWait);

            var result = await Task.Run(() => _composition.Unattended.RunAsync(
                plan, _composition.Runner, null, CancellationToken.None));

            if (result.Execution is null)
            {
                Log.Warn("执行方案没有可执行步骤");
                WindowsNotifier.Show("FreshStart 未执行", "方案没有可执行的步骤", waitFor: notifyWait);
                Shutdown(0);
                return;
            }

            Log.Info($"执行命令行完成: success={result.Execution.Success}, " +
                     $"failures={result.Failures.Count}, rolledBack={result.Execution.RolledBack}, " +
                     $"report={result.ReportPath}");

            var succeeded = result.Execution.Success && result.Failures.Count == 0;
            WindowsNotifier.Show(
                succeeded ? "FreshStart 执行完成" : "FreshStart 执行未完全成功",
                succeeded
                    ? $"{job.Name} · 已写入报告"
                    : $"{job.Name} · {result.Failures.Count} 步失败，详见报告",
                !succeeded,
                waitFor: notifyWait);
            Shutdown(succeeded ? 0 : 1);
        }
        catch (Exception ex)
        {
            Log.Error("执行命令行失败", ex);
            WindowsNotifier.Show("FreshStart 执行失败", ex.Message, isError: true, waitFor: TimeSpan.FromSeconds(3));
            Shutdown(4);
        }
    }

    private void WarnOtherInstances()
    {
        try
        {
            var currentId = Process.GetCurrentProcess().Id;
            var others = Process.GetProcessesByName("FreshStart.Host")
                .Where(process => process.Id != currentId)
                .ToList();
            if (others.Count == 0) return;

            var versions = new List<string>();
            foreach (var process in others)
            {
                try
                {
                    var version = process.MainModule?.FileVersionInfo.ProductVersion;
                    if (!string.IsNullOrWhiteSpace(version) && !versions.Contains(version)) versions.Add(version);
                }
                catch { /* 访问旧实例信息失败不阻断 */ }
                finally { process.Dispose(); }
            }

            var detail = versions.Count > 0 ? $"（旧实例：{string.Join(" / ", versions)}）" : string.Empty;
            Dispatcher.BeginInvoke(() => _composition?.Toast.Show(
                "检测到另一个 FreshStart 实例仍在运行；如果是旧版本，请关闭旧窗口后再使用当前版本。" + detail,
                ToastKind.Warning), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            Log.Warn($"检查其他 FreshStart 实例失败: {ex.Message}");
        }
    }

    private void OnActivationReceived(object? sender, string[] args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var options = StartupOptions.Parse(args);
            if (!string.IsNullOrWhiteSpace(options.Page))
            {
                _composition?.Navigation.Navigate(options.Page);
            }
            if (!string.IsNullOrWhiteSpace(options.Preset))
            {
                _composition?.Toast.Show($"预设「{options.Preset}」将在第四批交付", ToastKind.Info);
            }
            _mainWindow?.RestoreAndActivate();
        }, DispatcherPriority.Normal);
    }

    private async Task RunSmokeTestAsync()
    {
        try
        {
            await Task.Delay(1600);

            var process = Process.GetCurrentProcess();
            var path = _options.SmokeOut ?? Path.Combine(Path.GetTempPath(), "freshstart-smoke.json");
            StartupProbe.WriteFile(path, new Dictionary<string, object?>
            {
                ["workingSetMb"] = Math.Round(process.WorkingSet64 / 1024d / 1024d, 1),
                ["peakWorkingSetMb"] = Math.Round(process.PeakWorkingSet64 / 1024d / 1024d, 1),
                ["privateMb"] = Math.Round(process.PrivateMemorySize64 / 1024d / 1024d, 1),
                ["page"] = _composition?.Navigation.CurrentKey,
                ["theme"] = _composition?.Theme.CurrentTheme,
                ["windowWidth"] = _mainWindow?.ActualWidth,
                ["windowHeight"] = _mainWindow?.ActualHeight
            });
            Log.Info($"冒烟测试结果已写入: {path}");
        }
        catch (Exception ex)
        {
            Log.Error("冒烟测试失败", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常", e.Exception);
        e.Handled = true;
        try
        {
            _composition?.Toast.Show("发生未处理异常，详情已写入日志", ToastKind.Error);
        }
        catch { /* ignore */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Info("FreshStart 退出");
            Log.Flush(TimeSpan.FromMilliseconds(500));
            _singleInstance?.Dispose();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
