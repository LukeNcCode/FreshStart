using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using FreshStart.Services.Startup;
using FreshStart.Services.System;
using Microsoft.Win32;

namespace FreshStart.Services.WindowsUpdate;

public enum WindowsUpdateMode
{
    Normal,
    Paused,
    ExtendedPause,
    Disabled
}

public sealed class UpdateServiceState
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public int StartType { get; set; }
    public bool DelayedAutoStart { get; set; }
    public string StartText => StartType switch
    {
        2 => DelayedAutoStart ? "自动（延迟）" : "自动",
        3 => "手动",
        4 => "已禁用",
        _ => $"类型 {StartType}"
    };
}

public sealed class WindowsUpdateStatus
{
    public WindowsUpdateMode Mode { get; set; }
    public string ModeText => Mode switch
    {
        WindowsUpdateMode.Paused => "已暂停",
        WindowsUpdateMode.ExtendedPause => "已超长延期",
        WindowsUpdateMode.Disabled => "已关闭",
        _ => "正常"
    };

    public string Description
    {
        get
        {
            return Mode switch
            {
                WindowsUpdateMode.ExtendedPause =>
                    $"注册表暂停截止时间被设置到远期（{PauseExpiryText}）。Windows 可能忽略该值或在功能更新后恢复，不能保证永久阻止更新。",
                WindowsUpdateMode.Paused => "更新已暂停，到期后 Windows 会恢复检查。",
                WindowsUpdateMode.Disabled => "更新服务、策略与计划任务已被关闭。Windows 可能在功能更新或系统修复后自动恢复部分设置。",
                _ => "Windows Update 按系统默认策略运行。"
            };
        }
    }

    public string PauseExpiryText { get; set; } = "未设置";
    public bool PolicyDisabled { get; set; }
    public List<UpdateServiceState> Services { get; set; } = new();
}

public sealed class UpdateHistoryItem
{
    public string Title { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public int ResultCode { get; set; }

    public string ResultText => ResultCode switch
    {
        2 => "成功",
        3 => "成功（有警告）",
        4 => "失败",
        5 => "已中止",
        _ => $"结果 {ResultCode}"
    };
}

/// <summary>
/// Windows Update 状态读取、更新历史与操作工厂。
/// 关闭/恢复通过“服务启动类型 + 策略 + UpdateOrchestrator 计划任务”三处联动；
/// 所有写入都进入 OperationRunner 的备份/回滚管线。
/// </summary>
public sealed class WindowsUpdateService
{
    public const string PolicySubKey = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU";
    public const string PauseSubKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    public const string OrchestratorPath = @"\Microsoft\Windows\UpdateOrchestrator\";

    /// <summary>不同 Windows 版本把更新任务放在不同目录，三处都覆盖。</summary>
    public static readonly string[] UpdateTaskPaths =
    {
        @"\Microsoft\Windows\UpdateOrchestrator\",
        @"\Microsoft\Windows\WindowsUpdate\",
        @"\Microsoft\Windows\InstallService\"
    };

    public static readonly string[] ServiceNames = { "wuauserv", "UsoSvc", "WaaSMedicSvc" };
    public static readonly (string Name, string Display)[] ServiceDisplay =
    {
        ("wuauserv", "Windows Update"),
        ("UsoSvc", "Update Orchestrator"),
        ("WaaSMedicSvc", "Windows Update Medic")
    };

    public static readonly string[] PauseValues =
    {
        "PauseUpdatesStartTime",
        "PauseUpdatesExpiryTime",
        "PauseFeatureUpdatesStartTime",
        "PauseFeatureUpdatesEndTime",
        "PauseQualityUpdatesStartTime",
        "PauseQualityUpdatesEndTime"
    };

    private readonly RegistryService _registry;
    private readonly ServiceControlService _services;
    private readonly ScheduledTaskService _tasks;
    private readonly PowerShellRunner _powershell = new();

    public WindowsUpdateService(RegistryService registry, ServiceControlService services, ScheduledTaskService tasks)
    {
        _registry = registry;
        _services = services;
        _tasks = tasks;
    }

