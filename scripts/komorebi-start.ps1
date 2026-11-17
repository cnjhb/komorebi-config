$ErrorActionPreference = 'Continue'
$log = Join-Path $env:USERPROFILE 'komorebi-start.log'
function Log($m) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Out-File $log -Append -Encoding utf8
}

Log '=== startup begin ==='
Start-Sleep -Seconds 4

$bin = Join-Path $env:ProgramFiles 'komorebi\bin'
$env:PATH = "$bin;$env:PATH"

if (-not (Get-Process komorebi -ErrorAction SilentlyContinue)) {
    Log 'starting komorebi'
    Start-Process -FilePath (Join-Path $bin 'komorebi.exe') -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $env:USERPROFILE 'komorebi-stderr.log') `
        -RedirectStandardOutput (Join-Path $env:USERPROFILE 'komorebi-stdout.log')

    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        & (Join-Path $bin 'komorebic.exe') state *> $null
        if ($LASTEXITCODE -eq 0) { $up = $true; break }
    }
    if ($up) { Log 'komorebi is up' } else { Log 'WARNING: komorebi did not come up' }
} else {
    Log 'komorebi already running'
}

$whkd = Join-Path $env:ProgramFiles 'whkd\bin\whkd.exe'
if (-not (Get-Process whkd -ErrorAction SilentlyContinue)) {
    Log 'starting whkd'
    Start-Process -FilePath $whkd -WindowStyle Hidden
} else {
    Log 'whkd already running'
}

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
