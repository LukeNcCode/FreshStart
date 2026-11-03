using System.Diagnostics;
using System.Text;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;

namespace FreshStart.Services.Software;

public sealed class ChocoResult
{
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public bool Canceled { get; init; }
    public bool Success => !Canceled && ExitCode == 0;
}

/// <summary>
/// Chocolatey 安装后端（0.29.0-beta 实验，**当前未启用**）。
/// 主线仍用 winget + 官方直链兜底；本类保留原因：Chocolatey 覆盖 34/39、国内无可用的 choco 镜像，
/// 只有显式在 CompositionRoot 里注册才会生效。若将来接入，请在「推荐软件」页同时替换 ViewModel。
/// 注意：Chocolatey 在中国大陆没有公共镜像（阿里云/腾讯云均无该源），下载仍走各厂商原始地址。
/// </summary>
public sealed class ChocolateyService
{
    /// <summary>官方 NuGet v2 源。</summary>
    public const string OfficialSourceUrl = "https://community.chocolatey.org/api/v2/";

    /// <summary>源名称（choco 默认就叫 chocolatey）。</summary>
    public const string SourceName = "chocolatey";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(45);

    private IReadOnlyDictionary<string, string>? _installedCache;
    private DateTimeOffset _installedCacheAt;

    public Version? Version { get; private set; }
    public bool IsAvailable => Version is not null;

