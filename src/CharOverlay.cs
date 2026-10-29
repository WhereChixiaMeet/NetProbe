using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetProbe
{
    // Personal-темы (ТЗ, раздел 14): персонаж «держит» виджет и рамку окна.
    // Отдельное layered-окно с попиксельной альфой (UpdateLayeredWindow), владелец — виджет или главное окно:
    // не активируется, не ловит мышь (WS_EX_TRANSPARENT), нет на панели задач и в Alt+Tab, следует за владельцем.
    // Картинки — только локально: config\themes\<chisa|kurumi>\widget.png / frame.png (в git и в exe их нет).
    public class CharOverlay : Form
    {
        readonly Form host; readonly bool widget; readonly string slot;
        // 3.1: до 4 картинок — слоты frame / widget / pic3 / pic4 (файл <слот>.png в папке темы), у каждой свой хозяин: окно или виджет
        public static readonly string[] Slots = { "frame", "widget", "pic3", "pic4" };
        public Form Host { get { return host; } }
        Bitmap art; string artKey;
        public int Opacity255 = 220;
        // 3.0.6: своё положение и размер (перетаскивание мышкой, колёсико — размер). Хранится в Settings.CharLayout.
        public static Func<string, int[]> GetLayout;          // ключ "chisa.frame" → { dx, dy, h } (смещение от левого верхнего угла окна-хозяина, высота) или null
        public static Action<string, int[]> SetLayout;
        // ПКМ по персонажу: "pick" — другой PNG, "reset" — положение и размер по умолчанию, "hide" — убрать
        public event Action<CharOverlay, string> MenuPicked;
        bool edit, hover; int curH; string renderKey;
        public string Slot { get { return slot; } }
        string Key { get { return (Theme.Kurumi ? "kurumi." : "chisa.") + Slot; } }

        public CharOverlay(Form host, bool widget, string slot = null)
        {
            this.host = host; this.widget = widget; this.slot = slot ?? (widget ? "widget" : "frame");
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            Text = "RouteX character";
            Cursor = Cursors.SizeAll;
        }
        // скрыли — при следующем показе картинка рисуется заново (раньше у виджета персонаж появлялся «через раз»)
        protected override void OnVisibleChanged(EventArgs e) { if (!Visible) { renderKey = null; hover = false; } base.OnVisibleChanged(e); }
        protected override void OnHandleCreated(EventArgs e) { renderKey = null; base.OnHandleCreated(e); }

        // 3.1: персонажа можно сразу тянуть мышкой (ловятся только непрозрачные пиксели, сквозь пустые места клики проходят).
        // Навёл — рамка и подсказка; колёсико — размер; ПКМ — меню.
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (edit && !hover) { hover = true; Redraw(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover && !RectangleToScreen(ClientRectangle).Contains(Cursor.Position)) { hover = false; Redraw(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!edit) return;
            if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } // тянуть, как за заголовок окна
            else if (e.Button == MouseButtons.Right) ShowMenu();
        }
        void Redraw() { renderKey = null; if (host != null && !host.IsDisposed && host.Visible) Place(lastOpacity); }
        void ShowMenu()
        {
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            Action<string, string> add = (t, k) => cm.Items.Add(t, null, (s, e) => { var h = MenuPicked; if (h != null) h(this, k); });
            add(Tr.Tx("Другая картинка (PNG)…"), "pick");
            add(Tr.Tx("Сбросить положение и размер"), "reset");
            add(Tr.Tx("Убрать картинку"), "hide");
            cm.Items.Add(new ToolStripSeparator());
            add(Tr.Tx("Готово — выйти из редактора"), "done");
            cm.Closed += (s, e) => { hover = false; BeginInvoke((Action)(() => { cm.Dispose(); Redraw(); })); };
            cm.Show(Cursor.Position);
        }
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 /*LAYERED*/ | 0x00000020 /*TRANSPARENT*/ | 0x00000080 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/;
                return cp;
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = (IntPtr)(edit ? 1 : -1); return; } // WM_NCHITTEST: в редакторе — ловим мышь, иначе клики насквозь
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }             // WM_MOUSEACTIVATE → MA_NOACTIVATE: клик не забирает фокус
            if (m.Msg == 0x232) { SaveLayout(); hover = RectangleToScreen(ClientRectangle).Contains(Cursor.Position); base.WndProc(ref m); return; } // WM_EXITSIZEMOVE — отпустили после перетаскивания
            if (edit && (m.Msg == 0x020A))                         // WM_MOUSEWHEEL — размер
            {
                int delta = (short)((long)m.WParam >> 16);
                Rescale(delta > 0 ? 1.08 : 1 / 1.08); return;
            }
            base.WndProc(ref m);
        }

        // режим размещения: окно ловит мышь (тянуть, колёсико — размер, ПКМ — готово)
        public void SetEdit(bool on)
        {
            if (edit == on) return;
            edit = on;
            if (!IsHandleCreated) CreateHandle();
            int ex = GetWindowLong(Handle, -20);
            ex = on ? ex & ~0x20 & ~0x08000000 : ex | 0x20 | 0x08000000; // вне редактора — клики насквозь (кнопки под персонажем всегда нажимаются)
            SetWindowLong(Handle, -20, ex);
            Cursor = Cursors.SizeAll; renderKey = null;
            if (host != null && !host.IsDisposed && host.Visible) Place(lastOpacity);
        }
        public bool Editing { get { return edit; } }

        void SaveLayout()
        {
            if (host == null || host.IsDisposed) return;
            var hb = host.Bounds;
            var h = SetLayout; if (h != null) h(Key, new[] { Left - hb.Left, Top - hb.Top, curH > 0 ? curH : Height });
        }

        void Rescale(double k)
        {
            var img = Art(slot + ".png"); if (img == null) return;
            int oldH = curH > 0 ? curH : Height, oldW = Width;
            int nh = (int)Math.Max(Theme.S(60), Math.Min(Theme.S(2400), oldH * k));
            int nw = nh * img.Width / img.Height;
            var hb = host.Bounds;
            int cx = Left + oldW / 2, cy = Top + oldH / 2;
            var hs = SetLayout; if (hs != null) hs(Key, new[] { cx - nw / 2 - hb.Left, cy - nh / 2 - hb.Top, nh });
            Place(lastOpacity);
        }
        int lastOpacity = 85;

        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

        public static string Folder(ThemeKind k) { var d = Path.Combine(Settings.Sub("themes"), k == ThemeKind.Chisa ? "chisa" : "kurumi"); try { Directory.CreateDirectory(d); } catch { } return d; }
        public static bool HasArt(ThemeKind k, string file) { try { return File.Exists(Path.Combine(Folder(k), file)); } catch { return false; } }

        Bitmap Art(string file)
        {
            string p = Path.Combine(Folder(Theme.Kind), file);
            string key = p; try { key = p + "|" + File.GetLastWriteTimeUtc(p).Ticks; } catch { }
            if (artKey == key && art != null) return art;
            if (art != null) { art.Dispose(); art = null; }
            artKey = key;
            try
            {
                if (File.Exists(p))
                    using (var b = new Bitmap(p))
                    {
                        // 3.2: огромную картинку (4K и больше) один раз уменьшаем — иначе каждое движение окна перемасштабировало её заново и всё подвисало
                        int maxH = 1400;
                        if (b.Height <= maxH) art = new Bitmap(b);
                        else
                        {
                            int nw = Math.Max(1, b.Width * maxH / b.Height);
                            art = new Bitmap(nw, maxH, PixelFormat.Format32bppPArgb);
                            using (var g = Graphics.FromImage(art)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality; g.DrawImage(b, new Rectangle(0, 0, nw, maxH)); }
                        }
                    }
            }
            catch { art = null; }
            renderKey = null;
            return art;
        }

        // пересчитать положение и картинку; false = персонажа показывать нельзя (скрыть)
        public bool Place(int opacityPercent)
        {
            if (!Theme.Personal || host == null || host.IsDisposed || !host.Visible || host.WindowState != FormWindowState.Normal) return false;
            var img = Art(slot + ".png");
            if (img == null) return false;
            var hb = host.Bounds;
            bool kurumi = Theme.Kurumi; // Kurumi — сбоку (слева), Chisa — сверху
            int w, h, x, y;
            if (kurumi)
            {
                h = widget ? (int)(hb.Height * 1.9) : (int)Math.Min(hb.Height * 0.62, Theme.S(430));
                w = h * img.Width / img.Height;
                int over = Theme.S(widget ? 14 : 18);
                x = hb.Left - w + over;
                y = widget ? hb.Bottom - h + Theme.S(10) : hb.Top + (int)(hb.Height * 0.14);
            }
            else
            {
                w = widget ? (int)(hb.Width * 0.72) : (int)Math.Min(Theme.S(330), hb.Width * 0.4);
                h = w * img.Height / img.Width;
                int over = Theme.S(widget ? 12 : 14);
                y = hb.Top - h + over;
                // правее середины, но левее кнопок заголовка (свернуть/развернуть/закрыть)
                x = widget ? hb.Right - w - Theme.S(8) : Math.Min(hb.Left + (int)(hb.Width * 0.55), hb.Right - Theme.S(150) - w);
            }
            // доп. картинки по умолчанию — зеркально основной (с другой стороны окна / виджета); дальше двигаются в редакторе
            if (slot == "pic3" || slot == "pic4") x = hb.Left + hb.Right - (x + w);
            var lay = GetLayout != null ? GetLayout(Key) : null;
            bool custom = lay != null && lay.Length >= 3 && lay[2] > 8;
            if (custom) { h = lay[2]; w = h * img.Width / img.Height; x = hb.Left + lay[0]; y = hb.Top + lay[1]; }
            if (w < 8 || h < 8) return false;
            var r = new Rectangle(x, y, w, h);
            // места нет (край экрана) — не показываем, а не обрезаем; своё положение — показываем, пока хоть часть видна
            var wa = Screen.FromRectangle(hb).WorkingArea;
            if (custom || edit) { if (!SystemInformation.VirtualScreen.IntersectsWith(r)) return false; }
            else if (!wa.Contains(new Point(r.Left, r.Top)) || r.Right > wa.Right + 2 || r.Bottom > wa.Bottom + 2 || r.Left < wa.Left - 2) return false;
            curH = h; lastOpacity = opacityPercent;
            Opacity255 = edit ? 255 : (int)(255 * Math.Max(20, Math.Min(100, opacityPercent)) / 100.0);
            // та же картинка того же размера — только сдвигаем окно (без перерисовки): плавно при перетаскивании окна/виджета
            bool frameOn = edit;
            string rk = artKey + "|" + w + "x" + h + "|" + Opacity255 + "|" + frameOn + "|" + hover + "|" + Theme.Accent.ToArgb();
            if (rk == renderKey && IsHandleCreated)
            {
                if (Left != x || Top != y) SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
                return true;
            }
            renderKey = rk;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality; g.DrawImage(img, new Rectangle(0, 0, w, h));
                    if (frameOn)
                    {
                        // рамка + подсказка; почти прозрачная заливка, чтобы мышь ловилась по всему прямоугольнику
                        using (var f = new SolidBrush(Color.FromArgb(18, Theme.Accent))) g.FillRectangle(f, 0, 0, w, h);
                        using (var pn = new Pen(Theme.Accent, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash }) g.DrawRectangle(pn, 1, 1, w - 3, h - 3);
                        string tipText = edit ? Tr.Tx("Тяни мышкой · колёсико — размер · ПКМ — готово") : Tr.Tx("Тяни мышкой · колёсико — размер · ПКМ — меню");
                        using (var bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillRectangle(bg, 4, 4, Math.Min(w - 8, Theme.S(300)), Theme.S(22));
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        using (var tb = new SolidBrush(Color.White)) g.DrawString(tipText, Theme.Small, tb, new RectangleF(8, 7, Math.Min(w - 12, Theme.S(296)), Theme.S(18)), new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                    }
                }
                SetBits(bmp, r);
            }
            return true;
        }

        void SetBits(Bitmap bmp, Rectangle r)
        {
            if (!IsHandleCreated) CreateHandle();
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = bmp.GetHbitmap(Color.FromArgb(0)); old = SelectObject(mem, hb);
                var size = new SIZE { cx = r.Width, cy = r.Height }; var src = new POINT(); var pos = new POINT { x = r.X, y = r.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = (byte)Opacity255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
            }
            finally
            {
                if (hb != IntPtr.Zero) { SelectObject(mem, old); DeleteObject(hb); }
                DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void Dispose(bool disposing) { if (disposing && art != null) { art.Dispose(); art = null; } base.Dispose(disposing); }

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    }

    // DWM-цвета рамки/заголовка окна под тему (Win11; на Win10 — только тёмный заголовок, без ошибок)
    public static class DwmTheme
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        static int Ref(Color c) { return c.R | (c.G << 8) | (c.B << 16); }
        public static void Apply(IntPtr h)
        {
            try
            {
                int dark = Theme.IsDark ? 1 : 0; DwmSetWindowAttribute(h, 20, ref dark, 4);
                if (Native.WinBuild() < 22000) return;
                int border = Theme.Personal ? Ref(Theme.Accent) : -1 /*DWMWA_COLOR_DEFAULT*/;
                int cap = Theme.Personal ? Ref(Theme.Side) : -1, txt = Theme.Personal ? Ref(Theme.Text) : -1;
                if (border == -1) border = unchecked((int)0xFFFFFFFF);
                if (cap == -1) cap = unchecked((int)0xFFFFFFFF);
                if (txt == -1) txt = unchecked((int)0xFFFFFFFF);
                DwmSetWindowAttribute(h, 34, ref border, 4); DwmSetWindowAttribute(h, 35, ref cap, 4); DwmSetWindowAttribute(h, 36, ref txt, 4);
            }
            catch { }
        }
    }
}
