using System;
using System.Drawing;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // Сегмент режима маршрутизации ( ROUTE | VPN | DPI ) — один и тот же на вкладках Routing и Subscriptions.
    // Выбранный режим подсвечен; пока подключение активно, чип залит акцентом, выключено — только контур.
    // Клик по чипу — только выбор режима (при активном подключении — переподключение). Вкл/выкл — кнопкой «VPN · ВКЛ».
    // Недоступный чип серый, причина — в подсказке. Анимация подсветки ≤ 150 мс.
    public class ModeSegment : DoubleBufferedControl
    {
        public static readonly string[] Names = { "ROUTE", "VPN", "DPI" };
        int sel; bool active; int hover = -1;
        public readonly string[] Disabled = new string[3]; // null = доступен, иначе причина
        public event Action<int> ChipClicked;
        readonly ToolTip tip = Ui.Tip(); string tipShown;
        readonly Timer anim = new Timer { Interval = 15 };
        float pos, from; DateTime animStart;
        const int AnimMs = 140;

        public ModeSegment()
        {
            Size = new Size(Theme.S(276), Theme.S(36)); Cursor = Cursors.Hand; Font = Theme.Bold;
            anim.Tick += (s, e) =>
            {
                double t = Math.Min(1, (DateTime.Now - animStart).TotalMilliseconds / AnimMs);
                t = 1 - (1 - t) * (1 - t); // ease-out
                pos = (float)(from + (sel - from) * t);
                if (t >= 1) { pos = sel; anim.Stop(); }
                Invalidate();
            };
            pos = sel;
        }

        public int Selected
        {
            get { return sel; }
            set
            {
                value = Math.Max(0, Math.Min(2, value));
                if (value == sel) return;
                from = pos; sel = value; animStart = DateTime.Now;
                if (IsHandleCreated && Visible) anim.Start(); else { pos = sel; Invalidate(); }
            }
        }
        public bool Active { get { return active; } set { if (active != value) { active = value; Invalidate(); } } }

        int ChipAt(int x) { int w = Width / 3; return Math.Max(0, Math.Min(2, x / Math.Max(1, w))); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = ChipAt(e.X);
            if (h != hover) { hover = h; Invalidate(); }
            string t = Disabled[h] ?? ChipTip(h);
            if (t != tipShown) { tip.SetToolTip(this, t); tipShown = t; }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int c = ChipAt(e.X);
            if (Disabled[c] != null) return;
            var h = ChipClicked; if (h != null) h(c);
        }
        protected override void Dispose(bool disposing) { if (disposing) { anim.Dispose(); tip.Dispose(); } base.Dispose(disposing); }

        static string ChipTip(int i)
        {
            switch (i)
            {
                case 0: return Tx("ROUTE — DPI/VPN: приложения из Include в туннеле, DPI для остального; при сбое VPN — DPI");
                case 1: return Tx("VPN — только VPN: основной → резервный, kill switch при обрыве");
                default: return Tx("DPI — только DPI (zapret), без туннеля");
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            float rad = Theme.Classic ? 0 : (Height - 1) / 2f;
            var track = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            if (Theme.Classic) { using (var b = new SolidBrush(Theme.Input)) g.FillRectangle(b, track); Gfx.Bevel(g, Rectangle.Round(track), false); }
            else { Gfx.FillRound(g, Theme.CardHi, track, rad); Gfx.DrawRound(g, Theme.Border, track, rad); }
            float cw = (Width - Theme.S(6)) / 3f, pad = Theme.S(3);
            var hi = new RectangleF(pad + pos * cw, pad, cw, Height - pad * 2 - 1);
            float hr = Theme.Classic ? 0 : hi.Height / 2f;
            if (active) Gfx.FillRound(g, Theme.Accent, hi, hr);
            else { Gfx.FillRound(g, Theme.Card, hi, hr); Gfx.DrawRound(g, Theme.Accent, hi, hr, 1.4f); }
            for (int i = 0; i < 3; i++)
            {
                var r = new Rectangle((int)(pad + i * cw), 0, (int)cw, Height);
                bool on = i == sel && Math.Abs(pos - sel) < 0.5f;
                Color fg = Disabled[i] != null ? Theme.Dim : on && active ? Theme.AccentFg : on ? Theme.Text : hover == i ? Theme.Text : Theme.Muted;
                Gfx.Text(g, Names[i], Font, fg, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // Небольшая пилюля-тумблер источника «Primary VPN ⇄ Subscriptions» (вкладка Subscriptions, рядом со Speed/Test).
    // Доступна только в Режиме гибрида; показывает реальный активный источник.
    public class SourceSwitch : DoubleBufferedControl
    {
        int val; public string Reason; // null = доступен
        public event Action<int> Picked;
        readonly ToolTip tip = Ui.Tip();
        public SourceSwitch() { Size = new Size(Theme.S(150), Theme.S(32)); Cursor = Cursors.Hand; Font = Theme.Small; }
        public int Value { get { return val; } set { if (val != value) { val = value; Invalidate(); } } }
        public void SetReason(string r) { Reason = r; tip.SetToolTip(this, r ?? Tx("Активный источник: Primary VPN (туннели) или Subscriptions (xray). Приоритет всегда у Primary VPN")); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || Reason != null) return;
            int v = e.X < Width / 2 ? 0 : 1;
            if (v == val) return;
            var h = Picked; if (h != null) h(v);
        }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            bool en = Reason == null;
            // в стиле остальных кнопок панели: та же высота и скругление, короткие подписи
            float rad = Theme.Classic ? 0 : Theme.R(6);
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.FillRound(g, Theme.CardHi, r, rad); Gfx.DrawRound(g, Theme.Border, r, rad);
            float half = (Width - Theme.S(6)) / 2f;
            var hi = new RectangleF(Theme.S(3) + val * half, Theme.S(3), half, Height - Theme.S(6) - 1);
            Gfx.FillRound(g, en ? Theme.Accent : Theme.Mix(Theme.Border, Theme.CardHi, 0.3), hi, Theme.Classic ? 0 : Math.Max(0, rad - Theme.S(2)));
            string[] t = { "Primary", "Subs" };
            for (int i = 0; i < 2; i++)
            {
                Color fg = !en ? Theme.Dim : i == val ? Theme.AccentFg : Theme.Muted;
                Gfx.Text(g, t[i], Theme.Bold, fg, new Rectangle((int)(Theme.S(3) + i * half), 0, (int)half, Height), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    // Диалог-уведомление в стилистике NetProbe (вместо нативного MessageBox): заголовок, текст, кнопки, опц. галочка.
    public static class Notice
    {
        // возвращает индекс нажатой кнопки (−1 — закрыто крестиком); dontShow — состояние галочки, если она была
        public static int Show(IWin32Window owner, string title, string text, string[] buttons, string checkText, out bool dontShow)
        {
            dontShow = false;
            using (var f = new Form { Text = "NetProbe", StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None })
            {
                int W = Theme.S(460), p = Theme.S(20);
                var card = new CardPanel();
                var lt = Ui.Label(title, Theme.H2); lt.BackColor = Theme.Card;
                var lx = Ui.Hint(text, W - p * 2 - Theme.S(16)); lx.ForeColor = Theme.Text; lx.Font = Theme.Body; lx.BackColor = Theme.Card;
                lx.Height = TextRenderer.MeasureText(text, Theme.Body, new Size(lx.Width, 4000), TextFormatFlags.WordBreak).Height + Theme.S(4);
                card.Controls.Add(lt); card.Controls.Add(lx);
                lt.Location = new Point(Theme.S(16), Theme.S(14)); lx.Location = new Point(Theme.S(16), lt.Bottom + Theme.S(8));
                card.SetBounds(p, p, W - p * 2, lx.Bottom + Theme.S(16));
                f.Controls.Add(card);
                int y = card.Bottom + Theme.S(14);
                ThemedCheck chk = null;
                if (checkText != null) { chk = Ui.Check(checkText, false); chk.Location = new Point(p, y + Theme.S(6)); f.Controls.Add(chk); }
                int res = -1, x = W - p;
                for (int i = buttons.Length - 1; i >= 0; i--)
                {
                    int idx = i;
                    var b = new FlatButton(buttons[i], i == 0).AutoW(); b.Width = Math.Max(b.Width, Theme.S(96));
                    x -= b.Width; b.Location = new Point(x, y); x -= Theme.S(8);
                    b.Click += (s, e) => { res = idx; f.Close(); };
                    f.Controls.Add(b);
                }
                f.ClientSize = new Size(W, y + Theme.S(32) + p);
                f.HandleCreated += (s, e) => Native.TitleTheme(f.Handle, Theme.IsDark || Theme.Personal);
                if (owner == null) f.StartPosition = FormStartPosition.CenterScreen;
                f.ShowDialog(owner);
                if (chk != null) dontShow = chk.Checked;
                return res;
            }
        }
        public static void Show(IWin32Window owner, string title, string text) { bool d; Show(owner, title, text, new[] { Tx("Понятно") }, null, out d); }
    }
}
