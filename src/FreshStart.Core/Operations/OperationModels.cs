namespace FreshStart.Core.Operations;

public enum RiskLevel
{
    /// <summary>只读或可完全回滚的轻量配置。</summary>
    Safe = 0,

    /// <summary>修改用户级配置，通常可回滚，但可能影响使用习惯。</summary>
    Caution = 1,

    /// <summary>涉及服务、系统策略、包管理或数据删除，必须二次确认。</summary>
    High = 2
}

/// <summary>单项变更描述，用于"操作预演"弹窗，让用户在点击前看到会改什么。</summary>
public sealed class ChangeDescriptor
{
    public string Target { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? Before { get; set; }
    public string? After { get; set; }
    public string Impact { get; set; } = string.Empty;
}

/// <summary>一次操作的完整预览。</summary>
public sealed class OperationPreview
{
    public string OperationId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public bool CanRollback { get; set; } = true;
    public bool RequiresRestart { get; set; }
    public List<ChangeDescriptor> Changes { get; set; } = new();
}

public enum SnapshotKind
{
    RegistryValue,
    RegistryKey,
    Service,
    File,
    ScheduledTask,
    Other
}

/// <summary>
/// 通用快照条目。不同 Kind 使用 DataJson 承载各自载荷，
/// 由具体操作负责序列化/反序列化，Core 不依赖任何 Windows 类型。
/// </summary>
public sealed class SnapshotEntry
{
    public SnapshotKind Kind { get; set; }
    public string Target { get; set; } = string.Empty;
    public string? ValueName { get; set; }
    public bool Existed { get; set; }
    public string? ValueType { get; set; }
    public string? DataJson { get; set; }
    public string? Sddl { get; set; }
    public string? Aux { get; set; }
}

/// <summary>写入前捕获的系统状态，是回滚的唯一依据。</summary>
public sealed class OperationSnapshot
{
    public string OperationId { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
    public string AppVersion { get; set; } = "0.1.0";
    public string OsVersion { get; set; } = Environment.OSVersion.VersionString;
    public List<SnapshotEntry> Entries { get; set; } = new();
}

public sealed class OperationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public Exception? Error { get; set; }

    public static OperationResult Ok(string message = "完成") => new() { Success = true, Message = message };
    public static OperationResult Fail(string message, Exception? error = null) => new() { Success = false, Message = message, Error = error };
}

public sealed class OperationStep
{
    public string Message { get; set; } = string.Empty;
    public int? Percent { get; set; }
    public bool IsError { get; set; }
}

public enum OperationActivityPhase
{
    Started,
    Progress,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// 全局任务控制台事件。Runner 在操作开始、进度更新和结束时发布，
/// App 层负责渲染；Core 不依赖 WPF。
/// </summary>
public sealed class OperationActivity
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string OperationId { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int? Percent { get; set; }
    public bool IsError { get; set; }
    public OperationActivityPhase Phase { get; set; }
    public RiskLevel Risk { get; set; }
}

/// <summary>一次执行的结果，UI 用它决定 Toast、错误面板与是否显示"撤销"。</summary>
public sealed class OperationExecution
{
    public OperationPreview Preview { get; set; } = new();
    public OperationSnapshot? Snapshot { get; set; }
    public string? BackupDirectory { get; set; }
    public OperationResult Result { get; set; } = OperationResult.Ok();
    public bool Cancelled { get; set; }
    public bool RolledBack { get; set; }
    public bool RollbackFailed { get; set; }
    public string? RestorePointMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.Now;

    public bool Success => !Cancelled && Result.Success;
}
