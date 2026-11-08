#ifndef AppVersion
  #define AppVersion "0.3.0-preview.10"
#endif
#ifndef BuildDir
  #define BuildDir "..\artifacts\release-win-x64"
#endif
#ifndef OutputPath
  #define OutputPath "..\artifacts\release"
#endif

[Setup]
AppId={{0D82E9EB-5585-4DB0-8981-F7E35C9921E3}
AppName=PaperDesk
AppVersion={#AppVersion}
AppPublisher=PaperDesk
DefaultDirName={localappdata}\Programs\PaperDesk
DefaultGroupName=PaperDesk
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
UninstallDisplayIcon={app}\PaperDesk.exe
SetupIconFile=..\PaperDesk.Desktop\paperdesk.ico
OutputDir={#OutputPath}
OutputBaseFilename=PaperDesk-Setup-win-x64
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "startmenu"; Description: "{cm:StartMenuShortcut}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[CustomMessages]
english.StartMenuShortcut=Add a Start menu shortcut
russian.StartMenuShortcut=Добавить ярлык в меню «Пуск»

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\PaperDesk"; Filename: "{app}\PaperDesk.exe"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{autodesktop}\PaperDesk"; Filename: "{app}\PaperDesk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PaperDesk.exe"; Description: "{cm:LaunchProgram,PaperDesk}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(Step: TUninstallStep);
var Command: String;
begin
  if (Step = usUninstall) and RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PaperDesk', Command) then
    if Pos(Uppercase('"' + ExpandConstant('{app}\PaperDesk.exe') + '" '), Uppercase(Command)) = 1 then
      RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PaperDesk');
end;
