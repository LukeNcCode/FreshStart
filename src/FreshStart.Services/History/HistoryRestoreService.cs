using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Backup;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;
using FreshStart.Services.Startup;

namespace FreshStart.Services.History;

public sealed class HistoryRestoreResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int Restored { get; init; }
    public int Skipped { get; init; }
}

/// <summary>
/// 从历史备份恢复：按快照条目类型回放注册表值/键、服务启动类型。
/// 文件、计划任务、Appx 等没有通用回放器的条目会跳过并计数。
/// </summary>
public sealed class HistoryRestoreService
{
    private readonly RegistryService _registry;
    private readonly ServiceControlService _services;
    private readonly JsonBackupStore _backups = new();
    private readonly FreshStart.Core.Abstractions.IWriteLock _writeLock;

    public HistoryRestoreService(
        RegistryService registry,
        ServiceControlService services,
        FreshStart.Core.Abstractions.IWriteLock? writeLock = null)
    {
        _registry = registry;
        _services = services;
        _writeLock = writeLock ?? new FreshStart.Core.Threading.WriteLock();
    }

    public Task<IReadOnlyList<OperationRecord>> ListAsync(int count = 50)
        => new OperationHistoryStore().LoadRecentAsync(count);

    public async Task<HistoryRestoreResult> RestoreAsync(OperationRecord record, CancellationToken ct)
    {
        // 回滚会写注册表与服务，必须与其它写入串行
        using var lease = await _writeLock.EnterAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(record.BackupDirectory) || !Directory.Exists(record.BackupDirectory))
        {
            return new HistoryRestoreResult { Success = false, Message = "备份目录不存在" };
        }

        // BackupDirectory 来自 operations.jsonl（用户可写），必须限定在备份根目录内
        if (!IsInsideBackupRoot(record.BackupDirectory))
        {
            Log.Warn($"拒绝回滚：备份目录不在备份根目录内 - {record.BackupDirectory}");
            return new HistoryRestoreResult { Success = false, Message = "备份目录不在 FreshStart 备份根目录内，已拒绝回滚" };
        }

        var snapshot = await _backups.LoadAsync(record.BackupDirectory, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return new HistoryRestoreResult { Success = false, Message = "快照文件不存在或损坏" };
        }

        // 回放前比对 manifest 里的 SHA256，防止被篡改/损坏的快照写回系统
        if (!await VerifySnapshotAsync(record.BackupDirectory, ct).ConfigureAwait(false))
        {
            Log.Warn($"拒绝回滚：快照校验失败 - {record.BackupDirectory}");
            return new HistoryRestoreResult { Success = false, Message = "快照校验失败（SHA256 不匹配），已拒绝回滚" };
        }

        var restored = 0;
        var skipped = 0;
        var errors = new List<string>();

        foreach (var entry in snapshot.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                switch (entry.Kind)
                {
                    case SnapshotKind.RegistryValue:
                    {
                        var state = JsonSerializer.Deserialize<RegistryValueState>(entry.DataJson!, JsonConfig.Options);
                        if (state is null) { skipped++; break; }
                        _registry.ApplyValue(state);
                        restored++;
                        break;
                    }
                    case SnapshotKind.RegistryKey:
                    {
                        var state = JsonSerializer.Deserialize<RegistryKeyState>(entry.DataJson!, JsonConfig.Options);
                        if (state is null) { skipped++; break; }
                        _registry.RestoreKey(state);
                        restored++;
                        break;
                    }
                    case SnapshotKind.Service:
                    {
                        var state = JsonSerializer.Deserialize<ServiceState>(entry.DataJson!, JsonConfig.Options);
                        if (state is null || !state.Exists) { skipped++; break; }
                        var result = await _services.SetStartModeAsync(
                            state.Name, state.StartType, state.DelayedAutoStart, ct).ConfigureAwait(false);
                        if (result.Success) restored++;
                        else errors.Add($"{state.Name}: {result.Message}");
                        break;
                    }
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Target}: {ex.Message}");
            }
        }

        var message = $"已恢复 {restored} 项，跳过 {skipped} 项";
        if (errors.Count > 0) message += $"；失败 {errors.Count} 项：" + string.Join("；", errors.Take(3));
        Log.Info($"历史回滚 {record.OperationId}: {message}");
        return new HistoryRestoreResult
        {
            // 一条都没恢复时不能报成功；有跳过项时 Message 已明确说明能力边界
            Success = errors.Count == 0 && restored > 0,
            Message = message,
            Restored = restored,
            Skipped = skipped
        };
    }

    private static bool IsInsideBackupRoot(string directory)
    {
        string full;
        string root;
        try
        {
            full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            root = Path.GetFullPath(AppPaths.BackupRoot).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch { return false; }

        if (full.Length <= root.Length) return false;
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>比对 manifest.json 里的 SHA256 与 snapshot.json 实际内容。</summary>
    private static async Task<bool> VerifySnapshotAsync(string directory, CancellationToken ct)
    {
        try
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            var snapshotPath = Path.Combine(directory, "snapshot.json");
            if (!File.Exists(manifestPath) || !File.Exists(snapshotPath)) return false;

            var manifest = JsonSerializer.Deserialize<BackupManifest>(
                await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false), JsonConfig.Options);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Sha256)) return false;

            var json = await File.ReadAllTextAsync(snapshotPath, ct).ConfigureAwait(false);
            var actual = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn($"校验快照失败: {directory} - {ex.Message}");
            return false;
        }
    }
}
