[Setup]
AppName=Suni Live Translator
AppVersion=0.1.0
DefaultDirName={localappdata}\Programs\Suni
DefaultGroupName=Suni
PrivilegesRequired=lowest
OutputBaseFilename=SuniSetup-0.1.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
[Files]
Source: "..\dist\Suni\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Suni"; Filename: "{app}\Suni.exe"
Name: "{autodesktop}\Suni"; Filename: "{app}\Suni.exe"
