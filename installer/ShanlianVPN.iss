#define AppVersion "1.0.0"
#define RepoRoot ".."

#ifdef CustomRepoRoot
#undef RepoRoot
#define RepoRoot CustomRepoRoot
#endif

[Setup]
AppId={{1F8E132D-F289-4F67-90BD-4D8D95D6C9F5}
AppName=闪连 VPN
AppVersion={#AppVersion}
AppVerName=闪连 VPN {#AppVersion}
AppPublisher=Shanlian VPN
DefaultDirName={autopf}\Shanlian VPN
DefaultGroupName=闪连 VPN
OutputDir={#RepoRoot}\installer\output
OutputBaseFilename=闪连VPN-{#AppVersion}-setup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
UninstallDisplayName=闪连 VPN

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#RepoRoot}\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\闪连 VPN"; Filename: "{app}\闪连VPN.exe"
Name: "{autodesktop}\闪连 VPN"; Filename: "{app}\闪连VPN.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加图标："; Flags: checkedonce

[Run]
Filename: "{app}\闪连VPN.exe"; Description: "启动闪连 VPN"; Flags: nowait postinstall skipifsilent

