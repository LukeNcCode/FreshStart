param(
    [string]$Version = "",
    [string]$ZipPath = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content -Raw -Encoding UTF8 (Join-Path $root "Directory.Build.props")
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}

if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $root "publish\FreshStart-v$Version-win-x64.zip"
}

$resolvedZip = (Resolve-Path -LiteralPath $ZipPath).Path
$hashPath = "$resolvedZip.sha256"
if (-not (Test-Path -LiteralPath $hashPath)) {
    throw "Missing checksum file: $hashPath"
}

$expected = ((Get-Content -Raw -LiteralPath $hashPath).Trim() -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $resolvedZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) {
    throw "SHA256 mismatch: expected $expected, got $actual"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($resolvedZip)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    $required = @(
        "FreshStart.exe",
        "FreshStart.Host.exe",
        "README.md",
        "CHANGELOG.md",
        "SECURITY.md",
        "LICENSE",
        "docs/OVERVIEW.md",
        "docs/FEATURES.md",
        "assets/wallpapers/clouds.jpg"
    )
    $missing = @($required | Where-Object { $_ -notin $names })
    if ($missing.Count -gt 0) {
        throw "Release archive is incomplete. Missing: $($missing -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

$hostExe = Join-Path $root "publish\FreshStart\FreshStart.Host.exe"
$launcherExe = Join-Path $root "publish\FreshStart\FreshStart.exe"
if ((Test-Path -LiteralPath $launcherExe) -and (Test-Path -LiteralPath $hostExe)) {
    foreach ($path in @($launcherExe, $hostExe)) {
        $fileVersion = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
        if (-not $fileVersion.StartsWith("$Version.", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Version mismatch: $path (file version $fileVersion)"
        }
    }
}
else {
    Write-Warning "Side-by-side release directory not present; executable version checks were skipped."
}

$sizeMb = [math]::Round((Get-Item -LiteralPath $resolvedZip).Length / 1MB, 2)
Write-Host "Verified FreshStart v$Version ($sizeMb MB)" -ForegroundColor Green
Write-Host "SHA256: $actual" -ForegroundColor Green

$offlineZip = Join-Path $root "publish\FreshStart-v$Version-win-x64-offline.zip"
if (Test-Path -LiteralPath $offlineZip) {
    $offlineHashPath = "$offlineZip.sha256"
    if (-not (Test-Path -LiteralPath $offlineHashPath)) {
        throw "Missing offline checksum file: $offlineHashPath"
    }
    $offlineExpected = ((Get-Content -Raw -LiteralPath $offlineHashPath).Trim() -split '\s+')[0].ToLowerInvariant()
    $offlineActual = (Get-FileHash -LiteralPath $offlineZip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($offlineActual -ne $offlineExpected) {
        throw "Offline winget SHA256 mismatch: expected $offlineExpected, got $offlineActual"
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $offlineArchive = [System.IO.Compression.ZipFile]::OpenRead($offlineZip)
    try {
        $offlineNames = @($offlineArchive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
        $offlineRequired = @(
            "FreshStart.exe",
            "FreshStart.Host.exe",
            "assets/winget/Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle"
        )
        $offlineMissing = @($offlineRequired | Where-Object { $_ -notin $offlineNames })
        if ($offlineMissing.Count -gt 0) {
            throw "Complete offline archive is incomplete. Missing: $($offlineMissing -join ', ')"
        }
    }
    finally {
        $offlineArchive.Dispose()
    }
    Write-Host "Verified complete offline archive" -ForegroundColor Green
    Write-Host "SHA256: $offlineActual" -ForegroundColor Green
}
else {
    Write-Warning "Complete offline archive not present; skipping optional bundle verification."
}

$setupPath = Join-Path $root "publish\FreshStart-v$Version-setup.exe"
if (Test-Path -LiteralPath $setupPath) {
    $setupHashPath = "$setupPath.sha256"
    if (-not (Test-Path -LiteralPath $setupHashPath)) {
        throw "Missing setup checksum file: $setupHashPath"
    }
    $setupExpected = ((Get-Content -Raw -LiteralPath $setupHashPath).Trim() -split '\s+')[0].ToLowerInvariant()
    $setupActual = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($setupActual -ne $setupExpected) {
        throw "Setup SHA256 mismatch: expected $setupExpected, got $setupActual"
    }
    Write-Host "Verified setup installer" -ForegroundColor Green
    Write-Host "SHA256: $setupActual" -ForegroundColor Green
}
else {
    Write-Warning "Setup installer not present; skipping installer verification."
}
