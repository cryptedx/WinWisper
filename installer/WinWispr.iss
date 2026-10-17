#ifndef SourceDir
  #define SourceDir "artifacts\installer\source"
#endif
#ifndef OutputDir
  #define OutputDir "artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{A2F3C9F1-51C4-4D9E-9C4A-7D7DDAA1D2A4}
AppName=WinWispr
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=WinWispr
DefaultDirName={autopf}\WinWispr
DefaultGroupName=WinWispr
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousPrivileges=no
UsePreviousAppDir=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=Local\WinWispr
OutputDir="{#OutputDir}"
OutputBaseFilename=WinWispr-Setup-v{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName=WinWispr
UninstallDisplayIcon={app}\Assets\WinWisprIcon.ico
#ifdef Sign
SignTool=winwispr
SignedUninstaller=yes
#endif

[Files]
; The models come from the separate WinWispr Models installer (installer\WinWisprModels.iss); an update leaves
; them in place.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\WinWispr"; Filename: "{app}\WinWispr.exe"; IconFilename: "{app}\Assets\WinWisprIcon.ico"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\WinWispr"; Check: ShouldDeleteAppData
Type: dirifempty; Name: "{app}"

[Code]
var
  LocationPage: TInputOptionWizardPage;
  DeleteAppData: Boolean;

function ShouldDeleteAppData: Boolean;
begin
  Result := DeleteAppData;
end;

function InitializeUninstall: Boolean;
begin
  DeleteAppData := False;
  if not UninstallSilent then
    DeleteAppData := MsgBox(
      'Also delete settings, models, and technical logs?' + #13#10 +
      'This data cannot be recovered.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ModelsUninstaller: String;
  ResultCode: Integer;
begin
  // Remove the WinWispr Models first, so that its uninstall entry does not outlive the folder.
  ModelsUninstaller := ExpandConstant('{app}\ModelsUninstall\unins000.exe');
  if (CurUninstallStep = usUninstall) and FileExists(ModelsUninstaller) then
    Exec(ModelsUninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if CurUninstallStep = usPostUninstall then
    DelTree(ExpandConstant('{app}'), True, False, True);
end;

function StandardInstallDir: String;
begin
  if IsAdminInstallMode then
    Result := ExpandConstant('{autopf}\WinWispr')
  else
    Result := ExpandConstant('{localappdata}\Programs\WinWispr');
end;

procedure InitializeWizard;
begin
  LocationPage := CreateInputOptionPage(wpSelectDir,
    'Installation location', 'Where should WinWispr be installed?',
    'Choose the destination folder. The default folder follows the installation mode selected earlier.',
    True, False);
  LocationPage.Add('In the current folder (' + ExpandConstant('{src}') + ')');
  LocationPage.Add('Use the default folder (' + StandardInstallDir + ')');
  LocationPage.SelectedValueIndex := 1;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = LocationPage.ID) and (not WizardSilent) then
  begin
    if LocationPage.SelectedValueIndex = 0 then
      WizardForm.DirEdit.Text := ExpandConstant('{src}')
    else
      WizardForm.DirEdit.Text := StandardInstallDir;
  end;
end;
