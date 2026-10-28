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
        string ModTab(string key)
        {
            string t;
            if (Map(st.ModuleTabs, '=').TryGetValue(key, out t) && SetTabsDef.Any(d => d[0] == t)) return t; // положили в другую вкладку Настроек
            return moduleTab.TryGetValue(key, out t) ? t : "subs";
        }
        bool ModInSettings(string key) { return PageOf(key) == PgSettings; }

        string NavName(int idx, string def) { string v; return Map(st.NavNames, ':').TryGetValue(idx.ToString(), out v) && v.Trim() != "" ? v : def; }
        string PageName(int idx) { return idx < navs.Count ? navs[idx].Text : "#" + idx; }

        void RegisterModule(CardPanel c, string key, int home, string tab)
        {
            c.RememberBase();
            c.ModuleKey = key; modules[key] = c; moduleHome[key] = home; moduleTab[key] = tab;
            if (!moduleKeys.Contains(key)) moduleKeys.Add(key);
            c.ModuleMenu += ShowModuleMenu;
            Ui.Tip().SetToolTip(c, Tx("Тащи за заголовок: на пункт меню слева — на ту страницу, в колонке — поменять порядок. ПКМ по заголовку — меню."));
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
            var tm = Map(st.ModuleTabs, '='); tm.Remove(key); st.ModuleTabs = Unmap(tm, '=');
            var order = moduleKeys.ToList(); order.Remove(key); order.Add(key); st.ModuleOrder = string.Join(",", order); // на новой странице — последней
            if (page == PgSettings) st.SetTab = ModTab(key);
            st.Save();
            Logger.Write(LogLevel.Info, Tx("Карточка «") + (modules.ContainsKey(key) ? modules[key].Title : key) + Tx("» → ") + PageName(page));
            ApplyModules(open ? page : curPage);
        }

        void ResetLayout()
        {
            ResetLayoutState();
            if (hiddenToTray) RestoreFromTray();
            ApplyModules(curPage);
        }
        void ResetLayoutState()
        {
            st.ModuleLoc = ""; st.ModuleOrder = ""; st.ModuleTabs = ""; st.DockW = 380; st.SrvRightW = 360; st.SideWidth = 214; st.UiLocked = false; CardPanel.Locked = false; st.Save();
            Logger.Write(LogLevel.Info, Tx("Раскладка интерфейса сброшена: все карточки на своих местах"));
        }

        void ApplyModules(int page) { BeginInvoke((Action)(() => { RebuildUi(); SelectPage(Math.Min(page, pages.Count - 1)); })); }

        // ---- перетаскивание карточек (3.2.1) ----
        // Карточку берут за заголовок: она обводится пунктиром, за курсором едет её полупрозрачная копия.
        //  • над пунктом меню слева — пункт подсвечен, отпустил → карточка уходит на ту страницу (в конец);
        //  • над колонкой/полосой модулей текущей страницы (или над вкладкой Настроек, своей вкладкой, колонкой Subscriptions) —
        //    линия показывает, куда встанет карточка; отпустил → порядок сохраняется (ModuleOrder), в Настройках — и вкладка.
        //  • у края прокручиваемой колонки — она сама прокручивается; Esc или отпустить вне цели — отмена.
        readonly List<KeyValuePair<NavButton, int>> navTargets = new List<KeyValuePair<NavButton, int>>();
        void HookModuleDrop(NavButton nb, int page)
        {
            navTargets.RemoveAll(x => x.Key.IsDisposed);
            navTargets.Add(new KeyValuePair<NavButton, int>(nb, page));
            if (!dragHooked)
            {
                dragHooked = true;
                CardPanel.DragStarted += OnCardDragStart; CardPanel.DragMoved += OnCardDragMove; CardPanel.DragEnded += OnCardDragEnd;
            }
        }

        sealed class DropAt { public int Page = -1; public NavButton Nav; public Control Box; public string Before; public bool NoOp; }
        bool dragHooked; CardPanel dragCard; Point dragGrab; float dragK = 1; GhostForm ghost; Panel dropLine; DropAt dropAt;
        System.Windows.Forms.Timer dragTimer;

        sealed class GhostForm : Form
        {
            public GhostForm(Bitmap bmp)
            {
                FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual; TopMost = true;
                BackgroundImage = bmp; BackgroundImageLayout = ImageLayout.None; ClientSize = bmp.Size; Opacity = 0.72;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                // не перехватывает мышь (клики проходят насквозь), не активируется, не в Alt+Tab
                get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x8000000; return cp; }
            }
            protected override void Dispose(bool disposing) { if (disposing && BackgroundImage != null) { BackgroundImage.Dispose(); BackgroundImage = null; } base.Dispose(disposing); }
        }

        void OnCardDragStart(CardPanel c, Point grab)
        {
            if (c.IsDisposed || st.UiLocked) return;
            dragCard = c; dragGrab = grab;
            try
            {
                int maxW = Theme.S(380), maxH = Theme.S(300);
                dragK = Math.Min(1f, Math.Min(maxW / (float)Math.Max(1, c.Width), maxH / (float)Math.Max(1, c.Height)));
                using (var full = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height)))
                {
                    c.DrawToBitmap(full, new Rectangle(0, 0, c.Width, c.Height));
                    var small = new Bitmap(Math.Max(1, (int)(c.Width * dragK)), Math.Max(1, (int)(c.Height * dragK)));
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g.DrawImage(full, new Rectangle(0, 0, small.Width, small.Height));
                        using (var pen = new Pen(Theme.Accent, Theme.S(2))) g.DrawRectangle(pen, 1, 1, small.Width - 2, small.Height - 2);
                    }
                    ghost = new GhostForm(small);
                }
                ghost.Location = GhostAt(Cursor.Position); ghost.Show(this);
            }
            catch { ghost = null; }
            dropLine = new Panel { BackColor = Theme.Accent, Visible = false };
            if (dragTimer == null) { dragTimer = new System.Windows.Forms.Timer { Interval = 60 }; dragTimer.Tick += (s, e) => { if (dragCard != null) DragTick(Cursor.Position, true); }; }
            dragTimer.Start();
            DragTick(Cursor.Position, false);
        }
        Point GhostAt(Point pt) { return new Point(pt.X - (int)(dragGrab.X * dragK), pt.Y - (int)(dragGrab.Y * dragK)); }
        void OnCardDragMove(CardPanel c, Point pt) { if (c == dragCard) DragTick(pt, false); }

        void DragTick(Point pt, bool fromTimer)
        {
            if (dragCard == null) return;
            if (ghost != null && !ghost.IsDisposed) ghost.Location = GhostAt(pt);
            var t = HitDrop(pt, dragCard.ModuleKey);
            ShowDrop(t);
            // у верхнего/нижнего края прокручиваемой колонки — прокрутка (таймер — пока держишь курсор у края)
            if (fromTimer)
            {
                foreach (var sh in DragScrollers())
                {
                    var r = sh.RectangleToScreen(sh.ClientRectangle);
                    if (!r.Contains(pt)) continue;
                    int edge = Theme.S(36), step = Theme.S(22);
                    if (pt.Y < r.Top + edge) sh.ScrollTo(sh.Offset - step); else if (pt.Y > r.Bottom - edge) sh.ScrollTo(sh.Offset + step);
                }
            }
        }

        IEnumerable<ScrollHost> DragScrollers()
        {
            PageFrame f;
            foreach (var b in ModuleBoxes(curPage)) { var sh = b.Parent as ScrollHost; if (sh != null) yield return sh; }
            if (frames.TryGetValue(curPage, out f) && f.StripOn) yield return f.Scroll;
        }

        // куда на текущей странице кладутся модули (видимые контейнеры)
        List<Control> ModuleBoxes(int page)
        {
            var l = new List<Control>();
            PageFrame f;
            if (page == PgServers && srvModBody != null && srvModBody.Parent != null && srvModBody.Parent.Visible) l.Add(srvModBody);
            else if (page == PgSettings && setBody != null) l.Add(setBody);
            else if (page > PgSettings) { ScrollHost h; if (customHosts.TryGetValue(LocOfPage(page), out h)) l.Add(h.Content); }
            else if (frames.TryGetValue(page, out f)) { if (f.DockOn) l.Add(f.Dock.Content); if (f.StripOn) l.Add(f.Strip); }
            return l;
        }
        static Rectangle ViewOf(Control box)
        {
            var r = box.RectangleToScreen(box.ClientRectangle);
            for (var p = box.Parent; p != null; p = p.Parent) r = Rectangle.Intersect(r, p.RectangleToScreen(p.ClientRectangle));
            return r;
        }

        DropAt HitDrop(Point pt, string key)
        {
            foreach (var nt in navTargets)
            {
                var nb = nt.Key; if (nb.IsDisposed || !nb.Visible) continue;
                if (!nb.RectangleToScreen(nb.ClientRectangle).Contains(pt)) continue;
                return new DropAt { Page = nt.Value, Nav = nb, NoOp = PageOf(key) == nt.Value };
            }
            foreach (var box in ModuleBoxes(curPage))
            {
                if (box.IsDisposed || !ViewOf(box).Contains(pt)) continue;
                var p = box.PointToClient(pt);
                var cards = box.Controls.OfType<CardPanel>().Where(c => c.Visible).OrderBy(c => c.Top).ThenBy(c => c.Left).ToList();
                bool vertical = cards.Select(c => c.Left).Distinct().Count() <= 1;
                var t = new DropAt { Page = curPage, Box = box };
                foreach (var c in cards)
                {
                    bool before = vertical ? p.Y < c.Top + c.Height / 2 : (p.Y < c.Top || (p.Y < c.Bottom && p.X < c.Left + c.Width / 2));
                    if (before) { t.Before = c.ModuleKey; break; }
                }
                // встать туда же, где стоит — ничего не делать
                int me = cards.FindIndex(c => c.ModuleKey == key);
                if (me >= 0)
                {
                    if (t.Before == key) t.NoOp = true;
                    else if (t.Before == null && me == cards.Count - 1) t.NoOp = true;
                    else if (t.Before != null && me + 1 < cards.Count && cards[me + 1].ModuleKey == t.Before) t.NoOp = true;
                }
                return t;
            }
            return null;
        }

        NavButton hotNav;
        void ShowDrop(DropAt t)
        {
            dropAt = t;
            var nav = t != null && !t.NoOp ? t.Nav : null;
            if (hotNav != nav) { if (hotNav != null && !hotNav.IsDisposed) hotNav.Hot = false; hotNav = nav; if (nav != null) nav.Hot = true; }
            if (dropLine == null) return;
            if (t == null || t.Box == null || t.NoOp) { dropLine.Visible = false; return; }
            var box = t.Box;
            if (dropLine.Parent != box) { if (dropLine.Parent != null) dropLine.Parent.Controls.Remove(dropLine); box.Controls.Add(dropLine); }
            var cards = box.Controls.OfType<CardPanel>().Where(c => c.Visible).OrderBy(c => c.Top).ThenBy(c => c.Left).ToList();
            bool vertical = cards.Select(c => c.Left).Distinct().Count() <= 1;
            int th = Theme.S(4), gap = Theme.S(6);
            CardPanel at = t.Before != null ? cards.FirstOrDefault(c => c.ModuleKey == t.Before) : null;
            if (at != null) dropLine.Bounds = vertical ? new Rectangle(at.Left, Math.Max(0, at.Top - gap - th / 2), at.Width, th) : new Rectangle(Math.Max(0, at.Left - gap - th / 2), at.Top, th, at.Height);
            else if (cards.Count > 0) { var last = cards[cards.Count - 1]; dropLine.Bounds = vertical ? new Rectangle(last.Left, last.Bottom + gap - th / 2, last.Width, th) : new Rectangle(last.Right + gap - th / 2, last.Top, th, last.Height); }
            else dropLine.Bounds = new Rectangle(Theme.S(26), Theme.S(4), Math.Max(Theme.S(200), box.Width - Theme.S(52)), th);
            dropLine.Visible = true; dropLine.BringToFront();
        }

        void OnCardDragEnd(CardPanel c, Point pt, bool cancel)
        {
            if (c != dragCard) return;
            if (dragTimer != null) dragTimer.Stop();
            var t = cancel ? null : HitDrop(pt, c.ModuleKey);
            dragCard = null;
            if (ghost != null) { try { ghost.Close(); ghost.Dispose(); } catch { } ghost = null; }
            if (hotNav != null && !hotNav.IsDisposed) hotNav.Hot = false; hotNav = null;
            if (dropLine != null) { if (dropLine.Parent != null) dropLine.Parent.Controls.Remove(dropLine); dropLine.Dispose(); dropLine = null; }
            if (t == null || t.NoOp || st.UiLocked) return;
            string tab = t.Nav == null && t.Page == PgSettings ? setCurTab : null;
            DropModuleState(c.ModuleKey, t.Page, t.Before, tab);
            Logger.Write(LogLevel.Info, Tx("Карточка «") + (c.Title ?? c.ModuleKey) + Tx("» → ") + PageName(t.Page));
            ApplyModules(t.Page);
        }

        // положить модуль: страница, место (перед какой карточкой; null — в конец), вкладка Настроек (null — его обычная)
        void DropModuleState(string key, int page, string before, string settingsTab)
        {
            var m = Map(st.ModuleLoc, '=');
            int home; moduleHome.TryGetValue(key, out home);
            if (page == home) m.Remove(key); else m[key] = LocOfPage(page);
            st.ModuleLoc = Unmap(m, '=');
            var order = moduleKeys.ToList(); order.Remove(key);
            int bi = before != null ? order.IndexOf(before) : -1;
            if (bi >= 0) order.Insert(bi, key); else order.Add(key);
            st.ModuleOrder = string.Join(",", order);
            var tm = Map(st.ModuleTabs, '=');
            string def; moduleTab.TryGetValue(key, out def);
            if (page == PgSettings && settingsTab != null && settingsTab != def) tm[key] = settingsTab; else tm.Remove(key);
            st.ModuleTabs = Unmap(tm, '=');
            if (page == PgSettings) st.SetTab = settingsTab ?? ModTab(key);
            st.Save();
        }

        // ---- страница = внешняя панель: [прокрутка: содержимое страницы + полоса модулей под ним] + [колонка модулей справа] ----
        // Содержимое страницы НИКОГДА не сжимается: по ширине не уже ContentMin, по высоте — во весь экран.
        // Модули: справа, если колонка влезает; иначе — полосой ПОД страницей (страница прокручивается вниз к ним).
        sealed class PageFrame { public Panel Outer, Inner, Split, Strip; public ScrollHost Scroll, Dock; public FlatButton More; public int Reserve; public bool StripOn, DockOn; } // On — своё состояние (Visible = false, пока страница скрыта)
        // шапка страницы (заголовок, кнопки Route / DPI / VPN) — всегда во всю ширину окна; колонка модулей начинается под ней
        static int HeadH { get { return Theme.S(84); } }
        readonly Dictionary<Control, PageFrame> innerFrame = new Dictionary<Control, PageFrame>();
        // ширина для СОДЕРЖИМОГО страницы (без колонки модулей справа); шапка берёт полную ширину p.ClientSize.Width
        int PW(Control p) { PageFrame f; return innerFrame.TryGetValue(p, out f) ? Math.Max(10, p.ClientSize.Width - f.Reserve) : p.ClientSize.Width; }
        readonly Dictionary<int, PageFrame> frames = new Dictionary<int, PageFrame>();
        Panel WrapPage(Panel inner, int idx)
        {
            var f = new PageFrame { Inner = inner };
            f.Outer = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            f.Scroll = new ScrollHost { BackColor = Theme.Bg, Dock = DockStyle.Fill };
            f.Dock = new ScrollHost { BackColor = Theme.Bg, Width = Theme.S(380), Visible = false };
            f.Split = new Panel { Width = Theme.S(12), BackColor = Color.Transparent, Visible = false, Cursor = st.UiLocked ? Cursors.Default : Cursors.SizeWE };
            f.Strip = new BackdropPanel { BackColor = Theme.Bg, Visible = false };
            inner.Dock = DockStyle.None;
            f.Scroll.Content.Controls.Add(inner); f.Scroll.Content.Controls.Add(f.Strip);
            f.Outer.Controls.Add(f.Scroll); f.Outer.Controls.Add(f.Split); f.Outer.Controls.Add(f.Dock);
            f.Dock.BringToFront(); f.Split.BringToFront();
            innerFrame[inner] = f;
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
            bool show = f.StripOn && f.Scroll.Offset < Theme.S(40);
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
                int minW = Theme.S(300); foreach (var c in cards) minW = Math.Max(minW, c.BaseW + Theme.S(18));
                bool any = cards.Count > 0, right = any && W - minW - f.Split.Width >= ContentMin;
                f.Dock.Visible = right; f.Split.Visible = right; f.DockOn = right;
                Control box = right ? (Control)f.Dock.Content : f.Strip;
                foreach (var c in cards) if (c.Parent != box) { c.Parent.Controls.Remove(c); box.Controls.Add(c); }
                if (right)
                {
                    int dw = Math.Max(minW, Math.Min(W - ContentMin - f.Split.Width, Math.Max(minW, Theme.S(st.DockW))));
                    f.Dock.SetBounds(W - dw, HeadH, dw, Math.Max(10, H - HeadH));
                    f.Split.SetBounds(W - dw - f.Split.Width, HeadH, f.Split.Width, Math.Max(10, H - HeadH));
                    f.Dock.BringToFront(); f.Split.BringToFront();
                    int y = 0;
                    foreach (var c in cards) { y += c.Height + Theme.S(12); }
                    // сначала высота → появится ли полоса прокрутки, потом ширина карточек по реальной ширине колонки
                    f.Dock.Content.Height = y + Theme.S(10); f.Dock.MinContentWidth = 0; f.Dock.Relayout();
                    y = 0;
                    foreach (var c in cards) { c.SetBounds(0, y, Math.Max(c.BaseW, f.Dock.Content.Width - Theme.S(2)), c.Height); y = c.Bottom + Theme.S(12); }
                    int dneed = cards.Max(c => c.Width) + Theme.S(2);
                    if (dneed > f.Dock.Content.Width) { f.Dock.MinContentWidth = dneed; f.Dock.Relayout(); }
                }
                int viewW = right ? W - f.Dock.Width - f.Split.Width : W;
                // содержимое страницы — во весь видимый экран; полоса модулей (если есть) — ниже, к ней прокручивают
                int stripH = 0;
                f.Strip.Visible = f.StripOn = any && !right;
                if (f.StripOn)
                {
                    int L = Theme.S(26), gap = Theme.S(12), cw = Math.Max(10, viewW - L * 2 - Theme.S(14)), x = 0, y = Theme.S(4), rowH = 0;
                    foreach (var c in cards)
                    {
                        if (c.Width != c.BaseW) c.Width = c.BaseW;
                        if (x > 0 && x + c.Width > cw) { x = 0; y += rowH + gap; rowH = 0; }
                        c.Location = new Point(L + x, y); x += c.Width + gap; rowH = Math.Max(rowH, c.Height);
                    }
                    stripH = y + rowH + Theme.S(16);
                }
                f.Scroll.Content.Height = H + stripH;
                f.Scroll.MinContentWidth = 0; f.Scroll.Relayout();
                int cwid = f.Scroll.Content.Width;
                // узкое окно: страница сама ужимается (вкладки, колонки), горизонтальная прокрутка — только в самом крайнем случае
                int pageMin = Theme.S(600);
                if (cwid < pageMin) { cwid = pageMin; f.Scroll.MinContentWidth = pageMin; f.Scroll.Relayout(); }
                // карточка шире окна (узкое окно / широкая левая панель) — полоса шире, снизу горизонтальная прокрутка, ничего не обрезается
                if (f.StripOn)
                {
                    int need = Theme.S(26) * 2 + cards.Max(c => c.Width) + Theme.S(4);
                    if (need > Math.Max(cwid, f.Scroll.MinContentWidth)) { f.Scroll.MinContentWidth = need; f.Scroll.Relayout(); }
                }
                // справа колонка модулей: страница во всю ширину (шапка до правого края), содержимое — левее колонки (PW)
                int reserve = right ? f.Dock.Width + f.Split.Width : 0;
                if (right) cwid = W;
                bool resChanged = reserve != f.Reserve; f.Reserve = reserve;
                bool sameSize = f.Inner.Width == cwid && f.Inner.Height == H;
                f.Inner.SetBounds(0, 0, cwid, H);
                if (resChanged && sameSize) { f.Inner.Width = cwid + 1; f.Inner.Width = cwid; } // размер тот же — всё равно переложить страницу под новую ширину содержимого
                if (f.StripOn) f.Strip.SetBounds(0, H, Math.Max(cwid, f.Scroll.MinContentWidth), stripH);
                else f.Scroll.ScrollToTop();
                f.More.Text = Tx("Модули ниже") + " (" + cards.Count + ")"; f.More.AutoW();
                PlaceMore(f);
            }
            finally { layingFrame = false; }
        }

        // все модули — по своим местам (вызывается в конце сборки интерфейса)
        void PlaceAllModules()
        {
            // порядок карточек — как их расставил пользователь (перетаскиванием внутри страницы)
            var ord = (st.ModuleOrder ?? "").Split(',').Where(x => x != "").ToList();
            if (ord.Count > 0)
            {
                var orig = moduleKeys.ToList();
                Func<string, int> rank = k => { int i = ord.IndexOf(k); return i >= 0 ? i : ord.Count + orig.IndexOf(k); };
                moduleKeys.Sort((a, b) => rank(a).CompareTo(rank(b)));
            }
            foreach (var key in moduleKeys.ToList())
            {
                CardPanel c; if (!modules.TryGetValue(key, out c) || c.IsDisposed) { moduleKeys.Remove(key); continue; }
                int page = PageOf(key);
                Control target;
                // на Subscriptions все карточки — в её правую колонку (под автосвитч и т. п.), порядок — перетаскиванием
                if (page == PgServers && srvModBody != null) target = srvModBody;
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
                    foreach (var c in mine) if (c.Width != c.BaseW) c.Width = c.BaseW;
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
