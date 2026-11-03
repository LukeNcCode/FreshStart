using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace FreshStart.Launcher;

/// <summary>
/// 极轻量启动器（asInvoker）：
/// 1) 已有 Host 在运行 → 通过命名管道转发参数并立即退出（热启动 &lt;300ms）；
/// 2) 未运行 → 启动 FreshStart.Host.exe（清单触发一次 UAC）。
/// 本进程不加载 WPF、不读配置、不做任何系统修改。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var suffix = Environment.GetEnvironmentVariable("FRESHSTART_INSTANCE_SUFFIX");
        var suffixPart = string.IsNullOrWhiteSpace(suffix)
            ? string.Empty
            : "." + new string(suffix.Where(char.IsLetterOrDigit).Take(32).ToArray());
        var versionPart = GetInstanceVersionPart();
        var pipeName = $"FreshStart.Activate.{sessionId}.{versionPart}{suffixPart}";

        if (TryActivateExisting(pipeName, args))
        {
            return 0;
        }

        var hostPath = FindHostExecutable();
        if (hostPath is null)
        {
            WriteError("未找到 FreshStart.Host.exe，请确认程序目录完整。");
            return 3;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = QuoteArguments(args),
                WorkingDirectory = Path.GetDirectoryName(hostPath)!,
                UseShellExecute = true
            };
            Process.Start(startInfo);
            return 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户在 UAC 中点了"否"
            return 1;
        }
        catch (Exception ex)
        {
            WriteError($"启动失败：{ex.Message}");
            return 4;
        }
    }

    private static bool TryActivateExisting(string pipeName, string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(250);

            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            writer.WriteLine("activate");
            var forwarded = args.Length > 64 ? args[..64] : args;
            writer.WriteLine(forwarded.Length.ToString());
            foreach (var arg in forwarded)
            {
                writer.WriteLine(arg.ReplaceLineEndings(" "));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindHostExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("FRESHSTART_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(baseDir, "FreshStart.Host.exe"),
            Path.Combine(baseDir, @"..\FreshStart.App\bin\Debug\net8.0-windows\FreshStart.Host.exe"),
            Path.Combine(baseDir, @"..\FreshStart.App\bin\Release\net8.0-windows\win-x64\FreshStart.Host.exe"),
            Path.Combine(baseDir, @"..\..\src\FreshStart.App\bin\Debug\net8.0-windows\FreshStart.Host.exe"),
            Path.Combine(baseDir, @"..\..\src\FreshStart.App\bin\Release\net8.0-windows\win-x64\FreshStart.Host.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string QuoteArguments(string[] args)
        => string.Join(' ', args.Select(Quote));

    private static string Quote(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (!arg.Any(ch => char.IsWhiteSpace(ch) || ch == '"')) return arg;
        return "\"" + arg.Replace("\"", "\\\"") + "\"";
    }

    private static string GetInstanceVersionPart()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var text = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (Version.TryParse(text, out var version))
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }
            }
        }
        catch { /* ignore */ }

        return "dev";
    }

    private static void WriteError(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "freshstart-launcher.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }
}
