$ErrorActionPreference='Stop'
$taskRoot=$PSScriptRoot
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskFramework=Split-Path -Parent $taskCompiler
$taskApp=Join-Path $taskRoot 'dist\ChatGPTAccountSwitcher.exe'
if(-not(Test-Path -LiteralPath $taskApp)){throw 'Build the desktop app first.'}
$taskArgs=@('/nologo','/target:exe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',
 ('/win32manifest:'+(Join-Path $taskRoot 'src\app.manifest')),
 ('/out:'+(Join-Path $taskRoot 'dist\SwitcherChecks.exe')),
 ('/reference:'+$taskApp),
 ('/reference:'+(Join-Path $taskFramework 'WPF\PresentationFramework.dll')),
 ('/reference:'+(Join-Path $taskFramework 'WPF\PresentationCore.dll')),
 ('/reference:'+(Join-Path $taskFramework 'WPF\WindowsBase.dll')),
 ('/reference:'+(Join-Path $taskFramework 'System.Xaml.dll')),
 '/reference:System.Xml.dll','/reference:System.Management.dll','/reference:System.dll','/reference:System.Core.dll','/reference:System.Security.dll',
 '/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',
 (Join-Path $taskRoot 'desktop\Checks.cs'))
& $taskCompiler @taskArgs
if($LASTEXITCODE -ne 0){throw 'Development checker build failed.'}
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\app.config') -Destination (Join-Path $taskRoot 'dist\SwitcherChecks.exe.config')
Write-Output (Join-Path $taskRoot 'dist\SwitcherChecks.exe')
