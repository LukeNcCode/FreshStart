namespace FreshStart.App.Startup;

/// <summary>
/// 启动参数。与 Launcher 共用同一套语法：
///   FreshStart.exe --page=system-info
///   FreshStart.exe --apply-preset=gaming --silent
///   FreshStart.exe --smoke-test --smoke-out=D:\temp\smoke.json
/// </summary>
public sealed class StartupOptions
{
    public string[] RawArgs { get; private set; } = Array.Empty<string>();
    public bool SmokeTest { get; private set; }
    public string? SmokeOut { get; private set; }
    public string? Page { get; private set; }
    public string? Theme { get; private set; }
    public string? Preset { get; private set; }
    public bool Silent { get; private set; }

    /// <summary>--execute / --unattended：启动后直接执行方案并退出。</summary>
    public string? UnattendedPath { get; private set; }

    /// <summary>--yes：确认无人值守在无界面模式下自动执行。</summary>
    public bool Yes { get; private set; }

    public static StartupOptions Parse(string[] args)
    {
        var options = new StartupOptions { RawArgs = args ?? Array.Empty<string>() };
        if (args is null) return options;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (arg.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)) options.SmokeTest = true;
            else if (arg.Equals("--silent", StringComparison.OrdinalIgnoreCase)) options.Silent = true;
            else if (arg.Equals("--yes", StringComparison.OrdinalIgnoreCase)
                     || arg.Equals("-y", StringComparison.OrdinalIgnoreCase)) options.Yes = true;
            else if (TryValue(arg, "--page", args, ref i, out var page)) options.Page = page;
            else if (TryValue(arg, "--execute", args, ref i, out var executeJob)) options.UnattendedPath = executeJob;
            else if (TryValue(arg, "--unattended", args, ref i, out var job)) options.UnattendedPath = job;
            else if (TryValue(arg, "--theme", args, ref i, out var theme))
                options.Theme = string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
            else if (TryValue(arg, "--preset", args, ref i, out var preset)) options.Preset = preset;
            else if (TryValue(arg, "--apply-preset", args, ref i, out var applyPreset)) options.Preset = applyPreset;
            else if (TryValue(arg, "--smoke-out", args, ref i, out var smokeOut)) options.SmokeOut = smokeOut;
        }

        return options;
    }

    private static bool TryValue(string arg, string name, string[] args, ref int index, out string value)
    {
        value = string.Empty;
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
        {
            value = arg[(name.Length + 1)..].Trim('"');
            return value.Length > 0;
        }

        if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
        {
            value = args[index + 1].Trim('"');
            index++;
            return true;
        }

        return false;
    }
}