    public async Task<WindowsUpdateStatus> GetStatusAsync(CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var status = new WindowsUpdateStatus();
            foreach (var (name, display) in ServiceDisplay)
            {
                var state = _services.Capture(name);
                status.Services.Add(new UpdateServiceState
                {
                    Name = name,
                    DisplayName = display,
                    Exists = state.Exists,
                    StartType = state.StartType,
                    DelayedAutoStart = state.DelayedAutoStart
                });
            }

            status.PolicyDisabled = ReadDword("HKLM", PolicySubKey, "NoAutoUpdate") == 1;
            var pause = ReadString("HKLM", PauseSubKey, "PauseUpdatesExpiryTime")
                        ?? ReadString("HKLM", PauseSubKey, "PauseFeatureUpdatesEndTime");
            if (DateTimeOffset.TryParse(pause, out var expiry))
            {
                status.PauseExpiryText = expiry.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                if (expiry > DateTimeOffset.Now && !status.PolicyDisabled)
                {
                    status.Mode = expiry > DateTimeOffset.Now.AddDays(365)
                        ? WindowsUpdateMode.ExtendedPause
                        : WindowsUpdateMode.Paused;
                }
            }
            else
            {
                status.PauseExpiryText = "未设置";
            }

            var wuauserv = status.Services.FirstOrDefault(s => s.Name == "wuauserv");
            if (status.PolicyDisabled || (wuauserv is { Exists: true, StartType: 4 }))
            {
                status.Mode = WindowsUpdateMode.Disabled;
            }

            return status;
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UpdateHistoryItem>> GetHistoryAsync(CancellationToken ct = default)
    {
        const string script = """
            $session = New-Object -ComObject Microsoft.Update.Session
            $searcher = $session.CreateUpdateSearcher()
            $count = $searcher.GetTotalHistoryCount()
            $limit = [Math]::Min($count, 20)
            $items = @()
            if ($limit -gt 0) {
                $items = @($searcher.QueryHistory(0, $limit) | ForEach-Object {
                    [pscustomobject]@{
                        Title = [string]$_.Title
                        Date = $_.Date.ToString('yyyy-MM-dd HH:mm')
                        ResultCode = [int]$_.ResultCode
                    }
                })
            }
            ConvertTo-Json -InputObject $items -Depth 3 -Compress
            """;

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return Array.Empty<UpdateHistoryItem>();

        try
        {
            return JsonSerializer.Deserialize<List<UpdateHistoryItem>>(result.StdOut, JsonConfig.Options)
                   ?? new List<UpdateHistoryItem>();
        }
        catch (Exception ex)
        {
            Log.Warn($"解析更新历史失败: {ex.Message}");
            return Array.Empty<UpdateHistoryItem>();
        }
    }

    public Operation CreateOperation(WindowsUpdateMode mode)
        => new(this, _registry, _services, _tasks, mode);

    internal async Task<IReadOnlyList<ScheduledTaskInfo>> QueryUpdateTasksAsync(CancellationToken ct)
    {
        var results = new List<ScheduledTaskInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in UpdateTaskPaths)
        {
            var tasks = await _tasks.QueryTasksUnderPathAsync(path, ct).ConfigureAwait(false);
            foreach (var task in tasks)
            {
                if (seen.Add(task.FullName)) results.Add(task);
            }
        }
        return results;
    }

    internal int? ReadDword(string hive, string subKey, string valueName)
    {
        var state = _registry.CaptureValue(hive, "64", subKey, valueName);
        return state.Existed ? state.DwordData : null;
    }

    internal string? ReadString(string hive, string subKey, string valueName)
    {
        var state = _registry.CaptureValue(hive, "64", subKey, valueName);
        return state.Existed ? state.StringData : null;
    }

    public sealed class Operation : OperationBase
    {
        private readonly WindowsUpdateMode _mode;
        private readonly WindowsUpdateService _owner;
        private readonly RegistryService _registry;
        private readonly ServiceControlService _services;
        private readonly ScheduledTaskService _tasks;

        public Operation(
            WindowsUpdateService owner,
            RegistryService registry,
            ServiceControlService services,
            ScheduledTaskService tasks,
            WindowsUpdateMode mode)
        {
            _owner = owner;
            _mode = mode;
            _registry = registry;
            _services = services;
            _tasks = tasks;
        }

