using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace NetProbe
{
    // Вкладки в стиле темы: подписки на «Серверах» (подчёркивание) и сегменты списков приложений (Segmented).
    // Если вкладки не помещаются — по краям стрелки, прокрутка колесом.
    public class TabStrip : DoubleBufferedControl
    {
        public class Tab { public string Key, Text, Badge, Tip; public bool Dim; }
        public readonly List<Tab> Tabs = new List<Tab>();
        public bool Segmented;
        public string SelectedKey;
        public event Action<string> Selected;
        public event Action<string, Point> RightClicked;
        // перетаскивание вкладок мышкой: CanDrag решает, какие можно двигать (подписки — да, «Все/Рабочие/…» — нет),
        // Dropped(что, перед какой вкладкой встать; null — в конец)
        public Func<string, bool> CanDrag;
        public event Action<string, string> Dropped;
        int dragIdx = -1, dropAt = -1; Point dragFrom; bool dragging;

        readonly List<Rectangle> rects = new List<Rectangle>();
        readonly ToolTip tip = Ui.Tip();
        readonly Timer tipTimer = new Timer { Interval = Ui.TipDelay };
        Point tipAt;
        int hover = -1, offset, total;
        string tipShown;

        public TabStrip()
        {
            Height = Theme.S(32); Cursor = Cursors.Hand; Font = Theme.Body;
            tipTimer.Tick += (s, e) => { tipTimer.Stop(); if (!string.IsNullOrEmpty(tipShown) && !IsDisposed) tip.Show(tipShown, this, tipAt, 8000); };
        }

        protected override void Dispose(bool disposing) { if (disposing) { tipTimer.Dispose(); tip.Dispose(); } base.Dispose(disposing); }

        int Arrow { get { return Theme.S(20); } }
        bool Overflow { get { return !Segmented && total > Width; } }
        Font BadgeFont { get { return Theme.Small; } }

        public void SetTabs(IEnumerable<Tab> tabs)
        {
            Tabs.Clear(); Tabs.AddRange(tabs);
            if (Tabs.Count > 0 && !Tabs.Any(t => t.Key == SelectedKey)) SelectedKey = Tabs[0].Key;
            Calc(); EnsureVisible(); Invalidate();
        }

        public void Select(string key) { SelectedKey = key; EnsureVisible(); Invalidate(); var h = Selected; if (h != null) h(key); }

        int TextW(Tab t)
        {
            int w = TextRenderer.MeasureText(t.Text ?? "", Font).Width;
            if (!string.IsNullOrEmpty(t.Badge)) w += Theme.S(6) + TextRenderer.MeasureText(t.Badge, BadgeFont).Width;
            return w;
        }

        void Calc()
        {
            rects.Clear();
            if (Segmented)
            {
                int n = Math.Max(1, Tabs.Count), w = Width / n;
                for (int i = 0; i < Tabs.Count; i++) rects.Add(new Rectangle(i * w, 0, i == n - 1 ? Width - i * w : w, Height));
                total = Width; return;
            }
            int x = 0, pad = Theme.S(12);
            foreach (var t in Tabs) { int w = TextW(t) + pad * 2; rects.Add(new Rectangle(x, 0, w, Height)); x += w + Theme.S(2); }
            total = x;
            offset = Math.Max(0, Math.Min(offset, total - Width + Arrow * 2));
            if (!Overflow) offset = 0;
        }

        Rectangle Shown(int i) { var r = rects[i]; if (Overflow) r.X += Arrow - offset; return r; }

        void EnsureVisible()
        {
            if (!Overflow) return;
            int i = Tabs.FindIndex(t => t.Key == SelectedKey); if (i < 0) return;
            var r = rects[i]; int view = Width - Arrow * 2;
            if (r.Left < offset) offset = r.Left;
            else if (r.Right > offset + view) offset = r.Right - view;
            Calc();
        }

        void Scroll(int d) { offset += d; Calc(); Invalidate(); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Calc(); EnsureVisible(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Calc(); }
        protected override void OnMouseWheel(MouseEventArgs e) { if (Overflow) Scroll(-Math.Sign(e.Delta) * Theme.S(80)); base.OnMouseWheel(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; tipTimer.Stop(); tip.Hide(this); tipShown = null; Invalidate(); base.OnMouseLeave(e); }

        int HitTest(Point p)
        {
            if (Overflow && (p.X < Arrow || p.X >= Width - Arrow)) return -1;
            for (int i = 0; i < rects.Count; i++) if (Shown(i).Contains(p)) return i;
            return -1;
        }

        // куда встанет перетаскиваемая вкладка: индекс вкладки, перед которой вставить (Tabs.Count — в конец)
        int DropIndex(Point p)
        {
            for (int i = 0; i < rects.Count; i++) { var r = Shown(i); if (p.X < r.Left + r.Width / 2) return i; }
            return rects.Count;
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging && dragIdx >= 0 && dropAt >= 0 && dropAt != dragIdx && dropAt != dragIdx + 1)
            {
                string what = Tabs[dragIdx].Key, before = dropAt < Tabs.Count ? Tabs[dropAt].Key : null;
                if (before == null || CanDrag == null || CanDrag(before) || dropAt > dragIdx)
                {
                    var h = Dropped; if (h != null) h(what, before);
                }
            }
            dragIdx = -1; dragging = false; dropAt = -1; Cursor = Cursors.Hand; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragIdx >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && Math.Abs(e.X - dragFrom.X) > Theme.S(8)) { dragging = true; Cursor = Cursors.SizeWE; }
                if (dragging)
                {
                    int d = DropIndex(e.Location);
                    // нельзя поставить перед несдвигаемыми вкладками (Все / Рабочие / Избранные / Вручную)
                    while (d < Tabs.Count && CanDrag != null && !CanDrag(Tabs[d].Key)) d++;
                    if (d != dropAt) { dropAt = d; Invalidate(); }
                    if (Overflow && e.X < Arrow) Scroll(-Theme.S(20)); else if (Overflow && e.X > Width - Arrow) Scroll(Theme.S(20));
                    return;
                }
            }
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
            string t = h >= 0 ? Tabs[h].Tip : null;
            if (t != tipShown)
            {
                // как у остальных подсказок: показываем после Ui.TipDelay наведения
                tipShown = t; tip.Hide(this); tipTimer.Stop();
                if (!string.IsNullOrEmpty(t)) { tipAt = new Point(e.X + Theme.S(12), Height + Theme.S(4)); tipTimer.Start(); }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Overflow && e.X < Arrow) { Scroll(-Theme.S(140)); return; }
            if (Overflow && e.X >= Width - Arrow) { Scroll(Theme.S(140)); return; }
            int i = HitTest(e.Location); if (i < 0) return;
            if (e.Button == MouseButtons.Right) { var r = RightClicked; if (r != null) r(Tabs[i].Key, e.Location); return; }
            dragIdx = CanDrag != null && CanDrag(Tabs[i].Key) ? i : -1; dragFrom = e.Location; dragging = false; dropAt = -1;
            if (Tabs[i].Key != SelectedKey) Select(Tabs[i].Key);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            if (rects.Count != Tabs.Count) Calc();
            if (Segmented) { PaintSegmented(g); return; }
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            var clip = Overflow ? new Rectangle(Arrow, 0, Width - Arrow * 2, Height) : ClientRectangle;
            g.SetClip(clip);
            for (int i = 0; i < Tabs.Count; i++)
            {
                var t = Tabs[i]; var r = Shown(i);
                if (r.Right < clip.Left || r.Left > clip.Right) continue;
                bool sel = t.Key == SelectedKey, hov = i == hover;
                if (hov && !sel) Gfx.FillRound(g, Theme.Mix(Theme.Card, Theme.CardHi, 0.6), new RectangleF(r.X + 1, r.Y + Theme.S(3), r.Width - 2, r.Height - Theme.S(6)), Theme.Classic ? 0 : Theme.R(6));
                Color fg = sel ? Theme.Text : t.Dim ? Theme.Dim : hov ? Theme.Text : Theme.Muted;
                int tw = TextRenderer.MeasureText(t.Text ?? "", Font).Width, x = r.X + (r.Width - TextW(t)) / 2;
                Gfx.Text(g, t.Text, Font, fg, new Rectangle(x, 0, tw + Theme.S(4), Height - Theme.S(2)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (!string.IsNullOrEmpty(t.Badge))
                    Gfx.Text(g, t.Badge, BadgeFont, sel ? Theme.Accent : Theme.Dim, new Rectangle(x + tw + Theme.S(6), 0, Theme.S(60), Height - Theme.S(2)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, r.X + Theme.S(6), Height - Theme.S(3), r.Width - Theme.S(12), Theme.S(3));
            }
            if (dragging && dropAt >= 0 && rects.Count > 0)
            {
                int dx = dropAt < rects.Count ? Shown(dropAt).Left - Theme.S(2) : Shown(rects.Count - 1).Right;
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, dx, Theme.S(4), Theme.S(3), Height - Theme.S(8));
            }
            g.ResetClip();
            if (Overflow)
            {
                foreach (var left in new[] { true, false })
                {
                    var ar = left ? new Rectangle(0, 0, Arrow, Height - 1) : new Rectangle(Width - Arrow, 0, Arrow, Height - 1);
                    bool can = left ? offset > 0 : offset < total - (Width - Arrow * 2);
                    Gfx.Text(g, left ? "‹" : "›", Theme.H2, can ? Theme.Text : Theme.Dim, ar, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        void PaintSegmented(Graphics g)
        {
            var all = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float rad = Theme.Classic ? 0 : Theme.R(6);
            Gfx.FillRound(g, Theme.Input, all, rad);
            for (int i = 0; i < Tabs.Count; i++)
            {
                var t = Tabs[i]; var r = rects[i];
                bool sel = t.Key == SelectedKey, hov = i == hover;
                if (sel) Gfx.FillRound(g, Theme.Accent, new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), Theme.Classic ? 0 : Theme.R(5));
                else if (hov) Gfx.FillRound(g, Theme.CardHi, new RectangleF(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), Theme.Classic ? 0 : Theme.R(5));
                else if (i > 0 && Tabs[i - 1].Key != SelectedKey) using (var p = new Pen(Theme.Border)) g.DrawLine(p, r.X, r.Y + Theme.S(6), r.X, r.Bottom - Theme.S(6));
                Color fg = sel ? Theme.AccentFg : Theme.Text;
                string s = t.Text + (string.IsNullOrEmpty(t.Badge) ? "" : " " + t.Badge);
                Gfx.Text(g, s, Font, fg, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            Gfx.DrawRound(g, Theme.Border, all, rad);
        }
    }
}
