; Inno Setup script — per-user setup EXE (no admin prompt), Start-menu shortcut, clean uninstall.
; Compiled by the release workflow:  iscc /DVersion=x.y.z /DArch=x64 /DPublishDir=..\publish\win-x64 installer\SemanticPortrait.iss

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish\win-" + Arch
#endif

[Setup]
AppId={{e270eee0-2834-4243-8f28-4d2fc3071ce9}
AppName=SemanticPortrait
AppVersion={#Version}
AppPublisher=Earl Cameron
AppPublisherURL=https://github.com/monstercameron/SemanticPortrait
AppSupportURL=https://github.com/monstercameron/SemanticPortrait/issues
DefaultDirName={localappdata}\Programs\SemanticPortrait
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=SemanticPortrait-{#Version}-{#Arch}-setup
OutputDir=..\dist
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\SemanticPortrait.App.exe
LicenseFile=..\LICENSE
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\SemanticPortrait"; Filename: "{app}\SemanticPortrait.App.exe"
Name: "{userdesktop}\SemanticPortrait"; Filename: "{app}\SemanticPortrait.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\SemanticPortrait.App.exe"; Description: "Launch SemanticPortrait"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Nothing to run — user data (encrypted vault) intentionally survives uninstall in %LOCALAPPDATA%.
