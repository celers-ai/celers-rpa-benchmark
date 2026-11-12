// Celers Session Host: the server half of the Remote Lab (Celers RPA Benchmark), a stand-in for a Citrix server.
//
// It signs users in, starts the published apps for each session in a desktop of their own (off every monitor on
// this computer, so only the workspace shows them), sends each window's picture as compressed frames when it
// changes, and passes the workspace's mouse and keys to the apps. A session outlives its connection: the
// workspace reconnects with its token and finds the apps as it left them.
//
//   SessionHost.exe [--port 7480] [--bind 0.0.0.0] [--users celers:lab;...] [--app "Name=C:\path\app.exe|arguments"]...
//                   [--res 1366x768] [--origin -30000,0] [--fps 8] [--quality 55] [--latency 0] [--jitter 0]
//                   [--drop 0] [--freeze 30:3] [--idle 600] [--disconnect-every 0] [--res-every 0] [--log FILE]

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Celers.RemoteLab
{
    static class N
    {
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT p, uint flags);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public Win.RECT rcCaret;
        }

        public const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203,
                          WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MOUSEWHEEL = 0x20A, WM_KEYDOWN = 0x100,
                          WM_KEYUP = 0x101, WM_CHAR = 0x102, WM_SYSKEYDOWN = 0x104, WM_CLOSE = 0x10;

        public static IntPtr L(int lo, int hi) { return (IntPtr)((hi << 16) | (lo & 0xFFFF)); }

        /// <summary>One input, handled before the next (queued together, a box that handles a key itself threw away the
        /// characters behind it); at most half a second, so a click that opens a modal dialog does not hold the host.</summary>
        public static void Deliver(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            IntPtr r;
            if (SendMessageTimeout(hwnd, msg, w, l, 0x0002, 500, out r) == IntPtr.Zero) PostMessage(hwnd, msg, w, l);
        }

        /// <summary>A key through the app's own queue, where a dialog's message loop sees it (Enter, Alt+N).</summary>
        public static void Queue(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
        {
            PostMessage(hwnd, msg, w, l);
            Thread.Sleep(60);
        }
    }

    class App
    {
        public string Name, Exe, Args;
    }

    class Settings
    {
        public int Port = 7480, Fps = 8, Quality = 55, Latency, Jitter, Drop, FreezeEvery, FreezeFor,
                   Idle = 600, DisconnectEvery, Keep = 600, ResEvery;
        public Size Res = new Size(1366, 768);
        public Point Origin = new Point(-30000, 0);  // off every monitor: only the workspace shows the session
        public Dictionary<string, string> Users = new Dictionary<string, string>();
        public List<App> Apps = new List<App>();
        public string Log = "";
        public IPAddress Bind = IPAddress.Any;  // 127.0.0.1 for a lab on one computer (no firewall question)
    }

    class Session
    {
        public string Token, User;
        public Link Link;
        public Point Origin;  // where this session's desktop is on the host: each session its own, side by side
        public Size Res;      // this session's resolution (it changes with --res-every, as a resized Citrix desktop's)
        public readonly List<Process> Procs = new List<Process>();
        public readonly Dictionary<int, string> AppOf = new Dictionary<int, string>();
        public readonly Dictionary<IntPtr, int> Ids = new Dictionary<IntPtr, int>();
        public readonly Dictionary<IntPtr, uint> Hash = new Dictionary<IntPtr, uint>();
        public int NextId = 1;
        public string LastWindows = "";
        public long LastInput = Environment.TickCount, DisconnectedAt;
        public IntPtr Clicked; public int ClickedAt;
        public bool Ended;
        public readonly object Gate = new object();
    }

    class Host
    {
        readonly Settings cfg;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly Random rnd = new Random(7);
        readonly ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        readonly long started = Environment.TickCount;

        public Host(Settings cfg) { this.cfg = cfg; }

        public void Log(string evt, params object[] kv)
        {
            var d = Wire.Obj(kv);
            d["event"] = evt;
            d["at"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            string line = Wire.Json.Serialize(d);
            Console.WriteLine(line);
            if (cfg.Log == "") return;
            for (int i = 0; i < 20; i++)
            {
                try { File.AppendAllText(cfg.Log, line + "\n"); return; } catch (IOException) { Thread.Sleep(25); }
            }
        }

        public void Run()
        {
            var listener = new TcpListener(cfg.Bind, cfg.Port);
            listener.Start();
            Log("host_started", "port", cfg.Port, "apps", string.Join(",", cfg.Apps.Select(a => a.Name)));
            new Thread(FrameLoop) { IsBackground = true, Name = "frames" }.Start();
            new Thread(SessionLoop) { IsBackground = true, Name = "sessions" }.Start();
            while (true)
            {
                var tcp = listener.AcceptTcpClient();
                var link = new Link(tcp) { Latency = cfg.Latency, Jitter = cfg.Jitter };
                link.Received += (k, p) => OnMessage(link, k, p);
                link.Closed += () => OnClosed(link);
                link.Start();
            }
        }

        // ── sign-in and the session's life ───────────────────────────────────────

        void OnMessage(Link link, byte kind, byte[] p)
        {
            if (kind != (byte)'J') return;
            var m = Wire.Parse(Encoding.UTF8.GetString(p));
            string t = Wire.Str(m, "t");
            var s = link.Tag as Session;
            if (t == "hello")
            {
                string user = Wire.Str(m, "user"), pw = Wire.Str(m, "password"), want;
                if (!cfg.Users.TryGetValue(user.ToLowerInvariant(), out want) || want != pw)
                {
                    Log("sign_in_refused", "user", user);
                    link.SendJson(Wire.Obj("t", "denied", "why", "The user name or password is not right."));
                    return;
                }
                s = new Session { Token = Guid.NewGuid().ToString("N"), User = user };
                lock (sessions)
                {
                    // Several users signed in at once (a bot each, as on a Citrix or RDS server): each session's
                    // desktop in its own place, so their windows never overlap.
                    int slot = 0;
                    int step = Math.Max(cfg.Res.Width, 1920) + 200;  // room for the larger resolution too
                    while (sessions.Values.Any(o => !o.Ended && o.Origin.X == cfg.Origin.X + slot * step)) slot++;
                    s.Origin = new Point(cfg.Origin.X + slot * step, cfg.Origin.Y);
                    s.Res = cfg.Res;
                    sessions[s.Token] = s;
                }
                Attach(s, link);
                Log("signed_in", "user", user, "session", s.Token.Substring(0, 8));
                return;
            }
            if (t == "resume")
            {
                Session old;
                lock (sessions) sessions.TryGetValue(Wire.Str(m, "token"), out old);
                if (old == null || old.Ended)
                {
                    link.SendJson(Wire.Obj("t", "ended", "why", "This session has ended. Sign in again."));
                    return;
                }
                Attach(old, link);
                Log("reconnected", "user", old.User, "session", old.Token.Substring(0, 8));
                return;
            }
            if (s == null) return;
            if (t != "ping") s.LastInput = Environment.TickCount;
            switch (t)
            {
                case "launch": Launch(s, Wire.Str(m, "app")); break;
                case "mouse": Mouse(s, m); break;
                case "key": Key(s, m); break;
                case "char": Char(s, Wire.Int(m, "c")); break;
                case "clip": SetClipboard(Wire.Str(m, "text")); break;
                case "close": Close(s, Wire.Int(m, "id")); break;
                case "bye": End(s, "signed out"); break;
            }
        }

        void Attach(Session s, Link link)
        {
            lock (s.Gate)
            {
                if (s.Link != null && s.Link != link) s.Link.Close();
                s.Link = link;
                s.LastWindows = "";
                s.Hash.Clear();  // every window's picture again on the new connection
                s.DisconnectedAt = 0;
            }
            link.Tag = s;
            link.SendJson(Wire.Obj("t", "welcome", "token", s.Token, "user", s.User, "apps", cfg.Apps.Select(a => a.Name).ToArray(),
                                   "res", new[] { s.Res.Width, s.Res.Height }, "idle", cfg.Idle));
        }

        void OnClosed(Link link)
        {
            var s = link.Tag as Session;
            if (s == null) return;
            lock (s.Gate)
            {
                if (s.Link != link) return;
                s.Link = null;
                s.DisconnectedAt = Environment.TickCount;
            }
            if (!s.Ended) Log("disconnected", "user", s.User, "session", s.Token.Substring(0, 8));
        }

        void End(Session s, string why)
        {
            if (s.Ended) return;
            s.Ended = true;
            Log("session_ended", "user", s.User, "why", why);
            var link = s.Link;
            if (link != null)
            {
                link.SendJson(Wire.Obj("t", "ended", "why", why));
                new Thread(() => { Thread.Sleep(1500 + cfg.Latency + cfg.Jitter); link.Close(); }) { IsBackground = true }.Start();
            }
            foreach (var p in s.Procs.ToArray())
            {
                try { if (!p.HasExited) { p.CloseMainWindow(); if (!p.WaitForExit(2000)) p.Kill(); } } catch { }
            }
            lock (sessions) sessions.Remove(s.Token);
        }

        /// <summary>Idle sign-out, the network drops --disconnect-every asks for, and sessions left disconnected.</summary>
        void SessionLoop()
        {
            long lastDrop = Environment.TickCount, lastRes = Environment.TickCount;
            while (true)
            {
                Thread.Sleep(1000);
                Session[] all;
                lock (sessions) all = sessions.Values.ToArray();
                foreach (var s in all)
                {
                    if (cfg.Idle > 0 && unchecked(Environment.TickCount - s.LastInput) > cfg.Idle * 1000L)
                        End(s, "idle for " + (cfg.Idle >= 120 ? cfg.Idle / 60 + " minutes" : cfg.Idle + " seconds"));
                    else if (s.Link == null && s.DisconnectedAt != 0 && unchecked(Environment.TickCount - s.DisconnectedAt) > cfg.Keep * 1000L)
                        End(s, "left disconnected");
                }
                if (cfg.ResEvery > 0 && unchecked(Environment.TickCount - lastRes) > cfg.ResEvery * 1000L)
                {
                    lastRes = Environment.TickCount;
                    var other = cfg.Res.Width == 1920 ? new Size(1366, 768) : new Size(1920, 1080);
                    foreach (var s in all) ChangeRes(s, s.Res == cfg.Res ? other : cfg.Res);
                }
                if (cfg.DisconnectEvery > 0 && unchecked(Environment.TickCount - lastDrop) > cfg.DisconnectEvery * 1000L)
                {
                    lastDrop = Environment.TickCount;
                    foreach (var s in all)
                        if (s.Link != null) { Log("network_dropped", "user", s.User); s.Link.Close(); }
                }
            }
        }

        /// <summary>The session's resolution changed (a Citrix desktop resized, a reconnect from another monitor): the
        /// apps' windows kept inside it, the workspace told.</summary>
        void ChangeRes(Session s, Size size)
        {
            s.Res = size;
            Log("resolution", "user", s.User, "res", size.Width + "x" + size.Height);
            Send(s, Wire.Obj("t", "res", "res", new[] { size.Width, size.Height }));
            s.LastWindows = "";  // every window's place sent again
        }

        void Launch(Session s, string name)
        {
            var app = cfg.Apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (app == null) { Send(s, Wire.Obj("t", "launched", "app", name, "ok", false, "why", "No app named " + name)); return; }
            lock (s.Gate)
            {
                s.Procs.RemoveAll(p => { try { return p.HasExited; } catch { return true; } });
                if (s.Procs.Any(p => s.AppOf.ContainsKey(p.Id) && s.AppOf[p.Id] == app.Name))
                {
                    Send(s, Wire.Obj("t", "launched", "app", app.Name, "ok", true, "again", true));
                    return;
                }
            }
            string args = app.Args + " --at " + s.Origin.X + "," + s.Origin.Y + " --size " + s.Res.Width + "x" + s.Res.Height;
            var si = new ProcessStartInfo(app.Exe, args) { UseShellExecute = false };
            si.EnvironmentVariables["CELERS_LAB_APP"] = app.Name;
            try
            {
                var p = Process.Start(si);
                lock (s.Gate) { s.Procs.Add(p); s.AppOf[p.Id] = app.Name; }
                Log("app_started", "user", s.User, "app", app.Name, "pid", p.Id);
                Send(s, Wire.Obj("t", "launched", "app", app.Name, "ok", true));
            }
            catch (Exception e)
            {
                Send(s, Wire.Obj("t", "launched", "app", app.Name, "ok", false, "why", e.Message));
            }
        }

        void Send(Session s, Dictionary<string, object> m)
        {
            var l = s.Link;
            if (l != null) l.SendJson(m);
        }

        // ── the pictures ────────────────────────────────────────────────────────

        List<IntPtr> Windows(Session s)
        {
            var pids = new HashSet<uint>();
            lock (s.Gate)
                foreach (var p in s.Procs) { try { if (!p.HasExited) pids.Add((uint)p.Id); } catch { } }
            var found = new List<IntPtr>();
            if (pids.Count == 0) return found;
            Win.EnumWindows((h, l) =>
            {
                uint pid;
                Win.GetWindowThreadProcessId(h, out pid);
                if (pids.Contains(pid) && Win.IsWindowVisible(h)) found.Add(h);
                return true;
            }, IntPtr.Zero);
            return found;  // top of the z-order first
        }

        void FrameLoop()
        {
            while (true)
            {
                Thread.Sleep(Math.Max(30, 1000 / Math.Max(1, cfg.Fps)));
                // --freeze 30:3: the picture stops for 3 s every 30 s (a congested link).
                if (cfg.FreezeEvery > 0)
                {
                    long t = unchecked(Environment.TickCount - started) % (cfg.FreezeEvery * 1000L);
                    if (t < cfg.FreezeFor * 1000L) continue;
                }
                Session[] all;
                lock (sessions) all = sessions.Values.ToArray();
                foreach (var s in all)
                {
                    var link = s.Link;
                    if (link == null || s.Ended) continue;
                    try { Frames(s, link); } catch (Exception e) { Log("frame_error", "why", e.Message); }
                }
            }
        }

        void Frames(Session s, Link link)
        {
            var wins = Windows(s);
            var list = new List<object>();
            int z = 0;
            foreach (var h in wins)
            {
                HideFromTaskbar(h);
                IntoSession(s, h);
                Win.RECT r;
                if (!Win.GetWindowRect(h, out r)) continue;
                int id;
                if (!s.Ids.TryGetValue(h, out id)) { id = s.NextId++; s.Ids[h] = id; }
                list.Add(Wire.Obj("id", id, "title", Win.Title(h), "z", z++,
                                  "rect", new[] { r.Left - s.Origin.X, r.Top - s.Origin.Y, r.Right - r.Left, r.Bottom - r.Top }));
            }
            string shown = Wire.Json.Serialize(list);
            if (shown != s.LastWindows)
            {
                s.LastWindows = shown;
                link.SendJson(Wire.Obj("t", "windows", "list", list));
            }
            foreach (var gone in s.Ids.Keys.Where(h => !wins.Contains(h)).ToList()) { s.Ids.Remove(gone); s.Hash.Remove(gone); }
            if (link.Pending > 6) return;  // the network is behind: this frame is skipped, the next one shows the change
            foreach (var h in wins)
            {
                Win.RECT r;
                if (!Win.GetWindowRect(h, out r)) continue;
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w <= 0 || ht <= 0 || w > 8000 || ht > 8000) continue;
                using (var shot = new Bitmap(w, ht, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(shot))
                    {
                        N.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0100 | 0x0080);
                        IntPtr hdc = g.GetHdc();
                        N.PrintWindow(h, hdc, 2 /* PW_RENDERFULLCONTENT */);
                        g.ReleaseHdc(hdc);
                    }
                    uint hash = HashOf(shot);
                    uint old;
                    if (s.Hash.TryGetValue(h, out old) && old == hash) continue;
                    if (cfg.Drop > 0 && rnd.Next(100) < cfg.Drop) continue;  // lost: sent with a later frame
                    s.Hash[h] = hash;
                    var ms = new MemoryStream();
                    var ps = new EncoderParameters(1);
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)cfg.Quality);
                    shot.Save(ms, jpeg, ps);
                    link.SendFrame(Wire.Obj("id", s.Ids[h], "w", w, "h", ht), ms.ToArray());
                }
            }
        }

        static uint HashOf(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int n = data.Stride * data.Height / 4;
                var ints = new int[n];
                Marshal.Copy(data.Scan0, ints, 0, n);
                uint h = 2166136261;
                for (int i = 0; i < n; i++) h = (h ^ (uint)ints[i]) * 16777619;
                return h ^ (uint)(b.Width * 31 + b.Height);
            }
            finally { b.UnlockBits(data); }
        }

        /// <summary>A window Windows put on the real monitor (a dialog centred over an owner it would not show off
        /// screen) moved into the session, at the same place on it.</summary>
        void IntoSession(Session s, IntPtr h)
        {
            Win.RECT r;
            if (!Win.GetWindowRect(h, out r)) return;
            bool inside = r.Left >= s.Origin.X - 50 && r.Right <= s.Origin.X + s.Res.Width + 50 &&
                          r.Top >= s.Origin.Y - 50 && r.Top < s.Origin.Y + s.Res.Height - 40;
            if (inside) return;
            // On the session at the same place, kept inside it (a smaller resolution: moved in from the right edge).
            int left = r.Left - s.Origin.X, top = r.Top - s.Origin.Y, w = r.Right - r.Left;
            if (left < -50 || left > 4000) left = r.Left;  // put on the real monitor by Windows: its own place there
            if (top < -50 || top > 3000) top = r.Top;
            int x = Math.Max(0, Math.Min(Math.Max(0, s.Res.Width - w), left));
            int y = Math.Max(0, Math.Min(s.Res.Height - 60, top));
            N.SetWindowPos(h, IntPtr.Zero, s.Origin.X + x, s.Origin.Y + y, 0, 0, 0x0001 | 0x0004 | 0x0010);
        }

        static void HideFromTaskbar(IntPtr h)
        {
            int ex = N.GetWindowLong(h, -20);
            if ((ex & 0x80) == 0) N.SetWindowLong(h, -20, (ex | 0x80) & ~0x40000);
        }

        // ── input ───────────────────────────────────────────────────────────────

        IntPtr Target(Session s, Point at, out N.POINT client)
        {
            client = new N.POINT(at.X, at.Y);
            foreach (var top in Windows(s))
            {
                Win.RECT r;
                if (!Win.GetWindowRect(top, out r) || at.X < r.Left || at.X >= r.Right || at.Y < r.Top || at.Y >= r.Bottom) continue;
                IntPtr h = top;
                while (true)
                {
                    var pt = new N.POINT(at.X, at.Y);
                    N.ScreenToClient(h, ref pt);
                    IntPtr child = N.ChildWindowFromPointEx(h, pt, 1);
                    if (child == IntPtr.Zero || child == h) break;
                    h = child;
                }
                client = new N.POINT(at.X, at.Y);
                N.ScreenToClient(h, ref client);
                return h;
            }
            return IntPtr.Zero;
        }

        void Mouse(Session s, Dictionary<string, object> m)
        {
            var at = new Point(s.Origin.X + Wire.Int(m, "x"), s.Origin.Y + Wire.Int(m, "y"));
            N.POINT c;
            IntPtr h = Target(s, at, out c);
            if (h == IntPtr.Zero) return;
            string kind = Wire.Str(m, "kind");
            bool right = Wire.Str(m, "btn") == "r";
            if (kind == "wheel") { N.Deliver(h, N.WM_MOUSEWHEEL, (IntPtr)(Wire.Int(m, "delta") << 16), N.L(at.X, at.Y)); return; }
            if (kind == "down")
            {
                s.Clicked = h; s.ClickedAt = Environment.TickCount;
                // The window pressed comes to the front of the session, as a click brings a window forward.
                N.SetWindowPos(N.GetAncestor(h, 2), IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            }
            var lp = N.L(c.X, c.Y);
            switch (kind)
            {
                case "move": N.Deliver(h, N.WM_MOUSEMOVE, IntPtr.Zero, lp); break;
                case "down": N.Deliver(h, right ? N.WM_RBUTTONDOWN : N.WM_LBUTTONDOWN, (IntPtr)(right ? 2 : 1), lp); break;
                case "up": N.Deliver(h, right ? N.WM_RBUTTONUP : N.WM_LBUTTONUP, IntPtr.Zero, lp); break;
                case "dbl": N.Deliver(h, N.WM_LBUTTONDBLCLK, (IntPtr)1, lp); break;  // its button-up comes next
            }
        }

        uint ThreadOf(IntPtr h) { uint pid; return h == IntPtr.Zero ? 0 : Win.GetWindowThreadProcessId(h, out pid); }

        bool Ours(Session s, IntPtr h)
        {
            if (h == IntPtr.Zero || !N.IsWindow(h)) return false;
            uint pid;
            Win.GetWindowThreadProcessId(h, out pid);
            lock (s.Gate) return s.Procs.Any(p => { try { return !p.HasExited && (uint)p.Id == pid; } catch { return false; } });
        }

        /// <summary>Where a key goes: the session's focused control (a person's last click, a dialog it opened).</summary>
        IntPtr Focused(Session s)
        {
            var wins = Windows(s);
            if (wins.Count == 0) return IntPtr.Zero;
            var info = new N.GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(N.GUITHREADINFO)) };
            uint thread = ThreadOf(s.Clicked != IntPtr.Zero && Ours(s, s.Clicked) ? s.Clicked : wins[0]);
            bool known = N.GetGUIThreadInfo(thread, ref info) && Ours(s, info.hwndFocus);
            bool elsewhere = known && N.GetAncestor(info.hwndFocus, 2) != N.GetAncestor(s.Clicked, 2);
            // A dialog the click opened is in front: keys go there.
            if (s.Clicked != IntPtr.Zero && N.GetAncestor(s.Clicked, 2) != wins[0] && ThreadOf(wins[0]) == thread)
                elsewhere = true;
            if (s.Clicked != IntPtr.Zero && unchecked(Environment.TickCount - s.ClickedAt) < 1500 && !elsewhere &&
                Win.IsWindowVisible(s.Clicked) && Ours(s, s.Clicked)) return s.Clicked;
            if (known) return info.hwndFocus;
            if (s.Clicked != IntPtr.Zero && Win.IsWindowVisible(s.Clicked) && Ours(s, s.Clicked) && !elsewhere) return s.Clicked;
            return wins[0];
        }

        void Key(Session s, Dictionary<string, object> m)
        {
            uint msg = (uint)Wire.Int(m, "msg");
            IntPtr w = (IntPtr)Wire.Int(m, "w"), l = (IntPtr)Wire.Int(m, "l");
            IntPtr h = Focused(s);
            if (h == IntPtr.Zero) return;
            if (msg == N.WM_KEYUP) { N.Deliver(h, N.WM_KEYUP, w, (IntPtr)unchecked((int)0xC0000001)); return; }
            bool ctrl = Wire.Bool(m, "ctrl"), alt = Wire.Bool(m, "alt"), shift = Wire.Bool(m, "shift");
            int vk = Wire.Int(m, "w");
            if (ctrl && !alt)
            {
                uint edit = vk == 'A' ? 0x00B1u : vk == 'C' ? 0x0301u : vk == 'V' ? 0x0302u : vk == 'X' ? 0x0300u : 0u;
                if (edit == 0x00B1u) { N.Deliver(h, edit, IntPtr.Zero, (IntPtr)(-1)); return; }
                if (edit != 0) { N.Deliver(h, edit, IntPtr.Zero, IntPtr.Zero); return; }
            }
            uint appThread = ThreadOf(h);
            if ((ctrl || alt || shift) && appThread != 0)
            {
                // Shift+Tab, Alt+F, Ctrl+S: the inputs joined for a moment, so the app reads the modifier as held.
                uint me = N.GetCurrentThreadId();
                N.AttachThreadInput(me, appThread, true);
                var held = new byte[256];
                if (ctrl) held[0x11] = 0x80;
                if (alt) held[0x12] = 0x80;
                if (shift) held[0x10] = 0x80;
                SetKeyboardState(held);
                N.Queue(h, msg, w, l);
                Thread.Sleep(240);
                SetKeyboardState(new byte[256]);
                N.AttachThreadInput(me, appThread, false);
                return;
            }
            N.Queue(h, msg, w, l);
        }

        [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] state);

        void Char(Session s, int c)
        {
            IntPtr h = Focused(s);
            if (h != IntPtr.Zero) N.Deliver(h, N.WM_CHAR, (IntPtr)c, IntPtr.Zero);
        }

        void Close(Session s, int id)
        {
            try
            {
                foreach (var kv in s.Ids.ToArray())
                    if (kv.Value == id) N.PostMessage(kv.Key, N.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch (InvalidOperationException) { }
        }

        // ── clipboard (text), both ways ─────────────────────────────────────────

        string lastClip = "";

        void SetClipboard(string text)
        {
            lastClip = text;
            var t = new Thread(() => { try { if (text == "") Clipboard.Clear(); else Clipboard.SetText(text); } catch { } });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(2000);
        }

        public void ClipboardLoop()
        {
            uint seen = Win.GetClipboardSequenceNumber();
            while (true)
            {
                Thread.Sleep(500);
                uint now = Win.GetClipboardSequenceNumber();
                if (now == seen) continue;
                seen = now;
                string text = "";
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { continue; }
                if (text == "" || text == lastClip) continue;
                lastClip = text;
                Session[] all;
                lock (sessions) all = sessions.Values.ToArray();
                foreach (var s in all) Send(s, Wire.Obj("t", "clip", "text", text));
            }
        }
    }

    public static class HostProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var cfg = new Settings();
            string here = Path.GetDirectoryName(Application.ExecutablePath);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], v = i + 1 < args.Length ? args[i + 1] : "";
                switch (a)
                {
                    case "--port": cfg.Port = int.Parse(v); i++; break;
                    case "--fps": cfg.Fps = int.Parse(v); i++; break;
                    case "--quality": cfg.Quality = int.Parse(v); i++; break;
                    case "--latency": cfg.Latency = int.Parse(v); i++; break;
                    case "--jitter": cfg.Jitter = int.Parse(v); i++; break;
                    case "--drop": cfg.Drop = int.Parse(v); i++; break;
                    case "--idle": cfg.Idle = int.Parse(v); i++; break;
                    case "--keep": cfg.Keep = int.Parse(v); i++; break;
                    case "--disconnect-every": cfg.DisconnectEvery = int.Parse(v); i++; break;
                    case "--res-every": cfg.ResEvery = int.Parse(v); i++; break;
                    case "--freeze": var f = v.Split(':'); cfg.FreezeEvery = int.Parse(f[0]); cfg.FreezeFor = f.Length > 1 ? int.Parse(f[1]) : 2; i++; break;
                    case "--res": var wh = v.Split('x'); cfg.Res = new Size(int.Parse(wh[0]), int.Parse(wh[1])); i++; break;
                    case "--origin": var xy = v.Split(','); cfg.Origin = new Point(int.Parse(xy[0]), int.Parse(xy[1])); i++; break;
                    case "--log": cfg.Log = v; i++; break;
                    case "--bind": cfg.Bind = IPAddress.Parse(v); i++; break;
                    case "--users":
                        foreach (var up in v.Split(';').Where(x => x.Contains(":")))
                            cfg.Users[up.Split(':')[0].ToLowerInvariant()] = up.Substring(up.IndexOf(':') + 1);
                        i++; break;
                    case "--app":
                        int eq = v.IndexOf('='), bar = v.IndexOf('|');
                        if (eq > 0)
                            cfg.Apps.Add(new App { Name = v.Substring(0, eq), Exe = bar > eq ? v.Substring(eq + 1, bar - eq - 1) : v.Substring(eq + 1),
                                                   Args = bar > eq ? v.Substring(bar + 1) : "" });
                        i++; break;
                }
            }
            if (cfg.Users.Count == 0) cfg.Users["celers"] = "lab";
            if (cfg.Apps.Count == 0)
            {
                // The practice apps, when they are built next to the host.
                string practice = Path.Combine(here, "PracticeApps.exe");
                if (File.Exists(practice))
                    foreach (var kv in new[] { "BooksDesk=books", "StockKeeper=stock", "PayrollPro=payroll" })
                    {
                        var p = kv.Split('=');
                        string data = Path.Combine(here, p[1] + "_data.json");
                        cfg.Apps.Add(new App { Name = p[0], Exe = practice, Args = "--app " + p[1] + " --title " + p[0] +
                                               (File.Exists(data) ? " --data \"" + data + "\"" : "") });
                    }
            }
            var host = new Host(cfg);
            var clip = new Thread(host.ClipboardLoop) { IsBackground = true };
            clip.SetApartmentState(ApartmentState.STA);
            clip.Start();
            host.Run();
            return 0;
        }
    }
}
