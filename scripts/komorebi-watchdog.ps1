# Keeps the komorebi helpers alive.
#
# komorebi has no supervision of its own and whkd is a plain console app, so a
# silent death of the tray (which also draws the taskbar capsule and starts
# whkd) leaves the desktop looking normal while every hotkey is dead. This
# script is run every minute by the "komorebi-watchdog" scheduled task:
#
#   - komorebi missing      -> start the elevated "komorebi" scheduled task
#   - WorkspaceTray missing -> start it (it starts whkd and draws the capsule)
#
# A deliberate "退出" from the tray menu writes %USERPROFILE%\.workspace-tray-quit
# and is respected until the next logon, so the watchdog does not fight the user.

$ErrorActionPreference = 'Continue'

$log  = Join-Path $env:USERPROFILE 'komorebi-watchdog.log'
$flag = Join-Path $env:USERPROFILE '.workspace-tray-quit'
$tray = Join-Path $env:LOCALAPPDATA 'Programs\WorkspaceTray\WorkspaceTray.exe'

function Log([string]$message)
{
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $message" | Out-File $log -Append -Encoding utf8
}

if (-not (Get-Process komorebi -ErrorAction SilentlyContinue))
{
    Log 'komorebi is not running; starting the scheduled task'
    Start-ScheduledTask -TaskName komorebi
    exit 0
}

if (Get-Process WorkspaceTray -ErrorAction SilentlyContinue) { exit 0 }

if (Test-Path $flag) { exit 0 }

if (-not (Test-Path $tray))
{
    Log "tray executable missing: $tray"
    exit 0
}

# Through the task, never Start-Process: a child of this script would inherit
# the job object of whatever terminal ran it (KILL_ON_JOB_CLOSE) and vanish
# when that terminal closes.
Log 'WorkspaceTray is not running; starting the komorebi-tray task'
Start-ScheduledTask -TaskName komorebi-tray
