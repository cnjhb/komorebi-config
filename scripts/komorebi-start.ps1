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
$komorebi = Join-Path $bin 'komorebi.exe'
$komorebic = Join-Path $bin 'komorebic.exe'

function Test-Komorebi {
    & $komorebic state *> $null
    return ($LASTEXITCODE -eq 0)
}

# komorebi is started from here on purpose.
#
# It refuses to start unless AllowSetForegroundWindow succeeds, and whether
# that call is allowed depends on who launched it: a process started by
# explorer (the shell) gets the right, one started by Task Scheduler or by a
# random terminal is denied with ERROR_ACCESS_DENIED while the foreground lock
# is armed. This script runs from the Startup shortcut -> explorer, so its
# children can call it. The elevated "komorebi" scheduled task is only a
# fallback and races with this on purpose: whichever starts first wins,
# komorebi rejects a second instance.
$up = Test-Komorebi
if (-not $up) {
    Log 'komorebi is not running; starting it from the shell chain (medium)'
    Start-Process -FilePath $komorebi -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $env:USERPROFILE 'komorebi-stderr.log') `
        -RedirectStandardOutput (Join-Path $env:USERPROFILE 'komorebi-stdout.log') `
        -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Komorebi) { $up = $true; break }
    }
}

if ($up) {
    Log 'komorebi is up'
} else {
    $err = Get-Content (Join-Path $env:USERPROFILE 'komorebi-stderr.log') -Raw -ErrorAction SilentlyContinue
    Log "WARNING: komorebi did not come up: $($err.Trim())"
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
