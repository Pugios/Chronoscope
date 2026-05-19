[Setup]
AppId={{bd30d463-63bd-4623-a1a0-7da3305e8e14}
AppName=TimeViewer
AppVersion=0.2
AppPublisher=Mohamed Matar
DefaultDirName={autopf}\TimeViewer
DefaultGroupName=TimeViewer
SetupIconFile=Resources\AppIcon\favicon.ico
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

[Files]
Source: "bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TimeViewer"; Filename: "{app}\TimeViewer.exe"; IconFilename: "{app}\TimeViewer.exe"
Name: "{autodesktop}\TimeViewer"; Filename: "{app}\TimeViewer.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\TimeViewer.exe"; Description: "{cm:LaunchProgram,TimeViewer}"; Flags: nowait postinstall skipifsilent
