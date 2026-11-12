// Celers Remote Lab: what the session host and the workspace (client) share (Celers RPA Benchmark).
//
// One TCP connection per session. A message is [int32 length][byte kind][payload]:
//   'J'  a JSON object (control: sign-in, windows, input, clipboard)
//   'F'  a frame: [int32 header length][header JSON][JPEG bytes]
// A Link sends in order on its own thread, each message held back by the link's latency and jitter, as a slow
// network delivers it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Celers.RemoteLab
{
    public static class Wire
    {
        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };

        public static Dictionary<string, object> Obj(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static Dictionary<string, object> Parse(string text)
        {
            try { return Json.Deserialize<Dictionary<string, object>>(text) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        public static string Str(Dictionary<string, object> d, string key, string dflt = "")
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : dflt;
        }

        public static int Int(Dictionary<string, object> d, string key, int dflt = 0)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return dflt;
            try { return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture); } catch { return dflt; }
        }

        public static bool Bool(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        public static byte[] FramePayload(Dictionary<string, object> head, byte[] jpeg)
        {
            byte[] h = Encoding.UTF8.GetBytes(Json.Serialize(head));
            var p = new byte[4 + h.Length + jpeg.Length];
            BitConverter.GetBytes(h.Length).CopyTo(p, 0);
            h.CopyTo(p, 4);
            jpeg.CopyTo(p, 4 + h.Length);
            return p;
        }

        public static Dictionary<string, object> SplitFrame(byte[] p, out byte[] jpeg)
        {
            int n = BitConverter.ToInt32(p, 0);
            var head = Parse(Encoding.UTF8.GetString(p, 4, n));
            jpeg = new byte[p.Length - 4 - n];
            Array.Copy(p, 4 + n, jpeg, 0, jpeg.Length);
            return head;
        }

        public static bool ReadExactly(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }
    }

    /// <summary>A session's connection: messages out in order, each after the latency (and jitter); messages in on a
    /// reader thread. Closed once, from either side.</summary>
    public class Link
    {
        readonly TcpClient tcp;
        readonly NetworkStream stream;
        readonly Queue<Tuple<long, byte, byte[]>> outbox = new Queue<Tuple<long, byte, byte[]>>();
        readonly Random rnd = new Random();
        readonly Thread sender, reader;
        long lastDue;
        int closed;
        public int Latency, Jitter;  // milliseconds
        public event Action<byte, byte[]> Received;
        public event Action Closed;
        public object Tag;

        public Link(TcpClient tcp)
        {
            this.tcp = tcp;
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            sender = new Thread(SendLoop) { IsBackground = true, Name = "link-send" };
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "link-read" };
        }

        public void Start() { sender.Start(); reader.Start(); }

        public bool IsClosed { get { return closed != 0; } }

        public void SendJson(Dictionary<string, object> msg)
        {
            Send((byte)'J', Encoding.UTF8.GetBytes(Wire.Json.Serialize(msg)));
        }

        public void SendFrame(Dictionary<string, object> head, byte[] jpeg) { Send((byte)'F', Wire.FramePayload(head, jpeg)); }

        /// <summary>How many messages wait to go out (a frame source skips a frame rather than pile them up).</summary>
        public int Pending { get { lock (outbox) return outbox.Count; } }

        void Send(byte kind, byte[] payload)
        {
            if (closed != 0) return;
            long now = Environment.TickCount;
            long due;
            lock (outbox)
            {
                due = Math.Max(lastDue, now + Latency + (Jitter > 0 ? rnd.Next(Jitter + 1) : 0));
                lastDue = due;
                outbox.Enqueue(Tuple.Create(due, kind, payload));
                Monitor.Pulse(outbox);
            }
        }

        void SendLoop()
        {
            try
            {
                while (closed == 0)
                {
                    Tuple<long, byte, byte[]> m;
                    lock (outbox)
                    {
                        while (outbox.Count == 0 && closed == 0) Monitor.Wait(outbox, 500);
                        if (closed != 0) return;
                        m = outbox.Peek();
                        int wait = (int)(m.Item1 - Environment.TickCount);
                        if (wait > 0) { Monitor.Wait(outbox, wait); continue; }
                        outbox.Dequeue();
                    }
                    var head = new byte[5];
                    BitConverter.GetBytes(m.Item3.Length).CopyTo(head, 0);
                    head[4] = m.Item2;
                    stream.Write(head, 0, 5);
                    stream.Write(m.Item3, 0, m.Item3.Length);
                }
            }
            catch { Close(); }
        }

        void ReadLoop()
        {
            try
            {
                var head = new byte[5];
                while (closed == 0)
                {
                    if (!Wire.ReadExactly(stream, head, 5)) break;
                    int n = BitConverter.ToInt32(head, 0);
                    if (n < 0 || n > 64 * 1024 * 1024) break;
                    var p = new byte[n];
                    if (!Wire.ReadExactly(stream, p, n)) break;
                    var h = Received;
                    if (h != null) h(head[4], p);
                }
            }
            catch { }
            Close();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            try { tcp.Close(); } catch { }
            lock (outbox) Monitor.PulseAll(outbox);
            var h = Closed;
            if (h != null) h();
        }
    }

    /// <summary>Newline-separated JSON over a TCP connection.</summary>
    public class LineLink
    {
        readonly TcpClient tcp;
        readonly StreamReader read;
        readonly StreamWriter write;
        readonly object gate = new object();

        public LineLink(TcpClient tcp)
        {
            this.tcp = tcp;
            tcp.NoDelay = true;
            var s = tcp.GetStream();
            read = new StreamReader(s, new UTF8Encoding(false));
            write = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public string ReadLine() { try { return read.ReadLine(); } catch { return null; } }

        public bool WriteLine(string line)
        {
            try { lock (gate) write.WriteLine(line.Replace("\r", "").Replace("\n", " ")); return true; }
            catch { return false; }
        }

        public void Close() { try { tcp.Close(); } catch { } }
    }

    public static class Win
    {
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        public static string Title(IntPtr h)
        {
            var b = new StringBuilder(512);
            GetWindowText(h, b, b.Capacity);
            return b.ToString();
        }
    }
}
