param(
    [string]$Page = "settings",
    [string]$Theme = "Light"
)

$root = Split-Path $PSScriptRoot -Parent
$shotDir = Join-Path $root "shots"
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FsDialog {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
[FsDialog]::SetProcessDPIAware() | Out-Null

function Save-WindowShot([IntPtr]$hwnd, [string]$path) {
    $rect = New-Object FsDialog+RECT
    [FsDialog]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [FsDialog]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
}

function Find-Button([System.Windows.Automation.AutomationElement]$root, [string]$name) {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $typeCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $cond = New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

$exe = Join-Path $root "src\FreshStart.App\bin\Debug\net8.0-windows\win-x64\FreshStart.Host.exe"
Get-Process FreshStart.Host -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$p = Start-Process -FilePath $exe -ArgumentList "--page=$Page", "--theme=$Theme" -PassThru
Start-Sleep -Milliseconds 2600
$p.Refresh()
$hwnd = $p.MainWindowHandle

$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if ($null -eq $win) { Write-Error "window not found"; Stop-Process -Id $p.Id -Force; exit 1 }

$selfTest = Find-Button $win "运行自检"
if ($null -eq $selfTest) { Write-Error "self-test button not found"; Stop-Process -Id $p.Id -Force; exit 1 }
$selfTest.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1200
Save-WindowShot $hwnd (Join-Path $shotDir "dialog-confirm.png")

$skip = Find-Button $win "仅本次不创建"
if ($null -eq $skip) { Write-Error "restore-point skip button not found"; Stop-Process -Id $p.Id -Force; exit 1 }
$skip.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 1800
Save-WindowShot $hwnd (Join-Path $shotDir "dialog-result.png")

Start-Sleep -Milliseconds 500
Stop-Process -Id $p.Id -Force

$backupRoot = Join-Path $env:APPDATA "FreshStart\backup"
$history = Join-Path $env:APPDATA "FreshStart\history\operations.jsonl"
Write-Output "backup dirs:"
Get-ChildItem $backupRoot -Directory -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3 Name
Write-Output "history tail:"
if (Test-Path $history) { Get-Content $history -Tail 2 }
