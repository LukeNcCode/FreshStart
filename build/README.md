# Build scripts

| Script | Purpose |
|---|---|
| `publish.ps1` | Build Host and launcher, assemble the release directory and ZIP |
| `installer.iss` | Inno Setup definition for lightweight and offline setup packages |
| `make-icon.ps1` | Generate the multi-resolution application icon |
| `capture.ps1` | Capture one WPF page with a non-elevated audit build |
| `test-ui.ps1` | Exercise theme, sidebar and window controls |
| `test-dialog.ps1` | Exercise confirmation/restore-point flow |
| `test-history.ps1` | Capture operation history |
| `test-open.ps1` | Verify system-tool launch actions |
All scripts resolve paths relative to the repository root and write generated
files under ignored directories (`publish/` or `shots/`). Generating
`setup.exe` requires Inno Setup 6.
