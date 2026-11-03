using System.Diagnostics;
using System.Text;

namespace FreshStart.Services.System;

public sealed class SystemProcessResult
{
    public bool Started { get; init; }
    public bool UsedSystemToken { get; init; }
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// 以 LocalSystem 身份执行批处理脚本。通过一次性的 SYSTEM 计划任务运行，
/// 不依赖 PsExec / PowerRun 等第三方工具；失败时回退到当前管理员进程执行。
/// </summary>
public static class SystemProcessRunner
{
    public static async Task<SystemProcessResult> RunScriptAsync(
        string scriptPath,
        string logPath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (!File.Exists(scriptPath))
        {
            return new SystemProcessResult { Error = $"脚本不存在：{scriptPath}" };
        }

        // 包装脚本与完成标记放到 ACL 收紧的临时目录，避免被同机其他用户替换后提权执行
        var runDir = SecureWorkDirectory.Create("run");
        var log = string.IsNullOrWhiteSpace(logPath) ? Path.Combine(runDir, "run.log") : logPath;
        var donePath = Path.Combine(runDir, "run.done");
        var wrapperPath = Path.Combine(runDir, "run-wrapper.cmd");
        TryDelete(log);
        TryDelete(donePath);

        // 包一层，保证脚本执行完一定会留下完成标记（即使脚本内部有 exit）
        var wrapper = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine($"call \"{scriptPath}\"")
            .AppendLine($"echo DONE>\"{donePath}\"")
            .ToString();
        File.WriteAllText(wrapperPath, wrapper, Encoding.Default);

        var taskName = "FreshStartSys" + Guid.NewGuid().ToString("N")[..10];
        var created = false;

        try
        {
            var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var create = Run("schtasks.exe", new[]
            {
                "/create", "/tn", taskName,
                "/tr", $"\"{cmdExe}\" /c \"{wrapperPath}\"",
                "/sc", "once", "/st", "00:00",
                "/ru", "SYSTEM", "/rl", "HIGHEST", "/f"
            }, TimeSpan.FromSeconds(30));

            if (create.ExitCode != 0)
            {
                return await RunAsAdminAsync(wrapperPath, log, timeout, ct,
                    $"创建 SYSTEM 计划任务失败：{LastLine(create.Output)}").ConfigureAwait(false);
            }

            created = true;
            var run = Run("schtasks.exe", new[] { "/run", "/tn", taskName }, TimeSpan.FromSeconds(30));
            if (run.ExitCode != 0)
            {
                return await RunAsAdminAsync(wrapperPath, log, timeout, ct,
                    $"启动 SYSTEM 计划任务失败：{LastLine(run.Output)}").ConfigureAwait(false);
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(donePath)) break;
                await Task.Delay(500, ct).ConfigureAwait(false);
            }

            var output = ReadLog(log);
            if (!File.Exists(donePath))
            {
                return new SystemProcessResult
                {
                    Started = true,
                    UsedSystemToken = true,
                    ExitCode = -1,
                    Output = output,
                    Error = "SYSTEM 任务超时，已保留日志"
                };
            }

            return new SystemProcessResult
            {
                Started = true,
                UsedSystemToken = true,
                ExitCode = 0,
                Output = output,
                Error = string.Empty
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await RunAsAdminAsync(wrapperPath, log, timeout, ct, ex.Message).ConfigureAwait(false);
        }
        finally
        {
            if (created)
            {
                Run("schtasks.exe", new[] { "/delete", "/tn", taskName, "/f" }, TimeSpan.FromSeconds(20));
            }

            SecureWorkDirectory.TryDelete(runDir);
        }
    }

    private static async Task<SystemProcessResult> RunAsAdminAsync(
        string wrapperPath, string logPath, TimeSpan timeout, CancellationToken ct, string reason)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = $"/c \"{wrapperPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            if (process is null)
            {
                return new SystemProcessResult { Error = "无法启动 cmd.exe；" + reason };
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new SystemProcessResult
                {
                    Started = true,
                    ExitCode = -1,
                    Output = ReadLog(logPath),
                    Error = "执行超时；" + reason
                };
            }

            return new SystemProcessResult
            {
                Started = true,
                UsedSystemToken = false,
                ExitCode = process.ExitCode,
                Output = ReadLog(logPath),
                Error = reason
            };
        }
        catch (Exception ex)
        {
            return new SystemProcessResult { Error = ex.Message + "；" + reason };
        }
    }

    private static (int ExitCode, string Output) Run(string fileName, string[] arguments, TimeSpan timeout)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null) return (-1, "无法启动进程");

            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (-1, "命令超时");
            }

            return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string ReadLog(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }

    private static string LastLine(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .LastOrDefault(value => value.Length > 0);
        return line ?? "无输出";
    }
}