    /// <summary>choco.exe 的默认安装位置。</summary>
    public static string ChocoExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "chocolatey", "bin", "choco.exe");

    /// <summary>
    /// winget ID → Chocolatey 包名。仅在 Chocolatey 官方源里实测存在的映射才收录；
    /// 未收录的条目在 UI 上会标记「Chocolatey 无此包」。
    /// </summary>
    public static IReadOnlyDictionary<string, string> PackageMap { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Google.Chrome"] = "googlechrome",
            ["Mozilla.Firefox"] = "firefox",
            ["7zip.7zip"] = "7zip",
            ["Bandisoft.Bandizip"] = "bandizip",
            ["Microsoft.PowerToys"] = "powertoys",
            ["voidtools.Everything"] = "everything",
            ["Notepad++.Notepad++"] = "notepadplusplus",
            ["Microsoft.VisualStudioCode"] = "vscode",
            ["Git.Git"] = "git",
            ["Python.Python.3.12"] = "python",
            ["OpenJS.NodeJS.LTS"] = "nodejs-lts",
            ["Microsoft.DotNet.SDK.8"] = "dotnet-8.0-sdk",
            ["Microsoft.DotNet.DesktopRuntime.8"] = "dotnet-8.0-desktopruntime",
            ["Microsoft.VCRedist.2015+.x64"] = "vcredist-all",
            ["VideoLAN.VLC"] = "vlc",
            ["Daum.PotPlayer"] = "potplayer",
            ["OBSProject.OBSStudio"] = "obs-studio",
            ["Discord.Discord"] = "discord",
            ["Telegram.TelegramDesktop"] = "telegram",
            ["Tencent.WeChat"] = "wechat",
            // Chocolatey 只有传统 PC QQ（9.3.x），不是 QQ NT
            ["Tencent.QQ.NT"] = "tencentqq",
            ["Adobe.Acrobat.Reader.64-bit"] = "adobereader",
            ["Valve.Steam"] = "steam",
            ["Microsoft.WindowsTerminal"] = "microsoft-windows-terminal",
            ["JetBrains.Toolbox"] = "jetbrainstoolbox",
            ["Docker.DockerDesktop"] = "docker-desktop",
            ["Postman.Postman"] = "postman",
            ["Figma.Figma"] = "figma",
            ["Spotify.Spotify"] = "spotify",
            ["NetEase.CloudMusic"] = "netease-cloudmusic",
            ["Tencent.TIM"] = "tim",
            ["Alibaba.DingTalk.Mainland"] = "dingtalk",
            ["SlackTechnologies.Slack"] = "slack",
            ["Zoom.Zoom"] = "zoom"
        };

    /// <summary>Chocolatey 官方源里确实没有对应包的条目。</summary>
    public static IReadOnlySet<string> Unavailable { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Sogou.SogouInput",
        "Kingsoft.WPSOffice",
        "ByteDance.Feishu",
        "Bilibili.Bilibili",
        "Tencent.QQMusic"
    };

    /// <summary>解析该软件在 Chocolatey 里对应的包名；返回 null 表示没有可用包。</summary>
    public static string? ResolvePackageName(string? wingetId)
    {
        if (string.IsNullOrWhiteSpace(wingetId)) return null;
        var id = wingetId.Trim();
        if (Unavailable.Contains(id)) return null;
        return PackageMap.TryGetValue(id, out var package) ? package : id;
    }

    /// <summary>是否已知没有 Chocolatey 包（用于 UI 置灰与跳过）。</summary>
    public static bool IsKnownUnavailable(string? wingetId)
        => !string.IsNullOrWhiteSpace(wingetId) && Unavailable.Contains(wingetId.Trim());

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var result = await RunAsync("--version", null, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            Version = null;
            return false;
        }

        var raw = result.Output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Length > 0) ?? string.Empty;
        Version = global::System.Version.TryParse(raw.TrimStart('v', 'V'), out var version) ? version : null;
        Log.Info($"Chocolatey 版本: {raw}");
        return true;
    }

    /// <summary>
    /// 安装 Chocolatey：执行官方安装脚本（需要管理员与联网）。
    /// 已安装时直接返回成功。
    /// </summary>
    public async Task<OperationResult> EnsureInstalledAsync(
        IProgress<string>? progress, CancellationToken ct)
    {
        if (await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return OperationResult.Ok($"已安装 Chocolatey v{Version}");
        }

        if (!IsElevated())
        {
            return OperationResult.Fail("安装 Chocolatey 需要管理员权限，请以管理员身份重新启动 FreshStart");
        }

        progress?.Report("正在从官方源安装 Chocolatey（需要联网，约 1–2 分钟）…");
        const string script = """
            $ErrorActionPreference = 'Stop'
            [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
            $client = New-Object System.Net.WebClient
            $install = $client.DownloadString('https://community.chocolatey.org/install.ps1')
            Invoke-Expression $install
            """;

        var runner = new PowerShellRunner();
        var result = await runner.RunAsync(script, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        if (!result.Success)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut.Trim() : result.StdErr.Trim();
            return OperationResult.Fail(
                "安装 Chocolatey 失败：" + LastLine(detail) +
                "。可手动执行 `Set-ExecutionPolicy Bypass -Scope Process -Force; iex ((New-Object Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))` 后重试");
        }

        if (!await IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return OperationResult.Fail("安装脚本已执行，但仍未检测到 choco，请重开一次程序或查看日志");
        }

        await EnsureSourceAsync(ct).ConfigureAwait(false);
        InvalidateInstalledCache();
        return OperationResult.Ok($"Chocolatey 安装完成（v{Version}）");
    }

    /// <summary>确保使用官方源 community.chocolatey.org，并禁用其它源，避免从第三方源装包。</summary>
    public async Task<OperationResult> EnsureSourceAsync(CancellationToken ct = default)
    {
        var list = await RunAsync("source list --limit-output", null, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        if (!list.Success)
        {
            return OperationResult.Fail("读取 Chocolatey 源失败：" + LastLine(list.Output));
        }

        var hasOfficial = list.Output.Contains(OfficialSourceUrl, StringComparison.OrdinalIgnoreCase);
        if (hasOfficial)
        {
            return OperationResult.Ok("已使用 Chocolatey 官方源");
        }

        var add = await RunAsync(
            $"source add -n={SourceName} -s={OfficialSourceUrl} --priority=1 --limit-output",
            null, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (add.Success)
        {
            Log.Info($"已添加 Chocolatey 官方源: {OfficialSourceUrl}");
            return OperationResult.Ok("已添加 Chocolatey 官方源");
        }

        return OperationResult.Fail("添加 Chocolatey 官方源失败：" + LastLine(add.Output));
    }

    /// <summary>choco 在非管理员下会弹 20 秒交互确认，必须提前拦截。</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = global::System.Security.Principal.WindowsIdentity.GetCurrent();
            return new global::System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(global::System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>安装单个包（静默、无进度条）。</summary>
    public Task<ChocoResult> InstallAsync(string package, Action<string>? onOutput, CancellationToken ct)
    {
        if (!IsElevated())
        {
            return Task.FromResult(new ChocoResult
            {
                ExitCode = -2,
                Output = "安装软件需要管理员权限（Chocolatey 在非管理员环境下会要求交互确认）"
            });
        }

        var arguments = new List<string>
        {
            "install", package,
            "-y", "--no-progress", "--accept-license"
        };
        return RunAsync(arguments, onOutput, TimeSpan.FromMinutes(20), ct);
    }

    /// <summary>读取本机已安装包（name|version）。</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetInstalledAsync(CancellationToken ct = default)
    {
        if (_installedCache is not null && DateTimeOffset.Now - _installedCacheAt < CacheTtl)
        {
            return _installedCache;
        }

        var result = await RunAsync("list --local-only --limit-output", null, TimeSpan.FromMinutes(2), ct)
            .ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (result.Success)
        {
            foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length < 2) continue;
                map[parts[0].Trim()] = parts[1].Trim();
            }
        }

        _installedCache = map;
        _installedCacheAt = DateTimeOffset.Now;
        return map;
    }

    public void InvalidateInstalledCache()
    {
        _installedCache = null;
        _installedCacheAt = default;
    }

    /// <summary>执行 choco 命令；优先用绝对路径，避免 PATH 未刷新的情况。</summary>
    private async Task<ChocoResult> RunAsync(
        string arguments, Action<string>? onOutput, TimeSpan timeout, CancellationToken ct)
        => await RunAsync(new[] { arguments }, onOutput, timeout, ct).ConfigureAwait(false);

    private async Task<ChocoResult> RunAsync(
        IReadOnlyList<string> arguments, Action<string>? onOutput, TimeSpan timeout, CancellationToken ct)
    {
        var fileName = File.Exists(ChocoExePath) ? ChocoExePath : "choco.exe";
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // 单元素列表是"整串参数"的入口（内部只用于固定命令），其余按参数逐个传入
        if (arguments.Count == 1 && arguments[0].Contains(' '))
        {
            startInfo.Arguments = arguments[0];
        }
        else
        {
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        var gate = new object();
        void Report(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (gate)
            {
                if (output.Length < 200_000) output.AppendLine(line.Trim());
            }
            onOutput?.Invoke(line.Trim());
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"choco 启动失败: {ex.Message}");
            return new ChocoResult { ExitCode = -1, Output = ex.Message };
        }

        var outTask = ReadLinesAsync(process.StandardOutput, Report);
        var errTask = ReadLinesAsync(process.StandardError, Report);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new ChocoResult { ExitCode = -1, Output = output.ToString(), Canceled = true };
        }

        await Task.WhenAny(Task.WhenAll(outTask, errTask), Task.Delay(1000)).ConfigureAwait(false);
        return new ChocoResult { ExitCode = process.ExitCode, Output = output.ToString() };
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            // 同 WingetService：EndOfStream 是同步阻塞读，会卡住调用线程
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null) break;
                onLine(line);
            }
        }
        catch
        {
            // 进程退出时读取可能抛异常
        }
    }

    private static string LastLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "无输出";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0);
        return line ?? "无输出";
    }
}
