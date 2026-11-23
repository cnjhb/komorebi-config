# Registers the "komorebi-tray" scheduled task, which starts WorkspaceTray.
#
# Why a task instead of Start-Process:
#
#   Anything started from a terminal is inherited into that terminal's job
#   object. opencode-pty.exe (and many IDEs) create their shells inside a job
#   with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so when the terminal/session is
#   closed the kernel terminates the whole tree -- silently, with no crash
#   report. The capsule and whkd (its child) simply disappear while komorebi,
#   started by its own scheduled task, keeps tiling windows.
#
#   Task Scheduler starts the process from svchost.exe, outside any job, so it
#   survives the terminal. Start it with `Start-ScheduledTask komorebi-tray`.

$ErrorActionPreference = 'Stop'

$taskName = 'komorebi-tray'
$tray     = Join-Path $env:LOCALAPPDATA 'Programs\WorkspaceTray\WorkspaceTray.exe'

if (-not (Test-Path $tray))
{
    throw "tray executable not found: $tray (run install.ps1 first)"
}

$action = New-ScheduledTaskAction -Execute $tray -WorkingDirectory (Split-Path $tray)

$logon = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\$env:USERNAME"
$logon.Delay = 'PT20S'

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -MultipleInstances IgnoreNew -Hidden
# A long-running tray must not be killed by the default 3-day execution limit.
$settings.ExecutionTimeLimit = 'PT0S'

$principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $logon `
    -Settings $settings -Principal $principal -Force `
    -Description 'Start WorkspaceTray (capsule + whkd) outside the launching terminal job object' | Out-Null

Write-Host "scheduled task '$taskName' registered"
