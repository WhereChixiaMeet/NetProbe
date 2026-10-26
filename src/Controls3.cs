using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NetProbe
{
    // Чекбокс целиком своей отрисовки (без системной темы — ничего не «мигает» при наведении)
    public class ThemedCheck : DoubleBufferedControl
    {
        bool on, hover;
        public event EventHandler CheckedChanged;
        public ThemedCheck() { Cursor = Cursors.Hand; Font = Theme.Body; ForeColor = Theme.Text; SetStyle(ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true); TabStop = true; }
        public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); var h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); } }
        void Fit() { var t = TextRenderer.MeasureText(Text ?? "", Font); Size = new Size(Theme.S(26) + t.Width, Math.Max(Theme.S(22), t.Height + Theme.S(4))); }
        protected override void OnTextChanged(EventArgs e) { Fit(); Invalidate(); base.OnTextChanged(e); }
        protected override void OnFontChanged(EventArgs e) { Fit(); base.OnFontChanged(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyDown(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor.A == 0 ? ParentBack : BackColor);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int s = Theme.S(16), y = (Height - s) / 2;
            var r = new RectangleF(1.5f, y + 0.5f, s - 1, s - 1);
            float rad = Theme.Classic ? Theme.S(2) : Theme.S(4);
            if (on) Gfx.FillRound(g, Enabled ? Theme.Accent : Theme.Dim, r, rad);
            else { Gfx.FillRound(g, Theme.Input, r, rad); Gfx.DrawRound(g, hover && Enabled ? Theme.Accent : Theme.Dim, r, rad, 1.4f); }
            if (on)
                using (var p = new Pen(Theme.AccentFg, Theme.S(2)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round })
                    g.DrawLines(p, new[] { new PointF(r.Left + s * 0.24f, r.Top + s * 0.52f), new PointF(r.Left + s * 0.43f, r.Top + s * 0.72f), new PointF(r.Left + s * 0.78f, r.Top + s * 0.3f) });
            TextRenderer.DrawText(g, Text, Font, new Rectangle(s + Theme.S(8), 0, Width - s - Theme.S(8), Height), Enabled ? ForeColor : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }

    // Выпадающий список целиком своей отрисовки + всплывающий ThemedList
    public class DarkCombo : DoubleBufferedControl
    {
        public readonly List<object> Items = new List<object>();
        int index = -1; bool hover, open;
        public bool UserPick; // true, пока событие вызвано выбором пользователя
        public event EventHandler SelectedIndexChanged;
        ToolStripDropDown drop; ThemedList list;
        DateTime closedAt;

        public DarkCombo() { Height = Theme.S(28); Font = Theme.Body; Cursor = Cursors.Hand; SetStyle(ControlStyles.Selectable, true); TabStop = true; }

        public string GetItemText(object o) { return o == null ? "" : o.ToString(); }
        public int SelectedIndex
        {
            get { return index < Items.Count ? index : -1; }
            set { int v = value >= 0 && value < Items.Count ? value : -1; if (v == index) { Invalidate(); return; } index = v; Invalidate(); var h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty); }
        }
        public object SelectedItem
        {
            get { int i = SelectedIndex; return i >= 0 ? Items[i] : null; }
            set { SelectedIndex = value == null ? -1 : Items.FindIndex(x => Equals(x, value)); }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (!Enabled || (DateTime.Now - closedAt).TotalMilliseconds < 250) return; Focus(); Open(); }
        protected override void OnMouseWheel(MouseEventArgs e) { if (Focused && Items.Count > 0) Pick(Math.Max(0, Math.Min(Items.Count - 1, SelectedIndex - Math.Sign(e.Delta)))); base.OnMouseWheel(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && Items.Count > 0) Pick(Math.Min(Items.Count - 1, SelectedIndex + 1));
            else if (e.KeyCode == Keys.Up && Items.Count > 0) Pick(Math.Max(0, SelectedIndex - 1));
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space || e.KeyCode == Keys.F4) Open();
            base.OnKeyDown(e);
        }

        void Pick(int i) { UserPick = true; try { SelectedIndex = i; } finally { UserPick = false; } }

        void Open()
        {
            if (Items.Count == 0) return;
            list = new ThemedList();
            list.Items.AddRange(Items.Select(GetItemText));
            int rows = Math.Min(Items.Count, 12);
            list.Size = new Size(Width - 2, rows * list.RowH);
            if (SelectedIndex >= 0) { list.SelectedIndex = SelectedIndex; }
            list.SelectedIndexChanged += (s, e) => { };
            list.MouseUp += (s, e) => { int i = list.SelectedIndex; drop.Close(); if (i >= 0) Pick(i); };
            list.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { int i = list.SelectedIndex; drop.Close(); if (i >= 0) Pick(i); } };
            var host = new ToolStripControlHost(list) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = list.Size };
            drop = new ToolStripDropDown { Padding = new Padding(1), AutoSize = true, DropShadowEnabled = !Theme.Classic, Renderer = new ThemedMenuRenderer(), BackColor = Theme.Border };
            drop.Items.Add(host);
            drop.Closed += (s, e) => { open = false; closedAt = DateTime.Now; Invalidate(); var d = drop; BeginInvoke((Action)(() => { try { d.Dispose(); } catch { } })); };
            open = true; Invalidate();
            drop.Show(this, new Point(0, Height + 1));
            list.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.Clear(ParentBack); Gfx.Hq(g);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color border = open || Focused ? Theme.Accent : hover ? Theme.Muted : Theme.Border;
            if (Theme.Classic) { using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, r); using (var p = new Pen(border)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1); }
            else { Gfx.FillRound(g, Theme.Input, r, Theme.R(6)); Gfx.DrawRound(g, border, r, Theme.R(6)); }
            int aw = Theme.S(26);
            TextRenderer.DrawText(g, SelectedIndex >= 0 ? GetItemText(Items[SelectedIndex]) : "", Font, new Rectangle(Theme.S(8), 0, Width - aw - Theme.S(8), Height), Enabled ? Theme.Text : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (var f = new Font("Segoe MDL2 Assets", 8f))
                TextRenderer.DrawText(g, open ? "" : "", f, new Rectangle(Width - aw, 0, aw - Theme.S(4), Height), Enabled ? (hover || open ? Theme.Accent : Theme.Muted) : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        }
    }
}
