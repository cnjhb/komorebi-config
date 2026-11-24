// runhidden: starts a console program with CREATE_NO_WINDOW so no console (and
// therefore no Windows Terminal window) is ever created.
//
// Why: since Windows 11 can use Windows Terminal as the default terminal
// application, powershell.exe -WindowStyle Hidden still opens a WT window --
// WT ignores STARTF_USESHOWWINDOW. That produced a visible 1168x704 terminal
// flashing every minute from the watchdog task and a terminal lingering at
// logon while komorebi-start.ps1 waited for komorebi.
//
// Usage: runhidden.exe <exe> [argument ...]
// The launcher exits immediately; the child keeps running on its own.

using System;
using System.Diagnostics;
using System.Text;

static class RunHidden
{
    static int Main(string[] args)
    {
        if (args.Length < 1) return 2;

        var commandLine = new StringBuilder();
        for (int i = 1; i < args.Length; i++)
        {
            if (i > 1) commandLine.Append(' ');
            commandLine.Append('"').Append(args[i].Replace("\"", "\\\"")).Append('"');
        }

        try
        {
            var psi = new ProcessStartInfo(args[0])
            {
                Arguments = commandLine.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true,   // CREATE_NO_WINDOW: no console at all
            };
            Process.Start(psi);
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
