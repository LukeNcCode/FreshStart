param(
    [string]$Version = "",
    [switch]$SkipOfflineInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -Raw -Encoding UTF8 (Join-Path $root "Directory.Build.props")
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}

$hostExe = Join-Path $root "publish\FreshStart\FreshStart.Host.exe"
$launcherExe = Join-Path $root "publish\FreshStart\FreshStart.exe"
if (-not (Test-Path -LiteralPath $launcherExe) -or -not (Test-Path -LiteralPath $hostExe)) {
    throw "The unpacked publish directory is incomplete."
}
foreach ($path in @($launcherExe, $hostExe)) {
    $fileVersion = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    if (-not $fileVersion.StartsWith("$Version.", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Version mismatch: $path (file version $fileVersion)"
    }
}

$setupNames = @("FreshStart-v$Version-setup.exe")
if (-not $SkipOfflineInstaller) {
    $setupNames += "FreshStart-v$Version-offline-setup.exe"
}

foreach ($setupName in $setupNames) {
    $setupPath = Join-Path $root "publish\$setupName"
    if (-not (Test-Path -LiteralPath $setupPath)) {
        throw "Missing setup installer: $setupName"
    }

    $hashPath = "$setupPath.sha256"
    if (-not (Test-Path -LiteralPath $hashPath)) {
        throw "Missing setup checksum file: $hashPath"
    }

    $expected = ((Get-Content -Raw -LiteralPath $hashPath).Trim() -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Setup SHA256 mismatch for ${setupName}: expected $expected, got $actual"
    }

    $sizeMb = [math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
    Write-Host "Verified $setupName ($sizeMb MB)" -ForegroundColor Green
    Write-Host "SHA256: $actual" -ForegroundColor Green
}
