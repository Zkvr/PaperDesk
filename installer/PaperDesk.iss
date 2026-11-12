#ifndef AppVersion
  #define AppVersion "0.5.0-beta"
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
english.UpdateStartupFailed=PaperDesk could not update its automatic startup command.
russian.UpdateStartupFailed=PaperDesk не удалось обновить команду автозапуска.

[Files]
Source: "{#BuildDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "PaperDesk.installed.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\PaperDesk"; Filename: "{app}\PaperDesk.exe"; WorkingDir: "{app}"; Tasks: startmenu
Name: "{autodesktop}\PaperDesk"; Filename: "{app}\PaperDesk.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\PaperDesk.exe"; Description: "{cm:LaunchProgram,PaperDesk}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

procedure CurStepChanged(Step: TSetupStep);
var Command, Executable, AdjacentData: String;
begin
  if (Step = ssPostInstall) and RegQueryStringValue(HKCU, RunKey, 'PaperDesk', Command) then
  begin
    Executable := '"' + ExpandConstant('{app}\PaperDesk.exe') + '"';
    AdjacentData := '"' + ExpandConstant('{app}\data') + '"';
    if Uppercase(Command) = Uppercase(Executable + ' --startup --data ' + AdjacentData) then
      if not RegWriteStringValue(HKCU, RunKey, 'PaperDesk', Executable + ' --startup') then
        RaiseException(CustomMessage('UpdateStartupFailed'));
  end;
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var Command: String;
begin
  if (Step = usUninstall) and RegQueryStringValue(HKCU, RunKey, 'PaperDesk', Command) then
    if Pos(Uppercase('"' + ExpandConstant('{app}\PaperDesk.exe') + '" '), Uppercase(Command)) = 1 then
      RegDeleteValue(HKCU, RunKey, 'PaperDesk');
end;