        public override string Id => _mode switch
        {
            WindowsUpdateMode.Paused => "update.pause",
            WindowsUpdateMode.ExtendedPause => "update.extend",
            WindowsUpdateMode.Disabled => "update.disable",
            _ => "update.restore"
        };

        public override string Name => _mode switch
        {
            WindowsUpdateMode.Paused => "暂停 Windows 更新 7 天",
            WindowsUpdateMode.ExtendedPause => "超长延期 Windows 更新（注册表）",
            WindowsUpdateMode.Disabled => "彻底关闭 Windows 更新",
            _ => "恢复 Windows 更新默认设置"
        };

        public override string Description => _mode switch
        {
            WindowsUpdateMode.Paused => "只写注册表暂停截止时间，不改服务与计划任务",
            WindowsUpdateMode.ExtendedPause => "只写注册表远期暂停截止时间（2099-12-31），不改服务与计划任务",
            WindowsUpdateMode.Disabled => "禁用更新服务、写入 NoAutoUpdate 策略并禁用更新相关计划任务",
            _ => "恢复更新服务启动类型、删除关闭策略、恢复计划任务"
        };

        public override RiskLevel RiskLevel => _mode switch
        {
            WindowsUpdateMode.Paused => RiskLevel.Caution,
            _ => RiskLevel.High
        };

        public override IReadOnlyList<ChangeDescriptor> DescribeChanges()
        {
            var changes = new List<ChangeDescriptor>();
            var target = _mode switch
            {
                WindowsUpdateMode.Paused => "暂停至 7 天后",
                WindowsUpdateMode.ExtendedPause => "注册表超长延期至 2099-12-31",
                WindowsUpdateMode.Disabled => "服务禁用 + 策略关闭 + 计划任务禁用",
                _ => "恢复服务、策略与计划任务默认状态"
            };

            if (_mode is WindowsUpdateMode.Paused or WindowsUpdateMode.ExtendedPause)
            {
                changes.Add(new ChangeDescriptor
                {
                    Target = @"HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings\PauseUpdatesExpiryTime",
                    Kind = "注册表暂停",
                    Before = "当前暂停值",
                    After = target,
                    Impact = "只写注册表，不改服务/任务；Windows 可能忽略远期值或自行恢复"
                });
            }
            else
            {
                changes.Add(new ChangeDescriptor
                {
                    Target = @"HKLM\SYSTEM\CurrentControlSet\Services\wuauserv / UsoSvc / WaaSMedicSvc",
                    Kind = "Windows Update",
                    Before = "当前状态",
                    After = target,
                    Impact = _mode == WindowsUpdateMode.Disabled
                        ? "将无法自动获取安全更新；Windows 可能在系统更新或修复后恢复部分设置"
                        : "不会删除已安装的更新；可随时恢复默认"
                });
            }

            if (_mode is not (WindowsUpdateMode.Paused or WindowsUpdateMode.ExtendedPause))
            {
                changes.Add(new ChangeDescriptor
                {
                    Target = @"HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU\NoAutoUpdate",
                    Kind = "组策略",
                    Before = "当前值",
                    After = _mode == WindowsUpdateMode.Disabled ? "1（关闭自动更新）" : "删除/恢复",
                    Impact = "影响自动检查与安装更新的行为"
                });
                changes.Add(new ChangeDescriptor
                {
                    Target = @"任务计划程序\Microsoft\Windows\UpdateOrchestrator / WindowsUpdate / InstallService",
                    Kind = "计划任务",
                    Before = "当前状态",
                    After = _mode == WindowsUpdateMode.Disabled ? "全部禁用" : "全部启用",
                    Impact = "影响后台更新扫描与重启安排"
                });
            }
            return changes;
        }

