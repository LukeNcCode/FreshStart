using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.System;

namespace FreshStart.Services.Driver;

public sealed class DriverInfo
{
    public string Name { get; set; } = string.Empty;
    public string InfName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DateText { get; set; } = string.Empty;

    public string Subtitle => string.Join(" · ",
        new[] { Provider, ClassName, Version, InfName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
}

/// <summary>第三方驱动枚举与导出。枚举用 Get-WindowsDriver（只读），导出用 pnputil /export-driver。</summary>
public sealed class DriverService
{
    private readonly PowerShellRunner _powershell = new();

    public async Task<IReadOnlyList<DriverInfo>> ListAsync(CancellationToken ct = default)
    {
        const string script = """
            $items = @(Get-WindowsDriver -Online -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    Name = [string]$_.Driver
                    Provider = [string]$_.ProviderName
                    ClassName = [string]$_.ClassName
                    Version = [string]$_.Version
                    Date = if ($_.Date) { ([datetime]$_.Date).ToString('yyyy-MM-dd') } else { '' }
                }
            })
            ConvertTo-Json -InputObject $items -Depth 4 -Compress
            """;

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut))
        {
            Log.Warn($"Get-WindowsDriver 失败，尝试 WMI 回退: {result.StdErr}");
            return await ListViaWmiAsync(ct).ConfigureAwait(false);
        }

        var parsed = Parse(result.StdOut);
        if (parsed.Count == 0) return await ListViaWmiAsync(ct).ConfigureAwait(false);

        // Get-WindowsDriver 只给出 oemXX.inf 文件名；用 WMI 的 InfName → DeviceName 映射换成设备名。
        var deviceNames = await GetDeviceNamesByInfAsync(ct).ConfigureAwait(false);
        foreach (var driver in parsed)
        {
            if (string.IsNullOrWhiteSpace(driver.InfName)) driver.InfName = driver.Name;
            if (deviceNames.TryGetValue(driver.InfName, out var names) && names.Count > 0)
            {
                driver.Name = string.Join(" / ", names.Take(2));
            }
            else
            {
                // 没有关联设备的旧驱动包（例如历史版本）：用厂商+类型生成可读名字，oemXX.inf 保留在副标题
                var provider = string.IsNullOrWhiteSpace(driver.Provider) ? "第三方" : driver.Provider;
                var className = string.IsNullOrWhiteSpace(driver.ClassName) ? "设备" : driver.ClassName;
                driver.Name = $"{provider} {className} 驱动包";
            }
        }

        return parsed
            .GroupBy(driver => $"{driver.Name}|{driver.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(driver => driver.ClassName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(driver => driver.Provider, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<Dictionary<string, List<string>>> GetDeviceNamesByInfAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        const string script = """
            $items = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
                Where-Object { $_.InfName -and $_.DeviceName } |
                Select-Object @{n='Inf';e={[string]$_.InfName}}, @{n='Device';e={[string]$_.DeviceName}})
            ConvertTo-Json -InputObject $items -Depth 3 -Compress
            """;
        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return map;

        try
        {
            using var document = JsonDocument.Parse(result.StdOut);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };
            foreach (var item in items)
            {
                if (!item.TryGetProperty("Inf", out var infElement)) continue;
                var inf = infElement.GetString();
                if (string.IsNullOrWhiteSpace(inf)) continue;
                var device = item.TryGetProperty("Device", out var deviceElement)
                    ? deviceElement.GetString()?.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(device)) continue;

                if (!map.TryGetValue(inf, out var names))
                {
                    names = new List<string>();
                    map[inf] = names;
                }
                if (!names.Contains(device, StringComparer.OrdinalIgnoreCase)) names.Add(device);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"解析设备名映射失败: {ex.Message}");
        }
        return map;
    }

    private async Task<IReadOnlyList<DriverInfo>> ListViaWmiAsync(CancellationToken ct)
    {
        const string script = """
            $items = @(Get-CimInstance Win32_PnPSignedDriver -ErrorAction SilentlyContinue |
                Where-Object { $_.DriverProviderName -and $_.DriverProviderName -notlike 'Microsoft*' } |
                ForEach-Object {
                    [pscustomobject]@{
                        Name = [string]$_.DeviceName
                        InfName = [string]$_.InfName
                        Provider = [string]$_.DriverProviderName
                        ClassName = [string]$_.DeviceClass
                        Version = [string]$_.DriverVersion
                        Date = if ($_.DriverDate) { ([datetime]$_.DriverDate).ToString('yyyy-MM-dd') } else { '' }
                    }
                })
            ConvertTo-Json -InputObject $items -Depth 4 -Compress
            """;

        var result = await _powershell.RunAsync(script, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        if (!result.Success || string.IsNullOrWhiteSpace(result.StdOut)) return Array.Empty<DriverInfo>();
        var parsed = Parse(result.StdOut);
        return parsed
            .GroupBy(driver => $"{driver.Name}|{driver.Version}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(driver => driver.ClassName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(driver => driver.Provider, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<DriverInfo> Parse(string json)
    {
        try
        {
            var items = JsonSerializer.Deserialize<List<DriverInfo>>(json, JsonConfig.Options)
                        ?? new List<DriverInfo>();
            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .OrderBy(item => item.ClassName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Provider, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"解析驱动列表失败: {ex.Message}");
            return new List<DriverInfo>();
        }
    }

    public async Task<OperationResult> ExportAsync(string destination, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destination)) return OperationResult.Fail("请选择导出目录");
        try
        {
            Directory.CreateDirectory(destination);
            var startInfo = new ProcessStartInfo("pnputil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("/export-driver");
            startInfo.ArgumentList.Add("*");
            startInfo.ArgumentList.Add(destination);
            using var process = Process.Start(startInfo);
            if (process is null) return OperationResult.Fail("无法启动 pnputil");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return OperationResult.Fail("驱动导出超时或已取消");
            }

            var output = await outputTask.ConfigureAwait(false);
            return process.ExitCode == 0
                ? OperationResult.Ok($"驱动已导出到 {destination}")
                : OperationResult.Fail($"pnputil 失败（exit {process.ExitCode}）：{output.Trim()}");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }
}
