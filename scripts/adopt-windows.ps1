# Adopts every top-level window that komorebi is not yet managing.
#
# komorebi only manages windows it saw being created (or that are focused when
# `komorebic manage` runs). After a delayed start -- e.g. the AllowSetForegroundWindow
# retries after a reboot -- every window that existed before the WM came up stays
# floating on top of the tiled ones, which looks like "the window manager isn't
# running". This walks the managed list from `komorebic state`, focuses each
# remaining window and asks komorebi to manage it.
#
# It focuses every window in turn, so it is meant for startup, not for a hotkey.

$ErrorActionPreference = 'Continue'

$bin = Join-Path $env:ProgramFiles 'komorebi\bin'
$komorebic = Join-Path $bin 'komorebic.exe'

if (-not (Test-Path $komorebic)) { return }

Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class AdoptWindows {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool alt);
  public delegate bool EnumProc(IntPtr h, IntPtr p);

  static readonly string[] Skip = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
                                    "Windows.UI.Core.CoreWindow", "ApplicationFrameWindow" };
  static readonly string[] SkipExe = { "komorebi", "WorkspaceTray", "whkd", "TextInputHost" };

  public static List<IntPtr> List() {
    var result = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      if (!IsWindowVisible(h)) return true;
      if (GetWindowTextLength(h) == 0) return true;
      if (GetWindow(h, 4) != IntPtr.Zero) return true;              // owned popups/dialogs
      long ex = GetWindowLongPtr(h, -20).ToInt64();
      if ((ex & 0x00000080) != 0) return true;                      // WS_EX_TOOLWINDOW
      var cls = new StringBuilder(128); GetClassName(h, cls, 128);
      foreach (var s in Skip) if (cls.ToString() == s) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      string exe = "?"; try { exe = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch {}
      foreach (var s in SkipExe) if (exe == s) return true;
      result.Add(h);
      return true;
    }, IntPtr.Zero);
    return result;
  }

  public static void Focus(IntPtr h) { SwitchToThisWindow(h, true); }
}
"@

# Windows komorebi already manages.
$managed = @{}
try {
    $state = & $komorebic state 2>$null | ConvertFrom-Json
    foreach ($m in $state.monitors.elements) {
        foreach ($ws in $m.workspaces.elements) {
            foreach ($c in $ws.containers.elements) {
                foreach ($w in $c.windows.elements) { $managed[[int64]$w.hwnd] = $true }
            }
        }
    }
} catch { }

$adopted = 0
foreach ($h in [AdoptWindows]::List()) {
    $key = $h.ToInt64()
    if ($managed.ContainsKey($key)) { continue }
    [AdoptWindows]::Focus($h)
    Start-Sleep -Milliseconds 250
    & $komorebic manage *> $null
    Start-Sleep -Milliseconds 150
    $adopted++
}

if ($adopted -gt 0) {
    & $komorebic retile *> $null
    Write-Host "adopted $adopted window(s)"
}
