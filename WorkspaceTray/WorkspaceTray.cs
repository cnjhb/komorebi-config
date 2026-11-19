using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using RECT = KomorebiTaskbarInfo.Native.RECT;

namespace KomorebiTaskbarInfo
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Global\KomorebiTaskbarInfo", out createdNew))
            {
                if (!createdNew) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                // Never let a drawing/threading hiccup take the whole strip
                // down with the WinForms crash dialog: log it and keep going.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    InfoContext.Log("UI exception: " + e.Exception);
                };
                AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
                {
                    InfoContext.Log("fatal: " + e.ExceptionObject);
                };

                Application.Run(new InfoContext(args));
            }
        }
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    internal class CTaskbarList
    {
    }

    [ComImport]
    [Guid("56FDF342-FD6D-11d0-958A-006097C9A090")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    class InfoContext : ApplicationContext
    {
        const string PipeName = "komorebi-taskbar-info";
        const int HttpPort = 17321;
        static readonly string Komorebic = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "komorebi", "bin", "komorebic.exe");

        static readonly string StartScript = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "komorebi-start.ps1");

        static readonly string LogPath =
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "taskbar-info.log");

        readonly List<Strip> _strips = new List<Strip>();
        readonly NotifyIcon _tray;
        readonly ToolStripMenuItem _statusItem;
        readonly ToolStripMenuItem _filterItem;
        readonly System.Windows.Forms.Timer _timer;
        readonly Control _marshal;
        readonly object _sync = new object();
        readonly Dictionary<string, string> _lastTitle = new Dictionary<string, string>();
        readonly HashSet<long> _hiddenHwnds = new HashSet<long>();

        ITaskbarList _taskbarList;
        bool _filterTaskbar = false;
        bool _watchWhkd = true;
        HashSet<long> _lastManaged = new HashSet<long>();
        HashSet<long> _lastVisible = new HashSet<long>();

        Dictionary<string, object> _state;
        int _tick;
        DateTime _lastWhkdStart = DateTime.MinValue;
        Process _whkdProc;
        volatile string _forceRefresh;
        volatile string _webLine = "";
        volatile string _webLineFull = "";
        TcpListener _http;
        volatile bool _exit;
        bool _fullscreen;
        bool _light = true;
        Icon _icon;
        string _iconText = "";
        string _iconTooltip = "";
        bool _iconConnected;

        public InfoContext(string[] args)
        {
            _marshal = new Control();
            IntPtr forceHandle = _marshal.Handle;
            ReadTheme();

            _statusItem = new ToolStripMenuItem("正在连接 komorebi…");
            _statusItem.Enabled = false;

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());

            _filterItem = new ToolStripMenuItem("任务栏只显示当前工作区（实验性）");
            _filterItem.CheckOnClick = true;
            _filterItem.Checked = false;
            _filterItem.CheckedChanged += delegate
            {
                _filterTaskbar = _filterItem.Checked;
                ApplyTaskbarFilter();
            };
            menu.Items.Add(_filterItem);

            var watchWhkd = new ToolStripMenuItem("whkd 退出后自动重启");
            watchWhkd.CheckOnClick = true;
            watchWhkd.Checked = true;
            watchWhkd.CheckedChanged += delegate { _watchWhkd = watchWhkd.Checked; };
            menu.Items.Add(watchWhkd);
            menu.Items.Add(new ToolStripSeparator());

            var restart = new ToolStripMenuItem("重启 komorebi");
            restart.Click += delegate { RestartKomorebi(); };
            menu.Items.Add(restart);
            var quit = new ToolStripMenuItem("退出");
            quit.Click += delegate { ExitApp(); };
            menu.Items.Add(quit);

            _tray = new NotifyIcon();
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
            SetTray("?", "komorebi · 正在连接", false);

            HookSystemEvents();

            try
            {
                _taskbarList = (ITaskbarList)new CTaskbarList();
                Log("taskbar list created");
                try
                {
                    _taskbarList.HrInit();
                    Log("taskbar HrInit ok");
                }
                catch (Exception hex)
                {
                    Log("taskbar HrInit failed: " + hex.Message);
                }
            }
            catch (Exception ex)
            {
                Log("taskbar list: " + ex.Message);
                _taskbarList = null;
                _filterItem.Enabled = false;
                _filterItem.Checked = false;
            }

            BuildStrips();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 1000;
            _timer.Tick += delegate { Tick(); };
            _timer.Start();

            var worker = new Thread(Worker);
            worker.IsBackground = true;
            worker.Start();

            var http = new Thread(HttpWorker);
            http.IsBackground = true;
            http.Start();

            Log("started");
        }

        void HookSystemEvents()
        {
            // After a sign-in or a Modern Standby resume the taskbar is
            // re-created and the strips lose their layered surface, so push a
            // fresh frame as soon as the desktop comes back.
            try
            {
                SystemEvents.PowerModeChanged += delegate { ForceRefresh("power"); };
                SystemEvents.SessionSwitch += delegate { ForceRefresh("session"); };
                SystemEvents.DisplaySettingsChanged += delegate { ForceRefresh("display"); };
                SystemEvents.UserPreferenceChanged += delegate { ForceRefresh("preferences"); };
            }
            catch (Exception ex)
            {
                Log("system events: " + ex.Message);
            }
        }

        void ForceRefresh(string reason)
        {
            try
            {
                _marshal.BeginInvoke((Action)delegate { _forceRefresh = reason; });
            }
            catch { }
        }

        internal static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(LogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        void ReadTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("SystemUsesLightTheme");
                        if (v is int) _light = (int)v != 0;
                    }
                }
            }
            catch { }
        }

        static List<IntPtr> GetTaskbars()
        {
            var taskbars = new List<IntPtr>();
            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            if (primary != IntPtr.Zero) taskbars.Add(primary);

            IntPtr h = IntPtr.Zero;
            while ((h = Native.FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                taskbars.Add(h);

            return taskbars;
        }

        void BuildStrips()
        {
            foreach (var tb in GetTaskbars())
            {
                string device = Native.MonitorDeviceOf(tb);
                if (string.IsNullOrEmpty(device)) continue;
                var strip = new Strip(tb, device, tb == Native.FindWindow("Shell_TrayWnd", null));
                strip.Show();
                _strips.Add(strip);
                Log("strip " + device + " bounds=" + strip.CurrentBounds);
            }
        }

        // explorer restarts recreate the taskbar windows (and re-add every
        // taskbar button). Rebuild the strips and let the filter re-apply.
        void RebuildStrips()
        {
            var taskbars = GetTaskbars();
            bool same = taskbars.Count == _strips.Count;
            if (same)
            {
                for (int i = 0; i < taskbars.Count; i++)
                {
                    if (taskbars[i] != _strips[i].TaskbarHandle) { same = false; break; }
                }
            }
            if (same) return;

            foreach (var s in _strips)
            {
                try { s.Close(); s.Dispose(); } catch { }
            }
            _strips.Clear();
            _hiddenHwnds.Clear();

            try { _tray.Visible = false; _tray.Visible = true; } catch { }

            IntPtr primary = Native.FindWindow("Shell_TrayWnd", null);
            foreach (var tb in taskbars)
            {
                string device = Native.MonitorDeviceOf(tb);
                if (string.IsNullOrEmpty(device)) continue;
                var strip = new Strip(tb, device, tb == primary);
                strip.Show();
                _strips.Add(strip);
            }
            Log("strips rebuilt: " + _strips.Count);
        }

        void Tick()
        {
            try
            {
                _tick++;

                // Taskbar windows and the Windows theme change rarely, so they
                // are polled on a slower cadence; komorebi state still arrives
                // as events, so the capsule itself updates instantly.
                if (_tick % 3 == 1) RebuildStrips();
                if (_tick % 5 == 1) { ReadTheme(); EnsureWhkd(); }

                if (_forceRefresh != null)
                {
                    Log("repaint after " + _forceRefresh);
                    _forceRefresh = null;
                    // The taskbar window may have been recreated by the shell
                    // restore, so re-attach first, then force a fresh frame.
                    RebuildStrips();
                    foreach (var s in _strips.ToArray())
                    {
                        s.InvalidateRender();
                        if (s.Visible) s.RefreshBounds();
                    }
                }

                bool fullscreen = IsFullscreenForeground();
                if (fullscreen != _fullscreen)
                {
                    _fullscreen = fullscreen;
                    foreach (var s in _strips.ToArray()) s.Visible = !_fullscreen;
                }

                foreach (var s in _strips.ToArray())
                {
                    if (!s.Visible) continue;
                    s.RefreshBounds();
                }

                RefreshAll();
            }
            catch (Exception ex)
            {
                Log("tick: " + ex.Message);
            }
        }

        // whkd provides every keybinding and has no supervision of its own. Its
        // main() returns silently after event_loop(), and every executed
        // binding is printed to stdout, so a broken console makes it panic
        // with nothing recorded anywhere. The tray therefore owns the process:
        // stdout/stderr are redirected (no console dependency) and the exit
        // code is logged, which is the only way to tell a clean exit (0) from
        // a panic (101) or a kill (0xFFFFFFFF).
        void EnsureWhkd()
        {
            if (!_watchWhkd) return;

            try
            {
                if (_whkdProc != null)
                {
                    if (!_whkdProc.HasExited) return;
                    int code = 0;
                    try { code = _whkdProc.ExitCode; } catch { }
                    Log("whkd exited with code 0x" + code.ToString("X8"));
                    try { _whkdProc.Dispose(); } catch { }
                    _whkdProc = null;
                }

                // Started by something else (e.g. an older tray): monitor, but
                // the exit code is not observable in that case.
                if (Process.GetProcessesByName("whkd").Length > 0) return;
                if ((DateTime.Now - _lastWhkdStart).TotalSeconds < 10) return;

                string whkd = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "whkd", "bin", "whkd.exe");
                if (!System.IO.File.Exists(whkd)) return;

                var psi = new ProcessStartInfo(whkd);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                var proc = new Process();
                proc.StartInfo = psi;
                proc.OutputDataReceived += delegate { };
                proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (!string.IsNullOrEmpty(e.Data)) Log("whkd stderr: " + e.Data);
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                _whkdProc = proc;
                _lastWhkdStart = DateTime.Now;
                Log("whkd started by tray (pid " + proc.Id + ")");
            }
            catch (Exception ex)
            {
                Log("whkd restart: " + ex.Message);
            }
        }

        bool IsFullscreenForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            var cls = Native.ClassNameOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" ||
                cls == "Shell_SecondaryTrayWnd" || cls == "Windows.UI.Core.CoreWindow") return false;

            RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            IntPtr mon = Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
                   r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        void RefreshAll()
        {
            Dictionary<string, object> state;
            lock (_sync) state = _state;
            if (state == null) return;

            var monitors = AsDict(state, "monitors");
            if (monitors == null) return;
            var mons = AsArray(monitors, "elements");
            if (mons == null) return;

            // Taskbar filter: a managed window is visible when it sits on its
            // monitor's focused workspace. Unmanaged windows are not in the
            // komorebi state and are left alone. Nothing to do while the
            // experimental filter is off.
            if (_filterTaskbar)
            {
                var managed = new HashSet<long>();
                var visible = new HashSet<long>();
                for (int i = 0; i < mons.Length; i++)
                {
                    var mon = AsDict(mons[i]);
                    if (mon == null) continue;
                    var ws = AsDict(mon, "workspaces");
                    if (ws == null) continue;
                    var wss = AsArray(ws, "elements");
                    if (wss == null) continue;

                    int focusedWs = 0;
                    try { focusedWs = Convert.ToInt32(ws["focused"]); } catch { }

                    for (int j = 0; j < wss.Length; j++)
                    {
                        var w = AsDict(wss[j]);
                        if (w == null) continue;

                        var containers = AsDict(w, "containers");
                        var containerEls = containers == null ? null : AsArray(containers, "elements");
                        if (containerEls != null)
                        {
                            foreach (var c in containerEls)
                            {
                                var cDict = AsDict(c);
                                var winObj = cDict == null ? null : AsDict(cDict, "windows");
                                var wins = winObj == null ? null : AsArray(winObj, "elements");
                                if (wins == null) continue;
                                foreach (var win in wins)
                                {
                                    var d = AsDict(win);
                                    if (d == null) continue;
                                    long hwnd = Convert.ToInt64(d["hwnd"]);
                                    managed.Add(hwnd);
                                    if (j == focusedWs) visible.Add(hwnd);
                                }
                            }
                        }

                        var floating = AsDict(w, "floating_windows");
                        var floatingEls = floating == null ? null : AsArray(floating, "elements");
                        if (floatingEls != null)
                        {
                            foreach (var win in floatingEls)
                            {
                                var d = AsDict(win);
                                if (d == null) continue;
                                long hwnd = Convert.ToInt64(d["hwnd"]);
                                managed.Add(hwnd);
                                if (j == focusedWs) visible.Add(hwnd);
                            }
                        }
                    }
                }

                _lastManaged = managed;
                _lastVisible = visible;
                ApplyTaskbarFilter();
            }

            IntPtr fg = Native.GetForegroundWindow();
            string fgMonitor = null;
            string fgTitle = null;
            for (int i = 0; i < mons.Length && fgMonitor == null; i++)
            {
                var mon = AsDict(mons[i]);
                if (mon == null) continue;
                var ws = AsDict(mon, "workspaces");
                var wss = ws == null ? null : AsArray(ws, "elements");
                if (wss == null) continue;
                for (int j = 0; j < wss.Length && fgMonitor == null; j++)
                {
                    var w = AsDict(wss[j]);
                    var cs = w == null ? null : AsArray(w, "containers");
                    if (cs == null) continue;
                    for (int k = 0; k < cs.Length && fgMonitor == null; k++)
                    {
                        var c = AsDict(cs[k]);
                        var winObj = c == null ? null : AsDict(c, "windows");
                        var wins = winObj == null ? null : AsArray(winObj, "elements");
                        if (wins == null) continue;
                        for (int m = 0; m < wins.Length; m++)
                        {
                            var win = AsDict(wins[m]);
                            if (win == null) continue;
                            if (Convert.ToInt64(win["hwnd"]) == fg.ToInt64())
                            {
                                fgMonitor = Convert.ToString(mon["name"]);
                                fgTitle = Convert.ToString(win["title"]);
                                break;
                            }
                        }
                    }
                }
            }

            int focusedMonitor = 0;
            try { focusedMonitor = Convert.ToInt32(monitors["focused"]); } catch { }

            foreach (var strip in _strips.ToArray())
            {
                int mi = -1;
                for (int i = 0; i < mons.Length; i++)
                {
                    var mon = AsDict(mons[i]);
                    if (mon != null && Convert.ToString(mon["name"]) == strip.DeviceName) { mi = i; break; }
                }
                if (mi < 0) continue;

                var monitor = AsDict(mons[mi]);
                var ws = AsDict(monitor, "workspaces");
                if (ws == null) continue;
                var wss = AsArray(ws, "elements");
                if (wss == null) continue;

                int wsi = 0;
                try { wsi = Convert.ToInt32(ws["focused"]); } catch { }

                var names = new List<string>();
                var occupied = new List<bool>();
                foreach (var o in wss)
                {
                    var d = AsDict(o);
                    names.Add(d == null ? "" : Convert.ToString(d["name"]));
                    occupied.Add(IsWorkspaceOccupied(d));
                }

                string layout = "";
                if (wsi >= 0 && wsi < wss.Length)
                {
                    var focusedWs = AsDict(wss[wsi]);
                    if (focusedWs != null) layout = DescribeLayout(focusedWs);
                }

                string title = "";
                if (fgMonitor == strip.DeviceName)
                {
                    title = fgTitle;
                    _lastTitle[strip.DeviceName] = title;
                }
                else
                {
                    _lastTitle.TryGetValue(strip.DeviceName, out title);
                }

                strip.SetModel(names, wsi, title, layout, _light, occupied, mi == focusedMonitor);

                var line = new StringBuilder();
                for (int i = 0; i < names.Count; i++)
                {
                    string label = string.IsNullOrEmpty(names[i]) ? (i + 1).ToString() : names[i];
                    if (label.Length > 2) label = (i + 1).ToString();
                    if (i > 0) line.Append(' ');
                    if (i == wsi)
                    {
                        line.Append('[').Append(label).Append(']');
                    }
                    else
                    {
                        line.Append(label);
                        if (i < occupied.Count && occupied[i]) line.Append('\u00B7');
                    }
                }
                if (!string.IsNullOrEmpty(layout)) line.Append("  \u00B7  ").Append(layout);

                if (layout != strip.LastLayoutLog)
                {
                    strip.LastLayoutLog = layout;
                    Log("layout " + strip.DeviceName + " ws=" + wsi + " " + layout);
                }

                // The taskbar clock lives on the primary monitor's taskbar, so
                // "/" serves that monitor rather than the focused one.
                if (strip.IsPrimary)
                {
                    _webLine = line.ToString();
                    if (!string.IsNullOrEmpty(title)) line.Append("  \u00B7  ").Append(Truncate(title, 80));
                    _webLineFull = line.ToString();
                }

                if (mi == focusedMonitor)
                {
                    string name = (wsi >= 0 && wsi < names.Count) ? names[wsi] : "";
                    if (string.IsNullOrEmpty(name)) name = (wsi + 1).ToString();
                    SetTray(name, "komorebi · 工作区 " + name + " · " + strip.DeviceName, true);
                    _statusItem.Text = "工作区 " + name + " · " + strip.DeviceName;
                }
            }
        }

        void ApplyTaskbarFilter()
        {
            if (_taskbarList == null) return;

            if (!_filterTaskbar)
            {
                foreach (var hwnd in new List<long>(_hiddenHwnds))
                {
                    try { _taskbarList.AddTab(new IntPtr(hwnd)); }
                    catch (Exception ex) { Log("taskbar show failed " + hwnd + ": " + ex.Message); }
                }
                _hiddenHwnds.Clear();
                return;
            }

            // Forget windows that no longer exist.
            var gone = new List<long>();
            foreach (var hwnd in _hiddenHwnds)
            {
                if (!_lastManaged.Contains(hwnd)) gone.Add(hwnd);
            }
            foreach (var hwnd in gone) _hiddenHwnds.Remove(hwnd);

            foreach (var hwnd in _lastManaged)
            {
                bool shouldShow = _lastVisible.Contains(hwnd);
                bool hidden = _hiddenHwnds.Contains(hwnd);

                if (shouldShow)
                {
                    if (hidden)
                    {
                        try
                        {
                            _taskbarList.AddTab(new IntPtr(hwnd));
                            _hiddenHwnds.Remove(hwnd);
                            Log("taskbar show " + hwnd);
                        }
                        catch (Exception ex) { Log("taskbar show failed " + hwnd + ": " + ex.Message); }
                    }
                }
                else if (!hidden)
                {
                    try
                    {
                        _taskbarList.DeleteTab(new IntPtr(hwnd));
                        _hiddenHwnds.Add(hwnd);
                        Log("taskbar hide " + hwnd);
                    }
                    catch (Exception ex) { Log("taskbar hide failed " + hwnd + ": " + ex.Message); }
                }
            }
        }

        static Dictionary<string, object> AsDict(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o)) return null;
            return o as Dictionary<string, object>;
        }

        static Dictionary<string, object> AsDict(object o)
        {
            return o as Dictionary<string, object>;
        }

        static object[] AsArray(Dictionary<string, object> d, string key)
        {
            object o;
            if (d == null || !d.TryGetValue(key, out o) || o == null) return null;
            var arr = o as object[];
            if (arr != null) return arr;
            var seq = o as System.Collections.IEnumerable;
            if (seq == null) return null;
            var list = new List<object>();
            foreach (var item in seq) list.Add(item);
            return list.ToArray();
        }

        static string LayoutName(object layout)
        {
            var d = layout as Dictionary<string, object>;
            if (d == null || d.Count == 0) return "";
            object v;
            if (d.TryGetValue("Default", out v)) return Convert.ToString(v);
            foreach (var kv in d) return kv.Key;
            return "";
        }

        // The layout widget should reflect monocle/maximized/floating too:
        // toggle-monocle does not change the workspace's layout field.
        static string DescribeLayout(Dictionary<string, object> ws)
        {
            if (ws == null) return "";

            object v;
            if (ws.TryGetValue("monocle_container", out v) && v != null) return "Monocle";
            if (ws.TryGetValue("maximized_window", out v) && v != null) return "Max";

            object layer;
            if (ws.TryGetValue("layer", out layer) &&
                string.Equals(Convert.ToString(layer), "Floating", StringComparison.OrdinalIgnoreCase))
            {
                return "Float";
            }

            object layout;
            return ws.TryGetValue("layout", out layout) ? LayoutName(layout) : "";
        }

        static bool IsWorkspaceOccupied(Dictionary<string, object> ws)
        {
            if (ws == null) return false;

            var containers = AsDict(ws, "containers");
            var containerEls = containers == null ? null : AsArray(containers, "elements");
            if (containerEls != null && containerEls.Length > 0) return true;

            var floating = AsDict(ws, "floating_windows");
            var floatingEls = floating == null ? null : AsArray(floating, "elements");
            if (floatingEls != null && floatingEls.Length > 0) return true;

            object monocle;
            if (ws.TryGetValue("monocle_container", out monocle) && monocle != null) return true;

            return false;
        }

        void SetTray(string text, string tooltip, bool connected)
        {
            if (_icon != null && _iconText == text && _iconTooltip == tooltip && _iconConnected == connected)
                return;
            _iconConnected = connected;
            _iconText = text;
            _iconTooltip = tooltip;
            var newIcon = RenderDigitIcon(text, connected);
            var old = _icon;
            _icon = newIcon;
            _tray.Icon = _icon;
            if (old != null) old.Dispose();
            _tray.Text = tooltip.Length > 120 ? tooltip.Substring(0, 120) : tooltip;
        }

        static Icon RenderDigitIcon(string text, bool connected)
        {
            const int size = 32;
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    Color bg = connected
                        ? Color.FromArgb(255, 0x6C, 0x4C, 0xE8)
                        : Color.FromArgb(255, 0x9E, 0x9E, 0x9E);

                    using (var path = RoundedRect(new RectangleF(1, 1, size - 2, size - 2), 8f))
                    using (var brush = new SolidBrush(bg))
                        g.FillPath(brush, path);

                    using (var font = new Font("JetBrainsMono NF", 19f, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.White))
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString(text, font, brush, new RectangleF(0, 0, size, size), sf);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return icon;
            }
        }

        internal static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            // Clamp the arc diameter: a radius larger than half the shorter side
            // makes the arcs overlap and the outline degenerates into a square.
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d < 1f || r.Width < 1f || r.Height < 1f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Length <= max) return text;
            return text.Substring(0, max - 1) + "\u2026";
        }

        void HttpWorker()
        {
            // A previous instance may still hold the preferred port (killed
            // mid-accept, or the socket handle outlived it in a child), so walk
            // a few ports before giving up on the endpoint for this session.
            for (int round = 1; round <= 3 && !_exit && _http == null; round++)
            {
                for (int offset = 0; offset < 4 && _http == null; offset++)
                {
                    int port = HttpPort + offset;
                    try
                    {
                        var listener = new TcpListener(IPAddress.Loopback, port);
                        listener.Start();
                        _http = listener;
                        Log("http listening on 127.0.0.1:" + port);
                    }
                    catch (Exception ex)
                    {
                        if (round == 1) Log("http port " + port + ": " + ex.Message);
                    }
                }

                if (_http == null)
                {
                    for (int i = 0; i < 10 && !_exit; i++) Thread.Sleep(1000);
                }
            }

            if (_http == null)
            {
                Log("http disabled for this session");
                return;
            }

            while (!_exit)
            {
                try
                {
                    using (var client = _http.AcceptTcpClient())
                    {
                        client.ReceiveTimeout = 2000;
                        client.SendTimeout = 2000;
                        using (var stream = client.GetStream())
                        {
                            stream.ReadTimeout = 2000;
                            stream.WriteTimeout = 2000;

                            var buf = new byte[2048];
                            int total = 0;
                            while (total < buf.Length)
                            {
                                int n;
                                try { n = stream.Read(buf, total, buf.Length - total); }
                                catch { break; }
                                if (n <= 0) break;
                                total += n;
                                if (total >= 4 && Encoding.ASCII.GetString(buf, 0, total).Contains("\r\n\r\n")) break;
                            }

                            string requestHead = Encoding.ASCII.GetString(buf, 0, total);
                            string payload = requestHead.StartsWith("GET /full", StringComparison.OrdinalIgnoreCase)
                                ? (_webLineFull ?? "")
                                : (_webLine ?? "");

                            byte[] body = Encoding.UTF8.GetBytes(payload);
                            string header = "HTTP/1.1 200 OK\r\n" +
                                            "Content-Type: text/plain; charset=utf-8\r\n" +
                                            "Content-Length: " + body.Length + "\r\n" +
                                            "Cache-Control: no-store\r\n" +
                                            "Connection: close\r\n\r\n";
                            byte[] head = Encoding.ASCII.GetBytes(header);
                            stream.Write(head, 0, head.Length);
                            stream.Write(body, 0, body.Length);
                            stream.Flush();
                        }
                    }
                }
                catch { }
            }
        }

        void Worker()
        {
            while (!_exit)
            {
                try
                {
                    using (var pipe = new System.IO.Pipes.NamedPipeServerStream(
                        PipeName,
                        System.IO.Pipes.PipeDirection.InOut,
                        1,
                        System.IO.Pipes.PipeTransmissionMode.Byte,
                        System.IO.Pipes.PipeOptions.None))
                    {
                        var wait = pipe.WaitForConnectionAsync();
                        Subscribe();
                        if (!wait.Wait(4000)) continue;

                        Log("subscriber connected");
                        using (var reader = new System.IO.StreamReader(pipe, Encoding.UTF8))
                        {
                            var serializer = new JavaScriptSerializer();
                            serializer.MaxJsonLength = int.MaxValue;
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (_exit) return;
                                if (string.IsNullOrEmpty(line)) continue;
                                try
                                {
                                    var obj = serializer.Deserialize<Dictionary<string, object>>(line);
                                    if (obj == null) continue;
                                    object stateObj;
                                    if (!obj.TryGetValue("state", out stateObj)) continue;
                                    var st = stateObj as Dictionary<string, object>;
                                    if (st == null) continue;
                                    lock (_sync) _state = st;
                                    _marshal.BeginInvoke((Action)delegate
                                    {
                                        try { RefreshAll(); }
                                        catch (Exception rex) { Log("refresh: " + rex.Message); }
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Log("parse: " + ex.Message);
                                }
                            }
                        }
                        Log("subscriber disconnected");
                    }
                }
                catch (Exception ex)
                {
                    Log("worker: " + ex.Message);
                }
                Thread.Sleep(1000);
            }
        }

        void Subscribe()
        {
            try
            {
                var psi = new ProcessStartInfo(Komorebic, "subscribe-pipe " + PipeName);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit(3000);
                }
            }
            catch { }
        }

        void RestartKomorebi()
        {
            try
            {
                var psi = new ProcessStartInfo(
                    "powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -File \"" + StartScript + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("restart: " + ex.Message);
            }
        }

        void ExitApp()
        {
            _exit = true;
            if (_taskbarList != null)
            {
                foreach (var hwnd in _hiddenHwnds)
                {
                    try { _taskbarList.AddTab(new IntPtr(hwnd)); } catch { }
                }
                _hiddenHwnds.Clear();
            }
            try { if (_http != null) _http.Stop(); } catch { }
            try { _timer.Stop(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            foreach (var s in _strips)
            {
                try { s.Close(); s.Dispose(); } catch { }
            }
            ExitThread();
        }
    }

    class Strip : Form
    {
        // Dracula palette for the capsule
        static readonly Color Capsule = Color.FromArgb(255, 0x1E, 0x1F, 0x29);
        static readonly Color CapsuleEdge = Color.FromArgb(255, 0x33, 0x36, 0x4A);
        static readonly Color FocusEdge = Color.FromArgb(255, 0x6C, 0x5C, 0x9E);
        static readonly Color FocusDot = Color.FromArgb(255, 0xBD, 0x93, 0xF9);
        static readonly Color FocusDotIdle = Color.FromArgb(255, 0x44, 0x47, 0x5A);
        static readonly Color TextOccupied = Color.FromArgb(255, 0xD6, 0xDB, 0xE8);
        static readonly Color TextEmpty = Color.FromArgb(255, 0x4A, 0x50, 0x64);
        static readonly Color TextActive = Color.FromArgb(255, 0xBD, 0x93, 0xF9);
        static readonly Color TextTitle = Color.FromArgb(255, 0xF8, 0xF8, 0xF2);
        static readonly Color TextDim = Color.FromArgb(255, 0x62, 0x72, 0xA4);
        static readonly Color Separator = Color.FromArgb(255, 0x44, 0x47, 0x5A);

        readonly IntPtr _taskbar;
        readonly bool _primary;
        public readonly string DeviceName;

        List<string> _names = new List<string>();
        List<bool> _occupied = new List<bool>();
        int _focused;
        string _title = "";
        string _layout = "";
        bool _light = true;
        bool _monitorFocused;
        int _refreshCount;
        bool _paintOk;
        int _paintFailures;
        Rectangle _bounds;
        bool _rendered;

        public Rectangle CurrentBounds { get { return _bounds; } }
        public IntPtr TaskbarHandle { get { return _taskbar; } }
        public bool IsPrimary { get { return _primary; } }
        public string LastLayoutLog = "";

        // Force a repaint (used after sign-in, resume, theme and display changes).
        public void InvalidateRender()
        {
            _rendered = false;
        }

        // The first Render() happens in the constructor, before the window is
        // shown; push the frame again once it is really on screen.
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
        }

        public Strip(IntPtr taskbar, string device, bool primary)
        {
            _taskbar = taskbar;
            DeviceName = device;
            _primary = primary;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TopMost = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            _bounds = new Rectangle(0, 0, 1, 1);
            RefreshBounds();
        }

        // Per-pixel alpha: the window is a layered surface, so the area around
        // the pill is genuinely transparent and shows the taskbar underneath
        // (including its acrylic/hover states). No background sampling needed.
        void ApplyLayered(Bitmap bmp)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBmp);
            try
            {
                var size = new Native.SIZE { CX = bmp.Width, CY = bmp.Height };
                var src = new Native.POINT { X = 0, Y = 0 };
                var dst = new Native.POINT { X = _bounds.X, Y = _bounds.Y };
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = Native.AC_SRC_ALPHA
                };
                bool ok = Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size,
                    memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    InfoContext.Log("ULW failed on " + DeviceName + " size=" + size.CX + "x" + size.CY +
                        " dst=" + dst.X + "," + dst.Y + " err=" + err);
                }
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBmp);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        const int WM_NCHITTEST = 0x0084;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int WM_THEMECHANGED = 0x031A;
        const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
        const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        const int HTTRANSPARENT = -1;
        const int MA_NOACTIVATE = 3;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTTRANSPARENT); return; }
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }

            // Theme / composition changes alter the taskbar colours under us.
            if (m.Msg == WM_THEMECHANGED || m.Msg == WM_DWMCOMPOSITIONCHANGED ||
                m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED)
            {
                InvalidateRender();
                return;
            }

            base.WndProc(ref m);
        }

        public void RefreshBounds()
        {
            RECT tr;
            if (!Native.GetWindowRect(_taskbar, out tr)) return;

            int startLeft = tr.Right;
            IntPtr start = Native.FindWindowEx(_taskbar, IntPtr.Zero, "Start", null);
            if (start != IntPtr.Zero)
            {
                RECT sr;
                if (Native.GetWindowRect(start, out sr)) startLeft = sr.Left;
            }

            int inset = 4;
            int left = _primary && WidgetsOn() ? 200 : 8;
            int maxWidth = startLeft - 8 - left;
            int height = tr.Bottom - tr.Top - inset * 2;
            if (maxWidth < 140 || height < 20) return;

            float fontPx = Math.Max(10f, height * 0.36f);
            float pad = Math.Max(10f, height * 0.22f);
            float step = fontPx * 1.10f;

            float dot = Math.Max(7f, height * 0.18f);
            float content = pad + dot + pad * 0.55f + _names.Count * step + pad * 1.4f;
            if (!string.IsNullOrEmpty(_title))
                content += Math.Min(360f, EstimateTextWidth(_title, fontPx));
            if (!string.IsNullOrEmpty(_layout))
                content += EstimateTextWidth(_layout, fontPx) + pad;

            int width = Math.Min(maxWidth, Math.Max(140, (int)Math.Ceiling(content + pad)));

            var desired = new Rectangle(left, tr.Top + inset, width, height);
            bool changed = desired.Width != _bounds.Width || desired.Height != _bounds.Height ||
                           desired.X != _bounds.X || desired.Y != _bounds.Y;
            _bounds = desired;

            _refreshCount++;
            if (changed || !_rendered)
            {
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, desired.X, desired.Y, desired.Width, desired.Height,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                Render();
            }
            else if (_refreshCount % 5 == 0)
            {
                // Re-assert topmost every few ticks so we stay above other
                // always-on-top windows, without a SetWindowPos on every tick.
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
        }

        static bool WidgetsOn()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("TaskbarDa");
                        if (v is int) return (int)v != 0;
                    }
                }
            }
            catch { }
            return true;
        }

        static float EstimateTextWidth(string text, float fontPx)
        {
            float units = 0f;
            foreach (char ch in text) units += ch >= 0x2E80 ? 2f : 1f;
            return units * fontPx * 0.62f;
        }

        public void SetModel(List<string> names, int focused, string title, string layout, bool light,
                             List<bool> occupied, bool monitorFocused)
        {
            title = title ?? "";
            layout = layout ?? "";
            occupied = occupied ?? new List<bool>();

            bool changed = light != _light || focused != _focused ||
                           title != _title || layout != _layout ||
                           monitorFocused != _monitorFocused ||
                           names.Count != _names.Count || occupied.Count != _occupied.Count;
            if (!changed)
            {
                for (int i = 0; i < names.Count; i++)
                {
                    if (names[i] != _names[i]) { changed = true; break; }
                }
            }
            if (!changed)
            {
                for (int i = 0; i < occupied.Count; i++)
                {
                    if (occupied[i] != _occupied[i]) { changed = true; break; }
                }
            }
            if (!changed) return;

            _names = names;
            _focused = focused;
            _title = title;
            _layout = layout;
            _light = light;
            _occupied = occupied;
            _monitorFocused = monitorFocused;

            RefreshBounds();
            Render();
        }

        // Layered windows do not receive WM_PAINT; Render() pushes the whole
        // frame with UpdateLayeredWindow instead.
        protected override void OnPaint(PaintEventArgs e)
        {
        }

        void Render()
        {
            int w = _bounds.Width;
            int h = _bounds.Height;
            if (w <= 1 || h <= 1) return;

            _paintOk = false;
            try
            {
                using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        RenderInto(g, w, h);
                    }
                    ApplyLayered(bmp);
                }
                _paintOk = true;
            }
            catch (Exception ex)
            {
                _paintFailures++;
                if (_paintFailures <= 5 || _paintFailures % 60 == 0)
                    InfoContext.Log("paint #" + _paintFailures + ": " + ex);
            }

            // A failed paint is retried by the next tick instead of leaving a
            // stale frame on the taskbar until the model changes.
            _rendered = _paintOk;
        }

        void RenderInto(Graphics g, int w, int h)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // ClearType cannot be used on a transparent surface.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using (var fill = new SolidBrush(Capsule))
            using (var pill = InfoContext.RoundedRect(new RectangleF(0, 0, w, h), h / 2f))
                g.FillPath(fill, pill);

            Color edgeColor = _monitorFocused ? FocusEdge : CapsuleEdge;
            float edgeWidth = _monitorFocused ? 1.6f : 1f;
            using (var edge = new Pen(edgeColor, edgeWidth))
            using (var outline = InfoContext.RoundedRect(
                new RectangleF(edgeWidth / 2f, edgeWidth / 2f, w - edgeWidth, h - edgeWidth), h / 2f))
                g.DrawPath(edge, outline);

            float fontPx = Math.Max(10f, h * 0.36f);
            using (var font = new Font("JetBrainsMono NF", fontPx, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var bold = new Font("JetBrainsMono NF", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var occupiedBrush = new SolidBrush(TextOccupied))
            using (var emptyBrush = new SolidBrush(TextEmpty))
            using (var activeBrush = new SolidBrush(TextActive))
            using (var titleBrush = new SolidBrush(TextTitle))
            using (var dimBrush = new SolidBrush(TextDim))
            using (var sfCenter = new StringFormat())
            using (var sfTitle = new StringFormat())
            using (var sfRight = new StringFormat())
            {
                sfCenter.Alignment = StringAlignment.Center;
                sfCenter.LineAlignment = StringAlignment.Center;

                sfTitle.Alignment = StringAlignment.Near;
                sfTitle.LineAlignment = StringAlignment.Center;
                sfTitle.Trimming = StringTrimming.EllipsisCharacter;
                sfTitle.FormatFlags = StringFormatFlags.NoWrap;

                sfRight.Alignment = StringAlignment.Far;
                sfRight.LineAlignment = StringAlignment.Center;
                sfRight.Trimming = StringTrimming.EllipsisCharacter;
                sfRight.FormatFlags = StringFormatFlags.NoWrap;

                float pad = Math.Max(10f, h * 0.22f);
                float step = fontPx * 1.10f;
                float x = pad;

                // Which monitor komorebi has focused: a filled purple dot on the
                // focused monitor's taskbar, a muted one on the others.
                float dot = Math.Max(7f, h * 0.18f);
                using (var dotBrush = new SolidBrush(_monitorFocused ? FocusDot : FocusDotIdle))
                    g.FillEllipse(dotBrush, x, (h - dot) / 2f, dot, dot);
                x += dot + pad * 0.55f;

                for (int i = 0; i < _names.Count; i++)
                {
                    string label = _names[i];
                    if (string.IsNullOrEmpty(label)) label = (i + 1).ToString();
                    if (label.Length > 2) label = (i + 1).ToString();

                    bool active = i == _focused;
                    bool occ = i < _occupied.Count && _occupied[i];
                    var digitBrush = active ? activeBrush : (occ ? occupiedBrush : emptyBrush);
                    g.DrawString(label, active ? bold : font, digitBrush,
                        new RectangleF(x, 0, step, h), sfCenter);
                    x += step;
                }

                x += pad * 0.6f;
                using (var pen = new Pen(Separator, 1f))
                    g.DrawLine(pen, x, h * 0.3f, x, h * 0.7f);
                x += pad * 0.8f;

                float layoutWidth = 0f;
                if (!string.IsNullOrEmpty(_layout))
                {
                    layoutWidth = g.MeasureString(_layout, font).Width;
                    g.DrawString(_layout, font, dimBrush,
                        new RectangleF(w - pad - layoutWidth - 2, 0, layoutWidth + 2, h), sfRight);
                    layoutWidth += pad;
                }

                float titleRight = w - pad - layoutWidth;
                float titleWidth = titleRight - x;
                if (titleWidth > 30 && !string.IsNullOrEmpty(_title))
                {
                    g.DrawString(_title, font, titleBrush,
                        new RectangleF(x, 0, titleWidth, h), sfTitle);
                }
            }
        }
    }

    static class Native
    {
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int CX, CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string cls, string win);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string win);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        public static extern bool GetMonitorInfoEx(IntPtr h, ref MONITORINFOEX mi);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

        // --- per-pixel alpha (UpdateLayeredWindow) ---

        public const int WS_EX_LAYERED = 0x00080000;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const uint ULW_ALPHA = 0x00000002;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dstPos,
            ref SIZE size, IntPtr srcDc, ref POINT srcPos, int colorKey, ref BLENDFUNCTION blend, uint flags);

        public static string ClassNameOf(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        public static string MonitorDeviceOf(IntPtr hwnd)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            if (!GetMonitorInfoEx(mon, ref mi)) return null;
            string device = mi.szDevice ?? "";
            if (device.StartsWith(@"\\.\")) device = device.Substring(4);
            return device;
        }
    }
}
