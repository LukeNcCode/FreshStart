using System.Text;
using System.Text.Json;
using FreshStart.Core.Operations;

namespace FreshStart.Core.Operations;

/// <summary>审计记录。一行一条 JSON，追加写，永不修改历史。</summary>
public sealed class OperationRecord
{
    public string ExecutionId { get; set; } = Guid.NewGuid().ToString("N");
    public string OperationId { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public RiskLevel Risk { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool RolledBack { get; set; }
    public bool RollbackFailed { get; set; }
    public string? BackupDirectory { get; set; }
    public string? Message { get; set; }
    public string? RestorePointMessage { get; set; }
    public int ChangeCount { get; set; }
}

/// <summary>操作历史栈：内存里维护可撤销列表，磁盘上维护完整审计。</summary>
public sealed class OperationHistoryStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly List<OperationRecord> _recent = new();

    public IReadOnlyList<OperationRecord> Recent
    {
        get { lock (_recent) return _recent.ToList(); }
    }

    public event EventHandler? Changed;

    public async Task AppendAsync(OperationRecord record, CancellationToken ct = default)
    {
        lock (_recent)
        {
            _recent.Insert(0, record);
            if (_recent.Count > 50) _recent.RemoveRange(50, _recent.Count - 50);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(AppPaths.HistoryRoot);
            var line = JsonSerializer.Serialize(record, JsonConfig.Compact) + Environment.NewLine;
            await using var fs = new FileStream(AppPaths.HistoryFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(fs, new UTF8Encoding(false));
            await writer.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("写入操作历史失败", ex);
        }
        finally
        {
            _lock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<OperationRecord>> LoadRecentAsync(int count = 20, CancellationToken ct = default)
    {
        var result = new List<OperationRecord>();
        try
        {
            if (!File.Exists(AppPaths.HistoryFile)) return result;
            var lines = await File.ReadAllLinesAsync(AppPaths.HistoryFile, ct).ConfigureAwait(false);
            foreach (var line in lines.Reverse())
            {
                if (result.Count >= count) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var record = JsonSerializer.Deserialize<OperationRecord>(line, JsonConfig.Options);
                    if (record is not null) result.Add(record);
                }
                catch { /* 跳过损坏行 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取历史失败: {ex.Message}");
        }
        return result;
    }
}
