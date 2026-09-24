; Same AppId as the old MAUI installer, so it upgrades an existing install in place - and the
; app reads the same data folder, so tags and rules carry over.
; Build first:  dotnet publish -c Release -r win-x64 --self-contained

[Setup]
AppId={{bd30d463-63bd-4623-a1a0-7da3305e8e14}
AppName=TimeViewer
AppVersion=1.0
AppPublisher=Mohamed Matar
DefaultDirName={autopf}\TimeViewer
DefaultGroupName=TimeViewer
SetupIconFile=Assets\appicon.ico
OutputDir=installer
OutputBaseFilename=TimeViewerSetup
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
Name: "autostart"; Description: "Start TimeViewer when Windows starts"; GroupDescription: "Startup:"
Name: "autostart\minimized"; Description: "Start minimized"

[Files]
Source: "bin\Release\net10.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; The same entry the app writes from Settings (StartupService.cs): the Run value is what Task
; Manager lists under Startup apps, StartupApproved is the switch it flips (02 = enabled).
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TimeViewer"; ValueData: """{app}\TimeViewer.exe"""; Tasks: autostart and not autostart\minimized
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TimeViewer"; ValueData: """{app}\TimeViewer.exe"" --minimized"; Tasks: autostart\minimized
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: binary; ValueName: "TimeViewer"; ValueData: "02 00 00 00 00 00 00 00 00 00 00 00"; Tasks: autostart

[Icons]
Name: "{group}\TimeViewer"; Filename: "{app}\TimeViewer.exe"; IconFilename: "{app}\TimeViewer.exe"
Name: "{autodesktop}\TimeViewer"; Filename: "{app}\TimeViewer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TimeViewer.exe"; Description: "{cm:LaunchProgram,TimeViewer}"; Flags: nowait postinstall skipifsilent

[Code]
// Not uninsdeletevalue on the entries above: the app can create them from Settings without the
// installer task ever having run, and those must not outlive the uninstall either.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'TimeViewer');
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', 'TimeViewer');
  end;
end;
