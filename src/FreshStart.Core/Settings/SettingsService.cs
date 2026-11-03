using System.Text.Json;

namespace FreshStart.Core.Settings;

/// <summary>
/// 设置持久化。读取是同步的（文件极小，&lt;2KB），写入走后台且原子替换。
/// </summary>
public sealed class SettingsService
{
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private AppSettings _current = new();

    public AppSettings Current => _current;

    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                _current = new AppSettings();
                return;
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonConfig.Options);
            if (loaded is null) throw new InvalidDataException("设置文件为空");

            // 逐字段兜底，避免手工编辑后出现非法值
            if (loaded.Theme is not ("Light" or "Dark")) loaded.Theme = "Light";
            if (loaded.WindowWidth < 900) loaded.WindowWidth = 1100;
            if (loaded.WindowHeight < 600) loaded.WindowHeight = 720;
            if (loaded.BackupRetentionDays is < 1 or > 3650) loaded.BackupRetentionDays = 60;
            if (string.IsNullOrWhiteSpace(loaded.LastPage)) loaded.LastPage = "home";

            _current = loaded;
            Log.Info("设置已加载");
        }
        catch (Exception ex)
        {
            Log.Warn($"读取设置失败，使用默认值: {ex.Message}");
            TryQuarantineBrokenFile();
            _current = new AppSettings();
        }
    }

    /// <summary>修改设置并异步保存。所有设置变更都应走这里。</summary>
    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        mutate(_current);
        Changed?.Invoke(this, EventArgs.Empty);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        await _saveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(_current, JsonConfig.Options);
            await AtomicFile.WriteAllTextAsync(AppPaths.SettingsFile, json, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("保存设置失败", ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void SaveNow()
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = JsonSerializer.Serialize(_current, JsonConfig.Options);
            AtomicFile.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            Log.Error("同步保存设置失败", ex);
        }
    }

    private static void TryQuarantineBrokenFile()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return;
            var bad = AppPaths.SettingsFile + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(AppPaths.SettingsFile, bad, true);
            Log.Warn($"损坏的设置文件已重命名为: {bad}");
        }
        catch (Exception ex)
        {
            Log.Warn($"隔离损坏设置文件失败: {ex.Message}");
        }
    }
}

/// <summary>原子写文件：先写 .tmp，再替换目标，避免断电产生半截 JSON。</summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string content, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(tmp, content, new System.Text.UTF8Encoding(false), ct).ConfigureAwait(false);
        Replace(tmp, path);
    }

    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        Replace(tmp, path);
    }

    private static void Replace(string tmp, string path)
    {
        if (File.Exists(path))
        {
            File.Replace(tmp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }
}
