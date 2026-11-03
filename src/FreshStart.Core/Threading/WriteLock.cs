using FreshStart.Core.Abstractions;

namespace FreshStart.Core.Threading;

/// <summary>
/// 全局写锁：进程内 SemaphoreSlim + 跨进程独占文件锁，**不可重入**。
/// 复合操作内部如果需要调用同样加锁的服务，请使用该服务提供的"已持锁"版本
/// （例如 <c>CleanupService.CleanInsideWriteLockAsync</c>），而不是再次 EnterAsync。
/// </summary>
public sealed class WriteLock : IWriteLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly string _crossProcessLockPath;

    public WriteLock(string? crossProcessLockPath = null)
    {
        _crossProcessLockPath = crossProcessLockPath ?? ResolveLockPath();
    }

    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var crossProcessLease = await AcquireCrossProcessLeaseAsync(ct).ConfigureAwait(false);
            return new Lease(_semaphore, crossProcessLease);
        }
        catch
        {
            _semaphore.Release();
            throw;
        }
    }

    private async Task<FileStream?> AcquireCrossProcessLeaseAsync(CancellationToken ct)
    {
        try
        {
            var directory = Path.GetDirectoryName(_crossProcessLockPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return new FileStream(
                        _crossProcessLockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.WriteThrough);
                }
                catch (IOException)
                {
                    await Task.Delay(120, ct).ConfigureAwait(false);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // 开发模式可能没有 ProgramData 写权限；发布版 Host 始终以管理员运行。
            Log.Warn($"跨进程写锁不可用，退化为进程内写锁: {ex.Message}");
            return null;
        }
    }

    private static string ResolveLockPath()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(common))
        {
            return Path.Combine(common, "FreshStart", "locks", "write.lock");
        }

        return Path.Combine(AppPaths.StateRoot, "write.lock");
    }

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;
        private FileStream? _crossProcessLease;

        public Lease(SemaphoreSlim semaphore, FileStream? crossProcessLease)
        {
            _semaphore = semaphore;
            _crossProcessLease = crossProcessLease;
        }

        public void Dispose()
        {
            var crossProcessLease = Interlocked.Exchange(ref _crossProcessLease, null);
            try
            {
                crossProcessLease?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"释放跨进程写锁失败: {ex.Message}");
            }

            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
        }
    }
}
