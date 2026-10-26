using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace NetProbe
{
    // TextBox с рамкой в цвет темы: рисуем свой 1px кант поверх системного в WM_NCPAINT
    public class ThemedTextBox : TextBox
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        bool hover;
        public ThemedTextBox() { BorderStyle = BorderStyle.FixedSingle; }
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        string cue;
        public string Cue { get { return cue; } set { cue = value; if (IsHandleCreated) SendMessage(Handle, 0x1501, (IntPtr)1, cue); } } // EM_SETCUEBANNER
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (cue != null) SendMessage(Handle, 0x1501, (IntPtr)1, cue); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Frame(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Frame(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Frame(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Frame(); }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x0085 || m.Msg == 0x000F) Frame(); // WM_NCPAINT / WM_PAINT
        }
        void Frame()
        {
            if (!IsHandleCreated) return;
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using (var g = Graphics.FromHdc(dc))
                using (var p = new Pen(Focused ? Theme.Accent : hover ? Theme.Muted : Theme.Border))
                    g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            }
            finally { ReleaseDC(Handle, dc); }
        }
    }

    // Числовое поле в стиле темы: значение по центру, «−» / «+», колесо мыши и стрелки, ввод цифр
    public class Spin : DoubleBufferedControl
    {
        decimal min, max = 100, val;
        public int DecimalPlaces;
        public event EventHandler ValueChanged;
        int hover; string typed;
        public Spin() { Size = new Size(Theme.S(90), Theme.S(26)); SetStyle(ControlStyles.Selectable, true); TabStop = true; Cursor = Cursors.Hand; Font = Theme.Body; }
        public decimal Minimum { get { return min; } set { min = value; Value = val; } }
        public decimal Maximum { get { return max; } set { max = value; Value = val; } }
        public decimal Value
        {
            get { return val; }
            set { var v = Math.Max(min, Math.Min(max, value)); if (v == val) { Invalidate(); return; } val = v; Invalidate(); var h = ValueChanged; if (h != null) h(this, EventArgs.Empty); }
        }
        int Btn { get { return Math.Min(Height, Theme.S(20)); } }
        protected override void OnMouseMove(MouseEventArgs e) { int h = e.X < Btn ? -1 : e.X > Width - Btn ? 1 : 0; if (h != hover) { hover = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = 0; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { Focus(); typed = null; if (e.X < Btn) Value -= 1; else if (e.X > Width - Btn) Value += 1; base.OnMouseDown(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Value += Math.Sign(e.Delta); base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up) { typed = null; Value += 1; }
            else if (e.KeyCode == Keys.Down) { typed = null; Value -= 1; }
            else if (e.KeyCode == Keys.Back && typed != null) { typed = typed.Length > 1 ? typed.Substring(0, typed.Length - 1) : ""; Invalidate(); }
            else if (e.KeyCode == Keys.Enter) Commit();
            base.OnKeyDown(e);
        }
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (char.IsDigit(e.KeyChar)) { typed = (typed ?? "") + e.KeyChar; if (typed.Length > 6) typed = typed.Substring(0, 6); Invalidate(); }
            base.OnKeyPress(e);
        }
        void Commit() { decimal d; if (typed != null && decimal.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out d)) Value = d; typed = null; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { Commit(); base.OnLostFocus(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBack); Gfx.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (Theme.Classic) { using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, 0, 0, Width, Height); using (var pn = new Pen(Focused ? Theme.Accent : Theme.Border)) g.DrawRectangle(pn, 0, 0, Width - 1, Height - 1); }
            else { Gfx.FillRound(g, Theme.Input, r, Theme.R(6)); Gfx.DrawRound(g, Focused ? Theme.Accent : Theme.Border, r, Theme.R(6)); }
            int b2 = Btn;
            Color cl = hover == -1 && Enabled ? Theme.Accent : Theme.Muted, cr = hover == 1 && Enabled ? Theme.Accent : Theme.Muted;
            TextRenderer.DrawText(g, "", Theme.Icons, new Rectangle(0, 0, b2, Height), cl, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "", Theme.Icons, new Rectangle(Width - b2, 0, b2, Height), cr, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            string t = typed != null ? typed + "_" : val.ToString("F" + DecimalPlaces, CultureInfo.InvariantCulture);
            TextRenderer.DrawText(g, t, Font, new Rectangle(b2, 0, Width - b2 * 2, Height), Enabled ? Theme.Text : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
