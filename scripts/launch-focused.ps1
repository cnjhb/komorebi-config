# launch-focused.ps1
#
# Launch an application on the currently focused komorebi monitor/workspace.
#
# Why this exists:
#   When a new window is created, Windows gives it focus. komorebi's FocusChange
#   handler then focuses whatever monitor that window happens to be on. Apps like
#   Windows Terminal and Firefox decide that position themselves (they remember
#   the last one), so they can pop up on the "wrong" screen. komorebi has no
#   option to override this, so we do it here:
#     1. remember the focused monitor/workspace
#     2. launch the app (Windows Terminal gets --pos on the right monitor)
#     3. if the new window still lands elsewhere, move it back
#
# Usage:
#   launch-focused.ps1 terminal
#   launch-focused.ps1 firefox

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('terminal', 'firefox')]
    [string]$App
)

$ErrorActionPreference = 'Stop'

# komorebic writes UTF-8; make sure our own console/encodings do not garble it.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$komorebic  = Join-Path $env:ProgramFiles 'komorebi\bin\komorebic.exe'
$logPath    = Join-Path $env:USERPROFILE 'launch-focused.log'
$firefoxExe = Join-Path $env:ProgramFiles 'Mozilla Firefox\firefox.exe'

function Write-Log([string]$Message) {
    try {
        "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')  $Message" |
            Out-File -FilePath $logPath -Append -Encoding utf8
    } catch {}
}

# Read `komorebic state` through a file instead of a console pipe: whkd starts
# pwsh without a UTF-8 console, so piping native output can mis-decode window
# titles (which breaks JSON parsing when titles contain non-ASCII text).
function Get-KomorebiState {
    $out = [System.IO.Path]::GetTempFileName()
    $err = [System.IO.Path]::GetTempFileName()
    try {
        $proc = Start-Process -FilePath $komorebic -ArgumentList 'state' `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput $out -RedirectStandardError $err
        if ($proc.ExitCode -ne 0) {
            throw "komorebic state exited with $($proc.ExitCode)"
        }
        $json = [System.IO.File]::ReadAllText($out, [System.Text.Encoding]::UTF8)
        return $json | ConvertFrom-Json
    } finally {
        Remove-Item $out, $err -Force -ErrorAction SilentlyContinue
    }
}

function Start-App {
    switch ($App) {
        'terminal' {
            Start-Process -FilePath 'wt.exe' -ArgumentList '-w', 'new'
        }
        'firefox' {
            Start-Process -FilePath $firefoxExe
        }
    }
}

$state = $null
try {
    $state = Get-KomorebiState
} catch {
    # Never let a state/parse problem stop the shortcut from launching the app.
    Write-Log "app=$App could not read komorebi state: $($_.Exception.Message) (launching without placement)"
    Start-App
    exit 0
}

$targetMonitor   = [int]$state.monitors.focused
$targetWorkspace = [int]$state.monitors.elements[$targetMonitor].workspaces.focused

# Work area top-left of the target monitor (monitor origin + bar/taskbar offset).
$monitor = $state.monitors.elements[$targetMonitor]
$offset  = $monitor.work_area_offset
$offsetX = if ($null -ne $offset) { [int]$offset.left } else { 0 }
$offsetY = if ($null -ne $offset) { [int]$offset.top } else { 0 }
$posX    = [int]$monitor.size.left + $offsetX
$posY    = [int]$monitor.size.top  + $offsetY

$beforeHwnds = @()
for ($mi = 0; $mi -lt $state.monitors.elements.Count; $mi++) {
    $m = $state.monitors.elements[$mi]
    for ($wi = 0; $wi -lt $m.workspaces.elements.Count; $wi++) {
        $ws = $m.workspaces.elements[$wi]
        foreach ($c in $ws.containers.elements) {
            foreach ($w in $c.windows.elements) { $beforeHwnds += [long]$w.hwnd }
        }
        foreach ($w in $ws.floating_windows) { $beforeHwnds += [long]$w.hwnd }
    }
}

Write-Log "app=$App targetMonitor=$targetMonitor targetWorkspace=$targetWorkspace pos=$posX,$posY"

switch ($App) {
    'terminal' {
        Start-Process -FilePath 'wt.exe' -ArgumentList '-w', 'new', '--pos', "$posX,$posY"
    }
    'firefox' {
        Start-Process -FilePath $firefoxExe
    }
}

# Wait for the app's window to be managed by komorebi (up to ~10s).
$newWindow = $null
for ($i = 0; $i -lt 66; $i++) {
    Start-Sleep -Milliseconds 150
    try { $now = Get-KomorebiState } catch { continue }
    $nowWindows = @()
    for ($mi = 0; $mi -lt $now.monitors.elements.Count; $mi++) {
        $m = $now.monitors.elements[$mi]
        for ($wi = 0; $wi -lt $m.workspaces.elements.Count; $wi++) {
            $ws = $m.workspaces.elements[$wi]
            foreach ($c in $ws.containers.elements) {
                foreach ($w in $c.windows.elements) {
                    $nowWindows += [pscustomobject]@{ Hwnd = [long]$w.hwnd; Monitor = $mi; Workspace = $wi }
                }
            }
            foreach ($w in $ws.floating_windows) {
                $nowWindows += [pscustomobject]@{ Hwnd = [long]$w.hwnd; Monitor = $mi; Workspace = $wi }
            }
        }
    }
    $newWindow = $nowWindows | Where-Object { $beforeHwnds -notcontains $_.Hwnd } | Select-Object -First 1
    if ($newWindow) { break }
}

if (-not $newWindow) {
    Write-Log "app=$App no new window detected (opened as tab in an existing window?)"
    exit 0
}

if ($newWindow.Monitor -eq $targetMonitor -and $newWindow.Workspace -eq $targetWorkspace) {
    Write-Log "app=$App hwnd=$($newWindow.Hwnd) already on $targetMonitor/$targetWorkspace"
    exit 0
}

Start-Sleep -Milliseconds 150
& $komorebic move-to-monitor-workspace $targetMonitor $targetWorkspace | Out-Null
Write-Log "app=$App hwnd=$($newWindow.Hwnd) moved from $($newWindow.Monitor)/$($newWindow.Workspace) to $targetMonitor/$targetWorkspace"
