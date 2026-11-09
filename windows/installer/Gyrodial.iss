; The Gyrodial installer (Inno Setup 6): a per-user install that needs no admin rights, with a
; Start menu shortcut and an uninstaller. The release workflow builds it from the published app:
;
;   iscc /DAppVersion=0.3.0 /DSourceDir=<dist\Gyrodial> /DOutputDir=<folder> windows\installer\Gyrodial.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\dist\Gyrodial"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\dist"
#endif

[Setup]
; Never change AppId: it's how Windows knows a new version replaces the installed one
AppId={{6E0F7C2A-9B41-4D7E-A8C3-2F5B1D9E4A60}
AppName=Gyrodial
AppVersion={#AppVersion}
AppVerName=Gyrodial {#AppVersion}
AppPublisher=Gyrodial
AppPublisherURL=https://github.com/tank104/gyrodial
AppSupportURL=https://github.com/tank104/gyrodial/issues
AppUpdatesURL=https://github.com/tank104/gyrodial/releases
DefaultDirName={localappdata}\Programs\Gyrodial
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=Gyrodial-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Gyrodial.exe
UninstallDisplayName=Gyrodial
; Asks to close a running Gyrodial before installing over it or uninstalling it
AppMutex=Local\Gyrodial.App.SingleInstance
CloseApplications=yes
RestartApplications=no

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Files from an older version that this one no longer ships
Type: filesandordirs; Name: "{app}\firmware"

[Icons]
Name: "{autoprograms}\Gyrodial"; Filename: "{app}\Gyrodial.exe"

[Run]
Filename: "{app}\Gyrodial.exe"; Description: "Start Gyrodial"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes the Start with Windows entry and the Claude Code hook, which point into this folder
Filename: "{app}\Gyrodial.exe"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveHookAndAutoStart"
