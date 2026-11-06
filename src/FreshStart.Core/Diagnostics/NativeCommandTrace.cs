namespace FreshStart.Core.Diagnostics;

public sealed class NativeCommandTraceEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public string Command { get; init; } = string.Empty;
    public string WorkingDirectory { get; init; } = string.Empty;
    public int? ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;
    public double DurationMs { get; init; }
    public bool Completed { get; init; }
}

/// <summary>
/// 原生进程命令总线。App 层只负责展示，不解释或翻译命令。
/// </summary>
public static class NativeCommandTrace
{
    public static event EventHandler<NativeCommandTraceEvent>? Published;

    public static void Started(string command, string workingDirectory = "")
    {
        Publish(new NativeCommandTraceEvent
        {
            Command = command,
            WorkingDirectory = workingDirectory
        });
    }

    public static void Completed(
        string command,
        int exitCode,
        string output,
        TimeSpan duration,
        string workingDirectory = "")
    {
        Publish(new NativeCommandTraceEvent
        {
            Command = command,
            WorkingDirectory = workingDirectory,
            ExitCode = exitCode,
            Output = output,
            DurationMs = duration.TotalMilliseconds,
            Completed = true
        });
    }

    public static string Format(string fileName, IEnumerable<string> arguments)
    {
        static string Quote(string value)
            => value.Length > 0 && !value.Any(char.IsWhiteSpace)
                ? value
                : $"\"{value.Replace("\"", "\\\"")}\"";
        return string.Join(' ', new[] { Quote(fileName) }
            .Concat(arguments.Select(Quote)));
    }

    private static void Publish(NativeCommandTraceEvent activity)
    {
        try { Published?.Invoke(null, activity); }
        catch
        {
            // Diagnostics must never affect the command being executed.
        }
    }
}
