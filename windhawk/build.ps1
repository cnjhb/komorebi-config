# Builds komorebi-taskbar-info.wh.cpp from the upstream Taskbar Clock
# Customization mod and compiles it with Windhawk's bundled clang.
#
# Requires Windhawk to be installed (its compiler + engine import library).

param(
    [string]$OutputDir = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$upstreamUrl = 'https://raw.githubusercontent.com/ramensoftware/windhawk-mods/main/mods/taskbar-clock-customization.wh.cpp'
$upstream = Join-Path $env:TEMP 'taskbar-clock-customization.wh.cpp'

if (-not (Test-Path $upstream)) {
    Write-Host "downloading upstream mod source..."
    Invoke-WebRequest $upstreamUrl -OutFile $upstream -Headers @{'User-Agent' = 'ps'}
}

$src = [System.IO.File]::ReadAllText($upstream)

$src = $src.Replace('// @id              taskbar-clock-customization', '// @id              komorebi-taskbar-info')
$src = $src.Replace('// @name            Taskbar Clock Customization', '// @name            Komorebi taskbar info')
$src = [regex]::Replace($src, '(?m)^// @description.*$',
    '// @description     Shows the focused komorebi workspace, layout and window title in the taskbar clock area (real-time; reads the local WorkspaceTray HTTP endpoint)')
$src = $src.Replace('// @version         1.8.1', '// @version         1.0.0')
$src = $src.Replace('// @author          m417z', '// @author          cnjhb (based on m417z)')
$src = $src.Replace('// @github          https://github.com/m417z', '// @github          https://github.com/cnjhb/komorebi-config')
$src = $src.Replace('// @twitter         https://twitter.com/m417z', '// @twitter         ')
$src = $src.Replace('// @homepage        https://m417z.com/', '// @homepage        https://github.com/cnjhb/komorebi-config')

# The web content source is a localhost endpoint, so poll it every second
# instead of every minute (upstream interprets the value in minutes).
$src = $src.Replace(
    'DWORD seconds = std::max(g_settings.webContentsUpdateInterval, 1) * 60;',
    'DWORD seconds = std::max(g_settings.webContentsUpdateInterval, 1);')

$src = $src.Replace(
    '// Source code is published under The GNU General Public License v3.0.',
    "// Source code is published under The GNU General Public License v3.0.`r`n//`r`n// Modified for komorebi: id/name/version changed, and the web content update`r`n// interval is interpreted in seconds (the source is a localhost endpoint).`r`n// See windhawk/README.md in https://github.com/cnjhb/komorebi-config.")

$sourcePath = Join-Path $OutputDir 'komorebi-taskbar-info.wh.cpp'
[System.IO.File]::WriteAllText($sourcePath, $src, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "patched source: $sourcePath"

# --- compile with Windhawk's compiler -------------------------------------

$clang = Join-Path $env:ProgramFiles 'Windhawk\Compiler\bin\clang++.exe'
$inc   = Join-Path $env:ProgramFiles 'Windhawk\Compiler\include'
$engineRoot = Join-Path $env:ProgramFiles 'Windhawk\Engine'
if (-not (Test-Path $clang)) { throw "Windhawk compiler not found: $clang" }

$engineDir = Get-ChildItem $engineRoot -Directory |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $engineDir) { throw "no Windhawk engine under $engineRoot" }
$lib = Join-Path $engineDir.FullName '64\windhawk.lib'

$dll = Join-Path $OutputDir 'komorebi-taskbar-info.dll'

$cargs = @(
    '-std=c++23', '-O2', '-ffp-exception-behavior=maytrap', '-mguard=cf-nochecks',
    '-shared', '-s', '-DUNICODE', '-D_UNICODE',
    '-Wno-unneeded-internal-declaration', '-Wno-deprecated-declarations',
    '-DWINVER=0x0A00', '-D_WIN32_WINNT=0x0A00', '-D_WIN32_IE=0x0A00', '-DNTDDI_VERSION=0x0A000008',
    '-D__USE_MINGW_ANSI_STDIO=0', '-DWH_MOD',
    '-DWH_MOD_ID=L"komorebi-taskbar-info"',
    '-DWH_MOD_VERSION=L"1.0.0"',
    '-DWH_WINDHAWK_VERSION=0x01070300',
    $lib,
    '-x', 'c++', $sourcePath,
    '-include', 'windhawk_api.h',
    '-target', 'x86_64-w64-mingw32',
    '-Wl,--export-all-symbols',
    '-o', $dll,
    '-I', $inc,
    '-ldxgi', '-lole32', '-loleaut32', '-lpdh', '-lpowrprof',
    '-lruntimeobject', '-lshlwapi', '-lversion', '-lwininet'
)

$argLine = ($cargs | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
}) -join ' '

$errFile = [System.IO.Path]::GetTempFileName()
$outFile = [System.IO.Path]::GetTempFileName()

$p = Start-Process -FilePath $clang -ArgumentList $argLine -NoNewWindow -Wait -PassThru `
    -RedirectStandardError $errFile -RedirectStandardOutput $outFile

$stderr = [System.IO.File]::ReadAllText($errFile)
$stdout = [System.IO.File]::ReadAllText($outFile)
Remove-Item $errFile, $outFile -Force -ErrorAction SilentlyContinue

if ($stdout.Trim()) { Write-Host $stdout }
if ($stderr.Trim()) { Write-Host $stderr }
if ($p.ExitCode -ne 0) { throw "clang++ failed with exit code $($p.ExitCode)" }
if (-not (Test-Path $dll)) { throw "clang++ did not write $dll" }

Write-Host "built: $dll ($((Get-Item $dll).Length) bytes)"
