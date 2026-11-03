using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using FreshStart.Core;
using FreshStart.Services.Interop;

namespace FreshStart.Services.Performance;

public sealed class ProcessSample
{
    public int Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double CpuPercent { get; set; }
    public long WorkingSetBytes { get; set; }
}

public sealed class SystemMetricsSample
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public double CpuPercent { get; set; }
    public bool CpuAvailable { get; set; }
    public ulong TotalMemoryBytes { get; set; }
    public ulong AvailableMemoryBytes { get; set; }
    public double MemoryPercent { get; set; }
    public double DiskReadBytesPerSec { get; set; }
    public double DiskWriteBytesPerSec { get; set; }
    public double NetworkReceivedBytesPerSec { get; set; }
    public double NetworkSentBytesPerSec { get; set; }
    public double GpuPercent { get; set; }
    public bool GpuAvailable { get; set; }
    /// <summary>是否已成功读取过 GPU 计数器；未读到时 UI 显示“…”而不是“不可用”。</summary>
    public bool GpuSampled { get; set; }
    /// <summary>PDH 磁盘/GPU 计数器是否已初始化完成；未完成时 UI 显示“…”而不是 0。</summary>
    public bool CountersReady { get; set; }
    public List<ProcessSample> Processes { get; set; } = new();
}

