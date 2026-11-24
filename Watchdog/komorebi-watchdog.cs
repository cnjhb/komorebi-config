// komorebi-watchdog: keeps komorebi and WorkspaceTray alive.
//
// Built as /target:winexe on purpose. The first version was a PowerShell script
// run by a scheduled task every minute; with Windows Terminal as the default
// terminal that flashed a 1168x704 WT window once a minute ("a window keeps
// popping up and vanishing"), because WT ignores -WindowStyle Hidden and
// STARTF_USESHOWWINDOW. A GUI-subsystem executable never allocates a console,
// so the task is invisible no matter what the default terminal is.

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

static class Watchdog
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string LogPath = Path.Combine(Home, "komorebi-watchdog.log");
    static readonly string QuitFlag = Path.Combine(Home, ".workspace-tray-quit");
    static readonly string TrayPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Programs\WorkspaceTray\WorkspaceTray.exe");

    static int Main()
    {
        if (!Running("komorebi"))
        {
            Log("komorebi is not running; starting the scheduled task");
            RunTask("komorebi");
            return 0;
        }

        if (Running("WorkspaceTray")) return 0;
        if (File.Exists(QuitFlag)) return 0;

        if (!File.Exists(TrayPath))
        {
            Log("tray executable missing: " + TrayPath);
            return 0;
        }

        // Through the scheduled task, never a direct start: a child of this
        // process would inherit the job object of whatever started it, and
        // terminals that use kill-on-job-close would take the tray (and whkd)
        // down with them.
        Log("WorkspaceTray is not running; starting the komorebi-tray task");
        RunTask("komorebi-tray");
        return 0;
    }

    static bool Running(string name)
    {
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    static void RunTask(string name)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                                           "/run /tn \"" + name + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;   // CREATE_NO_WINDOW: no console, no WT window
            using (Process p = Process.Start(psi)) p.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Log("could not start " + name + ": " + ex.Message);
        }
    }

    static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { }
    }
}
