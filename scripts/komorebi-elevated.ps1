# Starts komorebi with the highest privileges.
#
# Windows 11 26H2 made AllowSetForegroundWindow fail for a medium-integrity
# process, so komorebi.exe exits at startup unless it runs elevated. This
# script is launched at logon by the "komorebi" scheduled task.
#
# The call can still be refused for a while after logon (it fails while the
# input desktop is not the interactive one, i.e. locked / session not ready),
# and komorebi exits with "failed call to AllowSetForegroundWindow after 5
# retries". Instead of 5 tries x 20s we retry for up to ~9 minutes and log the
# input desktop, the foreground window and a direct ASFW probe on every try,
# so the next failure says exactly which state was blocking it.

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

# Input desktop name (Winlogon while locked), foreground window, and a direct
# AllowSetForegroundWindow probe. Calling ASFW here is harmless: it is exactly
# the permission komorebi is asking for.
function Get-Diag {
    try {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WmDiag {
  [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int index, StringBuilder info, int len, out int needed);
  [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool AllowSetForegroundWindow(uint pid);
  [DllImport("user32.dll", SetLastError=true)] public static extern bool SystemParametersInfo(uint action, uint param, out uint value, uint winIni);
  public static string Diag() {
    string desktop = "?";
    IntPtr d = OpenInputDesktop(0, false, 0x0001);
    if (d != IntPtr.Zero) {
      var sb = new StringBuilder(256); int needed;
      if (GetUserObjectInformation(d, 2, sb, 256, out needed)) desktop = sb.ToString();
      CloseDesktop(d);
    }
    IntPtr fg = GetForegroundWindow();
    uint pid = 0; string cls = "";
    if (fg != IntPtr.Zero) {
      GetWindowThreadProcessId(fg, out pid);
      var c = new StringBuilder(128); GetClassName(fg, c, 128); cls = c.ToString();
    }
    uint lockTimeout = 999999;
    SystemParametersInfo(0x2002, 0, out lockTimeout, 0);
    bool self = AllowSetForegroundWindow((uint)System.Diagnostics.Process.GetCurrentProcess().Id);
    int selfErr = Marshal.GetLastWin32Error();
    bool any = AllowSetForegroundWindow(0xFFFFFFFF);
    int anyErr = Marshal.GetLastWin32Error();
    return "inputDesktop=" + desktop + " foreground=pid " + pid + " class " + cls +
           " lockTimeout=" + lockTimeout + " ASFW(self)=" + self + "/" + selfErr +
           " ASFW(any)=" + any + "/" + anyErr;
  }
}
"@
        return [WmDiag]::Diag()
    } catch {
        return "diag failed: $($_.Exception.Message)"
    }
}

Log ('diag: ' + (Get-Diag))

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
# also refused on this build, so set it while we still can (elevated). Writing
# the registry is not enough for the running session: SPI_SETFOREGROUNDLOCKTIMEOUT
# applies it to the current session immediately.
Set-ItemProperty 'HKCU:\Control Panel\Desktop' -Name ForegroundLockTimeout -Value 0 -ErrorAction SilentlyContinue
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class SpiLock {
  [DllImport("user32.dll", SetLastError=true)]
  public static extern bool SystemParametersInfo(uint action, uint param, IntPtr data, uint winIni);
  public static bool Clear() { return SystemParametersInfo(0x2003, 0, IntPtr.Zero, 0); }
}
"@
    [void][SpiLock]::Clear()
} catch { }

for ($attempt = 1; $attempt -le 60; $attempt++) {
    if (Test-Komorebi) { break }

    Log "starting komorebi (elevated, attempt $attempt)"
    Start-Process -FilePath $komorebi -WindowStyle Hidden `
        -RedirectStandardError (Join-Path $env:USERPROFILE 'komorebi-stderr.log') `
        -RedirectStandardOutput (Join-Path $env:USERPROFILE 'komorebi-stdout.log') `
        -ErrorAction SilentlyContinue

    $up = $false
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Komorebi) { $up = $true; break }
    }

    if ($up) {
        Log "komorebi is up (elevated, attempt $attempt)"
        # Windows that already existed when komorebi came up are not managed and
        # float on top of the tiled ones; adopt them once.
        $adopt = Join-Path $env:LOCALAPPDATA 'Programs\adopt-windows.ps1'
        if (Test-Path $adopt) {
            try {
                $out = & $adopt 2>&1 | Out-String
                foreach ($line in ($out -split "`n")) {
                    if ($line.Trim()) { Log ("adopt: " + $line.Trim()) }
                }
            } catch {
                Log "adopt failed: $($_.Exception.Message)"
            }
        }
        return
    }

    $err = Get-Content (Join-Path $env:USERPROFILE 'komorebi-stderr.log') -Raw -ErrorAction SilentlyContinue
    $out = Get-Content (Join-Path $env:USERPROFILE 'komorebi-stdout.log') -ErrorAction SilentlyContinue |
        Where-Object { $_ -match 'ERROR|WARN' } | Select-Object -Last 3
    Log "attempt $attempt failed: $($err.Trim())"
    if ($out) { foreach ($line in $out) { Log ("  " + $line) } }
    Log ('diag: ' + (Get-Diag))
    Get-Process komorebi -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

Log 'WARNING: komorebi did not come up (elevated)'