/// <summary>
/// 资源采样器：所有读取在后台线程完成；UI 只接收快照。
/// CPU/内存用轻量 Win32，磁盘/GPU 用 PDH，网络用 NetworkInterface 差值，进程用 Process 差值。
/// 任何计数器不可用时降级为 0/不可用，不阻塞页面。
/// </summary>
public sealed class PerformanceSampler : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;
    private int _intervalMs = 1000;
    private volatile bool _countersReady;
    private int _countersInitializing;

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuBaseline;
    private long _prevNetRx;
    private long _prevNetTx;
    private bool _hasNetBaseline;
    private DateTime _lastSampleAt = DateTime.UtcNow;
    private DateTime _lastProcessAt = DateTime.MinValue;
    private DateTime _lastGpuAt = DateTime.MinValue;
    private readonly Dictionary<int, TimeSpan> _prevProcessCpu = new();
    private List<ProcessSample> _lastProcesses = new();
    private double _lastGpuPercent;
    private bool _lastGpuAvailable = true;

    private PerformanceCounter? _diskRead;
    private PerformanceCounter? _diskWrite;
    private bool _diskAvailable = true;
    private bool _gpuAvailable = true;
    private bool _gpuHasValue;
    private int _gpuFailures;
    private readonly Dictionary<string, CounterSample> _prevGpuSamples = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<SystemMetricsSample>? Sampled;

    public bool IsRunning
    {
        get { lock (_lock) return _task is { IsCompleted: false }; }
    }

    public void Start(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMilliseconds(500)) interval = TimeSpan.FromMilliseconds(500);
        lock (_lock)
        {
            _intervalMs = (int)interval.TotalMilliseconds;
            if (_task is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            _task = Task.Run(() => LoopAsync(_cts.Token));
        }

        // PDH 计数器首次初始化可能耗时数秒（加载性能计数器库）。
        // 放到后台任务，采样循环立即返回 CPU/内存/网络/进程，磁盘与 GPU 就绪后再补上。
        _ = Task.Run(InitializeCounters);
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _cts;
            _cts = null;
            _task = null;
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        cts.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sample = Sample();
                Sampled?.Invoke(this, sample);
            }
            catch (Exception ex)
            {
                Log.Warn($"性能采样失败: {ex.Message}");
            }

            try
            {
                // 首帧后 400ms 再采一次：CPU 需要两个时间点差值才能算出百分比。
                // 之后按用户选择的间隔采样（1s 已接近任务管理器）。
                var delay = tick++ < 1
                    ? 400
                    : Math.Max(500, Volatile.Read(ref _intervalMs));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private SystemMetricsSample Sample()
    {
        var now = DateTime.UtcNow;
        var elapsed = Math.Max(0.2, (now - _lastSampleAt).TotalSeconds);
        _lastSampleAt = now;

        var sample = new SystemMetricsSample
        {
            Timestamp = DateTimeOffset.Now,
            CountersReady = _countersReady
        };

        // CPU：GetSystemTimes 的 kernel 包含 idle
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            sample.CpuAvailable = _hasCpuBaseline;
            if (_hasCpuBaseline)
            {
                var idleDelta = idle - _prevIdle;
                var totalDelta = (kernel - _prevKernel) + (user - _prevUser);
                sample.CpuPercent = totalDelta > 0
                    ? Math.Clamp((1.0 - idleDelta / (double)totalDelta) * 100, 0, 100)
                    : 0;
            }
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            _hasCpuBaseline = true;
        }

        var memory = NativeMethods.GetMemoryStatus();
        sample.TotalMemoryBytes = memory.TotalBytes;
        sample.AvailableMemoryBytes = memory.AvailableBytes;
        sample.MemoryPercent = memory.TotalBytes > 0
            ? Math.Clamp((memory.TotalBytes - memory.AvailableBytes) * 100.0 / memory.TotalBytes, 0, 100)
            : 0;

        SampleNetwork(elapsed, sample);
        SampleDisk(elapsed, sample);

        // GPU 和进程枚举都较贵：进程约 1 秒刷新，GPU 约 2 秒刷新，其余周期复用上一次结果
        var gpuDue = !_gpuHasValue || (now - _lastGpuAt).TotalSeconds >= 1.9;
        if (gpuDue)
        {
            SampleGpu(sample);
            _lastGpuPercent = sample.GpuPercent;
            _lastGpuAvailable = sample.GpuAvailable;
            _lastGpuAt = now;
        }
        else
        {
            sample.GpuPercent = _lastGpuPercent;
            sample.GpuAvailable = _lastGpuAvailable;
        }
        sample.GpuSampled = _gpuHasValue;

        if ((now - _lastProcessAt).TotalSeconds >= 0.9)
        {
            var processElapsed = Math.Max(0.5, (now - _lastProcessAt).TotalSeconds);
            _lastProcessAt = now;
            SampleProcesses(now, processElapsed, sample);
            _lastProcesses = sample.Processes;
        }
        else
        {
            sample.Processes = _lastProcesses;
        }

        return sample;
    }

    private void SampleNetwork(double elapsed, SystemMetricsSample sample)
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                try
                {
                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch { /* 某些虚拟网卡不支持 */ }
            }

            if (_hasNetBaseline)
            {
                sample.NetworkReceivedBytesPerSec = Math.Max(0, (rx - _prevNetRx) / elapsed);
                sample.NetworkSentBytesPerSec = Math.Max(0, (tx - _prevNetTx) / elapsed);
            }
            _prevNetRx = rx;
            _prevNetTx = tx;
            _hasNetBaseline = true;
        }
        catch
        {
            // 网络统计失败不影响其他指标
        }
    }

    private void SampleDisk(double elapsed, SystemMetricsSample sample)
    {
        if (!_countersReady) return;
        if (!_diskAvailable) return;
        try
        {
            if (_diskRead is null || _diskWrite is null) return;

            // 计数器本身是按秒的速率，不再除以 elapsed
            sample.DiskReadBytesPerSec = Math.Max(0, _diskRead.NextValue());
            sample.DiskWriteBytesPerSec = Math.Max(0, _diskWrite.NextValue());
        }
        catch
        {
            _diskAvailable = false;
            try { _diskRead?.Dispose(); } catch { }
            try { _diskWrite?.Dispose(); } catch { }
            _diskRead = null;
            _diskWrite = null;
        }
    }

    private void SampleGpu(SystemMetricsSample sample)
    {
        if (!_countersReady) return;
        if (!_gpuAvailable) return;
        try
        {
            var category = new PerformanceCounterCategory("GPU Engine");
            var data = category.ReadCategory();
            InstanceDataCollection? utilization = null;
            foreach (var keyObject in data.Keys)
            {
                if (keyObject is not string key) continue;
                if (string.Equals(key, "Utilization Percentage", StringComparison.OrdinalIgnoreCase))
                {
                    utilization = data[key] as InstanceDataCollection;
                    break;
                }
            }
            if (utilization is null)
            {
                sample.GpuAvailable = false;
                if (++_gpuFailures >= 3)
                {
                    _gpuAvailable = false;
                    _gpuHasValue = true;
                    Log.Warn("GPU 计数器缺少 Utilization Percentage");
                }
                return;
            }

            // GPU Engine 的 Utilization Percentage 是“双样本”计数器：
            // 必须保存上一次 CounterSample，用 Calculate(prev, cur) 才能算出非零百分比。
            var byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var calculated = 0;
            foreach (InstanceData instance in utilization.Values)
            {
                var name = instance.InstanceName;
                var engineIndex = name.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
                if (engineIndex < 0) continue;
                var engine = name[(engineIndex + "engtype_".Length)..];
                var separator = engine.IndexOf('_');
                if (separator > 0) engine = engine[..separator];

                var current = instance.Sample;
                if (_prevGpuSamples.TryGetValue(name, out var previous))
                {
                    var value = CounterSample.Calculate(previous, current);
                    if (!double.IsNaN(value) && !double.IsInfinity(value))
                    {
                        byEngine.TryGetValue(engine, out var total);
                        byEngine[engine] = total + Math.Max(0, value);
                        calculated++;
                    }
                }
                _prevGpuSamples[name] = current;
            }

            if (calculated == 0)
            {
                // 第一次只能建立基线，下一次才有百分比
                sample.GpuPercent = 0;
                sample.GpuAvailable = true;
                _gpuFailures = 0;
                return;
            }

            // 取最忙的引擎类型，和任务管理器“GPU 0”的统计口径一致
            sample.GpuPercent = Math.Clamp(byEngine.Count == 0 ? 0 : byEngine.Values.Max(), 0, 100);
            sample.GpuAvailable = true;
            _gpuHasValue = true;
            _gpuFailures = 0;

            if (_prevGpuSamples.Count > 4096)
            {
                _prevGpuSamples.Clear();
            }
        }
        catch (Exception ex)
        {
            sample.GpuAvailable = false;
            // 首次读取 PDH 偶发失败，继续重试；连续失败 3 次才判定不可用
            if (++_gpuFailures >= 3)
            {
                _gpuAvailable = false;
                _gpuHasValue = true;
                Log.Warn($"GPU 计数器不可用: {ex.Message}");
            }
        }
    }

    private void SampleProcesses(DateTime now, double elapsed, SystemMetricsSample sample)
    {
        var current = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var pid = process.Id;
                current.Add(pid);
                var cpuTime = process.TotalProcessorTime;
                var cpuPercent = 0.0;
                if (_prevProcessCpu.TryGetValue(pid, out var previous))
                {
                    var delta = (cpuTime - previous).TotalSeconds;
                    cpuPercent = Math.Clamp(delta / elapsed / Environment.ProcessorCount * 100, 0, 100);
                }
                _prevProcessCpu[pid] = cpuTime;

                var workingSet = 0L;
                try { workingSet = process.WorkingSet64; } catch { }
                var name = process.ProcessName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    sample.Processes.Add(new ProcessSample
                    {
                        Pid = pid,
                        Name = name,
                        CpuPercent = cpuPercent,
                        WorkingSetBytes = workingSet
                    });
                }
            }
            catch
            {
                // 进程已退出或拒绝访问
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var pid in _prevProcessCpu.Keys.Where(pid => !current.Contains(pid)).ToList())
        {
            _prevProcessCpu.Remove(pid);
        }
    }

    private void InitializeCounters()
    {
        if (_countersReady) return;
        if (Interlocked.Exchange(ref _countersInitializing, 1) == 1) return;
        try
        {
            try
            {
                _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                _diskRead.NextValue();
                _diskWrite.NextValue();
            }
            catch
            {
                _diskAvailable = false;
                try { _diskRead?.Dispose(); } catch { }
                try { _diskWrite?.Dispose(); } catch { }
                _diskRead = null;
                _diskWrite = null;
            }

            try
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                _gpuAvailable = category.CategoryName.Equals("GPU Engine", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                _gpuAvailable = false;
            }

            if (_disposed)
            {
                try { _diskRead?.Dispose(); } catch { }
                try { _diskWrite?.Dispose(); } catch { }
                _diskRead = null;
                _diskWrite = null;
                return;
            }

            _countersReady = true;
        }
        finally
        {
            Interlocked.Exchange(ref _countersInitializing, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _diskRead?.Dispose(); } catch { }
        try { _diskWrite?.Dispose(); } catch { }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
