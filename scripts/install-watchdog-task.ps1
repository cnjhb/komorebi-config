# Registers the "komorebi-watchdog" scheduled task: every minute it checks that
# komorebi and WorkspaceTray are alive and restarts whatever is missing.
#
# The restart goes through the scheduled tasks (Start-ScheduledTask) instead of
# Start-Process, because a process started from a terminal inherits that
# terminal's job object; many terminals use KILL_ON_JOB_CLOSE, which would take
# the tray down again the moment the terminal closes.
#
# Runs as the current user at the current integrity level (the tray must not be
# elevated), so no UAC prompt is needed.

$ErrorActionPreference = 'Stop'

$taskName = 'komorebi-watchdog'
$script   = Join-Path $env:LOCALAPPDATA 'Programs\komorebi-watchdog.ps1'

if (-not (Test-Path $script))
{
    throw "watchdog script not found: $script (run install.ps1 first)"
}

$action = New-ScheduledTaskAction `
    -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
    -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""

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

Write-Host "scheduled task '$taskName' registered (every minute)"
