$ErrorActionPreference = 'Continue'
$log = Join-Path $env:USERPROFILE 'komorebi-start.log'
function Log($m) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Out-File $log -Append -Encoding utf8
}

Log '=== startup begin ==='
Start-Sleep -Seconds 4

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
        Log 'starting workspace tray'
        Start-Process -FilePath $tray
    } else {
        Log 'workspace tray already running'
    }
} else {
    Log "workspace tray not installed at $tray"
}

Log '=== startup done ==='
