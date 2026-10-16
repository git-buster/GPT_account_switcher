$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$taskFramework = Split-Path -Parent $taskCompiler
$taskOutput = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskArgs = @('/nologo','/target:winexe','/platform:anycpu','/optimize+','/utf8output','/codepage:65001',
 ('/out:'+(Join-Path $taskOutput 'ChatGPTAccountSwitcher.exe')),
 ('/win32manifest:'+(Join-Path $taskRoot 'src\app.manifest')),
 ('/win32icon:'+(Join-Path $taskRoot 'src\icon.ico')),
 ('/resource:'+(Join-Path $taskRoot 'desktop\Dashboard.xaml')+',Dashboard.xaml'),
 ('/resource:'+(Join-Path $taskRoot 'src\icon.ico')+',Switcher.ico'),
 ('/resource:'+(Join-Path $taskRoot 'desktop\Theme.xaml')+',Theme.xaml'),
 ('/reference:'+(Join-Path $taskFramework 'WPF\PresentationFramework.dll')),
 ('/reference:'+(Join-Path $taskFramework 'WPF\PresentationCore.dll')),
 ('/reference:'+(Join-Path $taskFramework 'WPF\WindowsBase.dll')),
 ('/reference:'+(Join-Path $taskFramework 'System.Xaml.dll')),
 '/reference:System.Xml.dll','/reference:System.Management.dll','/reference:System.dll','/reference:System.Core.dll','/reference:System.Security.dll',
 '/reference:System.Web.Extensions.dll','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',
 (Join-Path $taskRoot 'desktop\DesktopApp.cs'),
 (Join-Path $taskRoot 'desktop\Operations.cs'),
 (Join-Path $taskRoot 'desktop\PopupPanel.cs'),
 (Join-Path $taskRoot 'desktop\UiTheme.cs'),
 (Join-Path $taskRoot 'desktop\UiText.cs'),(Join-Path $taskRoot 'desktop\ClientFiles.cs'),(Join-Path $taskRoot 'desktop\ClientDiscovery.cs'),(Join-Path $taskRoot 'desktop\ProjectLinks.cs'),(Join-Path $taskRoot 'desktop\HistorySharing.cs'))
& $taskCompiler @taskArgs
if ($LASTEXITCODE -ne 0) { throw 'Desktop panel build failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'src\app.config') -Destination (Join-Path $taskOutput 'ChatGPTAccountSwitcher.exe.config')
Write-Output (Join-Path $taskOutput 'ChatGPTAccountSwitcher.exe')
