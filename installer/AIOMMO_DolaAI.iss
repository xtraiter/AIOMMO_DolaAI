; Bộ cài AIOMMO DolaAI (Inno Setup 6). Build bằng scripts\build-installer.ps1.
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\AIOMMO_DolaAI_v1.0.0"
#endif
#define AppName "AIOMMO DolaAI"
#define AppExe "AIOMMO DolaAI.exe"

[Setup]
AppId={{8F3B6C1E-5A7D-4E2B-9C41-2D6A0B7E91F5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ALL IN ONE MMO
AppPublisherURL=https://github.com/xtraiter/AIOMMO_DolaAI
AppSupportURL=https://github.com/xtraiter/AIOMMO_DolaAI/issues
VersionInfoVersion={#AppVersion}
; Cài như ứng dụng chuyên nghiệp: chương trình vào thư mục ứng dụng (C:\Program Files\AIOMMO DolaAI, cần quyền Admin, chỉ đọc);
; dữ liệu của người dùng nằm riêng ở %APPDATA%\AIOMMO DolaAI nên không cần ghi vào thư mục chương trình.
; Người dùng không có quyền Admin có thể chọn "chỉ cho tôi" ở hộp thoại đầu tiên (cài vào %LOCALAPPDATA%\Programs).
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
LicenseFile=Terms_vi.txt
OutputDir=..\dist\installer
OutputBaseFilename=AIOMMO_DolaAI_Setup_{#AppVersion}
SetupIconFile=..\DolaCoordinator\Assets\logo64.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "vi"; MessagesFile: "Vietnamese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIconTask}"; GroupDescription: "{cm:IconsGroup}"
Name: "downloadbrowser"; Description: "{cm:BrowserTask}"; GroupDescription: "{cm:BrowserGroup}"

[Files]
; dữ liệu chạy (tài khoản, video, cơ sở dữ liệu) không bao giờ đóng vào bộ cài; khi cài đặt chúng nằm ở %APPDATA%\AIOMMO DolaAI
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "gateway\accounts\*,gateway\downloads\*,gateway\refs\*,gateway\*.db"; Flags: ignoreversion recursesubdirs createallsubdirs
; tệp đánh dấu "bản đã cài": app thấy nó thì lưu dữ liệu ở %APPDATA%\AIOMMO DolaAI thay vì cạnh chương trình
Source: "installed.marker"; DestDir: "{app}"; Flags: ignoreversion
Source: "Terms_vi.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\gateway\dola-gateway.exe"; Parameters: "install-browser"; WorkingDir: "{app}\gateway"; StatusMsg: "{cm:BrowserStatus}"; Tasks: downloadbrowser; Flags: waituntilterminated runasoriginaluser
Filename: "{app}\{#AppExe}"; Description: "{cm:RunAfter}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
// Khi gỡ cài đặt: hỏi có giữ dữ liệu (tài khoản đã đăng nhập, proxy, prompt, cài đặt — nằm ở %APPDATA%\AIOMMO DolaAI) không.
// Mặc định giữ (chế độ gỡ im lặng cũng giữ). SuppressibleMsgBox: hộp thoại này không treo khi chạy với /SUPPRESSMSGBOXES.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if SuppressibleMsgBox(CustomMessage('KeepDataQuestion'), mbConfirmation, MB_YESNO, IDYES) = IDNO then
    begin
      DelTree(ExpandConstant('{userappdata}\AIOMMO DolaAI'), True, True, True);
      DelTree(ExpandConstant('{app}'), True, True, True);
    end;
  end;
end;

// Windows giới hạn đường dẫn 260 ký tự; tệp sâu nhất trong gói dài khoảng 120 ký tự nên thư mục cài không nên dài quá 120.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and (Length(WizardDirValue) > 120) then
  begin
    MsgBox(CustomMessage('PathTooLong'), mbError, MB_OK);
    Result := False;
  end;
end;
