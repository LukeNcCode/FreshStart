<#
    FreshStart 发布脚本
    - 默认产物：publish\FreshStart\FreshStart.exe（NativeAOT 启动器）
                publish\FreshStart\FreshStart.Host.exe（WPF 主体，自包含单文件 + R2R）
    - 不裁剪 Host：.NET 8 的 WPF 不支持 trimming
    - 若本机缺少 NativeAOT 工具链，自动回退为自包含单文件启动器
#>
param(
    [string]$OutputDir = "",
    [switch]$SkipLauncherAot,
    [switch]$NoZip,
    [switch]$NoWingetOfflineBundle,
    [string]$SignCertificateSha1 = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $root "publish\FreshStart"
}

Write-Host "== FreshStart publish ==" -ForegroundColor Cyan
Write-Host "输出目录: $OutputDir"

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Push-Location $root
try {
    Write-Host "`n[1/3] 发布 Host（自包含单文件 + ReadyToRun，不裁剪）..." -ForegroundColor Yellow
    # ApplicationManifest 随 DevNoElevation 切换，而单文件 apphost 可能被增量复用，
    # 导致发布版沿用调试清单（asInvoker）。先 clean 再发布，确保写入 requireAdministrator。
    dotnet clean "src\FreshStart.App\FreshStart.App.csproj" -c Release -r win-x64 --nologo | Out-Null
    dotnet publish "src\FreshStart.App\FreshStart.App.csproj" `
        -c Release -r win-x64 -o $OutputDir --nologo `
        -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Host 发布失败（exit $LASTEXITCODE）" }

    Write-Host "`n[2/3] 发布 Launcher（NativeAOT）..." -ForegroundColor Yellow
    $aotOk = $false
    if (-not $SkipLauncherAot) {
        dotnet publish "src\FreshStart.Launcher\FreshStart.Launcher.csproj" `
            -c Release -r win-x64 -o $OutputDir --nologo `
            -p:PublishAot=true -p:DebugType=None
        $aotOk = ($LASTEXITCODE -eq 0)
    }

    if (-not $aotOk) {
        Write-Host "NativeAOT 不可用，回退为自包含单文件启动器" -ForegroundColor DarkYellow
        dotnet publish "src\FreshStart.Launcher\FreshStart.Launcher.csproj" `
            -c Release -r win-x64 -o $OutputDir --nologo `
            -p:PublishAot=false -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=None
        if ($LASTEXITCODE -ne 0) { throw "Launcher 发布失败（exit $LASTEXITCODE）" }
    }

    Write-Host "`n[3/3] 复制文档与内置壁纸..." -ForegroundColor Yellow
    Copy-Item "README.md", "CHANGELOG.md", "SECURITY.md", "LICENSE" $OutputDir -Force
    if (Test-Path "docs") {
        Copy-Item "docs" (Join-Path $OutputDir "docs") -Recurse -Force
    }
    if (Test-Path "assets\wallpapers") {
        $wallOut = Join-Path $OutputDir "assets\wallpapers"
        New-Item -ItemType Directory -Force -Path $wallOut | Out-Null
        Copy-Item "assets\wallpapers\*" $wallOut -Force
        Write-Host "已包含内置壁纸: $wallOut" -ForegroundColor DarkGray
    }

    Get-ChildItem $OutputDir -Filter "*.pdb" -File -ErrorAction SilentlyContinue | Remove-Item -Force

    if (-not [string]::IsNullOrWhiteSpace($SignCertificateSha1)) {
        $signTool = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
        if ($null -eq $signTool) {
            throw "指定了签名证书，但未找到 signtool.exe"
        }

        $signTargets = @(
            (Join-Path $OutputDir "FreshStart.exe"),
            (Join-Path $OutputDir "FreshStart.Host.exe")
        ) | Where-Object { Test-Path $_ }
        & $signTool.Source sign /sha1 $SignCertificateSha1 /fd SHA256 `
            /tr "http://timestamp.digicert.com" /td SHA256 @signTargets
        if ($LASTEXITCODE -ne 0) { throw "Authenticode 签名失败（exit $LASTEXITCODE）" }
        Write-Host "已对启动器与 Host 完成 Authenticode 签名" -ForegroundColor Green
    }
    else {
        Write-Host "未提供代码签名证书；本次产物未签名" -ForegroundColor DarkYellow
    }

    Write-Host "`n构建产物：" -ForegroundColor Green
    Get-ChildItem $OutputDir -File |
        Select-Object Name, @{n = "大小(MB)"; e = { [math]::Round($_.Length / 1MB, 2) } } |
        Format-Table -AutoSize

    if (-not $NoZip) {
        [xml]$buildProps = Get-Content -Raw -Encoding UTF8 (Join-Path $root "Directory.Build.props")
        $version = ($buildProps.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
        if ([string]::IsNullOrWhiteSpace($version)) { $version = "0.0.0" }
        $zipPath = Join-Path $root ("publish\FreshStart-v$version-win-x64.zip")
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
        Compress-Archive -Path (Join-Path $OutputDir "*") -DestinationPath $zipPath -CompressionLevel Optimal
        $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashLine = "$zipHash  $([IO.Path]::GetFileName($zipPath))"
        Set-Content -LiteralPath "$zipPath.sha256" -Value $hashLine -Encoding ascii
        Write-Host "ZIP: $zipPath" -ForegroundColor Green
        Write-Host "SHA256: $hashLine" -ForegroundColor Green

        if (-not $NoWingetOfflineBundle) {
            $wingetManifest = Get-ChildItem "assets\winget" `
                -Filter "Microsoft.DesktopAppInstaller*.msixbundle" -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -eq $wingetManifest) {
                Write-Warning "跳过 winget 离线包：assets\winget 中没有 Microsoft.DesktopAppInstaller*.msixbundle"
            }
            else {
                $offlineStage = Join-Path $root "publish\winget-offline"
                if (Test-Path $offlineStage) {
                    Remove-Item $offlineStage -Recurse -Force
                }
                $offlineAssets = Join-Path $offlineStage "assets\winget"
                New-Item -ItemType Directory -Force -Path $offlineAssets | Out-Null
                Copy-Item "assets\winget\*" $offlineAssets -Force

                $offlineZip = Join-Path $root ("publish\FreshStart-v$version-winget-offline-x64.zip")
                if (Test-Path $offlineZip) {
                    Remove-Item $offlineZip -Force
                }
                Compress-Archive -Path (Join-Path $offlineStage "*") `
                    -DestinationPath $offlineZip -CompressionLevel Optimal
                $offlineHash = (Get-FileHash -LiteralPath $offlineZip -Algorithm SHA256).Hash.ToLowerInvariant()
                $offlineHashLine = "$offlineHash  $([IO.Path]::GetFileName($offlineZip))"
                Set-Content -LiteralPath "$offlineZip.sha256" -Value $offlineHashLine -Encoding ascii
                Write-Host "winget 离线包: $offlineZip" -ForegroundColor Green
                Write-Host "SHA256: $offlineHashLine" -ForegroundColor Green
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Host "`n完成。发布版 Host 使用 app.manifest（requireAdministrator），首次启动会弹一次 UAC。" -ForegroundColor Cyan
