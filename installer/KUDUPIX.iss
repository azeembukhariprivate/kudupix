; KUDUPIX installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights.

#ifndef MyAppVersion
  #define MyAppVersion "1.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDUPIX"
#endif

#define MyAppName "KUDUPIX"
#define MyAppExe "KUDUPIX.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kudupix"

[Setup]
AppId={{318224FF-15F5-410A-B85E-8302F663155E}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=KUDUPIX-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDUPIX\Assets\KUDUPIX.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log,screenshots\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDUPIX now"; Flags: nowait postinstall skipifsilent

[Registry]
; Remove saved settings when uninstalling.
Root: HKCU; Subkey: "Software\KUDUPIX"; Flags: uninsdeletekey dontcreatekey

[Code]
{ KUDUPIX's own files (the library list, stars, tags, albums, fixes, small pictures and the originals kept before
  "Save over the original") are only removed when the person says so. Their photos and videos are never touched. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if DirExists(ExpandConstant('{localappdata}\KUDUPIX')) then
      if SuppressibleMsgBox('Also remove KUDUPIX''s own files from this PC: your stars, tags, albums and fixes, the small pictures, and the originals kept before "Save over the original"?' + #13#10#13#10 +
        'Your photos and videos are kept either way. Choose No to keep these for if you install KUDUPIX again.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(ExpandConstant('{localappdata}\KUDUPIX'), True, True, True);
end;
