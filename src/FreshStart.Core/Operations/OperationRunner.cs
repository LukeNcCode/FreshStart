using FreshStart.Core.Abstractions;
using FreshStart.Core.Backup;
using FreshStart.Core.Operations;

namespace FreshStart.Core.Operations;

/// <summary>
/// 唯一允许执行写操作的门。强制四步：读当前值 → 备份 → 写入 → 失败自动回滚。
/// 同一时刻只允许一个写操作，避免并发修改注册表/服务导致状态错乱。
/// </summary>
public sealed class OperationRunner
{
    private readonly IBackupStore _backupStore;
    private readonly IOperationGate _gate;
    private readonly IRestorePointService _restorePoints;
    private readonly OperationHistoryStore _history;
    private readonly IWriteLock _writeLock;
    private readonly List<UndoEntry> _undoStack = new();
    private readonly object _undoLock = new();

    public OperationRunner(
        IBackupStore backupStore,
        IOperationGate gate,
        IRestorePointService restorePoints,
        OperationHistoryStore history,
        IWriteLock? writeLock = null)
    {
        _backupStore = backupStore;
        _gate = gate;
        _restorePoints = restorePoints;
        _history = history;
        _writeLock = writeLock ?? new Threading.WriteLock();
    }

    /// <summary>由设置页控制；每次启动默认 true。</summary>
    public bool AskRestorePointOnFirstWrite { get; set; } = true;

    /// <summary>本次进程内用户对还原点的选择，重启后重置。</summary>
    public RestorePointChoice SessionRestorePointChoice { get; private set; } = RestorePointChoice.NotDecided;

    public bool CanUndo
    {
        get { lock (_undoLock) return _undoStack.Count > 0; }
    }

    public string? LastUndoName
    {
        get { lock (_undoLock) return _undoStack.Count > 0 ? _undoStack[^1].Preview.Name : null; }
    }

    public event EventHandler? HistoryChanged;

