using System.Management;
using FreshStart.Core;

namespace FreshStart.Services.System;

/// <summary>
/// WMI 查询封装。WMI 是冷启动最大的敌人：
/// 只在后台线程调用、每个查询都有超时、结果必须缓存，绝不在启动路径出现。
/// </summary>
public static class WmiQuery
{
    public static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> AllAsync(
        string wql,
        TimeSpan timeout,
        CancellationToken ct,
        string scope = @"root\cimv2")
    {
        try
        {
            var task = Task.Run(() => Execute(wql, timeout, scope), ct);
            return await task.WaitAsync(timeout + TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Log.Warn($"WMI 查询超时: {wql}");
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"WMI 查询失败: {wql} - {ex.Message}");
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }
    }

    public static async Task<IReadOnlyDictionary<string, object?>?> FirstAsync(
        string wql, TimeSpan timeout, CancellationToken ct, string scope = @"root\cimv2")
    {
        var all = await AllAsync(wql, timeout, ct, scope).ConfigureAwait(false);
        return all.Count > 0 ? all[0] : null;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Execute(string wql, TimeSpan timeout, string scope)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using var searcher = new ManagementObjectSearcher(scope, wql);
        searcher.Options.Timeout = timeout;
        searcher.Options.ReturnImmediately = true;
        using var results = searcher.Get();
        foreach (ManagementBaseObject item in results)
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in item.Properties)
            {
                try { row[property.Name] = property.Value; }
                catch { row[property.Name] = null; }
            }
            rows.Add(row);
        }
        return rows;
    }

    public static string? Str(this IReadOnlyDictionary<string, object?>? row, string key)
        => row is not null && row.TryGetValue(key, out var value) ? value?.ToString() : null;

    public static ulong U64(this IReadOnlyDictionary<string, object?>? row, string key)
    {
        var text = row.Str(key);
        return ulong.TryParse(text, out var value) ? value : 0;
    }

    public static int I32(this IReadOnlyDictionary<string, object?>? row, string key)
    {
        var text = row.Str(key);
        return int.TryParse(text, out var value) ? value : 0;
    }
}
