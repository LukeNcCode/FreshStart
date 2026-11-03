using System.Runtime.InteropServices;
using FreshStart.Core;
using FreshStart.Core.Abstractions;
using Microsoft.Win32;

namespace FreshStart.Services.System;

/// <summary>
/// 系统还原点。通过 srclient.dll 创建，必须在提权进程内调用。
/// 注意：Windows 默认 24 小时只允许创建一个还原点，失败时由上层降级为 JSON 快照。
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    private const int BeginSystemChange = 100;
    private const int EndSystemChange = 101;
    private const int ApplicationInstall = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StateManagerStatus
    {
        public uint Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo restorePoint, out StateManagerStatus status);

    public bool SystemProtectionEnabled
    {
        get
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");
                if (key?.GetValue("DisableSR") is int disabled && disabled == 1) return false;

                using var policyBase = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var policy = policyBase.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore");
                if (policy?.GetValue("DisableSR") is int policyDisabled && policyDisabled == 1) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public async Task<RestorePointResult> CreateAsync(string description, CancellationToken ct = default)
    {
        // SRSetRestorePoint 是同步阻塞调用，可能持续数秒，必须离开 UI 线程
        return await Task.Run(() => CreateCore(description), ct).ConfigureAwait(false);
    }

    private static RestorePointResult CreateCore(string description)
    {
        var info = new RestorePointInfo
        {
            EventType = BeginSystemChange,
            RestorePointType = ApplicationInstall,
            SequenceNumber = 0,
            Description = description.Length > 200 ? description[..200] : description
        };

        try
        {
            if (!SRSetRestorePointW(ref info, out var status))
            {
                var error = (uint)Marshal.GetLastWin32Error();
                var message = error switch
                {
                    0x80070422u => "系统还原服务未启用",
                    0x80070005u => "权限不足，需要管理员权限",
                    0x80042306u or 0x8004230Fu => "系统保护已关闭，或距上次还原点不足 24 小时",
                    _ => $"创建失败（Win32 0x{error:X8}）"
                };
                Log.Warn($"系统还原点创建失败: {message}");
                return new RestorePointResult { Success = false, Message = message };
            }

            var end = new RestorePointInfo
            {
                EventType = EndSystemChange,
                RestorePointType = ApplicationInstall,
                SequenceNumber = status.SequenceNumber,
                Description = info.Description
            };
            SRSetRestorePointW(ref end, out _);

            Log.Info($"系统还原点已创建，序号 {status.SequenceNumber}");
            return new RestorePointResult { Success = true, Message = $"系统还原点已创建（序号 {status.SequenceNumber}）" };
        }
        catch (Exception ex)
        {
            Log.Error("系统还原点创建异常", ex);
            return new RestorePointResult { Success = false, Message = $"异常：{ex.Message}" };
        }
    }
}
