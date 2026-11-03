# Development

## Prerequisites

- Windows 11 22H2 or later
- .NET SDK `8.0.426` (`global.json` uses `latestFeature`)
- Visual Studio 2022 C++ Build Tools for NativeAOT release builds
- Windows SDK for WPF, AppX and native tooling
- Inno Setup 6 for producing `setup.exe`

## Build

```powershell
dotnet restore FreshStart.sln
dotnet build FreshStart.sln -c Release
```

The production Host uses `app.manifest` and requests administrator rights. For
UI development, build an audit copy with `app.dev.manifest`:

```powershell
dotnet build src\FreshStart.App\FreshStart.App.csproj `
  -c Release -p:DevNoElevation=true -p:BaseOutputPath=bin\UiAudit\

src\FreshStart.App\bin\UiAudit\Release\net8.0-windows\win-x64\FreshStart.Host.exe `
  --page=home --theme=Light
```

Use a unique `FRESHSTART_INSTANCE_SUFFIX` when running multiple audit instances:

```powershell
$env:FRESHSTART_INSTANCE_SUFFIX = "ui-audit"
```

## Visual regression

The repository includes PowerShell capture helpers under `build/`. They use
`PrintWindow` against a non-elevated development build, allowing visual checks
without opening a visible test harness.

For UI changes, verify:

- light and dark themes;
- standard and minimum logical window sizes;
- expanded and collapsed navigation;
- placeholder and typed text;
- modal and toast overlays;
- no content behind the title bar or action bars.

## Release bundle

The release directory contains:

```text
FreshStart.exe       NativeAOT launcher
FreshStart.Host.exe  Self-contained WPF host
README.md
LICENSE
docs/
assets/wallpapers/
```

The main release never embeds the Microsoft winget bootstrap payload. Prepare
the files described in [assets/winget/README.md](../assets/winget/README.md),
then run:

```powershell
powershell -ExecutionPolicy Bypass -File build\publish.ps1
```

The script writes three artifacts when winget assets are available:

- `FreshStart-v<version>-win-x64.zip` — lightweight application package;
- `FreshStart-v<version>-win-x64-offline.zip` — application plus winget;
- `FreshStart-v<version>-setup.exe` — Inno Setup installer with the complete offline package.

Each artifact has a matching SHA256 file under `publish/`. Code signing is
optional and controlled by `-SignCertificateSha1`; unsigned artifacts may show
SmartScreen warnings.

Do not commit `publish/` to Git. GitHub source repositories limit individual
files to 100 MB, but GitHub Releases support assets up to 2 GB. Run the
`release` workflow manually after pushing the source, or upload the generated
ZIP through the GitHub Releases page. The workflow creates the main package
without winget; upload the complete offline ZIP and `setup.exe` separately
after building them on a machine that has the optional Microsoft assets and
Inno Setup installed.

## Command-line smoke test

```powershell
FreshStart.exe --smoke-test --smoke-out="$env:TEMP\freshstart-smoke.json"
```

The smoke test writes startup timing and memory data after the first frame, then
exits. It is intended for regression checks on a Windows host.

## Versioning

Update the version once in [Directory.Build.props](../Directory.Build.props),
then add a release entry to [CHANGELOG.md](../CHANGELOG.md). Packaging derives the
version from `Directory.Build.props`.
