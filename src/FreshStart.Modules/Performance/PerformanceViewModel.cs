using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.Performance;

namespace FreshStart.Modules.Performance;

public sealed class IntervalOption
{
    public IntervalOption(int seconds) => Seconds = seconds;

    public int Seconds { get; }
    public string Title => $"{Seconds}s";
}

public sealed class SortOption
{
    public SortOption(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }
}

public sealed class ProcessRow : ObservableObject
{
    public ProcessRow(int pid, string name)
    {
        Pid = pid;
        Name = name;
    }

    public int Pid { get; }
    public string Name { get; }

    private double _cpuPercent;
    public double CpuPercent
    {
        get => _cpuPercent;
        set
        {
            if (SetProperty(ref _cpuPercent, value)) OnPropertyChanged(nameof(CpuText));
        }
    }

    private long _workingSetBytes;
    public long WorkingSetBytes
    {
        get => _workingSetBytes;
        set
        {
            if (SetProperty(ref _workingSetBytes, value)) OnPropertyChanged(nameof(MemoryText));
        }
    }

    public string CpuText => $"{CpuPercent:0.0}%";
    public string MemoryText => FormatSize(WorkingSetBytes);
    public string PidText => $"PID {Pid}";

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}

public sealed partial class PerformanceViewModel : ObservableObject, IDisposable
{
    private const int MaxHistory = 60;
    private readonly PerformanceSampler _sampler;
    private readonly IToastService _toast;
    private readonly IUserPromptService _prompts;
    private readonly List<double> _cpu = new();
    private readonly List<double> _memory = new();
    private readonly List<double> _disk = new();
    private readonly List<double> _network = new();
    private readonly List<double> _gpu = new();
    private readonly Dictionary<int, ProcessRow> _processRows = new();
    private int _samplePending;
    private bool _disposed;

    public PerformanceViewModel(IToastService toast, IUserPromptService prompts)
    {
        _toast = toast;
        _prompts = prompts;
        _sampler = new PerformanceSampler();
        _sampler.Sampled += OnSampled;
        Intervals = new List<IntervalOption> { new(1), new(2), new(5) };
        SortOptions = new List<SortOption>
        {
            new("cpu", "按 CPU"),
            new("memory", "按内存")
        };
        _selectedInterval = Intervals[0];
        _selectedSort = SortOptions[0];
        ProcessesView = CollectionViewSource.GetDefaultView(Processes);
        UpdateSortDescriptions();
    }

    public IReadOnlyList<IntervalOption> Intervals { get; }
    public IReadOnlyList<SortOption> SortOptions { get; }
    public ObservableCollection<ProcessRow> Processes { get; } = new();
    public ICollectionView ProcessesView { get; }

    [ObservableProperty]
    private IntervalOption? _selectedInterval;

    [ObservableProperty]
    private SortOption? _selectedSort;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "正在连接性能计数器…";

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private string _diskText = "—";

    [ObservableProperty]
    private string _networkText = "—";

    [ObservableProperty]
    private string _gpuText = "—";

    [ObservableProperty]
    private string _processCountText = "—";

    [ObservableProperty]
    private bool _gpuAvailable = true;

    [ObservableProperty]
    private IReadOnlyList<double> _cpuHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _memoryHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _diskHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _networkHistory = Array.Empty<double>();

    [ObservableProperty]
    private IReadOnlyList<double> _gpuHistory = Array.Empty<double>();

    partial void OnSelectedIntervalChanged(IntervalOption? value)
    {
        if (value is null) return;
        _sampler.Start(TimeSpan.FromSeconds(value.Seconds));
        if (IsRunning) StatusText = $"采样中 · {value.Seconds}s · 每秒刷新";
    }

    partial void OnSelectedSortChanged(SortOption? value)
    {
        UpdateSortDescriptions();
        ProcessesView.Refresh();
    }

    public void Start()
    {
        if (_disposed) return;
        _sampler.Start(TimeSpan.FromSeconds(SelectedInterval?.Seconds ?? 1));
        IsRunning = true;
        StatusText = $"采样中 · {SelectedInterval?.Seconds ?? 1}s · 每秒刷新";
    }

    public void Stop()
    {
        _sampler.Stop();
        IsRunning = false;
        StatusText = "已暂停采样";
    }

