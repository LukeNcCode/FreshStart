using System.Diagnostics;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Core.Settings;
using FreshStart.Services.System;

namespace FreshStart.Services.Appx;

public sealed class AppxPackageInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string PackageFullName { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public bool IsFramework { get; set; }
    public bool IsResourcePackage { get; set; }
    public bool NonRemovable { get; set; }
    public bool HasStartEntry { get; set; }
    public bool IsSystem { get; set; }

    private static readonly HashSet<string> ProtectedPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost",
        "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search"
    };

    public bool IsProtected => ProtectedPackages.Contains(Name);
    public bool CanRemove => !IsFramework && !IsResourcePackage && !IsProtected
                             && !string.IsNullOrWhiteSpace(PackageFullName);
    public string LocalizedName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string VersionText => string.IsNullOrWhiteSpace(Version) ? "—" : Version;
}

public sealed class RemovedAppxInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string PackageFullName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public bool IsSystem { get; set; }
    public string RemovedAt { get; set; } = DateTimeOffset.Now.ToString("O");
    public string RemovedAtText => DateTimeOffset.TryParse(RemovedAt, out var value)
        ? value.ToLocalTime().ToString("MM-dd HH:mm")
        : RemovedAt;
}

/// <summary>
/// Appx 枚举、卸载与重新注册。卸载通过 Remove-AppxPackage；
/// 恢复通过 Add-AppxPackage -Register 重新注册安装目录中的 AppxManifest.xml。
/// 被卸载包记录在 %AppData%\FreshStart\appx-removed.json，重启后可恢复。
/// </summary>
public sealed class AppxService
{
    private readonly PowerShellRunner _powershell = new();
    private readonly string _removedPath = Path.Combine(AppPaths.Root, "appx-removed.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly HashSet<string> KnownSystem = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller",
        "Microsoft.Windows.Photos", "Microsoft.WindowsCalculator", "Microsoft.WindowsCamera",
        "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch",
        "Microsoft.WindowsTerminal", "Microsoft.SecHealthUI", "Microsoft.Windows.Clock",
        "Microsoft.WindowsSoundRecorder", "Microsoft.ZuneMusic", "Microsoft.ZuneVideo",
        "Microsoft.WindowsAlarms", "Microsoft.WindowsMaps", "Microsoft.MicrosoftStickyNotes",
        "Microsoft.WindowsFeedbackHub", "Microsoft.GetHelp", "Microsoft.Getstarted",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost",
        "Microsoft.Windows.Search", "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl",
        // Windows 11 预装收件箱应用：可卸载，但卸载后可能影响系统功能
        "microsoft.windowscommunicationsapps", "Microsoft.BingWeather", "Microsoft.BingNews",
        "Microsoft.People", "Microsoft.MicrosoftSolitaireCollection", "Microsoft.GamingApp",
        "Microsoft.YourPhone", "Microsoft.OutlookForWindows", "Microsoft.Todos",
        "Microsoft.PowerAutomateDesktop", "Microsoft.MicrosoftPCManager", "MicrosoftWindows.Client.CBS",
        "MicrosoftCorporationII.QuickAssist", "Microsoft.Clipchamp", "Microsoft.Windows.DevHome"
    };

