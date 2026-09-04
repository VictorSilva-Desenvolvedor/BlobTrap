; Inno Setup script for BlobTrap.
;
; Build it through installer\build.ps1, which publishes the app first and passes the
; version in. Compiling this file on its own will fail on the missing publish folder,
; which is deliberate - the installer should never ship a stale build.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#define AppName "BlobTrap"
#define AppPublisher "VictorPauloDev"
#define AppExeName "BlobTrap.exe"
#define SourceDir "..\publish"

[Setup]
; Never reuse this GUID for another product; it is how Windows recognises upgrades.
AppId={{7A3C6E14-9B58-4D2F-8E6A-1C0F5B92D7A4}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; Per-user only, never elevated. The app keeps its settings, downloaded tools and browser
; profile under the user's own %LOCALAPPDATA%, so a machine-wide install would be incoherent:
; uninstalling as one user cannot reach anybody else's data, and would orphan it.
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no

OutputDir=..\dist
OutputBaseFilename=BlobTrap-Setup-{#AppVersion}
SetupIconFile=..\src\BlobTrap.App\Assets\BlobTrap.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Closes a running copy before overwriting it, instead of failing mid-install.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
{ BlobTrap hosts an embedded browser, so it cannot start without the WebView2 runtime.
  Windows 11 ships it, but Windows 10 machines and stripped images may not have it. }
{ Uninstalling the runtime can leave the EdgeUpdate key behind with pv empty or "0.0.0.0",
  so the presence of the key proves nothing - only a real version does. }
function HasRuntimeVersion(Root: Integer; const Key: string): Boolean;
var
  Value: string;
begin
  Result := RegQueryStringValue(Root, Key, 'pv', Value)
    and (Value <> '')
    and (Value <> '0.0.0.0');
end;

function IsWebView2Installed: Boolean;
begin
  Result :=
    HasRuntimeVersion(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    HasRuntimeVersion(HKLM, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') or
    HasRuntimeVersion(HKCU, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}');
end;

function InitializeSetup: Boolean;
var
  Answer: Integer;
  ErrorCode: Integer;
begin
  Result := True;
  if IsWebView2Installed then
    Exit;

  Answer := MsgBox(
    'O BlobTrap precisa do Microsoft Edge WebView2 Runtime, que nao foi encontrado neste computador.' + #13#10#13#10 +
    'Deseja abrir a pagina de download agora? A instalacao do BlobTrap continua normalmente,' + #13#10 +
    'mas o aplicativo so vai abrir depois que o WebView2 estiver instalado.',
    mbConfirmation, MB_YESNO);

  if Answer = IDYES then
    ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOW, ewNoWait, ErrorCode);
end;

{ The app's data - settings, the downloaded ffmpeg/yt-dlp, and the embedded browser profile
  holding logged-in sessions - is only removed if the user says so. Defaulting to No keeps
  uninstalling non-destructive: reinstalling later finds the logins and tools still there.
  Downloaded videos live in the user's Videos folder and are never touched either way.

  A silent uninstall is checked explicitly and keeps the data without asking. Relying on
  /SUPPRESSMSGBOXES here does not work: it governs Setup's own dialogs, not a MsgBox raised
  from [Code], so an unattended uninstall would sit on this prompt forever. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  if UninstallSilent then
    Exit;

  DataDir := ExpandConstant('{localappdata}\BlobTrap');
  if not DirExists(DataDir) then
    Exit;

  if MsgBox(
       'Remover tambem os dados do BlobTrap?' + #13#10#13#10 +
       'Isso apaga as configuracoes, o ffmpeg e o yt-dlp baixados,' + #13#10 +
       'e as sessoes logadas do navegador embutido.' + #13#10#13#10 +
       'Os videos que voce baixou nao sao afetados.',
       mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(DataDir, True, True, True);
end;
