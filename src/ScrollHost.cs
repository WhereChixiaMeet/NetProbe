using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetProbe
{
    // Прокручиваемая область с тонким скроллбаром в стиле ThemedList (вместо системного AutoScroll)
    public class ScrollHost : Panel, IMessageFilter
    {
        public readonly Panel Content = new Panel();
        readonly VBar bar = new VBar();
        int offset;

        public ScrollHost()
        {
            DoubleBuffered = true;
            Content.Location = Point.Empty;
            Controls.Add(bar); Controls.Add(Content);
            bar.Scrolled += v => { offset = v; Content.Top = -offset; };
            Application.AddMessageFilter(this);
        }
        protected override void Dispose(bool disposing) { if (disposing) Application.RemoveMessageFilter(this); base.Dispose(disposing); }
        protected override void OnBackColorChanged(EventArgs e) { Content.BackColor = BackColor; base.OnBackColorChanged(e); }

        public void Relayout()
        {
            int w = bar.Visible ? Width - bar.Width - 4 : Width;
            Content.Width = Math.Max(10, Width - Theme.S(14));
            bar.SetBounds(Width - Theme.S(8), 2, Theme.S(6), Height - 4);
            bar.Total = Content.Height; bar.Page = Height;
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, Content.Height - Height)));
            bar.Value = offset; Content.Top = -offset;
        }
        protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || !Visible || !IsHandleCreated) return false; // WM_MOUSEWHEEL
            var c = FromHandle(WindowFromPoint(Cursor.Position));
            for (var x = c; x != null; x = x.Parent)
            {
                if (x is ThemedList || (x is TextBox && ((TextBox)x).Multiline) || x is Spin || x is DarkCombo) return false;
                if (x == this)
                {
                    int delta = (short)((long)m.WParam >> 16);
                    bar.Value = Math.Max(0, Math.Min(Math.Max(0, Content.Height - Height), offset - Math.Sign(delta) * Theme.S(60)));
                    return true;
                }
            }
            return false;
        }

        class VBar : DoubleBufferedControl
        {
            public int Total = 1, Page = 1; int val; bool drag, hover; int dy, dv;
            public event Action<int> Scrolled;
            int Max { get { return Math.Max(0, Total - Page); } }
            public int Value { get { return val; } set { int v = Math.Max(0, Math.Min(Max, value)); if (v == val) { Invalidate(); return; } val = v; Invalidate(); var h = Scrolled; if (h != null) h(v); } }
            Rectangle Thumb() { if (Total <= Page) return Rectangle.Empty; int h = Math.Max(Theme.S(30), Height * Page / Total); int y = Max == 0 ? 0 : (Height - h) * val / Max; return new Rectangle(0, y, Width, h); }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { var t = Thumb(); if (t.IsEmpty) return; if (!t.Contains(e.Location)) Value = Max * e.Y / Math.Max(1, Height); drag = true; dy = e.Y; dv = val; }
            protected override void OnMouseMove(MouseEventArgs e) { if (drag) { var t = Thumb(); Value = dv + (e.Y - dy) * Max / Math.Max(1, Height - t.Height); } base.OnMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(ParentBack); var t = Thumb(); if (t.IsEmpty) return;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Gfx.FillRound(g, drag || hover ? Theme.Muted : Theme.Dim, t, Width / 2f);
            }
        }
    }

    public static class DarkScroll
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string id);
        // Тёмные системные скроллбары у TextBox / RichTextBox / таблиц в тёмной теме
        public static void Apply(Control c)
        {
            if (!Theme.IsDark) return;
            Action set = () => { try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); foreach (Control ch in c.Controls) if (ch is ScrollBar) SetWindowTheme(ch.Handle, "DarkMode_Explorer", null); } catch { } };
            if (c.IsHandleCreated) set(); else c.HandleCreated += (s, e) => set();
        }
    }
}
