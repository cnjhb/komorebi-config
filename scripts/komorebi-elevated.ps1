# Starts komorebi with the highest privileges.
#
# Windows 11 26H2 made AllowSetForegroundWindow fail for a medium-integrity
# process, so komorebi.exe exits at startup unless it runs elevated. This
# script is launched at logon by the "komorebi" scheduled task.

$ErrorActionPreference = 'Continue'
$log = Join-Path $env:USERPROFILE 'komorebi-start.log'
function Log($m) {
    "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Out-File $log -Append -Encoding utf8
}

$bin = Join-Path $env:ProgramFiles 'komorebi\bin'
$env:PATH = "$bin;$env:PATH"
$komorebi = Join-Path $bin 'komorebi.exe'
$komorebic = Join-Path $bin 'komorebic.exe'

Log '=== elevated komorebi start ==='

function Test-Komorebi {
    & $komorebic state *> $null
    return ($LASTEXITCODE -eq 0)
}

if (Test-Komorebi) {
    Log 'komorebi already running'
    return
}

# Let the shell finish coming up before starting the window manager.
for ($i = 0; $i -lt 60; $i++) {
    if (Get-Process explorer -ErrorAction SilentlyContinue) { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Seconds 3

# komorebi tries to lower the foreground lock timeout itself, but that call is
# also refused on this build, so set it while we still can (elevated).
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ForegroundLockTimeout -Value 0 -ErrorAction SilentlyContinue

for ($attempt = 1; $attempt -le 5; $attempt++) {
    if (Test-Komorebi) { break }

    Log "starting komorebi (elevated, attempt $attempt)"
    Start-Process -FilePath $komorebi -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $env:USERPROFILE 'komorebi-stderr.log') `
        -RedirectStandardOutput (Join-Path $env:USERPROFILE 'komorebi-stdout.log') `
        -ErrorAction SilentlyContinue

    $up = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Komorebi) { $up = $true; break }
    }

    if ($up) {
        Log 'komorebi is up (elevated)'
        return
    }

    Log "attempt $attempt failed"
    $err = Get-Content (Join-Path $env:USERPROFILE 'komorebi-stderr.log') -Raw -ErrorAction SilentlyContinue
    if ($err) { Log ($err.Trim()) }
    Get-Process komorebi -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 5
}

Log 'WARNING: komorebi did not come up (elevated)'
