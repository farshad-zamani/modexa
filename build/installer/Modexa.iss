; ============================================================================================
;  Modexa installer (Inno Setup 6.5+). Built by build\release.ps1 — do not run by hand unless
;  dist\Modexa-release\Modexa.exe is already the hardened, obfuscated build.
;
;  Layout decisions:
;    * Per-user install to %LocalAppData%\Programs\Modexa (no admin / UAC prompt needed).
;    * The app keeps NO data next to the exe: settings, licenses, engine and backups live in
;      %LocalAppData%\Modexa, so updates simply replace Modexa.exe.
;    * .mxa files open in Modexa (double-click a purchased mod to install it).
; ============================================================================================

#define AppName      "Modexa"
#define AppExe       "Modexa.exe"
#ifndef AppExePath
  #define AppExePath "..\..\dist\_app\Modexa.exe"
#endif
#define SrcExe       AppExePath
#define AppVersion   GetVersionNumbersString(SrcExe)
#define Publisher    "CloudTart"
#define PublisherUrl "https://cloudtart.com"

[Setup]
AppId={{81B054EE-24D5-4ABE-886D-59730609CF89}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#PublisherUrl}
AppSupportURL={#PublisherUrl}
AppCopyright=© CloudTart · Made for WTMod.com
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\..\dist\installer
OutputBaseFilename=Modexa-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=110
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
ChangesAssociations=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto
#if FileExists("..\..\src\Modexa.App\assets\modexa.ico")
SetupIconFile=..\..\src\Modexa.App\assets\modexa.ico
#endif

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "fa"; MessagesFile: "Farsi.isl"

[CustomMessages]
en.AssocMxa=Open Modexa packages (.mxa) with Modexa
fa.AssocMxa=باز کردن بسته‌های مادکسا (.mxa) با مادکسا
en.MxaType=Modexa mod package
fa.MxaType=بسته‌ی ماد مادکسا
en.RemoveData=Also delete your Modexa data (settings, licenses, downloaded engine and backups of original game files)?%n%nChoose No to keep it for a later reinstall.
fa.RemoveData=داده‌های مادکسا (تنظیمات، لایسنس‌ها، موتور دانلودشده و نسخه‌ی پشتیبان فایل‌های اصلی بازی) هم حذف شود؟%n%nبرای نگه داشتن آن‌ها جهت نصب دوباره «خیر» را بزنید.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assocmxa"; Description: "{cm:AssocMxa}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SrcExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Per-user file association (HKA = HKCU for a per-user install).
Root: HKA; Subkey: "Software\Classes\.mxa"; ValueType: string; ValueName: ""; ValueData: "Modexa.Package"; Flags: uninsdeletevalue; Tasks: assocmxa
Root: HKA; Subkey: "Software\Classes\Modexa.Package"; ValueType: string; ValueName: ""; ValueData: "{cm:MxaType}"; Flags: uninsdeletekey; Tasks: assocmxa
Root: HKA; Subkey: "Software\Classes\Modexa.Package\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Tasks: assocmxa
Root: HKA; Subkey: "Software\Classes\Modexa.Package\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assocmxa

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Modexa');
    if DirExists(DataDir) and not UninstallSilent then
      if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