    private void OnSampled(object? sender, SystemMetricsSample sample)
    {
        if (Interlocked.Exchange(ref _samplePending, 1) == 1) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() =>
        {
            try { ApplySample(sample); }
            finally { Interlocked.Exchange(ref _samplePending, 0); }
        });
    }

    private void ApplySample(SystemMetricsSample sample)
    {
        CpuText = sample.CpuAvailable ? $"{sample.CpuPercent:0.0}%" : "…";
        MemoryText = $"{FormatSize((long)(sample.TotalMemoryBytes - sample.AvailableMemoryBytes))} / {FormatSize((long)sample.TotalMemoryBytes)}";
        if (sample.CountersReady)
        {
            DiskText = $"{FormatRate(sample.DiskReadBytesPerSec + sample.DiskWriteBytesPerSec)}/s";
            GpuAvailable = sample.GpuAvailable;
            GpuText = !sample.GpuSampled ? "…" : sample.GpuAvailable ? $"{sample.GpuPercent:0.0}%" : "不可用";
        }
        else
        {
            DiskText = "…";
            GpuAvailable = true;
            GpuText = "…";
        }
        NetworkText = $"{FormatRate(sample.NetworkReceivedBytesPerSec + sample.NetworkSentBytesPerSec)}/s";

        Append(_cpu, sample.CpuPercent);
        Append(_memory, sample.MemoryPercent);
        Append(_disk, sample.DiskReadBytesPerSec + sample.DiskWriteBytesPerSec);
        Append(_network, sample.NetworkReceivedBytesPerSec + sample.NetworkSentBytesPerSec);
        Append(_gpu, sample.GpuAvailable ? sample.GpuPercent : 0);

        // 曲线每次采样都刷新（1s），进程列表也同步刷新，观感接近任务管理器
        CpuHistory = _cpu.ToList();
        MemoryHistory = _memory.ToList();
        DiskHistory = _disk.ToList();
        NetworkHistory = _network.ToList();
        GpuHistory = _gpu.ToList();

        if (sample.Processes.Count > 0 || Processes.Count == 0)
        {
            ProcessCountText = sample.Processes.Count > 20
                ? $"共 {sample.Processes.Count} 个进程 · 显示占用最高的 20 个"
                : $"共 {sample.Processes.Count} 个进程";
            ApplyProcessSort(sample.Processes);
        }
    }

    private void ApplyProcessSort(IReadOnlyList<ProcessSample>? samples = null)
    {
        samples ??= Processes.Select(p => new ProcessSample
        {
            Pid = p.Pid,
            Name = p.Name,
            CpuPercent = p.CpuPercent,
            WorkingSetBytes = p.WorkingSetBytes
        }).ToList();

        var sortByMemory = string.Equals(SelectedSort?.Key, "memory", StringComparison.OrdinalIgnoreCase);
        var top = samples
            .OrderByDescending(p => sortByMemory ? p.WorkingSetBytes : p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .Take(20)
            .ToList();

        var seen = new HashSet<int>();
        foreach (var sample in top)
        {
            seen.Add(sample.Pid);
            if (_processRows.TryGetValue(sample.Pid, out var row))
            {
                row.CpuPercent = sample.CpuPercent;
                row.WorkingSetBytes = sample.WorkingSetBytes;
            }
            else
            {
                row = new ProcessRow(sample.Pid, sample.Name)
                {
                    CpuPercent = sample.CpuPercent,
                    WorkingSetBytes = sample.WorkingSetBytes
                };
                _processRows[sample.Pid] = row;
                Processes.Add(row);
            }
        }

        foreach (var pid in _processRows.Keys.Where(pid => !seen.Contains(pid)).ToList())
        {
            var row = _processRows[pid];
            _processRows.Remove(pid);
            Processes.Remove(row);
        }

        UpdateSortDescriptions();
        ProcessesView.Refresh();
    }

    private void UpdateSortDescriptions()
    {
        var sortByMemory = string.Equals(SelectedSort?.Key, "memory", StringComparison.OrdinalIgnoreCase);
        ProcessesView.SortDescriptions.Clear();
        ProcessesView.SortDescriptions.Add(new SortDescription(
            sortByMemory ? nameof(ProcessRow.WorkingSetBytes) : nameof(ProcessRow.CpuPercent),
            ListSortDirection.Descending));
        ProcessesView.SortDescriptions.Add(new SortDescription(
            nameof(ProcessRow.WorkingSetBytes), ListSortDirection.Descending));
    }

    [RelayCommand]
    private async Task KillProcessAsync(ProcessRow? row)
    {
        if (row is null) return;
        if (row.Pid is <= 4)
        {
            _toast.Show("系统进程不允许结束", ToastKind.Warning);
            return;
        }

        var confirmed = await _prompts.ConfirmAsync(new UserPrompt
        {
            Title = $"结束进程「{row.Name}」？",
            Message = $"PID {row.Pid} · CPU {row.CpuText} · 内存 {row.MemoryText}",
            Details = new[]
            {
                "强制结束进程可能导致未保存的数据丢失",
                "结束系统关键进程可能造成桌面/服务异常",
                "此操作不可撤销，FreshStart 不会为进程创建备份"
            },
            ConfirmText = "结束进程",
            CancelText = "取消",
            IsDangerous = true
        });
        if (!confirmed) return;

        try
        {
            using var process = Process.GetProcessById(row.Pid);
            process.Kill(entireProcessTree: true);
            _toast.Show($"已结束 {row.Name}（PID {row.Pid}）", ToastKind.Success);
            Log.Info($"结束进程: {row.Name} ({row.Pid})");
        }
        catch (Exception ex)
        {
            Log.Error($"结束进程失败: {row.Name} ({row.Pid})", ex);
            _toast.Show($"结束进程失败：{ex.Message}", ToastKind.Error);
        }
    }

    private static void Append(List<double> list, double value)
    {
        list.Add(value);
        if (list.Count > MaxHistory) list.RemoveAt(0);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024 / 1024:0.0} GB";
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / 1024 / 1024:0.0} MB";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0.0} KB";
        return $"{bytesPerSecond:0} B";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sampler.Sampled -= OnSampled;
        _sampler.Dispose();
    }
}
