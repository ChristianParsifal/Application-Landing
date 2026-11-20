$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$references = @(
    'System.Xaml.dll', 'System.Windows.Forms.dll', 'System.IO.Compression.dll',
    'System.IO.Compression.FileSystem.dll', 'WPF\PresentationFramework.dll',
    'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll'
) | ForEach-Object { '/reference:' + (Join-Path $framework $_) }
$source = Join-Path $PSScriptRoot 'App.cs'
$engine = Join-Path $PSScriptRoot 'Engine.cs'
$xaml = Join-Path $PSScriptRoot 'Main.xaml'
$output = Join-Path $PSScriptRoot 'AppLanding.exe'
$icon = Join-Path $PSScriptRoot 'AppIcon.ico'
& (Join-Path $framework 'csc.exe') /nologo /target:winexe /codepage:65001 /optimize+ ('/out:' + $output) ('/win32icon:' + $icon) ('/resource:' + $icon + ',AppIcon.ico') ('/resource:' + $xaml + ',Main.xaml') $references $source $engine
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)" }
Write-Output $output
