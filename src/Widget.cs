using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // Мини-виджет на рабочем столе, пока главное окно свернуто в трей. Цвет фона берётся из темы (монохром).
    public class MonRow { public string Name; public Color Series; public Level Lv = Level.Off; public string Val = "—"; public List<double> Spark = new List<double>(); }

    public class WidgetForm : Form
    {
        // строки 0-1 — пинг (VPN / Напрямую), 2-3 — скорость (↓ / ↑)
        readonly MonRow[] rows = MonRows.New();
        public string Header = "";
        bool showPing = true, showSpeed = false;
        public void SetParts(bool ping, bool speed)
        {
            if (!ping && !speed) ping = true;
            if (ping == showPing && speed == showSpeed && IsHandleCreated) return;
            showPing = ping; showSpeed = speed;
            Size = new Size(Theme.S(264), Theme.S(36) + Theme.S(30) * MonRows.Visible(ping, speed).Length);
            if (IsHandleCreated) ApplyShape();
            Invalidate();
        }
        public event EventHandler OpenRequested, Moved;
        readonly bool win11;

        public WidgetForm(ContextMenuStrip menu)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Widget; AutoScaleMode = AutoScaleMode.None; Text = "NetProbe widget";
            Size = new Size(Theme.S(264), Theme.S(96));
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            ContextMenuStrip = menu;
            win11 = Native.WinBuild() >= 22000;
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyShape();
        }
        void ApplyShape()
        {
            if (Theme.Classic) { Native.RoundCorners(Handle, false); return; }
            if (!(win11 && Native.RoundCorners(Handle, true)))
                using (var p = Gfx.Round(new RectangleF(0, 0, Width, Height), Theme.S(10))) Region = new Region(p);
        }

        public void SetRow(int i, Level lv, string val, double sample)
        {
            var r = rows[i]; r.Lv = lv; r.Val = val;
            r.Spark.Add(sample); if (r.Spark.Count > 40) r.Spark.RemoveAt(0);
            if (Visible) Invalidate();
        }
        public void SetLevel(int i, Level lv, string val) { rows[i].Lv = lv; rows[i].Val = val; if (Visible) Invalidate(); }

        public void EnsureOnScreen(int x, int y)
        {
            var wa = Screen.PrimaryScreen.WorkingArea;
            var p = new Point(x, y);
            // monitors left of / above the primary one have negative coordinates; -1,-1 = never moved
            bool ok = !(x == -1 && y == -1);
            if (ok)
            {
                ok = false;
                var r = new Rectangle(p, Size);
                foreach (var s in Screen.AllScreens)
                {
                    var i = Rectangle.Intersect(s.WorkingArea, r);
                    if (i.Width * 2 >= Width && i.Height * 2 >= Height) ok = true;
                }
            }
            Location = ok ? p : new Point(wa.Right - Width - Theme.S(16), wa.Bottom - Height - Theme.S(16));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);
                var h = Moved; if (h != null) h(this, EventArgs.Empty);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var h = OpenRequested; if (h != null) h(this, EventArgs.Empty);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Hq(g);
            g.Clear(BackColor);
            if (Theme.Classic) { using (var p = new Pen(Color.Black)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1); Gfx.Bevel(g, new Rectangle(1, 1, Width - 2, Height - 2), true); }
            else if (!win11) Gfx.DrawRound(g, Theme.Border, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Theme.S(10));
            else using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int pad = Theme.S(12);
            Gfx.Text(g, "NETPROBE", Theme.Small, Theme.Dim, new Rectangle(pad, Theme.S(6), Theme.S(120), Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Gfx.Text(g, Header, Theme.Small, Theme.Muted, new Rectangle(Width - pad - Theme.S(170), Theme.S(6), Theme.S(170), Theme.S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int y = Theme.S(28), rh = Theme.S(30);
            var vis = MonRows.Visible(showPing, showSpeed);
            for (int k = 0; k < vis.Length; k++)
            {
                var r = rows[vis[k]];
                float cy = y + rh * k + rh / 2f;
                Color lc = Theme.LevelColor(r.Lv);
                Gfx.Dot(g, lc, pad + Theme.S(6), cy, Theme.S(5.5), r.Lv != Level.Off);
                Gfx.Text(g, r.Name, Theme.Bold, Theme.Text, new Rectangle(pad + Theme.S(18), (int)cy - Theme.S(10), Theme.S(76), Theme.S(20)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                var spark = new RectangleF(pad + Theme.S(94), cy - Theme.S(9), Theme.S(80), Theme.S(18));
                DrawSpark(g, spark, r.Spark, r.Series);
                Gfx.Text(g, r.Val, Theme.Bold, r.Lv == Level.Down ? Theme.Red : Theme.Text, new Rectangle(Width - pad - Theme.S(70), (int)cy - Theme.S(10), Theme.S(70), Theme.S(20)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
        }

        internal static void DrawSpark(Graphics g, RectangleF r, List<double> s, Color c)
        {
            if (s.Count < 2) return;
            double mx = 1; foreach (var v in s) if (!double.IsNaN(v)) mx = Math.Max(mx, v);
            float step = r.Width / 39f; int off = 40 - s.Count;
            var pts = new List<PointF>();
            using (var p = new Pen(c, Theme.S(1.5)))
            {
                for (int i = 0; i < s.Count; i++)
                {
                    float x = r.Left + (off + i) * step;
                    if (double.IsNaN(s[i]))
                    {
                        if (pts.Count > 1) g.DrawLines(p, pts.ToArray());
                        pts.Clear();
                        using (var b = new SolidBrush(Theme.Red)) g.FillRectangle(b, x - 1, r.Bottom - 3, 2, 3);
                        continue;
                    }
                    pts.Add(new PointF(x, (float)(r.Bottom - r.Height * s[i] / mx)));
                }
                if (pts.Count > 1) g.DrawLines(p, pts.ToArray());
            }
        }
    }
    // Маленькая копия виджета в левом нижнем углу главного окна (боковая панель): VPN / Напрямую, точка уровня,
    // спарклайн и последнее значение. Данные — те же, что у виджета рабочего стола. Двойной клик — вкладка Monitor.
    public class MiniMonitor : DoubleBufferedControl
    {
        MonRow[] rows = MonRows.New();
        public string Header = "";
        bool showPing = true, showSpeed = false;

        public MiniMonitor() { Height = Theme.S(76); Cursor = Cursors.Hand; }
        public void SetParts(bool ping, bool speed)
        {
            if (!ping && !speed) ping = true;
            showPing = ping; showSpeed = speed;
            Height = Theme.S(26) + Theme.S(25) * MonRows.Visible(ping, speed).Length;
            Invalidate();
        }

        public void SetRow(int i, Level lv, string val, double sample)
        {
            var r = rows[i]; r.Lv = lv; r.Val = val;
            r.Spark.Add(sample); if (r.Spark.Count > 40) r.Spark.RemoveAt(0);
            if (Visible) Invalidate();
        }
        public void SetLevel(int i, Level lv, string val) { rows[i].Lv = lv; rows[i].Val = val; if (Visible) Invalidate(); }
        // после пересборки UI (смена темы/языка) — перенести накопленные точки
        public void CopyFrom(MiniMonitor o)
        {
            if (o == null) return;
            for (int i = 0; i < rows.Length; i++) { rows[i].Lv = o.rows[i].Lv; rows[i].Val = o.rows[i].Val; rows[i].Spark = new List<double>(o.rows[i].Spark); }
            showPing = o.showPing; showSpeed = o.showSpeed; Height = o.Height;
            Header = o.Header;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var box = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Panel(g, box, Theme.R(10), Theme.Card);
            int pad = Theme.S(10);
            Gfx.Text(g, "MONITOR", Theme.Small, Theme.Dim, new Rectangle(pad, Theme.S(4), Theme.S(70), Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (Header != "") Gfx.Text(g, Header, Theme.Small, Theme.Muted, new Rectangle(pad + Theme.S(66), Theme.S(4), Width - pad * 2 - Theme.S(66), Theme.S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            int y = Theme.S(22), rh = Theme.S(25);
            int nameW = Theme.S(60), valW = Theme.S(58);
            var vis = MonRows.Visible(showPing, showSpeed);
            for (int k = 0; k < vis.Length; k++)
            {
                var r = rows[vis[k]];
                float cy = y + rh * k + rh / 2f;
                Gfx.Dot(g, Theme.LevelColor(r.Lv), pad + Theme.S(4), cy, Theme.S(4.5), r.Lv != Level.Off);
                Gfx.Text(g, r.Name, Theme.Small, Theme.Text, new Rectangle(pad + Theme.S(12), (int)cy - Theme.S(9), nameW, Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                int sx = pad + Theme.S(12) + nameW + Theme.S(2), sw = Width - pad - valW - Theme.S(4) - sx;
                if (sw > Theme.S(16)) WidgetForm.DrawSpark(g, new RectangleF(sx, cy - Theme.S(7), sw, Theme.S(14)), r.Spark, r.Series);
                Gfx.Text(g, r.Val, Theme.Small, r.Lv == Level.Down ? Theme.Red : Theme.Text, new Rectangle(Width - pad - valW, (int)cy - Theme.S(9), valW, Theme.S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }

    public static class MonRows
    {
        public static MonRow[] New()
        {
            return new[] {
                new MonRow { Name = "VPN", Series = Theme.Vpn }, new MonRow { Name = Tx("Напрямую"), Series = Theme.Direct },
                new MonRow { Name = "↓ " + Tx("Загрузка"), Series = Theme.Green }, new MonRow { Name = "↑ " + Tx("Отдача"), Series = Theme.Accent } };
        }
        public static int[] Visible(bool ping, bool speed)
        {
            if (ping && speed) return new[] { 0, 1, 2, 3 };
            return speed ? new[] { 2, 3 } : new[] { 0, 1 };
        }
    }
}
