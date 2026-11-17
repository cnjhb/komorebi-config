param(
    [string]$OutputDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found at $csc (.NET Framework 4.8 is required)"
}

if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
}

$out = Join-Path $OutputDir 'WorkspaceTray.exe'

& $csc /nologo /target:winexe /codepage:65001 `
    "/win32manifest:$PSScriptRoot\app.manifest" `
    "/out:$out" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll `
    "$PSScriptRoot\WorkspaceTray.cs"

if ($LASTEXITCODE -ne 0) {
    throw "csc failed with exit code $LASTEXITCODE"
}

Write-Host "built: $out"
