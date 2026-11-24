# Registers the "komorebi-watchdog" scheduled task: every minute it checks that
# komorebi and WorkspaceTray are alive and restarts whatever is missing.
#
# The action is a GUI-subsystem executable (komorebi-watchdog.exe), not
# powershell.exe: with Windows Terminal as the default terminal, a console app
# started by Task Scheduler opens a visible WT window that ignores
# -WindowStyle Hidden, i.e. a window flashes every time the task runs.
#
# The restarts themselves also go through scheduled tasks (schtasks /run), so
# the started processes are not children of this task's job object.
#
# Runs as the current user at the current integrity level (the tray must not be
# elevated), so no UAC prompt is needed.

$ErrorActionPreference = 'Stop'

$taskName = 'komorebi-watchdog'
$exe      = Join-Path $env:LOCALAPPDATA 'Programs\komorebi-watchdog.exe'

if (-not (Test-Path $exe))
{
    throw "watchdog executable not found: $exe (run install.ps1 first)"
}

$action = New-ScheduledTaskAction -Execute $exe

# A logon trigger alone never repeats in an already-running session, so use a
# time trigger with an explicit (long) duration: runs every minute, survives
# reboots, and shows up in NextRunTime.
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1) `
    -RepetitionDuration (New-TimeSpan -Days 3650)

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -MultipleInstances IgnoreNew -Hidden
$settings.ExecutionTimeLimit = 'PT2M'

$principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Settings $settings -Principal $principal -Force `
    -Description 'Restart komorebi / WorkspaceTray when they die (whkd comes with the tray)' | Out-Null

Write-Host "scheduled task '$taskName' registered (every minute, no console window)"
