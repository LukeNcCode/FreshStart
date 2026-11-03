using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreshStart.Core.Abstractions;
using FreshStart.Core.Settings;

namespace FreshStart.Core.Backup;

public sealed class BackupManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string AppVersion { get; set; } = "0.1.0";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string OperationId { get; set; } = string.Empty;
    public string OperationName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public Operations.RiskLevel Risk { get; set; }
    public bool CanRollback { get; set; }
    public bool RequiresRestart { get; set; }
    public List<Operations.ChangeDescriptor> Changes { get; set; } = new();
    public string SnapshotFile { get; set; } = "snapshot.json";
    public string Sha256 { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string? RestorePoint { get; set; }
}

/// <summary>
/// 每次写入前把快照落盘到 backup\{yyyyMMdd-HHmmss}-{opId}\。
/// JSON 可读、可导出，任何一步失败都不会执行写入。
/// </summary>
public sealed class JsonBackupStore : IBackupStore
{
    private readonly string _root;

    public JsonBackupStore(string? root = null) => _root = root ?? AppPaths.BackupRoot;

    public async Task<BackupHandle> SaveAsync(
        Operations.OperationSnapshot snapshot,
        Operations.OperationPreview preview,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_root);

        var opSlug = Sanitize(preview.OperationId);
        if (opSlug.Length > 30) opSlug = opSlug[..30];
        // 目录名：{yyyyMMdd-HHmmss}-{opId}-{guid8}（与 ARCHITECTURE.md 一致，guid 取 8 位）
        var folderName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{opSlug}-{Guid.NewGuid():N}"[..(16 + opSlug.Length + 9)];
        var dir = Path.Combine(_root, folderName);
        Directory.CreateDirectory(dir);

        var snapshotPath = Path.Combine(dir, "snapshot.json");
        var manifestPath = Path.Combine(dir, "manifest.json");

        var snapshotJson = JsonSerializer.Serialize(snapshot, JsonConfig.Options);
        await AtomicFile.WriteAllTextAsync(snapshotPath, snapshotJson, ct).ConfigureAwait(false);

        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();

        var manifest = new BackupManifest
        {
            CreatedAt = snapshot.CapturedAt,
            OperationId = preview.OperationId,
            OperationName = preview.Name,
            Description = preview.Description,
            Risk = preview.Risk,
            CanRollback = preview.CanRollback,
            RequiresRestart = preview.RequiresRestart,
            Changes = preview.Changes,
            Sha256 = sha,
            OsVersion = snapshot.OsVersion
        };

        await AtomicFile.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonConfig.Options), ct).ConfigureAwait(false);

        // 写入后立即做一次完整性校验，确保备份可用再继续
        var verify = await File.ReadAllTextAsync(snapshotPath, ct).ConfigureAwait(false);
        if (verify != snapshotJson) throw new IOException("备份文件校验失败");

        Log.Info($"备份完成: {dir}");
        return new BackupHandle { Directory = dir, SnapshotPath = snapshotPath, ManifestPath = manifestPath, Sha256 = sha };
    }

    public async Task<Operations.OperationSnapshot?> LoadAsync(string directory, CancellationToken ct = default)
    {
        var path = Path.Combine(directory, "snapshot.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<Operations.OperationSnapshot>(json, JsonConfig.Options);
    }

    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? char.ToLowerInvariant(ch) : '-');
        }
        var result = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? "op" : result[..Math.Min(result.Length, 40)];
    }
}