        public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
        {
            var entries = new List<SnapshotEntry>();

            // 暂停/超长延期只写注册表，不碰服务与任务；避免受 SCM ACL（sc config 拒绝访问）影响。
            if (_mode is WindowsUpdateMode.Disabled or WindowsUpdateMode.Normal)
            {
                foreach (var name in WindowsUpdateService.ServiceNames)
                {
                    var state = _services.Capture(name);
                    entries.Add(new SnapshotEntry
                    {
                        Kind = SnapshotKind.Service,
                        Target = name,
                        Existed = state.Exists,
                        DataJson = JsonSerializer.Serialize(state, JsonConfig.Options)
                    });
                }
            }

            foreach (var (valueName, isDword) in TargetValuesForMode(_mode))
            {
                var hive = "HKLM";
                var subKey = IsPolicyValue(valueName) ? WindowsUpdateService.PolicySubKey : WindowsUpdateService.PauseSubKey;
                var state = _registry.CaptureValue(hive, "64", subKey, valueName);
                entries.Add(new SnapshotEntry
                {
                    Kind = SnapshotKind.RegistryValue,
                    Target = RegistryService.Normalize(hive, subKey, valueName),
                    ValueName = valueName,
                    Existed = state.Existed,
                    ValueType = state.ValueType,
                    DataJson = JsonSerializer.Serialize(new CapturedValue { SubKey = subKey, Value = state, IsDword = isDword }, JsonConfig.Options)
                });
            }

            if (_mode is WindowsUpdateMode.Disabled or WindowsUpdateMode.Normal)
            {
                var tasks = await _owner.QueryUpdateTasksAsync(ct).ConfigureAwait(false);
                entries.Add(new SnapshotEntry
                {
                    Kind = SnapshotKind.ScheduledTask,
                    Target = WindowsUpdateService.OrchestratorPath,
                    Existed = tasks.Count > 0,
                    DataJson = JsonSerializer.Serialize(tasks.Select(t => new TaskState
                    {
                        Path = t.Path,
                        Name = t.Name,
                        State = t.State
                    }).ToList(), JsonConfig.Options)
                });
            }

            return CreateSnapshot(entries);
        }

