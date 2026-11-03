using FreshStart.Core.Operations;

namespace FreshStart.Core.Abstractions;

public sealed class BackupHandle
{
    public string Directory { get; set; } = string.Empty;
    public string SnapshotPath { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
}

/// <summary>备份存储。必须在 Apply 之前成功返回，否则整个操作不得执行。</summary>
public interface IBackupStore
{
    Task<BackupHandle> SaveAsync(OperationSnapshot snapshot, OperationPreview preview, CancellationToken ct = default);
    Task<OperationSnapshot?> LoadAsync(string directory, CancellationToken ct = default);
}
