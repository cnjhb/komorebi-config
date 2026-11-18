# Registers the "komorebi" scheduled task, which starts komorebi elevated at
# logon.
#
# Why: since Windows 11 26H2 (build 26300) AllowSetForegroundWindow fails for a
# medium-integrity process, so komorebi.exe bails out right after startup with
#
#   Error: failed call to AllowSetForegroundWindow after 5 retries
#
# and never comes up. Running komorebi with the highest privileges avoids it.
# Registering a task with RunLevel Highest requires administrator rights, so
# this script re-launches itself through UAC.

$ErrorActionPreference = 'Stop'

$taskName = 'komorebi'
$logon    = Join-Path $env:LOCALAPPDATA 'Programs\komorebi-elevated.ps1'
$result   = Join-Path $env:TEMP 'komorebi-task-install.txt'

$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Remove-Item $result -ErrorAction SilentlyContinue
    Write-Host 'administrator rights are required, approving the UAC prompt...'
    $argList = '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', "`"$PSCommandPath`""
    Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Verb RunAs -ArgumentList $argList -Wait

    if (Test-Path $result) {
        $message = (Get-Content $result -Raw).Trim()
        Remove-Item $result -ErrorAction SilentlyContinue
        if ($message -eq 'OK') {
            Write-Host "scheduled task '$taskName' registered"
        } else {
            Write-Warning $message
        }
    } else {
        Write-Warning 'task registration was cancelled'
    }
    return
}

try {
    $action = New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$logon`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:COMPUTERNAME\$env:USERNAME"
    $trigger.Delay = 'PT10S'
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable -MultipleInstances IgnoreNew
    $settings.ExecutionTimeLimit = 'PT0S'
    $principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\$env:USERNAME" `
        -LogonType Interactive -RunLevel Highest

    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
        -Settings $settings -Principal $principal -Force `
        -Description 'Start komorebi elevated at logon (26H2 AllowSetForegroundWindow workaround)' | Out-Null

    "OK" | Out-File $result -Encoding utf8
} catch {
    "FAIL: $($_.Exception.Message)" | Out-File $result -Encoding utf8
}
