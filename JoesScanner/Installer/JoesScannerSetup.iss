#define MyAppName "Joe's Scanner"
#define MyAppVersion "1.2.1"
#define MyAppPublisher "Joe's Scanner"
#define MyAppExeName "JoesScanner.exe"

// All paths are relative to this .iss file in Installer\
#define MyAppDir ".\publish-unpackaged"
#define MyIconFile ".\logo.ico"

[Setup]
AppId={{b9ac30a1-b43f-4f02-81ca-a8bd8257abc3}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=C:\Users\nate\Desktop\
OutputBaseFilename=JoesScanner-Setup-Win-x64
Compression=lzma
SolidCompression=yes
SetupIconFile={#MyIconFile}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop icon"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
; Copy everything from the publish folder into the app folder
Source: "{#MyAppDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
