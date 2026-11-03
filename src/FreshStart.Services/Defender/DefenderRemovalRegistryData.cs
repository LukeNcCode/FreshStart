namespace FreshStart.Services.Defender;

public enum DefenderRegistryValueKind
{
    String,
    Dword,
    Qword,
    Binary,
    Delete
}

public sealed record DefenderRegistryValue(string Name, DefenderRegistryValueKind Kind, string Data);
public sealed record DefenderRegistryKey(string Path, bool Delete, DefenderRegistryValue[] Values);

/// <summary>
/// 从 windows-defender-remover 13 的 Remove_Defender / Remove_SecurityComp .reg 清单生成。
/// 运行时由 SYSTEM 计划任务逐条执行 reg.exe add/delete，避免随包携带 .reg 文件。
/// </summary>
public static class DefenderRemovalRegistryData
{
    public static IReadOnlyList<DefenderRegistryKey> Keys { get; } = new DefenderRegistryKey[]
    {
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\WindowsMitigation", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("UserPreference", DefenderRegistryValueKind.Dword, "2"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("MitigationAuditOptions", DefenderRegistryValueKind.Binary, "00,00,00,00,00,00,20,22,00,00,00,00,00,00,00,20,00,00,00,00,00,00,00,00"),
            new DefenderRegistryValue("MitigationOptions", DefenderRegistryValueKind.Binary, "00,22,22,20,22,20,22,22,20,00,00,00,00,20,00,20,00,00,00,00,00,00,00,00"),
            new DefenderRegistryValue("KernelSEHOPEnabled", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\SCMConfig", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("EnableSvchostMitigationPolicy", DefenderRegistryValueKind.Qword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Defender\\Features", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("MpPlatformKillbitsFromEngine", DefenderRegistryValueKind.Binary, "00,00,00,00,00,00,00,00"),
            new DefenderRegistryValue("TamperProtectionSource", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("MpCapability", DefenderRegistryValueKind.Binary, "00,00,00,00,00,00,00,00"),
            new DefenderRegistryValue("TamperProtection", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("RunAsPPL", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Lsa", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("LsaConfigFlags", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("RunAsPPL", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("RunAsPPLBoot", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LmCompatibilityLevel", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\CI\\Config", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("VulnerableDriverBlocklistEnable", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\SOFTWARE\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppContainer\\Storage\\microsoft.microsoftedge_8wekyb3d8bbwe\\MicrosoftEdge\\PhishingFilter", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("EnabledV9", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("PreventOverride", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Microsoft\\Edge", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("SmartScreenEnabled", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Microsoft\\Edge\\SmartScreenEnabled", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("@", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("SmartScreenEnabled", DefenderRegistryValueKind.String, "off"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\System", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("EnableSmartScreen", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("ShellSmartScreenLevel", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\Browser\\AllowSmartScreen", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\SmartScreen\\EnableSmartScreenInShell", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\SmartScreen\\EnableAppInstallControl", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\SmartScreen\\PreventOverrideForFilesInShell", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\AppHost", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("EnableWebContentEvaluation", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("PreventOverride", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\SmartScreen", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("ConfigureAppInstallControlEnabled", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("ConfigureAppInstallControl", DefenderRegistryValueKind.String, "Anywhere"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("DisableRoutinelyTakingAction", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("ServiceKeepAlive", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("AllowFastServiceStartup", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("DisableLocalAdminMerge", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Real-Time Protection", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("LocalSettingOverrideDisableOnAccessProtection", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LocalSettingOverrideRealtimeScanDirection", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LocalSettingOverrideDisableIOAVProtection", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LocalSettingOverrideDisableBehaviorMonitoring", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LocalSettingOverrideDisableIntrusionPreventionSystem", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LocalSettingOverrideDisableRealtimeMonitoring", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("DisableIOAVProtection", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableRealtimeMonitoring", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableBehaviorMonitoring", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableOnAccessProtection", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableScanOnRealtimeEnable", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("RealtimeScanDirection", DefenderRegistryValueKind.Dword, "2"),
            new DefenderRegistryValue("DisableInformationProtectionControl", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableIntrusionPreventionSystem", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableRawWriteNotification", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\Defender\\AllowBehaviorMonitoring", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Policies\\Microsoft\\Windows Defender", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("DisableRoutinelyTakingAction", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Spynet", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("DisableBlockAtFirstSeen", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("LocalSettingOverrideSpynetReporting", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("SpynetReporting", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("SubmitSamplesConsent", DefenderRegistryValueKind.Dword, "2"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Microsoft Antimalware\\SpyNet", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("SpyNetReporting", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("LocalSettingOverrideSpyNetReporting", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\RemovalTools\\MpGears", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("HeartbeatTrackingIndex", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("SpyNetReportingLocation", DefenderRegistryValueKind.String, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WindowsDefenderSecurityCenter\\DisableEnhancedNotifications", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WindowsDefenderSecurityCenter\\DisableNotifications", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WindowsDefenderSecurityCenter\\HideWindowsSecurityNotificationAreaControl", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Security Center", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Security Center", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("FirstRunDisabled", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("AntiVirusOverride", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("FirewallOverride", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender Security Center\\Notifications", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("DisableEnhancedNotifications", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("DisableNotifications", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Notifications\\Settings\\Windows.SystemToast.SecurityAndMaintenance", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Enabled", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("EnableVirtualizationBasedSecurity", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("RequirePlatformSecurityFeatures", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Enabled", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\CredentialGuard", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Enabled", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\SystemGuard", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Enabled", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Lsa", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("LsaCfgFlags", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{2781761E-28E0-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{2781761E-28E2-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{195B4D07-3DE2-4744-BBF2-D90121AE785B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{361290c0-cb1b-49ae-9f3e-ba1cbe5dab35}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{45F2C32F-ED16-4C94-8493-D72EF93A051B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{6CED0DAA-4CDE-49C9-BA3A-AE163DC3D7AF}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{8a696d12-576b-422e-9712-01b9dd84b446}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{8C9C0DB7-2CBA-40F1-AFE0-C55740DD91A0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{A2D75874-6750-4931-94C1-C99D3BC9D0C7}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{A7C452EF-8E9F-42EB-9F2B-245613CA0DC9}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{DACA056E-216A-4FD1-84A6-C306A017ECEC}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{E3C9166D-1D39-4D4E-A45D-BC7BE9B00578}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\WOW6432Node\\CLSID\\{F6976CF5-68A8-436C-975A-40BE53616D59}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{2781761E-28E0-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{2781761E-28E2-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{195B4D07-3DE2-4744-BBF2-D90121AE785B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{361290c0-cb1b-49ae-9f3e-ba1cbe5dab35}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{45F2C32F-ED16-4C94-8493-D72EF93A051B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{6CED0DAA-4CDE-49C9-BA3A-AE163DC3D7AF}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{8a696d12-576b-422e-9712-01b9dd84b446}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{8C9C0DB7-2CBA-40F1-AFE0-C55740DD91A0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{A2D75874-6750-4931-94C1-C99D3BC9D0C7}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{A7C452EF-8E9F-42EB-9F2B-245613CA0DC9}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{DACA056E-216A-4FD1-84A6-C306A017ECEC}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{E3C9166D-1D39-4D4E-A45D-BC7BE9B00578}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\Software\\Classes\\CLSID\\{F6976CF5-68A8-436C-975A-40BE53616D59}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{2781761E-28E0-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{2781761E-28E2-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{195B4D07-3DE2-4744-BBF2-D90121AE785B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{361290c0-cb1b-49ae-9f3e-ba1cbe5dab35}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{45F2C32F-ED16-4C94-8493-D72EF93A051B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{6CED0DAA-4CDE-49C9-BA3A-AE163DC3D7AF}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{8a696d12-576b-422e-9712-01b9dd84b446}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{8C9C0DB7-2CBA-40F1-AFE0-C55740DD91A0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{A2D75874-6750-4931-94C1-C99D3BC9D0C7}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{A7C452EF-8E9F-42EB-9F2B-245613CA0DC9}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{DACA056E-216A-4FD1-84A6-C306A017ECEC}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{E3C9166D-1D39-4D4E-A45D-BC7BE9B00578}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{F6976CF5-68A8-436C-975A-40BE53616D59}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{2781761E-28E0-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{2781761E-28E2-4109-99FE-B9D127C57AFE}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{195B4D07-3DE2-4744-BBF2-D90121AE785B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{361290c0-cb1b-49ae-9f3e-ba1cbe5dab35}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{45F2C32F-ED16-4C94-8493-D72EF93A051B}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{6CED0DAA-4CDE-49C9-BA3A-AE163DC3D7AF}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{8a696d12-576b-422e-9712-01b9dd84b446}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{8C9C0DB7-2CBA-40F1-AFE0-C55740DD91A0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{A2D75874-6750-4931-94C1-C99D3BC9D0C7}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{A7C452EF-8E9F-42EB-9F2B-245613CA0DC9}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{DACA056E-216A-4FD1-84A6-C306A017ECEC}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{E3C9166D-1D39-4D4E-A45D-BC7BE9B00578}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{F6976CF5-68A8-436C-975A-40BE53616D59}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\WMI\\Autologger\\DefenderAuditLogger", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\WMI\\Autologger\\DefenderApiLogger", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Security Health", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows Security Health", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows Security Health\\State", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Disabled", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Security Health\\Platform", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Registered", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\CLSID\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\WOW6432Node\\CLSID\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ControlPanel\\NameSpace\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Classes\\CLSID\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ControlPanel\\NameSpace\\{BB64F8A7-BEE7-4E1A-AB8D-7D8273F7FDB6}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\SecurityHealthService", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\TaskCache\\Tasks\\{0ACC9108-2000-46C0-8407-5FD9F89521E8}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\TaskCache\\Tasks\\{1D77BCC8-1D07-42D0-8C89-3A98674DFB6F}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\TaskCache\\Tasks\\{4A9233DB-A7D3-45D6-B476-8C7D8DF73EB5}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Schedule\\TaskCache\\Tasks\\{B05F34EE-83F2-413D-BC1D-7D5BD6E98300}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellServiceObjects\\{900c0763-5cad-4a34-bc1f-40cd513679d5}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\ShellServiceObjects\\{900c0763-5cad-4a34-bc1f-40cd513679d5}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows Defender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\Folder\\shell\\WindowsDefender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\DesktopBackground\\Shell\\WindowsSecurity", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\Folder\\shell\\WindowsDefender\\Command", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\MsSecCore", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\wscsvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WdNisDrv", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WdNisSvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WdFilter", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WdBoot", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\SgrmAgent", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\SgrmBroker", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WinDefend", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender Security Center\\App and Browser protection", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("DisallowExploitProtectionOverride", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\MsSecFlt", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\MsSecWfp", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\whesvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\WindowsRuntime\\Server\\WebThreatDefSvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\webthreatdefsvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\webthreatdefusersvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Svchost\\WebThreatDefense", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\PlutonHsp2", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\PlutonHeci", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\Hsp", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\WinDefend", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\windowsdefender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\AppUserModelId\\Windows.Defender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\AppUserModelId\\Microsoft.Windows.Defender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\AppX9kvz3rdv8t7twanaezbwfcdgrbg3bck0", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Classes\\ms-cxh", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\Local Settings\\MrtCache\\C:%5CWindows%5CSystemApps%5CMicrosoft.Windows.AppRep.ChxApp_cw5n1h2txyewy%5Cresources.pri", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WindowsDefender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\Software\\Classes\\AppX9kvz3rdv8t7twanaezbwfcdgrbg3bck0", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\WindowsDefender", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Ubpm", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("CriticalMaintenance_DefenderCleanup", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("CriticalMaintenance_DefenderVerification", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess\\Parameters\\FirewallPolicy\\RestrictedServices\\Static\\System", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("WindowsDefender-1", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("WindowsDefender-2", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("WindowsDefender-3", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows Defender\\Signature Updates", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("SignatureDisableNotification", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("RealtimeSignatureDelivery", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("ForceUpdateFromMU", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("DisableScheduledSignatureUpdateOnBattery", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("UpdateOnStartUp", DefenderRegistryValueKind.Dword, "0"),
            new DefenderRegistryValue("SignatureUpdateCatchupInterval", DefenderRegistryValueKind.Dword, "2"),
            new DefenderRegistryValue("DisableUpdateOnStartupWithoutEngine", DefenderRegistryValueKind.Dword, "1"),
            new DefenderRegistryValue("ScheduleTime", DefenderRegistryValueKind.Dword, "5184"),
            new DefenderRegistryValue("DisableScanOnUpdate", DefenderRegistryValueKind.Dword, "1"),
        }),
        new DefenderRegistryKey("HKEY_CURRENT_USER\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Windows Defender", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("SecurityHealth", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("Windows Defender", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("SecurityHealth", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("WindowsDefender", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("SecurityHealth", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\CLSID\\{E48B2549-D510-4A76-8A5F-FC126A6215F0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_CLASSES_ROOT\\WOW6432Node\\CLSID\\{E48B2549-D510-4A76-8A5F-FC126A6215F0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\CLSID\\{E48B2549-D510-4A76-8A5F-FC126A6215F0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes\\WOW6432Node\\CLSID\\{E48B2549-D510-4A76-8A5F-FC126A6215F0}", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\WindowsRuntime\\ActivatableClassId\\Microsoft.OneCore.WebThreatDefense.Service.UserSessionServiceManager", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\WindowsRuntime\\ActivatableClassId\\Microsoft.OneCore.WebThreatDefense.ThreatExperienceManager.ThreatExperienceManager", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\WindowsRuntime\\ActivatableClassId\\Microsoft.OneCore.WebThreatDefense.ThreatResponseEngine.ThreatDecisionEngine", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\WindowsRuntime\\ActivatableClassId\\Microsoft.OneCore.WebThreatDefense.Configuration.WTDUserSettings", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense\\AuditMode", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense\\NotifyUnsafeOrReusedPassword", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense\\ServiceEnabled", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\webthreatdefsvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\webthreatdefusersvc", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Svchost\\WebThreatDefense", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Svchost", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("WebThreatDefense", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess\\Parameters\\FirewallPolicy\\RestrictedServices\\Static\\System", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("WebThreatDefSvc_Allow_In", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("WebThreatDefSvc_Allow_Out", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("WebThreatDefSvc_Block_In", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("WebThreatDefSvc_Block_Out", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\SharedAccess\\Parameters\\FirewallPolicy\\RestrictedServices\\Configurable\\System", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("{2A5FE97D-01A4-4A9C-8241-BB3755B65EE0}", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("72e33e44-dc4c-40c5-a688-a77b6e988c69", DefenderRegistryValueKind.Delete, ""),
            new DefenderRegistryValue("b23879b5-1ef3-45b7-8933-554a4303d2f3", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Svchost", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("WebThreatDefense", DefenderRegistryValueKind.Delete, ""),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense\\AuditMode", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense\\NotifyUnsafeOrReusedPassword", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\PolicyManager\\default\\WebThreatDefense\\ServiceEnabled", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("value", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WTDS", Delete: true, new DefenderRegistryValue[]
        {
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WTDS\\Components", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("NotifyPasswordReuse", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Policies\\Microsoft\\Windows\\WTDS\\Components", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("NotifyMalicious", DefenderRegistryValueKind.Dword, "0"),
        }),
        new DefenderRegistryKey("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\Explorer", Delete: false, new DefenderRegistryValue[]
        {
            new DefenderRegistryValue("SettingsPageVisibility", DefenderRegistryValueKind.String, "hide:windowsdefender;"),
        }),
    };
}
