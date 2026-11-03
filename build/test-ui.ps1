Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FsUiTest {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
[FsUiTest]::SetProcessDPIAware() | Out-Null

function Save-WindowShot([IntPtr]$hwnd, [string]$path) {
    $rect = New-Object FsUiTest+RECT
    [FsUiTest]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [FsUiTest]::PrintWindow($hwnd, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()
    Write-Output "saved $path"
}

function Find-Button($root, [string]$name) {
    $nameCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $typeCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition($nameCond, $typeCond)))
}

function Invoke-Button($root, [string]$name) {
    $button = Find-Button $root $name
    if ($null -eq $button) { Write-Output "button not found: $name"; return $false }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root "src\FreshStart.App\bin\Debug\net8.0-windows\win-x64"
$shotDir = Join-Path $root "shots"
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
Get-Process FreshStart.Host -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$p = Start-Process -FilePath (Join-Path $dir "FreshStart.Host.exe") `
    -ArgumentList "--page=home", "--theme=Light" -PassThru
Start-Sleep -Milliseconds 2600

$p.Refresh()
$hwnd = $p.MainWindowHandle
$pidCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $p.Id)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
if ($null -eq $win) { Write-Error "window not found"; Stop-Process -Id $p.Id -Force; exit 1 }

Save-WindowShot $hwnd (Join-Path $shotDir "ui-01-light.png")

if (Invoke-Button $win "切换主题") {
    Start-Sleep -Milliseconds 700
    Save-WindowShot $hwnd (Join-Path $shotDir "ui-02-dark.png")
}

if (Invoke-Button $win "收起或展开侧边栏") {
    Start-Sleep -Milliseconds 700
    Save-WindowShot $hwnd (Join-Path $shotDir "ui-03-collapsed.png")
}

if (Invoke-Button $win "最大化") {
    Start-Sleep -Milliseconds 900
    Save-WindowShot $hwnd (Join-Path $shotDir "ui-04-maximized.png")
}

Start-Sleep -Milliseconds 500
Stop-Process -Id $p.Id -Force
