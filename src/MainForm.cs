using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm : Form
    {
        public const string Version = "3.3.0";
        Settings st;
        readonly ChannelMonitor monVpn = new ChannelMonitor("vpn"), monDir = new ChannelMonitor("direct");
        volatile Prober pVpn, pDir;
        List<AdapterInfo> adapters = new List<AdapterInfo>();
        AdapterInfo direct, defRoute;
        string curlPath = "curl.exe";
        readonly Router router = new Router();

        NotifyIcon tray; ContextMenuStrip trayMenu; ToolStripMenuItem miWidget, miMonitor, miConnect; ToolStripMenuItem[] miModes;
        WidgetForm widget, widget2; // widget2 — отдельный виджет скорости (режим «два виджета»)
        IntPtr trayIconHandle = IntPtr.Zero;
        bool exiting, hiddenToTray, trayHintShown, startHidden;
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
        readonly System.Windows.Forms.Timer appUpdTimer = new System.Windows.Forms.Timer { Interval = 20000 }; // проверка релизов NetProbe: через 20 с после старта, дальше раз в час
        readonly System.Windows.Forms.Timer reapplyTimer = new System.Windows.Forms.Timer { Interval = 700 };

        class ChState { public int FailRun; public DateTime FirstFail; public bool? Ok; public string Ip = ""; public DateTime DownSince; public DateTime LastNotify; public Level Lv = Level.Off; public string Val = "—"; }
        bool sameRoute;
        readonly Dictionary<string, ChState> chs = new Dictionary<string, ChState> { { "vpn", new ChState() }, { "direct", new ChState() } };

        Panel side, content;
        readonly List<NavButton> navs = new List<NavButton>();
        readonly List<Control> pages = new List<Control>();
        int curPage;
        bool monitoringWanted;

        public const int PgConnect = 0, PgServers = 1, PgMonitor = 2, PgConfigs = 3, PgDpi = 4, PgTests = 5, PgSites = 6, PgLog = 7, PgSettings = 8, PgTg = 9, PgFirstCustom = 10, PgTasks = 99;
        // порядок пунктов меню слева (индексы страниц не меняются — сохранённые места карточек и названия пунктов остаются верными)
        IEnumerable<int> NavOrder() { yield return PgConnect; yield return PgServers; yield return PgTg; for (int i = PgMonitor; i <= PgSettings; i++) yield return i; for (int i = PgFirstCustom; i < navs.Count; i++) yield return i; }

        public MainForm(bool startMinimized)
        {
            st = Settings.Load();
            Theme.AccentColor = Theme.ParseColor(st.Accent);
            Tr.Set(st.Lang);
            AppLists.ZapretLists = Path.Combine(st.ZapretPath, "lists");
            PushFonts();
            Theme.Apply((ThemeKind)st.Theme);
            startHidden = startMinimized || st.StartMinimized;
            Logger.ToFile = st.LogToFile; Logger.Dir = st.LogPath; Logger.Retention = st.Retention;
            Text = "NetProbe";
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Theme.S(1220), Theme.S(800));
            MinimumSize = new Size(Theme.S(1040), Theme.S(680));
            StartPosition = FormStartPosition.CenterScreen;
            RestoreWindowBounds();
            curPage = st.LastPage >= 0 ? st.LastPage : 0; // свои вкладки — после «Настроек»; лишний номер поправит BuildUi
            ApplyAppIcon();
            DoubleBuffered = true;

            CurlProber.EnsureFormatFile();
            Logger.Line += (t, lv, s) => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => AppendLog(t, lv, s))); } catch { } };
            FirstRunSetup();

            router.GetSettings = () => st;
            router.DpiForcedOff = st.DpiButton == 2;
            router.AskStopService = q => (bool)Invoke((Func<bool>)(() => { RestoreFromTray(); return MessageBox.Show(this, q, "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }));
            router.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)OnRouteChanged); } catch { } };
            // несколько правок подряд (добавить + убрать из соседнего списка) = один перезапуск туннеля
            reapplyTimer.Tick += (s, e) => { reapplyTimer.Stop(); if (router.Active) router.Reapply(); };
            // списки сайтов DPI не трогают туннель (и наоборот): DPI — обновить исключения winws; приложения — перезапуск туннеля с новыми правилами
            AppLists.Changed += key => { if (IsHandleCreated) try { BeginInvoke((Action)(() =>
            {
                if (AppLists.IsServers(key)) return;
                if (key == AppLists.DpiDomains || key == AppLists.DpiOnlyExcl) { router.RefreshDpiExclusions(); FillDpiSites(); return; }
                if (key == AppLists.DpiApps) return; // 3.0: dpi-apps больше не читается
                reapplyTimer.Stop(); reapplyTimer.Start(); RefreshListsUi();
            })); } catch { } };

            InitXray();
            BuildUi();
            BuildTray();
            HookChars();
            if (ShotsDir == null) { Toast.Init(appIconBmp); Toast.Activated += OnToast; }
            appNotifyTimer.Tick += (s2, e2) => { appNotifyTimer.Stop(); ScheduleAppNotify(); };

            monVpn.GetSettings = monDir.GetSettings = () => st;
            monVpn.GetProber = () => pVpn;
            monDir.GetProber = () => pDir;
            monVpn.Sample += OnSampleBg; monDir.Sample += OnSampleBg;

            RefreshAdapters(true);
            refreshTimer.Tick += (s, e) => RefreshAdapters(false);
            refreshTimer.Start();
            uiTimer.Tick += (s, e) => OnUiTick();
            uiTimer.Start();
            SpeedMeter.AdapterId = () => { var d = direct; return d != null ? d.Id : null; };
            SpeedMeter.Sample += (d, u) => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => OnSpeed(d, u))); } catch { } };
            if (st.SpeedOn) SpeedMeter.Start();
            NetworkChange.NetworkAddressChanged += (s, e) => { if (IsHandleCreated) try { BeginInvoke((Action)(() => RefreshAdapters(false))); } catch { } };
        }

        // Первый запуск: переносим стратегии из zapret и туннели/правила из TunnlTo
        void FirstRunSetup()
        {
            try
            {
                if (DpiConfigs.Names().Count == 0 && Directory.Exists(st.ZapretPath))
                {
                    int n = DpiConfigs.ImportAll(st.ZapretPath, false);
                    if (n > 0) Logger.Write(LogLevel.Info, Tx("Импортировано стратегий DPI из zapret: ") + n);
                }

            }
            catch (Exception ex) { Logger.Write(LogLevel.Warn, Tx("Первичная настройка: ") + ex.Message); }
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); DwmTheme.Apply(Handle); FileDrop.Enable(Handle); }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; startHidden = false; BeginInvoke((Action)(() => { Startup(); HideToTray(true); })); }
            base.SetVisibleCore(value);
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Startup(); }

        // Отладка: --shots <папка> — снимки всех страниц во всех темах, затем выход
        public static string ShotsDir;
        // сторож теста: если UI-поток 2 минуты не продвигается — пишем, где завис (стек), и выходим, чтобы run.cmd не висел
        static volatile string shotStage = "start"; static int shotTick = Environment.TickCount;
        static void ShotStep(string stage) { shotStage = stage; shotTick = Environment.TickCount; }
        static void ShotCrash(Exception ex)
        {
            try { File.AppendAllText(Path.Combine(ShotsDir, "layout.txt"), "\r\nCRASH at: " + shotStage + "\r\n" + ex + "\r\n"); } catch { }
            Environment.Exit(4);
        }
        static void StartShotWatchdog(string dir)
        {
            var main = System.Threading.Thread.CurrentThread;
            var th = new System.Threading.Thread(() =>
            {
                while (true)
                {
                    System.Threading.Thread.Sleep(5000);
                    if (unchecked(Environment.TickCount - shotTick) < 120000) continue;
                    string stack = "";
#pragma warning disable 618
                    try { main.Suspend(); try { stack = new System.Diagnostics.StackTrace(main, false).ToString(); } finally { main.Resume(); } } catch (Exception ex) { stack = "stack n/a: " + ex.Message; }
#pragma warning restore 618
                    try { File.WriteAllText(Path.Combine(dir, "layout.txt"), "HANG at: " + shotStage + "\r\n" + stack); } catch { }
                    Environment.Exit(3);
                }
            }) { IsBackground = true };
            th.Start();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (ShotsDir == null) return;
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += (s, ev) =>
            {
                t.Stop();
                Directory.CreateDirectory(ShotsDir);
                ShotStep("begin"); StartShotWatchdog(ShotsDir);
                Application.ThreadException += (s2, e2) => ShotCrash(e2.Exception); // ошибка в тесте — сразу в отчёт и выход, а не «висит»
                int keep = st.Theme;
                Action<string> shot = name => { ShotStep("shot " + name); Application.DoEvents(); System.Threading.Thread.Sleep(150); Application.DoEvents(); using (var bmp = new Bitmap(Width, Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); bmp.Save(Path.Combine(ShotsDir, name + ".png")); } };
                foreach (ThemeKind k in new[] { ThemeKind.Dark, ThemeKind.Light, ThemeKind.Classic, ThemeKind.Chisa, ThemeKind.Kurumi })
                {
                    Theme.Apply(k); st.Theme = (int)k; RebuildUi();
                    string pre = Tr.Code + "-" + k + "-";
                    for (int i = 0; i < pages.Count; i++) { SelectPage(i); shot(pre + i); }
                    SelectPage(PgDpi); ShowDpiTab(2); shot(pre + "dpi-sites"); ShowDpiTab(0);
                    widget.SetParts(true, true);
                    using (var bmp = new Bitmap(widget.Width, widget.Height)) { widget.DrawToBitmap(bmp, new Rectangle(0, 0, widget.Width, widget.Height)); bmp.Save(Path.Combine(ShotsDir, pre + "widget.png")); }
                    widget2.SetParts(false, true);
                    using (var bmp = new Bitmap(widget2.Width, widget2.Height)) { widget2.DrawToBitmap(bmp, new Rectangle(0, 0, widget2.Width, widget2.Height)); bmp.Save(Path.Combine(ShotsDir, pre + "widget2.png")); }
                }
                // минимальный размер окна: всё должно помещаться
                var keepSize = Size; Theme.Apply(ThemeKind.Dark); st.Theme = 0; RebuildUi();
                Size = MinimumSize;
                foreach (int pg in new[] { PgConnect, PgServers, PgMonitor, PgSettings }) { SelectPage(pg); shot(Tr.Code + "-min-" + pg); }
                Size = keepSize;
                // своя картинка фона: нигде не должно быть чёрных прямоугольников
                string bgTest = Path.Combine(ShotsDir, "_bg.png"), keepBg = st.BgImage;
                using (var bb = new Bitmap(800, 500)) using (var gg = Graphics.FromImage(bb)) using (var lg = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 800, 500), Color.FromArgb(200, 60, 120), Color.FromArgb(40, 140, 220), 35f)) { gg.FillRectangle(lg, 0, 0, 800, 500); bb.Save(bgTest); }
                st.BgImage = bgTest; ResetPageBackground(); RebuildUi();
                for (int i = 0; i < pages.Count; i++) { SelectPage(i); shot(Tr.Code + "-bg-" + i); }
                // вкладки настроек на своём фоне в теме Kurumi — снимок с экрана (DrawToBitmap не показывает ошибки перерисовки)
                Theme.Apply(ThemeKind.Kurumi); st.Theme = (int)ThemeKind.Kurumi; ResetPageBackground(); RebuildUi(); SelectPage(PgSettings);
                TopMost = true; Activate();
                foreach (var tk in new[] { "win", "look", "vpn", "subs", "mon", "upd", "log", "char", "upd", "win" })
                {
                    if (setTabs == null || !setTabs.Tabs.Any(t => t.Key == tk)) continue;
                    setTabs.Select(tk);
                    for (int w = 0; w < 6; w++) { Application.DoEvents(); System.Threading.Thread.Sleep(80); }
                    // PrintWindow(PW_RENDERFULLCONTENT): настоящая отрисовка окна, даже если его закрывает консоль
                    try { using (var bmp = new Bitmap(Width, Height)) { using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { PrintWindow(Handle, hdc, 2); } finally { g.ReleaseHdc(hdc); } } bmp.Save(Path.Combine(ShotsDir, Tr.Code + "-bgset-" + tk + ".png")); } } catch { }
                }
                TopMost = false;
                st.BgImage = keepBg; ResetPageBackground();
                // ===== матрица модулей: каждый модуль на каждой странице + все модули на одной странице (обычное и минимальное окно) =====
                // проверки: модуль на нужной странице и виден, не вылезает за край, карточки не налезают друг на друга,
                // место сохраняется в settings.ini и читается обратно (после перезапуска модуль окажется там же) → layout.txt
                var keepLoc = st.ModuleLoc; var keepPages = st.CustomPages;
                st.CustomPages = "c1:Test tab";
                Theme.Apply(ThemeKind.Dark); st.Theme = 0; st.ModuleLoc = ""; RebuildUi();
                var report = new List<string>();
                var keys = moduleKeys.ToList(); int pc = PageCount, runs = 0;
                Action<string, string, int> verify = (tag, key, pg) =>
                {
                    ShotStep("verify " + tag + " " + key);
                    CardPanel mc;
                    if (!modules.TryGetValue(key, out mc) || mc.IsDisposed) { report.Add(tag + ": " + key + " lost"); return; }
                    int on = -1;
                    for (int q = 0; q < pages.Count; q++) { Panel o; var root = pageOuter.TryGetValue(q, out o) ? o : pages[q]; for (var x = mc.Parent; x != null; x = x.Parent) if (x == root) on = q; }
                    if (on != pg) { report.Add(tag + ": " + key + " on page " + on + ", expected " + pg); return; }
                    SelectPage(pg); if (pg == PgSettings && setTabs != null) setTabs.Select(ModTab(key));
                    Application.DoEvents();
                    if (!mc.Visible) { report.Add(tag + ": " + key + " not visible on page " + pg); return; }
                    Panel outer; if (!pageOuter.TryGetValue(pg, out outer)) return;
                    var r = outer.RectangleToClient(mc.RectangleToScreen(mc.ClientRectangle));
                    bool hscroll = false; for (var x = mc.Parent; x != null; x = x.Parent) { var sh = x as ScrollHost; if (sh != null && sh.MinContentWidth > sh.Width - Theme.S(14)) hscroll = true; }
                    if (!hscroll && (r.Left < -2 || r.Right > outer.ClientSize.Width + 2)) report.Add(tag + ": " + key + " sticks out " + r + " / page w=" + outer.ClientSize.Width);
                    if (mc.Width < Theme.S(100) || mc.Height < Theme.S(40)) report.Add(tag + ": " + key + " too small " + mc.Size);
                    // до модуля можно добраться: на каждом уровне он внутри родителя (в прокрутке — внутри её содержимого)
                    for (Control x = mc; x != null && x.Parent != null && x != outer; x = x.Parent)
                    {
                        var P = x.Parent; if (P is ScrollHost) continue; // содержимое прокрутки выше окна — это нормально, его прокручивают
                        if (x.Bottom > P.Height + 2 || x.Right > P.Width + 2 || x.Left < -2 || x.Top < -2) { report.Add(tag + ": " + key + " unreachable: " + Name2(x) + " " + x.Bounds + " in " + Name2(P) + " " + P.Size); break; }
                    }
                    // содержимое самой страницы не сжато модулями: во всю высоту окна и не уже ContentMin
                    PageFrame fr;
                    if (frames.TryGetValue(pg, out fr))
                    {
                        if (Math.Abs(fr.Inner.Height - outer.ClientSize.Height) > 2) report.Add(tag + ": page content height " + fr.Inner.Height + " ≠ window " + outer.ClientSize.Height);
                        int effW = fr.Inner.Width - fr.Reserve;
                        if (fr.DockOn && Math.Abs(fr.Dock.Height - (outer.ClientSize.Height - HeadH)) > 2) report.Add(tag + ": module column height " + fr.Dock.Height + " ≠ " + (outer.ClientSize.Height - HeadH));
                        if (fr.DockOn && fr.Reserve != fr.Dock.Width + fr.Split.Width) report.Add(tag + ": content does not reserve the module column (" + fr.Reserve + ")");
                        if (fr.StripOn && fr.Scroll.Offset < Theme.S(40) && !fr.More.Visible) report.Add(tag + ": 'Modules below' button not shown");
                        if (effW < (fr.Dock.Visible ? ContentMin : Math.Min(ContentMin, outer.ClientSize.Width - Theme.S(14))) - Theme.S(16)) report.Add(tag + ": page content squeezed to w=" + effW);
                        // содержимое страницы не уходит под колонку модулей (шапка — может, она над колонкой)
                        if (fr.Reserve > 0)
                            foreach (Control x in fr.Inner.Controls)
                                if (x.Visible && x.Top >= HeadH - Theme.S(4) && x.Right > effW + 2) report.Add(tag + ": " + Name2(x) + " under modules column " + x.Bounds + " / content w=" + effW);
                    }
                    // модуль на чужой странице должен работать: списки заполнены текущими значениями
                    Action<Control> combos = null;
                    combos = cc => { foreach (Control x in cc.Controls) { var dc = x as DarkCombo; if (dc != null && dc.Items.Count > 0 && dc.SelectedIndex < 0) report.Add(tag + ": " + key + " empty drop-down at " + dc.Location); combos(x); } };
                    combos(mc);
                };
                Action<string> persist = tag =>
                {
                    st.Save(); var re = Settings.Load();
                    if ((re.ModuleLoc ?? "") != (st.ModuleLoc ?? "")) report.Add(tag + ": ModuleLoc not saved: '" + re.ModuleLoc + "' vs '" + st.ModuleLoc + "'");
                };
                // 1) каждый модуль по одному на каждой странице
                foreach (var key in keys)
                    for (int pg = 0; pg < pc; pg++)
                    {
                        ShotStep("rebuild one " + key + " p" + pg); st.ModuleLoc = key + "=" + LocOfPage(pg); RebuildUi(); runs++;
                        string tag = "one " + key + "→p" + pg;
                        verify(tag, key, pg);
                        foreach (var line in CheckLayout(pageOuter.ContainsKey(pg) ? (Control)pageOuter[pg] : pages[pg])) report.Add(tag + ": " + line);
                        if (pg == 0) persist(tag);
                    }
                // 2) все модули сразу на одной странице — обычное и минимальное окно
                var scr = Screen.FromControl(this).WorkingArea;
                foreach (var szName in new[] { "norm", "min", "max", "minwide" })
                {
                    // minwide: минимальное окно и самая широкая левая панель — самое узкое место для карточек
                    st.SideWidth = szName == "minwide" ? 380 : 214;
                    ShotStep("size " + szName);
                    Size = szName == "min" || szName == "minwide" ? MinimumSize : szName == "max" ? new Size(Math.Max(keepSize.Width, scr.Width), Math.Max(keepSize.Height, scr.Height)) : keepSize;
                    for (int pg = 0; pg < pc; pg++)
                    {
                        ShotStep("rebuild all p" + pg + " " + szName); st.ModuleLoc = string.Join(";", keys.Select(k => k + "=" + LocOfPage(pg))); RebuildUi(); runs++;
                        string tag = "all→p" + pg + " " + szName;
                        foreach (var key in keys) verify(tag, key, pg);
                        foreach (var line in CheckLayout(pageOuter.ContainsKey(pg) ? (Control)pageOuter[pg] : pages[pg])) report.Add(tag + ": " + line);
                        persist(tag);
                        if (szName == "norm") { SelectPage(pg); shot(Tr.Code + "-mod-all-p" + pg); }
                    }
                }
                Size = keepSize; st.SideWidth = 214;
                // сброс раскладки возвращает всё домой и снимает Lock
                st.ModuleLoc = string.Join(";", keys.Select(k => k + "=" + LocOfPage(pc - 1))); st.UiLocked = true; RebuildUi();
                ResetLayoutState(); RebuildUi(); runs++; // ResetLayout перестраивает через BeginInvoke — в тесте синхронно, иначе перестройка посреди проверки
                if (st.ModuleLoc != "" || st.ModuleOrder != "" || !st.UiLocked || !CardPanel.Locked) report.Add("reset: layout not reset");
                foreach (var key in keys) verify("reset", key, moduleHome[key]);
                // окно свернули и развернули (и запуск в трей): колонка модулей Routing по настоящему размеру
                ShotStep("minimize restore");
                st.ModuleLoc = "srv.tun=p0"; RebuildUi(); SelectPage(0); runs++;
                WindowState = FormWindowState.Minimized; Application.DoEvents(); System.Threading.Thread.Sleep(200);
                WindowState = FormWindowState.Normal; Application.DoEvents(); System.Threading.Thread.Sleep(200); Application.DoEvents();
                verify("restore", "srv.tun", 0);
                st.ModuleLoc = "srv.tun=p0"; Hide(); RebuildUi(); Show(); Application.DoEvents(); SelectPage(0); Application.DoEvents();
                verify("hidden rebuild", "srv.tun", 0);
                shot(Tr.Code + "-restore-p0");
                st.ModuleLoc = "";
                // перетаскивание внутри страницы: порядок и вкладка Настроек сохраняются и восстанавливаются
                ShotStep("drop order");
                DropModuleState("set.subs", PgServers, "srv.as", null); RebuildUi(); runs++;
                {
                    CardPanel a, b2; modules.TryGetValue("set.subs", out a); modules.TryGetValue("srv.as", out b2);
                    if (a == null || b2 == null || a.Parent != srvModBody || b2.Parent != srvModBody) report.Add("drop: set.subs not in Subscriptions column");
                    else if (a.Top >= b2.Top) report.Add("drop: set.subs not above srv.as (" + a.Top + " / " + b2.Top + ")");
                    persist("drop order");
                    SelectPage(PgServers); shot(Tr.Code + "-drop-srv");
                    st.ModuleLoc = "set.subs=p0;set.look=p0"; RebuildUi(); SelectPage(0); shot(Tr.Code + "-dock-p0");
                }
                DropModuleState("srv.tun", PgSettings, null, "log"); RebuildUi(); runs++;
                if (ModTab("srv.tun") != "log") report.Add("drop: srv.tun tab " + ModTab("srv.tun") + ", expected log");
                verify("drop tab", "srv.tun", PgSettings);
                // заголовок страницы = переименованный пункт меню
                st.NavNames = "1:Xyz"; RebuildUi(); runs++;
                if (!pages[1].Controls.OfType<Label>().Any(l => l.Text == "Xyz")) report.Add("rename: page title not renamed");
                st.NavNames = "";
                ResetLayoutState(); RebuildUi(); runs++;
                // 3) экспорт / импорт настроек переносит раскладку (места модулей, свои вкладки, названия меню, ширины, Lock)
                try
                {
                    st.ModuleLoc = "srv.tun=p7;set.look=c1"; st.NavNames = "7:Logs X"; st.UiLocked = true; st.DockW = 455; st.Save();
                    string json = Backup.Export();
                    st.ModuleLoc = ""; st.NavNames = ""; st.UiLocked = false; st.DockW = 380; st.Save();
                    Backup.Import(json);
                    var re = Settings.Load();
                    if (re.ModuleLoc != "srv.tun=p7;set.look=c1" || re.NavNames != "7:Logs X" || !re.UiLocked || re.DockW != 455 || re.CustomPages != "c1:Test tab")
                        report.Add("export/import lost layout: ModuleLoc='" + re.ModuleLoc + "' NavNames='" + re.NavNames + "' Lock=" + re.UiLocked + " DockW=" + re.DockW + " Pages='" + re.CustomPages + "'");
                    st.CopyFrom(re); RebuildUi();
                    if (PageOf("srv.tun") != 7 || PageOf("set.look") != PgFirstCustom) report.Add("after import: modules not where exported (srv.tun p" + PageOf("srv.tun") + ", set.look p" + PageOf("set.look") + ")");
                    if (navs.Count > 7 && navs[7].Text != "Logs X") report.Add("after import: menu name not applied: " + navs[7].Text);
                    st.UiLocked = false; CardPanel.Locked = false;
                }
                catch (Exception ex) { report.Add("export/import test failed: " + ex.Message); }
                st.ModuleLoc = keepLoc; st.CustomPages = keepPages; st.NavNames = ""; st.Save(); RebuildUi();
                try { File.WriteAllLines(Path.Combine(ShotsDir, "layout.txt"), new[] { "modules: " + keys.Count + " (" + string.Join(", ", keys) + ") · pages: " + pc + " · rebuilds: " + runs, report.Count == 0 ? "LAYOUT OK" : "PROBLEMS: " + report.Count }.Concat(report.Distinct().Take(400)).ToArray()); } catch { }
                st.Theme = keep; ExitApp();
            };
            t.Start();
        }

        bool started;
        void Startup()
        {
            if (started) return;
            started = true;
            Logger.Write(LogLevel.Info, "NetProbe " + Version + Tx(" запущен. Папка: ") + Settings.Root + Tx(" · лог: ") + st.LogPath);
            ThreadPool.QueueUserWorkItem(_ => { CheckCurl(); Logger.Prune(); });
            if (st.AutoStartMonitor) StartMonitoring(false); // мониторинг — как был при выходе (Start/Stop запоминаются)
            else SyncBgProbe(); // выключен — опросы только для лога (если включено)
            if (autorunApplied == null) { autorunApplied = st.Autorun; if (st.Autorun) ThreadPool.QueueUserWorkItem(_ => SetAutorun(true)); }
            // 3.0: домены из карточек приложений → общий список DPI (один раз, с одним диалогом)
            if (!st.DomainsMigrated)
            {
                List<string> moved = null;
                try { moved = AppLists.MigrateAppDomains(); } catch (Exception ex) { Logger.Write(LogLevel.Error, Tx("Перенос доменов: ") + ex.Message); }
                st.DomainsMigrated = true; st.Save();
                if (moved != null && moved.Count > 0)
                {
                    Logger.Write(LogLevel.Info, Tx("Домены из карточек приложений перенесены в общий список DPI: ") + string.Join(", ", moved));
                    BeginInvoke((Action)(() => Notice.Show(this, Tx("Перенос доменов"), Tx("Домены из карточек приложений переехали в общие списки DPI (вкладка Конфиги → DPI → Сайты): ") + string.Join(", ", moved.Take(12)) + (moved.Count > 12 ? " …" : ""))));
                }
            }
            var handoff = Updated ? SelfUpdate.TakeHandoff() : null;
            if (Updated && Settings.MigratedFrom2 && st.UpdatedFrom == "") { st.UpdatedFrom = "2.x"; st.Save(); LayoutNav(); } // обновились со старой версии, которая не умеет ставить флаг
            if (handoff != null)
            {
                StartProbe();
                router.Adopt(handoff);
                xm.Adopt(handoff.ContainsKey("xray") && handoff["xray"] == "1");
                if (Reapply3) { router.Reapply(); xm.Reapply(); Logger.Write(LogLevel.Route, Tx("Перезапуск: конфиги перестроены по новой логике")); }
                else if (NeedsManualRestart) BeginInvoke((Action)ShowRestartNotice);
            }
            else if (st.DpiButton == 1) router.SetManualDpi(true); // DPI был включён кнопкой до перезапуска
            // автоподключение: без диалогов, если туннель ещё не настроен (например, чистая установка)
            // один источник: подписки поднимаются при старте только если они — активный источник (раньше xray мог
            // подключиться параллельно с туннелем и «не выключался» кнопкой VPN маршрутизатора)
            if (handoff == null)
            {
                // 3.2: «подключаться к последнему серверу при запуске» — к тому серверу, к которому был подключён последним (ActiveServer)
                var last = st.XrayAutoConnect && File.Exists(XrayProcess.Exe(st)) ? ServerStore.Find(st.ActiveServer) : null;
                if (last != null && st.HybridMode && TunnelCount > 0 && st.Source != 1) { st.Source = 1; st.Save(); } // в гибриде — переключить источник на подписки
                var src0 = ActiveSource();
                if (last != null && src0 == Source.Subs)
                {
                    Logger.Write(LogLevel.Info, Tx("Автоподключение к последнему серверу: ") + last.Name);
                    StartMonitoringIfProxy(); XConnect(last.Id, true);
                }
                else if (st.AutoConnect && (st.RunMode == RouteMode.DpiOnly || src0 == Source.Vpn)) ConnectCurrent(false);
                else if (st.AutoConnect && src0 == Source.Subs) ConnectCurrent(false);
                else if (st.AutoConnect) Logger.Write(LogLevel.Info, Tx("Автоподключение пропущено: основной VPN-туннель не выбран"));
                if (st.XrayAutoConnect && last == null) Logger.Write(LogLevel.Info, Tx("Автоподключение к серверу: последний сервер не найден — подключись к нему один раз вручную"));
                else if (last != null && src0 != Source.Subs) Logger.Write(LogLevel.Warn, Tx("Автоподключение к серверу пропущено: ") + (SubsBlockedReason() ?? ""));
            }
            TgApply(); // TG Proxy: включён — поднимается вместе с NetProbe
            ScheduledUpdateCheck(true);
            updTimer.Tick += (s, e) => ScheduledUpdateCheck(false); updTimer.Start();
            appUpdTimer.Tick += (s, e) => { appUpdTimer.Stop(); if (st.CheckUpdates) CheckAppUpdate(false); else appUpdTimer.Start(); }; appUpdTimer.Start();
        }

        // ================= Shell / navigation =================
        void BuildUi()
        {
            SuspendLayout();
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            navs.Clear(); pages.Clear(); modules.Clear(); moduleKeys.Clear(); moduleHome.Clear(); moduleTab.Clear();
            docks.Clear(); dockSplits.Clear(); pageOuter.Clear(); frames.Clear(); srvModBody = null; tgBody = null; relayTg = null; innerFrame.Clear();
            CardPanel.Locked = st.UiLocked;
            BuildShell();
            BuildConnectPage();
            BuildServersPage();
            BuildMonitorPage();
            BuildConfigsPage();
            BuildDpiConfigsPage();
            BuildTestsPage();
            BuildSitesPage();
            BuildLogPage();
            BuildSettingsPage();
            BuildTgPage();
            BuildCustomPages();
            PlaceAllModules();
            if (curPage >= pages.Count && curPage != PgTasks) curPage = 0;
            // кнопки «Маршрут · DPI · VPN» сверху на всех вкладках, кроме Настроек (Routing и Subscriptions ставят их сами)
            foreach (int pi in new[] { PgMonitor, PgConfigs, PgDpi, PgTests, PgSites, PgLog, PgTg })
            {
                var pg = pages[pi]; var tb = NewTopButtons(pg, null);
                Action place = () => PlaceTopButtons(tb, pg.ClientSize.Width - Theme.S(26), Theme.S(22));
                pg.Resize += (s2, e2) => place(); place();
            }
            UpdateModeUi();
            SelectPage(curPage);
            ApplyUpdateBadges();
            ResumeLayout(true);
            OnRouteChanged();
        }

        // Смена темы «на лету»: пересобираем интерфейс, данные (мониторы, лог, график) сохраняются
        void RebuildUi()
        {
            Native.SendMessage(Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW off: без мигания и «квадрата» в углу
            try { RebuildCore(); }
            finally { Native.SendMessage(Handle, 0x000B, (IntPtr)1, IntPtr.Zero); RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100); }
        }

        // проверка раскладки страницы: видимые карточки и элементы внутри карточек не налезают друг на друга и не вылезают за край
        List<string> CheckLayout(Control page)
        {
            var bad = new List<string>();
            Action<Control> walk = null;
            walk = parent =>
            {
                var kids = parent.Controls.Cast<Control>().Where(c => c.Visible && c.Width > 0 && c.Height > 0).ToList();
                bool cardLevel = parent is CardPanel || kids.Any(c => c is CardPanel);
                if (cardLevel)
                {
                    var check = parent is CardPanel ? kids : kids.Where(c => c is CardPanel).ToList();
                    for (int i = 0; i < check.Count; i++)
                    {
                        var a2 = check[i].Bounds;
                        if (parent is CardPanel && (a2.Right > parent.ClientSize.Width + 1 || a2.Left < -1)) bad.Add("outside " + Name2(check[i]) + " in " + Name2(parent) + " " + a2 + " / w=" + parent.ClientSize.Width);
                        for (int j = i + 1; j < check.Count; j++)
                        {
                            var r = Rectangle.Intersect(a2, check[j].Bounds);
                            if (r.Width > 2 && r.Height > 2) bad.Add("overlap " + Name2(check[i]) + " × " + Name2(check[j]) + " in " + Name2(parent));
                        }
                    }
                }
                foreach (var c in kids) walk(c);
            };
            walk(page);
            return bad;
        }
        static string Name2(Control c) { var cp = c as CardPanel; return c.GetType().Name + (cp != null && cp.Title != null ? "«" + cp.Title + "»" : !string.IsNullOrEmpty(c.Text) && c.Text.Length < 30 ? "«" + c.Text + "»" : ""); }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, int flags);

        void RebuildCore()
        {
            ResetPageBackground();
            var a = chart != null ? chart.A.ToList() : new List<double>(); var b = chart != null ? chart.B.ToList() : new List<double>();
            if (runner != null) runner.Cf.Cancelled = true;
            SuspendLayout();
            var old = Controls.Cast<Control>().ToList();
            Controls.Clear();
            foreach (var c in old) c.Dispose();
            BuildUi();
            chart.A.AddRange(a); chart.B.AddRange(b); chart.Invalidate();
            RedrawLog();
            Native.TitleTheme(Handle, Theme.IsDark); DwmTheme.Apply(Handle);
            trayMenu.Renderer = new ThemedMenuRenderer();
            var wv = widget.Visible || widget2 != null && widget2.Visible;
            widget.Dispose(); if (widget2 != null) widget2.Dispose();
            widget = NewWidget(false); widget2 = NewWidget(true);
            if (wv) ApplyWidgetVisibility();
            if (MonOn) { foreach (var c in new[] { cardVpn, cardDir }) { c.Level = Level.Wait; c.Value = "…"; } btnMon.Text = Tx("Остановить мониторинг"); btnMon.Primary = false; }
            UpdateInfo(); UpdateTray(); BeginInvoke((Action)UpdateChars);
            ResumeLayout(true);
            Invalidate(true);
        }

        void BuildShell()
        {
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            side = new Panel { Dock = DockStyle.Left, Width = Theme.S(st.SideWidth), BackColor = Theme.Side };
            HookSideResize();
            Controls.Add(content); Controls.Add(side);
            side.Paint += (s, e) =>
            {
                var g = e.Graphics; Gfx.Hq(g);
                var lr = new RectangleF(Theme.S(20), Theme.S(24), Theme.S(30), Theme.S(30));
                if (appIconBmp != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(appIconBmp, lr); }
                else if (Theme.Classic) { using (var br = new SolidBrush(Theme.Accent)) g.FillRectangle(br, lr); Gfx.Bevel(g, Rectangle.Round(lr), true); }
                else Gfx.FillRound(g, Theme.Accent, lr, Theme.S(8));
                if (appIconBmp == null) Gfx.Text(g, "\uE968", Theme.Icons, Theme.AccentFg, Rectangle.Round(lr), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                Gfx.Text(g, "NetProbe", Theme.H2, Theme.Text, new Rectangle(Theme.S(60), Theme.S(20), Theme.S(150), Theme.S(22)), TextFormatFlags.Left);
                Gfx.Text(g, Tx("VPN · DPI · монитор  v") + Version, Theme.Small, Theme.Muted, new Rectangle(Theme.S(60), Theme.S(40), Theme.S(150), Theme.S(18)), TextFormatFlags.Left);
                if (Theme.Classic) Gfx.Bevel(g, new Rectangle(side.Width - 2, 0, 2, side.Height), true);
                else using (var p = new Pen(Theme.Border)) g.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height);
            };
            string[][] items = {
                new[] { "", Tx("Маршрутизация") }, new[] { "\uE753", Tx("Подписки") }, new[] { "", Tx("Мониторинг") },
                new[] { "", Tx("VPN-конфиги") }, new[] { "\uEA18", Tx("DPI-конфиги") }, new[] { "", Tx("Тесты") }, new[] { "", Tx("Сайты") }, new[] { "", Tx("Лог и опрос") }, new[] { "", Tx("Настройки") }, new[] { "\uE8BD", "TG Proxy" } };
            btnAppUpd = new FlatButton(Tx("Обновить NetProbe"), true) { Glyph = "\uE896", Visible = false, Height = Theme.S(30) };
            btnAppUpd.SetBounds(Theme.S(16), Theme.S(70), side.Width - Theme.S(34), Theme.S(30));
            btnAppUpd.Click += (s, e) => DoAppUpdate();
            side.Controls.Add(btnAppUpd);
            btnRestart = new FlatButton(Tx("Перезапустить сейчас"), true) { Glyph = "\uE72C", Visible = false, Height = Theme.S(30) };
            btnRestart.SetBounds(Theme.S(16), Theme.S(70), side.Width - Theme.S(34), Theme.S(30));
            btnRestart.Click += (s, e) => RestartNow();
            Ui.Tip().SetToolTip(btnRestart, Tx("NetProbe обновлён → изменилась логика маршрутизации → перезапусти приложение вручную"));
            side.Controls.Add(btnRestart);
            int y = Theme.S(80);
            // свои вкладки пользователя — после «Настроек»; названия пунктов можно менять (ПКМ по пункту)
            var all = items.Select((it, i) => new[] { it[0], NavName(i, it[1]) }).Concat(CustomPages().Select(cp => new[] { "\uE8A1", cp.Value })).ToArray();
            for (int i = 0; i < all.Length; i++)
            {
                int idx = i;
                var nb = new NavButton(all[i][0], all[i][1]) { Location = new Point(0, y), Width = side.Width - Theme.S(6) };
                nb.Click += (s, e) => SelectPage(idx);
                nb.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) NavMenu(idx); };
                HookModuleDrop(nb, idx); // на любой пункт меню можно бросить карточку
                side.Controls.Add(nb); navs.Add(nb);
                y += nb.Height + Theme.S(1);
            }
            LayoutNav();
            sideStatus = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Side, Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right, TextAlign = ContentAlignment.BottomLeft, UseMnemonic = false };
            sideStatus.SetBounds(Theme.S(20), side.Height - Theme.S(96), side.Width - Theme.S(30), Theme.S(82));
            side.Controls.Add(sideStatus);
            // маленькая копия виджета Monitor — над строкой состояния
            var oldMini = miniMon; miniMon = new MiniMonitor(); miniMon.CopyFrom(oldMini); miniMon.SetParts(st.SidePing && MonOn, st.SideSpeedOn);
            miniMon.DoubleClick += (s, e) => SelectPage(PgMonitor);
            miniMon.Click += (s, e) => SelectPage(PgMonitor);
            Ui.Tip().SetToolTip(miniMon, Tx("Мониторинг: VPN и напрямую — клик открывает вкладку Monitor"));
            side.Controls.Add(miniMon);
            laySide = () =>
            {
                sideStatus.SetBounds(Theme.S(20), side.Height - Theme.S(96), side.Width - Theme.S(30), Theme.S(82));
                miniMon.SetBounds(Theme.S(12), sideStatus.Top - Theme.S(4) - miniMon.Height, side.Width - Theme.S(26), miniMon.Height);
                int navBottom = navs.Count > 0 ? navs.Max(n => n.Bottom) : 0;
                miniMon.Visible = miniMon.Any && miniMon.Top > navBottom + Theme.S(8); // мало места по высоте — не налезаем на меню; показывать нечего — нет окошка
            };
            side.Resize += (s, e) => laySide(); laySide();
        }
        Label sideStatus; MiniMonitor miniMon; Action laySide;

        // ширина левой панели: тянуть мышкой за правый край (полоса 6 px), сохраняется
        bool sideDrag;
        void HookSideResize()
        {
            int grip = Theme.S(6);
            side.MouseMove += (s, e) =>
            {
                if (sideDrag)
                {
                    int w = Math.Max(Theme.S(150), Math.Min(Theme.S(380), e.X));
                    if (w != side.Width) SetSideWidth(w);
                    return;
                }
                side.Cursor = !st.UiLocked && e.X >= side.Width - grip ? Cursors.SizeWE : Cursors.Default;
            };
            side.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && !st.UiLocked && e.X >= side.Width - grip) { sideDrag = true; side.Capture = true; } };
            side.MouseUp += (s, e) =>
            {
                if (!sideDrag) return;
                sideDrag = false; side.Capture = false;
                st.SideWidth = (int)Math.Round(side.Width / Theme.Scale); st.Save();
            };
            side.MouseLeave += (s, e) => { if (!sideDrag) side.Cursor = Cursors.Default; };
        }
        void SetSideWidth(int w)
        {
            side.Width = w;
            foreach (var nb in navs) nb.Width = w - Theme.S(6);
            if (btnAppUpd != null) btnAppUpd.Width = w - Theme.S(34);
            if (btnRestart != null) btnRestart.Width = w - Theme.S(34);
            if (laySide != null) laySide();
            side.Invalidate();
        }

        // ===== положение и размер окна =====
        void RestoreWindowBounds()
        {
            if (ShotsDir != null || st.WinW <= 0 || st.WinH <= 0) return;
            var r = new Rectangle(st.WinX, st.WinY, Math.Max(MinimumSize.Width, st.WinW), Math.Max(MinimumSize.Height, st.WinH));
            bool visible = false;
            foreach (var sc in Screen.AllScreens)
            {
                var i = Rectangle.Intersect(sc.WorkingArea, r);
                if (i.Width >= Theme.S(200) && i.Height >= Theme.S(120)) visible = true;
            }
            if (visible) { StartPosition = FormStartPosition.Manual; Bounds = r; }
            else Size = r.Size;
            if (st.WinMax) WindowState = FormWindowState.Maximized;
        }
        void SaveWindowBounds()
        {
            if (ShotsDir != null || st == null || WindowState == FormWindowState.Minimized || !Visible) return;
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            bool max = WindowState == FormWindowState.Maximized;
            if (r.Width <= 0 || r.Height <= 0) return;
            if (st.WinX == r.X && st.WinY == r.Y && st.WinW == r.Width && st.WinH == r.Height && st.WinMax == max) return;
            st.WinX = r.X; st.WinY = r.Y; st.WinW = r.Width; st.WinH = r.Height; st.WinMax = max; st.Save();
        }
        protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); SaveWindowBounds(); }
        FormWindowState lastWinState = FormWindowState.Normal;

        // 3.2: страница = внешняя панель (фон, правая колонка модулей) + внутренняя (содержимое; её размер и видят раскладки)
        Panel NewPage(string title, string subtitle)
        {
            var bg = PageBackground();
            Theme.Translucent = bg != null; Theme.CardAlpha = (int)(255 * Math.Max(20, Math.Min(100, st.CardOpacity)) / 100.0);
            var p = new Panel { Dock = DockStyle.Fill, BackColor = bg != null ? Color.Transparent : Theme.Bg };
            int idx = pages.Count;
            var outer = WrapPage(p, idx);
            if (bg != null)
            {
                outer.BackgroundImage = bg; outer.BackgroundImageLayout = ImageLayout.Stretch;
                p.ControlAdded += (s2, e2) => MakeSeeThrough(e2.Control);
                MakeSeeThrough(docks[idx]); MakeSeeThrough(dockSplits[idx]); MakeSeeThrough(frames[idx].Scroll);
            }
            if (idx < PgFirstCustom) title = NavName(idx, title); // переименованный пункт меню — и заголовок страницы
            var h = Ui.Label(title, Theme.H1); h.Location = new Point(Theme.S(26), Theme.S(18));
            var sub = Ui.Label(subtitle, Theme.Body, Theme.Muted); sub.Location = new Point(Theme.S(28), Theme.S(50));
            p.Controls.Add(h); p.Controls.Add(sub);
            content.Controls.Add(outer); pages.Add(p);
            return p;
        }

        void SelectPage(int i)
        {
            curPage = i;
            if (ShotsDir == null && i >= 0 && i < pages.Count && st.LastPage != i) { st.LastPage = i; st.Save(); }
            for (int k = 0; k < pages.Count; k++) { Panel o; (pageOuter.TryGetValue(k, out o) ? o : pages[k]).Visible = k == i; if (k < navs.Count) navs[k].Selected = k == i; }
            // разделы Настроек могут лежать на любой странице — их поля заполняются там же
            if (i == PgSettings || moduleKeys.Any(k => k.StartsWith("set.") && PageOf(k) == i)) LoadSettingsUi();
            if (i == PgTasks) RefreshTasks();
            if (i == PgConnect) LoadConnectUi();
            if (i == PgLog) FillLogDays();
            if (i == PgServers) { FillServers(); UpdateCoreLabel(); }
            // страница стала видна — переложить колонку модулей под её настоящий размер (пока страница скрыта, размер мог быть устаревшим:
            // окно свёрнуто / в трее при запуске → колонка оставалась низкой и обрезала карточку)
            PageFrame pf; if (frames.TryGetValue(i, out pf)) { LayFrame(pf); PlaceMore(pf); }
        }

        // ================= Adapters / probers =================
        string ResolveCurl()
        {
            string c = (st.CurlPath ?? "").Trim();
            if (c == "" || c.Equals("curl.exe", StringComparison.OrdinalIgnoreCase) || c.Equals("curl", StringComparison.OrdinalIgnoreCase))
            {
                string local = Path.Combine(Settings.Root, "curl.exe");
                if (File.Exists(local)) return local;
                string sys = Path.Combine(Environment.SystemDirectory, "curl.exe");
                if (File.Exists(sys)) return sys;
                return "curl.exe";
            }
            return c;
        }

        void CheckCurl()
        {
            var r = Proc.Run(curlPath, "--version", 8000);
            if (r.StartError != null || r.Exit != 0)
                Logger.Write(LogLevel.Error, Tx("curl.exe не найден (") + curlPath + Tx("). В Windows 10 1803+ он есть в System32; иначе положите curl.exe рядом с NetProbe.exe или укажите путь в Настройках."));
            else Logger.Write(LogLevel.Info, "curl: " + r.Out.Split('\n')[0].Trim());
            if (!File.Exists(st.WiresockPath)) Logger.Write(LogLevel.Warn, Tx("WireSock не найден: ") + st.WiresockPath + Tx(" — VPN-режимы недоступны, пока он не установлен."));
            string z = ZapretRunner.Check(st.ZapretPath); if (z != null) Logger.Write(LogLevel.Warn, z);
        }

        void RefreshAdapters(bool first)
        {
            var list = Adapters.List();
            var newDirect = Adapters.PickDirect(list, st.DirectInterface);
            var newRoute = Adapters.DefaultRoute(list);
            string oldIp = direct != null ? direct.Ip : null;
            string oldRoute = defRoute != null ? defRoute.Id : null;
            adapters = list; direct = newDirect; defRoute = newRoute;
            if (first || oldIp != (direct != null ? direct.Ip : null))
            {
                if (direct != null) Logger.Write(first ? LogLevel.Info : LogLevel.Warn, Tx("Источник канала «Напрямую»: ") + direct.Name + " (" + direct.Desc + ") " + direct.Ip + (direct.Gateway != null ? Tx(", шлюз ") + direct.Gateway : ""));
                else Logger.Write(LogLevel.Error, Tx("Не найден физический адаптер со шлюзом — канал «Напрямую» пойдёт по маршруту по умолчанию."));
            }
            if (!first && oldRoute != (defRoute != null ? defRoute.Id : null))
                Logger.Write(LogLevel.Warn, Tx("Маршрут по умолчанию изменился → ") + (defRoute != null ? defRoute.Name : Tx("нет")));
            RebuildProbers();
            UpdateInfo();
        }

        void RebuildProbers()
        {
            curlPath = ResolveCurl();
            // Две копии curl.exe под разными именами: netprobe-vpn.exe всегда кладётся в туннель, netprobe-direct.exe — нет
            string vpnExe = MakeCurlCopy(curlPath, "netprobe-vpn.exe");
            string dirExe = MakeCurlCopy(curlPath, "netprobe-direct.exe");
            // без WireSock, но с подключённым xray VPN-канал мониторинга идёт через его socks
            string proxy = (st.VpnProxy ?? "").Trim();
            if (proxy == "" && xm.Active && !router.Active) proxy = "socks5h://127.0.0.1:" + st.SocksPort;
            // IPv4 / IPv6: через socks5h адрес резолвит прокси (и может выбрать любой протокол) — тогда резолвим сами (socks5)
            if (st.ProbeIpFamily != 2 && proxy.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase)) proxy = "socks5://" + proxy.Substring(10);
            pVpn = new CurlProber { CurlPath = vpnExe, Proxy = proxy, ViaShell = false, IpFamily = st.ProbeIpFamily };
            string bind = direct != null ? direct.Ip : null;
            if (st.DirectMode == 1) pDir = new PsProber { PingSource = bind };
            else pDir = new CurlProber { CurlPath = dirExe, ViaShell = false, BindIp = bind, NoProxy = true, PingSource = bind };
            if (cardVpn != null) { cardVpn.Sub = pVpn.Describe; cardDir.Sub = pDir.Describe; cardVpn.Invalidate(); cardDir.Invalidate(); UpdateEngineLabel(); }
        }

        string MakeCurlCopy(string src, string name)
        {
            try
            {
                if (!File.Exists(src)) return src;
                string dir = Path.Combine(CurlProber.WorkDir, "bin");
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, name);
                var si = new FileInfo(src);
                if (File.Exists(dst))
                {
                    var di = new FileInfo(dst);
                    if (di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc) return dst;
                }
                File.Copy(src, dst, true);
                Logger.Write(LogLevel.Info, Tx("Пробник: ") + dst);
                return dst;
            }
            catch (Exception e)
            {
                Logger.Write(LogLevel.Warn, Tx("Не удалось создать ") + name + " (" + e.Message + Tx(") — используется ") + src + Tx("; раздельный туннель не различит каналы."));
                return src;
            }
        }

        void UpdateInfo()
        {
            if (infoLabel == null || infoLabel.IsDisposed) return;
            var vpnAd = adapters.Where(a => a.IsVpn).ToList();
            bool routeVpn = defRoute != null && (direct == null || defRoute.Index != direct.Index);
            infoLabel.Text = Tx("Маршрут по умолчанию: ") + (defRoute != null ? defRoute.Name : "?") + (routeVpn ? Tx("  (VPN-туннель)") : "") +
                       Tx("     ·     VPN-адаптеры: ") + (vpnAd.Count > 0 ? string.Join(", ", vpnAd.Select(a => a.Name)) : Tx("нет")) +
                       Tx("     ·     Напрямую: ") + (direct != null ? direct.Name + " " + direct.Ip : Tx("маршрут по умолчанию")) +
                       "     ·     DNS: " + Adapters.DnsServers();
            string w = "";
            if (!string.IsNullOrEmpty(st.VpnProxy)) w = Tx("VPN-канал идёт через прокси ") + st.VpnProxy + ".";
            var a1 = chs["vpn"].Ip; var a2 = chs["direct"].Ip;
            if (a1 != "" && a1 == a2) w = Tx("Оба канала выходят с одного публичного IP (") + a1 + Tx(") — VPN выключен или правило туннеля не совпадает.");
            warnLabel.Text = w; warnLabel.Visible = w != "";
            UpdateSide();
        }

        void UpdateSide()
        {
            if (sideStatus == null || sideStatus.IsDisposed) return;
            sideStatus.Text = "● " + (!router.Active && xm.Active ? XrayManager.StateName(xm.State) + " · xray" : Router.StateName(router.State)) + "\n" + Router.ModeName(st.RunMode) + "\n" + (MonOn ? Tx("Мониторинг: ") + PollText() : Tx("Мониторинг выключен")) + Tx("\nНапрямую: ") + (direct != null ? direct.Ip : Tx("маршрут по умолчанию"));
        }

        string PollText()
        {
            return st.PollRate.ToString("0.0#", CultureInfo.InvariantCulture) + Tx(" опр/с · ") + (int)Math.Max(Settings.MinIntervalMs, 1000 / st.PollRate) + Tx(" мс");
        }

        // ================= Monitoring =================
        // Мониторинг включает/выключает пользователь (кнопка, трей) — состояние запоминается (AutoStartMonitor).
        // Маршрутизатору для аварийного переключения нужен опрос VPN-канала: если пользователь мониторинг выключил,
        // опрос идёт «в фоне» (bgProbe) — без карточек, графика и виджета.
        bool bgProbe;
        void SyncAutoMonBox() { if (cAutoMon != null && !cAutoMon.IsDisposed && cAutoMon.Checked != st.AutoStartMonitor) { bool l = loadingSettings; loadingSettings = true; try { cAutoMon.Checked = st.AutoStartMonitor; } finally { loadingSettings = l; } } }
        bool MonOn { get { return monVpn.Running && !bgProbe; } }
        void StartProbe()
        {
            if (st.AutoStartMonitor) { StartMonitoring(false); return; }
            SyncBgProbe();
        }
        // Мониторинг выключен (нет карточек и виджета), но опросы могут быть нужны:
        //  • лог — опросы и статистика Day/Week пишутся, пока включено «Опросы в лог» (LogProbes);
        //  • маршрутизатор — опрос VPN-канала для аварийного переключения.
        void SyncBgProbe()
        {
            if (MonOn) return;
            bool both = st.LogProbes, vpn = both || router.NeedsVpnProbe;
            if (!vpn) { if (monVpn.Running) monVpn.Stop(); if (monDir.Running) monDir.Stop(); bgProbe = false; return; }
            bgProbe = true;
            if (!monVpn.Running) monVpn.Start();
            if (both && !monDir.Running) monDir.Start(); else if (!both && monDir.Running) monDir.Stop();
        }
        void StartMonitoring(bool user = true)
        {
            if (user && !st.AutoStartMonitor) { st.AutoStartMonitor = true; st.Save(); }
            if (user) SyncAutoMonBox();
            monitoringWanted = true;
            if (MonOn) return;
            if (bgProbe) { bgProbe = false; if (monVpn.Running) monVpn.Stop(); if (monDir.Running) monDir.Stop(); }
            monVpn.Start(); monDir.Start();
            ApplyMonitorParts();
            foreach (var c in new[] { cardVpn, cardDir }) { c.Level = Level.Wait; c.Value = "…"; }
            btnMon.Text = Tx("Остановить мониторинг"); btnMon.Primary = false; btnMon.Invalidate();
            if (miMonitor != null) miMonitor.Checked = true;
            Logger.Write(LogLevel.Info, Tx("Мониторинг запущен · ") + PollText() + Tx(" на канал"));
            UpdateInfo();
        }

        void StopMonitoring(bool user = true)
        {
            if (user) { monitoringWanted = false; if (st.AutoStartMonitor) { st.AutoStartMonitor = false; st.Save(); } SyncAutoMonBox(); }
            if (!MonOn) { SyncBgProbe(); return; }
            bool wasBg = false;
            bool keepProbe = router.NeedsVpnProbe || st.LogProbes;
            monVpn.Stop(); monDir.Stop(); bgProbe = false;
            SyncBgProbe(); // опросы для лога / маршрутизатора продолжаются в фоне, без интерфейса
            foreach (var k in chs.Keys.ToList()) { chs[k].Ok = null; chs[k].Lv = Level.Off; }
            foreach (var c in new[] { cardVpn, cardDir }) { c.Level = Level.Off; }
            btnMon.Text = Tx("Запустить мониторинг"); btnMon.Primary = true; btnMon.Invalidate();
            if (miMonitor != null) miMonitor.Checked = false;
            widget.SetLevel(0, Level.Off, "—"); widget.SetLevel(1, Level.Off, "—");
            if (miniMon != null) { miniMon.SetLevel(0, Level.Off, "—"); miniMon.SetLevel(1, Level.Off, "—"); }
            UpdateTray();
            if (!wasBg) Logger.Write(LogLevel.Info, Tx("Мониторинг остановлен") + (keepProbe && bgProbe ? (st.LogProbes ? Tx(" (опросы продолжают писаться в лог)") : Tx(" (проверка VPN для аварийного переключения продолжается в фоне)")) : ""));
            UpdateInfo();
            ApplyMonitorParts();
        }

        void OnSampleBg(ChannelMonitor m, ProbeResult r)
        {
            if (m.Key == "vpn") router.VpnSample(r.Ok, r.Time);
            if (bgProbe)
            {
                // мониторинг выключен: без интерфейса, но в лог (если включено «Опросы в лог»)
                if (!st.LogProbes) return;
                History.Add(m.Key, r.Ok, r.Latency);
                if (st.LogPolls) Logger.Write(LogLevel.Poll, (m.Key == "vpn" ? "VPN" : Tx("Напрямую")) + ": " + (r.Ok ? "HTTP " + r.Code + " · RTT " + F0(r.Latency) + Tx(" мс") : Tx("СБОЙ · ") + r.Error));
                return;
            }
            History.Add(m.Key, r.Ok, r.Latency);
            var snap = m.Stats.Snap();
            if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => OnSample(m, r, snap))); } catch { }
        }

        Level LatLevel(double ms) { return ms < st.GoodMs ? Level.Good : ms < st.WarnMs ? Level.Fair : Level.Slow; }
        static string F0(double v) { return v < 0 ? "—" : v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v < 0 ? "—" : v.ToString("0.0", CultureInfo.InvariantCulture); }

        readonly Dictionary<string, List<string>> seenIps = new Dictionary<string, List<string>>();
        readonly HashSet<string> multiIpLogged = new HashSet<string>();
        void OnSample(ChannelMonitor m, ProbeResult r, StatsSnapshot s)
        {
            if (!m.Running || bgProbe || cardVpn == null || cardVpn.IsDisposed) return;
            bool isVpn = m.Key == "vpn";
            string name = isVpn ? "VPN" : Tx("Напрямую");
            var cs = chs[m.Key];
            var card = isVpn ? cardVpn : cardDir;
            // одиночный неудачный опрос — это ещё не обрыв: «связь потеряна» только после DownAfterFails сбоев подряд
            int downAfter = Math.Max(1, st.DownAfterFails);
            cs.FailRun = r.Ok ? 0 : cs.FailRun + 1;
            if (cs.FailRun == 1) cs.FirstFail = r.Time;
            bool isDown = !r.Ok && cs.FailRun >= downAfter, blip = !r.Ok && !isDown;
            Level lv = r.Ok ? LatLevel(r.Latency) : isDown ? Level.Down : Level.Slow;
            if (r.Ok && s.Count >= 10 && s.LossPct >= 5 && lv < Level.Slow) lv = Level.Slow;
            if (blip)
            {
                Logger.Write(LogLevel.Warn, name + Tx(": опрос не прошёл (") + cs.FailRun + "/" + downAfter + ") — " + r.Error);
                card.Level = lv; card.Line1 = "✗  " + r.Error; card.Pulse();
                chart.Add(isVpn, LatencyChart.Blip);
                if (st.LogPolls) Logger.Write(LogLevel.Poll, name + ": " + Tx("СБОЙ · ") + r.Error);
                cs.Lv = lv;
                widget.SetLevel(isVpn ? 0 : 1, lv, cs.Val); if (miniMon != null && !miniMon.IsDisposed) miniMon.SetLevel(isVpn ? 0 : 1, lv, cs.Val);
                UpdateTray();
                return;
            }

            if (cs.Ok == null)
                Logger.Write(r.Ok ? LogLevel.Ok : LogLevel.Error, name + ": " + (r.Ok ? Tx("есть связь · ") + F0(r.Latency) + Tx(" мс") + (r.Ip != "" ? " · IP " + r.Ip + " " + r.Loc : "") : Tx("нет связи — ") + r.Error));
            else if (cs.Ok == true && !r.Ok)
            {
                cs.DownSince = cs.FirstFail; // обрыв считается с первого неудачного опроса
                Logger.Write(LogLevel.Error, name + Tx(": связь потеряна — ") + r.Error);
                Notify(cs, name + Tx(": связь потеряна"), r.Error, ToolTipIcon.Error);
            }
            else if (cs.Ok == false && r.Ok)
            {
                var down = DateTime.Now - cs.DownSince;
                Logger.Write(LogLevel.Ok, name + Tx(": связь восстановлена через ") + (int)down.TotalSeconds + Tx(" с · ") + F0(r.Latency) + Tx(" мс"));
                Notify(cs, name + Tx(": связь восстановлена"), Tx("Задержка ") + F0(r.Latency) + Tx(" мс"), ToolTipIcon.Info);
            }
            if (r.Ok && r.Ip != "" && cs.Ip != "" && r.Ip != cs.Ip)
            {
                List<string> seen;
                if (!seenIps.TryGetValue(m.Key, out seen)) seenIps[m.Key] = seen = new List<string>();
                bool isNew = !seen.Contains(r.Ip);
                if (!st.IpNewOnly || isNew) Logger.Write(LogLevel.Warn, name + Tx(": публичный IP сменился ") + cs.Ip + " → " + r.Ip + " (" + r.Loc + ")");
                else if (!multiIpLogged.Contains(m.Key))
                {
                    // адрес «прыгает» между уже виденными — одна строка вместо спама
                    multiIpLogged.Add(m.Key);
                    Logger.Write(LogLevel.Info, name + Tx(": выходит через несколько IP: ") + string.Join(", ", seen));
                }
            }
            if (r.Ok && r.Ip != "")
            {
                List<string> seen;
                if (!seenIps.TryGetValue(m.Key, out seen)) seenIps[m.Key] = seen = new List<string>();
                if (!seen.Contains(r.Ip)) { seen.Add(r.Ip); if (seen.Count > 8) seen.RemoveAt(0); multiIpLogged.Remove(m.Key); }
            }
            if (cs.Ok == null) cs.DownSince = DateTime.Now;
            if (!r.Ok && cs.Ok != false) cs.DownSince = cs.FirstFail;
            cs.Ok = r.Ok;
            if (r.Ok && r.Ip != "") { bool chg = cs.Ip != r.Ip; cs.Ip = r.Ip; if (chg) UpdateInfo(); }
            var other = chs[isVpn ? "direct" : "vpn"];
            bool same = r.Ok && other.Ok == true && cs.Ip != "" && cs.Ip == other.Ip;
            if (same != sameRoute)
            {
                sameRoute = same;
                if (same) Logger.Write(LogLevel.Warn, Tx("VPN и «Напрямую» выходят с одного IP ") + cs.Ip + Tx(" — оба идут одним маршрутом."));
                else Logger.Write(LogLevel.Ok, Tx("Каналы разделены: VPN ") + chs["vpn"].Ip + Tx(" / напрямую ") + chs["direct"].Ip);
            }
            cardVpn.Badge = cardDir.Badge = sameRoute ? Tx("ОДИН МАРШРУТ") : null;
            if (st.LogPolls)
                Logger.Write(LogLevel.Poll, name + ": " + (r.Ok ? "HTTP " + r.Code + " · RTT " + F0(r.Latency) + Tx(" мс · DNS ") + F0(r.Dns) + " · TCP " + F0(r.Connect) + " · TLS " + F0(r.Tls) + Tx(" · всего ") + F0(r.Total) + Tx(" мс") : Tx("СБОЙ · ") + r.Error));

            card.Level = lv;
            card.Value = r.Ok ? F0(r.Latency) : "—";
            card.Line1 = r.Ok ? "IP " + (r.Ip != "" ? r.Ip : r.RemoteIp) + (r.Loc != "" ? "  ·  " + r.Loc : "") + (r.Colo != "" ? Tx("  ·  узел ") + r.Colo : "") + "  ·  HTTP " + r.Code : "✗  " + r.Error;
            card.Line2 = r.Ok ? "DNS " + F0(r.Dns) + "  ·  TCP " + F0(r.Connect) + "  ·  TLS " + F0(r.Tls) + Tx("  ·  всего ") + F0(r.Total) + Tx(" мс")
                              : Tx("Повтор через ") + F1(m.EffectiveIntervalMs / 1000) + Tx(" с (после ") + m.ConsecutiveFails + Tx(" ошибок)");
            card.Line3 = Tx("ср ") + F0(s.Avg) + Tx("  ·  мин ") + F0(s.Min) + Tx("  ·  макс ") + F0(s.Max) + Tx("  ·  джиттер ") + F1(s.Jitter) + Tx("  ·  потери ") + F1(s.LossPct) + Tx("%  ·  аптайм ") + F1(s.Uptime) + Tx("%  ·  обрывы ") + s.Drops;
            card.Pulse();
            if (isDown && cs.FailRun == downAfter) chart.ConfirmLast(isVpn, downAfter - 1);
            chart.Add(isVpn, r.Ok ? r.Latency : double.NaN);

            cs.Lv = lv; cs.Val = r.Ok ? F0(r.Latency) + Tx(" мс") : Tx("нет связи");
            widget.SetRow(isVpn ? 0 : 1, lv, cs.Val, r.Ok ? r.Latency : double.NaN);
            if (miniMon != null && !miniMon.IsDisposed) miniMon.SetRow(isVpn ? 0 : 1, lv, r.Ok ? F0(r.Latency) + Tx(" мс") : "✗", r.Ok ? r.Latency : double.NaN);
            UpdateTray();
            if (isVpn) UpdateEffective();
        }

        void Notify(ChState cs, string title, string text, ToolTipIcon icon)
        {
            if (!st.Notify || (DateTime.Now - cs.LastNotify).TotalSeconds < 30) return;
            cs.LastNotify = DateTime.Now;
            try { tray.ShowBalloonTip(4000, title, string.IsNullOrEmpty(text) ? " " : text, icon); } catch { }
        }

        // ================= Route control =================
        void Connect() { ConnectCurrent(true); }

        void Disconnect() { DisconnectAll(); }

        RouteState lastState = RouteState.Off;
        void OnRouteChanged()
        {
            var s = router.State;
            if (s != lastState)
            {
                if (lastState != RouteState.Off && st.Notify && tray != null)
                {
                    if (s == RouteState.DpiFailover) try { tray.ShowBalloonTip(4000, Tx("NetProbe: переключение на DPI"), router.Detail, ToolTipIcon.Warning); } catch { }
                    else if (s == RouteState.Backup) try { tray.ShowBalloonTip(4000, Tx("NetProbe: резервный VPN"), st.BackupTunnel, ToolTipIcon.Warning); } catch { }
                    else if (s == RouteState.VpnDown) try { tray.ShowBalloonTip(4000, Tx("NetProbe: VPN недоступен"), router.Detail, ToolTipIcon.Error); } catch { }
                    else if (s == RouteState.Vpn && (lastState == RouteState.DpiFailover || lastState == RouteState.Backup || lastState == RouteState.VpnDown)) try { tray.ShowBalloonTip(3000, Tx("NetProbe: снова на основном VPN"), st.PrimaryTunnel, ToolTipIcon.Info); } catch { }
                }
                lastState = s;
                if (bgProbe) SyncBgProbe();
                else if (s == RouteState.Off && monVpn.Running && !monitoringWanted) StopMonitoring(false);
            }
            if (miConnect != null) miConnect.Text = AnyActive ? Tx("Отключить") : Tx("Подключить");
            UpdateConnectUi();
            UpdateServersUi();
            UpdateModeUi();
            UpdateDpiState();
            UpdateTray();
            UpdateWidgetHeader();
            if (miniMon != null && !miniMon.IsDisposed) { miniMon.Header = ""; /* без статуса — только MONITOR */ miniMon.Invalidate(); }
        }

        // в виджетах — тип подключения: протокол сервера (VLESS · REALITY · xray) или туннеля (WIREGUARD · WireSock)
        void UpdateWidgetHeader()
        {
            string h = AnyActive ? EngineText() : "";
            foreach (var w in new[] { widget, widget2 })
                if (w != null && !w.IsDisposed && w.Header != h) { w.Header = h; w.Invalidate(); }
        }

        void OnSpeed(double d, double u)
        {
            string sd = SpeedMeter.Short(d) + "b/s", su = SpeedMeter.Short(u) + "b/s";
            foreach (var w in new[] { widget, widget2 })
                if (w != null && !w.IsDisposed) { w.SetRow(2, Level.Good, sd, d); w.SetRow(3, Level.Good, su, u); }
            if (miniMon != null && !miniMon.IsDisposed) { miniMon.SetRow(2, Level.Good, sd, d); miniMon.SetRow(3, Level.Good, su, u); }
            if (speedChart != null && !speedChart.IsDisposed) { speedChart.Adapter = SpeedMeter.AdapterName; speedChart.Add(d, u); }
        }

        void OnUiTick()
        {
            UpdateWidgetHeader();
            if (curPage == PgConnect) UpdateConnectUi();
            History.Tick();
            if (curPage == PgTasks && chkTasksAuto != null && chkTasksAuto.Checked) RefreshTasks();
        }

        // ================= Tray & widget =================
        WidgetForm NewWidget(bool second)
        {
            var w = new WidgetForm(trayMenu) { TopMost = st.WidgetTopMost, Opacity = st.WidgetOpacity / 100.0 };
            w.OpenRequested += (s, e) => RestoreFromTray();
            if (second) w.Moved += (s, e) => { st.Widget2X = w.Left; st.Widget2Y = w.Top; st.Save(); };
            else w.Moved += (s, e) => { st.WidgetX = w.Left; st.WidgetY = w.Top; st.Save(); };
            w.Header = AnyActive ? EngineText() : "";
            if (second) w.SetParts(false, true); else { bool wp = st.WidgetPing && MonOn, wsp = st.WidgetSpeedOn; if (wp || wsp) w.SetParts(wp, wsp && !(st.WidgetSplit && wp)); }
            if (!second) HookWidgetChars(w);
            return w;
        }

        void BuildTray()
        {
            BuildTrayMenu();
            tray = new NotifyIcon { Text = "NetProbe", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (hiddenToTray) RestoreFromTray(); else HideToTray(false); } };
            widget = NewWidget(false); widget2 = NewWidget(true);
            UpdateTray();
        }

        void BuildTrayMenu()
        {
            trayMenu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body, ShowImageMargin = true };
            trayMenu.Items.Add(Tx("Открыть NetProbe"), null, (s, e) => RestoreFromTray());
            miConnect = new ToolStripMenuItem(Tx("Подключить"));
            miConnect.Click += (s, e) => { if (AnyActive) Disconnect(); else Connect(); };
            trayMenu.Items.Add(miConnect);
            var miMode = new ToolStripMenuItem(Tx("Режим"));
            miModes = new ToolStripMenuItem[3];
            for (int i = 0; i < 3; i++)
            {
                int m = i;
                miModes[i] = new ToolStripMenuItem(Router.ModeName((RouteMode)i)) { Checked = st.Mode == i };
                miModes[i].Click += (s, e) => SetMode((RouteMode)m);
                miMode.DropDownItems.Add(miModes[i]);
            }
            trayMenu.Items.Add(miMode);
            trayMenu.Items.Add(new ToolStripSeparator());
            miWidget = new ToolStripMenuItem(Tx("Виджет на рабочем столе")) { Checked = st.ShowWidget };
            miWidget.Click += (s, e) => { st.ShowWidget = !st.ShowWidget; miWidget.Checked = st.ShowWidget; st.Save(); ApplyWidgetVisibility(); };
            miCharEdit = new ToolStripMenuItem(Tx("Редактор персонажей")) { Checked = charEdit };
            miCharEdit.Click += (s, e) => SetCharEdit(!charEdit);
            trayMenu.Opening += (s, e) => { if (miCharEdit != null) { miCharEdit.Visible = Theme.Personal; miCharEdit.Checked = charEdit; } };
            trayMenu.Items.Add(miWidget);
            trayMenu.Items.Add(miCharEdit);
            // страховка: если что-то «потерялось» при раскладке — всё на свои места (работает и при Lock interface)
            trayMenu.Items.Add(Tx("Сбросить раскладку интерфейса"), null, (s, e) => ResetLayout());
            miMonitor = new ToolStripMenuItem(Tx("Мониторинг"));
            miMonitor.Click += (s, e) => { if (MonOn) StopMonitoring(); else StartMonitoring(); };
            trayMenu.Items.Add(miMonitor);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Tx("Выход"), null, (s, e) => ExitApp());
            if (miMonitor != null) miMonitor.Checked = MonOn;
        }

        void RebuildTrayMenu()
        {
            var old = trayMenu;
            BuildTrayMenu();
            tray.ContextMenuStrip = trayMenu;
            widget.ContextMenuStrip = trayMenu; if (widget2 != null) widget2.ContextMenuStrip = trayMenu;
            try { old.Dispose(); } catch { }
            OnRouteChanged();
        }

        // Карточки режимов выбирают, чьи списки настраиваются; VPN при этом не переподключается.
        // DPI/VPN и Только VPN заодно задают поведение кнопки VPN (с переключением на DPI или без).

        // Иконка приложения: своя из настроек или встроенная; она же — основа значка в трее
        Bitmap appIconBmp;
        void ApplyAppIcon()
        {
            Icon ico = null;
            try
            {
                string p = st.AppIconPath;
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p))
                {
                    string ext = Path.GetExtension(p).ToLowerInvariant();
                    if (ext == ".ico") ico = new Icon(p);
                    else if (ext == ".exe" || ext == ".dll") ico = Icon.ExtractAssociatedIcon(p);
                    else using (var img = Image.FromFile(p)) using (var b = new Bitmap(img, 64, 64)) ico = Icon.FromHandle(b.GetHicon());
                }
            }
            catch { ico = null; }
            if (ico == null) try { ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (ico == null) return;
            Icon = ico;
            try { var old = appIconBmp; using (var i64 = new Icon(ico, 64, 64)) appIconBmp = i64.ToBitmap(); if (old != null) old.Dispose(); } catch { appIconBmp = ico.ToBitmap(); }
            // иконка отдельным файлом в папке приложения
            try { string f = Path.Combine(Settings.Root, "NetProbe.ico"); if (!File.Exists(f)) using (var src = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) using (var fs = File.Create(f)) src.Save(fs); } catch { }
            if (tray != null) UpdateTray();
            if (side != null && !side.IsDisposed) side.Invalidate();
            if (IsHandleCreated) { Native.SendMessage(Handle, 0x0080, (IntPtr)1, ico.Handle); Native.SendMessage(Handle, 0x0080, IntPtr.Zero, ico.Handle); } // WM_SETICON: окно и панель задач
        }

        // Фон страниц: своя картинка, смешанная с цветом темы, чтобы карточки и текст читались
        Image pageBg;
        Image PageBackground()
        {
            if (pageBg != null) return pageBg;
            bool hasImg = !string.IsNullOrWhiteSpace(st.BgImage) && File.Exists(st.BgImage);
            // Personal: персонаж внутри окна (запасной режим) — в слое фона страниц, за полупрозрачными карточками
            string insideFile = CharOverlay.HasArt(Theme.Kind, "window.png") ? "window.png" : "frame.png"; // своей картинки «внутри» нет — берём картинку «у окна»
            bool charInside = Theme.Personal && st.CharWindow && !st.CharFrame && CharOverlay.HasArt(Theme.Kind, insideFile);
            if (!hasImg && !charInside) return null;
            try
            {
                Bitmap b;
                if (hasImg)
                    using (var img = Image.FromFile(st.BgImage))
                    {
                        b = new Bitmap(img.Width, img.Height);
                        using (var g = Graphics.FromImage(b))
                        {
                            g.DrawImage(img, 0, 0, img.Width, img.Height);
                            using (var br = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(95, st.BgDim)) / 100.0), Theme.Bg))) g.FillRectangle(br, 0, 0, b.Width, b.Height);
                        }
                    }
                else
                {
                    var sz = content != null && content.ClientSize.Width > 200 ? content.ClientSize : new Size(Theme.S(1100), Theme.S(760));
                    b = new Bitmap(sz.Width, sz.Height);
                    using (var g = Graphics.FromImage(b)) g.Clear(Theme.Bg);
                }
                if (charInside)
                    using (var art = new Bitmap(Path.Combine(CharOverlay.Folder(Theme.Kind), insideFile)))
                    using (var g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        // по умолчанию: Kurumi — у левого края снизу, Chisa — сверху справа; размер и угол — в Настройках
                        int pct = st.CharInsideSize > 0 ? st.CharInsideSize : (Theme.Kurumi ? 78 : 55);
                        int anchor = st.CharInsideAnchor >= 0 ? st.CharInsideAnchor : (Theme.Kurumi ? 0 : 2);
                        int h = (int)(b.Height * Math.Max(10, Math.Min(100, pct)) / 100.0), w = h * art.Width / art.Height;
                        int x = anchor == 1 || anchor == 2 ? b.Width - w : anchor == 4 ? (b.Width - w) / 2 : 0;
                        int y = anchor == 0 || anchor == 1 ? b.Height - h : anchor == 4 ? (b.Height - h) / 2 : 0;
                        var dst = new Rectangle(x, y, w, h);
                        var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)(Math.Max(20, Math.Min(100, st.CharOpacity)) / 100.0 * 0.55) };
                        using (var ia = new System.Drawing.Imaging.ImageAttributes()) { ia.SetColorMatrix(cm); g.DrawImage(art, dst, 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, ia); }
                    }
                pageBg = b;
            }
            catch { pageBg = null; }
            return pageBg;
        }
        // Подписи с непрозрачным фоном карточки/страницы делаем прозрачными, чтобы фон просвечивал
        void MakeSeeThrough(Control c)
        {
            if (c is Label && (c.BackColor == Theme.Card || c.BackColor == Theme.Bg)) c.BackColor = Color.Transparent;
            // 3.0.7: панели-контейнеры (прокрутка Настроек, правая колонка Subscriptions, VPN/DPI-конфиги) — тоже прозрачные,
            // иначе на своём фоне вместо картинки был чёрный прямоугольник
            if (c is Panel && !(c is CardPanel) && c.BackColor == Theme.Bg) c.BackColor = Color.Transparent;
            if (c is CardPanel || c is Panel) c.ControlAdded += (s, e) => MakeSeeThrough(e.Control);
            foreach (Control ch in c.Controls) MakeSeeThrough(ch);
        }

        void ResetPageBackground() { var o = pageBg; pageBg = null; if (o != null) BeginInvoke((Action)(() => o.Dispose())); }

        void UpdateTray()
        {
            if (tray == null) return;
            var a = chs["vpn"]; var b = chs["direct"];
            string tip = "NetProbe · " + (!router.Active && xm.Active ? XrayManager.StateName(xm.State) + " · xray" : Router.StateName(router.State)) + "\nVPN: " + a.Val + Tx("\nНапрямую: ") + b.Val;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            try { tray.Text = tip; } catch { }
            int sz = SystemInformation.SmallIconSize.Width;
            using (var bmp = new Bitmap(sz, sz))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                    // та же иконка, что у окна, + точка состояния в углу
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    if (appIconBmp != null) g.DrawImage(appIconBmp, 0, 0, sz, sz);
                    Color dot = router.State == RouteState.VpnDown ? Theme.Red : router.State == RouteState.DpiFailover || router.State == RouteState.DpiOnly || router.ManualDpi ? Theme.Yellow : router.Active || xm.Active ? Theme.LevelColor(a.Lv) : a.Lv == Level.Off ? Color.Empty : Theme.LevelColor(a.Lv);
                    if (dot != Color.Empty)
                    {
                        float d = sz * 0.46f;
                        using (var br = new SolidBrush(Color.FromArgb(20, 20, 20))) g.FillEllipse(br, sz - d - 0.5f, sz - d - 0.5f, d + 0.5f, d + 0.5f);
                        using (var br = new SolidBrush(dot)) g.FillEllipse(br, sz - d + 1.5f, sz - d + 1.5f, d - 3, d - 3);
                    }
                }
                IntPtr h = bmp.GetHicon();
                var old = trayIconHandle;
                tray.Icon = Icon.FromHandle(h);
                trayIconHandle = h;
                if (old != IntPtr.Zero) Native.DestroyIcon(old);
            }
        }

        void ApplyWidgetVisibility()
        {
            // мониторинг выключен — строк пинга нет; нечего показывать (ни пинга, ни скорости) — виджета нет совсем
            bool ping = st.WidgetPing && MonOn, speed = st.WidgetSpeedOn;
            bool split = st.WidgetSplit && ping && speed;
            if (ping || speed) widget.SetParts(ping, speed && !split);
            if (widget2 != null) widget2.SetParts(false, true);
            if (hiddenToTray && st.ShowWidget && (ping || speed || charEdit))
            {
                if (!widget.Visible) { widget.EnsureOnScreen(st.WidgetX, st.WidgetY); widget.Show(); }
                widget.TopMost = st.WidgetTopMost; widget.Opacity = st.WidgetOpacity / 100.0; widget.Invalidate();
                if (widget2 != null)
                {
                    if (split)
                    {
                        if (!widget2.Visible)
                        {
                            // по умолчанию второй виджет — над первым
                            int x2 = st.Widget2X, y2 = st.Widget2Y;
                            if (x2 == -1 && y2 == -1) { x2 = widget.Left; y2 = widget.Top - widget2.Height - Theme.S(8); }
                            widget2.EnsureOnScreen(x2, y2); widget2.Show();
                        }
                        widget2.TopMost = st.WidgetTopMost; widget2.Opacity = st.WidgetOpacity / 100.0; widget2.Invalidate();
                    }
                    else if (widget2.Visible) widget2.Hide();
                }
            }
            else
            {
                if (widget.Visible) widget.Hide();
                if (widget2 != null && widget2.Visible) widget2.Hide();
            }
        }

        // настройки мониторов (виджет, боковая панель, график скорости) применяются сразу
        void ApplyMonitorParts()
        {
            ApplyWidgetVisibility();
            if (miniMon != null && !miniMon.IsDisposed) { miniMon.SetParts(st.SidePing && MonOn, st.SideSpeedOn); if (laySide != null) laySide(); }
            if (st.SpeedOn) SpeedMeter.Start(); else SpeedMeter.Stop();
            if (!MonOn) SyncBgProbe();
            if (layMonitor != null) layMonitor();
        }

        void HideToTray(bool silent)
        {
            SaveWindowBounds();
            hiddenToTray = true;
            Hide();
            ApplyWidgetVisibility();
            if (!silent && !trayHintShown && !st.ShowWidget)
            {
                trayHintShown = true;
                try { tray.ShowBalloonTip(2500, Tx("NetProbe работает"), Tx("Маршрутизация и мониторинг продолжаются в трее."), ToolTipIcon.Info); } catch { }
            }
        }

        void RestoreFromTray()
        {
            hiddenToTray = false;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate(); Native.SetForegroundWindow(Handle);
            ApplyWidgetVisibility();
            PageFrame pf; if (frames.TryGetValue(curPage, out pf)) LayFrame(pf); // из трея — колонка модулей по настоящему размеру окна
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != lastWinState) { lastWinState = WindowState; if (WindowState != FormWindowState.Minimized && IsHandleCreated) BeginInvoke((Action)(() => { SaveWindowBounds(); PageFrame pf; if (frames.TryGetValue(curPage, out pf)) LayFrame(pf); })); }
            if (WindowState == FormWindowState.Minimized && st != null && st.MinimizeToTray && !hiddenToTray && Visible && !rebuilding)
                BeginInvoke((Action)(() => HideToTray(false)));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && st.CloseToTray)
            {
                e.Cancel = true; HideToTray(false); return;
            }
            exiting = true;
            SaveWindowBounds();
            monVpn.Stop(); monDir.Stop();
            if (runner != null) runner.Cf.Cancelled = true;
            if (!exitForUpdate) // при самообновлении VPN/DPI/xray продолжают работать, новая версия их подхватит
            {
                try { router.ShutdownSync(); } catch { }
                try { xm.ShutdownSync(); } catch { }
            }
            try { TgProxy.Stop(); } catch { }
            try { tray.Visible = false; tray.Dispose(); } catch { }
            base.OnFormClosing(e);
        }

        void ExitApp() { exiting = true; Close(); Application.Exit(); }

        // ===== самообновление NetProbe (GitHub Releases) =====
        public static bool Updated;
        bool exitForUpdate; FlatButton btnAppUpd; Updater.Release appRelease;

        void LayoutNav()
        {
            if (btnAppUpd == null) return;
            bool show = appRelease != null && SelfUpdate.Newer(appRelease.Tag);
            btnAppUpd.Visible = show;
            if (show && btnAppUpd.Enabled) btnAppUpd.Text = Tx("Обновить до ") + appRelease.Tag; // во время загрузки там статус
            if (btnRestart != null) { btnRestart.Visible = NeedsManualRestart && !show; if (btnRestart.Visible) show = true; }
            int y = Theme.S(show ? 112 : 80);
            foreach (int i in NavOrder()) { if (i >= navs.Count) continue; var nb = navs[i]; nb.Top = y; y += nb.Height + Theme.S(1); }
        }

        void CheckAppUpdate(bool manual)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.Release rel = null; string err = null;
                try { rel = SelfUpdate.Latest(); } catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        // фоновая проверка: при ошибке (сеть ещё не поднялась, туннель переподключается) повтор через 2 мин, иначе через час
                        // «Сразу» — опрос раз в 15 мин, иначе раз в час (лимит GitHub API 60/ч с IP)
                        if (!manual) { appUpdTimer.Stop(); appUpdTimer.Interval = err != null ? 2 * 60 * 1000 : (st.AppNotifyEvery == 0 ? 15 : 60) * 60 * 1000; appUpdTimer.Start(); }
                        if (rel == null && appRelease != null) rel = appRelease; // ошибка сети не прячет уже найденную кнопку
                        bool newer = rel != null && rel.AssetUrl != "" && SelfUpdate.Newer(rel.Tag);
                        bool found = newer && (appRelease == null || appRelease.Tag != rel.Tag);
                        appRelease = newer ? rel : null;
                        LayoutNav();
                        if (lAppVer != null && !lAppVer.IsDisposed) lAppVer.Text = "v" + Version + (newer ? "  →  " + rel.Tag + " " + Tx("доступна") : err != null ? "  ·  " + err : "  ·  " + Tx("последняя"));
                        if (found) Logger.Write(LogLevel.Info, Tx("Доступна новая версия NetProbe: ") + rel.Tag);
                        if (newer) ScheduleAppNotify(); // одно уведомление на версию — по частоте и задержке из Настроек
                        if (manual && !newer) MessageBox.Show(this, err != null ? err : Tx("Установлена последняя версия: ") + "v" + Version, "NetProbe");
                    }));
                }
                catch { }
            });
        }

        // ===== уведомление о новой версии (ТЗ, раздел 13): одно на версию, частота Сразу/День/Неделя/Месяц, задержка после публикации =====
        readonly System.Windows.Forms.Timer appNotifyTimer = new System.Windows.Forms.Timer();
        static readonly int[] NotifyPeriodDays = { 0, 1, 7, 30 };
        void ScheduleAppNotify()
        {
            appNotifyTimer.Stop();
            var rel = appRelease;
            if (rel == null || !st.UpdNotify || !SelfUpdate.Newer(rel.Tag) || st.AppNotifiedTag == rel.Tag) return;
            var now = DateTime.Now;
            var pub = rel.PublishedAt == DateTime.MinValue ? now : rel.PublishedAt.ToLocalTime();
            var due = pub.AddMinutes(st.AppNotifyDelayOn ? st.AppNotifyDelayMin : 0);
            var last = st.AppNotifiedAt > 0 ? DateTime.FromOADate(st.AppNotifiedAt) : DateTime.MinValue;
            int days = NotifyPeriodDays[Math.Max(0, Math.Min(3, st.AppNotifyEvery))];
            if (days > 0 && last != DateTime.MinValue && last.AddDays(days) > due) due = last.AddDays(days);
            if (due > now)
            {
                // таймер не дольше суток за раз: дальше просто пересчитаем
                double ms = Math.Min((due - now).TotalMilliseconds + 5000 + new Random().Next(25000), 24 * 3600 * 1000.0);
                appNotifyTimer.Interval = (int)Math.Max(1000, ms);
                appNotifyTimer.Start();
                return;
            }
            st.AppNotifiedTag = rel.Tag; st.AppNotifiedAt = now.ToOADate(); st.Save();
            Logger.Write(LogLevel.Info, Tx("Уведомление о новой версии NetProbe ") + rel.Tag);
            string title = "NetProbe " + rel.Tag + " " + Tx("доступна");
            string text = Tx("Обновление сохраняет VPN и DPI. После обновления перезапусти приложение вручную — изменилась логика маршрутизации.");
            if (!Toast.Show(title, text, Tuple.Create(Tx("Обновить"), "update"), Tuple.Create(Tx("Позже"), "later")) && tray != null)
                try { tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); } catch { }
        }

        // ===== критическое обновление (ТЗ, раздел 8): после обновления — перезапуск вручную, по кнопке, без тихой замены =====
        FlatButton btnRestart;
        public static bool Reapply3; // запуск после «Перезапустить сейчас»: перестроить конфиги туннеля/winws/xray по новой логике
        bool NeedsManualRestart { get { return st.UpdatedFrom != "" && st.UpdatedFrom != Version; } }
        void ShowRestartNotice()
        {
            if (!NeedsManualRestart) return;
            bool d;
            int r = Notice.Show(this, Tx("NetProbe обновлён до ") + "v" + Version, Tx("Изменилась логика маршрутизации — перезапусти приложение вручную. VPN, DPI и xray при перезапуске не отключатся."),
                new[] { Tx("Перезапустить сейчас"), Tx("Позже") }, null, out d);
            if (r == 0) RestartNow();
        }
        void RestartNow()
        {
            try
            {
                var h = router.Handoff(); h["xray"] = xm.Active ? "1" : "0";
                SelfUpdate.SaveHandoff(h);
                st.UpdatedFrom = ""; st.Save();
                Logger.Write(LogLevel.Info, Tx("Перезапуск NetProbe по кнопке (VPN/DPI/xray продолжают работать)"));
                SelfUpdate.LaunchRestart();
                exitForUpdate = true; ExitApp();
            }
            catch (Exception ex) { Logger.Write(LogLevel.Error, Tx("Перезапуск: ") + ex.Message); }
        }

        void OnToast(string arg)
        {
            if (!IsHandleCreated) return;
            try { BeginInvoke((Action)(() => { if (arg == "later") return; RestoreFromTray(); if (arg == "update" && appRelease != null) DoAppUpdate(); })); } catch { }
        }

        void DoAppUpdate()
        {
            var rel = appRelease; if (rel == null) return;
            if (MessageBox.Show(this, Tx("Скачать и установить NetProbe ") + rel.Tag + "?\r\n" + Tx("VPN и DPI продолжат работать, приложение перезапустится само."), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            btnAppUpd.Enabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string exe = SelfUpdate.Download(rel, m => { try { BeginInvoke((Action)(() => btnAppUpd.Text = m)); } catch { } });
                    var h = router.Handoff(); h["xray"] = xm.Active ? "1" : "0";
                    SelfUpdate.SaveHandoff(h);
                    st.UpdatedFrom = Version; st.Save(); // новая версия покажет «перезапусти вручную»
                    Logger.Write(LogLevel.Info, Tx("Установка NetProbe ") + rel.Tag + "…");
                    SelfUpdate.LaunchSwap(exe);
                    BeginInvoke((Action)(() => { exitForUpdate = true; ExitApp(); }));
                }
                catch (Exception ex)
                {
                    Logger.Write(LogLevel.Error, Tx("Ошибка обновления: ") + ex.Message);
                    try { BeginInvoke((Action)(() => { btnAppUpd.Enabled = true; LayoutNav(); MessageBox.Show(this, Tx("Ошибка обновления: ") + ex.Message, "NetProbe"); })); } catch { }
                }
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.WM_SHOWME && Program.WM_SHOWME != 0) { RestoreFromTray(); return; }
            if (m.Msg == Program.WM_QUITME && Program.WM_QUITME != 0) { ExitApp(); return; }
            if (m.Msg == FileDrop.WM_DROPFILES)
            {
                // PNG из Проводника на окно — картинка персонажа у окна
                var f = FileDrop.Files(m.WParam).FirstOrDefault(x => x.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (f != null) BeginInvoke((Action)(() => DropCharPicture(f, false)));
                return;
            }
            base.WndProc(ref m);
        }
    }
}
