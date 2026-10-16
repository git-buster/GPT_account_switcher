[CmdletBinding()]

param(

    [ValidatePattern('^\d+\.\d+\.\d+(?:-r\d+)?$')][string]$Version='0.6.14'

)

$ErrorActionPreference='Stop'

$taskRoot=[IO.Path]::GetFullPath($PSScriptRoot)

$taskName='ChatGPTDesktopSwitcher-'+$Version

$taskPublic=Join-Path $taskRoot 'GitHub'

$taskSource=Join-Path $taskPublic 'Source'

$taskPortable=Join-Path $taskPublic 'Portable'
$taskInstallerFolder=Join-Path $taskPublic 'Installer'
$taskMsi=Join-Path $taskRoot ('release\ChatGPTDesktopSwitcher-'+$Version+'-Setup.msi')
if(-not(Test-Path -LiteralPath $taskMsi -PathType Leaf)){throw 'Build and validate the installer first.'}

$taskRuntime=Join-Path $taskRoot ('release\'+$taskName)

$taskSourceZip=Join-Path $taskRoot ('release\'+$taskName+'-source.zip')

$taskRuntimeZip=Join-Path $taskRoot ('release\'+$taskName+'.zip')

$taskFolders=@($taskPublic,$taskRuntime)

$taskArchives=@($taskSourceZip,$taskRuntimeZip)

foreach($taskPath in @($taskFolders)+@($taskArchives)) {

    if(-not [IO.Path]::GetFullPath($taskPath).StartsWith($taskRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unexpected output path.'}

    if(Test-Path -LiteralPath $taskPath){throw 'Output exists; nothing overwritten. Move the reviewed output aside before packaging again.'}

}

# Public allowlist only. Never copy the workspace or a profile directory recursively.

$taskFiles=[ordered]@{

 'README.md'='README.md'

 'SECURITY.md'='SECURITY.md'

 'LICENSE'='LICENSE'

 '.gitignore'='desktop\Public.gitignore'

 'build-desktop.ps1'='build-desktop.ps1'

 'build-checks.ps1'='build-checks.ps1'
 'build-installer.ps1'='build-installer.ps1'
 'validate-installer.py'='validate-installer.py'

 'validate-source.ps1'='validate-source.ps1'

 'package-desktop.ps1'='package-desktop.ps1'

 'desktop\Public.gitignore'='desktop\Public.gitignore'

 'desktop\Repository.README.md'='desktop\Repository.README.md'

 'desktop\Repository.gitignore'='desktop\Repository.gitignore'

 'desktop\Public.build.yml'='desktop\Public.build.yml'

 'desktop\Public.issue.md'='desktop\Public.issue.md'

 'desktop\DesktopApp.cs'='desktop\DesktopApp.cs'

 'desktop\Operations.cs'='desktop\Operations.cs'

 'desktop\PopupPanel.cs'='desktop\PopupPanel.cs'

 'desktop\UiTheme.cs'='desktop\UiTheme.cs'

 'desktop\UiText.cs'='desktop\UiText.cs'

 'desktop\ClientFiles.cs'='desktop\ClientFiles.cs'

 'desktop\ProjectLinks.cs'='desktop\ProjectLinks.cs'
 'desktop\ClientDiscovery.cs'='desktop\ClientDiscovery.cs'
 'desktop\HistorySharing.cs'='desktop\HistorySharing.cs'

 'desktop\Checks.cs'='desktop\Checks.cs'

 'desktop\SharedStateCheck.py'='desktop\SharedStateCheck.py'

 'desktop\Dashboard.xaml'='desktop\Dashboard.xaml'

 'desktop\Theme.xaml'='desktop\Theme.xaml'

 'src\app.manifest'='src\app.manifest'

 'src\app.config'='src\app.config'

 'src\icon.ico'='src\icon.ico'

 'src\icon.svg'='src\icon.svg'

 'docs\USAGE.md'='docs\USAGE.md'

 'docs\README.zh-Hans.md'='docs\README.zh-Hans.md'

 'docs\README.zh-Hant.md'='docs\README.zh-Hant.md'

 'docs\README.fr.md'='docs\README.fr.md'

 'docs\README.ja.md'='docs\README.ja.md'

 'docs\images\panel-en.png'='docs\images\panel-en.png'

 'docs\images\panel-zh-Hans.png'='docs\images\panel-zh-Hans.png'

 'docs\images\panel-zh-Hant.png'='docs\images\panel-zh-Hant.png'

 'docs\images\panel-fr.png'='docs\images\panel-fr.png'

 'docs\images\panel-ja.png'='docs\images\panel-ja.png'

 'docs\images\settings-en.png'='docs\images\settings-en.png'

 'docs\images\app-en.png'='docs\images\app-en.png'

 'docs\images\sharing-en.png'='docs\images\sharing-en.png'

 'docs\images\quota-en.png'='docs\images\quota-en.png'

 'docs\images\about-en.png'='docs\images\about-en.png'
 'docs\images\history-en.png'='docs\images\history-en.png'
 'docs\images\history-zh-Hans.png'='docs\images\history-zh-Hans.png'
 'docs\images\history-zh-Hant.png'='docs\images\history-zh-Hant.png'
 'docs\images\history-fr.png'='docs\images\history-fr.png'
 'docs\images\history-ja.png'='docs\images\history-ja.png'

}

$taskRootFiles=[ordered]@{

 'README.md'='desktop\Repository.README.md'

 'LICENSE'='LICENSE'

 'SECURITY.md'='SECURITY.md'

 '.gitignore'='desktop\Repository.gitignore'

 '.github\workflows\build.yml'='desktop\Public.build.yml'

 '.github\ISSUE_TEMPLATE\bug_report.md'='desktop\Public.issue.md'

}

$taskExe=Join-Path $taskRoot 'dist\ChatGPTAccountSwitcher.exe'

$taskConfig=$taskExe+'.config'

    if(-not(Test-Path -LiteralPath $taskExe)-or -not(Test-Path -LiteralPath $taskConfig)){throw 'Build the desktop app first.'}

    $taskBuildVersion=($Version -replace '-r\d+$','')+'.0'

    if([Diagnostics.FileVersionInfo]::GetVersionInfo($taskExe).FileVersion -ne $taskBuildVersion){throw 'Executable version does not match the requested package version.'}

foreach($taskFile in @($taskFiles.Values)+@($taskRootFiles.Values)) {

    $taskOrigin=Join-Path $taskRoot $taskFile

    if(-not(Test-Path -LiteralPath $taskOrigin -PathType Leaf)){throw 'A required public source file is missing.'}

    for($taskInfo=Get-Item -LiteralPath $taskOrigin;$null -ne $taskInfo;$taskInfo=$(if($taskInfo -is [IO.FileInfo]){$taskInfo.Directory}else{$taskInfo.Parent})){

        if(($taskInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Source path contains a reparse point.'}

    }

}

foreach($taskFolder in $taskFolders){[IO.Directory]::CreateDirectory($taskFolder) | Out-Null}

[IO.Directory]::CreateDirectory($taskSource) | Out-Null

[IO.Directory]::CreateDirectory($taskPortable) | Out-Null
[IO.Directory]::CreateDirectory($taskInstallerFolder) | Out-Null

[IO.Directory]::CreateDirectory((Join-Path $taskRoot 'release')) | Out-Null

foreach($taskEntry in $taskFiles.GetEnumerator()) {

    $taskTarget=Join-Path $taskSource $taskEntry.Key

    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskTarget)) | Out-Null

    Copy-Item -LiteralPath (Join-Path $taskRoot $taskEntry.Value) -Destination $taskTarget

}

$taskPublication=@('# Public source package','',('Version: '+$Version),'Author: git-buster','','Buildable source, guides and interface illustrations.','', '## Included files','')

$taskPublication+=@($taskFiles.Keys | ForEach-Object {'- '+$_.Replace('\','/')})

[IO.File]::WriteAllLines((Join-Path $taskSource 'PUBLICATION.md'),$taskPublication,[Text.UTF8Encoding]::new($false))

$taskDocs=@($taskFiles.Keys | Where-Object {$_.StartsWith('docs\')})

    Copy-Item -LiteralPath $taskExe -Destination (Join-Path $taskRuntime 'ChatGPTAccountSwitcher.exe')

    Copy-Item -LiteralPath $taskConfig -Destination (Join-Path $taskRuntime 'ChatGPTAccountSwitcher.exe.config')

    foreach($taskDoc in @('README.md','SECURITY.md')){Copy-Item -LiteralPath (Join-Path $taskRoot $taskDoc) -Destination (Join-Path $taskRuntime $taskDoc)}

    Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskRuntime 'LICENSE')

    foreach($taskDoc in $taskDocs){

        $taskTarget=Join-Path $taskRuntime $taskDoc

        [IO.Directory]::CreateDirectory((Split-Path -Parent $taskTarget)) | Out-Null

        Copy-Item -LiteralPath (Join-Path $taskRoot $taskDoc) -Destination $taskTarget

    }

    $taskHashLines=@([IO.Directory]::EnumerateFiles($taskRuntime,'*',[IO.SearchOption]::AllDirectories) | Sort-Object | ForEach-Object {((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+$_.Substring($taskRuntime.Length+1).Replace('\','/')})

    [IO.File]::WriteAllLines((Join-Path $taskRuntime 'SHA256.txt'),$taskHashLines,[Text.UTF8Encoding]::new($false))

    $taskRuntimeExpected=@(@('ChatGPTAccountSwitcher.exe','ChatGPTAccountSwitcher.exe.config','README.md','SECURITY.md','LICENSE','SHA256.txt')+@($taskDocs | ForEach-Object {$_.Replace('\','/')})) | Sort-Object

    foreach($taskRelative in $taskRuntimeExpected){

        $taskTarget=Join-Path $taskPortable $taskRelative

        [IO.Directory]::CreateDirectory((Split-Path -Parent $taskTarget)) | Out-Null

        Copy-Item -LiteralPath (Join-Path $taskRuntime $taskRelative) -Destination $taskTarget

    }

foreach($taskEntry in $taskRootFiles.GetEnumerator()){

    $taskTarget=Join-Path $taskPublic $taskEntry.Key

    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskTarget)) | Out-Null

    if($taskEntry.Key -eq 'SECURITY.md'){

        $taskText=[IO.File]::ReadAllText((Join-Path $taskRoot $taskEntry.Value)).Replace('(docs/USAGE.md)','(Source/docs/USAGE.md)')

        [IO.File]::WriteAllText($taskTarget,$taskText,[Text.UTF8Encoding]::new($false))

    }else{Copy-Item -LiteralPath (Join-Path $taskRoot $taskEntry.Value) -Destination $taskTarget}

}

$taskExpected=@(@($taskFiles.Keys | ForEach-Object {$_.Replace('\','/')})+'PUBLICATION.md') | Sort-Object

$taskInstallerExpected=@('ChatGPTDesktopSwitcher-Setup.msi','README.md','LICENSE','SECURITY.md','SHA256.txt')
Copy-Item -LiteralPath $taskMsi -Destination (Join-Path $taskInstallerFolder 'ChatGPTDesktopSwitcher-Setup.msi')
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination (Join-Path $taskInstallerFolder 'LICENSE')
[IO.File]::WriteAllText((Join-Path $taskInstallerFolder 'SECURITY.md'),[IO.File]::ReadAllText((Join-Path $taskRoot 'SECURITY.md')).Replace('(docs/USAGE.md)','(../Source/docs/USAGE.md)'),[Text.UTF8Encoding]::new($false))
$taskInstallerReadme=@('# GPT Switcher installer','','Author: git-buster','','Double-click **ChatGPTDesktopSwitcher-Setup.msi**. Installs for the current Windows user, with desktop and Start menu shortcuts. Requires Windows 10/11, .NET Framework 4.8 and one complete supported official ChatGPT client.','','Start with Windows is optional and initially off. It can also be changed in app settings; it starts only the switcher tray icon, without account windows. Uninstall from Windows Installed apps; account data and history are retained.','','The first detected client path is saved locally. If it stops working after an update or move, use **Accounts and settings → Auto detect → Save settings**. Choose the installed or portable edition, rather than running both copies.','','[English guide](https://github.com/git-buster/GPT_account_switcher/blob/main/Source/docs/USAGE.md) · [简体中文](https://github.com/git-buster/GPT_account_switcher/blob/main/Source/docs/README.zh-Hans.md) · [繁體中文](https://github.com/git-buster/GPT_account_switcher/blob/main/Source/docs/README.zh-Hant.md) · [Français](https://github.com/git-buster/GPT_account_switcher/blob/main/Source/docs/README.fr.md) · [日本語](https://github.com/git-buster/GPT_account_switcher/blob/main/Source/docs/README.ja.md)','','[Project and feedback](https://github.com/git-buster/GPT_account_switcher/issues).')
[IO.File]::WriteAllLines((Join-Path $taskInstallerFolder 'README.md'),$taskInstallerReadme,[Text.UTF8Encoding]::new($false))
$taskInstallerHashes=@($taskInstallerExpected | Where-Object {$_ -ne 'SHA256.txt'} | ForEach-Object {((Get-FileHash -LiteralPath (Join-Path $taskInstallerFolder $_) -Algorithm SHA256).Hash.ToLowerInvariant())+'  '+$_})
[IO.File]::WriteAllLines((Join-Path $taskInstallerFolder 'SHA256.txt'),$taskInstallerHashes,[Text.UTF8Encoding]::new($false))
$taskPublicExpected=@(@($taskExpected | ForEach-Object {'Source/'+$_})+@($taskRuntimeExpected | ForEach-Object {'Portable/'+$_})+@($taskInstallerExpected | ForEach-Object {'Installer/'+$_})+@($taskRootFiles.Keys | ForEach-Object {$_.Replace('\','/')})+'PUBLICATION.md') | Sort-Object

$taskPublicManifest=@('# Public distribution','',('Version: '+$Version),'Author: git-buster','','Source/ contains buildable source. Portable/ contains the runnable Windows program and guides. Installer/ contains the per-user MSI.','','## Included files','')+@($taskPublicExpected | ForEach-Object {'- '+$_})

[IO.File]::WriteAllLines((Join-Path $taskPublic 'PUBLICATION.md'),$taskPublicManifest,[Text.UTF8Encoding]::new($false))

$taskPatterns=@(

 '(?i)[A-Z]:(?:\\{1,2}|/)(?:Users|Documents and Settings)(?:\\{1,2}|/)(?!Public(?:\\{1,2}|/)|Default(?:\\{1,2}|/))[^\\/\s"'']+'

 '(?i)/(?:home|Users)/[^/\s"'']+'

 '(?<![A-Za-z0-9])[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'

 'eyJ[A-Za-z0-9_-]{30,}\.[A-Za-z0-9_-]{30,}\.[A-Za-z0-9_-]{10,}'

 'sk-(?:proj-)?[A-Za-z0-9_-]{30,}'

 '(?:ghp_|github_pat_)[A-Za-z0-9_]{30,}'

 '\b(?:Asia|Africa|America|Europe|Australia|Pacific|Atlantic|Indian)/[A-Za-z_]+\b'

)

# Compare current host metadata in memory only; never write its values into the report.

$taskHostValues=@($env:USERPROFILE,$env:COMPUTERNAME,[TimeZoneInfo]::Local.Id,[Globalization.CultureInfo]::CurrentCulture.Name,[Globalization.CultureInfo]::CurrentUICulture.Name) | Where-Object {$_ -and $_.Length -ge 4} | Select-Object -Unique

$taskShortUserRule=$null

if($env:USERNAME){

    $taskUserRule='(?i)(?<![A-Za-z0-9_])'+[regex]::Escape($env:USERNAME)+'(?![A-Za-z0-9_])'

    $taskPatterns+=$taskUserRule

    if($env:USERNAME.Length -lt 4){$taskShortUserRule=$taskUserRule}

}

foreach($taskHostValue in $taskHostValues){$taskPatterns+=[regex]::Escape($taskHostValue)}

$taskChecked=0

function Assert-DocumentationPng([byte[]]$Bytes) {

    if($Bytes.Length -lt 33 -or [BitConverter]::ToString($Bytes,0,8) -ne '89-50-4E-47-0D-0A-1A-0A'){throw 'Invalid documentation image.'}

    $taskOffset=8L; $taskImageData=$false; $taskImageEnd=$false

    while($taskOffset -lt $Bytes.Length){

        if($taskOffset+12 -gt $Bytes.Length){throw 'Truncated image.'}

        $taskLength=$Bytes[$taskOffset]*16777216L+$Bytes[$taskOffset+1]*65536L+$Bytes[$taskOffset+2]*256L+$Bytes[$taskOffset+3]

        if($taskOffset+12+$taskLength -gt $Bytes.Length){throw 'Invalid image chunk.'}

        $taskKind=[Text.Encoding]::ASCII.GetString($Bytes,[int]($taskOffset+4),4)

        if($taskKind -notin @('IHDR','IDAT','IEND','sRGB','gAMA','pHYs')){throw 'Image text, EXIF or other unexpected metadata is not allowed.'}

        if($taskOffset -eq 8 -and ($taskKind -ne 'IHDR' -or $taskLength -ne 13)){throw 'Invalid image header.'}

        if($taskKind -eq 'IHDR' -and $taskOffset -ne 8){throw 'Duplicate image header.'}

        if($taskKind -eq 'IDAT'){$taskImageData=$true}

        if($taskKind -eq 'IEND'){

            if($taskLength -ne 0 -or $taskOffset+12 -ne $Bytes.Length){throw 'Trailing image data is not allowed.'}

            $taskImageEnd=$true

        }

        $taskOffset+=12+$taskLength

    }

    if(-not $taskImageData -or -not $taskImageEnd){throw 'Incomplete image.'}

}

foreach($taskFolder in $taskFolders) {

    foreach($taskFile in [IO.Directory]::EnumerateFiles($taskFolder,'*',[IO.SearchOption]::AllDirectories)) {

        if([IO.Path]::GetExtension($taskFile) -in @('.json','.jsonl','.dat','.db','.sqlite','.sqlite3','.dmp','.pdb','.log','.lnk')){throw 'A forbidden data file was found.'}

        $taskChecked++; $taskBytes=[IO.File]::ReadAllBytes($taskFile)

        $taskIsPng=[IO.Path]::GetExtension($taskFile) -eq '.png'

        if($taskIsPng){Assert-DocumentationPng $taskBytes}

        foreach($taskEncoding in @([Text.Encoding]::UTF8,[Text.Encoding]::Unicode)) {

            $taskText=$taskEncoding.GetString($taskBytes)

            foreach($taskPattern in $taskPatterns) {

                # Compressed pixel bytes can randomly contain a short host identifier.

                # PNG text/EXIF and trailing data are forbidden above; the fixed, fictional

                # image allowlist is also visually reviewed. All longer secret rules remain.

                if(($taskIsPng -or [IO.Path]::GetExtension($taskFile) -eq '.msi') -and $taskPattern -eq $taskShortUserRule){continue}

                foreach($taskMatch in [regex]::Matches($taskText,$taskPattern,[Text.RegularExpressions.RegexOptions]::IgnoreCase)) {

                    if($taskMatch.Value -match '@example\.invalid$'){continue}

                    if($taskPattern -eq $taskShortUserRule -and [IO.Path]::GetExtension($taskFile) -in @('.exe','.ico','.png')){

                        # Short identifiers can occur as machine-code bytes. Check readable strings instead.

                        # Source/text, full personal paths, emails, credentials and longer host values stay strict.

                        $taskReadable=@([regex]::Matches($taskText,'[\x20-\x7E]{4,}') | Where-Object {$_.Index -le $taskMatch.Index -and $_.Index+$_.Length -ge $taskMatch.Index+$taskMatch.Length})

                        if($taskReadable.Count -eq 0){continue}

                    }

                    throw ('Privacy scan rejected public file '+$taskFile.Substring($taskFolder.Length+1).Replace('\','/')+' (rule '+[Array]::IndexOf($taskPatterns,$taskPattern)+'). Matched values are not printed; no archive was produced.')

                }

            }

        }

    }

}

function Assert-PublicFiles([string]$Folder,[string[]]$Expected) {

    $taskActual=@([IO.Directory]::EnumerateFiles($Folder,'*',[IO.SearchOption]::AllDirectories) | ForEach-Object {$_.Substring($Folder.Length+1).Replace('\','/')}) | Sort-Object

    if(@(Compare-Object $Expected $taskActual).Count -ne 0){throw 'File list does not match the public allowlist.'}

}

Assert-PublicFiles $taskSource $taskExpected

Assert-PublicFiles $taskPublic $taskPublicExpected

Assert-PublicFiles $taskRuntime $taskRuntimeExpected

Add-Type -AssemblyName System.IO.Compression

Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-PublicArchive([string]$Folder,[string[]]$Expected,[string]$Destination,[string]$Prefix) {

    $taskArchive=[IO.Compression.ZipFile]::Open($Destination,[IO.Compression.ZipArchiveMode]::Create)

    try {

        foreach($taskRelative in $Expected) {

            $taskEntry=$taskArchive.CreateEntry($Prefix+$taskRelative,[IO.Compression.CompressionLevel]::Optimal)

            # Fixed date prevents local file modification times and host timezone fingerprints in ZIP metadata.

            $taskEntry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)

            $taskInput=[IO.File]::OpenRead((Join-Path $Folder $taskRelative)); $taskOutput=$taskEntry.Open()

            try{$taskInput.CopyTo($taskOutput)}finally{$taskOutput.Dispose();$taskInput.Dispose()}

        }

    } finally {$taskArchive.Dispose()}

    $taskArchive=[IO.Compression.ZipFile]::OpenRead($Destination)

    try {

        $taskEntries=@($taskArchive.Entries | ForEach-Object {$_.FullName}) | Sort-Object

        $taskWanted=@($Expected | ForEach-Object {$Prefix+$_}) | Sort-Object

        if(@(Compare-Object $taskWanted $taskEntries).Count -ne 0){throw 'Archive file list does not match the allowlist.'}

    } finally {$taskArchive.Dispose()}

}

    New-PublicArchive $taskRuntime $taskRuntimeExpected $taskRuntimeZip ''

New-PublicArchive $taskSource $taskExpected $taskSourceZip 'Source/'

$taskAudit=@('# Public package audit','',('Version: '+$Version),'Author: git-buster',('Source files: '+$taskExpected.Count),('Public files scanned: '+$taskChecked),'','- Public allowlists and archive entries verified.','- No JSON files, account stores, credentials, cookies or project/history data.','- No detected personal paths, real emails or secret-token patterns.','- No current host identity, display-language or timezone values.','- Documentation images rendered from fictional data only.','- ZIP entry dates normalized.','- Runtime SHA256 inventory generated when a program package is included.','- Not uploaded to GitHub.','','Scope: fixed public outputs and detectable patterns; not a guarantee against every possible secret format.')

[IO.File]::WriteAllLines((Join-Path $taskRoot ('release\'+$taskName+'-privacy-audit.md')),$taskAudit,[Text.UTF8Encoding]::new($false))

Write-Output ('Public package verified: '+$taskExpected.Count+' source files; '+$taskChecked+' scanned files; no detected private data.')

