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
        readonly Form host; readonly bool widget;
        Bitmap art; string artKey;
        public int Opacity255 = 220;

        public CharOverlay(Form host, bool widget)
        {
            this.host = host; this.widget = widget;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            Text = "NetProbe character";
        }

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
            if (m.Msg == 0x84) { m.Result = (IntPtr)(-1); return; } // WM_NCHITTEST → HTTRANSPARENT: клики проходят насквозь
            base.WndProc(ref m);
        }

        public static string Folder(ThemeKind k) { return Path.Combine(Settings.Sub("themes"), k == ThemeKind.Chisa ? "chisa" : "kurumi"); }
        public static bool HasArt(ThemeKind k, string file) { try { return File.Exists(Path.Combine(Folder(k), file)); } catch { return false; } }

        Bitmap Art(string file)
        {
            string p = Path.Combine(Folder(Theme.Kind), file);
            if (artKey == p && art != null) return art;
            if (art != null) { art.Dispose(); art = null; }
            artKey = p;
            try { if (File.Exists(p)) using (var b = new Bitmap(p)) art = new Bitmap(b); } catch { art = null; }
            return art;
        }

        // пересчитать положение и картинку; false = персонажа показывать нельзя (скрыть)
        public bool Place(int opacityPercent)
        {
            if (!Theme.Personal || host == null || host.IsDisposed || !host.Visible || host.WindowState != FormWindowState.Normal) return false;
            var img = Art(widget ? "widget.png" : "frame.png");
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
            if (w < 8 || h < 8) return false;
            var r = new Rectangle(x, y, w, h);
            // места нет (край экрана) — не показываем, а не обрезаем
            var wa = Screen.FromRectangle(hb).WorkingArea;
            if (!wa.Contains(new Point(r.Left, r.Top)) || r.Right > wa.Right + 2 || r.Bottom > wa.Bottom + 2 || r.Left < wa.Left - 2) return false;
            Opacity255 = (int)(255 * Math.Max(20, Math.Min(100, opacityPercent)) / 100.0);
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality; g.DrawImage(img, new Rectangle(0, 0, w, h)); }
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
