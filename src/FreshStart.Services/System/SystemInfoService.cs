using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Settings;
using FreshStart.Services.Interop;
using Microsoft.Win32;

namespace FreshStart.Services.System;

public sealed class DiskInfo
{
    public string Model { get; set; } = string.Empty;
    public ulong SizeBytes { get; set; }
    public string InterfaceType { get; set; } = string.Empty;
    public string Display => SizeBytes > 0 ? $"{Model}（{FormatSize(SizeBytes)}）" : Model;

    public static string FormatSize(ulong bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }
}

public sealed class SystemInfoModel
{
    public string ComputerName { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public string OsVersion { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string Activation { get; set; } = "未知";
    public DateTimeOffset? InstallDate { get; set; }
    public TimeSpan Uptime { get; set; }

    public string CpuName { get; set; } = string.Empty;
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public int CpuMaxMhz { get; set; }
    public ulong TotalMemoryBytes { get; set; }

    public string Motherboard { get; set; } = string.Empty;
    public string BiosVendor { get; set; } = string.Empty;
    public string BiosVersion { get; set; } = string.Empty;
    public string BiosDate { get; set; } = string.Empty;
    public string ComputerVendor { get; set; } = string.Empty;
    public string ComputerModel { get; set; } = string.Empty;

    public List<string> Gpus { get; set; } = new();
    public List<DiskInfo> Disks { get; set; } = new();
    public List<string> NetworkAdapters { get; set; } = new();

    public DateTimeOffset CollectedAt { get; set; }
    public bool FromCache { get; set; }
    public bool IsPartial { get; set; }
}

/// <summary>
/// 系统信息采集。快字段（OS/CPU 名/内存/网卡）用注册表与 P/Invoke，毫秒级完成；
/// 慢字段（主板/BIOS/显卡/磁盘/激活）走 WMI，只允许后台执行并缓存 24 小时。
/// </summary>
public sealed class SystemInfoService
{
    private readonly string _cachePath = Path.Combine(AppPaths.CacheRoot, "systeminfo.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<SystemInfoModel> CollectAsync(bool forceRefresh, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = TryLoadCache();
            var model = cached ?? new SystemInfoModel();
            FillQuick(model);

            var stale = forceRefresh || cached is null ||
                        DateTimeOffset.Now - cached.CollectedAt > TimeSpan.FromHours(24);

            model.FromCache = !stale;
            if (stale)
            {
                try
                {
                    await FillWmiAsync(model, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn($"WMI 系统信息采集失败，使用快速字段: {ex.Message}");
                    model.IsPartial = true;
                }
                TrySaveCache(model);
            }

            model.CollectedAt = DateTimeOffset.Now;
            return model;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void FillQuick(SystemInfoModel model)
    {
        model.ComputerName = Environment.MachineName;
        model.Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        model.LogicalCores = Environment.ProcessorCount;
        model.Architecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() switch
        {
            "x64" => "x64",
            "arm64" => "ARM64",
            "x86" => "x86",
            _ => RuntimeInformation.OSArchitecture.ToString()
        };

        var memory = NativeMethods.GetMemoryStatus();
        if (memory.TotalBytes > 0) model.TotalMemoryBytes = memory.TotalBytes;

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                var productName = key.GetValue("ProductName") as string ?? "Windows";
                var build = key.GetValue("CurrentBuildNumber") as string ?? key.GetValue("CurrentBuild") as string ?? "0";
                var ubr = key.GetValue("UBR") is int u ? u : 0;
                int.TryParse(build, out var buildNumber);

                if (buildNumber >= 22000)
                {
                    productName = productName.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
                }

                model.OsName = productName;
                model.OsVersion = key.GetValue("DisplayVersion") as string ?? string.Empty;
                model.OsBuild = ubr > 0 ? $"{build}.{ubr}" : build;

                if (key.GetValue("InstallDate") is int installSeconds && installSeconds > 0)
                {
                    model.InstallDate = DateTimeOffset.FromUnixTimeSeconds(installSeconds).ToLocalTime();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统版本失败: {ex.Message}");
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key is not null)
            {
                model.CpuName = (key.GetValue("ProcessorNameString") as string ?? string.Empty).Trim();
                if (key.GetValue("~MHz") is int mhz) model.CpuMaxMhz = mhz;
            }
        }
        catch { /* ignore */ }

        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                            !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                            !n.Description.Contains("Teredo", StringComparison.OrdinalIgnoreCase) &&
                            !n.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase))
                .Select(n =>
                {
                    try
                    {
                        var speed = n.Speed > 0 ? $" · {n.Speed / 1_000_000_000d:0.#} Gbps" : string.Empty;
                        return $"{n.Name}（{n.Description}）{speed}";
                    }
                    catch { return n.Name; }
                })
                .Distinct()
                .ToList();
            if (adapters.Count > 0) model.NetworkAdapters = adapters;
        }
        catch { /* ignore */ }

        if (model.Disks.Count == 0)
        {
            try
            {
                model.Disks = DriveInfo.GetDrives()
                    .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .Select(d => new DiskInfo
                    {
                        Model = $"{(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel)} ({d.Name.TrimEnd('\\')})",
                        SizeBytes = (ulong)d.TotalSize,
                        InterfaceType = "卷"
                    })
                    .ToList();
            }
            catch { /* ignore */ }
        }
    }