        protected override async Task<OperationResult> ApplyCoreAsync(
            OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        {
            progress?.Report(new OperationStep { Message = "正在修改更新服务…", Percent = 30 });
            var serviceTargets = _mode switch
            {
                WindowsUpdateMode.Paused or WindowsUpdateMode.ExtendedPause =>
                    Array.Empty<(string Name, int Start, bool Delayed)>(),
                WindowsUpdateMode.Disabled => new (string Name, int Start, bool Delayed)[]
                {
                    ("wuauserv", 4, false), ("UsoSvc", 4, false), ("WaaSMedicSvc", 4, false)
                },
                _ => new (string Name, int Start, bool Delayed)[]
                {
                    ("wuauserv", 3, false), ("UsoSvc", 2, true), ("WaaSMedicSvc", 3, false)
                }
            };

            foreach (var (name, start, delayed) in serviceTargets)
            {
                var current = _services.Capture(name);
                if (!current.Exists) continue;
                if (current.StartType == start && current.DelayedAutoStart == delayed) continue;
                var result = await _services.SetStartModeAsync(name, start, delayed, ct).ConfigureAwait(false);
                if (!result.Success) return result;
            }

            progress?.Report(new OperationStep { Message = "正在写入更新策略…", Percent = 60 });
            var now = DateTimeOffset.UtcNow;
            var end = _mode == WindowsUpdateMode.ExtendedPause
                ? new DateTimeOffset(2099, 12, 31, 23, 59, 59, TimeSpan.Zero)
                : now.AddDays(7);
            foreach (var (valueName, isDword) in TargetValuesForMode(_mode))
            {
                var subKey = IsPolicyValue(valueName) ? WindowsUpdateService.PolicySubKey : WindowsUpdateService.PauseSubKey;
                var target = BuildTarget(_mode, valueName, subKey, isDword, now, end);
                _registry.ApplyValue(target);
                if (!_registry.ValueEquals(target))
                {
                    return OperationResult.Fail($"更新策略校验失败：{valueName}");
                }
            }

            if (_mode is WindowsUpdateMode.Disabled or WindowsUpdateMode.Normal)
            {
                progress?.Report(new OperationStep { Message = "正在处理更新计划任务…", Percent = 80 });
                var tasks = await _owner.QueryUpdateTasksAsync(ct).ConfigureAwait(false);
                foreach (var task in tasks)
                {
                    var result = await _tasks.SetEnabledAsync(task.Path, task.Name, _mode != WindowsUpdateMode.Disabled, ct)
                        .ConfigureAwait(false);
                    if (!result.Success) return result;
                }
            }

            progress?.Report(new OperationStep { Message = "完成", Percent = 100 });
            return OperationResult.Ok(_mode switch
            {
                WindowsUpdateMode.Paused => "更新已暂停 7 天",
                WindowsUpdateMode.ExtendedPause => "已通过注册表超长延期（至 2099-12-31）",
                WindowsUpdateMode.Disabled => "更新服务、策略与计划任务已关闭",
                _ => "更新已恢复默认设置"
            });
        }

        protected override async Task<OperationResult> RollbackCoreAsync(
            OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        {
            progress?.Report(new OperationStep { Message = "正在恢复更新设置…", Percent = 30 });

            foreach (var entry in state.Entries.Where(e => e.Kind == SnapshotKind.RegistryValue))
            {
                var captured = JsonSerializer.Deserialize<CapturedValue>(entry.DataJson!, JsonConfig.Options);
                if (captured is not null) _registry.ApplyValue(captured.Value);
            }

            foreach (var entry in state.Entries.Where(e => e.Kind == SnapshotKind.Service))
            {
                var service = JsonSerializer.Deserialize<ServiceState>(entry.DataJson!, JsonConfig.Options);
                if (service is { Exists: true })
                {
                    var current = _services.Capture(service.Name);
                    if (current.Exists && current.StartType == service.StartType &&
                        current.DelayedAutoStart == service.DelayedAutoStart)
                    {
                        continue;
                    }
                    await _services.SetStartModeAsync(service.Name, service.StartType, service.DelayedAutoStart, ct)
                        .ConfigureAwait(false);
                }
            }

            var taskEntry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.ScheduledTask);
            if (taskEntry?.DataJson is not null)
            {
                var tasks = JsonSerializer.Deserialize<List<TaskState>>(taskEntry.DataJson, JsonConfig.Options);
                if (tasks is not null)
                {
                    foreach (var task in tasks)
                    {
                        var enabled = !string.Equals(task.State, "Disabled", StringComparison.OrdinalIgnoreCase);
                        await _tasks.SetEnabledAsync(task.Path, task.Name, enabled, ct).ConfigureAwait(false);
                    }
                }
            }

            return OperationResult.Ok("更新设置已恢复");
        }

        private static IEnumerable<(string ValueName, bool IsDword)> ValueTargets()
        {
            yield return ("NoAutoUpdate", true);
            yield return ("AUOptions", true);
            foreach (var name in WindowsUpdateService.PauseValues) yield return (name, false);
        }

        private static IEnumerable<(string ValueName, bool IsDword)> TargetValuesForMode(
            WindowsUpdateMode mode)
            => mode is WindowsUpdateMode.Paused or WindowsUpdateMode.ExtendedPause
                ? WindowsUpdateService.PauseValues.Select(name => (ValueName: name, IsDword: false))
                : ValueTargets();

        private static bool IsPolicyValue(string valueName)
            => valueName is "NoAutoUpdate" or "AUOptions";

        private static RegistryValueState BuildTarget(
            WindowsUpdateMode mode, string valueName, string subKey, bool isDword,
            DateTimeOffset now, DateTimeOffset end)
        {
            var state = new RegistryValueState
            {
                Hive = "HKLM",
                View = "64",
                SubKey = subKey,
                Name = valueName
            };

            if (mode == WindowsUpdateMode.Disabled)
            {
                if (valueName == "NoAutoUpdate")
                {
                    state.Existed = true;
                    state.ValueType = "DWord";
                    state.DwordData = 1;
                }
                else
                {
                    state.Existed = false;
                }
                return state;
            }

            if (!isDword && (mode is WindowsUpdateMode.Paused or WindowsUpdateMode.ExtendedPause))
            {
                state.Existed = true;
                state.ValueType = "String";
                state.StringData = valueName.Contains("StartTime", StringComparison.Ordinal)
                    ? now.ToString("yyyy-MM-ddTHH:mm:ssZ")
                    : end.ToString("yyyy-MM-ddTHH:mm:ssZ");
                return state;
            }

            state.Existed = false;
            return state;
        }

        private sealed class CapturedValue
        {
            public string SubKey { get; set; } = string.Empty;
            public RegistryValueState Value { get; set; } = new();
            public bool IsDword { get; set; }
        }

        private sealed class TaskState
        {
            public string Path { get; set; } = "\\";
            public string Name { get; set; } = string.Empty;
            public string State { get; set; } = string.Empty;
        }
    }
}
