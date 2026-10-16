# Compile-only validation: never loads or executes the resulting assemblies.
$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
foreach($taskScript in @('build-desktop.ps1','build-checks.ps1','build-installer.ps1','validate-source.ps1','package-desktop.ps1')) {
    $taskBytes=[IO.File]::ReadAllBytes((Join-Path $taskRoot $taskScript))
    if(@($taskBytes | Where-Object {$_ -gt 127}).Count -gt 0 -and -not($taskBytes.Length -ge 3 -and $taskBytes[0] -eq 239 -and $taskBytes[1] -eq 187 -and $taskBytes[2] -eq 191)){throw 'A non-ASCII Windows PowerShell script must declare UTF-8 with a BOM.'}
}
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskFramework=Split-Path -Parent $taskCompiler
$taskOutput=Join-Path $taskRoot 'artifacts\source-validation'
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskReferences=@(
 ('/reference:'+(Join-Path $taskFramework 'WPF\PresentationFramework.dll')),
 ('/reference:'+(Join-Path $taskFramework 'WPF\PresentationCore.dll')),
 ('/reference:'+(Join-Path $taskFramework 'WPF\WindowsBase.dll')),
 ('/reference:'+(Join-Path $taskFramework 'System.Xaml.dll')),
 '/reference:System.Xml.dll','/reference:System.Management.dll','/reference:System.dll','/reference:System.Core.dll','/reference:System.Security.dll',
 '/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll'
)
$taskLibrary=Join-Path $taskOutput 'SourceValidation.dll'
$taskArgs=@('/nologo','/target:library','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',
 ('/out:'+$taskLibrary),
 ('/win32manifest:'+(Join-Path $taskRoot 'src\app.manifest')),
 ('/win32icon:'+(Join-Path $taskRoot 'src\icon.ico')),
 ('/resource:'+(Join-Path $taskRoot 'desktop\Dashboard.xaml')+',Dashboard.xaml'),
 ('/resource:'+(Join-Path $taskRoot 'src\icon.ico')+',Switcher.ico'),
 ('/resource:'+(Join-Path $taskRoot 'desktop\Theme.xaml')+',Theme.xaml'))+$taskReferences+@(
 (Join-Path $taskRoot 'desktop\DesktopApp.cs'),(Join-Path $taskRoot 'desktop\Operations.cs'),(Join-Path $taskRoot 'desktop\PopupPanel.cs'),(Join-Path $taskRoot 'desktop\UiTheme.cs'),(Join-Path $taskRoot 'desktop\UiText.cs'),(Join-Path $taskRoot 'desktop\ClientFiles.cs'),(Join-Path $taskRoot 'desktop\ClientDiscovery.cs'),(Join-Path $taskRoot 'desktop\ProjectLinks.cs'),(Join-Path $taskRoot 'desktop\HistorySharing.cs'))
& $taskCompiler @taskArgs
if($LASTEXITCODE -ne 0){throw 'Product source compilation failed.'}
$taskArgs=@('/nologo','/target:library','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',
 ('/out:'+(Join-Path $taskOutput 'ChecksValidation.dll')),('/reference:'+$taskLibrary))+$taskReferences+@(Join-Path $taskRoot 'desktop\Checks.cs')
& $taskCompiler @taskArgs
if($LASTEXITCODE -ne 0){throw 'Checker source compilation failed.'}
$null=[xml][IO.File]::ReadAllText((Join-Path $taskRoot 'desktop\Dashboard.xaml'))
$null=[xml][IO.File]::ReadAllText((Join-Path $taskRoot 'desktop\Theme.xaml'))
$null=[xml][IO.File]::ReadAllText((Join-Path $taskRoot 'src\app.manifest'))
$null=[xml][IO.File]::ReadAllText((Join-Path $taskRoot 'src\app.config'))
Write-Output 'Product and checker sources compile; XML parses. No assembly was executed and no official client was started or stopped.'
