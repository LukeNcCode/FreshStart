param(
    [string]$Page = "home",
    [string]$Theme = "Light",
    [string]$Out = "",
    [int]$Wait = 2500,
    [string]$Exe = ""
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
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
  [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
[FsCapture]::SetProcessDPIAware() | Out-Null

$exePath = $Exe
Get-Process FreshStart.Host -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 200

$p = Start-Process -FilePath $exePath -ArgumentList "--page=$Page", "--theme=$Theme" -PassThru
Start-Sleep -Milliseconds $Wait
$p.Refresh()
$hwnd = $p.MainWindowHandle
if ($hwnd -eq 0) { Write-Error "window not found"; exit 1 }

$rect = New-Object FsCapture+RECT
[FsCapture]::DwmGetWindowAttribute($hwnd, 9, [ref]$rect, 16) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[FsCapture]::PrintWindow($hwnd, $hdc, 2) | Out-Null
$g.ReleaseHdc($hdc)
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Stop-Process -Id $p.Id -Force
Write-Output "saved $Out ($w x $h)"
