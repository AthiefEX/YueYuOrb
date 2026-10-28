#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

#define AppName "月语悬浮球"
#define AppPublisher "七月风语"
#define AppExeName "YueYuOrb.exe"

[Setup]
AppId={{CB89B2C1-41C1-47A6-8B73-CF54C6D0D403}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\YueYuOrb
DefaultGroupName=月语悬浮球
UninstallDisplayIcon={app}\{#AppExeName}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19044
PrivilegesRequired=lowest
OutputDir=..\artifacts
OutputBaseFilename=YueYuOrb-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=yes
UsePreviousAppDir=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "clearuserdata"; Description: "卸载时清除本用户月语悬浮球数据"; GroupDescription: "卸载选项:"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\月语悬浮球"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\月语悬浮球"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动月语悬浮球"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usPostUninstall) and WizardIsTaskSelected('clearuserdata') then
  begin
    DataDir := ExpandConstant('{localappdata}\FloatDesk');
    if DirExists(DataDir) then
      DelTree(DataDir, True, True, True);
  end;
end;
