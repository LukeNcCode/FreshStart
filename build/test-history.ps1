Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$shotDir = Join-Path $root "shots"
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FsHistoryShot {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
[FsHistoryShot]::SetProcessDPIAware() | Out-Null

function Save-Shot([IntPtr]$hwnd, [string]$path) {
    $rect = New-Object FsHistoryShot+RECT
    [FsHistoryShot]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16) | Out-Null
    $w = $rect.Right - $rect.Left; $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [FsHistoryShot]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Find-Button($root, [string]$name) {
    $n = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $t = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition($n, $t)))
}

$exe = Join-Path $root "src\FreshStart.App\bin\Debug\net8.0-windows\win-x64\FreshStart.Host.exe"
Get-Process FreshStart.Host -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'FreshStart' } | ForEach-Object {
    # 不强制结束提权进程；测试实例使用独立 suffix，下面的启动不会冲突
}
$env:FRESHSTART_INSTANCE_SUFFIX = "historytest"
$env:FRESHSTART_DATA_ROOT = Join-Path $env:TEMP "FreshStart-history-test"
$p = Start-Process -FilePath $exe -ArgumentList "--page=windows-update" -PassThru
Start-Sleep -Milliseconds 3000
$p.Refresh()
$hwnd = $p.MainWindowHandle
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidc = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidc)
if ($null -eq $win) { Write-Error "window missing"; exit 1 }

$btn = Find-Button $win "加载历史"
"load button found: $($null -ne $btn)"
if ($btn) { $btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
Start-Sleep -Seconds 8
$p.Refresh(); $hwnd = $p.MainWindowHandle
Save-Shot $hwnd (Join-Path $shotDir "windows-update-history.png")
Start-Sleep -Milliseconds 300
Stop-Process -Id $p.Id -Force
