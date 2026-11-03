using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Core.Settings;
using FreshStart.Services.ContextMenu;
using FreshStart.Services.Registry;
using Microsoft.Win32;

namespace FreshStart.Services.Startup;

public enum StartupKind
{
    RegistryRun,
    StartupFolder,
    ScheduledTask,
    Service
}

public sealed class StartupEntry
{
    public StartupKind Kind { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Impact { get; set; } = "低";
    public int ImpactRank { get; set; } = 1;
    public bool IsEnabled { get; set; }
    public bool CanToggle { get; set; } = true;
    public bool CanDelay { get; set; }
    public bool IsDelayed { get; set; }

    public string? Hive { get; set; }
    public string? View { get; set; }
    public string? SubKey { get; set; }
    public string? ValueName { get; set; }
    public string? ApprovedSubKey { get; set; }
    public string? ApprovedValueName { get; set; }
    public string? FilePath { get; set; }
    public string? TaskPath { get; set; }
    public string? TaskName { get; set; }
    public string? ServiceName { get; set; }

    public string ToggleText => IsEnabled ? "禁用" : "启用";
    public string DelayText => IsDelayed ? "取消延迟" : "延迟启动";

    public string KindText => Kind switch
    {
        StartupKind.RegistryRun => "注册表",
        StartupKind.StartupFolder => "启动文件夹",
        StartupKind.ScheduledTask => "计划任务",
        StartupKind.Service => "服务",
        _ => "启动项"
    };
}

public sealed class StartupSnapshot
{
    public List<StartupEntry> Entries { get; set; } = new();
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 启动项枚举与操作工厂。Run 使用 StartupApproved 二进制标记禁用（与任务管理器一致）；
/// 启动文件夹使用 .disabled 后缀；计划任务启停走任务计划服务；服务使用 SCM 启动类型。
/// 服务被 FreshStart 禁用后会记录在 touched 列表，重启后仍可看到并重新启用。
/// </summary>
public sealed class StartupService
{
    private readonly RegistryService _registry;
    private readonly ScheduledTaskService _tasks;
    private readonly ServiceControlService _services;
    private readonly StartupTouchedStore _touched = new();

    public StartupService(RegistryService registry, ScheduledTaskService? tasks = null, ServiceControlService? services = null)
    {
        _registry = registry;
        _tasks = tasks ?? new ScheduledTaskService();
        _services = services ?? new ServiceControlService();
        _touched.Load();
    }

    public async Task<StartupSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var entries = new List<StartupEntry>();
        entries.AddRange(ReadRunEntries());
        entries.AddRange(ReadStartupFolders());

        var tasks = await _tasks.QueryLogonTasksAsync(ct).ConfigureAwait(false);
        entries.AddRange(tasks.Select(task => new StartupEntry
        {
            Kind = StartupKind.ScheduledTask,
            Id = $"task:{task.FullName}",
            Name = task.Name,
            Detail = string.IsNullOrWhiteSpace(task.Action) ? task.Path : task.Action,
            Source = "计划任务 · 登录/开机",
            Impact = "中",
            ImpactRank = 2,
            IsEnabled = task.IsEnabled,
            CanToggle = true,
            CanDelay = false,
            TaskPath = task.Path,
            TaskName = task.Name
        }));

        entries.AddRange(ReadServices());
        entries = entries
            .OrderBy(e => e.Kind == StartupKind.Service ? 2 : e.Kind == StartupKind.RegistryRun ? 0 : 1)
            .ThenByDescending(e => e.ImpactRank)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new StartupSnapshot { Entries = entries };
    }

    public ISystemOperation CreateToggleOperation(StartupEntry entry)
    {
        var enable = !entry.IsEnabled;
        return entry.Kind switch
        {
            StartupKind.RegistryRun => CreateRunToggleOperation(entry, enable),
            StartupKind.StartupFolder => CreateFolderToggleOperation(entry, enable),
            StartupKind.ScheduledTask => new ScheduledTaskToggleOperation(_tasks, entry.TaskPath!, entry.TaskName!, enable),
            StartupKind.Service => new ServiceStartModeOperation(
                _services, entry.ServiceName!, enable ? 2 : 4, enable && entry.IsDelayed, RiskLevel.High),
            _ => throw new InvalidOperationException("未知启动项类型")
        };
    }

