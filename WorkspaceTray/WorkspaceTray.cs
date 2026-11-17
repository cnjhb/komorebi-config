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
        HashSet<long> _lastManaged = new HashSet<long>();
        HashSet<long> _lastVisible = new HashSet<long>();

        Dictionary<string, object> _state;
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
            _timer.Interval = 400;
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
                RebuildStrips();
                ReadTheme();

                bool fullscreen = IsFullscreenForeground();
                if (fullscreen != _fullscreen)
                {
                    _fullscreen = fullscreen;
                    foreach (var s in _strips) s.Visible = !_fullscreen;
                }

                foreach (var s in _strips)
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
            // komorebi state and are left alone.
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

            foreach (var strip in _strips)
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
                    if (focusedWs != null) layout = LayoutName(focusedWs["layout"]);
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

                strip.SetModel(names, wsi, title, layout, _light, occupied);

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

                // The taskbar clock lives on the primary monitor's taskbar, so
                // "/" serves that monitor (index 0) rather than the focused one.
                if (mi == 0)
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
            float d = radius * 2f;
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
            try
            {
                _http = new TcpListener(IPAddress.Loopback, HttpPort);
                _http.Start();
                Log("http listening on 127.0.0.1:" + HttpPort);
            }
            catch (Exception ex)
            {
                Log("http start: " + ex.Message);
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
        static readonly Color TextOccupied = Color.FromArgb(255, 0xD6, 0xDB, 0xE8);
        static readonly Color TextEmpty = Color.FromArgb(255, 0x4A, 0x50, 0x64);
        static readonly Color TextActive = Color.FromArgb(255, 0xBD, 0x93, 0xF9);
        static readonly Color TextTitle = Color.FromArgb(255, 0xF8, 0xF8, 0xF2);
        static readonly Color TextDim = Color.FromArgb(255, 0x62, 0x72, 0xA4);
        static readonly Color Separator = Color.FromArgb(255, 0x44, 0x47, 0x5A);

        Color _bgTop;
        Color _bgMid;
        Color _bgBottom;
        DateTime _lastSample = DateTime.MinValue;

        readonly IntPtr _taskbar;
        readonly bool _primary;
        public readonly string DeviceName;

        List<string> _names = new List<string>();
        List<bool> _occupied = new List<bool>();
        int _focused;
        string _title = "";
        string _layout = "";
        bool _light = true;
        Rectangle _bounds;
        bool _rendered;

        public Rectangle CurrentBounds { get { return _bounds; } }
        public IntPtr TaskbarHandle { get { return _taskbar; } }

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
            var initialBg = IsLightTheme() ? Color.FromArgb(255, 249, 249, 249) : Color.FromArgb(255, 43, 43, 43);
            _bgTop = _bgMid = _bgBottom = initialBg;
            BackColor = initialBg;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            _bounds = new Rectangle(0, 0, 1, 1);
            RefreshBounds();
        }

        static bool IsLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        var v = key.GetValue("SystemUsesLightTheme");
                        if (v is int) return (int)v != 0;
                    }
                }
            }
            catch { }
            return true;
        }

        static int ColorDiff(Color a, Color b)
        {
            return Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));
        }

        // Samples the taskbar background (top/middle/bottom) in the empty gap
        // to the right of the strip. The window itself stays rectangular and
        // paints this gradient, so the anti-aliased capsule corners blend into
        // the taskbar instead of being clipped by a hard 1-bit window region.
        void SampleBackground(RECT taskbar, int startLeft)
        {
            if ((DateTime.Now - _lastSample).TotalSeconds < 2) return;
            _lastSample = DateTime.Now;

            int x1 = _bounds.Right + 8;
            int x2 = startLeft - 8;
            if (x2 - x1 < 16) return;

            int x = (x1 + x2) / 2;
            int top = taskbar.Top + 6;
            int bottom = taskbar.Bottom - 6;
            int mid = (top + bottom) / 2;

            try
            {
                using (var bmp = new Bitmap(1, 3))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, top, 0, 0, new Size(1, 1));
                    g.CopyFromScreen(x, mid, 0, 1, new Size(1, 1));
                    g.CopyFromScreen(x, bottom, 0, 2, new Size(1, 1));

                    var c1 = bmp.GetPixel(0, 0);
                    var c2 = bmp.GetPixel(0, 1);
                    var c3 = bmp.GetPixel(0, 2);

                    if (ColorDiff(c1, _bgTop) > 3 || ColorDiff(c2, _bgMid) > 3 || ColorDiff(c3, _bgBottom) > 3)
                    {
                        _bgTop = c1;
                        _bgMid = c2;
                        _bgBottom = c3;
                        BackColor = c2;
                        Render();
                    }
                }
            }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        const int WM_NCHITTEST = 0x0084;
        const int WM_MOUSEACTIVATE = 0x0021;
        const int HTTRANSPARENT = -1;
        const int MA_NOACTIVATE = 3;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTTRANSPARENT); return; }
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = new IntPtr(MA_NOACTIVATE); return; }
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

            float content = pad + _names.Count * step + pad * 1.4f;
            if (!string.IsNullOrEmpty(_title))
                content += Math.Min(360f, EstimateTextWidth(_title, fontPx));
            if (!string.IsNullOrEmpty(_layout))
                content += EstimateTextWidth(_layout, fontPx) + pad;

            int width = Math.Min(maxWidth, Math.Max(140, (int)Math.Ceiling(content + pad)));

            var desired = new Rectangle(left, tr.Top + inset, width, height);
            bool changed = desired.Width != _bounds.Width || desired.Height != _bounds.Height ||
                           desired.X != _bounds.X || desired.Y != _bounds.Y;
            _bounds = desired;

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, desired.X, desired.Y, desired.Width, desired.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

            SampleBackground(tr, startLeft);

            if (changed || !_rendered)
            {
                Render();
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
                             List<bool> occupied)
        {
            title = title ?? "";
            layout = layout ?? "";
            occupied = occupied ?? new List<bool>();

            bool changed = light != _light || focused != _focused ||
                           title != _title || layout != _layout ||
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
            Render();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            RenderInto(e.Graphics, _bounds.Width, _bounds.Height);
        }

        void Render()
        {
            if (_bounds.Width <= 0 || _bounds.Height <= 0) return;
            Invalidate();
            Update();
            _rendered = true;
        }

        void RenderInto(Graphics g, int w, int h)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            // The window is rectangular; paint a taskbar-coloured gradient so
            // the anti-aliased capsule edge blends into the taskbar instead of
            // being clipped by a jagged 1-bit window region.
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, w, h), _bgTop, _bgBottom, 90f))
            {
                var blend = new ColorBlend(3);
                blend.Colors = new[] { _bgTop, _bgMid, _bgBottom };
                blend.Positions = new[] { 0f, 0.5f, 1f };
                bg.InterpolationColors = blend;
                g.FillRectangle(bg, 0, 0, w, h);
            }

            using (var fill = new SolidBrush(Capsule))
            using (var pill = InfoContext.RoundedRect(new RectangleF(0, 0, w, h), h / 2f))
                g.FillPath(fill, pill);

            using (var edge = new Pen(CapsuleEdge, 1f))
            using (var outline = InfoContext.RoundedRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), h / 2f))
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
