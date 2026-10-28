using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public static class Gfx
    {
        // Фон под контролом. С картинкой фона страницы — рисуем её участок и полупрозрачные карточки-предки,
        // чтобы карточки просвечивали и углы оставались круглыми; без картинки — обычная заливка цветом родителя.
        public static void Backdrop(Control c, Graphics g)
        {
            Control page = null;
            for (var x = c.Parent; x != null; x = x.Parent) if (x.BackgroundImage != null) { page = x; break; }
            if (page == null || !Theme.Translucent) { g.Clear(c.Parent != null ? c.Parent.BackColor : Theme.Bg); return; }
            Rectangle r;
            try { r = new Rectangle(page.PointToClient(c.Parent.PointToScreen(c.Location)), c.Size); } catch { g.Clear(Theme.Bg); return; }
            var img = page.BackgroundImage;
            float sx = img.Width / (float)Math.Max(1, page.ClientSize.Width), sy = img.Height / (float)Math.Max(1, page.ClientSize.Height);
            var im = g.InterpolationMode; g.InterpolationMode = InterpolationMode.Bilinear;
            g.DrawImage(img, new Rectangle(0, 0, c.Width, c.Height), r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy, GraphicsUnit.Pixel);
            g.InterpolationMode = im;
            for (var x = c.Parent; x != null && x != page; x = x.Parent)
                if (x is CardPanel || x is StatusBoard) using (var b = new SolidBrush(Theme.CardFill)) g.FillRectangle(b, 0, 0, c.Width, c.Height);
                else if (!(x is CardPanel) && !(x is StatusBoard) && x.BackColor.A == 255 && x.BackgroundImage == null && x != page && !(x.Parent == page && x.Dock == DockStyle.Fill)) { using (var b = new SolidBrush(x.BackColor)) g.FillRectangle(b, 0, 0, c.Width, c.Height); }
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            if (Theme.Chisa && rad > 0)
            {
                // Chisa: срезанные углы (chamfer) вместо скругления — острый «техно»-силуэт
                float c = Math.Min(rad, Math.Min(r.Width, r.Height) / 2f); c = Math.Min(c, Theme.S(7));
                p.AddPolygon(new[] { new PointF(r.X + c, r.Y), new PointF(r.Right, r.Y), new PointF(r.Right, r.Bottom - c), new PointF(r.Right - c, r.Bottom), new PointF(r.X, r.Bottom), new PointF(r.X, r.Y + c) });
                return p;
            }
            float d = rad * 2;
            // радиус больше половины стороны — «пилюля», а не квадрат (раньше тут был прямоугольник: квадратная каёмка у сегмента/кнопок)
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            if (d < 1f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Hq(Graphics g)
        {
            g.SmoothingMode = Theme.Classic ? SmoothingMode.None : SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float rad)
        {
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Color c, RectangleF r, float rad, float w = 1f)
        {
            using (var p = Round(r, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
        }

        // Классическая «объёмная» рамка: светлый верх/лево, тёмный низ/право
        public static void Bevel(Graphics g, Rectangle r, bool raised)
        {
            var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
            Color a = raised ? Theme.Hilite : Theme.Shadow, b = raised ? Theme.Shadow : Theme.Hilite;
            using (var pa = new Pen(a)) using (var pb = new Pen(b))
            {
                g.DrawLine(pa, r.Left, r.Top, r.Right - 1, r.Top); g.DrawLine(pa, r.Left, r.Top, r.Left, r.Bottom - 1);
                g.DrawLine(pb, r.Left, r.Bottom - 1, r.Right - 1, r.Bottom - 1); g.DrawLine(pb, r.Right - 1, r.Top, r.Right - 1, r.Bottom - 1);
            }
            g.SmoothingMode = sm;
        }

        // Фон карточки в стиле текущей темы
        public static void Panel(Graphics g, RectangleF r, float rad, Color fill)
        {
            if (Theme.Classic)
            {
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                return;
            }
            if (fill == Theme.Card) fill = Theme.CardFill;
            FillRound(g, fill, r, rad);
            DrawRound(g, Theme.Border, r, rad);
        }

        public static void Dot(Graphics g, Color c, float cx, float cy, float r, bool glow)
        {
            if (glow && !Theme.Classic)
            {
                for (int i = 4; i >= 1; i--)
                {
                    float rr = r + i * r * 0.35f;
                    using (var b = new SolidBrush(Color.FromArgb(12 + (4 - i) * 5, c))) g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
                }
            }
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            if (Theme.Classic) using (var p = new Pen(Theme.Shadow)) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        }

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags fl)
        {
            TextRenderer.DrawText(g, s, f, r, c, fl | TextFormatFlags.NoPrefix);
        }
    }

    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        public static void TitleTheme(IntPtr h, bool dark)
        {
            try { int on = dark ? 1 : 0; if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4); } catch { }
        }

        public static bool RoundCorners(IntPtr h, bool round)
        {
            try { int v = round ? 2 : 1; return DwmSetWindowAttribute(h, 33, ref v, 4) == 0; } catch { return false; }
        }

        public static int WinBuild()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    return int.Parse((string)k.GetValue("CurrentBuildNumber"));
            }
            catch { return 0; }
        }
    }

    public class DoubleBufferedControl : Control
    {
        public DoubleBufferedControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected Color ParentBack { get { return Parent != null ? Parent.BackColor : Theme.Bg; } }
    }

    public class FlatButton : DoubleBufferedControl
    {
        public bool Primary, Danger;
        public string Glyph;
        public bool IconOnly; // узкое окно: только значок (текст остаётся в подсказке)
        bool hover, down;

        public FlatButton(string text, bool primary = false)
        {
            Text = text; Primary = primary; Cursor = Cursors.Hand; Font = Theme.Button;
            Size = new Size(Theme.S(120), Theme.S(32)); ForeColor = Theme.Text;
        }

        public int PreferredW { get { return IconOnly && Glyph != null ? Math.Max(Height, Theme.S(32)) : TextRenderer.MeasureText(Text, Font).Width + Theme.S(Glyph != null ? 50 : 30); } }
        public FlatButton AutoW() { Width = PreferredW; return this; }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            Color bg, fg;
            if (Theme.Classic)
            {
                bg = Primary ? Theme.Accent : Color.FromArgb(253, 253, 253); fg = Primary ? Theme.AccentFg : Danger ? Theme.Red : Theme.Text;
                Color bd = Primary ? Theme.Mix(Theme.Accent, Color.Black, 0.2) : Theme.Border;
                if (!Enabled) fg = Theme.Dim;
                else if (down) bg = Primary ? Theme.Mix(Theme.Accent, Color.Black, 0.15) : Color.FromArgb(204, 228, 247);
                else if (hover) { bg = Primary ? Theme.Mix(Theme.Accent, Color.White, 0.12) : Theme.CardHi; if (!Primary) bd = Theme.Accent; }
                var rc = new Rectangle(0, 0, Width, Height);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, rc);
                using (var p = new Pen(bd)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            else
            {
                bg = Primary ? Theme.Accent : Danger ? Theme.Mix(Theme.Red, Theme.Card, 0.78) : Theme.CardHi;
                fg = Primary ? Theme.AccentFg : Danger ? Theme.Red : Theme.Text;
                if (!Enabled) { bg = Theme.Mix(bg, ParentBack, 0.55); fg = Theme.Dim; }
                else if (down) bg = Theme.Mix(bg, Theme.IsDark ? Color.Black : Color.Gray, 0.2);
                else if (hover) bg = Theme.Mix(bg, Theme.IsDark ? Color.White : Color.Black, Primary ? 0.12 : 0.06);
                var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                Gfx.FillRound(g, bg, r, Theme.R(6));
                if (!Primary) Gfx.DrawRound(g, hover && Enabled && Theme.Personal ? Theme.Mix(Theme.Accent, Theme.Border, 0.4) : Theme.Border, r, Theme.R(6));
                Skin.Button(g, r, Primary, hover && Enabled, down, Enabled);
            }
            int off = Theme.Classic && down ? 1 : 0;
            var tr = new Rectangle(off, off, Width, Height);
            if (Glyph != null && IconOnly)
                Gfx.Text(g, Glyph, Theme.Icons, fg, tr, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            else if (Glyph != null)
            {
                int tw = TextRenderer.MeasureText(Text, Font).Width + Theme.S(22);
                int x0 = (Width - tw) / 2 + off;
                Gfx.Text(g, Glyph, Theme.Icons, fg, new Rectangle(x0, off, Theme.S(18), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                tr = new Rectangle(x0 + Theme.S(22), off, Width - x0 - Theme.S(22), Height);
                Gfx.Text(g, Text, Font, fg, tr, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            else Gfx.Text(g, Text, Font, fg, tr, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    // Переключатель вкл/выкл (для блоков «Резервный VPN» и т.п.)
    public class Toggle : DoubleBufferedControl
    {
        bool on; public event EventHandler Changed;
        public bool On { get { return on; } set { if (on == value) return; on = value; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); } }
        public Toggle() { Size = new Size(Theme.S(44), Theme.S(24)); Cursor = Cursors.Hand; }
        protected override void OnClick(EventArgs e) { if (Enabled) On = !On; base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            Color track = on ? Theme.Accent : Theme.Border, knob = on ? Theme.AccentFg : Theme.Card;
            if (!Enabled) track = Theme.Mix(track, ParentBack, 0.5f);
            if (Theme.Classic)
            {
                using (var b = new SolidBrush(on ? Theme.Accent : Theme.Input)) g.FillRectangle(b, r);
                Gfx.Bevel(g, Rectangle.Round(r), false);
                float kw = r.Height - 4, kx = on ? r.Right - kw - 2 : r.Left + 2;
                using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, kx, r.Top + 2, kw, kw);
                Gfx.Bevel(g, Rectangle.Round(new RectangleF(kx, r.Top + 2, kw, kw)), true);
                return;
            }
            Gfx.FillRound(g, track, r, r.Height / 2);
            float d = r.Height - Theme.S(6), x = on ? r.Right - d - Theme.S(3) : r.Left + Theme.S(3);
            using (var b = new SolidBrush(on ? knob : Theme.Muted)) g.FillEllipse(b, x, r.Top + Theme.S(3), d, d);
            Skin.Knob(g, new RectangleF(x, r.Top + Theme.S(3), d, d), on);
        }
    }

    // Выбор цвета акцента: готовые цвета + свой (ColorDialog)
    public class AccentPicker : DoubleBufferedControl
    {
        public Color Value;
        public event Action<Color> Picked;
        int hover = -1;
        int D { get { return Theme.S(22); } }
        int Gap { get { return Theme.S(8); } }
        public AccentPicker() { Height = Theme.S(28); Width = (Theme.Presets.Length + 1) * (D + Gap); Cursor = Cursors.Hand; }
        int Hit(int x) { int i = x / (D + Gap); return i >= 0 && i <= Theme.Presets.Length ? i : -1; }
        protected override void OnMouseMove(MouseEventArgs e) { int h = Hit(e.X); if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Hit(e.X); if (i < 0) return;
            Color c;
            if (i < Theme.Presets.Length) c = Theme.Presets[i].Item2;
            else using (var d = new ColorDialog { Color = Value, FullOpen = true }) { if (d.ShowDialog(FindForm()) != DialogResult.OK) return; c = d.Color; }
            Value = c; Invalidate();
            var h = Picked; if (h != null) h(c);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = (Height - D) / 2;
            bool custom = true;
            for (int i = 0; i <= Theme.Presets.Length; i++)
            {
                var r = new Rectangle(i * (D + Gap) + 2, y, D, D);
                if (i < Theme.Presets.Length)
                {
                    var c = Theme.Presets[i].Item2;
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
                    if (c.ToArgb() == Value.ToArgb()) { custom = false; using (var p = new Pen(Theme.Text, 2)) g.DrawEllipse(p, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6); }
                }
                else
                {
                    using (var b = new SolidBrush(custom ? Value : Theme.CardHi)) g.FillEllipse(b, r);
                    using (var p = new Pen(Theme.Border)) g.DrawEllipse(p, r);
                    TextRenderer.DrawText(g, "+", Theme.Bold, r, custom ? Theme.AccentFg : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    if (custom) using (var p = new Pen(Theme.Text, 2)) g.DrawEllipse(p, r.X - 3, r.Y - 3, r.Width + 6, r.Height + 6);
                }
                if (i == hover) using (var p = new Pen(Color.FromArgb(120, Theme.Text), 1)) g.DrawEllipse(p, r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            }
        }
    }

    public class NavButton : DoubleBufferedControl
    {
        public string Glyph; bool selected, hover, badge;
        public bool Hot { set { hover = value; Invalidate(); } } // сюда можно бросить перетаскиваемую карточку
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
        public bool Badge { get { return badge; } set { badge = value; Invalidate(); } } // точка «есть обновление»

        public NavButton(string glyph, string text) { Glyph = glyph; Text = text; Cursor = Cursors.Hand; Font = Theme.Body; Height = Theme.S(38); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = new RectangleF(Theme.S(10), Theme.S(3), Width - Theme.S(20), Height - Theme.S(6));
            Color fg = selected || hover ? Theme.Text : Theme.Muted;
            if (Theme.Classic)
            {
                if (selected) { using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r); fg = Theme.AccentFg; }
                else if (hover) Gfx.Bevel(g, Rectangle.Round(r), true);
            }
            else
            {
                if (selected) Gfx.FillRound(g, Theme.CardHi, r, Theme.R(7));
                else if (hover) Gfx.FillRound(g, Theme.Card, r, Theme.R(7));
                if (selected) Gfx.FillRound(g, Theme.Accent, new RectangleF(Theme.S(10), Height / 2f - Theme.S(8), Theme.S(3), Theme.S(16)), Theme.S(1.5));
                if (selected) Skin.NavMarker(g, r, Height);
            }
            Gfx.Text(g, Glyph, Theme.Icons, fg, new Rectangle(Theme.S(24), 0, Theme.S(24), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            Gfx.Text(g, Text, selected ? Theme.Bold : Theme.Body, fg, new Rectangle(Theme.S(54), 0, Width - Theme.S(60), Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (badge) { var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias; using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, Width - Theme.S(28), Height / 2f - Theme.S(4), Theme.S(8), Theme.S(8)); g.SmoothingMode = sm; }
        }
    }

    public class Slider : DoubleBufferedControl
    {
        double min = 0, max = 1, val = 0.5;
        public double Step = 0.1;
        public event EventHandler ValueChanged;
        bool drag;

        public Slider() { Height = Theme.S(26); Cursor = Cursors.Hand; SetStyle(ControlStyles.Selectable, true); TabStop = true; }
        public double Min { get { return min; } set { min = value; Value = val; Invalidate(); } }
        public double Max { get { return max; } set { max = value; Value = val; Invalidate(); } }
        public double Value
        {
            get { return val; }
            set
            {
                double v = Math.Max(min, Math.Min(max, value));
                if (Step > 0) v = Math.Round(Math.Round((v - min) / Step) * Step + min, 6);
                v = Math.Max(min, Math.Min(max, v));
                if (Math.Abs(v - val) > 1e-9) { val = v; Invalidate(); var h = ValueChanged; if (h != null) h(this, EventArgs.Empty); }
            }
        }

        int Pad { get { return Theme.S(9); } }
        void SetFromX(int x) { double t = (x - Pad) / (double)Math.Max(1, Width - Pad * 2); Value = min + Math.Max(0, Math.Min(1, t)) * (max - min); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); drag = true; SetFromX(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left) Value = val - Step; else if (e.KeyCode == Keys.Right) Value = val + Step;
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            float cy = Height / 2f, h = Theme.S(4);
            float w = Width - Pad * 2;
            float t = (float)((val - min) / Math.Max(1e-9, max - min));
            float x = Pad + w * t, r = Theme.S(8);
            if (Theme.Classic)
            {
                var tr = new Rectangle(Pad, (int)(cy - 2), (int)w, 4);
                using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, tr);
                Gfx.Bevel(g, tr, false);
                var kr = new Rectangle((int)(x - Theme.S(5)), (int)(cy - Theme.S(10)), Theme.S(10), Theme.S(20));
                using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, kr);
                Gfx.Bevel(g, kr, true);
                return;
            }
            Gfx.FillRound(g, Theme.Border, new RectangleF(Pad, cy - h / 2, w, h), h / 2);
            Gfx.FillRound(g, Enabled ? Theme.Accent : Theme.Dim, new RectangleF(Pad, cy - h / 2, Math.Max(h, w * t), h), h / 2);
            if (Focused) using (var b = new SolidBrush(Color.FromArgb(50, Theme.Accent))) g.FillEllipse(b, x - r - 3, cy - r - 3, r * 2 + 6, r * 2 + 6);
            using (var b = new SolidBrush(Theme.Card)) g.FillEllipse(b, x - r, cy - r, r * 2, r * 2);
            using (var p = new Pen(Theme.Accent, 2)) g.DrawEllipse(p, x - r + 1, cy - r + 1, r * 2 - 2, r * 2 - 2);
        }
    }

    public class ProgressLine : DoubleBufferedControl
    {
        double v; public double Value { get { return v; } set { v = Math.Max(0, Math.Min(1, value)); Invalidate(); } }
        public ProgressLine() { Height = Theme.S(4); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            Gfx.FillRound(g, Theme.Border, new RectangleF(0, 0, Width, Height), Theme.Classic ? 0 : Height / 2f);
            if (v > 0) Gfx.FillRound(g, Theme.Accent, new RectangleF(0, 0, (float)(Width * v), Height), Theme.Classic ? 0 : Height / 2f);
        }
    }

    public class CardPanel : Panel
    {
        public string Title, Sub;
        // 3.2: модуль интерфейса — карточку можно перетащить за заголовок (на «Настройки» / обратно на свою вкладку) или ПКМ → меню
        public string ModuleKey;
        public event Action<CardPanel> ModuleMenu;
        public const string DragFormat = "NetProbe.Module";
        Point downAt; bool canDrag;
        public static bool Locked; // Lock interface — карточки не перетаскиваются
        bool TitleZone(Point p) { return ModuleKey != null && p.Y < Theme.S(40); }
        bool InTitle(Point p) { return !Locked && TitleZone(p); }
        public CardPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card; ForeColor = Theme.Text;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            canDrag = e.Button == MouseButtons.Left && InTitle(e.Location); downAt = e.Location;
            if (e.Button == MouseButtons.Right && TitleZone(e.Location)) { var h = ModuleMenu; if (h != null) h(this); }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = InTitle(e.Location) ? Cursors.SizeAll : Cursors.Default;
            if (canDrag && e.Button == MouseButtons.Left && (Math.Abs(e.X - downAt.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - downAt.Y) > SystemInformation.DragSize.Height))
            {
                canDrag = false;
                var d = new DataObject(); d.SetData(DragFormat, ModuleKey);
                DoDragDrop(d, DragDropEffects.Move);
            }
        }
        protected override void OnMouseUp(MouseEventArgs e) { canDrag = false; base.OnMouseUp(e); }
        // карточка шире, чем её строили: элементы у правого края едут вправо, широкие (списки, поля, подсказки) растягиваются
        int builtW;
        public void AutoAnchor()
        {
            builtW = Width;
            foreach (Control c in Controls)
            {
                if (c.Right < Width - Theme.S(24)) continue;
                bool wide = c.Left < Theme.S(160) && c.Width > Width / 3;
                c.Anchor = wide ? AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right : AnchorStyles.Top | AnchorStyles.Right;
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (Theme.Classic) r = new RectangleF(0, 0, Width, Height);
            Gfx.Panel(g, r, Theme.R(10), Theme.Card);
            Skin.CardDecor(g, r);
            if (!string.IsNullOrEmpty(Title)) Gfx.Text(g, Title, Theme.H2, Theme.Text, new Rectangle(Theme.S(16), Theme.S(10), Width - Theme.S(32), Theme.S(24)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!string.IsNullOrEmpty(Sub)) Gfx.Text(g, Sub, Theme.Small, Theme.Muted, new Rectangle(Theme.S(16), Theme.S(34), Width - Theme.S(32), Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    public class IndicatorCard : DoubleBufferedControl
    {
        public string Title = "", Sub = "", Value = "—", Unit = Tx("мс"), Line1 = "", Line2 = "", Line3 = "";
        public string Badge;
        public Color Series = Theme.Accent;
        Level level = Level.Off;
        float pulse = -1; Timer tm;
        public Level Level { get { return level; } set { level = value; Invalidate(); } }

        public IndicatorCard()
        {
            tm = new Timer { Interval = 33 };
            tm.Tick += (s, e) => { pulse += 0.06f; if (pulse >= 1) { pulse = -1; tm.Stop(); } Invalidate(); };
        }
        public void Pulse() { if (Theme.Classic) return; pulse = 0; tm.Start(); }
        protected override void Dispose(bool disposing) { if (disposing) tm.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Panel(g, r, Theme.R(12), Theme.Card);
            if (!Theme.Classic) Gfx.FillRound(g, Series, new RectangleF(Theme.S(18), 0.5f, Theme.S(36), Theme.S(3)), Theme.S(1.5));
            int pad = Theme.S(20);
            Gfx.Text(g, Title, Theme.H2, Theme.Text, new Rectangle(pad, Theme.S(14), Width - pad * 2, Theme.S(24)), TextFormatFlags.Left);
            Gfx.Text(g, Sub, Theme.Small, Theme.Muted, new Rectangle(pad, Theme.S(38), Width - pad * 2 - Theme.S(110), Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            Color lc = Theme.LevelColor(level);
            string lt = Theme.LevelText(level);
            Color pc = lc;
            if (Badge != null && level != Level.Off && level != Level.Down) { lt = Badge; pc = Theme.Orange; }
            var ts = TextRenderer.MeasureText(lt, Theme.Small);
            var pill = new RectangleF(Width - pad - ts.Width - Theme.S(16), Theme.S(16), ts.Width + Theme.S(16), Theme.S(22));
            if (Theme.Classic) { using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, pill); Gfx.Bevel(g, Rectangle.Round(pill), false); }
            else Gfx.FillRound(g, Color.FromArgb(40, pc), pill, pill.Height / 2);
            Gfx.Text(g, lt, Theme.Small, pc, Rectangle.Round(pill), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            float cx = pad + Theme.S(16), cy = Theme.S(92), dr = Theme.S(11);
            if (pulse >= 0)
            {
                float pr = dr + pulse * Theme.S(20);
                using (var pen = new Pen(Color.FromArgb((int)((1 - pulse) * 150), lc), Theme.S(2))) g.DrawEllipse(pen, cx - pr, cy - pr, pr * 2, pr * 2);
            }
            Gfx.Dot(g, lc, cx, cy, dr, level != Level.Off);

            var vs = TextRenderer.MeasureText(g, Value, Theme.Big, Size.Empty, TextFormatFlags.NoPadding);
            int vx = pad + Theme.S(46);
            Gfx.Text(g, Value, Theme.Big, Theme.Text, new Rectangle(vx, (int)cy - vs.Height / 2, vs.Width + 4, vs.Height), TextFormatFlags.NoPadding | TextFormatFlags.Left);
            Gfx.Text(g, Unit, Theme.Body, Theme.Muted, new Rectangle(vx + vs.Width + Theme.S(4), (int)cy - Theme.S(2), Theme.S(60), Theme.S(20)), TextFormatFlags.Left);

            int y = Theme.S(128);
            Gfx.Text(g, Line1, Theme.Body, Theme.Text, new Rectangle(pad, y, Width - pad * 2, Theme.S(20)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Line2, Theme.Small, Theme.Muted, new Rectangle(pad, y + Theme.S(22), Width - pad * 2, Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Line3, Theme.Small, Theme.Muted, new Rectangle(pad, y + Theme.S(42), Width - pad * 2, Theme.S(18)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    public class LatencyChart : DoubleBufferedControl
    {
        public const int Capacity = 120;
        public readonly List<double> A = new List<double>(), B = new List<double>();
        public string NameA = "VPN", NameB = Tx("Напрямую");
        public int Good = 120, Warn = 300;
        // Day / Week: точки из логов (NaN = нет данных, разрыв). null — режим Session (живые опросы)
        public List<double> HA, HB; public string HistSub, HistEmpty;
        bool Hist { get { return HA != null || HistEmpty != null; } }
        int Cap { get { return HA != null ? Math.Max(2, Math.Max(HA.Count, HB.Count)) : Capacity; } }

        public void Add(bool a, double v)
        {
            var l = a ? A : B; l.Add(v); if (l.Count > Capacity) l.RemoveAt(0); Invalidate();
        }
        public void Clear() { A.Clear(); B.Clear(); Invalidate(); }
        // NaN — обрыв, -∞ — неподтверждённый сбой опроса
        public const double Blip = double.NegativeInfinity;
        static bool Bad(double v) { return double.IsNaN(v) || double.IsInfinity(v); }
        // сбой подтвердился (обрыв) — последние n жёлтых меток становятся красными
        public void ConfirmLast(bool a, int n)
        {
            var l = a ? A : B;
            for (int i = l.Count - 1; i >= 0 && n > 0; i--, n--) if (double.IsNegativeInfinity(l[i])) l[i] = double.NaN;
            Invalidate();
        }

        static double Nice(double v)
        {
            double[] s = { 50, 100, 150, 200, 300, 400, 500, 750, 1000, 1500, 2000, 3000, 5000, 10000, 20000, 30000 };
            foreach (var x in s) if (v <= x) return x;
            return Math.Ceiling(v / 10000) * 10000;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var rr = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Panel(g, rr, Theme.R(12), Theme.Card);
            int pad = Theme.S(18);
            Gfx.Text(g, Tx("Задержка, мс"), Theme.H2, Theme.Text, new Rectangle(pad, Theme.S(12), Theme.S(300), Theme.S(24)), TextFormatFlags.Left);
            Gfx.Text(g, Hist ? (HistSub ?? "") : Tx("последние ") + Capacity + Tx(" опросов  ·  красные отметки = неудачные опросы"), Theme.Small, Theme.Muted, new Rectangle(pad, Theme.S(34), Theme.S(560), Theme.S(18)), TextFormatFlags.Left);

            int lx = Width - pad;
            foreach (var it in new[] { Tuple.Create(NameB, Theme.Direct), Tuple.Create(NameA, Theme.Vpn) })
            {
                var sz = TextRenderer.MeasureText(it.Item1, Theme.Small);
                lx -= sz.Width;
                Gfx.Text(g, it.Item1, Theme.Small, Theme.Muted, new Rectangle(lx, Theme.S(16), sz.Width + 2, Theme.S(18)), TextFormatFlags.Left);
                lx -= Theme.S(14);
                Gfx.FillRound(g, it.Item2, new RectangleF(lx, Theme.S(21), Theme.S(10), Theme.S(8)), Theme.S(2));
                lx -= Theme.S(16);
            }

            var plot = new RectangleF(pad + Theme.S(36), Theme.S(62), Width - pad * 2 - Theme.S(40), Height - Theme.S(62) - Theme.S(26));
            if (plot.Width < 20 || plot.Height < 20) return;
            var sA = HA ?? (Hist ? new List<double>() : A); var sB = HB ?? (Hist ? new List<double>() : B);
            double mx = 0;
            foreach (var v in sA) if (!Bad(v)) mx = Math.Max(mx, v);
            foreach (var v in sB) if (!Bad(v)) mx = Math.Max(mx, v);
            double ymax = Nice(Math.Max(mx * 1.15, Math.Min(Good * 1.2, 150)));

            using (var gp = new Pen(Theme.Grid, 1))
            {
                int n = 4;
                foreach (var cand in new[] { 5, 4, 3 }) if (((int)ymax) % (cand * 5) == 0) { n = cand; break; }
                for (int i = 0; i <= n; i++)
                {
                    float y = plot.Bottom - plot.Height * i / (float)n;
                    g.DrawLine(gp, plot.Left, y, plot.Right, y);
                    Gfx.Text(g, ((int)(ymax * i / n)).ToString(), Theme.Small, Theme.Dim, new Rectangle((int)plot.Left - Theme.S(46), (int)y - Theme.S(9), Theme.S(40), Theme.S(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }
            foreach (var th in new[] { Tuple.Create((double)Good, Theme.Green), Tuple.Create((double)Warn, Theme.Yellow) })
            {
                if (th.Item1 >= ymax) continue;
                float y = (float)(plot.Bottom - plot.Height * th.Item1 / ymax);
                using (var p = new Pen(Color.FromArgb(90, th.Item2), 1) { DashStyle = DashStyle.Dash }) g.DrawLine(p, plot.Left, y, plot.Right, y);
            }
            DrawSeries(g, plot, sA, Theme.Vpn, ymax, 0, false);
            DrawSeries(g, plot, sB, Theme.Direct, ymax, 1, false);
            if (Hist && HA == null)
                Gfx.Text(g, HistEmpty, Theme.Body, Theme.Dim, Rectangle.Round(plot), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            else if (sA.Count == 0 && sB.Count == 0)
                Gfx.Text(g, Tx("Пока нет данных — запустите мониторинг"), Theme.Body, Theme.Dim, Rectangle.Round(plot), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        void DrawSeries(Graphics g, RectangleF plot, List<double> s, Color c, double ymax, int idx, bool dashed)
        {
            if (s.Count == 0) return;
            int cap = Cap; bool hist = Hist;
            float step = plot.Width / (cap - 1);
            int off = cap - s.Count;
            var pts = new List<PointF>();
            Action flush = () =>
            {
                if (pts.Count >= 2)
                {
                    if (!Theme.Classic)
                    {
                        var poly = new List<PointF>(pts) { new PointF(pts[pts.Count - 1].X, plot.Bottom), new PointF(pts[0].X, plot.Bottom) };
                        using (var br = new LinearGradientBrush(new RectangleF(plot.X, plot.Y - 1, plot.Width, plot.Height + 2), Color.FromArgb(30, c), Color.FromArgb(0, c), 90f))
                            g.FillPolygon(br, poly.ToArray());
                    }
                    using (var p = new Pen(c, Theme.S(2)) { LineJoin = LineJoin.Round }) { if (dashed) p.DashStyle = DashStyle.Dash; g.DrawLines(p, pts.ToArray()); }
                }
                else if (pts.Count == 1) using (var b = new SolidBrush(c)) g.FillEllipse(b, pts[0].X - 2, pts[0].Y - 2, 4, 4);
                pts.Clear();
            };
            for (int i = 0; i < s.Count; i++)
            {
                float x = plot.Left + (off + i) * step;
                if (Bad(s[i]))
                {
                    flush();
                    // красная метка — подтверждённый обрыв, жёлтая — одиночный сбой (ещё не обрыв)
                    if (!hist) using (var b = new SolidBrush(Color.FromArgb(200, double.IsNegativeInfinity(s[i]) ? Theme.Yellow : Theme.Red))) g.FillRectangle(b, x - 1.5f, plot.Bottom + Theme.S(4) + idx * Theme.S(6), 3, Theme.S(5));
                    continue;
                }
                float y = (float)(plot.Bottom - plot.Height * Math.Min(s[i], ymax) / ymax);
                pts.Add(new PointF(x, y));
            }
            flush();
            int last = s.Count - 1;
            if (!Bad(s[last]))
            {
                float x = plot.Left + (off + last) * step, y = (float)(plot.Bottom - plot.Height * Math.Min(s[last], ymax) / ymax);
                Gfx.Dot(g, c, x, y, Theme.S(3.5), true);
            }
        }
    }

    public class ThemedMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.CardHi; } }
        public override Color MenuItemSelected { get { return Theme.CardHi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHi; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color CheckBackground { get { return Theme.CardHi; } }
        public override Color CheckSelectedBackground { get { return Theme.CardHi; } }
    }

    public class ThemedMenuRenderer : ToolStripProfessionalRenderer
    {
        public ThemedMenuRenderer() : base(new ThemedMenuColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Dim; base.OnRenderItemText(e); }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics; Gfx.Hq(g);
            var r = e.ImageRectangle;
            using (var p = new Pen(Theme.Text, 2)) g.DrawLines(p, new[] { new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.5f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f), new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.28f) });
        }
    }

    public static class Ui
    {
        // подсказки появляются не сразу, а после 3 с наведения (просьба владельца — не мельтешат)
        public const int TipDelay = 3000;
        public static ToolTip Tip() { return new ToolTip { InitialDelay = TipDelay, ReshowDelay = TipDelay, AutoPopDelay = 15000 }; }
        public static Label Label(string text, Font f = null, Color? c = null)
        {
            return new Label { Text = text, UseMnemonic = false, Font = f ?? Theme.Body, ForeColor = c ?? Theme.Text, AutoSize = true, BackColor = Color.Transparent };
        }

        public static Label Hint(string text, int width)
        {
            var hl = new Label { Text = text, UseMnemonic = false, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, Width = width, BackColor = Color.Transparent };
            hl.Height = TextRenderer.MeasureText(text, Theme.Small, new Size(width, 2000), TextFormatFlags.WordBreak).Height + 2;
            return hl;
        }

        public static TextBox TextBox(string text = "", bool multi = false)
        {
            var tb = new ThemedTextBox { Text = text, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = multi ? Theme.Mono : Theme.Body, Multiline = multi, ScrollBars = multi ? ScrollBars.Both : ScrollBars.None, WordWrap = false, AcceptsReturn = multi };
            if (multi) DarkScroll.Apply(tb);
            return tb;
        }

        public static Spin Num(decimal min, decimal max, decimal val, int dec = 0)
        {
            var n = new Spin { Minimum = min, Maximum = max, DecimalPlaces = dec, Width = Theme.S(90) };
            n.Value = Math.Max(min, Math.Min(max, val));
            return n;
        }

        public static ThemedCheck Check(string text, bool v)
        {
            return new ThemedCheck { Text = text, Checked = v, ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.Body };
        }

        public static RadioButton Radio(string text, bool v)
        {
            return new RadioButton { Text = text, Checked = v, ForeColor = Theme.Text, BackColor = Color.Transparent, Font = Theme.Body, AutoSize = true, FlatStyle = Theme.Classic ? FlatStyle.Standard : FlatStyle.Flat, Cursor = Cursors.Hand, UseMnemonic = false };
        }

        public static void StyleGrid(DataGridView g)
        {
            DarkScroll.Apply(g);
            g.BackgroundColor = Theme.Card; g.BorderStyle = BorderStyle.None; g.GridColor = Theme.Border;
            g.EnableHeadersVisualStyles = false; g.RowHeadersVisible = false; g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false; g.ReadOnly = true; g.SelectionMode = DataGridViewSelectionMode.FullRowSelect; g.MultiSelect = true;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; g.ColumnHeadersBorderStyle = Theme.Classic ? DataGridViewHeaderBorderStyle.Raised : DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; g.ColumnHeadersHeight = Theme.S(32);
            g.RowTemplate.Height = Theme.S(28); g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.DefaultCellStyle.BackColor = Theme.Classic ? Theme.Input : Theme.Card; g.DefaultCellStyle.ForeColor = Theme.Text; g.DefaultCellStyle.Font = Theme.Body;
            g.DefaultCellStyle.SelectionBackColor = Theme.Classic ? Theme.Accent : Theme.CardHi; g.DefaultCellStyle.SelectionForeColor = Theme.Classic ? Theme.AccentFg : Theme.Text;
            g.DefaultCellStyle.Padding = new Padding(Theme.S(8), 0, Theme.S(4), 0);
            g.ColumnHeadersDefaultCellStyle.BackColor = Theme.Classic ? Theme.Bg : Theme.Card; g.ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted;
            g.ColumnHeadersDefaultCellStyle.Font = Theme.Small; g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Classic ? Theme.Bg : Theme.Card;
            g.ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.S(8), 0, Theme.S(4), 0);
            if (Theme.Classic) g.BackgroundColor = Theme.Input;
        }

        public static ListBox List()
        {
            return new ListBox { BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = Theme.Classic ? BorderStyle.Fixed3D : BorderStyle.None, Font = Theme.Body, IntegralHeight = false, SelectionMode = SelectionMode.MultiExtended };
        }
    }
}
