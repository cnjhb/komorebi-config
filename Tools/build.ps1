param(
    [string]$OutputDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc (.NET Framework 4.8 is required)"
}

$out = Join-Path $OutputDir 'runhidden.exe'

# /target:winexe => GUI subsystem => the launcher itself has no console.
& $csc /nologo /target:winexe /codepage:65001 "/out:$out" `
    "$PSScriptRoot\runhidden.cs"

if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}

Write-Host "built: $out"
