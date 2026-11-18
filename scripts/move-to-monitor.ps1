# move-to-monitor.ps1
#
# Move the focused window to the next/previous monitor.
#
# Why this exists:
#   In komorebi v0.1.41 the focused window of a monocled workspace lives in
#   `monocle_container`, not in `containers`, and every move command
#   (move-to-monitor, cycle-move-to-monitor, move-to-monitor-workspace) looks
#   the window up in `containers` and bails out with
#
#     ERROR komorebi::process_command: there is no container
#
#   so `win + o` silently does nothing after `win + f`. This wrapper toggles
#   monocle off, moves the window, then turns monocle back on for the
#   workspace it landed on.
#
# Usage:
#   move-to-monitor.ps1 next
#   move-to-monitor.ps1 previous

param(
    [Parameter(Position = 0)]
    [ValidateSet('next', 'previous')]
    [string]$Direction = 'next'
)

$ErrorActionPreference = 'Stop'

# komorebic writes UTF-8; make sure our own console/encodings do not garble it.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}

$komorebic = Join-Path $env:ProgramFiles 'komorebi\bin\komorebic.exe'
$logPath   = Join-Path $env:USERPROFILE 'move-to-monitor.log'

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

try {
    $state = Get-KomorebiState
} catch {
    Write-Log "direction=$Direction could not read state: $($_.Exception.Message)"
    & $komorebic cycle-move-to-monitor $Direction | Out-Null
    exit 0
}

$monitorCount = @($state.monitors.elements).Count
if ($monitorCount -lt 2) {
    Write-Log "direction=$Direction only $monitorCount monitor(s), nothing to do"
    exit 0
}

$fromMonitor   = [int]$state.monitors.focused
$fromWorkspace = [int]$state.monitors.elements[$fromMonitor].workspaces.focused
$workspace     = $state.monitors.elements[$fromMonitor].workspaces.elements[$fromWorkspace]
$monocle       = ($null -ne $workspace.monocle_container)

if ($monocle) {
    & $komorebic toggle-monocle | Out-Null
    Start-Sleep -Milliseconds 150
}

& $komorebic cycle-move-to-monitor $Direction | Out-Null
Start-Sleep -Milliseconds 250

$toMonitor = $null
try { $toMonitor = [int](Get-KomorebiState).monitors.focused } catch {}

if ($monocle -and $null -ne $toMonitor -and $toMonitor -ne $fromMonitor) {
    # The window (and focus) landed on another monitor: keep it monocled there.
    & $komorebic toggle-monocle | Out-Null
}

Write-Log "direction=$Direction monocle=$monocle monitor=$fromMonitor->$toMonitor"
