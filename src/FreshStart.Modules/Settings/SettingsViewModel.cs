using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Operations;
using FreshStart.Core.Settings;
using FreshStart.Services.Registry;
using Microsoft.Win32;

namespace FreshStart.Modules.Settings;

public sealed partial class AccentOption : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string Hex { get; init; } = "#007AFF";

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>内置桌面壁纸选项。</summary>
public sealed partial class WallpaperOption : ObservableObject
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string PreviewPath { get; init; } = string.Empty;

    /// <summary>发布包里带了这张图才有预览。</summary>
    public bool HasPreview => PreviewPath.Length > 0;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 设置页。所有开关立即生效并落盘；写入管线自检用于验证
/// "读取 → 备份 → 写入 → 失败回滚" 四步链路（只写 HKCU 沙箱）。
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public IReadOnlyList<AccentOption> AccentOptions { get; } = new List<AccentOption>
    {
        new() { Name = "苹果蓝", Hex = "#007AFF" },
        new() { Name = "薄荷绿", Hex = "#34C759" },
        new() { Name = "日落橙", Hex = "#FF9500" },
        new() { Name = "珊瑚红", Hex = "#FF3B30" },
        new() { Name = "薰衣草", Hex = "#AF52DE" },
        new() { Name = "石墨灰", Hex = "#636366" }
    };

    [RelayCommand]
    private void SetAccent(AccentOption? option)
    {
        if (option is null) return;
        _theme.SetAccent(option.Hex);
        RefreshAccentState();
        _toast.Show($"主题色已切换为「{option.Name}」", ToastKind.Success);
    }

    private void RefreshAccentState()
    {
        var active = _settings.Current.AccentColor;
        foreach (var option in AccentOptions)
        {
            option.IsSelected = string.Equals(option.Hex, active, StringComparison.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<WallpaperOption> WallpaperOptions { get; }

    [ObservableProperty] private string _currentWallpaperText = "—";
    [ObservableProperty] private bool _canRestoreWallpaper;

    /// <summary>把内置图片设为 Windows 桌面壁纸（不影响 FreshStart 自己的界面）。</summary>
    [RelayCommand]
    private void ApplyDesktopWallpaper(WallpaperOption? option)
    {
        if (option is null) return;

        // 首次应用内部壁纸前，记下原来的壁纸：优先用原文件路径，失效时用 Windows 当前显示的副本兜底
        if (_settings.Current.DesktopWallpaper.Length == 0)
        {
            var current = _wallpaper.GetCurrentPath();
            var usable = current.Length > 0
                         && System.IO.File.Exists(current)
                         && !IsBundledWallpaperPath(current);
            if (usable)
            {
                _settings.Current.OriginalWallpaperPath = current;
            }
            else
            {
                var backup = _wallpaper.BackupCurrent();
                if (backup is not null) _settings.Current.OriginalWallpaperPath = backup;
            }
        }

        var result = _wallpaper.Apply(option.Key, option.Name);
        _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Error);
        if (!result.Success) return;

        _settings.Current.DesktopWallpaper = option.Key;
        _settings.SaveNow();
        RefreshWallpaperState();
    }

    [RelayCommand]
    private void RestoreWallpaper()
    {
        var result = _wallpaper.Restore(_settings.Current.OriginalWallpaperPath);
        _toast.Show(result.Message, result.Success ? ToastKind.Success : ToastKind.Warning);
        if (!result.Success) return;

        _settings.Current.DesktopWallpaper = string.Empty;
        _settings.SaveNow();
        RefreshWallpaperState();
    }

    private void RefreshWallpaperState()
    {
        var applied = _settings.Current.DesktopWallpaper;
        foreach (var option in WallpaperOptions)
        {
            option.IsSelected = string.Equals(option.Key, applied, StringComparison.OrdinalIgnoreCase);
        }

        var current = _wallpaper.GetCurrentPath();
        CurrentWallpaperText = current.Length > 0 ? current : "未设置（Windows 默认）";
        CanRestoreWallpaper = _settings.Current.OriginalWallpaperPath.Length > 0;
    }

    private static bool IsBundledWallpaperPath(string path) =>
        path.Contains(@"FreshStart\wallpapers", StringComparison.OrdinalIgnoreCase);

    /// <summary>内置壁纸的发布包路径（用于缩略图预览）。</summary>
    private static string PreviewOf(string key) =>
        FreshStart.Services.System.SystemWallpaperService.TryGetSourceFile(key, out var path) ? path : string.Empty;

    private readonly SettingsService _settings;
    private readonly IThemeService _theme;
    private readonly IMotionService _motion;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private readonly OperationRunner _runner;
    private readonly RegistryService _registry;
    private readonly FreshStart.Services.System.SystemWallpaperService _wallpaper;
    private readonly IActivityConsoleHost _activityConsole;

    public SettingsViewModel(
        SettingsService settings,
        IThemeService theme,
        IMotionService motion,
        IToastService toast,
        IUserPromptService prompts,
        OperationRunner runner,
        RegistryService registry,
        FreshStart.Services.System.SystemWallpaperService wallpaper,
        IActivityConsoleHost activityConsole)
    {
        _settings = settings;
        _theme = theme;
        _motion = motion;
        _toast = toast;
        _prompts = prompts;
        _runner = runner;
        _registry = registry;
        _wallpaper = wallpaper;
        _activityConsole = activityConsole;
        _runner.AskRestorePointOnFirstWrite = settings.Current.AskRestorePointOnFirstWrite;
        _activityConsole.EnabledChanged += (_, _) =>
            OnPropertyChanged(nameof(TaskConsoleEnabled));
        RefreshAccentState();

        WallpaperOptions = new List<WallpaperOption>
        {
            new() { Key = "clouds", Name = "云雾", PreviewPath = PreviewOf("clouds") },
            new() { Key = "butterfly", Name = "发光蝴蝶", PreviewPath = PreviewOf("butterfly") },
            new() { Key = "flower", Name = "孤独花朵", PreviewPath = PreviewOf("flower") }
        };
        RefreshWallpaperState();

        _theme.ThemeChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDarkMode));
            OnPropertyChanged(nameof(ThemeSummary));
            RefreshAccentState();
        };

        var entryAssembly = Assembly.GetEntryAssembly();
        var informational = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];
        var version = entryAssembly?.GetName().Version ?? new Version(0, 1, 0, 0);
        VersionText = $"FreshStart {informational ?? $"{version.Major}.{version.Minor}.{version.Build}"}";
        LastTestText = "尚未运行。自检只会在 HKCU\\Software\\FreshStart\\SelfTest 下写入一个字符串值，不会修改系统设置。";
        UpdateUndoState();
        _runner.HistoryChanged += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            if (dispatcher.CheckAccess()) UpdateUndoState();
            else dispatcher.BeginInvoke(UpdateUndoState);
        };
    }

    public string VersionText { get; }
    public string DataDirectory => AppPaths.Root;
    public string BackupDirectory => AppPaths.BackupRoot;
    public string LogDirectory => AppPaths.LogRoot;

    public bool IsDarkMode
    {
        get => _theme.IsDark;
        set
        {
            if (value == _theme.IsDark) return;
            OnPropertyChanged();
            _ = ApplyThemeAsync(value);
        }
    }

    private async Task ApplyThemeAsync(bool dark)
    {
        await _theme.SetThemeAsync(dark ? "Dark" : "Light");
        await _settings.UpdateAsync(s => s.Theme = dark ? "Dark" : "Light");
        OnPropertyChanged(nameof(ThemeSummary));
        _toast.Show(dark ? "已切换为深色模式" : "已切换为浅色模式");
    }

    public string ThemeSummary => IsDarkMode ? "当前：深色" : "当前：浅色";

    public bool AnimationsEnabled
    {
        get => _settings.Current.AnimationsEnabled;
        set
        {
            if (value == _settings.Current.AnimationsEnabled) return;
            OnPropertyChanged();
            _motion.SetEnabled(value);
            _ = _settings.UpdateAsync(s => s.AnimationsEnabled = value);
            _toast.Show(value ? "界面动效已开启" : "界面动效已关闭");
        }
    }

    public bool AskRestorePoint
    {
        get => _settings.Current.AskRestorePointOnFirstWrite;
        set
        {
            if (value == _settings.Current.AskRestorePointOnFirstWrite) return;
            OnPropertyChanged();
            _runner.AskRestorePointOnFirstWrite = value;
            _ = _settings.UpdateAsync(s => s.AskRestorePointOnFirstWrite = value);
            _toast.Show(value ? "每次启动后首次修改前会询问还原点" : "已关闭还原点询问，仅保留 JSON 快照");
        }
    }

    public bool TaskConsoleEnabled
    {
        get => _activityConsole.IsEnabled;
        set
        {
            if (value == _activityConsole.IsEnabled) return;
            _activityConsole.SetEnabled(value);
            OnPropertyChanged();
            _toast.Show(value
                ? "任务控制台已开启，将使用独立窗口显示实时进度"
                : "任务控制台已关闭");
        }
    }

    [ObservableProperty]
    private string _lastTestText = string.Empty;

    [ObservableProperty]
    private string? _lastBackupDirectory;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private string _undoText = "暂无可撤销的操作";

    [RelayCommand]
    private void OpenBackupFolder() => OpenInExplorer(BackupDirectory);

    [RelayCommand]
    private void OpenLogFolder() => OpenInExplorer(LogDirectory);

    [RelayCommand]
    private void OpenDataFolder() => OpenInExplorer(DataDirectory);

    [RelayCommand]
    private async Task RunSelfTestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        LastBackupDirectory = null;
        LastTestText = "正在执行：读取当前值 → 备份 → 写入 → 读回校验…";
        try
        {
            var operation = new RegistryValueOperation(
                _registry,
                id: "dev.registry.selftest",
                name: "备份与回滚自检",
                description: "在 HKCU 沙箱写入一个时间戳字符串，验证备份与回滚链路（不会修改系统设置）",
                risk: RiskLevel.Safe,
                hive: "HKCU",
                view: "64",
                subKey: @"Software\FreshStart\SelfTest",
                valueName: "LastRun",
                kind: RegistryValueKind.String,
                newValue: DateTimeOffset.Now.ToString("O"));

            var execution = await _runner.ExecuteAsync(operation);
            LastBackupDirectory = execution.BackupDirectory;

            if (execution.Cancelled)
            {
                LastTestText = "已取消，未写入任何内容。";
                _toast.Show("自检已取消", ToastKind.Warning);
            }
            else if (execution.Success)
            {
                LastTestText = $"自检通过。备份已保存到：{execution.BackupDirectory}";
                _toast.Show("备份与回滚链路自检通过，已生成备份", ToastKind.Success);
            }
            else
            {
                LastTestText = execution.Result.Message;
                _toast.Show("自检失败，请查看日志", ToastKind.Error);
            }
        }
        catch (Exception ex)
        {
            LastTestText = $"自检异常：{ex.Message}";
            Log.Error("写入管线自检异常", ex);
            _toast.Show("自检异常，请查看日志", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            UpdateUndoState();
        }
    }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        if (!_runner.CanUndo) return;
        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = "撤销上一次操作？",
            Message = $"将回滚：{_runner.LastUndoName}",
            Details = new[]
            {
                "回滚使用写入前保存的 JSON 快照，不会重新猜测旧值",
                "如果系统状态已被其他程序改动，回滚可能覆盖这些改动",
                "回滚过程同样会写入审计日志"
            },
            ConfirmText = "撤销",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await _runner.UndoLastAsync();
            if (result is null) return;
            _toast.Show(result.Result.Success ? "已撤销上一次操作" : "撤销失败，请查看日志",
                result.Result.Success ? ToastKind.Success : ToastKind.Error);
            LastTestText = result.Result.Message;
        }
        catch (Exception ex)
        {
            Log.Error("撤销失败", ex);
            _toast.Show($"撤销失败：{ex.Message}", ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            UpdateUndoState();
        }
    }

    private void UpdateUndoState()
    {
        CanUndo = _runner.CanUndo;
        UndoText = _runner.CanUndo ? $"可撤销：{_runner.LastUndoName}" : "暂无可撤销的操作";
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开目录失败: {path}", ex);
        }
    }
}
