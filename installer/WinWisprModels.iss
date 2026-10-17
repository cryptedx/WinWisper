; WinWispr Models: the speech model, the translation model and the offline voices that Setup and Portable no
; longer contain. It installs them into an existing WinWispr folder, where the app reads them next to
; WinWispr.exe, and its own uninstaller removes exactly these files. eng/Build-ModelPackage.ps1 builds it.
#ifndef SourceDir
  #define SourceDir "artifacts\models\source"
#endif
#ifndef OutputDir
  #define OutputDir "artifacts\models"
#endif
#ifndef PackageVersion
  #define PackageVersion "1"
#endif
; The AppId of WinWispr's own Setup (installer\WinWispr.iss); its uninstall entry names the installation folder.
#define WinWisprUninstallKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{A2F3C9F1-51C4-4D9E-9C4A-7D7DDAA1D2A4}_is1"

[Setup]
AppId={{6B1D0E52-8C3F-4E7A-9A41-2F5C7D9E3B18}
AppName=WinWispr Models
AppVersion={#PackageVersion}
VersionInfoVersion={#PackageVersion}.0.0.0
AppVerName=WinWispr Models {#PackageVersion}
AppPublisher=WinWispr
DefaultDirName={code:WinWisprDirectory}
DirExistsWarning=no
AppendDefaultDirName=no
DisableProgramGroupPage=yes
CreateAppDir=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=no
UsePreviousAppDir=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\WinWispr
CloseApplications=yes
OutputDir="{#OutputDir}"
OutputBaseFilename=WinWispr-Models-v{#PackageVersion}
Compression=lzma2/fast
SolidCompression=no
DiskSpanning=no
WizardStyle=modern
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName=WinWispr Models
UninstallFilesDir={app}\ModelsUninstall
#ifdef Sign
SignTool=winwispr
SignedUninstaller=yes
#endif

[Files]
Source: "{#SourceDir}\ModelData\model.tar.bz2"; DestDir: "{app}\ModelData"; Flags: ignoreversion nocompression
Source: "{#SourceDir}\TranslationData\*"; DestDir: "{app}\TranslationData"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\VoiceData\*"; DestDir: "{app}\VoiceData"; Flags: ignoreversion recursesubdirs createallsubdirs

[UninstallDelete]
Type: dirifempty; Name: "{app}\ModelData"
Type: dirifempty; Name: "{app}\TranslationData"
Type: dirifempty; Name: "{app}\VoiceData"

[Code]
function RegisteredWinWisprDirectory(var Directory: String): Boolean;
begin
  Result :=
    RegQueryStringValue(HKCU, '{#WinWisprUninstallKey}', 'InstallLocation', Directory) or
    RegQueryStringValue(HKLM, '{#WinWisprUninstallKey}', 'InstallLocation', Directory);
  if Result then
    Directory := RemoveBackslashUnlessRoot(Directory);
end;

function WinWisprDirectory(Param: String): String;
begin
  if not RegisteredWinWisprDirectory(Result) then
  begin
    if IsAdminInstallMode then
      Result := ExpandConstant('{autopf}\WinWispr')
    else
      Result := ExpandConstant('{localappdata}\Programs\WinWispr');
  end;
end;

function HasWinWispr(Directory: String): Boolean;
begin
  Result := FileExists(AddBackslash(Directory) + 'WinWispr.exe');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and not HasWinWispr(WizardDirValue) then
  begin
    MsgBox('WinWispr.exe was not found in this folder.' + #13#10 +
      'Choose the folder where WinWispr is installed or unpacked.', mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not HasWinWispr(WizardDirValue) then
    Result := 'WinWispr.exe was not found in ' + WizardDirValue +
      '. Install WinWispr first, or choose the folder of a portable copy.';
end;
