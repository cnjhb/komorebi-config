#Requires -Version 5.1
<#
    Installs this komorebi setup for the current user.

    Copies:
      komorebi/komorebi.json      -> %USERPROFILE%\komorebi.json
      komorebi/applications.json  -> %USERPROFILE%\applications.json
      whkd/whkdrc                 -> %USERPROFILE%\.config\whkdrc
      scripts/*.ps1               -> %LOCALAPPDATA%\Programs\
      WorkspaceTray/*             -> %LOCALAPPDATA%\Programs\WorkspaceTray\  (+ build)

    Also (re)creates the Startup shortcut that launches komorebi, whkd and the
    taskbar workspace strip at logon.
#>

$ErrorActionPreference = 'Stop'

$root        = $PSScriptRoot
$userProfile = $env:USERPROFILE
$programs    = Join-Path $env:LOCALAPPDATA 'Programs'
$trayDir     = Join-Path $programs 'WorkspaceTray'
$configDir   = Join-Path $userProfile '.config'

Write-Host '== komorebi-config installer =='

$komorebic = Join-Path $env:ProgramFiles 'komorebi\bin\komorebic.exe'
$whkdExe   = Join-Path $env:ProgramFiles 'whkd\bin\whkd.exe'
if (-not (Test-Path $komorebic)) { Write-Warning "komorebi not found: $komorebic" }
if (-not (Test-Path $whkdExe))   { Write-Warning "whkd not found: $whkdExe" }

foreach ($dir in @($programs, $trayDir, $configDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
    }
}

Copy-Item (Join-Path $root 'komorebi\komorebi.json')     (Join-Path $userProfile 'komorebi.json') -Force
Copy-Item (Join-Path $root 'komorebi\applications.json') (Join-Path $userProfile 'applications.json') -Force
Copy-Item (Join-Path $root 'whkd\whkdrc')                (Join-Path $configDir 'whkdrc') -Force
Copy-Item (Join-Path $root 'scripts\komorebi-start.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\launch-focused.ps1') $programs -Force
Copy-Item (Join-Path $root 'WorkspaceTray\WorkspaceTray.cs') $trayDir -Force
Copy-Item (Join-Path $root 'WorkspaceTray\app.manifest')     $trayDir -Force
Copy-Item (Join-Path $root 'WorkspaceTray\build.ps1')        $trayDir -Force
Write-Host "copied configuration files"

Write-Host 'compiling WorkspaceTray...'
& (Join-Path $trayDir 'build.ps1') | Out-Host

$lnkPath  = Join-Path ([Environment]::GetFolderPath('Startup')) 'komorebi.lnk'
$wsh      = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($lnkPath)
$shortcut.TargetPath       = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcut.Arguments        = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$programs\komorebi-start.ps1`""
$shortcut.WorkingDirectory = $userProfile
$shortcut.IconLocation     = "$komorebic,0"
$shortcut.Description      = 'Start komorebi, whkd and the taskbar workspace strip'
$shortcut.Save()
Write-Host "startup shortcut: $lnkPath"

Write-Host ''
Write-Host 'Installed. To apply now:'
Write-Host '  1. komorebic stop --whkd --bar'
Write-Host '  2. double-click the Startup shortcut (or sign out and back in)'
