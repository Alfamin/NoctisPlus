; Noctis — Inno Setup Installer Script
; Builds a setup.exe from the publish\win-x64\ output.
; Compile with: ISCC.exe installer.iss

#define MyAppName "Noctis Plus"
; Build-NoctisPlus.ps1 passes /DMyAppVersion=<Noctis.csproj Version>, so the Setup name matches what the updater looks for.
#ifndef MyAppVersion
  #define MyAppVersion "2.0.1"
#endif
#define MyAppPublisher "Alfamin"
#define MyAppExeName "Noctis.exe"
#define MyAppURL "https://github.com/Alfamin/NoctisPlus"
#ifndef PublishDir
  #define PublishDir "publish\win-x64"
#endif

[Setup]
; Not official Noctis's AppId: the two install, update and uninstall separately (ProductIdentity.InstallerAppId).
AppId={{46F93B65-9A61-4601-8CF8-365E785E34A0}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=installer-output
OutputBaseFilename=NoctisPlus-v{#MyAppVersion}-Setup
SetupIconFile=src\Noctis\Assets\Icons\Noctis.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
MinVersion=10.0.17763
; Inno 6.5+ turns on Windows RedirectionGuard for Setup, and on current Windows builds the app
; Setup launches (finish page or the in-app updater's relaunch) inherits it. That session's
; yt-dlp then dies with WinError 448 on any user-made junction in PATH (e.g. scoop's
; apps\*\current), so every YouTube search/download fails until Noctis is restarted. The guard
; protects elevated installers; this one never elevates (lowest), so it buys nothing here.
RedirectionGuard=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Tell Explorer associations changed so the Open-with list refreshes without a re-login.
ChangesAssociations=yes
; One Setup at a time (the in-app updater and scripts\install.ps1 can both start one).
SetupMutex=NoctisPlusSetup
; Uninstalling removes the program only: %APPDATA%\Noctis Plus (library, settings, sign-ins) is kept, like official Noctis.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "fileassoc"; Description: "Add Noctis Plus to &Open with / Default apps for audio files"; GroupDescription: "File types:"

[Registry]
; Per-user (PrivilegesRequired=lowest, so HKA = HKCU). Mirrors Helpers/WindowsFileAssociations.cs,
; which does the same for portable / winget installs from Settings -> General. Windows 10/11
; never let an installer take the default itself; these keys make Noctis a candidate.
Root: HKA; Subkey: "Software\Classes\NoctisPlus.AudioFile"; ValueType: string; ValueName: ""; ValueData: "Audio File"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\NoctisPlus.AudioFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\NoctisPlus.AudioFile\shell\open"; ValueType: string; ValueName: ""; ValueData: "Play with Noctis Plus"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\NoctisPlus.AudioFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Noctis Plus"; Flags: uninsdeletekey; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "Music player"; Tasks: fileassoc
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Noctis Plus"; ValueData: "Software\NoctisPlus\Capabilities"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp3"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flac"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogg"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".oga"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.oga\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4a"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wav"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wma"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wma\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aac"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".opus"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aiff"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aiff\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aif"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aif\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aifc"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.aifc\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ape"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.ape\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wv"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.wv\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".alac"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.alac\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".dsf"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.dsf\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".dff"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.dff\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc
Root: HKA; Subkey: "Software\NoctisPlus\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4b"; ValueData: "NoctisPlus.AudioFile"; Tasks: fileassoc
Root: HKA; Subkey: "Software\Classes\.m4b\OpenWithProgids"; ValueType: none; ValueName: "NoctisPlus.AudioFile"; Flags: uninsdeletevalue; Tasks: fileassoc

[Files]
; Copy everything CI published rather than an allowlist of patterns. The old
; hand-written list (Noctis.exe + *.dll + libvlc\*) silently omitted ffmpeg.exe, which
; CI pins and SHA-256 verifies specifically so users don't need a system install — so
; every winget / Chocolatey / Setup.exe user lost the Audio Converter, the ReplayGain
; scanner and share-clip video export unless they happened to have ffmpeg on PATH.
; A wildcard cannot drift from what CI actually produces.
; *.pdb is excluded: it ships full source paths and internal symbol names to end users,
; and nothing in the app consumes it.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
; Owner #87: songs that were only played (the stream cache) are re-downloadable, not the user's data; they do not stay behind.
Type: filesandordirs; Name: "{localappdata}\Noctis Plus\stream-cache"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// On silent installs (the in-app updater path), Inno skips every [Run] entry
// flagged `postinstall skipifsilent`, so the app would never relaunch after
// "Install & Restart". Launch the new exe ourselves at ssDone in that case.
// /NOLAUNCH=1 (scripts\install.ps1 with NOCTISPLUS_NO_START, CI install checks) installs only.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  // Load the freshly copied audio engine once, hidden (Noctis.exe --warm-audio, ends itself within 30 s), so the
  // first start after an install or update does not wait ~5 s for Windows to open its 325 new plugin files.
  if CurStep = ssPostInstall then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '--warm-audio', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if (CurStep = ssDone) and WizardSilent() and (ExpandConstant('{param:NOLAUNCH|0}') <> '1') then
    Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', '',
         SW_SHOW, ewNoWait, ResultCode);
end;
