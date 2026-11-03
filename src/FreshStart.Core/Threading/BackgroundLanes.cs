namespace FreshStart.Core.Threading;

public enum WorkLane
{
    /// <summary>用户显式触发的写操作（Runner 内部还有串行锁）。</summary>
    P0 = 0,

    /// <summary>当前页面可见时需要的数据，离开页面立即取消。</summary>
    P1 = 1,

    /// <summary>预热、缓存刷新，空闲时才跑。</summary>
    P2 = 2
}

/// <summary>
/// 后台任务分道调度：限制每档并发，避免开机瞬间几十个任务抢线程池。
/// </summary>
public sealed class BackgroundLanes : IDisposable
{
    private readonly SemaphoreSlim _p0 = new(1, 1);
    private readonly SemaphoreSlim _p1 = new(2, 2);
    private readonly SemaphoreSlim _p2 = new(1, 1);
    private bool _disposed;

    public int Running { get; private set; }

    public async Task RunAsync(WorkLane lane, string name, Func<CancellationToken, Task> work, CancellationToken ct = default)
    {
        var gate = lane switch
        {
            WorkLane.P0 => _p0,
            WorkLane.P1 => _p1,
            _ => _p2
        };

        await gate.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _running);
        try
        {
            await work(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 页面切换导致的取消属于正常路径
        }
        catch (Exception ex)
        {
            Log.Error($"后台任务失败 [{lane}] {name}", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            gate.Release();
        }
    }

    private int _running;
    public int ActiveCount => Volatile.Read(ref _running);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _p0.Dispose();
        _p1.Dispose();
        _p2.Dispose();
    }
}
