// Celers Workspace: the client half of the Remote Lab (Celers RPA Benchmark), a stand-in for Citrix Workspace.
//
// Sign in to a session host, pick an app, and work in it: each of the app's windows is a window here showing only
// its picture (seamless, as Citrix shows published apps), or the whole session is one window (--mode desktop).
// The mouse and keys go to the host. The connection drops and comes back (the apps wait on the host); an idle
// session is signed out. Text copied on either side is pasted on the other.
//
//   CelersWorkspace.exe [--server HOST:7480] [--user celers] [--password lab] [--auto] [--launch PayrollPro]
//                       [--mode seamless|desktop] [--full] [--title TEXT] [--latency 0] [--jitter 0]
//                       [--lose-keys 0] [--clipboard both|in|out|off] [--at X,Y] [--log FILE] [--minimized]
//
// --minimized: every window shown minimized (a person who minimized Citrix).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Celers.RemoteLab
{
    class Opts
    {
        public string Server = "127.0.0.1:7480", User = "", Password = "", Launch = "", Mode = "seamless", Title = "Celers Desktop",
                      Clip = "both", Log = "";
        public bool Auto, Full, Minimized;
        public int Latency, Jitter, LoseKeys;
        public Point At = new Point(0, 0);
    }

    class Remote
    {
        public int Id, Z;
        public string Title = "";
        public Rectangle Rect;  // on the session
        public Bitmap Picture;
        public RemoteWindow Form;
    }

    static class Ws
    {
        public static Opts O;
        public static Control Hub;
        public static Link Link;
        public static string Token = "";
        public static string[] Apps = new string[0];
        public static Size Res = new Size(1366, 768);
        public static readonly Dictionary<int, Remote> Wins = new Dictionary<int, Remote>();
        public static SignInForm SignIn;
        public static AppsForm AppList;
        public static DesktopForm Desktop;
        public static ReconnectForm Reconnecting;
        public static bool Leaving;
        public static readonly Random Rnd = new Random();
        static string lastClip = "";
        static uint clipSeen;

        public static void Ui(Action a)
        {
            try { if (Hub.InvokeRequired) Hub.BeginInvoke(a); else a(); } catch { }
        }

        public static void Log(string evt, params object[] kv)
        {
            if (O.Log == "") return;
            var d = Wire.Obj(kv);
            d["event"] = "workspace_" + evt;
            d["at"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            for (int i = 0; i < 20; i++)
            {
                try { File.AppendAllText(O.Log, Wire.Json.Serialize(d) + "\n"); return; } catch (IOException) { Thread.Sleep(25); }
            }
        }

        public static void Send(Dictionary<string, object> m)
        {
            var l = Link;
            if (l != null && !l.IsClosed) l.SendJson(m);
        }

        // ── the connection ──────────────────────────────────────────────────────

        /// <summary>Connect and sign in (or resume); the reply comes as a message. False when the host is not there.</summary>
        public static bool Connect(Dictionary<string, object> first)
        {
            string host = O.Server, port = "7480";
            int colon = O.Server.LastIndexOf(':');
            if (colon > 0) { host = O.Server.Substring(0, colon); port = O.Server.Substring(colon + 1); }
            TcpClient tcp;
            try
            {
                tcp = new TcpClient();
                var ar = tcp.BeginConnect(host, int.Parse(port), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(4000) || !tcp.Connected) { tcp.Close(); return false; }
                tcp.EndConnect(ar);
            }
            catch { return false; }
            var link = new Link(tcp) { Latency = O.Latency, Jitter = O.Jitter };
            link.Received += (k, p) => OnMessage(link, k, p);
            link.Closed += () => Ui(() => OnClosed(link));
            Link = link;
            link.Start();
            link.SendJson(first);
            return true;
        }

        static void OnMessage(Link link, byte kind, byte[] p)
        {
            if (kind == (byte)'F')
            {
                byte[] jpeg;
                var head = Wire.SplitFrame(p, out jpeg);
                Bitmap bmp;
                try { using (var ms = new MemoryStream(jpeg)) bmp = new Bitmap(ms); } catch { return; }
                Ui(() => OnFrame(Wire.Int(head, "id"), bmp));
                return;
            }
            var m = Wire.Parse(Encoding.UTF8.GetString(p));
            string t = Wire.Str(m, "t");
            Ui(() =>
            {
                switch (t)
                {
                    case "welcome": OnWelcome(m); break;
                    case "denied": if (SignIn != null) SignIn.Failed(Wire.Str(m, "why")); link.Close(); break;
                    case "windows": OnWindows(m); break;
                    case "launched":
                        if (AppList != null) AppList.Status(Wire.Bool(m, "ok") ? "" : Wire.Str(m, "why"));
                        Log("launched", "app", Wire.Str(m, "app"), "ok", Wire.Bool(m, "ok"));
                        break;
                    case "clip": OnClip(Wire.Str(m, "text")); break;
                    case "ended": OnEnded(Wire.Str(m, "why")); break;
                    case "res":  // the session's resolution changed: the one window scales it to fit again
                        var nr = m["res"] as System.Collections.ArrayList;
                        if (nr != null && nr.Count == 2) Res = new Size(Convert.ToInt32(nr[0]), Convert.ToInt32(nr[1]));
                        Log("resolution", "res", Res.Width + "x" + Res.Height);
                        if (Desktop != null && !Desktop.IsDisposed) Desktop.Invalidate();
                        break;
                }
            });
        }

        static void OnWelcome(Dictionary<string, object> m)
        {
            bool resumed = Token != "" && Token == Wire.Str(m, "token");
            Token = Wire.Str(m, "token");
            var apps = m.ContainsKey("apps") ? m["apps"] as System.Collections.ArrayList : null;
            Apps = apps == null ? new string[0] : apps.Cast<object>().Select(x => Convert.ToString(x)).ToArray();
            var res = m.ContainsKey("res") ? m["res"] as System.Collections.ArrayList : null;
            if (res != null && res.Count == 2) Res = new Size(Convert.ToInt32(res[0]), Convert.ToInt32(res[1]));
            if (Reconnecting != null) { Reconnecting.Done(); Reconnecting = null; }
            if (resumed) { Log("reconnected"); return; }
            Log("signed_in", "user", Wire.Str(m, "user"));
            if (SignIn != null) SignIn.Hide();
            if (AppList == null || AppList.IsDisposed) AppList = new AppsForm();
            AppList.Fill(Wire.Str(m, "user"), Apps);
            AppList.Show();
            if (O.Mode == "desktop")
            {
                if (Desktop == null || Desktop.IsDisposed) Desktop = new DesktopForm();
                Desktop.Show();
                if (O.Minimized) Desktop.WindowState = FormWindowState.Minimized;
            }
            if (O.Launch != "") { foreach (var a in O.Launch.Split(',')) Send(Wire.Obj("t", "launch", "app", a.Trim())); O.Launch = ""; }
        }

        static void OnWindows(Dictionary<string, object> m)
        {
            var list = m["list"] as System.Collections.ArrayList;
            var seen = new HashSet<int>();
            int top = -1;
            foreach (Dictionary<string, object> w in list)
            {
                int id = Wire.Int(w, "id");
                seen.Add(id);
                var r = (System.Collections.ArrayList)w["rect"];
                Remote rw;
                bool fresh = !Wins.TryGetValue(id, out rw);
                if (fresh) { rw = new Remote { Id = id }; Wins[id] = rw; }
                rw.Title = Wire.Str(w, "title");
                rw.Z = Wire.Int(w, "z");
                rw.Rect = new Rectangle(Convert.ToInt32(r[0]), Convert.ToInt32(r[1]), Convert.ToInt32(r[2]), Convert.ToInt32(r[3]));
                if (O.Mode == "seamless")
                {
                    if (rw.Form == null || rw.Form.IsDisposed) rw.Form = new RemoteWindow(rw);
                    rw.Form.Place();
                    if (!rw.Form.Visible)
                    {
                        rw.Form.Show();
                        if (O.Minimized) rw.Form.WindowState = FormWindowState.Minimized;
                    }
                }
                if (fresh && rw.Z == 0) top = id;
            }
            foreach (var id in Wins.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var rw = Wins[id];
                Wins.Remove(id);
                if (rw.Form != null && !rw.Form.IsDisposed) rw.Form.Gone();
                if (rw.Picture != null) rw.Picture.Dispose();
            }
            // A window that has just opened on top (a dialog) comes to the front here too.
            if (top >= 0 && Wins[top].Form != null && Wins[top].Form.WindowState != FormWindowState.Minimized) Wins[top].Form.Activate();
            if (Desktop != null && !Desktop.IsDisposed) Desktop.Invalidate();
        }

        static void OnFrame(int id, Bitmap bmp)
        {
            Remote rw;
            if (!Wins.TryGetValue(id, out rw)) { bmp.Dispose(); return; }
            var old = rw.Picture;
            rw.Picture = bmp;
            if (old != null) old.Dispose();
            if (rw.Form != null && !rw.Form.IsDisposed) rw.Form.Invalidate();
            if (Desktop != null && !Desktop.IsDisposed) Desktop.Invalidate();
        }

        static void OnClosed(Link link)
        {
            if (link != Link) return;
            Link = null;
            if (Leaving || Token == "") return;
            Log("disconnected");
            if (Reconnecting == null) { Reconnecting = new ReconnectForm(); Reconnecting.Show(); }
            new Thread(ReconnectLoop) { IsBackground = true }.Start();
        }

        static void ReconnectLoop()
        {
            long until = Environment.TickCount + 60000;
            while (Environment.TickCount < until && Token != "" && !Leaving)
            {
                Thread.Sleep(2000);
                if (Link != null) return;
                if (Connect(Wire.Obj("t", "resume", "token", Token))) return;
            }
            Ui(() =>
            {
                if (Link != null || Token == "") return;
                OnEnded("The session could not be reconnected.");
            });
        }

        public static void OnEnded(string why)
        {
            Log("ended", "why", why);
            Token = "";
            if (Reconnecting != null) { Reconnecting.Done(); Reconnecting = null; }
            CloseAll();
            var l = Link;
            Link = null;
            if (l != null) l.Close();
            if (!Leaving)
            {
                MessageBox.Show(why.StartsWith("idle") ? "Your session has ended: it was " + why + ". Sign in again to go on."
                                : why == "signed out" ? "You have signed out." : why,
                                "Celers Workspace", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowSignIn();
            }
        }

        public static void CloseAll()
        {
            foreach (var rw in Wins.Values) { if (rw.Form != null && !rw.Form.IsDisposed) rw.Form.Gone(); if (rw.Picture != null) rw.Picture.Dispose(); }
            Wins.Clear();
            if (AppList != null && !AppList.IsDisposed) AppList.Hide();
            if (Desktop != null && !Desktop.IsDisposed) Desktop.Hide();
        }

        public static void ShowSignIn()
        {
            if (SignIn == null || SignIn.IsDisposed) SignIn = new SignInForm();
            SignIn.Ready();
            SignIn.Show();
            SignIn.Activate();
        }

        public static void SignOut()
        {
            Send(Wire.Obj("t", "bye"));
        }

        // ── clipboard ───────────────────────────────────────────────────────────

        static void OnClip(string text)
        {
            if (O.Clip != "both" && O.Clip != "in") return;
            lastClip = text;
            try { Clipboard.SetText(text); clipSeen = Win.GetClipboardSequenceNumber(); } catch { }
        }

        public static void ClipTick()
        {
            uint now = Win.GetClipboardSequenceNumber();
            if (now == clipSeen) return;
            clipSeen = now;
            if (O.Clip != "both" && O.Clip != "out") return;
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return; }
            if (text == "" || text == lastClip) return;
            lastClip = text;
            Send(Wire.Obj("t", "clip", "text", text));
        }

        // ── input ───────────────────────────────────────────────────────────────

        static long lastMove;

        public static void Mouse(Point session, string kind, MouseButtons b, int delta = 0)
        {
            if (kind == "move")
            {
                if (b == MouseButtons.None && Environment.TickCount - lastMove < 40) return;
                lastMove = Environment.TickCount;
            }
            Send(Wire.Obj("t", "mouse", "x", session.X, "y", session.Y, "kind", kind, "btn", b == MouseButtons.Right ? "r" : "l", "delta", delta));
        }

        public static bool KeyDown(ref Message msg, Keys keyData)
        {
            if (msg.Msg != 0x100 && msg.Msg != 0x104) return false;
            Keys key = keyData & Keys.KeyCode;
            bool character = (keyData & (Keys.Control | Keys.Alt)) == 0 && !NonCharacter(key);
            if (character) return false;  // its WM_CHAR comes to OnKeyPress
            if (key == Keys.ShiftKey || key == Keys.ControlKey || key == Keys.Menu) return true;
            Send(Wire.Obj("t", "key", "msg", msg.Msg, "w", (int)key, "l", unchecked((int)(long)msg.LParam),
                          "ctrl", (keyData & Keys.Control) != 0, "alt", (keyData & Keys.Alt) != 0, "shift", (keyData & Keys.Shift) != 0));
            return true;
        }

        public static void KeyUp(Keys key) { Send(Wire.Obj("t", "key", "msg", 0x101, "w", (int)key, "l", 0)); }

        public static void Char(char c)
        {
            if (O.LoseKeys > 0 && Rnd.Next(100) < O.LoseKeys) { Log("key_lost", "char", c.ToString()); return; }
            Send(Wire.Obj("t", "char", "c", (int)c));
        }

        static bool NonCharacter(Keys k)
        {
            return k == Keys.Tab || k == Keys.Enter || k == Keys.Escape || k == Keys.Back || k == Keys.Delete ||
                   k == Keys.Insert || k == Keys.Home || k == Keys.End || k == Keys.PageUp || k == Keys.PageDown ||
                   k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down ||
                   (k >= Keys.F1 && k <= Keys.F24) || k == Keys.ShiftKey || k == Keys.ControlKey || k == Keys.Menu;
        }
    }

    // ── the windows ─────────────────────────────────────────────────────────────

    public class SignInForm : Form
    {
        readonly TextBox server = new TextBox { Name = "txtServer", Width = 260 };
        readonly TextBox user = new TextBox { Name = "txtUser", Width = 260 };
        readonly TextBox password = new TextBox { Name = "txtPassword", Width = 260, UseSystemPasswordChar = true };
        readonly Button go = new Button { Name = "btnSignIn", Text = "Sign in", Width = 100 };
        readonly Label status = new Label { Name = "lblStatus", AutoSize = true, ForeColor = Color.Firebrick };

        public SignInForm()
        {
            Text = "Celers Workspace";
            Name = "frmSignIn";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(380, 250);
            BackColor = Color.White;
            var head = new Label { Text = "Sign in to your workspace", Font = new Font(Font.FontFamily, 13f, FontStyle.Bold), AutoSize = true, Location = new Point(20, 16) };
            Controls.Add(head);
            int y = 60;
            foreach (var pair in new[] { Tuple.Create("Server:", (Control)server), Tuple.Create("User name:", (Control)user), Tuple.Create("Password:", (Control)password) })
            {
                Controls.Add(new Label { Text = pair.Item1, AutoSize = true, Location = new Point(20, y + 3) });
                pair.Item2.Location = new Point(100, y);
                Controls.Add(pair.Item2);
                y += 34;
            }
            go.Location = new Point(260, y + 6);
            status.Location = new Point(20, y + 46);
            Controls.Add(go);
            Controls.Add(status);
            AcceptButton = go;
            server.Text = Ws.O.Server;
            user.Text = Ws.O.User;
            password.Text = Ws.O.Password;
            go.Click += (s, e) => Go();
            FormClosed += (s, e) => { if (Ws.Token == "") Application.Exit(); };
        }

        public void Ready() { go.Enabled = true; status.Text = ""; password.Text = Ws.O.Auto ? Ws.O.Password : ""; }

        public void Go()
        {
            go.Enabled = false;
            status.ForeColor = Color.DimGray;
            status.Text = "Signing in...";
            Ws.O.Server = server.Text.Trim();
            string u = user.Text.Trim(), p = password.Text;
            new Thread(() =>
            {
                bool ok = Ws.Connect(Wire.Obj("t", "hello", "user", u, "password", p));
                if (!ok) Ws.Ui(() => Failed("Cannot reach the server " + Ws.O.Server + "."));
            }) { IsBackground = true }.Start();
        }

        public void Failed(string why)
        {
            go.Enabled = true;
            status.ForeColor = Color.Firebrick;
            status.Text = why;
            Ws.Log("sign_in_failed", "why", why);
        }
    }

    public class AppsForm : Form
    {
        readonly FlowLayoutPanel tiles = new FlowLayoutPanel { Name = "pnlApps", Dock = DockStyle.Fill, Padding = new Padding(12) };
        readonly Label who = new Label { Name = "lblUser", Dock = DockStyle.Top, Height = 30, Padding = new Padding(12, 8, 0, 0) };
        readonly Label status = new Label { Name = "lblStatus", Dock = DockStyle.Bottom, Height = 24, Padding = new Padding(12, 4, 0, 0) };
        readonly Button signOut = new Button { Name = "btnSignOut", Text = "Sign out", Dock = DockStyle.Bottom, Height = 30 };

        public AppsForm()
        {
            Text = "Celers Workspace - Apps";
            Name = "frmApps";
            StartPosition = FormStartPosition.Manual;
            Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 420, 60);
            ClientSize = new Size(380, 300);
            BackColor = Color.White;
            Controls.Add(tiles);
            Controls.Add(who);
            Controls.Add(status);
            Controls.Add(signOut);
            signOut.Click += (s, e) => Ws.SignOut();
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing && Ws.Token != "") { e.Cancel = true; WindowState = FormWindowState.Minimized; } };
        }

        public void Fill(string user, string[] apps)
        {
            who.Text = "Signed in as " + user;
            tiles.Controls.Clear();
            foreach (var a in apps)
            {
                string name = a;
                var b = new Button { Name = "app" + new string(a.Where(char.IsLetterOrDigit).ToArray()), Text = a, Width = 110, Height = 70,
                                     FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(232, 240, 250) };
                b.Click += (s, e) => { Status("Starting " + name + "..."); Ws.Send(Wire.Obj("t", "launch", "app", name)); };
                tiles.Controls.Add(b);
            }
            Status("");
        }

        public void Status(string text) { status.Text = text; }
    }

    public class ReconnectForm : Form
    {
        public ReconnectForm()
        {
            Text = "Celers Workspace";
            Name = "frmReconnecting";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ControlBox = false;
            ClientSize = new Size(340, 90);
            Controls.Add(new Label { Name = "lblReconnecting", Text = "The connection was lost. Reconnecting to your session...", AutoSize = true, Location = new Point(16, 20) });
            var cancel = new Button { Name = "btnCancel", Text = "Cancel", Location = new Point(246, 52) };
            cancel.Click += (s, e) => { Ws.Token = ""; Done(); Ws.OnEnded("You cancelled the reconnection."); };
            Controls.Add(cancel);
        }

        public void Done() { if (!IsDisposed) Close(); }
    }

    /// <summary>One window of the session, shown seamlessly: borderless, at its place on the session, its picture only.</summary>
    public class RemoteWindow : Form
    {
        readonly Remote rw;
        bool closing;

        internal RemoteWindow(Remote rw)
        {
            this.rw = rw;
            Name = "frmRemote" + rw.Id;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(240, 240, 240);
            Place();
            MouseDown += (s, e) => Ws.Mouse(ToSession(e.Location), e.Clicks == 2 && e.Button == MouseButtons.Left ? "dbl" : "down", e.Button);
            MouseUp += (s, e) => Ws.Mouse(ToSession(e.Location), "up", e.Button);
            MouseMove += (s, e) => Ws.Mouse(ToSession(e.Location), "move", e.Button);
            MouseWheel += (s, e) => Ws.Mouse(ToSession(e.Location), "wheel", MouseButtons.None, e.Delta);
            FormClosing += (s, e) =>
            {
                // Alt+F4 on the window closes the app's window on the session, not this picture of it.
                if (!closing && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Ws.Send(Wire.Obj("t", "close", "id", rw.Id)); }
            };
        }

        public void Place()
        {
            Text = rw.Title;
            var b = Shown;
            if (WindowState == FormWindowState.Minimized) return;  // placed when restored (Bounds would restore it)
            if (Bounds != b) Bounds = b;
        }

        /// <summary>Where this window shows on this screen when not minimized.</summary>
        public Rectangle Shown { get { return new Rectangle(Ws.O.At.X + rw.Rect.X, Ws.O.At.Y + rw.Rect.Y, rw.Rect.Width, rw.Rect.Height); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Normal && Bounds != Shown) Bounds = Shown;
        }

        public void Gone() { closing = true; Close(); }

        Point ToSession(Point p) { return new Point(rw.Rect.X + p.X, rw.Rect.Y + p.Y); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (rw.Picture != null) e.Graphics.DrawImageUnscaled(rw.Picture, 0, 0);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { if (rw.Picture == null) base.OnPaintBackground(e); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return Ws.KeyDown(ref msg, keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e) { Ws.Char(e.KeyChar); e.Handled = true; }
        protected override void OnKeyUp(KeyEventArgs e) { Ws.KeyUp(e.KeyCode); e.Handled = true; }
    }

    /// <summary>The whole session in one window (--mode desktop), scaled to fit, as a full Citrix desktop shows.</summary>
    public class DesktopForm : Form
    {
        public DesktopForm()
        {
            Text = Ws.O.Title;
            Name = "frmDesktop";
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = Color.Black;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(40, 40);
            ClientSize = new Size(Math.Min(Ws.Res.Width, 1280), Math.Min(Ws.Res.Height, 720));
            if (Ws.O.Full) { FormBorderStyle = FormBorderStyle.None; Bounds = Screen.PrimaryScreen.Bounds; }
            MouseDown += (s, e) => Input(e, e.Clicks == 2 && e.Button == MouseButtons.Left ? "dbl" : "down");
            MouseUp += (s, e) => Input(e, "up");
            MouseMove += (s, e) => Input(e, "move");
            MouseWheel += (s, e) => Input(e, "wheel");
            Resize += (s, e) => Invalidate();
            FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing && Ws.Token != "") Ws.SignOut(); };
        }

        // Where the picture was last shown (screen point of the client area's corner, and its size): a minimized
        // window's are nothing, and "where" says where it shows once restored.
        Point lastClient = new Point(40, 40);
        Size lastSize = new Size(1280, 720);

        public bool Minimized { get { return WindowState == FormWindowState.Minimized; } }

        void Remember()
        {
            if (Minimized || !IsHandleCreated || ClientSize.Width <= 0) return;
            lastClient = PointToScreen(Point.Empty);
            lastSize = ClientSize;
        }

        protected override void OnMove(EventArgs e) { base.OnMove(e); Remember(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Remember(); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Remember(); }

        /// <summary>The whole window's place on the screen when restored.</summary>
        public Rectangle Frame { get { return Minimized ? RestoreBounds : Bounds; } }

        float K(out float ox, out float oy)
        {
            var box = Minimized ? lastSize : ClientSize;
            float k = Math.Min((float)box.Width / Ws.Res.Width, (float)box.Height / Ws.Res.Height);
            ox = (box.Width - Ws.Res.Width * k) / 2;
            oy = (box.Height - Ws.Res.Height * k) / 2;
            return k;
        }

        /// <summary>A rectangle on the session to where it is shown on this screen.</summary>
        public Rectangle ToScreen(Rectangle r)
        {
            float ox, oy, k = K(out ox, out oy);
            var c = Minimized ? lastClient : PointToScreen(Point.Empty);
            var at = new Point(c.X + (int)(ox + r.X * k), c.Y + (int)(oy + r.Y * k));
            return new Rectangle(at.X, at.Y, (int)(r.Width * k), (int)(r.Height * k));
        }

        void Input(MouseEventArgs e, string kind)
        {
            float ox, oy, k = K(out ox, out oy);
            float x = (e.X - ox) / k, y = (e.Y - oy) / k;
            if (x < 0 || y < 0 || x >= Ws.Res.Width || y >= Ws.Res.Height) return;
            Ws.Mouse(new Point((int)x, (int)y), kind, kind == "wheel" ? MouseButtons.None : e.Button, e.Delta);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float ox, oy, k = K(out ox, out oy);
            var g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            using (var bg = new SolidBrush(Color.FromArgb(20, 70, 110)))
                g.FillRectangle(bg, ox, oy, Ws.Res.Width * k, Ws.Res.Height * k);
            foreach (var rw in Ws.Wins.Values.OrderByDescending(w => w.Z))
                if (rw.Picture != null)
                    g.DrawImage(rw.Picture, ox + rw.Rect.X * k, oy + rw.Rect.Y * k, rw.Rect.Width * k, rw.Rect.Height * k);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            return Ws.KeyDown(ref msg, keyData) || base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e) { Ws.Char(e.KeyChar); e.Handled = true; }
        protected override void OnKeyUp(KeyEventArgs e) { Ws.KeyUp(e.KeyCode); e.Handled = true; }
    }

    public static class WorkspaceProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            var o = new Opts();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i], v = i + 1 < args.Length ? args[i + 1] : "";
                switch (a)
                {
                    case "--server": o.Server = v; i++; break;
                    case "--user": o.User = v; i++; break;
                    case "--password": o.Password = v; i++; break;
                    case "--launch": o.Launch = v; i++; break;
                    case "--mode": o.Mode = v == "desktop" ? "desktop" : "seamless"; i++; break;
                    case "--title": o.Title = v; i++; break;
                    case "--clipboard": o.Clip = v; i++; break;
                    case "--log": o.Log = v; i++; break;
                    case "--latency": o.Latency = int.Parse(v); i++; break;
                    case "--jitter": o.Jitter = int.Parse(v); i++; break;
                    case "--lose-keys": o.LoseKeys = int.Parse(v); i++; break;
                    case "--at": var xy = v.Split(','); o.At = new Point(int.Parse(xy[0]), int.Parse(xy[1])); i++; break;
                    case "--auto": o.Auto = true; break;
                    case "--full": o.Full = true; break;
                    case "--minimized": o.Minimized = true; break;
                }
            }
            Ws.O = o;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Ws.Hub = new Control();
            Ws.Hub.CreateControl();
            var h = Ws.Hub.Handle;  // made now, on this thread: messages from the connection come to it
            var clip = new System.Windows.Forms.Timer { Interval = 500 };
            clip.Tick += (s, e) => Ws.ClipTick();
            clip.Start();
            Application.ApplicationExit += (s, e) =>
            {
                Ws.Leaving = true;
            };
            Ws.ShowSignIn();
            if (o.Auto) Ws.SignIn.Shown += (s, e) => Ws.SignIn.Go();
            Application.Run();
            return 0;
        }
    }
}
