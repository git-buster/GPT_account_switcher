[CmdletBinding()]
param([ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version='0.6.14')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$taskStage=Join-Path $taskRoot 'artifacts\installer-build'
$taskOutput=Join-Path $taskRoot ('release\ChatGPTDesktopSwitcher-'+$Version+'-Setup.msi')
if((Test-Path -LiteralPath $taskStage)-or(Test-Path -LiteralPath $taskOutput)){throw 'Installer output already exists. Move the reviewed output aside before rebuilding.'}
$taskExe=Join-Path $taskRoot 'dist\ChatGPTAccountSwitcher.exe'
if([Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe).FileVersion -ne ($Version+'.0')){throw 'Build the matching desktop EXE first.'}
$taskFiles=@('dist\ChatGPTAccountSwitcher.exe','dist\ChatGPTAccountSwitcher.exe.config','README.md','SECURITY.md','LICENSE','docs\USAGE.md','docs\README.zh-Hans.md','docs\README.zh-Hant.md','docs\README.fr.md','docs\README.ja.md')
$taskFiles+=@('panel-en.png','panel-zh-Hans.png','panel-zh-Hant.png','panel-fr.png','panel-ja.png','settings-en.png','app-en.png','sharing-en.png','quota-en.png','about-en.png','history-en.png','history-zh-Hans.png','history-zh-Hant.png','history-fr.png','history-ja.png' | ForEach-Object {'docs\images\'+$_})
foreach($taskSource in $taskFiles){$taskPath=Join-Path $taskRoot $taskSource;if(-not(Test-Path -LiteralPath $taskPath -PathType Leaf)){throw 'Required public payload is missing.'};for($taskInfo=Get-Item -LiteralPath $taskPath;$null -ne $taskInfo;$taskInfo=$(if($taskInfo -is [IO.FileInfo]){$taskInfo.Directory}else{$taskInfo.Parent})){if($taskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse paths are not allowed.'}}}
[IO.Directory]::CreateDirectory($taskStage)|Out-Null
[IO.Directory]::CreateDirectory((Split-Path -Parent $taskOutput))|Out-Null
$taskPayload=Join-Path $taskStage 'payload';[IO.Directory]::CreateDirectory($taskPayload)|Out-Null
$taskDdf=@('.OPTION EXPLICIT','.Set CabinetNameTemplate=payload.cab',('.Set DiskDirectoryTemplate="'+$taskStage+'"'),('.Set InfFileName="'+(Join-Path $taskStage 'payload.inf')+'"'),('.Set RptFileName="'+(Join-Path $taskStage 'payload.rpt')+'"'),'.Set Cabinet=on','.Set Compress=on','.Set CompressionType=MSZIP','.Set MaxDiskSize=0','.Set MaxCabinetSize=0')
$taskRows=@();$taskIndex=0
foreach($taskSource in $taskFiles){
 $taskIndex++;$taskId='File'+$taskIndex.ToString('000');$taskTarget=Join-Path $taskPayload $taskId
 Copy-Item -LiteralPath (Join-Path $taskRoot $taskSource) -Destination $taskTarget
 # CAB stores DOS local timestamps: use the same literal date on every builder.
 (Get-Item -LiteralPath $taskTarget).LastWriteTime=[datetime]'2000-01-01T00:00:00'
 $taskRelative=$taskSource -replace '^dist\\',''
 $taskRows+=,[pscustomobject]@{Id=$taskId;Relative=$taskRelative;Source=$taskTarget;Component=$(if($taskRelative.StartsWith('docs\images\')){'ImagesComponent'}elseif($taskRelative.StartsWith('docs\')){'DocsComponent'}else{'AppComponent'});Sequence=$taskIndex}
 $taskDdf+=('"'+$taskTarget+'" '+$taskId)
}
$taskDdfPath=Join-Path $taskStage 'payload.ddf';[IO.File]::WriteAllLines($taskDdfPath,$taskDdf,[Text.Encoding]::Default)
& (Join-Path $env:WINDIR 'System32\makecab.exe') /F $taskDdfPath | Out-Null
if($LASTEXITCODE -ne 0 -or -not(Test-Path -LiteralPath (Join-Path $taskStage 'payload.cab'))){throw 'Public cabinet build failed.'}
function Task-Guid([string]$Name){$taskHash=[Security.Cryptography.SHA256]::Create();try{$taskBytes=$taskHash.ComputeHash([Text.Encoding]::UTF8.GetBytes('git-buster/GPT-Switcher/'+$Name));$taskGuidBytes=New-Object byte[] 16;[Array]::Copy($taskBytes,$taskGuidBytes,16);return ([Guid]::new($taskGuidBytes)).ToString('B').ToUpperInvariant()}finally{$taskHash.Dispose()}}
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class SwitcherMsiBuilder {
 [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiOpenDatabase(string path,IntPtr mode,out uint database);
 [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiDatabaseOpenView(uint database,string sql,out uint view);
 [DllImport("msi.dll")] static extern uint MsiViewExecute(uint view,uint record);
 [DllImport("msi.dll")] static extern uint MsiCreateRecord(uint count);
 [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiRecordSetString(uint record,uint field,string text);
 [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiRecordSetStream(uint record,uint field,string path);
 [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiGetSummaryInformation(uint database,string path,uint count,out uint summary);
 [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiSummaryInfoSetProperty(uint summary,uint property,uint kind,int number,IntPtr time,string text);
 [DllImport("msi.dll")] static extern uint MsiSummaryInfoPersist(uint summary);
 [DllImport("msi.dll")] static extern uint MsiDatabaseCommit(uint database);
 [DllImport("msi.dll")] public static extern uint MsiCloseHandle(uint handle);
 static void Check(uint result,string action){if(result!=0)throw new IOException(action+" failed (Windows Installer "+result+").");}
 public static uint Open(string path){uint database;Check(MsiOpenDatabase(path,new IntPtr(3),out database),"Create database");return database;}
 public static void Sql(uint database,string sql){uint view;Check(MsiDatabaseOpenView(database,sql,out view),"Open database view");try{Check(MsiViewExecute(view,0),"Execute database view");}finally{MsiCloseHandle(view);}}
 public static void Stream(uint database,string table,string name,string path){uint view;Check(MsiDatabaseOpenView(database,"INSERT INTO `"+table+"` (`Name`,`Data`) VALUES (?,?)",out view),"Open stream view");uint record=MsiCreateRecord(2);try{Check(MsiRecordSetString(record,1,name),"Set stream name");Check(MsiRecordSetStream(record,2,path),"Set public stream");Check(MsiViewExecute(view,record),"Insert public stream");}finally{MsiCloseHandle(record);MsiCloseHandle(view);}}
 public static void Summary(uint database,int[] keys,string[] values){uint summary;Check(MsiGetSummaryInformation(database,null,20,out summary),"Open summary");try{for(int i=0;i<keys.Length;i++){bool numeric=keys[i]==1||keys[i]==14||keys[i]==15||keys[i]==19;Check(MsiSummaryInfoSetProperty(summary,(uint)keys[i],numeric?(keys[i]==1?2u:3u):30u,numeric?Int32.Parse(values[i]):0,IntPtr.Zero,numeric?null:values[i]),"Set public summary");}Check(MsiSummaryInfoPersist(summary),"Save summary");}finally{MsiCloseHandle(summary);}}
 public static void Commit(uint database){Check(MsiDatabaseCommit(database),"Commit database");}
}
'@
function Task-Sql([string]$Sql){try{[SwitcherMsiBuilder]::Sql($taskDatabase,$Sql)}catch{throw ('Public MSI statement failed: '+$Sql+'; '+$_.Exception.GetBaseException().Message)}}
function Task-Insert([string]$Table,[object[]]$Values){$taskValues=@($Values|ForEach-Object{if($null -eq $_){'NULL'}elseif($_ -is [int]){[string]$_}else{"'"+([string]$_).Replace("'","''")+"'"}});$taskColumns=@([regex]::Matches($taskSchemas[$Table],'`([^`]+)`\s+(?:CHAR|SHORT|LONG|OBJECT)\b')|ForEach-Object{'`'+$_.Groups[1].Value+'`'});if($taskColumns.Count -ne $taskValues.Count){throw ('Installer column count mismatch: '+$Table)};Task-Sql ('INSERT INTO `'+$Table+'` ('+($taskColumns -join ',')+') VALUES ('+($taskValues -join ',')+')')}
function Task-Stream([string]$Table,[string]$Name,[string]$Path){[SwitcherMsiBuilder]::Stream($taskDatabase,$Table,$Name,$Path)}
$taskDatabase=[SwitcherMsiBuilder]::Open($taskOutput)
try{
 $taskSchemas=[ordered]@{
  Property='`Property` CHAR(72) NOT NULL, `Value` CHAR(0) LOCALIZABLE PRIMARY KEY `Property`'
  Directory='`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`'
  Component='`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`'
  Feature='`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`'
  FeatureComponents='`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`'
  File='`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`'
  Media='`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`'
  Registry='`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` CHAR(0) LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`'
  RemoveFile='`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`'
  Shortcut='`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(255) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`'
  Icon='`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`'
  CustomAction='`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`'
  InstallExecuteSequence='`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`'
  InstallUISequence='`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`'
  LaunchCondition='`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`'
  AppSearch='`Property` CHAR(72) NOT NULL, `Signature_` CHAR(72) NOT NULL PRIMARY KEY `Property`, `Signature_`'
  RegLocator='`Signature_` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL, `Name` CHAR(255), `Type` SHORT PRIMARY KEY `Signature_`'
  Signature='`Signature` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `MinVersion` CHAR(20), `MaxVersion` CHAR(20), `MinSize` LONG, `MaxSize` LONG, `MinDate` LONG, `MaxDate` LONG, `Languages` CHAR(255) PRIMARY KEY `Signature`'
  Upgrade='`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`'
  Dialog='`Dialog` CHAR(72) NOT NULL, `HCentering` SHORT NOT NULL, `VCentering` SHORT NOT NULL, `Width` SHORT NOT NULL, `Height` SHORT NOT NULL, `Attributes` LONG, `Title` CHAR(128) LOCALIZABLE, `Control_First` CHAR(50) NOT NULL, `Control_Default` CHAR(50), `Control_Cancel` CHAR(50) PRIMARY KEY `Dialog`'
  Control='`Dialog_` CHAR(72) NOT NULL, `Control` CHAR(50) NOT NULL, `Type` CHAR(20) NOT NULL, `X` SHORT NOT NULL, `Y` SHORT NOT NULL, `Width` SHORT NOT NULL, `Height` SHORT NOT NULL, `Attributes` LONG, `Property` CHAR(72), `Text` CHAR(0) LOCALIZABLE, `Control_Next` CHAR(50), `Help` CHAR(50) LOCALIZABLE PRIMARY KEY `Dialog_`, `Control`'
  ControlEvent='`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Argument` CHAR(255) NOT NULL, `Condition` CHAR(255), `Ordering` SHORT PRIMARY KEY `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`'
  CheckBox='`Property` CHAR(72) NOT NULL, `Value` CHAR(64) LOCALIZABLE PRIMARY KEY `Property`'
  TextStyle='`TextStyle` CHAR(72) NOT NULL, `FaceName` CHAR(32) NOT NULL, `Size` SHORT NOT NULL, `Color` LONG, `StyleBits` SHORT PRIMARY KEY `TextStyle`'
  EventMapping='`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Event` CHAR(50) NOT NULL, `Attribute` CHAR(50) NOT NULL PRIMARY KEY `Dialog_`, `Control_`, `Event`'
  Error='`Error` SHORT NOT NULL, `Message` CHAR(0) LOCALIZABLE PRIMARY KEY `Error`'
  ActionText='`Action` CHAR(72) NOT NULL, `Description` CHAR(64) LOCALIZABLE, `Template` CHAR(128) LOCALIZABLE PRIMARY KEY `Action`'
  UIText='`Key` CHAR(72) NOT NULL, `Text` CHAR(255) LOCALIZABLE PRIMARY KEY `Key`'
  ControlCondition='`Dialog_` CHAR(72) NOT NULL, `Control_` CHAR(50) NOT NULL, `Action` CHAR(50) NOT NULL, `Condition` CHAR(255) NOT NULL PRIMARY KEY `Dialog_`, `Control_`, `Action`, `Condition`'
 }
 foreach($taskSchema in $taskSchemas.GetEnumerator()){Task-Sql ('CREATE TABLE `'+$taskSchema.Key+'` ('+$taskSchema.Value+')')}
 # Keep Error present, but empty: Windows Installer supplies its localized stock messages.
 # An absent table can turn a normal font-initialization message into error 2228.
 foreach($taskAction in @(@('Rollback','Undoing installation.','[1]'),@('RollbackCleanup','Cleaning up installation.','[1]'),@('GenerateScript','Preparing installation.','[1]'),@('InstallFiles','Installing files.','[1]'),@('RemoveFiles','Removing files.','[1]'),@('CreateShortcuts','Creating shortcuts.',$null),@('WriteRegistryValues','Saving installation settings.',$null))){Task-Insert ActionText $taskAction}
 $taskUpgrade='{5B7145CB-10B1-42EF-BD4B-996DCB35617E}'
 $taskProperties=[ordered]@{ProductCode=(Task-Guid ('product/'+$Version));ProductName='GPT Switcher';ProductVersion=$Version;Manufacturer='git-buster';ProductLanguage='1033';UpgradeCode=$taskUpgrade;DefaultUIFont='DefaultFont';ARPCOMMENTS='Local account selector. Sign in only through the official client.';ARPURLINFOABOUT='https://github.com/git-buster/GPT_account_switcher';ARPHELPLINK='https://github.com/git-buster/GPT_account_switcher/issues';ARPPRODUCTICON='SwitcherIcon';STARTUPMODE='0';SecureCustomProperties='OLDERPRODUCTS;NEWERPRODUCTS;STARTONLOGIN;STARTUPMODE';MSIFASTINSTALL='7';DISABLEADVTSHORTCUTS='1'}
 foreach($taskProperty in $taskProperties.GetEnumerator()){Task-Insert Property @($taskProperty.Key,$taskProperty.Value)}
 foreach($taskDirectory in @(@('TARGETDIR',$null,'SourceDir'),@('LocalAppDataFolder','TARGETDIR','.'),@('UserPrograms','LocalAppDataFolder','Programs'),@('INSTALLFOLDER','UserPrograms','GPTSW~1|GPT Switcher'),@('DOCFOLDER','INSTALLFOLDER','docs'),@('IMAGESFOLDER','DOCFOLDER','images'),@('DesktopFolder','TARGETDIR','.'),@('ProgramMenuFolder','TARGETDIR','.'))){Task-Insert Directory $taskDirectory}
 foreach($taskComponent in @(@('AppComponent','INSTALLFOLDER','InstallPath'),@('DocsComponent','DOCFOLDER','DocsPath'),@('ImagesComponent','IMAGESFOLDER','ImagesPath'))){
  Task-Insert Component @($taskComponent[0],(Task-Guid ('component/'+$taskComponent[0])),$taskComponent[1],4,$null,('Reg'+$taskComponent[0]))
  Task-Insert FeatureComponents @('Complete',$taskComponent[0]);Task-Insert Registry @(('Reg'+$taskComponent[0]),1,'Software\git-buster\GPTSwitcher',$taskComponent[2],('['+$taskComponent[1]+']'),$taskComponent[0])
 }
 Task-Insert Feature @('Complete',$null,'GPT Switcher','Program and five-language guides.',1,1,'INSTALLFOLDER',0)
 foreach($taskRow in $taskRows){Task-Insert File @($taskRow.Id,$taskRow.Component,([IO.Path]::GetFileName($taskRow.Relative)),[int](Get-Item -LiteralPath $taskRow.Source).Length,$(if($taskRow.Id -eq 'File001'){$Version+'.0'}else{$null}),$null,16384,[int]$taskRow.Sequence)}
 Task-Insert Media @(1,$taskRows.Count,$null,'#payload.cab',$null,$null)
 Task-Stream '_Streams' 'payload.cab' (Join-Path $taskStage 'payload.cab');Task-Stream Icon 'SwitcherIcon' (Join-Path $taskRoot 'src\icon.ico')
 foreach($taskShortcut in @(@('DesktopShortcut','DesktopFolder'),@('MenuShortcut','ProgramMenuFolder'))){Task-Insert Shortcut @($taskShortcut[0],$taskShortcut[1],'GPTSW~1|GPT Switcher','AppComponent','[#File001]',$null,'GPT Switcher',$null,'SwitcherIcon',0,1,'INSTALLFOLDER')}
 Task-Insert Shortcut @('UninstallShortcut','ProgramMenuFolder','UNINST~1|Uninstall GPT Switcher','AppComponent','[SystemFolder]msiexec.exe','/x [ProductCode]','Uninstall GPT Switcher',$null,'SwitcherIcon',0,1,'INSTALLFOLDER')
 foreach($taskRemove in @(@('RemoveImagesFolder','ImagesComponent','IMAGESFOLDER'),@('RemoveDocsFolder','DocsComponent','DOCFOLDER'),@('RemoveAppFolder','AppComponent','INSTALLFOLDER'))){Task-Insert RemoveFile @($taskRemove[0],$taskRemove[1],$null,$taskRemove[2],2)}
 Task-Insert RegLocator @('NetFxRelease',2,'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full','Release',18)
 Task-Insert RegLocator @('ExistingStartup',1,'Software\Microsoft\Windows\CurrentVersion\Run','GPTSwitcher',2)
 Task-Insert AppSearch @('NETFRAMEWORK48','NetFxRelease');Task-Insert AppSearch @('EXISTINGSTARTUP','ExistingStartup')
 Task-Insert LaunchCondition @('Installed OR (NETFRAMEWORK48 >= "#528040")','Install .NET Framework 4.8, then run setup again.')
 Task-Insert LaunchCondition @('NOT NEWERPRODUCTS','A newer GPT Switcher version is already installed.')
 Task-Insert Upgrade @($taskUpgrade,'0.0.0',$Version,$null,256,$null,'OLDERPRODUCTS');Task-Insert Upgrade @($taskUpgrade,$Version,$null,$null,2,$null,'NEWERPRODUCTS')
 Task-Insert CustomAction @('KeepStartupChoice',51,'STARTONLOGIN','1');Task-Insert CustomAction @('StartupMode',51,'STARTUPMODE','1')
 Task-Insert CustomAction @('ConfigureStartup',1042,'File001','--configure-startup [STARTUPMODE]')
 Task-Insert CustomAction @('RollbackNewStartup',1298,'File001','--remove-startup-if-current')
 Task-Insert CustomAction @('RemoveOwnStartup',1042,'File001','--remove-startup-if-current')
 Task-Insert CustomAction @('StopSwitcher',1042,'File001','--request-exit')
 Task-Insert CustomAction @('RepairAll',51,'REINSTALL','ALL');Task-Insert CustomAction @('RepairMode',51,'REINSTALLMODE','amus')
 $taskCommon=@(@('FindRelatedProducts',$null,25),@('AppSearch',$null,50),@('LaunchConditions',$null,100),@('CostInitialize',$null,800),@('FileCost',$null,900),@('CostFinalize',$null,1000))
 foreach($taskSequence in $taskCommon){Task-Insert InstallExecuteSequence $taskSequence;Task-Insert InstallUISequence $taskSequence}
 foreach($taskSequence in @(@('RepairAll','Installed AND NOT REMOVE',700),@('RepairMode','Installed AND NOT REMOVE',710),@('KeepStartupChoice','EXISTINGSTARTUP',1100),@('WelcomeDlg','NOT REMOVE',1200),@('ProgressDlg',$null,1290),@('ExecuteAction',$null,1300),@('FinishedDlg',$null,-1),@('CancelledDlg',$null,-2),@('FailedDlg',$null,-3))){Task-Insert InstallUISequence $taskSequence}
 foreach($taskSequence in @(@('RepairAll','Installed AND NOT REMOVE',700),@('RepairMode','Installed AND NOT REMOVE',710),@('StartupMode','STARTONLOGIN',1100),@('InstallValidate',$null,1400),@('InstallInitialize',$null,1500),@('ProcessComponents',$null,1600),@('UnpublishFeatures',$null,1800),@('RemoveRegistryValues',$null,2600),@('StopSwitcher','REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE',3050),@('RemoveOwnStartup','REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE',3100),@('RemoveShortcuts',$null,3200),@('RemoveFiles',$null,3500),@('InstallFiles',$null,4000),@('CreateShortcuts',$null,4500),@('WriteRegistryValues',$null,5000),@('RollbackNewStartup','NOT REMOVE AND NOT EXISTINGSTARTUP',5090),@('ConfigureStartup','NOT REMOVE',5100),@('RegisterUser',$null,6000),@('RegisterProduct',$null,6100),@('PublishFeatures',$null,6300),@('PublishProduct',$null,6400),@('InstallExecute',$null,6450),@('RemoveExistingProducts','OLDERPRODUCTS',6500),@('InstallFinalize',$null,6600))){Task-Insert InstallExecuteSequence $taskSequence}
 Task-Insert TextStyle @('DefaultFont','Segoe UI',9,0,0);Task-Insert TextStyle @('HeadingFont','Segoe UI',16,2495028,1)
 foreach($taskDialog in @(@('WelcomeDlg',3,'InstallButton','InstallButton','CancelButton'),@('ProgressDlg',1,'Status','Status',$null),@('FinishedDlg',3,'FinishButton','FinishButton','FinishButton'),@('CancelledDlg',3,'FinishButton','FinishButton','FinishButton'),@('FailedDlg',3,'FinishButton','FinishButton','FinishButton'))){Task-Insert Dialog @($taskDialog[0],50,50,370,230,$taskDialog[1],'GPT Switcher Setup',$taskDialog[2],$taskDialog[3],$taskDialog[4])}
 function Task-Control([string]$Dialog,[string]$Name,[string]$Type,[int]$X,[int]$Y,[int]$Width,[int]$Height,[string]$Text,[string]$Property=$null,[string]$Next=$null){Task-Insert Control @($Dialog,$Name,$Type,$X,$Y,$Width,$Height,3,$Property,$Text,$Next,$null)}
 Task-Control WelcomeDlg Heading Text 20 17 330 30 '{\HeadingFont}GPT Switcher'
 Task-Control WelcomeDlg Detail Text 20 60 330 50 'Install for your Windows user. Desktop and Start menu shortcuts will be created. Exit any running GPT Switcher before updating.'
 Task-Control WelcomeDlg StartupChoice CheckBox 20 120 330 35 'Start GPT Switcher with Windows (quietly in the tray)' STARTONLOGIN InstallButton
 Task-Control WelcomeDlg InstallButton PushButton 185 195 75 22 'Install / repair' $null CancelButton
 Task-Control WelcomeDlg CancelButton PushButton 270 195 75 22 'Cancel' $null StartupChoice
 Task-Insert CheckBox @('STARTONLOGIN','1')
 Task-Insert ControlEvent @('WelcomeDlg','InstallButton','EndDialog','Return','1',1);Task-Insert ControlEvent @('WelcomeDlg','CancelButton','EndDialog','Exit','1',1)
 Task-Control ProgressDlg Heading Text 20 20 330 30 '{\HeadingFont}Setting up GPT Switcher'
 Task-Control ProgressDlg Status Text 20 70 330 40 'Please wait while GPT Switcher is installed.'
 Task-Control ProgressDlg Progress ProgressBar 20 125 330 16 ''
 Task-Insert EventMapping @('ProgressDlg','Progress','SetProgress','Progress');Task-Insert EventMapping @('ProgressDlg','Status','ActionText','Text')
 foreach($taskEnd in @(@('FinishedDlg','Setup completed. Use the desktop shortcut to open GPT Switcher. Sign in through the official client.'),@('CancelledDlg','Setup was cancelled.'),@('FailedDlg','Setup could not complete. Check Windows Installer permissions and .NET Framework 4.8.'))){Task-Control $taskEnd[0] Heading Text 20 20 330 30 '{\HeadingFont}GPT Switcher';Task-Control $taskEnd[0] Detail Text 20 75 330 75 $taskEnd[1];Task-Control $taskEnd[0] FinishButton PushButton 270 195 75 22 'Finish';Task-Insert ControlEvent @($taskEnd[0],'FinishButton','EndDialog','Return','1',1)}
 $taskSummaryKeys=@(1,2,3,4,5,6,7,9,14,15,18,19)
 $taskSummaryValues=@('1252','GPT Switcher Setup','Per-user installation','git-buster','Installer','GPT Switcher program and illustrated guides.','Intel;1033',([Guid]::NewGuid().ToString('B').ToUpperInvariant()),'200','10','Windows Installer','0')
 [SwitcherMsiBuilder]::Summary($taskDatabase,$taskSummaryKeys,$taskSummaryValues)
 [SwitcherMsiBuilder]::Commit($taskDatabase)
}finally{[void][SwitcherMsiBuilder]::MsiCloseHandle($taskDatabase)}
Write-Output 'Built per-user MSI with desktop shortcut and optional tray-only Windows startup. No account data was read.'
Write-Output $taskOutput
