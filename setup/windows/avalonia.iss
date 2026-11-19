[Setup]
AppId=GitExtensions.Avalonia
AppName=Git Extensions (Avalonia)
AppVersion={#AppVersion}
DefaultDirName={userpf}\Git Extensions Avalonia
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
SetupIconFile=..\assets\Logo\git-extensions-logo.ico
UninstallDisplayIcon={app}\GitExtensions.exe
Compression=lzma2
SolidCompression=yes

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Git Extensions (Avalonia)"; Filename: "{app}\GitExtensions.exe"
