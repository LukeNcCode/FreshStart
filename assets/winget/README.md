# winget bootstrap assets

This directory is only needed to build the optional offline add-on for clean or
network-restricted Windows 11 installations. FreshStart starts without winget,
and the software page can install or update App Installer online on demand.

These files are intentionally excluded from Git because several exceed GitHub's
100 MB per-file limit:

```text
Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle
Microsoft.VCLibs.140.00_14.0.33519.0_x64.appx
Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64.appx
windowsappruntimeinstall-x64.exe
```

Obtain the App Installer bundle and compatible dependencies from the official
[microsoft/winget-cli releases](https://github.com/microsoft/winget-cli/releases).
The Windows App Runtime installer can be obtained from:

```text
https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe
```

Place the files in this directory before running `build/publish.ps1`. The script
keeps them out of the main package and creates
`FreshStart-v<version>-winget-offline-x64.zip` separately.

To use the add-on offline:

1. Extract it into the FreshStart application directory.
2. Open **推荐软件**.
3. Click **导入离线包** and select the extracted App Installer bundle.

Verify the publisher and Authenticode signature before packaging any Microsoft
binary. Do not commit these files to Git or Git LFS.
