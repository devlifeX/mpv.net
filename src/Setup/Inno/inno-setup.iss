
#define MyAppName "mpv.net"
#define MyAppExeName "mpvnet.exe"
#ifndef MyAppSourceDir
  #define MyAppSourceDir "..\..\MpvNet.Windows\bin\Debug"
#endif
#define MyAppVersion GetFileVersion(AddBackslash(MyAppSourceDir) + MyAppExeName)

[Setup]
AppId={{9AA2B100-BEF3-44D0-B819-D8FC3C4D557D}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Frank Skare (stax76)
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
Compression=lzma2
DefaultDirName={autopf}\{#MyAppName}
OutputBaseFilename=mpv.net-v{#MyAppVersion}-setup-x64
OutputDir=E:\Desktop
DefaultGroupName={#MyAppName}
SetupIconFile=..\..\MpvNet.Windows\mpv-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Files]
Source: "{#MyAppSourceDir}\*"; DestDir: "{app}"; Excludes: "win-x64,win-arm64"; Flags: ignoreversion recursesubdirs createallsubdirs;
