using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace NetProbe
{
    // Список в стиле темы: свой тонкий скроллбар, иконки приложений, правый тег, мультивыбор
    public class ThemedList : DoubleBufferedControl
    {
        public readonly List<string> Items = new List<string>();
        readonly HashSet<int> sel = new HashSet<int>();
        int anchor = -1, top, hover = -1;
        bool dragThumb; int dragY, dragTop;
        public bool MultiSelect, ShowIcons;
        public Func<string, string> RightText;
        public Func<string, bool> BoldItem;
        public event EventHandler SelectedIndexChanged;

        public ThemedList() { SetStyle(ControlStyles.Selectable, true); TabStop = true; Font = Theme.Body; }

        public int RowH { get { return Theme.S(26); } }
        int MaxTop { get { return Math.Max(0, Items.Count - Height / RowH); } }
        int BarW { get { return Theme.S(6); } }

        public void BeginUpdate() { }
        public void EndUpdate() { sel.RemoveWhere(i => i >= Items.Count); top = Math.Min(top, MaxTop); Invalidate(); }

        public int SelectedIndex
        {
            get { return sel.Count == 0 ? -1 : sel.Min(); }
            set { sel.Clear(); if (value >= 0 && value < Items.Count) { sel.Add(value); anchor = value; EnsureVisible(value); } Invalidate(); Fire(); }
        }
        public string SelectedItem
        {
            get { int i = SelectedIndex; return i >= 0 ? Items[i] : null; }
            set { SelectedIndex = value == null ? -1 : Items.IndexOf(value); }
        }
        public List<string> SelectedItems { get { return sel.OrderBy(i => i).Where(i => i < Items.Count).Select(i => Items[i]).ToList(); } }
        public void ClearSelected() { sel.Clear(); Invalidate(); }

        void Fire() { var h = SelectedIndexChanged; if (h != null) h(this, EventArgs.Empty); }
        void EnsureVisible(int i) { if (i < top) top = i; else if (i >= top + Height / RowH) top = Math.Max(0, i - Height / RowH + 1); top = Math.Min(top, MaxTop); }

        Rectangle Thumb()
        {
            if (Items.Count * RowH <= Height) return Rectangle.Empty;
            int h = Math.Max(Theme.S(24), Height * Height / (Items.Count * RowH));
            int y = MaxTop == 0 ? 0 : (Height - h) * top / MaxTop;
            return new Rectangle(Width - BarW - 2, y, BarW, h);
        }

        protected override void OnMouseWheel(MouseEventArgs e) { top = Math.Max(0, Math.Min(MaxTop, top - Math.Sign(e.Delta) * 3)); Invalidate(); base.OnMouseWheel(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var th = Thumb();
            if (!th.IsEmpty && e.X >= Width - BarW - 6)
            {
                if (th.Contains(e.X, e.Y) || true) { dragThumb = true; dragY = e.Y; dragTop = top; if (!th.Contains(e.X, e.Y)) { top = Math.Max(0, Math.Min(MaxTop, MaxTop * e.Y / Math.Max(1, Height))); dragTop = top; Invalidate(); } }
                return;
            }
            int i = top + e.Y / RowH;
            if (i >= Items.Count) { if (e.Button == MouseButtons.Left) { sel.Clear(); Invalidate(); Fire(); } return; }
            if (e.Button == MouseButtons.Right && sel.Contains(i)) return;
            if (MultiSelect && (ModifierKeys & Keys.Control) != 0) { if (!sel.Remove(i)) sel.Add(i); anchor = i; }
            else if (MultiSelect && (ModifierKeys & Keys.Shift) != 0 && anchor >= 0) { sel.Clear(); for (int k = Math.Min(anchor, i); k <= Math.Max(anchor, i); k++) sel.Add(k); }
            else { sel.Clear(); sel.Add(i); anchor = i; }
            Invalidate(); Fire();
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragThumb)
            {
                var th = Thumb(); int track = Math.Max(1, Height - th.Height);
                top = Math.Max(0, Math.Min(MaxTop, dragTop + (e.Y - dragY) * MaxTop / track));
                Invalidate(); return;
            }
            int h = e.X >= Width - BarW - 6 ? -2 : top + e.Y / RowH;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragThumb = false; base.OnMouseUp(e); }
        protected override void OnResize(EventArgs e) { top = Math.Min(top, MaxTop); base.OnResize(e); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) && Items.Count > 0)
            {
                int i = SelectedIndex < 0 ? 0 : Math.Max(0, Math.Min(Items.Count - 1, SelectedIndex + (e.KeyCode == Keys.Up ? -1 : 1)));
                SelectedIndex = i;
            }
            if (e.KeyCode == Keys.A && e.Control && MultiSelect) { sel.Clear(); for (int k = 0; k < Items.Count; k++) sel.Add(k); Invalidate(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Color back = Theme.Classic ? Theme.Input : Theme.Card;
            if (Theme.Translucent && !Theme.Classic) Gfx.Backdrop(this, g); else g.Clear(back);
            int rh = RowH, w = Width - BarW - 6;
            for (int i = top; i < Items.Count && (i - top) * rh < Height; i++)
            {
                var r = new Rectangle(0, (i - top) * rh, w, rh);
                bool s = sel.Contains(i);
                if (s) { using (var b = new SolidBrush(Theme.Classic ? Theme.Accent : Theme.CardHi)) g.FillRectangle(b, r); if (!Theme.Classic) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, r.Y + Theme.S(5), Theme.S(2), rh - Theme.S(10)); }
                else if (i == hover) using (var b = new SolidBrush(Theme.Mix(back, Theme.CardHi, 0.5))) g.FillRectangle(b, r);
                Color fg = s && Theme.Classic ? Theme.AccentFg : Theme.Text;
                int x = Theme.S(8);
                string text = Items[i];
                // «приложение|домен» — домен под приложением: отступ и значок глобуса
                if (AppLists.IsChild(text))
                {
                    x += Theme.S(20);
                    TextRenderer.DrawText(g, "\uE774", Theme.Icons, new Rectangle(x, r.Y, Theme.S(16), rh), s && Theme.Classic ? Theme.AccentFg : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                    x += Theme.S(24);
                    text = AppLists.ChildOf(text);
                }
                else if (ShowIcons)
                {
                    int isz = Theme.S(16);
                    var ico = AppIcons.Get(Items[i], this);
                    if (ico != null) g.DrawImage(ico, x, r.Y + (rh - isz) / 2, isz, isz);
                    else TextRenderer.DrawText(g, "", Theme.Icons, new Rectangle(x, r.Y, isz, rh), Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                    x += isz + Theme.S(8);
                }
                string right = RightText != null ? RightText(Items[i]) : null;
                int rw = string.IsNullOrEmpty(right) ? 0 : TextRenderer.MeasureText(right, Theme.Small).Width + Theme.S(8);
                bool bold = BoldItem != null && BoldItem(Items[i]);
                TextRenderer.DrawText(g, text, bold ? Theme.Bold : Theme.Body, new Rectangle(x, r.Y, w - x - rw, rh), fg, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                if (rw > 0) TextRenderer.DrawText(g, right, Theme.Small, new Rectangle(w - rw, r.Y, rw - Theme.S(4), rh), s && Theme.Classic ? Theme.AccentFg : Theme.Accent, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            if (Items.Count == 0) TextRenderer.DrawText(g, "—", Theme.Body, new Rectangle(0, 0, Width, rh * 2), Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var th = Thumb();
            if (!th.IsEmpty)
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Color tc = dragThumb || hover == -2 ? Theme.Muted : Theme.Dim;
                if (Theme.Classic) { using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, th); Gfx.Bevel(g, th, true); }
                else Gfx.FillRound(g, tc, th, BarW / 2f);
            }
        }
    }

    // Иконки приложений для списков: из полного пути или по запущенному процессу. Кэшируются, ищутся в фоне.
    public static class AppIcons
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, DateTime> missAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Путь уже известен (из снимка процессов) — извлечь иконку без поиска процесса
        public static void Seed(string name, string path)
        {
            Remember(name, path);
            lock (cache) { if (cache.ContainsKey(name)) return; }
            try { using (var ico = Icon.ExtractAssociatedIcon(path)) if (ico != null) { var b = new Icon(ico, 16, 16).ToBitmap(); lock (cache) { cache[name] = b; missAt.Remove(name); } } } catch { }
        }

        // Свои иконки для элементов списков: config\icons.txt (элемент|путь к .exe/.ico/.png)
        static Dictionary<string, string> custom;
        static string IconsFile { get { return System.IO.Path.Combine(Settings.Dir, "icons.txt"); } }
        static Dictionary<string, string> Custom()
        {
            if (custom != null) return custom;
            custom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(IconsFile)) foreach (var l in File.ReadAllLines(IconsFile)) { int i = l.IndexOf('|'); if (i > 0) custom[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
            return custom;
        }
        public static void SetCustom(string entry, string path)
        {
            var c = Custom();
            if (string.IsNullOrEmpty(path)) c.Remove(entry); else c[entry] = path;
            try { File.WriteAllLines(IconsFile, c.Select(kv => kv.Key + "|" + kv.Value).ToArray()); } catch { }
            lock (cache) { cache.Remove(entry); missAt.Remove(entry); }
        }
        static Bitmap LoadIcon(string path)
        {
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ico") using (var ico = new Icon(path, 16, 16)) return ico.ToBitmap();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") using (var img = Image.FromFile(path)) return new Bitmap(img, 16, 16);
            using (var ico = Icon.ExtractAssociatedIcon(path)) return new Icon(ico, 16, 16).ToBitmap();
        }

        public static Bitmap Get(string entry, Control owner)
        {
            string cp;
            if (Custom().TryGetValue(entry, out cp))
                lock (cache) { Bitmap cb; if (!cache.TryGetValue(entry, out cb)) { try { cb = LoadIcon(cp); } catch { cb = null; } if (cb != null) cache[entry] = cb; } if (cb != null) return cb; }
            lock (cache)
            {
                Bitmap b;
                if (cache.TryGetValue(entry, out b)) return b;
                DateTime m;
                if (missAt.TryGetValue(entry, out m) && (DateTime.Now - m).TotalSeconds < 30) return null;
                if (!pending.Add(entry)) return null;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Bitmap bmp = null;
                try
                {
                    string path = Resolve(entry);
                    if (path != null) bmp = LoadIcon(path);
                }
                catch { }
                lock (cache)
                {
                    pending.Remove(entry);
                    if (bmp != null) cache[entry] = bmp; else missAt[entry] = DateTime.Now;
                }
                if (bmp != null && owner != null && owner.IsHandleCreated && !owner.IsDisposed) try { owner.BeginInvoke((Action)owner.Invalidate); } catch { }
            });
            return null;
        }

        public static string PathOf(string entry) { try { return Resolve(entry); } catch { return null; } }

        // Путь к exe даже для незапущенных программ: запомненные пути, запущенные процессы,
        // App Paths в реестре, ярлыки меню «Пуск», DisplayIcon из списка установленных программ.
        static Dictionary<string, string> known;
        static readonly object knownLk = new object();
        static string KnownFile { get { return System.IO.Path.Combine(Settings.Dir, "iconpaths.txt"); } }
        static List<Tuple<string, string>> shortcuts;

        static Dictionary<string, string> Known()
        {
            lock (knownLk)
            {
                if (known != null) return known;
                known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try { if (File.Exists(KnownFile)) foreach (var l in File.ReadAllLines(KnownFile)) { int i = l.IndexOf('|'); if (i > 0) known[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
                return known;
            }
        }

        static void Remember(string entry, string path)
        {
            lock (knownLk)
            {
                var k = Known();
                string old; if (k.TryGetValue(entry, out old) && old == path) return;
                k[entry] = path;
                try { File.WriteAllLines(KnownFile, k.Select(kv => kv.Key + "|" + kv.Value).ToArray()); } catch { }
            }
        }

        static string Key(string entry) { string e = entry.Trim().ToLowerInvariant(); return e.EndsWith(".exe") ? e.Substring(0, e.Length - 4) : e; }

        static bool NameMatch(string entry, string file)
        {
            string k = Key(entry), f = System.IO.Path.GetFileNameWithoutExtension(file ?? "").ToLowerInvariant();
            return k.Length > 0 && f.Length > 0 && (f == k || f.Contains(k));
        }

        static string Resolve(string entry)
        {
            if (entry.Contains("\\") && File.Exists(entry)) return entry;
            if (ExclusionSet.IsIp(entry) || (entry.Contains(".") && !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) return null; // домен
            string kp;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName + ".exe";
                    if (ProcInfo.Matches(entry, name, "") || name.Equals(entry, StringComparison.OrdinalIgnoreCase))
                    {
                        string path = ProcInfo.ImagePath(p.Id);
                        if (path != "") { Remember(entry, path); return path; }
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
            if (Known().TryGetValue(entry, out kp) && File.Exists(kp)) return kp;
            string found = FromAppPaths(entry) ?? FromShortcuts(entry) ?? FromUninstall(entry) ?? FromFolders(entry);
            if (found != null) Remember(entry, found);
            return found;
        }

        static string FromAppPaths(string entry)
        {
            string exe = Key(entry) + ".exe";
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                try
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                    {
                        string v = k != null ? Convert.ToString(k.GetValue("")).Trim('"') : null;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            return null;
        }

        static string FromShortcuts(string entry)
        {
            lock (knownLk)
            {
                if (shortcuts == null)
                {
                    shortcuts = new List<Tuple<string, string>>();
                    try
                    {
                        var t = Type.GetTypeFromProgID("WScript.Shell");
                        object sh = Activator.CreateInstance(t);
                        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
                        {
                            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                            foreach (var lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
                                try
                                {
                                    object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
                                    string target = Convert.ToString(sc.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null));
                                    if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) shortcuts.Add(Tuple.Create(System.IO.Path.GetFileNameWithoutExtension(lnk), target));
                                }
                                catch { }
                        }
                    }
                    catch { }
                }
            }
            var hit = shortcuts.FirstOrDefault(x => NameMatch(entry, x.Item2)) ?? shortcuts.FirstOrDefault(x => x.Item1.ToLowerInvariant().Contains(Key(entry)));
            return hit != null ? hit.Item2 : null;
        }

        // Библиотеки Steam (steamapps\common) и папки пользователя (Рабочий стол, Загрузки) — неглубокий поиск exe
        static List<string> exeIndex;
        static string FromFolders(string entry)
        {
            lock (knownLk)
            {
                if (exeIndex == null)
                {
                    exeIndex = new List<string>();
                    var roots = new List<Tuple<string, int>>();
                    try
                    {
                        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                        {
                            string sp = k != null ? Convert.ToString(k.GetValue("SteamPath")).Replace('/', '\\') : "";
                            if (sp != "")
                            {
                                var libs = new List<string> { sp };
                                string vdf = System.IO.Path.Combine(sp, "steamapps", "libraryfolders.vdf");
                                if (File.Exists(vdf))
                                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                                        libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                                foreach (var l in libs.Distinct(StringComparer.OrdinalIgnoreCase)) roots.Add(Tuple.Create(System.IO.Path.Combine(l, "steamapps", "common"), 3));
                            }
                        }
                    }
                    catch { }
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    roots.Add(Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), 3));
                    roots.Add(Tuple.Create(System.IO.Path.Combine(home, "Downloads"), 2));
                    roots.Add(Tuple.Create(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 2));
                    foreach (var r in roots) Walk(r.Item1, r.Item2);
                }
            }
            return exeIndex.FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(Key(entry), StringComparison.OrdinalIgnoreCase))
                ?? exeIndex.FirstOrDefault(f => NameMatch(entry, f));
        }

        static void Walk(string dir, int depth)
        {
            if (depth < 0 || string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || exeIndex.Count > 20000) return;
            try
            {
                foreach (var f in Directory.GetFiles(dir, "*.exe"))
                {
                    string n = System.IO.Path.GetFileName(f).ToLowerInvariant();
                    if (n.Contains("unins") || n.Contains("setup") || n.Contains("crash") || n.Contains("update") || n.StartsWith("vc_redist")) continue;
                    exeIndex.Add(f);
                }
                foreach (var d in Directory.GetDirectories(dir)) Walk(d, depth - 1);
            }
            catch { }
        }

        static string FromUninstall(string entry)
        {
            string k = Key(entry);
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                foreach (var root in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    try
                    {
                        using (var r = hive.OpenSubKey(root))
                        {
                            if (r == null) continue;
                            foreach (var sub in r.GetSubKeyNames())
                                using (var a = r.OpenSubKey(sub))
                                {
                                    string name = Convert.ToString(a.GetValue("DisplayName")).ToLowerInvariant();
                                    string icon = Convert.ToString(a.GetValue("DisplayIcon"));
                                    if (name == "" || icon == "" || !name.Contains(k)) continue;
                                    int c = icon.LastIndexOf(','); if (c > 2) icon = icon.Substring(0, c);
                                    icon = icon.Trim().Trim('"');
                                    if (File.Exists(icon)) return icon;
                                }
                        }
                    }
                    catch { }
            return null;
        }
    }
}