    public async Task<IReadOnlyList<AppxPackageInfo>> EnumerateAsync(CancellationToken ct = default)
    {
        const string script = """
            $start = [System.Collections.Generic.Dictionary[string,System.Collections.Generic.List[string]]]::new([System.StringComparer]::OrdinalIgnoreCase)
            Get-StartApps | ForEach-Object {
                if ($_.AppID -match '^(?<family>[^!]+)!') {
                    $family = $matches['family']
                    if (-not $start.ContainsKey($family)) {
                        $start[$family] = [System.Collections.Generic.List[string]]::new()
                    }
                    $name = [string]$_.Name
                    if ($name -and -not $start[$family].Contains($name)) { $start[$family].Add($name) }
                }
            }
            $items = @(Get-AppxPackage | ForEach-Object {
                $family = [string]$_.PackageFamilyName
                $display = $null
                $names = $null
                if ($start.TryGetValue($family, [ref]$names) -and $names.Count -gt 0) {
                    $display = ($names | Select-Object -First 3) -join ' / '
                }
                $hasStart = -not [string]::IsNullOrWhiteSpace($display)
                if (-not $display) {
                    try {
                        $manifest = Get-AppxPackageManifest -Package $_.PackageFullName -ErrorAction Stop
                        $display = [string]$manifest.Package.Properties.DisplayName
                    } catch { $display = [string]$_.Name }
                }
                if (-not $display -or $display.StartsWith('ms-resource:')) { $display = [string]$_.Name }
                [pscustomobject]@{
                    Name = [string]$_.Name
                    DisplayName = $display
                    PackageFullName = [string]$_.PackageFullName
                    FamilyName = $family
                    Publisher = [string]$_.PublisherDisplayName
                    Version = [string]$_.Version
                    InstallLocation = [string]$_.InstallLocation
                    IsFramework = [bool]$_.IsFramework
                    IsResourcePackage = [bool]$_.IsResourcePackage
                    NonRemovable = [bool]$_.NonRemovable
                    HasStartEntry = $hasStart
                }
            })
            ConvertTo-Json -InputObject $items -Depth 4 -Compress
            """;

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.StdErr)
            ? $"读取 Appx 失败（exit {result.ExitCode}）"
            : result.StdErr.Trim());

        var items = JsonSerializer.Deserialize<List<AppxPackageInfo>>(result.StdOut, JsonConfig.Options)
                    ?? new List<AppxPackageInfo>();
        foreach (var item in items)
        {
            item.IsSystem = item.NonRemovable
                            || KnownSystem.Contains(item.Name)
                            || item.Name.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase);
        }
        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .OrderByDescending(item => item.IsSystem)
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<OperationResult> RemoveAsync(string packageFullName, CancellationToken ct)
    {
        var script = $"Remove-AppxPackage -Package '{Escape(packageFullName)}' -ErrorAction Stop";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"卸载失败（exit {result.ExitCode}）：{LastLine(result.StdOut)}"
                : result.StdErr.Trim());
        }
        return OperationResult.Ok("已卸载");
    }

    public async Task<OperationResult> RegisterAsync(string installLocation, string packageName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(installLocation))
            return OperationResult.Fail("安装目录为空，无法重新注册");
        var manifest = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifest)) return OperationResult.Fail($"找不到清单文件：{manifest}");

        var script = $"""
            $manifest = '{Escape(manifest)}'
            Add-AppxPackage -Register $manifest -DisableDevelopmentMode -ErrorAction Stop
            """;
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            return OperationResult.Fail(string.IsNullOrWhiteSpace(result.StdErr)
                ? $"重新注册失败（exit {result.ExitCode}）"
                : result.StdErr.Trim());
        }

        if (!await IsInstalledAsync(packageName, ct).ConfigureAwait(false))
        {
            return OperationResult.Fail("重新注册后未检测到包");
        }
        return OperationResult.Ok("已重新注册");
    }

    public async Task<bool> IsInstalledAsync(string packageName, CancellationToken ct)
    {
        var script = $"if (Get-AppxPackage -Name '{Escape(packageName)}' -ErrorAction SilentlyContinue) {{ 'true' }} else {{ 'false' }}";
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return result.Success && string.Equals(result.StdOut.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>强制卸载系统组件：解除注册、删除预置包、取得目录权限并删除安装/用户数据。</summary>
    public async Task<OperationResult> ForceRemoveAsync(
        AppxPackageInfo package, IProgress<OperationStep>? progress, CancellationToken ct,
        bool allowFileFallback = true)
    {
        var errors = new List<string>();
        var installPath = package.InstallLocation;
        var localPackages = string.IsNullOrWhiteSpace(package.FamilyName)
            ? string.Empty
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", package.FamilyName);

        // 结束可能占用包文件的安全中心进程，否则删除时会出现 Access denied
        foreach (var processName in new[] { "SecHealthUI", "SecurityHealthHost", "SecurityHealthSystray" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) { Log.Warn($"结束 {processName} 失败: {ex.Message}"); }
            }
        }

        // 采用 windows-defender-remover 的策略：
        // 写 AppxAllUserStore\Deprovisioned / EndOfLife + DISM 解除不可卸载策略，
        // 再移除预置包与包注册；通常不需要 takeown/删除 WindowsApps。
        var policyScript = "$name = '" + Escape(package.Name) + "'\n"
            + "$full = '" + Escape(package.PackageFullName) + "'\n"
            + "$family = '" + Escape(package.FamilyName) + "'\n"
            + "$store = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Appx\\AppxAllUserStore'\n"
            + "$users = @('S-1-5-18')\n"
            + "if (Test-Path $store) { $users += @(Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like '*S-1-5-21*' } | ForEach-Object { $_.PSChildName }) }\n"
            + "if ($family) { New-Item \"$store\\Deprovisioned\\$family\" -Force | Out-Null }\n"
            + "foreach ($sid in $users) { New-Item \"$store\\EndOfLife\\$sid\\$full\" -Force | Out-Null }\n"
            + "dism /online /set-nonremovableapppolicy /packagefamily:$family /nonremovable:0 | Out-Null\n"
            + "$p = Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $name }\n"
            + "if ($p) { $p | Remove-AppxProvisionedPackage -Online -AllUsers -ErrorAction SilentlyContinue }\n"
            + "Remove-AppxPackage -Package $full -AllUsers -ErrorAction SilentlyContinue\n";

        await RunWithProgressAsync(
            async () => await _powershell.RunAsync(policyScript, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false),
            progress, "正在解除不可卸载策略并移除包（DISM，最长 120 秒）", 20, 55, ct).ConfigureAwait(false);

        progress?.Report(new OperationStep { Message = "正在校验包注册…", Percent = 60 });
        var verify = await _powershell.RunAsync(
            $"if (Get-AppxPackage -AllUsers -Name '{Escape(package.Name)}' -ErrorAction SilentlyContinue) {{ 'exists' }} else {{ 'gone' }}",
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var gone = verify.Success && verify.StdOut.Trim().Contains("gone", StringComparison.OrdinalIgnoreCase);

        // 策略移除成功就不再碰文件系统；只有仍存在时才回退到权限 + 删除目录
        if (gone)
        {
            if (IsSecurityCenter(package))
            {
                await RemoveSecurityCenterTrayAsync(progress, ct).ConfigureAwait(false);
            }
            return OperationResult.Ok("已解除不可卸载策略并移除包（可能需要重启）");
        }

        if (!allowFileFallback)
        {
            return OperationResult.Fail("包注册仍存在；已跳过 takeown / 目录删除（建议重启后复查）");
        }

        foreach (var path in new[] { installPath, localPackages })
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            if (!IsSafePackagePath(path))
            {
                errors.Add($"{path}: 不在 Appx 允许的安装目录内，已跳过删除");
                continue;
            }

            await RunWithProgressAsync(
                () => TakeOwnershipAsync(path, ct),
                progress, $"正在取得目录权限：{Path.GetFileName(path)}", 65, 72, ct).ConfigureAwait(false);
        }

        foreach (var path in new[] { installPath, localPackages })
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
            if (!IsSafePackagePath(path))
            {
                errors.Add($"{path}: 不在 Appx 允许的安装目录内，已跳过删除");
                continue;
            }

            try
            {
                await RunWithProgressAsync(
                    () => Task.Run(() => Directory.Delete(path, recursive: true), ct),
                    progress, $"正在删除目录：{Path.GetFileName(path)}", 72, 88, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors.Add($"{path}: {ex.Message}");
            }
        }

        progress?.Report(new OperationStep { Message = "正在再次校验…", Percent = 92 });
        verify = await _powershell.RunAsync(
            $"if (Get-AppxPackage -AllUsers -Name '{Escape(package.Name)}' -ErrorAction SilentlyContinue) {{ 'exists' }} else {{ 'gone' }}",
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        gone = verify.Success && verify.StdOut.Trim().Contains("gone", StringComparison.OrdinalIgnoreCase);

        if (errors.Count > 0)
        {
            return OperationResult.Fail(
                "部分文件删除失败：" + string.Join("；", errors) +
                "。建议重启后重试；若仍失败，请运行 sfc /scannow 与 DISM /Online /Cleanup-Image /RestoreHealth，" +
                "避免系统组件处于半删除状态（可能影响安全中心与部分安装器）");
        }
        return gone
            ? await FinishRemovedAsync(package, progress, ct).ConfigureAwait(false)
            : OperationResult.Fail(
                "包注册仍存在：Windows 可能把它视为系统组件重新登记。请重启后再次执行；" +
                "若仍存在，需要系统安装介质做修复安装，或在「原生应用」页重试强制卸载");
    }

    /// <summary>
    /// 强制卸载成功后收尾：安全中心还要清掉托盘自启动，否则包已删除但托盘图标仍会随登录出现。
    /// </summary>
    private static async Task<OperationResult> FinishRemovedAsync(
        AppxPackageInfo package, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (IsSecurityCenter(package))
        {
            await RemoveSecurityCenterTrayAsync(progress, ct).ConfigureAwait(false);
            return OperationResult.Ok("已强制卸载安全中心，并移除托盘图标自启动（可能需要重启）");
        }

        return OperationResult.Ok("已强制卸载（可能需要重启）");
    }

    private static bool IsSecurityCenter(AppxPackageInfo package)
        => package.Name.Equals("Microsoft.SecHealthUI", StringComparison.OrdinalIgnoreCase);

    private static async Task RemoveSecurityCenterTrayAsync(
        IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在清理安全中心托盘自启动…", Percent = 94 });

        const string script = """
            $run = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
            Remove-ItemProperty -Path $run -Name 'SecurityHealth' -ErrorAction SilentlyContinue
            Get-Process -Name 'SecurityHealthSystray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
            """;

        var runner = new PowerShellRunner();
        var result = await runner.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            Log.Warn($"清理安全中心托盘自启动失败: {result.StdErr.Trim()}");
        }
    }

    /// <summary>
    /// 长时间系统调用期间每 700ms 汇报一次“已用时”，避免 UI 看起来卡死。
    /// 百分比在 [start, end) 之间缓慢递增，完成后跳到 end。
    /// </summary>
    private static async Task RunWithProgressAsync(
        Func<Task> action,
        IProgress<OperationStep>? progress,
        string message,
        int start,
        int end,
        CancellationToken ct)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopwatch = Stopwatch.StartNew();
        var heartbeat = Task.Run(async () =>
        {
            var percent = start;
            while (!heartbeatCts.IsCancellationRequested)
            {
                progress?.Report(new OperationStep
                {
                    Message = $"{message}（已用 {stopwatch.Elapsed.TotalSeconds:0}s）",
                    Percent = Math.Min(end - 1, percent)
                });
                percent = Math.Min(end - 1, percent + 1);
                try
                {
                    await Task.Delay(700, heartbeatCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });

        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* ignore */ }
            progress?.Report(new OperationStep { Message = $"{message} · 完成", Percent = end });
        }
    }

    /// <summary>
    /// 只有 Appx 的合法位置才允许 takeown / 递归删除：
    /// 程序包在 WindowsApps / ModifiableWindowsApps / SystemApps，
    /// 用户包数据在 %LocalAppData%\Packages。InstallLocation 若异常（盘符根、用户目录），直接跳过。
    /// </summary>
    private static bool IsSafePackagePath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return false; }

        foreach (var root in EnumeratePackageRoots())
        {
            var normalized = root.TrimEnd(Path.DirectorySeparatorChar);
            if (normalized.Length == 0 || full.Length <= normalized.Length) continue;
            if (full.StartsWith(normalized + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumeratePackageRoots()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "WindowsApps");
            yield return Path.Combine(programFiles, "ModifiableWindowsApps");
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "WindowsApps");
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrWhiteSpace(windows))
        {
            yield return Path.Combine(windows, "SystemApps");
        }

        // 强制卸载还会清掉用户包数据目录
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "Packages");
        }
    }

    private static async Task TakeOwnershipAsync(string path, CancellationToken ct)
    {
        await RunProcessWithTimeoutAsync("takeown.exe", $"/f \"{path}\" /r /d y", TimeSpan.FromSeconds(600), ct).ConfigureAwait(false);
        await RunProcessWithTimeoutAsync("icacls.exe", $"\"{path}\" /grant *S-1-5-32-544:F /t /c /q", TimeSpan.FromSeconds(600), ct).ConfigureAwait(false);
    }

    /// <summary>takeown/icacls 在 WindowsApps 上可能长时间无输出；超时强制结束，交给后续删除流程判断。</summary>
    private static async Task RunProcessWithTimeoutAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                Log.Warn($"{fileName} 超时（{timeout.TotalSeconds:0}s），已终止: {arguments}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"{fileName} 执行失败: {ex.Message}");
        }
    }

    public IReadOnlyList<RemovedAppxInfo> LoadRemoved()
    {
        try
        {
            if (!File.Exists(_removedPath)) return Array.Empty<RemovedAppxInfo>();
            return JsonSerializer.Deserialize<List<RemovedAppxInfo>>(File.ReadAllText(_removedPath), JsonConfig.Options)
                   ?? new List<RemovedAppxInfo>();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已卸载 Appx 记录失败: {ex.Message}");
            return Array.Empty<RemovedAppxInfo>();
        }
    }

    public void AddRemoved(IEnumerable<AppxPackageInfo> packages)
    {
        // 加超时：记录文件在 UI 线程被调用时会阻塞，长期占用锁也不应该无限期卡住调用方
        if (!_lock.Wait(TimeSpan.FromSeconds(10)))
        {
            Log.Warn("记录已卸载 Appx 列表超时（另一次写入未结束），本次跳过");
            return;
        }
        try
        {
            var list = LoadRemoved().ToList();
            foreach (var package in packages)
            {
                list.RemoveAll(item => string.Equals(item.Name, package.Name, StringComparison.OrdinalIgnoreCase));
                list.Add(new RemovedAppxInfo
                {
                    Name = package.Name,
                    PackageFullName = package.PackageFullName,
                    Publisher = package.Publisher,
                    Version = package.Version,
                    InstallLocation = package.InstallLocation,
                    IsSystem = package.IsSystem,
                    RemovedAt = DateTimeOffset.Now.ToString("O")
                });
            }
            Save(list);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void RemoveRemoved(string id)
    {
        if (!_lock.Wait(TimeSpan.FromSeconds(10)))
        {
            Log.Warn("更新已卸载 Appx 列表超时（另一次写入未结束），本次跳过");
            return;
        }
        try
        {
            var list = LoadRemoved().Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            Save(list);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Save(List<RemovedAppxInfo> list)
    {
        Directory.CreateDirectory(AppPaths.Root);
        AtomicFile.WriteAllText(_removedPath, JsonSerializer.Serialize(list, JsonConfig.Options));
    }

    private static string Escape(string value) => value.Replace("'", "''");

    private static string LastLine(string value)
    {
        var lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? string.Empty : lines[^1].Trim();
    }
}

/// <summary>批量卸载：一次确认、一次备份，任一项失败则把已卸载的包重新注册。</summary>
public sealed class AppxRemoveBatchOperation : OperationBase
{
    private readonly AppxService _service;
    private readonly IReadOnlyList<AppxPackageInfo> _packages;
    private readonly List<AppxPackageInfo> _removed = new();
    private readonly RiskLevel _risk;

    public AppxRemoveBatchOperation(AppxService service, IReadOnlyList<AppxPackageInfo> packages, RiskLevel risk)
    {
        _service = service;
        _packages = packages;
        _risk = risk;
    }

    public override string Id => "appx.remove";
    public override string Name => _packages.Count == 1 ? $"卸载「{_packages[0].Name}」" : $"批量卸载 {_packages.Count} 个 Appx";
    public override string Description => "通过 Remove-AppxPackage 卸载当前用户 Appx；可通过重新注册安装目录恢复（部分系统应用可能失败）";
    public override RiskLevel RiskLevel => _risk;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => _packages.Select(package => new ChangeDescriptor
    {
        Target = package.PackageFullName,
        Kind = "Appx 包",
        Before = "已安装",
        After = "卸载",
        Impact = package.IsSystem ? "系统应用，可能导致系统功能异常；需要二次确认" : "当前用户应用，可通过重新注册恢复"
    }).ToList();

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "AppxBatch",
            Existed = true,
            DataJson = JsonSerializer.Serialize(_packages, JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        for (var i = 0; i < _packages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var package = _packages[i];
            progress?.Report(new OperationStep
            {
                Message = $"正在卸载 {package.Name}（{i + 1}/{_packages.Count}）…",
                Percent = (int)((i + 1) * 100.0 / _packages.Count)
            });
            var result = await _service.RemoveAsync(package.PackageFullName, ct).ConfigureAwait(false);
            if (!result.Success) return result;
            _removed.Add(package);
        }
        return OperationResult.Ok($"已卸载 {_removed.Count} 个 Appx");
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        var failed = new List<string>();
        for (var i = _removed.Count - 1; i >= 0; i--)
        {
            var package = _removed[i];
            progress?.Report(new OperationStep
            {
                Message = $"正在恢复 {package.Name}…",
                Percent = (int)((i + 1) * 100.0 / Math.Max(1, _removed.Count))
            });
            var result = await _service.RegisterAsync(package.InstallLocation, package.Name, ct).ConfigureAwait(false);
            if (!result.Success) failed.Add($"{package.Name}: {result.Message}");
        }
        return failed.Count == 0
            ? OperationResult.Ok("已回滚卸载")
            : OperationResult.Fail($"回滚未完全成功：{string.Join("；", failed)}");
    }
}

public sealed class AppxRestoreOperation : OperationBase
{
    private readonly AppxService _service;
    private readonly RemovedAppxInfo _item;

    public AppxRestoreOperation(AppxService service, RemovedAppxInfo item)
    {
        _service = service;
        _item = item;
    }

    public override string Id => "appx.restore";
    public override string Name => $"恢复「{_item.Name}」";
    public override string Description => "通过 Add-AppxPackage -Register 重新注册 AppxManifest.xml";
    public override RiskLevel RiskLevel => RiskLevel.Caution;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _item.PackageFullName,
            Kind = "Appx 包",
            Before = "已卸载",
            After = "重新注册",
            Impact = "需要安装目录与清单文件仍然存在；系统应用恢复可能失败"
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "AppxRestore",
            Existed = false,
            DataJson = JsonSerializer.Serialize(_item, JsonConfig.Options)
        };
        return Task.FromResult(CreateSnapshot(new[] { entry }));
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在重新注册 Appx…", Percent = 60 });
        return await _service.RegisterAsync(_item.InstallLocation, _item.Name, ct).ConfigureAwait(false);
    }

    protected override async Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        return await _service.RemoveAsync(_item.PackageFullName, ct).ConfigureAwait(false);
    }
}
