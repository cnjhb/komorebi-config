param(
    [string]$OutputDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc (.NET Framework 4.8 is required)"
}

$out = Join-Path $OutputDir 'komorebi-watchdog.exe'

# /target:winexe => GUI subsystem => no console and no Windows Terminal window
# when Task Scheduler launches it.
& $csc /nologo /target:winexe /codepage:65001 "/out:$out" `
    "$PSScriptRoot\komorebi-watchdog.cs"

if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}

Write-Host "built: $out"
