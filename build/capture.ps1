param(
    [string]$Page = "home",
    [string]$Theme = "Light",
    [string]$Out = "",
    [int]$Wait = 2500,
    [string]$Exe = "",
    [switch]$Intro
)

$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($Out)) {
    $Out = Join-Path $root "shots\shot.png"
}
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $root "src\FreshStart.App\bin\Debug\net8.0-windows\win-x64\FreshStart.Host.exe"
}

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FsCapture {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
[FsCapture]::SetProcessDPIAware() | Out-Null

$exePath = $Exe
Get-Process FreshStart.Host -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200

$arguments = @("--page=$Page", "--theme=$Theme")
if ($Intro) { $arguments += "--intro" }
$p = Start-Process -FilePath $exePath -ArgumentList $arguments -PassThru
Start-Sleep -Milliseconds $Wait
$p.Refresh()
$hwnd = $p.MainWindowHandle
if ($hwnd -eq 0) { Write-Error "window not found"; exit 1 }

[FsCapture]::ShowWindow($hwnd, 9) | Out-Null
[FsCapture]::SetWindowPos($hwnd, [IntPtr](-1), 0, 0, 0, 0, 0x0043) | Out-Null
[FsCapture]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 700

$rect = New-Object FsCapture+RECT
[FsCapture]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, [System.Drawing.Size]::new($w, $h))
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
[FsCapture]::SetWindowPos($hwnd, [IntPtr](-2), 0, 0, 0, 0, 0x0043) | Out-Null
Stop-Process -Id $p.Id -Force
Write-Output "saved $Out ($w x $h)"
