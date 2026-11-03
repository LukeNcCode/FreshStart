using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using FreshStart.Services.System;

namespace FreshStart.Modules.SystemInfo;

public sealed class InfoItem
{
    public InfoItem(string label, string value)
    {
        Label = label;
        Value = value;
    }

    public string Label { get; }
    public string Value { get; }
}

public sealed class InfoSection
{
    public InfoSection(string title, string iconKey, IReadOnlyList<InfoItem> items)
    {
        Title = title;
        IconKey = iconKey;
        Items = items;
    }

    public string Title { get; }
    public string IconKey { get; }
    public string IconData => Visual.AppIcons.Get(IconKey);
    public IReadOnlyList<InfoItem> Items { get; }
}

/// <summary>
/// 系统信息页 ViewModel。进入页面才加载数据，先骨架后填充；
/// WMI 慢查询在 Service 内部走后台线程并有超时，UI 线程不做任何系统调用。
/// </summary>
public sealed partial class SystemInfoViewModel : ObservableObject
{
    private readonly SystemInfoService _service;
    private readonly IToastService _toast;
    private CancellationTokenSource? _cts;
    private SystemInfoModel? _model;

    public SystemInfoViewModel(SystemInfoService service, IToastService toast)
    {
        _service = service;
        _toast = toast;
    }

    public bool HasLoaded { get; private set; }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _statusText = "正在读取系统信息…";

    [ObservableProperty]
    private IReadOnlyList<InfoSection> _sections = Array.Empty<InfoSection>();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (HasLoaded) return;
        await CollectAsync(forceRefresh: false);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await CollectAsync(forceRefresh: true);

    private async Task CollectAsync(bool forceRefresh)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsLoading = Sections.Count == 0;
        HasError = false;
        StatusText = IsLoading ? "正在读取系统信息…" : "正在刷新…";

        try
        {
            var model = await _service.CollectAsync(forceRefresh, token).ConfigureAwait(true);
            _model = model;
            Sections = BuildSections(model);
            HasLoaded = true;

            var source = model.FromCache ? "缓存数据" : "实时采集";
            var partial = model.IsPartial ? " · 部分硬件信息暂不可用" : string.Empty;
            StatusText = $"{source} · 采集于 {model.CollectedAt:MM-dd HH:mm:ss}{partial}";
        }
        catch (OperationCanceledException)
        {
            // 页面已离开，忽略
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusText = "读取失败";
            Log.Error("系统信息加载失败", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void CopyAll()
    {
        if (_model is null)
        {
            _toast.Show("系统信息尚未加载完成", ToastKind.Warning);
            return;
        }

        try
        {
            var text = BuildClipboardText(_model);
            System.Windows.Clipboard.SetText(text);
            _toast.Show("系统信息已复制到剪贴板", ToastKind.Success);
        }
        catch (Exception ex)
        {
            Log.Error("复制系统信息失败", ex);
            _toast.Show("复制失败，剪贴板可能被其他程序占用", ToastKind.Error);
        }
    }

    public void Cancel()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
    }

    private static IReadOnlyList<InfoSection> BuildSections(SystemInfoModel model)
    {
        static string V(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

        var osRows = new List<InfoItem>
        {
            new("计算机名", V(model.ComputerName)),
            new("系统版本", V($"{model.OsName} {model.OsVersion}".Trim())),
            new("内部版本", V(model.OsBuild)),
            new("系统架构", V(model.Architecture)),
            new("激活状态", V(model.Activation)),
            new("安装日期", model.InstallDate is null ? "—" : model.InstallDate.Value.ToString("yyyy-MM-dd")),
            new("开机时长", FormatUptime(model.Uptime))
        };

        var cpuRows = new List<InfoItem>
        {
            new("处理器", V(model.CpuName)),
            new("物理核心", model.PhysicalCores > 0 ? $"{model.PhysicalCores} 核" : "—"),
            new("逻辑核心", model.LogicalCores > 0 ? $"{model.LogicalCores} 线程" : "—"),
            new("最高频率", model.CpuMaxMhz > 0 ? $"{model.CpuMaxMhz / 1000.0:0.00} GHz" : "—"),
            new("内存容量", model.TotalMemoryBytes > 0 ? DiskInfo.FormatSize(model.TotalMemoryBytes) : "—")
        };

        var vendorModel = string.Join(" ", new[] { model.ComputerVendor, model.ComputerModel }
            .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        var boardRows = new List<InfoItem>
        {
            new("主板", V(model.Motherboard)),
            new("电脑型号", V(vendorModel)),
            new("BIOS 厂商", V(model.BiosVendor)),
            new("BIOS 版本", V(model.BiosVersion)),
            new("BIOS 日期", V(model.BiosDate))
        };

        var deviceRows = new List<InfoItem>
        {
            new("显卡", model.Gpus.Count > 0 ? string.Join("\n", model.Gpus) : "—"),
            new("网络适配器", model.NetworkAdapters.Count > 0 ? string.Join("\n", model.NetworkAdapters) : "—")
        };

        var diskRows = model.Disks.Select((disk, index) =>
            new InfoItem($"磁盘 {index + 1}", V(disk.Display))).ToList();
        if (diskRows.Count == 0) diskRows.Add(new InfoItem("存储", "—"));

        return new List<InfoSection>
        {
            new("系统", "info", osRows),
            new("处理器与内存", "chip", cpuRows),
            new("主板与固件", "hardware", boardRows),
            new("显卡与网络", "activity", deviceRows),
            new("存储", "disk", diskRows)
        };
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime <= TimeSpan.Zero) return "—";
        if (uptime.TotalDays >= 1) return $"{(int)uptime.TotalDays} 天 {uptime.Hours} 小时 {uptime.Minutes} 分钟";
        if (uptime.TotalHours >= 1) return $"{(int)uptime.TotalHours} 小时 {uptime.Minutes} 分钟";
        return $"{Math.Max(1, uptime.Minutes)} 分钟";
    }

    private static string BuildClipboardText(SystemInfoModel model)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("FreshStart 系统信息");
        builder.AppendLine($"采集时间：{model.CollectedAt:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine();
        foreach (var section in BuildSections(model))
        {
            builder.AppendLine($"[{section.Title}]");
            foreach (var item in section.Items)
            {
                builder.AppendLine($"{item.Label}：{item.Value.Replace("\n", " / ")}");
            }
            builder.AppendLine();
        }
        return builder.ToString().TrimEnd();
    }
}
