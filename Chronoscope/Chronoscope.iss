; Same AppId as the TimeViewer installers (the MAUI app, then this one before the rename), so it
; replaces an existing install and takes over its Apps & features entry. The app moves the old
; data folder itself on first start (FileSystem.cs), so tags and rules carry over.
; Build first:  dotnet publish -c Release -r win-x64 --self-contained

[Setup]
AppId={{bd30d463-63bd-4623-a1a0-7da3305e8e14}
AppName=Chronoscope
AppVersion=1.0
AppPublisher=Mohamed Matar
DefaultDirName={autopf}\Chronoscope
DefaultGroupName=Chronoscope
; An upgrade would otherwise reuse the old "TimeViewer" folder and Start menu group
UsePreviousAppDir=no
UsePreviousGroup=no
SetupIconFile=Assets\appicon.ico
OutputDir=installer
OutputBaseFilename=ChronoscopeSetup
Compression=lzma
SolidCompression=yes
WizardStyle=modern
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start Chronoscope when Windows starts"; GroupDescription: "Startup:"
Name: "autostart\minimized"; Description: "Start minimized"

; What an installed TimeViewer leaves behind: its program folder and shortcuts. The data folder
; is not touched here - the app moves it.
[InstallDelete]
Type: filesandordirs; Name: "{autopf}\TimeViewer"
Type: filesandordirs; Name: "{autoprograms}\TimeViewer"
Type: files; Name: "{autodesktop}\TimeViewer.lnk"

[Files]
Source: "bin\Release\net10.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The same entry the app writes from Settings (StartupService.cs): the Run value is what Task
; Manager lists under Startup apps, StartupApproved is the switch it flips (02 = enabled).
[Registry]
; TimeViewer's startup entry would launch an exe that is gone
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TimeViewer"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "TimeViewer"; Flags: deletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Chronoscope"; ValueData: """{app}\Chronoscope.exe"""; Tasks: autostart and not autostart\minimized
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Chronoscope"; ValueData: """{app}\Chronoscope.exe"" --minimized"; Tasks: autostart\minimized
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "Chronoscope"; ValueData: "02 00 00 00 00 00 00 00 00 00 00 00"; Tasks: autostart

[Icons]
Name: "{group}\Chronoscope"; Filename: "{app}\Chronoscope.exe"; IconFilename: "{app}\Chronoscope.exe"
Name: "{autodesktop}\Chronoscope"; Filename: "{app}\Chronoscope.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Chronoscope.exe"; Description: "{cm:LaunchProgram,Chronoscope}"; Flags: nowait postinstall skipifsilent

[Code]
// Not uninsdeletevalue on the entries above: the app can create them from Settings without the
// installer task ever having run, and those must not outlive the uninstall either.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Chronoscope');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'Chronoscope');
  end;
end;
