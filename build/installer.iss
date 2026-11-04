#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#ifndef MyFileVersion
  #define MyFileVersion "0.1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\FreshStart"
#endif
#ifndef WingetDir
  #define WingetDir "..\assets\winget"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish"
#endif
#ifndef IconPath
  #define IconPath "..\src\FreshStart.App\Assets\app.ico"
#endif
#ifndef OutputBaseName
  #define OutputBaseName "FreshStart-v" + MyAppVersion + "-setup"
#endif
#ifndef IncludeWinget
  #define IncludeWinget "0"
#endif

#define MyAppName "FreshStart"
#define MyAppPublisher "LukeNcCode"
#define MyAppExeName "FreshStart.exe"

[Setup]
AppId={{5E52F0A5-0B2F-4A90-A651-E9FA73719B7C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyFileVersion}
DefaultDirName={autopf}\FreshStart
DefaultGroupName=FreshStart
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseName}
SetupIconFile={#IconPath}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
CloseApplicationsFilter=FreshStart.exe,FreshStart.Host.exe
RestartApplications=no
LicenseFile=..\LICENSE
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#if IncludeWinget == "1"
Source: "{#WingetDir}\*"; DestDir: "{app}\assets\winget"; Flags: ignoreversion recursesubdirs createallsubdirs
#endif

[InstallDelete]
#if IncludeWinget == "0"
Type: filesandordirs; Name: "{app}\assets\winget"
#endif

[Icons]
Name: "{autoprograms}\FreshStart"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\FreshStart"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch FreshStart"; Flags: nowait postinstall skipifsilent
