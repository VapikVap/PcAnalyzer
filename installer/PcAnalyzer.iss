; Установщик PC Performance Analyzer (Inno Setup 6).
; Собирается скриптом build-installer.ps1 в корне репозитория — он передаёт AppVersion и SourceDir.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

#define AppName "PC Performance Analyzer"
#define AppExe "PcAnalyzer.exe"

[Setup]
AppId={{6F1C2B7E-4A3D-4E8B-9C21-5D7A0B3E9F14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=..\src\PcAnalyzer.App\Assets\app.ico
OutputDir=..\artifacts
OutputBaseFilename=PcAnalyzer-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Права администратора нужны для установки в Program Files; пользователь может выбрать установку только для себя.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
; HKCU используется только для автозапуска текущего пользователя — это и задумано.
UsedUserAreasWarning=no
; Запущенное приложение проверяется в InitializeSetup (мьютекс из App.xaml.cs): при обновлении из приложения
; установщик ждёт его закрытия, при обычной установке просит закрыть. Restart Manager — запасной вариант.
CloseApplications=yes

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"
Name: "autostart"; Description: "Запускать вместе с Windows (свёрнуто в трей)"; GroupDescription: "Дополнительно:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; То же значение, что пишет SystemIntegration.SetAutostart, чтобы галочка в настройках приложения совпадала.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PcPerformanceAnalyzer"; ValueData: """{app}\{#AppExe}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "Запустить {#AppName}"; Flags: nowait postinstall skipifsilent
; Обновление из приложения (/UPDATE): запустить новую версию от имени пользователя, а не администратора.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsUpdate

[UninstallRun]
; Остановить сессию трассировки PresentMon, если она осталась после аварийного завершения.
Filename: "{sys}\logman.exe"; Parameters: "stop PcAnalyzer -ets"; Flags: runhidden; RunOnceId: "StopEtw"

[Code]
const
  AppMutexName = 'Local\PcPerformanceAnalyzer.SingleInstance';

function IsUpdate: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/UPDATE') = 0 then
      Result := True;
end;

function InitializeSetup: Boolean;
var
  Waited: Integer;
begin
  Result := True;
  if IsUpdate then
  begin
    // Приложение само запустило установщик и сейчас закрывается (сохраняет сессию) — ждём до 60 с.
    Waited := 0;
    while CheckForMutexes(AppMutexName) and (Waited < 60000) do
    begin
      Sleep(500);
      Waited := Waited + 500;
    end;
  end;
  // SuppressibleMsgBox: при /SUPPRESSMSGBOXES вернёт IDCANCEL, а не будет бесконечно «повторять».
  while CheckForMutexes(AppMutexName) do
    if SuppressibleMsgBox('{#AppName} сейчас запущен. Закройте его (значок в области уведомлений → «Выход») и нажмите «Повтор».',
              mbError, MB_RETRYCANCEL, IDCANCEL) = IDCANCEL then
    begin
      Result := False;
      Exit;
    end;
end;

// Автозапуск мог быть включён из настроек приложения, а не установщиком: убрать его при удалении в любом случае.
// Данные пользователя (%LOCALAPPDATA%\PcAnalyzer: история сессий, настройки) намеренно не удаляются.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'PcPerformanceAnalyzer');
end;
