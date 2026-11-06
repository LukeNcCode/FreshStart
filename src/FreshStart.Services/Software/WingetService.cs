using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;
using Microsoft.Win32;
using FreshStart.Core.Diagnostics;

namespace FreshStart.Services.Software;

public sealed class SoftwareDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "自定义";
    public bool IsCustom { get; set; }

    /// <summary>同一软件的其他 winget ID（如 Chrome Beta/Dev、Firefox ESR）。</summary>
    public List<string> Aliases { get; set; } = new();

    /// <summary>额外的“已安装名称”关键字，匹配注册表 DisplayName；默认使用 Name。</summary>
    public List<string> MatchNames { get; set; } = new();
}

/// <summary>
/// 内置推荐清单。winget ID 可能随软件发布变化，页面允许添加自定义来源。
/// </summary>
public static class SoftwareCatalog
{
    public static IReadOnlyList<SoftwareDefinition> All { get; } = new List<SoftwareDefinition>
    {
        new() { Id = "7zip.7zip", Name = "7-Zip", Description = "开源解压缩工具，支持 7z / zip / rar 解压，无广告", Category = "解压缩" },
        new() { Id = "Bandisoft.Bandizip", Name = "Bandizip", Description = "界面友好的压缩工具，支持压缩包预览与多线程解压", Category = "解压缩" },
        new() { Id = "Google.Chrome", Name = "Google Chrome", Description = "主流浏览器，扩展生态与账号同步完整", Category = "浏览器",
            Aliases = new() { "Google.Chrome.Beta", "Google.Chrome.Beta.EXE", "Google.Chrome.Dev", "Google.Chrome.Canary" } },
        new() { Id = "Mozilla.Firefox", Name = "Mozilla Firefox", Description = "独立内核浏览器，隐私保护与容器标签页", Category = "浏览器",
            Aliases = new() { "Mozilla.Firefox.ESR", "Mozilla.Firefox.DeveloperEdition" } },
        new() { Id = "voidtools.Everything", Name = "Everything", Description = "基于 NTFS 索引的本地文件搜索，几乎即时返回结果", Category = "系统工具" },
        new() { Id = "Daum.PotPlayer", Name = "PotPlayer", Description = "本地视频播放器，硬解与字幕支持完善", Category = "影音" },
        new() { Id = "OBSProject.OBSStudio", Name = "OBS Studio", Description = "开源录屏与直播推流工具，支持多场景切换", Category = "影音" },
        new() { Id = "NetEase.UURemote", Name = "UU远程", Description = "网易出品的低延迟远程控制工具，支持跨设备远程桌面与文件传输", Category = "远程控制" }
    };
}

public sealed class SoftwareCustomStore
{
    private readonly string _path = Path.Combine(AppPaths.Root, "software-custom.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public IReadOnlyList<SoftwareDefinition> Load()
    {
        try
        {
            if (!File.Exists(_path)) return Array.Empty<SoftwareDefinition>();
            var items = JsonSerializer.Deserialize<List<SoftwareDefinition>>(
                File.ReadAllText(_path), JsonConfig.Options) ?? new List<SoftwareDefinition>();
            foreach (var item in items) item.IsCustom = true;
            return items;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取自定义软件源失败: {ex.Message}");
            return Array.Empty<SoftwareDefinition>();
        }
    }

    public async Task AddAsync(SoftwareDefinition item, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = Load().ToList();
            list.RemoveAll(existing => string.Equals(existing.Id, item.Id, StringComparison.OrdinalIgnoreCase));
            list.Add(item);
            Save(list);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = Load().Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)).ToList();
            Save(list);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Save(List<SoftwareDefinition> list)
    {
        Directory.CreateDirectory(AppPaths.Root);
        FreshStart.Core.Settings.AtomicFile.WriteAllText(
            _path, JsonSerializer.Serialize(list, JsonConfig.Options));
    }
}

public sealed class WingetResult
{
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public bool Canceled { get; init; }
    public bool Success => !Canceled && ExitCode == 0;
}

/// <summary>
/// 连续安装器崩溃（0xC0000005）的熔断：多次相同退出码通常意味着系统级问题，
/// 继续逐个尝试只会浪费时间并产生一堆相同错误。
/// </summary>
public static class InstallerHealth
{
    private static int _crashStreak;
    private static DateTimeOffset _lastCrash;

    public static int CrashCount => _crashStreak;

    public static bool IsUnhealthy =>
        _crashStreak >= 3 && DateTimeOffset.Now - _lastCrash < TimeSpan.FromMinutes(10);

    public static void NoteCrash()
    {
        _crashStreak++;
        _lastCrash = DateTimeOffset.Now;
    }

    public static void Reset() => _crashStreak = 0;
}

/// <summary>已安装软件索引：winget ID 集合 + 注册表显示名集合。</summary>
public sealed class InstalledSoftwareIndex
{
    public HashSet<string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Names { get; } = new();

    public bool Matches(SoftwareDefinition definition)
    {
        if (Ids.Contains(definition.Id)) return true;
        foreach (var alias in definition.Aliases)
        {
            if (Ids.Contains(alias)) return true;
        }

        // 默认用软件名做关键字；额外关键字用于注册表名称与产品名不同的情况
        if (MatchesName(definition.Name)) return true;
        foreach (var keyword in definition.MatchNames)
        {
            if (MatchesName(keyword)) return true;
        }
        return false;
    }

