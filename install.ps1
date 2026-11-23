#Requires -Version 5.1
<#
    Installs this komorebi setup for the current user.

    Copies:
      komorebi/komorebi.json      -> %USERPROFILE%\komorebi.json
      komorebi/applications.json  -> %USERPROFILE%\applications.json
      whkd/whkdrc                 -> %USERPROFILE%\.config\whkdrc
      scripts/*.ps1               -> %LOCALAPPDATA%\Programs\
      WorkspaceTray/*             -> %LOCALAPPDATA%\Programs\WorkspaceTray\  (+ build)

    Also (re)creates the Startup shortcut that launches whkd and the taskbar
    workspace strip at logon, and registers the "komorebi" scheduled task that
    starts komorebi elevated (UAC prompt).
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
Copy-Item (Join-Path $root 'scripts\komorebi-elevated.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\install-komorebi-task.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\launch-focused.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\move-to-monitor.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\komorebi-watchdog.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\install-watchdog-task.ps1') $programs -Force
Copy-Item (Join-Path $root 'scripts\install-tray-task.ps1') $programs -Force
Copy-Item (Join-Path $root 'WorkspaceTray\WorkspaceTray.cs') $trayDir -Force
Copy-Item (Join-Path $root 'WorkspaceTray\app.manifest')     $trayDir -Force
Copy-Item (Join-Path $root 'WorkspaceTray\build.ps1')        $trayDir -Force
Write-Host "copied configuration files"

Write-Host 'compiling WorkspaceTray...'
$runningTray = Get-Process WorkspaceTray -ErrorAction SilentlyContinue
if ($runningTray) {
    Write-Host 'stopping running WorkspaceTray (the exe is locked while it runs)'
    $runningTray | Stop-Process -Force
    Start-Sleep -Milliseconds 600
}
& (Join-Path $trayDir 'build.ps1') | Out-Host

$lnkPath  = Join-Path ([Environment]::GetFolderPath('Startup')) 'komorebi.lnk'
$wsh      = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($lnkPath)
$shortcut.TargetPath       = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shortcut.Arguments        = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$programs\komorebi-start.ps1`""
$shortcut.WorkingDirectory = $userProfile
$shortcut.IconLocation     = "$komorebic,0"
$shortcut.Description      = 'Start whkd and the taskbar workspace strip (komorebi itself is started by the komorebi scheduled task)'
$shortcut.Save()
Write-Host "startup shortcut: $lnkPath"

Write-Host 'registering the komorebi scheduled task (highest privileges, UAC prompt)'
& (Join-Path $root 'scripts\install-komorebi-task.ps1')

Write-Host 'registering the tray scheduled task'
& (Join-Path $programs 'install-tray-task.ps1')

Write-Host 'registering the watchdog scheduled task (every minute)'
& (Join-Path $programs 'install-watchdog-task.ps1')

Write-Host ''
Write-Host 'Installed. To apply now:'
Write-Host '  1. komorebic stop --whkd'
Write-Host '  2. Start-ScheduledTask komorebi, then double-click the Startup shortcut'
Write-Host '     (or simply sign out and back in)'
