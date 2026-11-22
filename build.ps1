# Builds bin\Lumen.exe with the C# compiler that ships with Windows (.NET Framework 4.8) - nothing to install.
#   .\build.ps1          build
#   .\build.ps1 -Run     build, stop any running copy, and start the new one
param([switch]$Run)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$wpf = Join-Path $fw 'WPF'
$out = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $out | Out-Null

$ico = Join-Path $root 'assets\lumen.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'assets\make-icon.ps1') -Path $ico | Out-Null }

if ($Run) { Get-Process Lumen -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Milliseconds 300 }

$refs = "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll", "$fw\System.Xaml.dll",
        'System.dll', 'System.Core.dll', 'System.Data.dll', 'System.Xml.dll', 'System.Drawing.dll',
        'System.Windows.Forms.dll', 'Microsoft.CSharp.dll'
$cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/warn:3', '/nowarn:1607',
             "/out:$out\Lumen.exe", "/win32icon:$ico", "/win32manifest:$root\assets\app.manifest",
             "/resource:$root\defaults\commands.json,Lumen.commands.json",
             "/resource:$root\defaults\settings.json,Lumen.settings.json",
             "/resource:$ico,Lumen.ico") +
           ($refs | ForEach-Object { "/r:$_" }) +
           (Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName })

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)" }
"Built $out\Lumen.exe"
if ($Run) { Start-Process (Join-Path $out 'Lumen.exe'); "Started Lumen - press Ctrl+Space" }
