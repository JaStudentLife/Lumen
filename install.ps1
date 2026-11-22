# Installs Lumen for the current user (no admin needed).
#   .\install.ps1              copy to %LOCALAPPDATA%\Programs\Lumen, add a Start menu shortcut, start it
#   .\install.ps1 -Startup     ...and start Lumen automatically when you sign in
#   .\install.ps1 -Uninstall   remove the app, shortcut and autostart entry (keeps your settings in %APPDATA%\Lumen)
param([switch]$Startup, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Lumen'
$exe = Join-Path $dest 'Lumen.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Lumen.lnk'
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

Get-Process Lumen -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

if ($Uninstall) {
    Remove-ItemProperty -Path $run -Name Lumen -ErrorAction SilentlyContinue
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    "Lumen removed. Your settings are still in $env:APPDATA\Lumen"
    return
}

$src = Join-Path $PSScriptRoot 'bin\Lumen.exe'
if (-not (Test-Path $src)) { & (Join-Path $PSScriptRoot 'build.ps1') | Out-Null }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $src $exe -Force
Copy-Item (Join-Path $PSScriptRoot 'README.md') $dest -Force -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.Description = 'Lumen - Spotlight-style search (Ctrl+Space)'
$s.Save()

if ($Startup) { Set-ItemProperty -Path $run -Name Lumen -Value ('"' + $exe + '" --background') }
elseif ((Get-ItemProperty -Path $run -Name Lumen -ErrorAction SilentlyContinue)) {
    # keep an existing autostart entry pointing at the installed copy
    Set-ItemProperty -Path $run -Name Lumen -Value ('"' + $exe + '" --background')
}

Start-Process $exe -ArgumentList '--background'
"Installed to $dest - press Ctrl+Space to search."