    public async Task<OperationExecution> ExecuteAsync(
        ISystemOperation operation,
        IProgress<OperationStep>? progress = null,
        CancellationToken ct = default)
    {
        var preview = BuildPreview(operation);
        var askRestorePoint = AskRestorePointOnFirstWrite && SessionRestorePointChoice == RestorePointChoice.NotDecided;

        var consent = await _gate.RequestConsentAsync(preview, askRestorePoint, ct).ConfigureAwait(false);
        if (!consent.Approved)
        {
            return new OperationExecution { Preview = preview, Cancelled = true, Result = OperationResult.Fail("用户取消") };
        }

        if (askRestorePoint)
        {
            SessionRestorePointChoice = consent.RestorePoint == RestorePointChoice.NotDecided
                ? RestorePointChoice.DisabledForSession
                : consent.RestorePoint;
        }

        using var lease = await _writeLock.EnterAsync(ct).ConfigureAwait(false);
        var execution = new OperationExecution { Preview = preview, StartedAt = DateTimeOffset.Now };
        try
        {
            // 第 1 步：读取当前值（失败则不产生任何写入）
            progress?.Report(new OperationStep { Message = "正在读取当前状态…", Percent = 10 });
            var snapshot = await operation.CaptureCurrentStateAsync(ct).ConfigureAwait(false);
            execution.Snapshot = snapshot;

            // 第 2 步：备份（失败则中止）
            progress?.Report(new OperationStep { Message = "正在创建备份…", Percent = 25 });
            var handle = await _backupStore.SaveAsync(snapshot, preview, ct).ConfigureAwait(false);
            execution.BackupDirectory = handle.Directory;

            // 可选：系统还原点（仅高风险 + 用户本次会话开启）
            if (SessionRestorePointChoice == RestorePointChoice.EnabledForSession &&
                operation.RiskLevel == RiskLevel.High)
            {
                progress?.Report(new OperationStep { Message = "正在创建系统还原点（可能需要数秒）…", Percent = 35 });
                var rp = await _restorePoints.CreateAsync($"FreshStart · {operation.Name}", ct).ConfigureAwait(false);
                execution.RestorePointMessage = rp.Message;
                if (!rp.Success)
                {
                    // 还原点失败不阻断操作，但必须记录，JSON 快照仍然可用
                    Log.Warn($"系统还原点创建失败: {rp.Message}");
                }
            }

            // 第 3 步：写入
            progress?.Report(new OperationStep { Message = "正在应用变更…", Percent = 55 });
            OperationResult applyResult;
            try
            {
                applyResult = await operation.ApplyAsync(snapshot, progress, ct).ConfigureAwait(false);
                if (!applyResult.Success)
                {
                    throw new OperationFailedException(applyResult.Message, applyResult.Error);
                }
            }
            catch (Exception ex)
            {
                // 取消可能与"已写入一半"同时发生：只要能回滚就回滚，不能留下半应用状态
                var cancelled = ex is OperationCanceledException;
                if (operation.CanRollback)
                {
                    Log.Error(cancelled
                        ? $"操作被取消，开始回滚已应用的步骤: {operation.Id}"
                        : $"操作失败，开始回滚: {operation.Id}", ex);
                    progress?.Report(new OperationStep
                    {
                        Message = cancelled ? "已取消，正在回滚已应用的步骤…" : "执行失败，正在自动回滚…",
                        Percent = 80,
                        IsError = true
                    });
                    execution.Cancelled = cancelled;
                    execution.RolledBack = true;
                    var rollback = await operation.RollbackAsync(snapshot, progress, CancellationToken.None).ConfigureAwait(false);
                    execution.RollbackFailed = !rollback.Success;
                    execution.Result = OperationResult.Fail(
                        execution.RollbackFailed
                            ? (cancelled
                                ? $"已取消，但自动回滚未完全成功：{ex.Message}。备份目录：{execution.BackupDirectory}"
                                : $"执行失败且自动回滚未完全成功：{ex.Message}。备份目录：{execution.BackupDirectory}")
                            : (cancelled
                                ? "已取消，已回滚本次已应用的变更"
                                : $"执行失败，已自动回滚：{ex.Message}"),
                        cancelled ? null : ex);
                }
                else
                {
                    Log.Error(cancelled
                        ? $"操作被取消（该操作不支持回滚）: {operation.Id}"
                        : $"操作失败（该操作不支持回滚）: {operation.Id}", ex);
                    execution.Cancelled = cancelled;
                    execution.Result = OperationResult.Fail(
                        cancelled
                            ? "已取消（该操作不支持自动回滚，已执行的步骤不会恢复）"
                            : $"执行失败（该操作不支持自动回滚）：{ex.Message}",
                        cancelled ? null : ex);
                }
                execution.FinishedAt = DateTimeOffset.Now;
                await AppendHistoryAsync(execution, operation).ConfigureAwait(false);
                return execution;
            }

            // 第 4 步（成功分支）：只有可回滚的操作才进入撤销栈
            progress?.Report(new OperationStep { Message = "完成", Percent = 100 });
            execution.Result = OperationResult.Ok(applyResult.Message);
            execution.FinishedAt = DateTimeOffset.Now;
            if (operation.CanRollback)
            {
                PushUndo(operation, snapshot, handle.Directory, preview);
            }
            await AppendHistoryAsync(execution, operation).ConfigureAwait(false);
            Log.Info($"操作成功: {operation.Id}，备份: {handle.Directory}");
            return execution;
        }
        catch (OperationCanceledException)
        {
            execution.Cancelled = true;
            execution.Result = OperationResult.Fail("已取消");
            execution.FinishedAt = DateTimeOffset.Now;
            await AppendHistoryAsync(execution, operation).ConfigureAwait(false);
            return execution;
        }
        catch (Exception ex)
        {
            execution.Result = OperationResult.Fail($"操作中止：{ex.Message}", ex);
            execution.FinishedAt = DateTimeOffset.Now;
            Log.Error($"操作中止: {operation.Id}", ex);
            await AppendHistoryAsync(execution, operation).ConfigureAwait(false);
            return execution;
        }
    }

