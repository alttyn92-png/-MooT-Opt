[Setup]
#ifndef MootServer
  #define MootServer "https://cultmoot.online"
#endif
#ifndef OutputName
  #define OutputName "MooT-Setup"
#endif
AppId=MooT.Agent.LocalMVP
AppName=MooT Optimization Boonderking
AppVersion=1.3.3
AppPublisher=MooT
DefaultDirName={localappdata}\Programs\MooT
DefaultGroupName=MooT
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
OutputDir=agent-1.3.3\installer
OutputBaseFilename={#OutputName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\MooT-Agent.exe
DisableProgramGroupPage=yes
CloseApplications=yes
SetupLogging=yes
SetupIconFile=web\moot.ico

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"

[Files]
Source: "agent-1.3.3\MooT-Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\MooT Agent"; Filename: "{app}\MooT-Agent.exe"; Parameters: "--server {#MootServer}"
Name: "{autodesktop}\MooT Agent"; Filename: "{app}\MooT-Agent.exe"; Parameters: "--server {#MootServer}"; Tasks: desktopicon

[Run]
Filename: "{app}\MooT-Agent.exe"; Parameters: "--server {#MootServer}"; Description: "Запустить MooT Agent"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\MooT.lnk"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "MooT"; Flags: uninsdeletevalue

