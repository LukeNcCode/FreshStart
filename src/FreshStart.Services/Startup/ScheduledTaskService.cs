using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;

namespace FreshStart.Services.Startup;

public sealed class ScheduledTaskInfo
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = "\\";
    public string State { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;

    public string FullName => (Path.TrimEnd('\\') + "\\" + Name).TrimStart('\\');
    public bool IsEnabled => !string.Equals(State, "Disabled", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 计划任务读写。枚举与状态查询走 PowerShell（只读、后台、超时），启停走同一套 cmdlet。
/// </summary>
public sealed class ScheduledTaskService
{
    private readonly PowerShellRunner _runner = new();

    public async Task<IReadOnlyList<ScheduledTaskInfo>> QueryLogonTasksAsync(CancellationToken ct)
    {
        const string script = """
            $items = @(Get-ScheduledTask | Where-Object {
                @($_.Triggers | Where-Object { $_.CimClass.CimClassName -match 'Logon|Boot' }).Count -gt 0
            } | ForEach-Object {
                [pscustomobject]@{
                    Name = [string]$_.TaskName
                    Path = [string]$_.TaskPath
                    State = [string]$_.State
                    Author = [string]$_.Author
                    Action = (@($_.Actions | ForEach-Object { (([string]$_.Execute) + ' ' + ([string]$_.Arguments)).Trim() }) -join ' ; ')
                }
            } | Where-Object { $_.Path -notlike '\Microsoft\*' })
            ConvertTo-Json -InputObject $items -Depth 4 -Compress
            """;

        return await QueryAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScheduledTaskInfo>> QueryTasksUnderPathAsync(string path, CancellationToken ct)
    {
        var escaped = Escape(path);
        var script = $@"
$items = @(Get-ScheduledTask -TaskPath '{escaped}' -ErrorAction SilentlyContinue | ForEach-Object {{
    [pscustomobject]@{{
        Name = [string]$_.TaskName
        Path = [string]$_.TaskPath
        State = [string]$_.State
        Author = [string]$_.Author
        Action = (@($_.Actions | ForEach-Object {{ (([string]$_.Execute) + ' ' + ([string]$_.Arguments)).Trim() }}) -join ' ; ')
    }}
}})
ConvertTo-Json -InputObject $items -Depth 4 -Compress
";
        return await QueryAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ScheduledTaskInfo>> QueryAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        var result = await _runner.RunAsync(script, timeout, ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return Array.Empty<ScheduledTaskInfo>();

        try
        {
            var items = JsonSerializer.Deserialize<List<ScheduledTaskInfo>>(result.StdOut, JsonConfig.Options)
                        ?? new List<ScheduledTaskInfo>();
            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"解析计划任务失败: {ex.Message}");
            return Array.Empty<ScheduledTaskInfo>();
        }
    }

    public async Task<string> GetStateAsync(string taskPath, string taskName, CancellationToken ct)
    {
        var script = $"(Get-ScheduledTask -TaskPath '{Escape(taskPath)}' -TaskName '{Escape(taskName)}' -ErrorAction Stop).State";
        var result = await _runner.RunAsync(script, TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        return result.Success ? result.StdOut.Trim() : string.Empty;
    }

    public async Task<OperationResult> SetEnabledAsync(string taskPath, string taskName, bool enabled, CancellationToken ct)
    {
        var action = enabled ? "Enable-ScheduledTask" : "Disable-ScheduledTask";
        var escapedPath = Escape(taskPath);
        var escapedName = Escape(taskName);
        var script = $@"
$t = Get-ScheduledTask -TaskPath '{escapedPath}' -TaskName '{escapedName}' -ErrorAction Stop
{action} -TaskPath '{escapedPath}' -TaskName '{escapedName}' -ErrorAction Stop | Out-Null
";

        var result = await _runner.RunAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"计划任务操作失败（exit {result.ExitCode}）"
                : result.StdErr.Trim());
        }

        var state = await GetStateAsync(taskPath, taskName, ct).ConfigureAwait(false);
        var isEnabled = !string.Equals(state, "Disabled", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(state);
        if (isEnabled != enabled) return OperationResult.Fail("计划任务状态校验失败");

        return OperationResult.Ok($"计划任务已{(enabled ? "启用" : "禁用")}");
    }

    private static string Escape(string value) => value.Replace("'", "''");
}

public sealed class ScheduledTaskToggleOperation : OperationBase
{
    private readonly ScheduledTaskService _tasks;
    private readonly string _path;
    private readonly string _name;
    private readonly bool _enable;

    public ScheduledTaskToggleOperation(ScheduledTaskService tasks, string path, string name, bool enable)
    {
        _tasks = tasks;
        _path = path;
        _name = name;
        _enable = enable;
    }

    public override string Id => "startup.task.toggle";
    public override string Name => $"{( _enable ? "启用" : "禁用")}计划任务「{_name}」";
    public override string Description => "通过 Windows 计划任务服务启用/禁用登录或开机触发的任务";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = $@"任务计划程序\{_path.TrimEnd('\\')}\{_name}",
            Kind = "计划任务",
            Before = _enable ? "已禁用" : "已启用",
            After = _enable ? "已启用" : "已禁用",
            Impact = "影响登录时的启动行为；任务本身不会被删除"
        }
    };

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var state = await _tasks.GetStateAsync(_path, _name, ct).ConfigureAwait(false);
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.ScheduledTask,
            Target = $"{_path}|{_name}",
            Existed = !string.IsNullOrEmpty(state),
            DataJson = JsonSerializer.Serialize(new TaskState { Path = _path, Name = _name, State = state }, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在修改计划任务…", Percent = 60 });
        return await _tasks.SetEnabledAsync(_path, _name, _enable, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var entry = state.Entries.FirstOrDefault(e => e.Kind == SnapshotKind.ScheduledTask);
        if (entry?.DataJson is null) return OperationResult.Fail("计划任务快照损坏");
        var original = JsonSerializer.Deserialize<TaskState>(entry.DataJson, JsonConfig.Options);
        if (original is null) return OperationResult.Fail("计划任务快照损坏");

        progress?.Report(new OperationStep { Message = "正在恢复计划任务…", Percent = 60 });
        var enabled = !string.Equals(original.State, "Disabled", StringComparison.OrdinalIgnoreCase);
        return await _tasks.SetEnabledAsync(original.Path, original.Name, enabled, ct).ConfigureAwait(false);
    }

    private sealed class TaskState
    {
        public string Path { get; set; } = "\\";
        public string Name { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
    }
}
