using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetProbe
{
    // Прокручиваемая область с тонким скроллбаром в стиле ThemedList (вместо системного AutoScroll)
    public class ScrollHost : Panel, IMessageFilter
    {
        public readonly Panel Content = new BackdropPanel();
        readonly VBar bar = new VBar(), hbar = new VBar { Horiz = true };
        int offset, hoff;
        // минимальная ширина содержимого: если окно уже — появляется горизонтальная полоса (карточку не обрезает)
        public int MinContentWidth;

        public ScrollHost()
        {
            DoubleBuffered = true;
            Content.Location = Point.Empty;
            hbar.Visible = false;
            Controls.Add(bar); Controls.Add(hbar); Controls.Add(Content);
            hbar.Scrolled += v => { hoff = v; Content.Left = -hoff; };
            bar.Scrolled += v => { offset = v; Content.Top = -offset; var h = Scrolled; if (h != null) h(v); };
            Application.AddMessageFilter(this);
        }
        protected override void Dispose(bool disposing) { if (disposing) Application.RemoveMessageFilter(this); base.Dispose(disposing); }
        protected override void OnBackColorChanged(EventArgs e) { Content.BackColor = BackColor; base.OnBackColorChanged(e); }
        // свой фон страницы: рисуем кусок картинки сами (вложенная WinForms-прозрачность давала чёрные/пустые места)
        protected override void OnPaintBackground(PaintEventArgs e) { if (BackdropPanel.HasPage(this)) Gfx.Backdrop(this, e.Graphics); else base.OnPaintBackground(e); }

        public void ScrollToTop() { offset = 0; bar.Value = 0; Content.Top = 0; }
        public void ScrollTo(int y) { bar.Value = Math.Max(0, Math.Min(Math.Max(0, Content.Height - Height), y)); }
        public int Offset { get { return offset; } }
        public event Action<int> Scrolled;
        public void Relayout()
        {
            // прокрутки нет — содержимое на всю ширину (без полосы под скроллбар)
            int vw = Content.Height > Height ? Width - Theme.S(14) : Width;
            bool h = MinContentWidth > vw;
            int vh = h ? Height - Theme.S(12) : Height;
            if (h && Content.Height > vh) vw = Width - Theme.S(14);
            Content.Width = Math.Max(10, Math.Max(vw, MinContentWidth));
            hbar.Visible = h;
            hbar.SetBounds(2, Height - Theme.S(8), Math.Max(10, Width - Theme.S(14)), Theme.S(6));
            hbar.Total = Content.Width; hbar.Page = Math.Max(1, vw);
            hoff = h ? Math.Max(0, Math.Min(hoff, Content.Width - vw)) : 0;
            hbar.Value = hoff; Content.Left = -hoff;
            bar.SetBounds(Width - Theme.S(8), 2, Theme.S(6), vh - 4);
            bar.Total = Content.Height; bar.Page = vh;
            offset = Math.Max(0, Math.Min(offset, Math.Max(0, Content.Height - vh)));
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
                if (x is ThemedList || (x is TextBox && ((TextBox)x).Multiline) ) return false; // Spin / DarkCombo колесо не трогают — крутим страницу
                if (x is ScrollHost && x != this) return false; // вложенная прокрутка (Настройки внутри прокручиваемой страницы) крутит сама
                if (x == this)
                {
                    int delta = (short)((long)m.WParam >> 16);
                    if (hbar.Visible && (ModifierKeys & Keys.Shift) != 0) { hbar.Value = hoff - Math.Sign(delta) * Theme.S(60); return true; }
                    bar.Value = Math.Max(0, Math.Min(Math.Max(0, Content.Height - Height), offset - Math.Sign(delta) * Theme.S(60)));
                    return true;
                }
            }
            return false;
        }

        class VBar : DoubleBufferedControl
        {
            public bool Horiz;
            public int Total = 1, Page = 1; int val; bool drag, hover; int dy, dv;
            public event Action<int> Scrolled;
            int Max { get { return Math.Max(0, Total - Page); } }
            public int Value { get { return val; } set { int v = Math.Max(0, Math.Min(Max, value)); if (v == val) { Invalidate(); return; } val = v; Invalidate(); var h = Scrolled; if (h != null) h(v); } }
            int Len { get { return Horiz ? Width : Height; } }
            int Pos(MouseEventArgs e) { return Horiz ? e.X : e.Y; }
            Rectangle Thumb() { if (Total <= Page) return Rectangle.Empty; int h = Math.Max(Theme.S(30), Len * Page / Total); int y = Max == 0 ? 0 : (Len - h) * val / Max; return Horiz ? new Rectangle(y, 0, h, Height) : new Rectangle(0, y, Width, h); }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { var t = Thumb(); if (t.IsEmpty) return; if (!t.Contains(e.Location)) Value = Max * Pos(e) / Math.Max(1, Len); drag = true; dy = Pos(e); dv = val; }
            protected override void OnMouseMove(MouseEventArgs e) { if (drag) { var t = Thumb(); Value = dv + (Pos(e) - dy) * Max / Math.Max(1, Len - (Horiz ? t.Width : t.Height)); } base.OnMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { drag = false; base.OnMouseUp(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; Gfx.Backdrop(this, g); var t = Thumb(); if (t.IsEmpty) return;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Gfx.FillRound(g, drag || hover ? Theme.Muted : Theme.Dim, t, Math.Min(Width, Height) / 2f);
            }
        }
    }

    // Панель, которая на своём фоне сама рисует кусок картинки страницы. Прозрачные подписи внутри берут фон у неё —
    // одна ступень прозрачности вместо цепочки прозрачных панелей.
    public class BackdropPanel : Panel
    {
        public BackdropPanel() { DoubleBuffered = true; }
        public static bool HasPage(Control c)
        {
            if (!Theme.Translucent) return false;
            for (var x = c.Parent; x != null; x = x.Parent) if (x.BackgroundImage != null) return true;
            return false;
        }
        protected override void OnPaintBackground(PaintEventArgs e) { if (HasPage(this)) Gfx.Backdrop(this, e.Graphics); else base.OnPaintBackground(e); }
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