    public ISystemOperation CreateDelayOperation(StartupEntry entry)
    {
        if (entry.Kind != StartupKind.Service || !entry.CanDelay)
            throw new InvalidOperationException("该启动项不支持延迟");
        return new ServiceStartModeOperation(_services, entry.ServiceName!, 2, !entry.IsDelayed, RiskLevel.Caution);
    }

    public void MarkServiceEnabled(StartupEntry entry, bool enabled)
    {
        if (entry.Kind != StartupKind.Service || entry.ServiceName is null) return;
        if (enabled) _touched.Unmark(entry.ServiceName);
        else _touched.Mark(entry.ServiceName);
    }

    private RegistryValueOperation CreateRunToggleOperation(StartupEntry entry, bool enable)
    {
        if (entry.Hive is null || entry.View is null || entry.ApprovedSubKey is null || entry.ApprovedValueName is null)
            throw new InvalidOperationException("启动项缺少注册表路径");

        // 12 字节 StartupApproved：首字节 2=启用，3=禁用，其余保留。任务管理器使用同样的结构。
        var data = new byte[12];
        data[0] = (byte)(enable ? 2 : 3);

        return new RegistryValueOperation(
            _registry,
            enable ? "startup.run.enable" : "startup.run.disable",
            $"{(enable ? "启用" : "禁用")}启动项「{entry.Name}」",
            "通过 Explorer\\StartupApproved 标记启用/禁用 Run 项，不删除原始启动命令",
            RiskLevel.Caution,
            entry.Hive,
            entry.View,
            entry.ApprovedSubKey,
            entry.ApprovedValueName,
            RegistryValueKind.Binary,
            data);
    }

    private static FileSystem.FileMoveOperation CreateFolderToggleOperation(StartupEntry entry, bool enable)
    {
        var source = entry.FilePath ?? throw new InvalidOperationException("启动文件夹项缺少路径");
        var destination = enable
            ? (source.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? source[..^".disabled".Length]
                : throw new InvalidOperationException("启动文件夹项状态异常"))
            : source + ".disabled";

        return new FileSystem.FileMoveOperation(
            enable ? "startup.folder.enable" : "startup.folder.disable",
            $"{(enable ? "启用" : "禁用")}启动文件夹项「{entry.Name}」",
            "通过 .disabled 后缀启用/禁用启动文件夹中的快捷方式，文件保留在原目录",
            RiskLevel.Caution,
            source,
            destination,
            entry.Name);
    }

    private List<StartupEntry> ReadRunEntries()
    {
        var entries = new List<StartupEntry>();
        foreach (var hive in new[] { "HKCU", "HKLM" })
        {
            var registryHive = hive == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var classes = hive == "HKCU" ? @"Software\Microsoft\Windows\CurrentVersion" : @"SOFTWARE\Microsoft\Windows\CurrentVersion";
            using var baseKey = RegistryKey.OpenBaseKey(registryHive, RegistryView.Registry64);
            using var approvedBase = baseKey.OpenSubKey($@"{classes}\Explorer\StartupApproved");

            foreach (var runKeyName in new[] { "Run", "RunOnce" })
            {
                using var runKey = baseKey.OpenSubKey($@"{classes}\{runKeyName}");
                if (runKey is null) continue;

                foreach (var valueName in runKey.GetValueNames())
                {
                    var command = runKey.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (string.IsNullOrWhiteSpace(command)) continue;

                    var approvedKeyName = hive == "HKCU"
                        ? $@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{runKeyName}"
                        : $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{runKeyName}";
                    var approvedValue = approvedBase?.OpenSubKey(runKeyName)?.GetValue(valueName) as byte[];
                    var enabled = approvedValue is not { Length: > 0 } || (approvedValue[0] != 3 && approvedValue[0] != 7);

                    entries.Add(new StartupEntry
                    {
                        Kind = StartupKind.RegistryRun,
                        Id = $"run:{hive}:{runKeyName}:{valueName}",
                        Name = valueName,
                        Detail = command,
                        Source = $"{hive} · {runKeyName}",
                        Impact = "低",
                        ImpactRank = 1,
                        IsEnabled = enabled,
                        CanToggle = true,
                        CanDelay = false,
                        Hive = hive,
                        View = "64",
                        SubKey = $@"{classes}\{runKeyName}",
                        ValueName = valueName,
                        ApprovedSubKey = approvedKeyName,
                        ApprovedValueName = valueName
                    });
                }
            }
        }

        return entries;
    }

