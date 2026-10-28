using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // Скорость соединения: раз в секунду читает счётчики байт физического адаптера (через него идёт весь трафик,
    // в том числе VPN/xray в зашифрованном виде) и отдаёт ↓/↑ в байтах в секунду. Ничего не отправляет в сеть.
    public static class SpeedMeter
    {
        static System.Threading.Timer timer;
        static NetworkInterface ni; static string niId;
        static long lastRx = -1, lastTx = -1; static DateTime lastAt;
        static int busy;
        public static Func<string> AdapterId;          // id адаптера «Напрямую» (из MainForm)
        public static event Action<double, double> Sample; // ↓, ↑ — байт/с (вызывается из фонового потока)
        public static string AdapterName { get { var n = ni; return n != null ? n.Name : ""; } }

        public static void Start()
        {
            if (timer != null) return;
            timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }
        public static void Stop() { var t = timer; timer = null; if (t != null) t.Dispose(); }

        static void Tick()
        {
            if (Interlocked.Exchange(ref busy, 1) == 1) return;
            try
            {
                string want = null; try { var f = AdapterId; if (f != null) want = f(); } catch { }
                if (ni == null || want != niId)
                {
                    niId = want; ni = null; lastRx = lastTx = -1;
                    try { ni = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => x.Id == want); } catch { }
                    if (ni == null) return;
                }
                long rx, tx;
                try { var s = ni.GetIPStatistics(); rx = s.BytesReceived; tx = s.BytesSent; }
                catch { ni = null; return; }
                var now = DateTime.UtcNow;
                if (lastRx >= 0)
                {
                    double sec = Math.Max(0.2, (now - lastAt).TotalSeconds);
                    double d = Math.Max(0, rx - lastRx) / sec, u = Math.Max(0, tx - lastTx) / sec;
                    var h = Sample; if (h != null) h(d, u);
                }
                lastRx = rx; lastTx = tx; lastAt = now;
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        }

        // байт/с → «12.3 Мбит/с»
        public static string Fmt(double bps)
        {
            double bits = bps * 8;
            if (bits >= 1e9) return (bits / 1e9).ToString("0.00") + Tx(" Гбит/с");
            if (bits >= 1e6) return (bits / 1e6).ToString(bits >= 1e8 ? "0" : "0.0") + Tx(" Мбит/с");
            return (bits / 1e3).ToString("0") + Tx(" Кбит/с");
        }
        // компактно для виджета/боковой панели: «12.3M», «850K»
        public static string Short(double bps)
        {
            double bits = bps * 8;
            if (bits >= 1e9) return (bits / 1e9).ToString("0.0") + "G";
            if (bits >= 1e6) return (bits / 1e6).ToString(bits >= 1e8 ? "0" : "0.0") + "M";
            return (bits / 1e3).ToString("0") + "K";
        }
    }

    // График скорости под графиком задержки на вкладке Monitor: последние 120 секунд, ↓ и ↑ в Мбит/с.
    public class SpeedChart : DoubleBufferedControl
    {
        public const int Capacity = 120;
        public readonly List<double> Down = new List<double>(), Up = new List<double>();
        public string Adapter = "";

        public void Add(double down, double up)
        {
            Down.Add(down * 8 / 1e6); Up.Add(up * 8 / 1e6);
            if (Down.Count > Capacity) Down.RemoveAt(0);
            if (Up.Count > Capacity) Up.RemoveAt(0);
            if (Visible) Invalidate();
        }

        static double Nice(double v)
        {
            double[] s = { 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
            foreach (var x in s) if (v <= x) return x;
            return Math.Ceiling(v / 10000) * 10000;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var rr = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Panel(g, rr, Theme.R(12), Theme.Card);
            int pad = Theme.S(18);
            string cur = Down.Count > 0 ? "↓ " + SpeedMeter.Fmt(Down[Down.Count - 1] * 1e6 / 8) + "   ↑ " + SpeedMeter.Fmt(Up[Up.Count - 1] * 1e6 / 8) : "";
            Gfx.Text(g, Tx("Скорость, Мбит/с"), Theme.H2, Theme.Text, new Rectangle(pad, Theme.S(10), Theme.S(260), Theme.S(24)), TextFormatFlags.Left);
            Gfx.Text(g, cur, Theme.Bold, Theme.Text, new Rectangle(pad + Theme.S(250), Theme.S(12), Math.Max(0, Width - pad * 2 - Theme.S(250) - Theme.S(200)), Theme.S(22)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Tx("последние ") + Capacity + Tx(" с") + (Adapter != "" ? "  ·  " + Adapter : ""), Theme.Small, Theme.Muted, new Rectangle(pad, Theme.S(32), Math.Max(0, Width - pad * 2), Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            int lx = Width - pad;
            foreach (var it in new[] { Tuple.Create("↑ " + Tx("Отдача"), Theme.Accent), Tuple.Create("↓ " + Tx("Загрузка"), Theme.Green) })
            {
                var sz = TextRenderer.MeasureText(it.Item1, Theme.Small);
                lx -= sz.Width;
                Gfx.Text(g, it.Item1, Theme.Small, Theme.Muted, new Rectangle(lx, Theme.S(14), sz.Width + 2, Theme.S(18)), TextFormatFlags.Left);
                lx -= Theme.S(14);
                Gfx.FillRound(g, it.Item2, new RectangleF(lx, Theme.S(19), Theme.S(10), Theme.S(8)), Theme.S(2));
                lx -= Theme.S(16);
            }

            var plot = new RectangleF(pad + Theme.S(36), Theme.S(58), Width - pad * 2 - Theme.S(40), Height - Theme.S(58) - Theme.S(16));
            if (plot.Width < 20 || plot.Height < 20) return;
            double mx = 0;
            foreach (var v in Down) mx = Math.Max(mx, v);
            foreach (var v in Up) mx = Math.Max(mx, v);
            double ymax = Nice(Math.Max(mx * 1.15, 1));
            using (var gp = new Pen(Theme.Grid, 1))
            {
                int n = plot.Height < Theme.S(90) ? 2 : 4;
                for (int i = 0; i <= n; i++)
                {
                    float y = plot.Bottom - plot.Height * i / (float)n;
                    g.DrawLine(gp, plot.Left, y, plot.Right, y);
                    double v = ymax * i / n;
                    Gfx.Text(g, v >= 10 || v == 0 ? ((int)v).ToString() : v.ToString("0.#"), Theme.Small, Theme.Dim, new Rectangle((int)plot.Left - Theme.S(46), (int)y - Theme.S(9), Theme.S(40), Theme.S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }
            Series(g, plot, Down, Theme.Green, ymax);
            Series(g, plot, Up, Theme.Accent, ymax);
            if (Down.Count == 0)
                Gfx.Text(g, Tx("Пока нет данных — адаптер не найден"), Theme.Body, Theme.Dim, Rectangle.Round(plot), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        static void Series(Graphics g, RectangleF plot, List<double> s, Color c, double ymax)
        {
            if (s.Count < 2) return;
            float step = plot.Width / (Capacity - 1); int off = Capacity - s.Count;
            var pts = new PointF[s.Count];
            for (int i = 0; i < s.Count; i++) pts[i] = new PointF(plot.Left + (off + i) * step, (float)(plot.Bottom - plot.Height * Math.Min(s[i], ymax) / ymax));
            if (!Theme.Classic)
            {
                var poly = new List<PointF>(pts) { new PointF(pts[pts.Length - 1].X, plot.Bottom), new PointF(pts[0].X, plot.Bottom) };
                using (var br = new LinearGradientBrush(new RectangleF(plot.X, plot.Y - 1, plot.Width, plot.Height + 2), Color.FromArgb(30, c), Color.FromArgb(0, c), 90f))
                    g.FillPolygon(br, poly.ToArray());
            }
            using (var p = new Pen(c, Theme.S(2)) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
            var l = pts[pts.Length - 1]; Gfx.Dot(g, c, l.X, l.Y, Theme.S(3.5), true);
        }
    }
}