    /// <summary>撤销上一次成功操作。回滚同样受写锁保护。</summary>
    public async Task<OperationExecution?> UndoLastAsync(IProgress<OperationStep>? progress = null, CancellationToken ct = default)
    {
        UndoEntry? entry;
        lock (_undoLock)
        {
            if (_undoStack.Count == 0) return null;
            entry = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
        }

        using var lease = await _writeLock.EnterAsync(ct).ConfigureAwait(false);

        progress?.Report(new OperationStep { Message = $"正在撤销：{entry.Preview.Name}", Percent = 20 });
        // 一旦开始撤销就必须完整执行；取消只允许发生在获得写锁之前。
        var result = await entry.Operation.RollbackAsync(
            entry.Snapshot, progress, CancellationToken.None).ConfigureAwait(false);
        var execution = new OperationExecution
        {
            Preview = entry.Preview,
            Snapshot = entry.Snapshot,
            BackupDirectory = entry.BackupDirectory,
            Result = result,
            RolledBack = result.Success,
            RollbackFailed = !result.Success,
            StartedAt = DateTimeOffset.Now,
            FinishedAt = DateTimeOffset.Now
        };
        await _history.AppendAsync(new OperationRecord
        {
            OperationId = entry.Preview.OperationId,
            OperationName = $"撤销 · {entry.Preview.Name}",
            Risk = entry.Preview.Risk,
            StartedAt = execution.StartedAt,
            FinishedAt = execution.FinishedAt,
            Success = result.Success,
            RolledBack = result.Success,
            Message = result.Message,
            BackupDirectory = entry.BackupDirectory,
            ChangeCount = entry.Preview.Changes.Count
        }, CancellationToken.None).ConfigureAwait(false);

        // 撤销失败时把条目放回栈顶，用户可以修好问题后重试，而不是丢掉撤销能力
        if (!result.Success)
        {
            lock (_undoLock)
            {
                _undoStack.Add(entry);
                if (_undoStack.Count > 20) _undoStack.RemoveAt(0);
            }
        }

        RaiseHistoryChanged();
        return execution;
    }

    private void PushUndo(ISystemOperation operation, OperationSnapshot snapshot, string backupDir, OperationPreview preview)
    {
        lock (_undoLock)
        {
            _undoStack.Add(new UndoEntry(operation, snapshot, backupDir, preview));
            if (_undoStack.Count > 20) _undoStack.RemoveAt(0);
        }
        RaiseHistoryChanged();
    }

    private async Task AppendHistoryAsync(OperationExecution execution, ISystemOperation operation)
    {
        await _history.AppendAsync(new OperationRecord
        {
            OperationId = operation.Id,
            OperationName = operation.Name,
            Risk = operation.RiskLevel,
            StartedAt = execution.StartedAt,
            FinishedAt = execution.FinishedAt,
            Success = execution.Success,
            Cancelled = execution.Cancelled,
            RolledBack = execution.RolledBack,
            RollbackFailed = execution.RollbackFailed,
            BackupDirectory = execution.BackupDirectory,
            Message = execution.Result.Message,
            RestorePointMessage = execution.RestorePointMessage,
            ChangeCount = execution.Preview.Changes.Count
        }).ConfigureAwait(false);
        RaiseHistoryChanged();
    }

    /// <summary>订阅者异常不能影响操作结果（UI 事件可能在不同线程上触发）。</summary>
    private void RaiseHistoryChanged()
    {
        try { HistoryChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Warn($"历史变更事件处理失败: {ex.Message}"); }
    }

    private static OperationPreview BuildPreview(ISystemOperation operation) => new()
    {
        OperationId = operation.Id,
        Name = operation.Name,
        Description = operation.Description,
        Risk = operation.RiskLevel,
        CanRollback = operation.CanRollback,
        Changes = operation.DescribeChanges().ToList()
    };

    private sealed record UndoEntry(
        ISystemOperation Operation,
        OperationSnapshot Snapshot,
        string BackupDirectory,
        OperationPreview Preview);
}

public sealed class OperationFailedException : Exception
{
    public OperationFailedException(string message, Exception? inner = null) : base(message, inner) { }
}
