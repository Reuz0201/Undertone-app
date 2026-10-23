; Установщик Undertone (Inno Setup 6). Собирается в GitHub Actions:
;   ISCC.exe /DAppVersion=3.0.0 /DSourceDir=<папка publish> installer\Undertone.iss
; Ставит в %LOCALAPPDATA%\Programs\Undertone без прав администратора — туда же умеет обновляться само приложение.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6E2B7B33-0B5C-4C36-9F0C-55C3B5A1D2F7}
AppName=Undertone
AppVersion={#AppVersion}
AppVerName=Undertone {#AppVersion}
AppPublisher=Reuz
AppPublisherURL=https://github.com/Reuz0201/Undertone-app
AppSupportURL=https://github.com/Reuz0201/Undertone-app/issues
DefaultDirName={localappdata}\Programs\Undertone
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputBaseFilename=Undertone-Setup-{#AppVersion}
SetupIconFile=..\Undertone\Assets\undertone.ico
UninstallDisplayIcon={app}\Undertone.exe
UninstallDisplayName=Undertone
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Ярлык на рабочем столе"; GroupDescription: "Дополнительно:"
Name: "autostart"; Description: "Запускать вместе с Windows (в трее)"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Undertone"; Filename: "{app}\Undertone.exe"
Name: "{userdesktop}\Undertone"; Filename: "{app}\Undertone.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Undertone"; \
  ValueData: """{app}\Undertone.exe"" --tray"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\Undertone.exe"; Description: "Запустить Undertone"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; файлы старых версий после самообновления
Type: files; Name: "{app}\*.old"

[UninstallRun]
; убрать автозапуск, если его включили в настройках приложения
Filename: "{cmd}"; Parameters: "/C reg delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Undertone /f"; \
  Flags: runhidden; RunOnceId: "RemoveAutostart"
