; EQDeeps installer (ADR-010).
;
; Built by scripts/publish.ps1 and by the release workflow, which pass the
; version and the published payload directory in:
;
;   ISCC /DAppVersion=0.4.0 /DPayloadDir=..\artifacts\win-x64 installer\EQDeeps.iss
;
; Defaults to a per-user install under %LocalAppData%\Programs\EQDeeps, which
; needs no administrator rights and — crucially — stays writable, so the
; in-app updater can replace it without a UAC prompt. Users who want a
; machine-wide install can still choose one on the first wizard page; EQDeeps
; detects that case and waits for an explicit click before updating, rather
; than raising a consent dialog behind their back (see UpdateInstaller).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\artifacts\win-x64"
#endif

#define AppName "EQDeeps"
#define AppExeName "EQDeeps.Server.exe"
#define AppPublisher "Austin Culbertson"
#define AppUrl "https://github.com/Moonchopper/EQDeeps"

[Setup]
; Never change AppId: it is what lets an update find the existing install and
; land in the directory the user originally chose.
AppId={{6F1D9C4E-4E2B-4C71-9E3A-4D5B0C7A18E2}
AppName={#AppName}
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases

; Per-user by default (no UAC, self-updatable); the dialog still offers
; "for all users" to anyone who wants it.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableProgramGroupPage=yes
; The directory page is the point of shipping a wizard — leave it on.
DisableDirPage=no

OutputDir=..\artifacts\installer
OutputBaseFilename=EQDeeps-Setup-{#AppVersion}
SetupIconFile=..\src\EQDeeps.Server\Assets\eqdeeps.ico
UninstallDisplayIcon={app}\{#AppExeName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; WebView2 (the app shell) requires Windows 10 or newer, and EQDeeps ships x64.
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Silent update installs run while EQDeeps may still be shutting down; let the
; restart manager close it rather than failing on a locked file.
CloseApplications=yes
RestartApplications=no

LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\NOTICE"; DestDir: "{app}"; DestName: "NOTICE.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; skipifsilent keeps update installs from launching a second copy — the
; updater's handoff script decides whether EQDeeps comes back up.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Parsed sessions, preferences and the MRU live in %AppData%\EQDeeps and are
; deliberately left alone — uninstalling should not throw away someone's data.
Type: files; Name: "{app}\update-install.log"