    /// <summary>
    /// 名称匹配：必须整词出现，避免 Git 命中 GitHub、微信命中微信开发者工具。
    /// 允许后面跟版本号/空格/标点，例如 “7-Zip 26.02 (x64)”。
    /// </summary>
    public bool MatchesName(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return false;
        foreach (var name in Names)
        {
            var index = name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var beforeOk = index == 0 || !char.IsLetterOrDigit(name[index - 1]);
                var end = index + keyword.Length;
                var afterOk = end >= name.Length || !char.IsLetterOrDigit(name[end]);
                if (beforeOk && afterOk) return true;
                index = name.IndexOf(keyword, index + 1, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}

/// <summary>
/// winget 子进程封装。隐藏窗口、异步逐行读取、可取消；不采集用户数据。
/// </summary>
public sealed class WingetService
{
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);
    private bool? _available;
    private InstalledSoftwareIndex? _installedCache;
    private DateTimeOffset _installedCacheAt;
    private static readonly TimeSpan InstalledCacheTtl = TimeSpan.FromSeconds(45);

    /// <summary>winget 版本（去掉前缀 v），例如 1.2.10691。</summary>
    public Version? Version { get; private set; }

    /// <summary>能正常跑现代清单的最低版本。</summary>
    public static readonly Version MinimumSupportedVersion = new(1, 6);

    /// <summary>
    /// 能校验当前 winget 源索引的最低版本。
    /// 1.6 等旧客户端读新索引会报 0x8A15005E（源数据完整性校验失败），表现为所有安装都失败。
    /// </summary>
    public static readonly Version MinimumSourceCapableVersion = new(1, 11);

    private static readonly Lazy<Version?> BundledVersion = new(ReadBundledBundleVersion);

    /// <summary>
    /// 推荐版本：部署了 winget 离线附加包时以包内版本为准，否则退回 1.11。
    /// 该值只用于「推荐软件」页的按需检测，不再阻塞应用启动。
    /// </summary>
    public static Version RequiredVersion => BundledVersion.Value ?? MinimumSourceCapableVersion;

    /// <summary>本机版本是否满足安装建议（按补齐段比较，避免 1.29.290 与 1.29.290.0 误判）。</summary>
    public static bool MeetsRequiredVersion(Version? current)
        => current is not null && NormalizeVersion(current) >= NormalizeVersion(RequiredVersion);

    /// <summary>低于 <see cref="MinimumSupportedVersion"/> 的 winget 不支持大量现代清单/参数，安装会失败。</summary>
    public bool IsVersionSupported => Version is null || NormalizeVersion(Version) >= NormalizeVersion(MinimumSupportedVersion);

    /// <summary>当前版本能否校验现代源索引（0x8A15005E 的健康检查）。</summary>
    public bool IsSourceCapable => Version is null || NormalizeVersion(Version) >= NormalizeVersion(MinimumSourceCapableVersion);

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is not null) return _available.Value;
        var result = await RunRawAsync("--version", null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        _available = result.Success;
        if (_available.Value)
        {
            var raw = result.Output.Trim().TrimStart('v', 'V');
            if (global::System.Version.TryParse(raw, out var version)) Version = version;
            Log.Info($"winget 版本: {result.Output.Trim()}");
        }
        else
        {
            Log.Warn("未检测到可用的 winget");
        }
        return _available.Value;
    }

    public async Task<bool> IsInstalledAsync(string id, CancellationToken ct)
    {
        var result = await RunRawAsync(
            new List<string> { "list", "--id", id, "-e", "--accept-source-agreements" },
            null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        return result.Success && result.Output.Contains(id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 一次性读取已安装软件索引：winget 列表里的 ID + 注册表 Uninstall 项的 DisplayName。
    /// winget 的 ID 可能随渠道变化（如 Chrome Beta 的 ID 是 Google.Chrome.Beta.EXE），
    /// 注册表名称则能覆盖手动安装/不同渠道的软件。
    /// 返回 null 表示 winget 读取失败（例如源异常），调用方应显示“未检测”。
    /// </summary>
    public async Task<InstalledSoftwareIndex?> GetInstalledIndexAsync(CancellationToken ct = default)
    {
        if (_installedCache is not null && DateTimeOffset.Now - _installedCacheAt < InstalledCacheTtl)
        {
            return _installedCache;
        }

        var result = await RunRawAsync(
            "list --accept-source-agreements",
            null, TimeSpan.FromSeconds(60), ct, maxBufferChars: 0).ConfigureAwait(false);
        if (!result.Success) return null;

        var index = new InstalledSoftwareIndex();
        // 已安装列表每行形如：7-Zip 26.02 (x64)  7zip.7zip  26.02  26.05  winget
        // 名称可能含空格，但 ID 不含空格，按空白分词后精确比对。
        foreach (var line in result.Output.Split('\n'))
        {
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                index.Ids.Add(token.Trim());
            }
        }

        index.Names.AddRange(ReadInstalledDisplayNames());
        _installedCache = index;
        _installedCacheAt = DateTimeOffset.Now;
        return index;
    }

    public void InvalidateInstalledCache()
    {
        _installedCache = null;
        _installedCacheAt = default;
    }

    /// <summary>读取注册表卸载项名称；跳过系统组件、补丁和子组件。</summary>
    private static List<string> ReadInstalledDisplayNames()
    {
        var names = new List<string>();
        try
        {
            Collect(RegistryHive.LocalMachine, RegistryView.Registry64, names);
            Collect(RegistryHive.LocalMachine, RegistryView.Registry32, names);
            Collect(RegistryHive.CurrentUser, RegistryView.Default, names);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已安装软件注册表失败: {ex.Message}");
        }
        return names;
    }

    private static void Collect(RegistryHive hive, RegistryView view, List<string> names)
    {
        const string subKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var root = baseKey.OpenSubKey(subKey);
            if (root is null) return;

            foreach (var keyName in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key is null) continue;
                    if (key.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
                    if (key.GetValue("SystemComponent") is int system && system == 1) continue;
                    if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent)) continue;
                    names.Add(display.Trim());
                }
                catch
                {
                    // 单个卸载项不可读时跳过
                }
            }
        }
        catch
        {
            // 某些精简系统可能没有 Uninstall 键
        }
    }

    /// <summary>
    /// 从微软官方地址下载并安装最新 App Installer（winget）。MSIX 由系统校验微软签名。
    /// </summary>
    public async Task<OperationResult> InstallOrUpdateAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "FreshStart-AppInstaller.msixbundle");
        try
        {
            progress?.Report("正在从微软官方下载 App Installer…");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("FreshStart/0.13");
                using var response = await http.GetAsync(
                    "https://aka.ms/getwinget", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var target = File.Create(temp))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var lastPercent = -1;
                    int read;
                    while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        received += read;
                        var percent = total > 0 ? (int)(received * 100 / total) : -1;
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress?.Report(percent >= 0
                                ? $"正在下载 App Installer… {percent}%"
                                : $"正在下载 App Installer… {received / 1024 / 1024} MB");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warn($"HTTP 下载失败: {ex.Message} / {ex.InnerException?.Message}");
                progress?.Report("HTTP 下载失败，改用系统 curl 重试…");
                await DownloadWithCurlAsync(temp, ct).ConfigureAwait(false);
            }

            if (!File.Exists(temp) || new FileInfo(temp).Length < 1024 * 1024)
            {
                throw new IOException("下载的 App Installer 文件不完整");
            }

            progress?.Report("正在安装 App Installer（微软签名校验）…");
            var installResult = await InstallPackageWithDependenciesAsync(temp, progress, ct).ConfigureAwait(false);
            if (!installResult.Success) return installResult;

            _available = null;
            Version = null;
            await Task.Delay(1500, ct).ConfigureAwait(false);
            var available = await IsAvailableAsync(ct).ConfigureAwait(false);
            return available && IsVersionSupported
                ? OperationResult.Ok($"winget 已更新（v{Version}）")
                : OperationResult.Ok("安装完成，但 winget 版本尚未刷新，请重启 FreshStart 后再试");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* ignore */ }
        }
    }

    /// <summary>从本地文件安装 App Installer（适合虚拟机网络受限、先在宿主机下载后拷入）。</summary>
    public async Task<OperationResult> InstallFromFileAsync(
        string path, IProgress<string>? progress, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path)) return OperationResult.Fail($"文件不存在：{path}");
            if (new FileInfo(path).Length < 1024 * 1024) return OperationResult.Fail("文件不完整（小于 1 MB）");

            progress?.Report("正在安装 App Installer（微软签名校验）…");
            var installResult = await InstallPackageWithDependenciesAsync(path, progress, ct).ConfigureAwait(false);
            if (!installResult.Success) return installResult;

            _available = null;
            Version = null;
            await Task.Delay(1500, ct).ConfigureAwait(false);
            var available = await IsAvailableAsync(ct).ConfigureAwait(false);
            return available && IsVersionSupported
                ? OperationResult.Ok($"winget 已安装/更新（v{Version}）")
                : OperationResult.Ok("安装完成，但 winget 版本尚未刷新，请重启 FreshStart 后再试");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    /// <summary>安装 App Installer；缺少 Windows App Runtime 时自动安装依赖后重试。</summary>
    private async Task<OperationResult> InstallPackageWithDependenciesAsync(
        string path, IProgress<string>? progress, CancellationToken ct)
    {
        var powershell = new PowerShellRunner();
        var command = $"Add-AppxPackage -Path '{path.Replace("'", "''")}' -ForceApplicationShutdown -ErrorAction Stop";
        var result = await powershell.RunAsync(command, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        var error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        if (result.Success) return OperationResult.Ok("安装成功");
        if (!error.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult.Fail($"安装失败：{error.Trim()}");
        }

        progress?.Report("缺少 Windows App Runtime 1.8，正在安装依赖…");
        var dependency = await EnsureWindowsAppRuntimeAsync(path, progress, ct).ConfigureAwait(false);
        if (!dependency.Success) return dependency;

        progress?.Report("依赖已安装，正在重试 App Installer…");
        result = await powershell.RunAsync(command, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        error = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        return result.Success
            ? OperationResult.Ok("安装成功")
            : OperationResult.Fail($"安装失败：{error.Trim()}");
    }

    /// <summary>优先使用与 msixbundle 同目录的 WindowsAppRuntimeInstall*.exe，找不到再下载官方依赖。</summary>
    private async Task<OperationResult> EnsureWindowsAppRuntimeAsync(
        string bundlePath, IProgress<string>? progress, CancellationToken ct, bool force = false)
    {
        var directory = Path.GetDirectoryName(bundlePath) ?? string.Empty;
        string? executable = null;
        if (Directory.Exists(directory))
        {
            executable = Directory.EnumerateFiles(directory, "*.exe")
                .FirstOrDefault(file =>
                    Path.GetFileName(file).Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(file).Contains("Install", StringComparison.OrdinalIgnoreCase));
        }

        if (executable is null)
        {
            executable = Path.Combine(Path.GetTempPath(), "WindowsAppRuntimeInstall-x64.exe");
            progress?.Report("正在下载 Windows App Runtime 1.8…");
            try
            {
                await DownloadWithCurlAsync(
                    "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe",
                    executable, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return OperationResult.Fail(
                    "缺少 Windows App Runtime 1.8，自动下载失败。请把 WindowsAppRuntimeInstall-x64.exe 和 msixbundle 放在同一目录后重试。"
                    + " " + ex.Message);
            }
        }

        progress?.Report(force
            ? "正在强制安装 Windows App Runtime 1.8 及其依赖（可能需要 1–2 分钟）…"
            : "正在安装 Windows App Runtime 1.8（可能需要 1–2 分钟）…");
        var arguments = force ? "--quiet --force" : "--quiet";
        var exitCode = await RunProcessAsync(executable, arguments, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        return exitCode is 0 or 3010
            ? OperationResult.Ok("依赖已安装")
            : OperationResult.Fail($"Windows App Runtime 安装失败（exit {exitCode}）");
    }

    private static async Task<int> RunProcessAsync(
        string fileName, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new IOException($"无法启动 {fileName}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
    }

    private static Task DownloadWithCurlAsync(string destination, CancellationToken ct)
        => DownloadWithCurlAsync("https://aka.ms/getwinget", destination, ct);

    private static async Task DownloadWithCurlAsync(string url, string destination, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("curl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-L", "--fail", "--silent", "--show-error",
                     "--output", destination, url
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new IOException("无法启动 curl.exe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            throw;
        }
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            throw new IOException($"curl 下载失败（exit {process.ExitCode}）：{error.Trim()}");
        }
    }

    public Task<WingetResult> UpdateSourcesAsync(Action<string>? onOutput, CancellationToken ct)
    {
        // 注意：新版 winget 的 source update 不接受 --accept-source-agreements（会报"无法识别参数"）
        var args = "source update --disable-interactivity";
        return RunRawAsync(args, onOutput, TimeSpan.FromMinutes(2), ct);
    }

    /// <summary>
    /// 历史版本的国内镜像源地址。仅用于把已切到镜像的机器迁回官方源。
    /// </summary>
    private const string LegacyDomesticSourceUrl = "https://mirrors.ustc.edu.cn/winget-source";

    /// <summary>
    /// 确保 winget 使用**微软官方源**：
    /// 若检测到历史遗留的国内镜像源（旧版本会自动切换），执行一次 source reset 迁回官方源并刷新索引。
    /// 已经是官方源时直接返回，不做任何修改。
    /// </summary>
    public async Task<OperationResult> EnsureOfficialSourceAsync(CancellationToken ct = default)
    {
        var current = await RunRawAsync("source list", null, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        var usingMirror = current.Success
                          && current.Output.Contains(LegacyDomesticSourceUrl, StringComparison.OrdinalIgnoreCase);
        if (!usingMirror)
        {
            await EnsureMsStoreSourceAsync(ct).ConfigureAwait(false);
            return OperationResult.Ok("正在使用微软官方源");
        }

        Log.Info("检测到历史遗留的国内镜像源，正在迁回微软官方源");
        var reset = await RestoreOfficialSourceAsync(ct).ConfigureAwait(false);
        if (!reset.Success)
        {
            // 非管理员时 source reset 会被拒绝；镜像源本身仍可用，这里不当作致命错误
            Log.Warn($"迁回官方源失败: {reset.Message}");
            return OperationResult.Ok("仍在使用历史遗留的国内镜像源（无法自动迁回，需要管理员权限）");
        }

        await UpdateWingetSourceIndexAsync(ct).ConfigureAwait(false);
        await EnsureMsStoreSourceAsync(ct).ConfigureAwait(false);
        return OperationResult.Ok("已从国内镜像迁回微软官方源");
    }

    /// <summary>
    /// 部分 winget 清单会引用 msstore 安装源（如 QQ NT）。如果系统没有 msstore 源，
    /// 会出现 0x8A150012「source name does not exist」。这里补回官方 msstore 源。
    /// </summary>
    private async Task EnsureMsStoreSourceAsync(CancellationToken ct)
    {
        const string sourceUrl = "https://storeedgefd.dsx.mp.microsoft.com/v9.0";

        var current = await RunRawAsync("source list", null, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        if (current.Success && current.Output.Contains("msstore", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var add = await RunRawAsync(
            $"source add msstore \"{sourceUrl}\" --type Microsoft.Rest " +
            "--disable-interactivity --accept-source-agreements",
            null, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);

        if (!add.Success)
        {
            add = await RunRawAsync(
                $"source add --name msstore --arg \"{sourceUrl}\" --type Microsoft.Rest " +
                "--disable-interactivity --accept-source-agreements",
                null, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }

        if (add.Success)
        {
            Log.Info("已补充 msstore 源（用于引用 Store 安装源的软件包）");
        }
        else
        {
            Log.Warn("补充 msstore 源失败: " + ShortLine(add.Output));
        }
    }

    /// <summary>恢复微软官方 winget 源。</summary>
    public async Task<OperationResult> RestoreOfficialSourceAsync(CancellationToken ct = default)
    {
        // source reset 只接受 [-n] <name> 与通用选项，不能带 --accept-source-agreements
        var result = await RunRawAsync(
            "source reset --name winget --disable-interactivity",
            null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            result = await RunRawAsync(
                "source reset winget --disable-interactivity",
                null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        }

        return result.Success
            ? OperationResult.Ok("已恢复官方 winget 源")
            : OperationResult.Fail("恢复官方 winget 源失败：" + ShortLine(result.Output));
    }

    private static readonly Regex LocalPackagePattern = new(
        @"^Microsoft\.(DesktopAppInstaller|VCLibs|UI\.Xaml|WindowsAppRuntime)[^\\/]*\.(msixbundle|appxbundle|msix|appx)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>随软件一起发布的 winget 安装包目录（assets\winget）。</summary>
    public static string BundledWingetFolder =>
        Path.Combine(AppContext.BaseDirectory, "assets", "winget");

    /// <summary>查找内置的 App Installer 包；依赖在前、主包在后。</summary>
    public static IReadOnlyList<string> FindLocalWingetPackages()
    {
        try
        {
            var folder = BundledWingetFolder;
            if (!Directory.Exists(folder)) return Array.Empty<string>();

            var files = Directory.EnumerateFiles(folder)
                .Where(file => LocalPackagePattern.IsMatch(Path.GetFileName(file)))
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();

            if (files.Count > 0)
            {
                Log.Info($"发现内置 winget 安装包: {folder}（{files.Count} 个文件）");
            }

            return files
                .OrderBy(file => Path.GetFileName(file).StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"扫描内置 winget 安装包失败: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>补齐缺失段（winget 输出三段、包版本四段），避免 1.29.290 被判定为小于 1.29.290.0。</summary>
    private static Version NormalizeVersion(Version version)
        => new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    /// <summary>字典里该包的版本是否满足最低要求。</summary>
    private static bool IsSatisfied(IReadOnlyDictionary<string, Version?> versions, string name, Version minVersion)
    {
        if (!versions.TryGetValue(name, out var version) || version is null) return false;
        return NormalizeVersion(version) >= NormalizeVersion(minVersion);
    }

    private static string DescribeInstalled(IReadOnlyDictionary<string, Version?> versions, string name)
    {
        if (!versions.TryGetValue(name, out var version) || version is null) return "未安装";
        return $"v{version}";
    }

    /// <summary>读取 assets\winget 里依赖包（非 App Installer 主包）的 Name → Version。</summary>
    private static Dictionary<string, Version?> ReadLocalDependencyVersions()
    {
        var map = new Dictionary<string, Version?>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in FindLocalWingetPackages())
        {
            if (Path.GetFileName(package).StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var identity = ReadPackageIdentity(package);
            if (identity is null) continue;
            var (name, version) = identity.Value;
            if (!map.TryGetValue(name, out var existing) || existing is null || version > existing)
            {
                map[name] = version;
            }
        }

        return map;
    }

    /// <summary>从 appx/msix 的 AppxManifest.xml 读取 Identity 的 Name 与 Version。</summary>
    private static (string Name, Version Version)? ReadPackageIdentity(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("AppxManifest.xml");
            if (entry is null) return null;

            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();
            var nameMatch = Regex.Match(xml, "Identity\\s+Name=\"([^\"]+)\"");
            var versionMatch = Regex.Match(xml, "Identity[^>]*Version=\"([0-9.]+)\"");
            if (!nameMatch.Success || !versionMatch.Success) return null;
            if (!Version.TryParse(versionMatch.Groups[1].Value, out var version)) return null;
            return (nameMatch.Groups[1].Value, version);
        }
        catch (Exception ex)
        {
            Log.Warn($"读取内置依赖包身份失败: {Path.GetFileName(path)} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读取内置 App Installer 包自身的版本（从 bundle 清单解析，纯托管、不依赖签名校验）。
    /// 用于避免"用较旧的内置包把已更新的客户端降级"。
    /// </summary>
    public static Version? ReadBundledBundleVersion()
    {
        try
        {
            var bundle = FindLocalWingetPackages().FirstOrDefault(file => Path.GetFileName(file)
                .StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase));
            if (bundle is null) return null;

            using var zip = ZipFile.OpenRead(bundle);
            var entry = zip.GetEntry("AppxMetadata/AppxBundleManifest.xml");
            if (entry is null) return null;

            using var reader = new StreamReader(entry.Open());
            var xml = reader.ReadToEnd();

            Version? newest = null;
            foreach (Match match in Regex.Matches(xml, @"<Package\b[^>]*\bVersion=""([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)"""))
            {
                if (!Version.TryParse(match.Groups[1].Value, out var parsed)) continue;
                if (newest is null || parsed > newest) newest = parsed;
            }

            return newest;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取内置 App Installer 包版本失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 用随软件发布的 App Installer 包（含 VCLibs / UI.Xaml / WindowsAppRuntime 依赖）升级 winget。
    /// 找不到内置包时返回失败，不会联网下载。
    /// </summary>
    public async Task<OperationResult> TryInstallBundledAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        var packages = FindLocalWingetPackages();
        var main = packages.FirstOrDefault(package => Path.GetFileName(package)
            .StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase));

        if (main is null)
        {
            return OperationResult.Fail(
                $"未找到内置 App Installer 安装包：{BundledWingetFolder}\\Microsoft.DesktopAppInstaller*.msixbundle");
        }

        // 不允许用较旧的内置包把已更新的客户端降级（Add-AppxPackage 实际会执行降级）
        if (Version is null) await IsAvailableAsync(ct).ConfigureAwait(false);
        var bundledVersion = ReadBundledBundleVersion();
        if (Version is not null && bundledVersion is not null
            && NormalizeVersion(Version) >= NormalizeVersion(bundledVersion))
        {
            progress?.Report($"当前 winget v{Version} 已不低于内置包 v{bundledVersion}，无需安装");
            return OperationResult.Ok(
                $"当前 winget v{Version} 已是内置包（v{bundledVersion}）同版本或更新，已跳过安装");
        }

        // 安装期间 winget 会占用包文件
        foreach (var process in Process.GetProcessesByName("winget"))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* ignore */ }
        }

        var failures = new List<string>();
        var mainFailures = new List<string>();
        var runner = new PowerShellRunner();
        var dependencyPackages = packages
            .Where(item => !Path.GetFileName(item).StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 依赖预检：把"系统已装版本不足"与"内置依赖包能否补齐"分开判断。
        // 内置的 VCLibs appx 会在安装时作为 -DependencyPath 一起部署，所以只要 assets 里有够新的包，
        // 就绝不能在这里提前失败（否则永远走不到安装那一步）。
        var requirements = ReadBundledRequirements();
        if (requirements.Count > 0)
        {
            var requirementNames = requirements.Select(item => item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var installed = await GetInstalledDependencyVersionsAsync(requirementNames, ct).ConfigureAwait(false);
            var local = ReadLocalDependencyVersions();

            var missing = requirements
                .Where(item => !IsSatisfied(installed, item.Name, item.MinVersion))
                .ToList();

            if (missing.Count > 0)
            {
                progress?.Report("系统依赖不足：" + string.Join("；", missing.Select(item =>
                    $"{item.Name} 需要 ≥ {item.MinVersion}（当前 {DescribeInstalled(installed, item.Name)}）")));

                var coverable = missing.Where(item => IsSatisfied(local, item.Name, item.MinVersion)).ToList();
                var notCoverable = missing.Except(coverable).ToList();

                if (notCoverable.Count > 0)
                {
                    progress?.Report("正在用内置 Windows App Runtime 安装器补齐依赖…");
                    var runtime = await EnsureWindowsAppRuntimeAsync(main, progress, ct, force: true).ConfigureAwait(false);
                    if (!runtime.Success)
                    {
                        Log.Warn($"补齐 Windows App Runtime 依赖失败: {runtime.Message}");
                    }

                    var recheck = await GetInstalledDependencyVersionsAsync(
                        notCoverable.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), ct)
                        .ConfigureAwait(false);

                    var stillMissing = notCoverable
                        .Where(item => !IsSatisfied(recheck, item.Name, item.MinVersion))
                        .Where(item => !IsSatisfied(local, item.Name, item.MinVersion))
                        .ToList();

                    if (stillMissing.Count > 0)
                    {
                        return OperationResult.Fail(
                            "内置 App Installer 的依赖无法满足：" +
                            string.Join("；", stillMissing.Select(item => $"{item.Name} 需要 ≥ {item.MinVersion}")) +
                            "。请把对应版本的 appx 放进 assets\\winget 后重试" +
                            "（可取自 winget-cli 官方 Release 的 DesktopAppInstaller_Dependencies.zip）");
                    }
                }

                if (coverable.Count > 0)
                {
                    progress?.Report("将由内置依赖包补齐：" + string.Join("、",
                        coverable.Select(item => $"{item.Name} {local[item.Name]}")));
                }
            }
        }

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(package);
            var isMain = name.StartsWith("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase);
            progress?.Report($"正在安装 {name}…");

            var result = await InstallAppxAsync(runner, package, isMain ? dependencyPackages : null, ct)
                .ConfigureAwait(false);
            if (result.Success) continue;

            var message = ExtractDeploymentError(result);
            Log.Warn($"安装 {name} 失败: {message}");

            // 已安装更高版本：依赖已经满足，继续
            if (message.Contains("0x80073D06", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"{name} 已安装更高版本，跳过");
                continue;
            }

            // 依赖缺失（0x80073CF3 / WindowsAppRuntime）时，先用内置运行时安装器补齐再重试
            if (message.Contains("0x80073CF3", StringComparison.OrdinalIgnoreCase)
                || message.Contains("WindowsAppRuntime", StringComparison.OrdinalIgnoreCase)
                || message.Contains("dependency", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("缺少 Windows App Runtime 依赖，正在安装…");
                var dependency = await EnsureWindowsAppRuntimeAsync(package, progress, ct).ConfigureAwait(false);
                if (dependency.Success)
                {
                    result = await InstallAppxAsync(runner, package, isMain ? dependencyPackages : null, ct)
                        .ConfigureAwait(false);
                    if (result.Success) continue;
                    message = ExtractDeploymentError(result);
                }
                else
                {
                    message += "；依赖安装失败：" + dependency.Message;
                }
            }

            // 整包失败时，尝试解包后只安装 x64 的 .msix（部分系统上 bundle 部署会失败）
            if (name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".appxbundle", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report("整包安装失败，尝试解包安装 x64 包…");
                var extracted = TryExtractBundle(package);
                if (extracted is not null)
                {
                    var innerResult = await InstallAppxAsync(runner, extracted, dependencyPackages, ct)
                        .ConfigureAwait(false);
                    TryDeleteDirectory(Path.GetDirectoryName(extracted));
                    if (innerResult.Success) continue;
                    message += "；x64 包安装也失败：" + ExtractDeploymentError(innerResult);
                }
            }

            var failure = $"{name}: {message}";
            failures.Add(failure);
            if (isMain) mainFailures.Add(failure);
        }

        _available = null;
        InvalidateInstalledCache();
        await Task.Delay(500, ct).ConfigureAwait(false);

        var verify = await RunRawAsync("--version", null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var upgraded = verify.Success
                       && global::System.Version.TryParse(
                           verify.Output.Trim().TrimStart('v', 'V'), out var version)
                       && MeetsRequiredVersion(version);

        if (upgraded)
        {
            var note = failures.Count > 0 ? $"（{failures.Count} 个依赖步骤跳过）" : string.Empty;
            return OperationResult.Ok($"winget 已升级：{verify.Output.Trim()}{note}");
        }

        if (mainFailures.Count > 0)
        {
            var message = "安装内置 winget 包失败：" + string.Join("；", mainFailures);
            if (mainFailures.Any(item => item.Contains("0x80073CF3", StringComparison.OrdinalIgnoreCase)))
            {
                message += "。" + await GetDependencyReportAsync(ct).ConfigureAwait(false);
            }

            return OperationResult.Fail(message);
        }

        return verify.Success
            ? OperationResult.Fail(
                $"已安装内置包，但版本仍低于门槛 {RequiredVersion}（当前 {verify.Output.Trim()}）：" +
                "该版本无法校验当前 winget 源索引（0x8A15005E）。" +
                "请把 assets\\winget 换成新版 App Installer 离线包，或在 Microsoft Store 更新 App Installer")
            : OperationResult.Fail("内置包已安装，但 winget 仍不可用");
    }

    /// <summary>清除缓存并重新检测 winget；用于前台更新窗口轮询。</summary>
    public async Task<Version?> RefreshVersionAsync(CancellationToken ct = default)
    {
        _available = null;
        Version = null;
        await IsAvailableAsync(ct).ConfigureAwait(false);
        return Version;
    }

    /// <summary>
    /// 检测 winget 源证书是否已过期/不匹配（0x8A15005E）。
    /// 1.6 等旧版本的源证书在 2024 年后会失效，表现为所有安装都失败。
    /// 网络/源暂时不可用不视为证书错误，避免误锁主页。
    /// </summary>
    public async Task<bool> HasSourceCertificateErrorAsync(CancellationToken ct = default)
    {
        var result = await RunRawAsync(
            "search --id 7zip.7zip -e --source winget --accept-source-agreements",
            null, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);

        var text = result.Output + "\n" + result.ExitCode;
        return result.ExitCode == -1978335138
               || text.Contains("0x8A15005E", StringComparison.OrdinalIgnoreCase)
               || text.Contains("certificate did not match", StringComparison.OrdinalIgnoreCase)
               || text.Contains("证书", StringComparison.OrdinalIgnoreCase)
                  && text.Contains("不匹配", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 对比内置 App Installer 的依赖要求与系统已安装版本。
    /// 0x80073CF3 时用于给出明确的依赖缺失原因，而不是只显示 HRESULT。
    /// </summary>
    public async Task<string> GetDependencyReportAsync(CancellationToken ct = default)
    {
        var requirements = ReadBundledRequirements();
        if (requirements.Count == 0) return "无法读取内置 App Installer 的依赖要求。";

        var installed = await GetInstalledDependencyVersionsAsync(
            requirements.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ct).ConfigureAwait(false);
        var missing = new List<string>();
        foreach (var requirement in requirements)
        {
            installed.TryGetValue(requirement.Name, out var version);
            if (version is not null && version >= requirement.MinVersion) continue;

            missing.Add($"{requirement.Name} 需要 ≥ {requirement.MinVersion}（当前 " +
                        (version is null ? "未安装" : $"v{version}") + "）");
        }

        return missing.Count == 0
            ? "内置包依赖已满足。"
            : "内置依赖不满足：" + string.Join("；", missing);
    }

    private static IReadOnlyList<(string Name, Version MinVersion)> ReadBundledRequirements()
    {
        var list = new List<(string Name, Version MinVersion)>();
        try
        {
            var bundle = Directory.GetFiles(BundledWingetFolder, "Microsoft.DesktopAppInstaller*.msixbundle")
                             .FirstOrDefault()
                         ?? Directory.GetFiles(BundledWingetFolder, "Microsoft.DesktopAppInstaller*.appxbundle")
                             .FirstOrDefault();
            if (bundle is null) return list;

            using var bundleArchive = ZipFile.OpenRead(bundle);
            var entry = bundleArchive.Entries.FirstOrDefault(item =>
                item.Name.StartsWith("AppInstaller_x64.", StringComparison.OrdinalIgnoreCase)
                && (item.Name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase)
                    || item.Name.EndsWith(".appx", StringComparison.OrdinalIgnoreCase)));
            if (entry is null) return list;

            using var msixStream = new MemoryStream();
            using (var stream = entry.Open()) stream.CopyTo(msixStream);
            msixStream.Position = 0;

            using var msix = new ZipArchive(msixStream, ZipArchiveMode.Read);
            var manifest = msix.GetEntry("AppxManifest.xml");
            if (manifest is null) return list;

            using var manifestStream = manifest.Open();
            var document = XDocument.Load(manifestStream);
            XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
            foreach (var dependency in document.Descendants(ns + "PackageDependency"))
            {
                var name = dependency.Attribute("Name")?.Value;
                var min = dependency.Attribute("MinVersion")?.Value;
                if (!string.IsNullOrWhiteSpace(name) && Version.TryParse(min, out var version))
                {
                    list.Add((name, version));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取内置 App Installer 依赖失败: {ex.Message}");
        }

        return list;
    }

    private async Task<Dictionary<string, Version?>> GetInstalledDependencyVersionsAsync(
        IReadOnlyList<string> dependencies, CancellationToken ct)
    {
        var result = new Dictionary<string, Version?>(StringComparer.OrdinalIgnoreCase);
        if (dependencies.Count == 0) return result;

        var names = string.Join(",", dependencies.Select(name => $"'{name.Replace("'", "''")}'"));
        var script =
            "$names = @(" + names + ")\n" +
            "$result = foreach ($name in $names) {\n" +
            "    $pkg = Get-AppxPackage -Name $name -ErrorAction SilentlyContinue |\n" +
            "        Sort-Object Version -Descending | Select-Object -First 1\n" +
            "    [pscustomobject]@{ Name = $name; Version = if ($pkg) { [string]$pkg.Version } else { '' } }\n" +
            "}\n" +
            "$result | ConvertTo-Json -Compress";

        try
        {
            var runner = new PowerShellRunner();
            var output = await runner.RunAsync(script, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!output.Success || string.IsNullOrWhiteSpace(output.StdOut)) return result;

            using var document = JsonDocument.Parse(output.StdOut);
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : new List<JsonElement> { document.RootElement };

            foreach (var item in items)
            {
                var name = item.TryGetProperty("Name", out var nameElement) ? nameElement.GetString() : null;
                var versionText = item.TryGetProperty("Version", out var versionElement) ? versionElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                result[name] = Version.TryParse(versionText, out var version) ? version : null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取已安装依赖版本失败: {ex.Message}");
        }

        return result;
    }

    private static async Task<PowerShellResult> InstallAppxAsync(
        PowerShellRunner runner, string path, IReadOnlyList<string>? dependencies, CancellationToken ct)
    {
        var dependencyScript = string.Empty;
        var dependencyArgument = string.Empty;
        if (dependencies is { Count: > 0 })
        {
            var list = string.Join(",", dependencies.Select(item => $"'{item.Replace("'", "''")}'"));
            dependencyScript = $"$deps = @({list}); ";
            dependencyArgument = "-DependencyPath $deps ";
        }

        var script = dependencyScript +
            $"Add-AppxPackage -Path '{path.Replace("'", "''")}' {dependencyArgument}" +
            "-ForceUpdateFromAnyVersion -ForceApplicationShutdown -ErrorAction Stop";
        return await runner.RunAsync(script, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
    }

    /// <summary>从 PowerShell 输出里提取真正的部署错误（HRESULT / Add-AppxPackage 行）。</summary>
    private static string ExtractDeploymentError(PowerShellResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).Trim();
        if (string.IsNullOrWhiteSpace(text)) return "未知错误（无输出）";

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        return lines.FirstOrDefault(line =>
                   line.Contains("HRESULT", StringComparison.OrdinalIgnoreCase)
                   || line.Contains("0x8", StringComparison.OrdinalIgnoreCase))
               ?? lines.FirstOrDefault(line => line.Contains("Add-AppxPackage", StringComparison.OrdinalIgnoreCase))
               ?? lines[^1];
    }

    private static string? TryExtractBundle(string packagePath)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), "fs-winget-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            ZipFile.ExtractToDirectory(packagePath, temp);

            var files = Directory.EnumerateFiles(temp, "*.msix", SearchOption.AllDirectories).ToList();
            return files.FirstOrDefault(file => Path.GetFileName(file).Contains("x64", StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Warn($"解包 winget 安装包失败: {ex.Message}");
            return null;
        }
    }

    private static void TryDeleteDirectory(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try { Directory.Delete(folder, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>清除 winget 本地缓存（安装临时文件、诊断输出），不删除源与已安装软件。</summary>
    public Task<OperationResult> ClearCacheAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            long bytes = 0;
            var files = 0;
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            var folders = new[]
            {
                Path.Combine(localAppData, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "Temp"),
                Path.Combine(localAppData, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "DiagOutputDir"),
                Path.Combine(Path.GetTempPath(), "WinGet"),
                Path.Combine(localAppData, "Temp", "WinGet")
            };

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder)) continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var length = new FileInfo(file).Length;
                            File.Delete(file);
                            bytes += length;
                            files++;
                        }
                        catch { /* 占用文件跳过 */ }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理缓存目录失败 {folder}: {ex.Message}");
                }
            }

            var text = files == 0
                ? "没有可清理的缓存"
                : $"已清除 {files} 个缓存文件（{FormatSize(bytes)}）";
            Log.Info($"winget 缓存清理: {text}");
            return OperationResult.Ok(text);
        }, ct);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:0.#} {units[index]}";
    }

    /// <summary>
    /// 修复 winget：重置并更新源、清理安装器缓存、重新验证版本。
    /// 适用于「winget install 直接崩溃（0xC0000005）」这类运行时异常。
    /// </summary>
    public async Task<OperationResult> RepairAsync(Action<string>? onOutput, CancellationToken ct)
    {
        Log.Info("开始修复 winget");

        var version = await RunRawAsync("--version", null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (!version.Success)
        {
            onOutput?.Invoke("winget 不可用，尝试使用内置安装包升级…");
            var local = await TryInstallBundledAsync(
                onOutput is null ? null : new Progress<string>(onOutput), ct).ConfigureAwait(false);
            if (!local.Success)
            {
                return OperationResult.Fail(local.Message + "；请确认软件目录 assets\\winget 下有安装包，或使用「从本地文件安装」");
            }

            onOutput?.Invoke(local.Message);
            version = await RunRawAsync("--version", null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (!version.Success) return OperationResult.Fail("升级后 winget 仍不可用");
        }
        else
        {
            var rawVersion = version.Output.Trim().TrimStart('v', 'V');
            if (global::System.Version.TryParse(rawVersion, out var parsed) && parsed < new global::System.Version(1, 6))
            {
                onOutput?.Invoke($"当前 winget {parsed} 低于 1.6，尝试使用内置安装包升级…");
                var local = await TryInstallBundledAsync(
                    onOutput is null ? null : new Progress<string>(onOutput), ct).ConfigureAwait(false);
                if (local.Success)
                {
                    onOutput?.Invoke(local.Message);
                    version = await RunRawAsync("--version", null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                }
            }
        }

        onOutput?.Invoke("正在重置 winget 源…");
        var reset = await RunRawAsync(
            "source reset --disable-interactivity",
            onOutput, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);

        onOutput?.Invoke("正在更新 winget 源…");
        var update = await RunRawAsync(
            "source update --disable-interactivity",
            onOutput, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);

        // 清理损坏的下载缓存，避免再次触发安装器崩溃
        try
        {
            var localState = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState");

            foreach (var cacheName in new[] { "Temp", "DiagOutputDir" })
            {
                var cachePath = Path.Combine(localState, cacheName);
                if (!Directory.Exists(cachePath)) continue;

                foreach (var file in Directory.EnumerateFiles(cachePath, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { /* 占用忽略 */ }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理 winget 缓存失败: {ex.Message}");
        }

        _available = null;
        InvalidateInstalledCache();
        var verify = await RunRawAsync("--version", onOutput, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

        var detail = $"版本 {verify.Output.Trim()}；源重置{(reset.Success ? "成功" : "失败")}；源更新{(update.Success ? "成功" : "失败")}";
        Log.Info($"winget 修复完成: {detail}");
        return verify.Success
            ? OperationResult.Ok($"winget 已修复（{detail}）")
            : OperationResult.Fail($"修复后 winget 仍不可用（{detail}）");
    }

    public Task<WingetResult> InstallAsync(string id, Action<string>? onOutput, CancellationToken ct, string? location = null)
        => RunInstallWithSourceFallbackAsync(id, onOutput, silent: true, ct, location);

    /// <summary>静默参数被 winget 拒绝时（0x8A150002）的回退：允许安装器显示自己的界面。</summary>
    public Task<WingetResult> InstallInteractiveAsync(string id, Action<string>? onOutput, CancellationToken ct, string? location = null)
        => RunInstallWithSourceFallbackAsync(id, onOutput, silent: false, ct, location);

    /// <summary>
    /// 安装失败且原因是「源不存在」（0x8A150012，常见提示：msstore）时自动修复：
    /// 补齐 msstore 源 → 不带 --source 重试 → 再带 --source winget 重试。
    /// </summary>
    private async Task<WingetResult> RunInstallWithSourceFallbackAsync(
        string id, Action<string>? onOutput, bool silent, CancellationToken ct, string? location = null)
    {
        var hasLocation = !string.IsNullOrWhiteSpace(location);
        IReadOnlyList<string> Build(bool withSource)
        {
            var arguments = new List<string> { "install", "--id", id, "-e" };
            if (silent) arguments.Add("--silent");
            arguments.Add("--accept-package-agreements");
            arguments.Add("--accept-source-agreements");
            if (hasLocation)
            {
                // 由系统负责引号转义，路径含空格也安全；并非所有安装器都支持 --location
                arguments.Add("--location");
                arguments.Add(location!.Trim());
            }
            if (withSource)
            {
                arguments.Add("--source");
                arguments.Add("winget");
            }
            return arguments;
        }

        var result = await RunRawAsync(Build(true), onOutput, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (result.Success || result.Canceled) return result;

        // 源索引校验失败（0x8A15005E）：先重置源自救，再按 msstore 缺失兜底，最后给出可执行的修复指引
        if (IsSourceIntegrityError(result))
        {
            Log.Warn($"winget 源索引校验失败（exit {result.ExitCode}）：{ShortLine(result.Output)}");
            onOutput?.Invoke("源索引校验失败（0x8A15005E），正在重置 winget 源…");
            await RepairSourcesAsync(ct).ConfigureAwait(false);

            result = await RunRawAsync(Build(true), onOutput, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            if (result.Success || result.Canceled) return result;
            if (!IsSourceIntegrityError(result)) return result;

            onOutput?.Invoke(Version is not null && Version < MinimumSourceCapableVersion
                ? $"当前 winget v{Version} 低于 {MinimumSourceCapableVersion}，无法校验现代源索引；请更新 App Installer（内置离线包或 Microsoft Store）"
                : "重置源后仍无法校验源索引，请稍后重试");
            return result;
        }

        // 源索引里找不到该包（0x8A150014）：索引可能滞后，刷新后再试一次
        if (IsPackageNotFound(result))
        {
            Log.Warn($"winget 源里找不到该包（exit {result.ExitCode}）：{ShortLine(result.Output)}");
            onOutput?.Invoke("源索引里找不到该包，正在刷新源索引…");
            await UpdateSourcesAsync(null, ct).ConfigureAwait(false);

            result = await RunRawAsync(Build(true), onOutput, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
            if (result.Success || result.Canceled || !IsPackageNotFound(result)) return result;

            // 仍然找不到：重置源后再试一次（索引损坏的情况）
            onOutput?.Invoke("刷新索引后仍未找到该包，正在重置源重试…");
            await RepairSourcesAsync(ct).ConfigureAwait(false);
            return await RunRawAsync(Build(true), onOutput, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        }

        if (!NeedsMsStoreSource(result)) return result;

        Log.Warn($"winget 报告源不存在（exit {result.ExitCode}）：{ShortLine(result.Output)}");
        onOutput?.Invoke("该安装包引用了 msstore 源，正在补充源并重试…");
        await EnsureMsStoreSourceAsync(ct).ConfigureAwait(false);

        result = await RunRawAsync(Build(false), onOutput, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
        if (result.Success || result.Canceled || !NeedsMsStoreSource(result)) return result;

        onOutput?.Invoke("仍未成功，改用 winget 源重试…");
        return await RunRawAsync(Build(true), onOutput, TimeSpan.FromMinutes(15), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 刷新 winget 源的索引。
    /// 注意：新版 winget 不再接受 `source update winget`（位置参数），必须用 `--name winget`，
    /// 否则会静默失败并导致"源已切换但搜不到包"（0x8A150014）。旧版不接受 --name 时回退整源刷新。
    /// </summary>
    private async Task<WingetResult> UpdateWingetSourceIndexAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromMinutes(3);
        // 不能用 --accept-source-agreements（source update 不支持该参数）
        var byName = await RunRawAsync(
            "source update --name winget --disable-interactivity",
            null, limit, ct).ConfigureAwait(false);
        if (byName.Success) return byName;

        var all = await RunRawAsync(
            "source update --disable-interactivity",
            null, limit, ct).ConfigureAwait(false);
        return all.Success ? all : byName;
    }

    /// <summary>0x8A150014 = 源索引里没有匹配的应用（常见于镜像索引滞后或 ID 变更）。</summary>
    private static bool IsPackageNotFound(WingetResult result)
        => result.ExitCode == -1978335212
           || result.Output.Contains("0x8A150014", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains("No package found", StringComparison.OrdinalIgnoreCase);

    /// <summary>0x8A15005E = 源数据完整性校验失败（索引签名/格式与客户端不匹配）。</summary>
    private static bool IsSourceIntegrityError(WingetResult result)
        => result.ExitCode == -1978335138
           || result.Output.Contains("0x8A15005E", StringComparison.OrdinalIgnoreCase)
           || result.Output.Contains("source data integrity", StringComparison.OrdinalIgnoreCase);

    /// <summary>重置 winget 源为官方并刷新索引；失败只记日志，由调用方决定下一步。</summary>
    private async Task RepairSourcesAsync(CancellationToken ct)
    {
        // source reset 不接受 --accept-source-agreements；用 --name 指定源（新版写法）
        var reset = await RunRawAsync("source reset --name winget --disable-interactivity",
            null, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        if (!reset.Success)
        {
            await RunRawAsync("source reset winget --disable-interactivity",
                null, TimeSpan.FromSeconds(120), ct).ConfigureAwait(false);
        }

        await UpdateWingetSourceIndexAsync(ct).ConfigureAwait(false);
    }

    /// <summary>0x8A150012 = 源名称不存在；清单引用 msstore 但系统缺少该源时命中。</summary>
    private static bool NeedsMsStoreSource(WingetResult result)
        => result.ExitCode == -1978335214
           || result.Output.Contains("msstore", StringComparison.OrdinalIgnoreCase);

    public Task<WingetResult> UninstallAsync(string id, Action<string>? onOutput, CancellationToken ct)
    {
        var args = new List<string> { "uninstall", "--id", id, "-e", "--silent", "--accept-source-agreements" };
        return RunRawAsync(args, onOutput, TimeSpan.FromMinutes(10), ct);
    }

    /// <summary>
    /// 升级单个已安装的包（不重装、保留配置）。--include-unknown 让 manifest 缺版本信息时也能升。
    /// </summary>
    public Task<WingetResult> UpgradeAsync(string id, Action<string>? onOutput, CancellationToken ct)
    {
        var args = new List<string>
        {
            "upgrade", "--id", id, "-e", "--silent",
            "--accept-source-agreements", "--accept-package-agreements",
            "--include-unknown", "--disable-interactivity"
        };
        return RunRawAsync(args, onOutput, TimeSpan.FromMinutes(30), ct);
    }

    /// <summary>
    /// 查询可升级的包（ID → 可用版本）。返回 null 表示**查询失败**（超时/源不可用），
    /// 返回空字典表示查询成功但当前没有可升级项——调用方要能区分这两种情况。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetUpgradeInfoAsync(CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = await RunRawAsync(
            "upgrade --include-unknown --disable-interactivity",
            null, TimeSpan.FromSeconds(90), ct, maxBufferChars: 0).ConfigureAwait(false);
        if (result.Canceled) return null;
        if (!result.Success) return null;

        foreach (var line in result.Output.Split('\n'))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4) continue;

            var id = tokens[^4];
            var available = tokens[^2];
            if (id.IndexOf('.') <= 0) continue;                       // winget ID 必带发布者前缀
            if (!Version.TryParse(available, out _)) continue;
            map[id] = available;
        }

        return map;
    }

    private static string ShortLine(string output)
    {
        var line = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0
                                    && !value.Contains("aka.ms/winget", StringComparison.OrdinalIgnoreCase)
                                    && !value.Contains("更多帮助", StringComparison.Ordinal)
                                    && !value.Contains("more help", StringComparison.OrdinalIgnoreCase));
        return line ?? "无输出";
    }

    private async Task<WingetResult> RunRawAsync(
        string arguments, Action<string>? onOutput, TimeSpan timeout, CancellationToken ct,
        int maxBufferChars = 12000, IReadOnlyList<string>? argumentList = null)
    {
        var command = argumentList is null
            ? $"winget.exe {arguments}".TrimEnd()
            : NativeCommandTrace.Format("winget.exe", argumentList);
        NativeCommandTrace.Started(command, Environment.CurrentDirectory);
        var stopwatch = Stopwatch.StartNew();
        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = ConsoleEncoding.OemCodePage,
            StandardErrorEncoding = ConsoleEncoding.OemCodePage
        };

        // 含外部输入（winget ID）的调用走 ArgumentList，由系统负责引号转义
        if (argumentList is null)
        {
            startInfo.Arguments = arguments;
        }
        else
        {
            foreach (var argument in argumentList) startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"winget 启动失败: {ex.Message}");
            NativeCommandTrace.Completed(
                command, -1, ex.ToString(), stopwatch.Elapsed, Environment.CurrentDirectory);
            return new WingetResult { ExitCode = -1, Output = ex.Message };
        }

        var output = new StringBuilder();
        var outputLock = new object();
        void Report(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var clean = AnsiRegex.Replace(line, string.Empty).Trim();
            if (clean.Length == 0) return;
            lock (outputLock)
            {
                output.AppendLine(clean);
                // 安装日志只保留尾部；已安装列表必须完整保留，否则会漏判
                if (maxBufferChars > 0 && output.Length > maxBufferChars)
                {
                    output.Remove(0, maxBufferChars / 3);
                }
            }
            onOutput?.Invoke(clean);
        }

        var outTask = ReadLinesAsync(process.StandardOutput, Report);
        var errTask = ReadLinesAsync(process.StandardError, Report);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(800)).ConfigureAwait(false);
            var canceledOutput = output.ToString();
            NativeCommandTrace.Completed(
                command, -1, canceledOutput, stopwatch.Elapsed, Environment.CurrentDirectory);
            return new WingetResult { ExitCode = -1, Output = canceledOutput, Canceled = true };
        }

        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(1000)).ConfigureAwait(false);
        var finalOutput = output.ToString();
        NativeCommandTrace.Completed(
            command, process.ExitCode, finalOutput, stopwatch.Elapsed, Environment.CurrentDirectory);
        return new WingetResult { ExitCode = process.ExitCode, Output = finalOutput };
    }

    /// <summary>参数列表入口：含外部输入（winget ID、路径、URL）的调用走这里，由系统转义。</summary>
    private Task<WingetResult> RunRawAsync(
        IReadOnlyList<string> arguments, Action<string>? onOutput, TimeSpan timeout, CancellationToken ct,
        int maxBufferChars = 12000)
        => RunRawAsync(string.Empty, onOutput, timeout, ct, maxBufferChars, arguments);

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            // 注意：不能用 reader.EndOfStream 作循环条件——它是同步阻塞读，
            // 会让 RunRawAsync 在第一个 await 之前就卡住调用线程（UI 冻结、取消按钮点不动）。
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                onLine(line);
            }
        }
        catch
        {
            // 进程结束时读取可能抛异常
        }
    }
}

public sealed class WingetInstallBatchOperation : OperationBase
{
    private readonly WingetService _winget;
    private readonly IReadOnlyList<SoftwareDefinition> _packages;
    private readonly string? _installLocation;
    private readonly DirectInstallerService? _direct;
    private readonly List<SoftwareDefinition> _installedNow = new();
    private readonly List<SoftwareDefinition> _failedPackages = new();
    private readonly List<string> _failures = new();
    private readonly Dictionary<string, bool> _alreadyInstalled = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<SoftwareDefinition> FailedPackages => _failedPackages;
    public IReadOnlyList<SoftwareDefinition> InstalledPackages => _installedNow;

    /// <param name="installLocation">自定义安装目录（可选），透传为 winget `--location`。</param>
    /// <param name="directInstaller">官方直链兜底（可选）：winget 失败时改用厂商官方安装包。</param>
    public WingetInstallBatchOperation(
        WingetService winget,
        IReadOnlyList<SoftwareDefinition> packages,
        string? installLocation = null,
        DirectInstallerService? directInstaller = null)
    {
        _winget = winget;
        _packages = packages;
        _installLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation.Trim();
        _direct = directInstaller;
    }

    public override string Id => "software.winget.install";
    public override string Name => _packages.Count == 1 ? $"安装「{_packages[0].Name}」" : $"批量安装 {_packages.Count} 个软件";
    public override string Description => _installLocation is null
        ? "通过 winget 静默安装；单个失败会记录并继续，已成功的软件保留，不自动卸载"
        : $"通过 winget 静默安装到 {_installLocation}（仅对支持 --location 的安装器生效）；单个失败会记录并继续";
    public override RiskLevel RiskLevel => RiskLevel.Caution;
    public override bool CanRollback => false;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => _packages.Select(package => new ChangeDescriptor
    {
        Target = package.Id,
        Kind = "winget 软件",
        Before = "未安装/未知",
        After = "静默安装",
        Impact = package.Description
    }).ToList();

    public override async Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        foreach (var package in _packages)
        {
            _alreadyInstalled[package.Id] = await _winget.IsInstalledAsync(package.Id, ct).ConfigureAwait(false);
        }
        var entry = new SnapshotEntry
        {
            Kind = SnapshotKind.Other,
            Target = "WingetBatch",
            Existed = true,
            DataJson = JsonSerializer.Serialize(_alreadyInstalled, JsonConfig.Options)
        };
        return CreateSnapshot(new[] { entry });
    }

    protected override async Task<OperationResult> ApplyCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在检查 winget 源（使用微软官方源）…", Percent = 1 });
        var source = await _winget.EnsureOfficialSourceAsync(ct).ConfigureAwait(false);
        if (!source.Success)
        {
            progress?.Report(new OperationStep
            {
                Message = "winget 源检查失败，继续使用当前源：" + source.Message,
                Percent = 2,
                IsError = true
            });
        }

        // 版本太旧时每个包都会以 0x8A15005E 失败，先给出一次结论，不要白跑十几分钟
        if (!_winget.IsSourceCapable)
        {
            var current = _winget.Version?.ToString() ?? "未知";
            return OperationResult.Fail(
                $"当前 winget v{current} 低于 {WingetService.MinimumSourceCapableVersion}，无法校验当前源索引（0x8A15005E），" +
                "安装必然失败。请先更新 App Installer：在「推荐软件」页点「打开 Microsoft Store」安装最新版，" +
                "或把 assets\\winget 里的内置离线包换成 ≥ 1.11 的版本后重试");
        }

        for (var i = 0; i < _packages.Count; i++)
        {
            var package = _packages[i];
            if (_alreadyInstalled.TryGetValue(package.Id, out var installed) && installed)
            {
                progress?.Report(new OperationStep
                {
                    Message = $"{package.Name} 已安装，跳过",
                    Percent = (int)((i + 1) * 100.0 / _packages.Count)
                });
                continue;
            }

            // 安装前再确认一次：包含同软件的其他渠道（如 Chrome Beta 的 ID 是 Google.Chrome.Beta.EXE）
            if (await IsAnyVariantInstalledAsync(package, ct).ConfigureAwait(false))
            {
                progress?.Report(new OperationStep
                {
                    Message = $"{package.Name} 已安装（其他渠道），跳过",
                    Percent = (int)((i + 1) * 100.0 / _packages.Count)
                });
                continue;
            }

            progress?.Report(new OperationStep
            {
                Message = $"正在安装 {package.Name}（{i + 1}/{_packages.Count}）…",
                Percent = (int)(i * 100.0 / _packages.Count)
            });

            if (InstallerHealth.IsUnhealthy)
            {
                _failures.Add($"{package.Name}：连续 {InstallerHealth.CrashCount} 个安装器以 0xC0000005 退出，已停止后续安装");
                _failedPackages.Add(package);
                for (var j = i + 1; j < _packages.Count; j++) _failedPackages.Add(_packages[j]);
                break;
            }

            var result = await _winget.InstallAsync(package.Id, line =>
            {
                progress?.Report(new OperationStep { Message = line, Percent = (int)(i * 100.0 / _packages.Count) });
            }, ct, _installLocation).ConfigureAwait(false);

            if (result.Canceled) throw new OperationCanceledException(ct);
            if (!result.Success && result.ExitCode == -1978335212)
            {
                // winget 源过期/索引缺失时找不到包，更新源后重试一次
                progress?.Report(new OperationStep { Message = "winget 源中未找到该包，正在更新源…", Percent = 10 });
                await _winget.UpdateSourcesAsync(null, ct).ConfigureAwait(false);
                result = await _winget.InstallAsync(package.Id, line =>
                {
                    progress?.Report(new OperationStep { Message = line, Percent = (int)(i * 100.0 / _packages.Count) });
                }, ct, _installLocation).ConfigureAwait(false);
                if (result.Canceled) throw new OperationCanceledException(ct);
            }

            if (!result.Success)
            {
                // 已安装判断：先查已安装索引，再逐个别名做精确查询
                var index = await _winget.GetInstalledIndexAsync(ct).ConfigureAwait(false);
                var alreadyInstalled = index?.Matches(package) == true;
                if (!alreadyInstalled)
                {
                    foreach (var alias in package.Aliases)
                    {
                        if (await _winget.IsInstalledAsync(alias, ct).ConfigureAwait(false))
                        {
                            alreadyInstalled = true;
                            break;
                        }
                    }
                }
                if (alreadyInstalled)
                {
                    progress?.Report(new OperationStep
                    {
                        Message = $"{package.Name} 已检测到安装（可能为其他渠道），跳过",
                        Percent = (int)((i + 1) * 100.0 / _packages.Count)
                    });
                    continue;
                }

                // 0x8A150002：winget 拒绝静默参数，回退为允许安装器显示界面
                if (result.ExitCode == -1978335230)
                {
                    progress?.Report(new OperationStep
                    {
                        Message = $"{package.Name} 静默安装被拒绝，改为交互安装…",
                        Percent = (int)(i * 100.0 / _packages.Count)
                    });
                    result = await _winget.InstallInteractiveAsync(package.Id, line =>
                    {
                        progress?.Report(new OperationStep { Message = line, Percent = (int)(i * 100.0 / _packages.Count) });
                    }, ct, _installLocation).ConfigureAwait(false);
                    if (result.Canceled) throw new OperationCanceledException(ct);
                    if (result.Success)
                    {
                        _installedNow.Add(package);
                        _winget.InvalidateInstalledCache();
                        continue;
                    }
                }

                // 0x8A150011：安装包哈希与清单不一致，多为缓存损坏；清理缓存并重试一次
                if (result.ExitCode == -1978335215)
                {
                    progress?.Report(new OperationStep
                    {
                        Message = $"{package.Name} 安装包哈希不匹配，清理缓存后重试…",
                        Percent = (int)(i * 100.0 / _packages.Count)
                    });
                    await _winget.ClearCacheAsync(ct).ConfigureAwait(false);
                    result = await _winget.InstallAsync(package.Id, line =>
                    {
                        progress?.Report(new OperationStep { Message = line, Percent = (int)(i * 100.0 / _packages.Count) });
                    }, ct, _installLocation).ConfigureAwait(false);
                    if (result.Canceled) throw new OperationCanceledException(ct);
                    if (result.Success)
                    {
                        _installedNow.Add(package);
                        _winget.InvalidateInstalledCache();
                        InstallerHealth.Reset();
                        continue;
                    }
                }

                var reason = result.ExitCode switch
                {
                    -1978335230 => "winget 拒绝当前静默参数（常见于已安装其他渠道版本或安装器不支持静默）",
                    -1073741819 => "安装器异常退出（0xC0000005）。常见原因：winget 组件异常、系统组件/安全中心被不完整移除、安装器与系统不兼容。可先手动运行 `winget install --id <ID> -e`；若同样崩溃，先在「推荐软件」页点「修复 winget」，仍失败则执行 sfc /scannow 与 DISM /Online /Cleanup-Image /RestoreHealth",
                    -1978335212 => "winget 源中找不到该包（已尝试更新源，ID 可能已变更）",
                    -1978335214 => "winget 源不存在（软件包引用了 msstore 源，但系统未配置该源）；可在「推荐软件」页点「修复 winget」，或手动执行 winget source add msstore https://storeedgefd.dsx.mp.microsoft.com/v9.0 --type Microsoft.Rest",
                    -1978335215 => "安装包哈希与清单不一致（缓存损坏或源内容变化）；已清理缓存重试仍失败，请手动下载安装或稍后重试",
                    -1978335138 => "winget 源索引校验失败（0x8A15005E：源数据完整性/签名与客户端不匹配）。常见原因：winget 版本过旧（内置离线包 1.6 已无法校验当前源索引），或镜像索引未同步。已自动重置源并重试；仍失败请更新 App Installer 到 ≥ 1.11（换用新版 assets\\winget 离线包，或在 Microsoft Store 更新）后重试",
                    _ => LastLine(result.Output)
                };

                if (result.ExitCode == -1073741819) InstallerHealth.NoteCrash();

                // 直链兜底：winget 失败时改用厂商官方安装包（只对已登记的 7 个高频软件生效）
                var fallback = _direct is null ? null : DirectInstallerCatalog.Find(package.Id);
                if (fallback is not null)
                {
                    progress?.Report(new OperationStep
                    {
                        Message = $"{package.Name} 用 winget 失败（{LastLine(result.Output)}），改用官方直链下载安装…",
                        Percent = (int)(i * 100.0 / _packages.Count),
                        IsError = true
                    });

                    var direct = await _direct!.InstallAsync(fallback, progress, ct).ConfigureAwait(false);
                    if (direct.Success)
                    {
                        _installedNow.Add(package);
                        _winget.InvalidateInstalledCache();
                        continue;
                    }

                    _failures.Add($"{package.Name}（winget exit {result.ExitCode}；直链兜底失败：{direct.Message}）");
                    _failedPackages.Add(package);
                    progress?.Report(new OperationStep
                    {
                        Message = $"{package.Name} 直链兜底也失败，已跳过并继续：{direct.Message}",
                        Percent = (int)((i + 1) * 100.0 / _packages.Count)
                    });
                    continue;
                }

                _failures.Add($"{package.Name}（exit {result.ExitCode}）：{reason}");
                _failedPackages.Add(package);
                progress?.Report(new OperationStep
                {
                    Message = $"{package.Name} 安装失败，已跳过并继续：{reason}",
                    Percent = (int)((i + 1) * 100.0 / _packages.Count)
                });
                continue;
            }
            _installedNow.Add(package);
            _winget.InvalidateInstalledCache();
            InstallerHealth.Reset();
        }
        if (_failures.Count == 0)
        {
            return OperationResult.Ok($"已安装 {_installedNow.Count} 个软件");
        }

        return OperationResult.Fail(
            $"已安装 {_installedNow.Count} 个软件，{_failures.Count} 个失败（已安装的会保留）：" +
            string.Join("；", _failures));
    }

    private async Task<bool> IsAnyVariantInstalledAsync(SoftwareDefinition package, CancellationToken ct)
    {
        try
        {
            if (await _winget.IsInstalledAsync(package.Id, ct).ConfigureAwait(false)) return true;
            foreach (var alias in package.Aliases)
            {
                if (await _winget.IsInstalledAsync(alias, ct).ConfigureAwait(false)) return true;
            }
        }
        catch
        {
            // 检查失败不阻断安装
        }
        return false;
    }

    protected override Task<OperationResult> RollbackCoreAsync(
        OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
        => Task.FromResult(OperationResult.Fail("软件安装不支持自动回滚；已安装的软件会保留"));

    private static string LastLine(string output)
    {
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0
                           && !line.Contains("aka.ms/winget", StringComparison.OrdinalIgnoreCase)
                           && !line.Contains("更多帮助", StringComparison.Ordinal)
                           && !line.Contains("more help", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return lines.Count == 0 ? "无输出" : lines[^1];
    }
}