    private static async Task FillWmiAsync(SystemInfoModel model, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(3);

        var computerTask = WmiQuery.FirstAsync("SELECT Manufacturer, Model FROM Win32_ComputerSystem", timeout, ct);
        var boardTask = WmiQuery.FirstAsync("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard", timeout, ct);
        var biosTask = WmiQuery.FirstAsync("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS", timeout, ct);
        var cpuTask = WmiQuery.FirstAsync("SELECT Name, NumberOfCores, MaxClockSpeed FROM Win32_Processor", timeout, ct);
        var gpuTask = WmiQuery.AllAsync("SELECT Name FROM Win32_VideoController", timeout, ct);
        var diskTask = WmiQuery.AllAsync("SELECT Model, Size, InterfaceType FROM Win32_DiskDrive", timeout, ct);
        var activationTask = WmiQuery.AllAsync(
            "SELECT LicenseStatus FROM SoftwareLicensingProduct WHERE PartialProductKey IS NOT NULL AND ApplicationID LIKE '55c92734%'",
            timeout, ct);

        var all = Task.WhenAll(computerTask, boardTask, biosTask, cpuTask, gpuTask, diskTask, activationTask);
        try
        {
            await all.WaitAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn("WMI 采集整体超时，使用已完成的部分");
        }

        var computer = Completed(computerTask);
        var board = Completed(boardTask);
        var bios = Completed(biosTask);
        var cpu = Completed(cpuTask);
        var gpu = Completed(gpuTask);
        var disks = Completed(diskTask);
        var activation = Completed(activationTask);

        if (computer is not null)
        {
            model.ComputerVendor = computer.Str("Manufacturer") ?? string.Empty;
            model.ComputerModel = computer.Str("Model") ?? string.Empty;
        }

        if (board is not null)
        {
            model.Motherboard = string.Join(" ", new[] { board.Str("Manufacturer"), board.Str("Product") }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        }

        if (bios is not null)
        {
            model.BiosVendor = bios.Str("Manufacturer") ?? string.Empty;
            model.BiosVersion = bios.Str("SMBIOSBIOSVersion") ?? string.Empty;
            var date = bios.Str("ReleaseDate");
            if (!string.IsNullOrEmpty(date) && date.Length >= 8)
            {
                model.BiosDate = $"{date[..4]}-{date.Substring(4, 2)}-{date.Substring(6, 2)}";
            }
        }

        if (cpu is not null)
        {
            var name = cpu.Str("Name");
            if (!string.IsNullOrWhiteSpace(name)) model.CpuName = name.Trim();
            var cores = cpu.I32("NumberOfCores");
            if (cores > 0) model.PhysicalCores = cores;
            var mhz = cpu.I32("MaxClockSpeed");
            if (mhz > 0) model.CpuMaxMhz = mhz;
        }

        if (gpu is { Count: > 0 })
        {
            model.Gpus = gpu
                .Select(row => row.Str("Name"))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!.Trim())
                .Distinct()
                .ToList();
        }

        if (disks is { Count: > 0 })
        {
            model.Disks = disks.Select(row => new DiskInfo
            {
                Model = row.Str("Model")?.Trim() ?? "磁盘",
                SizeBytes = row.U64("Size"),
                InterfaceType = row.Str("InterfaceType") ?? string.Empty
            }).Where(d => d.SizeBytes > 0).ToList();
        }

        if (activation is { Count: > 0 })
        {
            var status = activation[0].I32("LicenseStatus");
            model.Activation = status switch
            {
                0 => "未激活",
                1 => "已激活",
                2 => "宽限期（OOB）",
                3 => "宽限期（OOT）",
                4 => "非正版宽限期",
                5 => "通知模式（未激活）",
                6 => "延长宽限期",
                _ => "未知"
            };
        }
    }

    private static T? Completed<T>(Task<T> task) where T : class?
        => task.Status == TaskStatus.RanToCompletion ? task.Result : null;

    private SystemInfoModel? TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            var json = File.ReadAllText(_cachePath);
            return JsonSerializer.Deserialize<SystemInfoModel>(json, JsonConfig.Options);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统信息缓存失败: {ex.Message}");
            return null;
        }
    }

    private void TrySaveCache(SystemInfoModel model)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheRoot);
            var copy = JsonSerializer.Deserialize<SystemInfoModel>(
                JsonSerializer.Serialize(model, JsonConfig.Options), JsonConfig.Options);
            if (copy is null) return;
            copy.CollectedAt = DateTimeOffset.Now;
            AtomicFile.WriteAllText(_cachePath, JsonSerializer.Serialize(copy, JsonConfig.Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"写入系统信息缓存失败: {ex.Message}");
        }
    }
}

public sealed class ElevationService
{
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = global::System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new global::System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(global::System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
