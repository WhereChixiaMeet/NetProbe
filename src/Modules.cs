using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // 3.2: модульный интерфейс.
    //  • Модуль = карточка: TUN и автосвитч на Subscriptions, каждый раздел Настроек. У модуля есть «дом» (своя страница),
    //    но его можно положить на ЛЮБУЮ страницу: перетащить за заголовок на пункт меню слева или ПКМ по заголовку → «Перенести в».
    //    Модуль остаётся тем же живым элементом — все его настройки работают там, куда его положили.
    //  • На обычных страницах чужие модули встают в правую колонку (ширина тянется разделителем); нет модулей — колонки нет.
    //    На Subscriptions — в её правую колонку, в Настройках — во вкладку модуля, на своих вкладках — в колонки.
    //  • Пункты меню слева переименовываются, можно создать свою вкладку (ПКМ по меню). Lock interface — всё зафиксировано.
    // Хранение: ModuleLoc «ключ=pN|cN;…» (нет записи — модуль дома), CustomPages «c1:Название;…», NavNames «индекс:Название;…».
    public partial class MainForm
    {
        readonly Dictionary<string, CardPanel> modules = new Dictionary<string, CardPanel>();
        readonly List<string> moduleKeys = new List<string>();
        readonly Dictionary<string, int> moduleHome = new Dictionary<string, int>();
        readonly Dictionary<string, string> moduleTab = new Dictionary<string, string>();
        // правая колонка «чужих» модулей на обычных страницах
        readonly Dictionary<int, ScrollHost> docks = new Dictionary<int, ScrollHost>();
        readonly Dictionary<int, Panel> dockSplits = new Dictionary<int, Panel>();
        readonly Dictionary<int, Panel> pageOuter = new Dictionary<int, Panel>();
        readonly Dictionary<string, ScrollHost> customHosts = new Dictionary<string, ScrollHost>();
        readonly Dictionary<string, Action> customLays = new Dictionary<string, Action>();
        Panel srvModBody;

        IEnumerable<string> ModuleKeys() { return moduleKeys.ToList(); }

        static Dictionary<string, string> Map(string s, char kv)
        {
            var m = new Dictionary<string, string>();
            foreach (var part in (s ?? "").Split(';'))
            {
                int i = part.IndexOf(kv); if (i <= 0) continue;
                m[part.Substring(0, i)] = part.Substring(i + 1);
            }
            return m;
        }
        static string Unmap(Dictionary<string, string> m, char kv) { return string.Join(";", m.Select(x => x.Key + kv + x.Value.Replace(";", ",").Replace(":", " ").Replace("=", " "))); }

        // свои вкладки: c1 → «Название», по порядку создания; страница — сразу после «Настроек»
        List<KeyValuePair<string, string>> CustomPages()
        {
            return (st.CustomPages ?? "").Split(';').Select(p => { int i = p.IndexOf(':'); return i > 0 ? new KeyValuePair<string, string>(p.Substring(0, i), p.Substring(i + 1)) : new KeyValuePair<string, string>("", ""); })
                .Where(p => p.Key != "").ToList();
        }
        int PageCount { get { return PgSettings + 1 + CustomPages().Count; } }
        string LocOfPage(int idx) { return idx > PgSettings ? CustomPages()[idx - PgSettings - 1].Key : "p" + idx; }
        int PageOfLoc(string loc)
        {
            if (loc == null) return -1;
            if (loc.StartsWith("c")) { int i = CustomPages().FindIndex(p => p.Key == loc); return i < 0 ? -1 : PgSettings + 1 + i; }
            int n; if (loc.StartsWith("p") && int.TryParse(loc.Substring(1), out n) && n >= 0 && n <= PgSettings) return n;
            if (loc == "settings") return PgSettings; // 3.2.0
            return -1;
        }
        int PageOf(string key)
        {
            string v; int home; moduleHome.TryGetValue(key, out home);
            if (!Map(st.ModuleLoc, '=').TryGetValue(key, out v)) return home;
            int p = PageOfLoc(v);
            return p < 0 ? home : p;
        }
        string ModTab(string key) { string t; return moduleTab.TryGetValue(key, out t) ? t : "subs"; }
        bool ModInSettings(string key) { return PageOf(key) == PgSettings; }

        string NavName(int idx, string def) { string v; return Map(st.NavNames, ':').TryGetValue(idx.ToString(), out v) && v.Trim() != "" ? v : def; }
        string PageName(int idx) { return idx < navs.Count ? navs[idx].Text : "#" + idx; }

        void RegisterModule(CardPanel c, string key, int home, string tab)
        {
            c.ModuleKey = key; modules[key] = c; moduleHome[key] = home; moduleTab[key] = tab;
            if (!moduleKeys.Contains(key)) moduleKeys.Add(key);
            c.ModuleMenu += ShowModuleMenu;
            Ui.Tip().SetToolTip(c, Tx("Перетащи заголовок на пункт меню слева, чтобы перенести карточку туда. ПКМ по заголовку — меню."));
        }

        // ---- Lock interface ----
        bool LockedMenu()
        {
            if (!st.UiLocked) return false;
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            cm.Items.Add(Tx("Интерфейс заблокирован — разблокировать"), null, (s, e) => SetUiLocked(false));
            cm.Closed += (s, e) => BeginInvoke((Action)(() => cm.Dispose()));
            cm.Show(Cursor.Position);
            return true;
        }
        void SetUiLocked(bool on)
        {
            st.UiLocked = on; st.Save(); CardPanel.Locked = on;
            Logger.Write(LogLevel.Info, on ? Tx("Интерфейс заблокирован") : Tx("Интерфейс разблокирован"));
            if (cLock != null && !cLock.IsDisposed && cLock.Checked != on) cLock.Checked = on;
            foreach (var sp in dockSplits.Values) if (!sp.IsDisposed) sp.Cursor = on ? Cursors.Default : Cursors.SizeWE;
        }

        void ShowModuleMenu(CardPanel c)
        {
            if (LockedMenu()) return;
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            int cur = PageOf(c.ModuleKey), home; moduleHome.TryGetValue(c.ModuleKey, out home);
            var to = new ToolStripMenuItem(Tx("Перенести в"));
            for (int i = 0; i < PageCount && i < navs.Count; i++)
            {
                int pi = i;
                var mi = new ToolStripMenuItem(PageName(i) + (i == home ? "  ·  " + Tx("дом") : "")) { Enabled = i != cur, Checked = i == cur };
                mi.Click += (s, e) => MoveModule(c.ModuleKey, pi, true);
                to.DropDownItems.Add(mi);
            }
            to.DropDownItems.Add(new ToolStripSeparator());
            to.DropDownItems.Add(Tx("Новая вкладка…"), null, (s, e) => { var id = NewCustomPage(); if (id != null) MoveModuleLoc(c.ModuleKey, id, true); });
            cm.Items.Add(to);
            if (cur != home) cm.Items.Add(Tx("Вернуть домой") + " (" + PageName(home) + ")", null, (s, e) => MoveModule(c.ModuleKey, home, true));
            cm.Items.Add(Tx("Все карточки — на свои места"), null, (s, e) => ResetLayout());
            cm.Closed += (s, e) => BeginInvoke((Action)(() => cm.Dispose()));
            cm.Show(Cursor.Position);
        }

        void MoveModule(string key, int page, bool open) { MoveModuleLoc(key, LocOfPage(page), open); }
        void MoveModuleLoc(string key, string loc, bool open)
        {
            int page = PageOfLoc(loc); if (page < 0) return;
            if (PageOf(key) == page) return;
            var m = Map(st.ModuleLoc, '=');
            int home; moduleHome.TryGetValue(key, out home);
            if (page == home) m.Remove(key); else m[key] = loc;
            st.ModuleLoc = Unmap(m, '=');
            if (page == PgSettings) st.SetTab = ModTab(key);
            st.Save();
            Logger.Write(LogLevel.Info, Tx("Карточка «") + (modules.ContainsKey(key) ? modules[key].Title : key) + Tx("» → ") + PageName(page));
            ApplyModules(open ? page : curPage);
        }

        void ResetLayout()
        {
            st.ModuleLoc = ""; st.DockW = 380; st.SrvRightW = 360; st.SideWidth = 214; st.UiLocked = false; CardPanel.Locked = false; st.Save();
            Logger.Write(LogLevel.Info, Tx("Раскладка интерфейса сброшена: все карточки на своих местах"));
            if (hiddenToTray) RestoreFromTray();
            ApplyModules(curPage);
        }

        void ApplyModules(int page) { BeginInvoke((Action)(() => { RebuildUi(); SelectPage(Math.Min(page, pages.Count - 1)); })); }

        // пункт меню слева принимает перетаскиваемую карточку
        void HookModuleDrop(NavButton nb, int page)
        {
            nb.AllowDrop = true;
            DragEventHandler over = (s, e) =>
            {
                var key = e.Data != null && e.Data.GetDataPresent(CardPanel.DragFormat) ? e.Data.GetData(CardPanel.DragFormat) as string : null;
                bool ok = key != null && !st.UiLocked && PageOf(key) != page;
                e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None; nb.Hot = ok;
            };
            nb.DragEnter += over; nb.DragOver += over;
            nb.DragLeave += (s, e) => nb.Hot = false;
            nb.DragDrop += (s, e) =>
            {
                nb.Hot = false;
                var key = e.Data.GetData(CardPanel.DragFormat) as string;
                if (key != null && !st.UiLocked) MoveModule(key, page, true);
            };
        }

        // ---- страница = внешняя панель: [прокрутка: содержимое страницы + полоса модулей под ним] + [колонка модулей справа] ----
        // Содержимое страницы НИКОГДА не сжимается: по ширине не уже ContentMin, по высоте — во весь экран.
        // Модули: справа, если колонка влезает; иначе — полосой ПОД страницей (страница прокручивается вниз к ним).
        sealed class PageFrame { public Panel Outer, Inner, Split, Strip; public ScrollHost Scroll, Dock; public FlatButton More; }
        readonly Dictionary<int, PageFrame> frames = new Dictionary<int, PageFrame>();
        Panel WrapPage(Panel inner, int idx)
        {
            var f = new PageFrame { Inner = inner };
            f.Outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            f.Scroll = new ScrollHost { BackColor = Theme.Bg, Dock = DockStyle.Fill };
            f.Dock = new ScrollHost { BackColor = Theme.Bg, Dock = DockStyle.Right, Width = Theme.S(380), Visible = false };
            f.Split = new Panel { Dock = DockStyle.Right, Width = Theme.S(12), BackColor = Color.Transparent, Visible = false, Cursor = st.UiLocked ? Cursors.Default : Cursors.SizeWE };
            f.Strip = new BackdropPanel { BackColor = Theme.Bg, Visible = false };
            inner.Dock = DockStyle.None;
            f.Scroll.Content.Controls.Add(inner); f.Scroll.Content.Controls.Add(f.Strip);
            f.Outer.Controls.Add(f.Scroll); f.Outer.Controls.Add(f.Split); f.Outer.Controls.Add(f.Dock);
            frames[idx] = f; docks[idx] = f.Dock; dockSplits[idx] = f.Split; pageOuter[idx] = f.Outer;
            bool drag = false; int x0 = 0, w0 = 0;
            f.Split.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && !st.UiLocked) { drag = true; x0 = Cursor.Position.X; w0 = f.Dock.Width; f.Split.Capture = true; } };
            f.Split.MouseMove += (s, e) => { if (!drag) return; st.DockW = (int)Math.Round(Math.Max(1, w0 - (Cursor.Position.X - x0)) / Theme.Scale); LayFrame(f); };
            f.Split.MouseUp += (s, e) => { if (!drag) return; drag = false; f.Split.Capture = false; st.Save(); };
            f.Outer.Resize += (s, e) => LayFrame(f);
            // модули под страницей — кнопка-подсказка внизу: «▼ Модули ниже» (прокручивает к ним); прокрутили — прячется
            f.More = new FlatButton(Tx("Модули ниже")) { Glyph = "\uE74B", Visible = false, Height = Theme.S(30) };
            f.More.Click += (s, e) => f.Scroll.ScrollTo(f.Strip.Top);
            f.Outer.Controls.Add(f.More); f.More.BringToFront();
            f.Scroll.Scrolled += v => PlaceMore(f);
            return f.Outer;
        }
        static int ContentMin { get { return Theme.S(800); } }
        void PlaceMore(PageFrame f)
        {
            bool show = f.Strip.Visible && f.Scroll.Offset < Theme.S(40);
            f.More.Visible = show;
            if (show) { f.More.Location = new Point((f.Scroll.Width - f.More.Width) / 2, f.Outer.ClientSize.Height - f.More.Height - Theme.S(10)); f.More.BringToFront(); }
        }
        bool layingFrame;
        // модули страницы лежат в f.Dock.Content или f.Strip; отсюда — в колонку справа или в полосу под страницей
        void LayFrame(PageFrame f)
        {
            if (layingFrame || f.Outer.IsDisposed) return;
            layingFrame = true;
            try
            {
                var cards = f.Dock.Content.Controls.OfType<CardPanel>().Concat(f.Strip.Controls.OfType<CardPanel>())
                    .OrderBy(c => moduleKeys.IndexOf(c.ModuleKey)).ToList();
                int W = f.Outer.ClientSize.Width, H = f.Outer.ClientSize.Height;
                int minW = Theme.S(300); foreach (var c in cards) minW = Math.Max(minW, c.Width + Theme.S(18));
                bool any = cards.Count > 0, right = any && W - minW - f.Split.Width >= ContentMin;
                f.Dock.Visible = right; f.Split.Visible = right;
                Control box = right ? (Control)f.Dock.Content : f.Strip;
                foreach (var c in cards) if (c.Parent != box) { c.Parent.Controls.Remove(c); box.Controls.Add(c); }
                if (right)
                {
                    f.Dock.Width = Math.Max(minW, Math.Min(W - ContentMin - f.Split.Width, Math.Max(minW, Theme.S(st.DockW))));
                    int y = Theme.S(80);
                    foreach (var c in cards) { c.SetBounds(0, y, Math.Max(c.Width, f.Dock.Content.Width - Theme.S(2)), c.Height); y = c.Bottom + Theme.S(12); }
                    f.Dock.Content.Height = y + Theme.S(10); f.Dock.Relayout();
                }
                int viewW = right ? W - f.Dock.Width - f.Split.Width : W;
                // содержимое страницы — во весь видимый экран; полоса модулей (если есть) — ниже, к ней прокручивают
                int stripH = 0;
                f.Strip.Visible = any && !right;
                if (f.Strip.Visible)
                {
                    int L = Theme.S(26), gap = Theme.S(12), cw = Math.Max(10, viewW - L * 2 - Theme.S(14)), x = 0, y = Theme.S(4), rowH = 0;
                    foreach (var c in cards)
                    {
                        if (x > 0 && x + c.Width > cw) { x = 0; y += rowH + gap; rowH = 0; }
                        c.Location = new Point(L + x, y); x += c.Width + gap; rowH = Math.Max(rowH, c.Height);
                    }
                    stripH = y + rowH + Theme.S(16);
                }
                f.Scroll.Content.Height = H + stripH;
                f.Scroll.MinContentWidth = 0; f.Scroll.Relayout();
                int cwid = f.Scroll.Content.Width;
                // карточка шире окна (узкое окно / широкая левая панель) — полоса шире, снизу горизонтальная прокрутка, ничего не обрезается
                if (f.Strip.Visible)
                {
                    int need = Theme.S(26) * 2 + cards.Max(c => c.Width) + Theme.S(4);
                    if (need > cwid) { f.Scroll.MinContentWidth = need; f.Scroll.Relayout(); }
                }
                f.Inner.SetBounds(0, 0, cwid, H);
                if (f.Strip.Visible) f.Strip.SetBounds(0, H, Math.Max(cwid, f.Scroll.MinContentWidth), stripH);
                else f.Scroll.ScrollToTop();
                f.More.Text = Tx("Модули ниже") + " (" + cards.Count + ")"; f.More.AutoW();
                PlaceMore(f);
            }
            finally { layingFrame = false; }
        }

        // все модули — по своим местам (вызывается в конце сборки интерфейса)
        void PlaceAllModules()
        {
            foreach (var key in moduleKeys.ToList())
            {
                CardPanel c; if (!modules.TryGetValue(key, out c) || c.IsDisposed) { moduleKeys.Remove(key); continue; }
                int page = PageOf(key);
                Control target;
                // в правую колонку Subscriptions — только её узкие карточки; широкие (разделы Настроек) — в общую полосу модулей страницы
                if (page == PgServers && srvModBody != null && key.StartsWith("srv.")) target = srvModBody;
                else if (page == PgServers) { ScrollHost d0; target = docks.TryGetValue(page, out d0) ? d0.Content : srvModBody; }
                else if (page == PgSettings && setBody != null) target = setBody;
                else if (page > PgSettings) { ScrollHost h; target = customHosts.TryGetValue(LocOfPage(page), out h) ? h.Content : null; }
                else { ScrollHost d; target = docks.TryGetValue(page, out d) ? d.Content : null; }
                if (target == null) { page = moduleHome[key]; target = page == PgServers ? (Control)srvModBody : setBody; }
                if (c.Parent != target) { if (c.Parent != null) c.Parent.Controls.Remove(c); target.Controls.Add(c); }
                c.Visible = true;
            }
            foreach (var fr in frames.Values) LayFrame(fr);
            foreach (var l in customLays.Values) l();
            if (relayServers != null) relayServers();
            if (setTabs != null) FillSettingsTabs();
        }

        // ---- меню слева: переименование, свои вкладки ----
        void NavMenu(int idx)
        {
            if (LockedMenu()) return;
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            var cp = CustomPages();
            bool custom = idx > PgSettings && idx - PgSettings - 1 < cp.Count;
            cm.Items.Add(Tx("Переименовать…"), null, (s, e) =>
            {
                string v = Prompt(Tx("Переименовать"), Tx("Название пункта меню:"), navs[idx].Text);
                if (string.IsNullOrWhiteSpace(v)) return;
                if (custom) { var l = CustomPages(); l[idx - PgSettings - 1] = new KeyValuePair<string, string>(l[idx - PgSettings - 1].Key, v); SaveCustomPages(l); }
                else { var m = Map(st.NavNames, ':'); m[idx.ToString()] = v; st.NavNames = Unmap(m, ':'); }
                st.Save(); ApplyModules(curPage);
            });
            if (!custom && Map(st.NavNames, ':').ContainsKey(idx.ToString()))
                cm.Items.Add(Tx("Название по умолчанию"), null, (s, e) => { var m = Map(st.NavNames, ':'); m.Remove(idx.ToString()); st.NavNames = Unmap(m, ':'); st.Save(); ApplyModules(curPage); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tx("Новая вкладка…"), null, (s, e) => { var id = NewCustomPage(); if (id != null) ApplyModules(PageOfLoc(id)); });
            if (custom)
                cm.Items.Add(Tx("Удалить вкладку (карточки вернутся на место)"), null, (s, e) =>
                {
                    var l = CustomPages(); string id = l[idx - PgSettings - 1].Key; l.RemoveAt(idx - PgSettings - 1);
                    var m = Map(st.ModuleLoc, '='); foreach (var k in m.Where(x => x.Value == id).Select(x => x.Key).ToList()) m.Remove(k); st.ModuleLoc = Unmap(m, '=');
                    SaveCustomPages(l); st.Save(); ApplyModules(PgServers);
                });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tx("Сбросить раскладку интерфейса"), null, (s, e) => ResetLayout());
            cm.Closed += (s, e) => BeginInvoke((Action)(() => cm.Dispose()));
            cm.Show(Cursor.Position);
        }
        void SaveCustomPages(List<KeyValuePair<string, string>> l) { st.CustomPages = string.Join(";", l.Select(p => p.Key + ":" + p.Value.Replace(";", ",").Replace(":", " "))); }

        string NewCustomPage()
        {
            string name = Prompt(Tx("Новая вкладка"), Tx("Название вкладки:"), Tx("Моя вкладка"));
            if (string.IsNullOrWhiteSpace(name)) return null;
            var l = CustomPages();
            int n = 1; while (l.Any(p => p.Key == "c" + n)) n++;
            l.Add(new KeyValuePair<string, string>("c" + n, name));
            SaveCustomPages(l); st.Save();
            return "c" + n;
        }

        // свои вкладки: карточки колонками по ширине окна, пусто — подсказка
        void BuildCustomPages()
        {
            customHosts.Clear(); customLays.Clear();
            foreach (var cpg in CustomPages())
            {
                string id = cpg.Key;
                var p = NewPage(cpg.Value, Tx("Своя вкладка · ПКМ по пункту меню — переименовать или удалить"));
                var host = new ScrollHost { BackColor = Theme.Bg };
                var empty = Ui.Hint(Tx("Пусто. Перетащи сюда карточку за заголовок (на этот пункт меню слева) или ПКМ по заголовку карточки → «Перенести в»."), Theme.S(520));
                empty.BackColor = Theme.Bg;
                p.Controls.AddRange(new Control[] { host, empty });
                customHosts[id] = host;
                Action lay = () =>
                {
                    var mine = host.Content.Controls.OfType<CardPanel>().OrderBy(c => moduleKeys.IndexOf(c.ModuleKey)).ToList();
                    int L = Theme.S(26), T = Theme.S(80), W = p.ClientSize.Width - L * 2, gap = Theme.S(14);
                    empty.Visible = mine.Count == 0; empty.Location = new Point(L, T);
                    host.SetBounds(L, T, W, Math.Max(10, p.ClientSize.Height - T - Theme.S(14)));
                    int cw = mine.Count == 0 ? 1 : mine.Max(c => c.Width), cols = Math.Max(1, (W - Theme.S(14) + gap) / (cw + gap));
                    var colY = new int[cols];
                    foreach (var c in mine) { int col = Array.IndexOf(colY, colY.Min()); c.Location = new Point(col * (cw + gap), colY[col]); colY[col] = c.Bottom + gap; }
                    host.Content.Height = colY.Max() + Theme.S(10);
                    host.MinContentWidth = mine.Count == 0 ? 0 : cw + Theme.S(2);
                    host.Relayout();
                };
                customLays[id] = lay;
                p.Resize += (s, e) => lay();
            }
        }
    }
}