    private static List<StartupEntry> ReadStartupFolders()
    {
        var entries = new List<StartupEntry>();
        foreach (var (path, owner) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), "当前用户"),
                     (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                         @"Programs\Startup"), "所有用户")
                 })
        {
            if (!Directory.Exists(path)) continue;
            try
            {
                foreach (var file in Directory.EnumerateFileSystemEntries(path))
                {
                    var fileName = Path.GetFileName(file);
                    if (string.Equals(fileName, "desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    var disabled = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    var display = disabled ? fileName[..^".disabled".Length] : fileName;
                    entries.Add(new StartupEntry
                    {
                        Kind = StartupKind.StartupFolder,
                        Id = $"folder:{file}",
                        Name = Path.GetFileNameWithoutExtension(display),
                        Detail = file,
                        Source = $"启动文件夹 · {owner}",
                        Impact = "中",
                        ImpactRank = 2,
                        IsEnabled = !disabled,
                        CanToggle = true,
                        CanDelay = false,
                        FilePath = file
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"枚举启动文件夹失败: {path} - {ex.Message}");
            }
        }
        return entries;
    }

    private List<StartupEntry> ReadServices()
    {
        var entries = new List<StartupEntry>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return entries;

        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                using var key = services.OpenSubKey(name);
                if (key is null) continue;
                var start = key.GetValue("Start") is int value ? value : 3;
                var type = key.GetValue("Type") is int t ? t : 0;
                if ((type & 0x30) == 0) continue; // 只保留 Win32 服务，跳过驱动
                var touched = _touched.Contains(name);
                if (start != 2 && !touched) continue;
                if (start is 0 or 1) continue;

                var display = ShellResourceResolver.Resolve(key.GetValue("DisplayName") as string);
                if (string.IsNullOrWhiteSpace(display)) display = name;
                var imagePath = key.GetValue("ImagePath") as string ?? string.Empty;

                // 启动项视角只展示第三方服务；系统服务（svchost/System32）由 Windows 更新、
                // Defender 及后续“服务与任务”模块管理，避免误禁用系统组件。
                if (IsSystemServicePath(imagePath)) continue;

                var delayed = key.GetValue("DelayedAutoStart") is int d && d == 1 && start == 2;

                entries.Add(new StartupEntry
                {
                    Kind = StartupKind.Service,
                    Id = $"service:{name}",
                    Name = display,
                    Detail = imagePath,
                    Source = start == 2 ? "服务 · 自动启动" : "服务 · 已禁用（FreshStart）",
                    Impact = "高",
                    ImpactRank = 3,
                    IsEnabled = start == 2,
                    CanToggle = true,
                    CanDelay = start == 2,
                    IsDelayed = delayed,
                    ServiceName = name
                });
            }
            catch (Exception ex)
            {
                Log.Debug($"读取服务 {name} 失败: {ex.Message}");
            }
        }

        return entries;
    }

    private static bool IsSystemServicePath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return true;
        var value = imagePath.Replace("\"", string.Empty).ToLowerInvariant();
        return value.Contains("%systemroot%")
               || value.Contains(@"\windows\system32\")
               || value.Contains(@"\windows\syswow64\")
               || value.Contains(@"\windows\system32", StringComparison.Ordinal);
    }
}

/// <summary>记录被 FreshStart 禁用的服务，保证重启后仍能看到并重新启用。</summary>
internal sealed class StartupTouchedStore
{
    private readonly string _path = Path.Combine(AppPaths.Root, "startup-touched.json");
    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path), JsonConfig.Options);
            if (list is null) return;
            lock (_lock) foreach (var name in list) _names.Add(name);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取启动项记录失败: {ex.Message}");
        }
    }

    public bool Contains(string name)
    {
        lock (_lock) return _names.Contains(name);
    }

    public void Mark(string name)
    {
        lock (_lock) _names.Add(name);
        Save();
    }

    public void Unmark(string name)
    {
        lock (_lock) _names.Remove(name);
        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            lock (_lock)
            {
                AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_names.ToList(), JsonConfig.Options));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"保存启动项记录失败: {ex.Message}");
        }
    }
}
