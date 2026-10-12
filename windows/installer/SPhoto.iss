; SPhoto installer, built in CI with Inno Setup 6 (https://jrsoftware.org/isinfo.php,
; an open-source installer tool; not a Microsoft product).
;   iscc /DAppVersion=0.2.0.50 /DSourceDir=publish\windows-x64 /DOutputDir=installer SPhoto.iss
; Installs for the current user only (no administrator prompt), into
; %LOCALAPPDATA%\Programs\SPhoto, like Microsoft's per-user installs.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\windows-x64"
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{6B0F3C2E-5D1A-4E8B-9C3F-2A7D8E1B4C55}
AppName=SPhoto
AppVersion={#AppVersion}
AppVerName=SPhoto {#AppVersion}
AppPublisher=SinEAI
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\SPhoto
DefaultGroupName=SPhoto
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayIcon={app}\SPhoto.exe
UninstallDisplayName=SPhoto
OutputDir={#OutputDir}
OutputBaseFilename=SPhoto-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; An update replaces the whole app folder, so no file of an older build lingers.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\SPhoto"; Filename: "{app}\SPhoto.exe"
Name: "{autodesktop}\SPhoto"; Filename: "{app}\SPhoto.exe"; Tasks: desktopicon

[Run]
; Lists SPhoto under "Open with" and in Default apps; does not take over any type.
Filename: "{app}\SPhoto.exe"; Parameters: "--register"; Flags: runhidden waituntilterminated
Filename: "{app}\SPhoto.exe"; Description: "{cm:LaunchProgram,SPhoto}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\SPhoto.exe"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "Unregister"
