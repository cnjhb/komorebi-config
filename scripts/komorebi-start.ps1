$ErrorActionPreference = 'Continue'
$log = Join-Path $env:USERPROFILE 'komorebi-start.log'
function Log($m) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Out-File $log -Append -Encoding utf8
}

Log '=== startup begin ==='
Start-Sleep -Seconds 4

# A deliberate quit from the tray menu sets this flag; a new logon starts a new
# tray, so the flag must not block the watchdog for the rest of the session.
Remove-Item (Join-Path $env:USERPROFILE '.workspace-tray-quit') -ErrorAction SilentlyContinue

$bin = Join-Path $env:ProgramFiles 'komorebi\bin'
$env:PATH = "$bin;$env:PATH"

# komorebi itself is started elevated by the "komorebi" scheduled task. A
# medium-integrity start fails on Windows 11 26H2 (AllowSetForegroundWindow).
$up = $false
for ($i = 0; $i -lt 60; $i++) {
    & (Join-Path $bin 'komorebic.exe') state *> $null
    if ($LASTEXITCODE -eq 0) { $up = $true; break }
    Start-Sleep -Milliseconds 500
}
if ($up) {
    Log 'komorebi is up'
} else {
    Log 'WARNING: komorebi is not running (did the elevated scheduled task start?)'
}

# whkd is started (and supervised) by WorkspaceTray: that way the tray owns the
# process, redirects its stdout/stderr and can log the exit code when it dies.

$tray = Join-Path $env:LOCALAPPDATA 'Programs\WorkspaceTray\WorkspaceTray.exe'
if (Test-Path $tray) {
    if (-not (Get-Process WorkspaceTray -ErrorAction SilentlyContinue)) {
        # Through the scheduled task, not Start-Process: a child of this script
        # inherits the job object of the terminal that ran it, and terminals
        # that use kill-on-job-close would take the tray (and whkd) down with
        # them when they exit.
        Log 'starting workspace tray through its scheduled task'
        try { Start-ScheduledTask -TaskName komorebi-tray }
        catch { Log "could not start komorebi-tray: $($_.Exception.Message)" }
    } else {
        Log 'workspace tray already running'
    }
} else {
    Log "workspace tray not installed at $tray"
}

Log '=== startup done ==='
