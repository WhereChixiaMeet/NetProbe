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
        public const string Version = "2.0.6";
        Settings st;
        readonly ChannelMonitor monVpn = new ChannelMonitor("vpn"), monDir = new ChannelMonitor("direct");
        volatile Prober pVpn, pDir;
        List<AdapterInfo> adapters = new List<AdapterInfo>();
        AdapterInfo direct, defRoute;
        string curlPath = "curl.exe";
        readonly Router router = new Router();

        NotifyIcon tray; ContextMenuStrip trayMenu; ToolStripMenuItem miWidget, miMonitor, miConnect; ToolStripMenuItem[] miModes;
        WidgetForm widget;
        IntPtr trayIconHandle = IntPtr.Zero;
        bool exiting, hiddenToTray, trayHintShown, startHidden;
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly System.Windows.Forms.Timer updTimer = new System.Windows.Forms.Timer { Interval = 30 * 60 * 1000 };
        readonly System.Windows.Forms.Timer reapplyTimer = new System.Windows.Forms.Timer { Interval = 700 };

        class ChState { public bool? Ok; public string Ip = ""; public DateTime DownSince; public DateTime LastNotify; public Level Lv = Level.Off; public string Val = "—"; }
        bool sameRoute;
        readonly Dictionary<string, ChState> chs = new Dictionary<string, ChState> { { "vpn", new ChState() }, { "direct", new ChState() } };

        Panel side, content;
        readonly List<NavButton> navs = new List<NavButton>();
        readonly List<Control> pages = new List<Control>();
        int curPage;
        bool monitoringWanted;

        public const int PgConnect = 0, PgServers = 1, PgConfigs = 2, PgMonitor = 3, PgTests = 4, PgSites = 5, PgLog = 6, PgSettings = 7, PgTasks = 99;

        public MainForm(bool startMinimized)
        {
            st = Settings.Load();
            Theme.AccentColor = Theme.ParseColor(st.Accent);
            Tr.Ru = st.Lang == "ru";
            AppLists.ZapretLists = Path.Combine(st.ZapretPath, "lists");
            Theme.Apply((ThemeKind)st.Theme);
            startHidden = startMinimized || st.StartMinimized;
            Logger.ToFile = st.LogToFile; Logger.Dir = st.LogPath; Logger.Retention = st.Retention;
            Text = "NetProbe";
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(Theme.S(1220), Theme.S(800));
            MinimumSize = new Size(Theme.S(1040), Theme.S(680));
            StartPosition = FormStartPosition.CenterScreen;
            ApplyAppIcon();
            DoubleBuffered = true;

            CurlProber.EnsureFormatFile();
            Logger.Line += (t, lv, s) => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => AppendLog(t, lv, s))); } catch { } };
            FirstRunSetup();

            router.GetSettings = () => st;
            router.AskStopService = q => (bool)Invoke((Func<bool>)(() => { RestoreFromTray(); return MessageBox.Show(this, q, "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes; }));
            router.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)OnRouteChanged); } catch { } };
            // несколько правок подряд (добавить + убрать из соседнего списка) = один перезапуск туннеля
            reapplyTimer.Tick += (s, e) => { reapplyTimer.Stop(); if (router.Active) router.Reapply(); };
            AppLists.Changed += key => { if (IsHandleCreated) try { BeginInvoke((Action)(() => { if (AppLists.IsServers(key)) { FillSrvLists(); return; } reapplyTimer.Stop(); reapplyTimer.Start(); RefreshListsUi(); })); } catch { } }; // списки «Серверов» не трогают туннель WireSock

            InitXray();
            BuildUi();
            BuildTray();

            monVpn.GetSettings = monDir.GetSettings = () => st;
            monVpn.GetProber = () => pVpn;
            monDir.GetProber = () => pDir;
            monVpn.Sample += OnSampleBg; monDir.Sample += OnSampleBg;

            RefreshAdapters(true);
            refreshTimer.Tick += (s, e) => RefreshAdapters(false);
            refreshTimer.Start();
            uiTimer.Tick += (s, e) => OnUiTick();
            uiTimer.Start();
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

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); }

        protected override void SetVisibleCore(bool value)
        {
            if (startHidden && !IsHandleCreated) { CreateHandle(); value = false; startHidden = false; BeginInvoke((Action)(() => { Startup(); HideToTray(true); })); }
            base.SetVisibleCore(value);
        }

        protected override void OnLoad(EventArgs e) { base.OnLoad(e); Startup(); }

        // Отладка: --shots <папка> — снимки всех страниц во всех темах, затем выход
        public static string ShotsDir;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (ShotsDir == null) return;
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += (s, ev) =>
            {
                t.Stop();
                Directory.CreateDirectory(ShotsDir);
                int keep = st.Theme;
                foreach (ThemeKind k in new[] { ThemeKind.Dark, ThemeKind.Light, ThemeKind.Classic })
                {
                    Theme.Apply(k); st.Theme = (int)k; RebuildUi();
                    for (int i = 0; i < pages.Count; i++)
                    {
                        SelectPage(i); Application.DoEvents(); System.Threading.Thread.Sleep(150); Application.DoEvents();
                        using (var bmp = new Bitmap(Width, Height)) { DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height)); bmp.Save(Path.Combine(ShotsDir, k + "-" + i + ".png")); }
                    }
                }
                using (var bmp = new Bitmap(widget.Width, widget.Height)) { widget.DrawToBitmap(bmp, new Rectangle(0, 0, widget.Width, widget.Height)); bmp.Save(Path.Combine(ShotsDir, "widget.png")); }
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
            if (st.AutoStartMonitor) StartMonitoring();
            if (autorunApplied == null) { autorunApplied = st.Autorun; if (st.Autorun) ThreadPool.QueueUserWorkItem(_ => SetAutorun(true)); }
            var handoff = Updated ? SelfUpdate.TakeHandoff() : null;
            if (handoff != null)
            {
                StartMonitoring();
                router.Adopt(handoff);
                xm.Adopt(handoff.ContainsKey("xray") && handoff["xray"] == "1");
            }
            // автоподключение: без диалогов, если туннель ещё не настроен (например, чистая установка)
            else if (st.AutoConnect)
            {
                if (Tunnels.Exists(st.PrimaryTunnel)) Connect();
                else Logger.Write(LogLevel.Info, Tx("Автоподключение пропущено: основной VPN-туннель не выбран"));
            }
            ScheduledUpdateCheck(true);
            updTimer.Tick += (s, e) => ScheduledUpdateCheck(false); updTimer.Start();
            if (handoff == null && st.XrayAutoConnect && ServerStore.Find(st.ActiveServer) != null) { StartMonitoring(); xm.Connect(st.ActiveServer); }
        }

        // ================= Shell / navigation =================
        void BuildUi()
        {
            SuspendLayout();
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            navs.Clear(); pages.Clear();
            BuildShell();
            BuildConnectPage();
            BuildServersPage();
            BuildConfigsPage();
            BuildMonitorPage();
            BuildTestsPage();
            BuildSitesPage();
            BuildLogPage();
            BuildSettingsPage();
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
            Native.TitleTheme(Handle, Theme.IsDark);
            trayMenu.Renderer = new ThemedMenuRenderer();
            var wv = widget.Visible;
            widget.Dispose();
            widget = NewWidget();
            if (wv) ApplyWidgetVisibility();
            if (monVpn.Running) { foreach (var c in new[] { cardVpn, cardDir }) { c.Level = Level.Wait; c.Value = "…"; } btnMon.Text = Tx("Остановить мониторинг"); btnMon.Primary = false; }
            UpdateInfo(); UpdateTray();
            ResumeLayout(true);
            Invalidate(true);
        }

        void BuildShell()
        {
            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            side = new Panel { Dock = DockStyle.Left, Width = Theme.S(214), BackColor = Theme.Side };
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
                new[] { "", Tx("Подключение") }, new[] { "\uE753", Tx("Серверы") }, new[] { "", Tx("Конфиги") },
                new[] { "", Tx("Мониторинг") }, new[] { "", Tx("Тесты") }, new[] { "", Tx("Сайты") }, new[] { "", Tx("Лог и опрос") }, new[] { "", Tx("Настройки") } };
            btnAppUpd = new FlatButton(Tx("Обновить NetProbe"), true) { Glyph = "\uE896", Visible = false, Height = Theme.S(30) };
            btnAppUpd.SetBounds(Theme.S(16), Theme.S(70), side.Width - Theme.S(34), Theme.S(30));
            btnAppUpd.Click += (s, e) => DoAppUpdate();
            side.Controls.Add(btnAppUpd);
            int y = Theme.S(80);
            for (int i = 0; i < items.Length; i++)
            {
                int idx = i;
                var nb = new NavButton(items[i][0], items[i][1]) { Location = new Point(0, y), Width = side.Width - 2 };
                nb.Click += (s, e) => SelectPage(idx);
                side.Controls.Add(nb); navs.Add(nb);
                y += nb.Height + Theme.S(1);
            }
            LayoutNav();
            sideStatus = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Side, Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right, TextAlign = ContentAlignment.BottomLeft, UseMnemonic = false };
            sideStatus.SetBounds(Theme.S(20), side.Height - Theme.S(96), side.Width - Theme.S(30), Theme.S(82));
            side.Controls.Add(sideStatus);
            side.Resize += (s, e) => sideStatus.SetBounds(Theme.S(20), side.Height - Theme.S(96), side.Width - Theme.S(30), Theme.S(82));
        }
        Label sideStatus;

        Panel NewPage(string title, string subtitle)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var bg = PageBackground();
            Theme.Translucent = bg != null; Theme.CardAlpha = (int)(255 * Math.Max(20, Math.Min(100, st.CardOpacity)) / 100.0);
            if (bg != null) { p.BackgroundImage = bg; p.BackgroundImageLayout = ImageLayout.Stretch; p.ControlAdded += (s2, e2) => MakeSeeThrough(e2.Control); }
            var h = Ui.Label(title, Theme.H1); h.Location = new Point(Theme.S(26), Theme.S(18));
            var sub = Ui.Label(subtitle, Theme.Body, Theme.Muted); sub.Location = new Point(Theme.S(28), Theme.S(50));
            p.Controls.Add(h); p.Controls.Add(sub);
            content.Controls.Add(p); pages.Add(p);
            return p;
        }

        void SelectPage(int i)
        {
            curPage = i;
            for (int k = 0; k < pages.Count; k++) { pages[k].Visible = k == i; navs[k].Selected = k == i; }
            if (i == PgSettings) LoadSettingsUi();
            if (i == PgTasks) RefreshTasks();
            if (i == PgConnect) LoadConnectUi();
            if (i == PgLog) FillLogDays();
            if (i == PgServers) { FillServers(); UpdateCoreLabel(); }
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
            pVpn = new CurlProber { CurlPath = vpnExe, Proxy = proxy, ViaShell = false };
            string bind = direct != null ? direct.Ip : null;
            if (st.DirectMode == 1) pDir = new PsProber { PingSource = bind };
            else pDir = new CurlProber { CurlPath = dirExe, ViaShell = false, BindIp = bind, NoProxy = true, PingSource = bind };
            if (cardVpn != null) { cardVpn.Sub = pVpn.Describe; cardDir.Sub = pDir.Describe; cardVpn.Invalidate(); cardDir.Invalidate(); }
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
            sideStatus.Text = "● " + Router.StateName(router.State) + "\n" + Router.ModeName(st.RunMode) + "\n" + (monVpn.Running ? Tx("Мониторинг: ") + PollText() : Tx("Мониторинг выключен")) + Tx("\nНапрямую: ") + (direct != null ? direct.Ip : Tx("маршрут по умолчанию"));
        }

        string PollText()
        {
            return st.PollRate.ToString("0.0#", CultureInfo.InvariantCulture) + Tx(" опр/с · ") + (int)Math.Max(Settings.MinIntervalMs, 1000 / st.PollRate) + Tx(" мс");
        }

        // ================= Monitoring =================
        void StartMonitoring()
        {
            monitoringWanted = true;
            if (monVpn.Running) return;
            monVpn.Start(); monDir.Start();
            foreach (var c in new[] { cardVpn, cardDir }) { c.Level = Level.Wait; c.Value = "…"; }
            btnMon.Text = Tx("Остановить мониторинг"); btnMon.Primary = false; btnMon.Invalidate();
            if (miMonitor != null) miMonitor.Checked = true;
            Logger.Write(LogLevel.Info, Tx("Мониторинг запущен · ") + PollText() + Tx(" на канал"));
            UpdateInfo();
        }

        void StopMonitoring(bool user = true)
        {
            if (user) monitoringWanted = false;
            if (!monVpn.Running) return;
            if (router.NeedsVpnProbe) { Logger.Write(LogLevel.Warn, Tx("Мониторинг нужен маршрутизатору для аварийного переключения — он продолжит работу, пока VPN подключён.")); return; }
            monVpn.Stop(); monDir.Stop();
            foreach (var k in chs.Keys.ToList()) { chs[k].Ok = null; chs[k].Lv = Level.Off; }
            foreach (var c in new[] { cardVpn, cardDir }) { c.Level = Level.Off; }
            btnMon.Text = Tx("Запустить мониторинг"); btnMon.Primary = true; btnMon.Invalidate();
            if (miMonitor != null) miMonitor.Checked = false;
            widget.SetLevel(0, Level.Off, "—"); widget.SetLevel(1, Level.Off, "—");
            UpdateTray();
            Logger.Write(LogLevel.Info, Tx("Мониторинг остановлен"));
            UpdateInfo();
        }

        void OnSampleBg(ChannelMonitor m, ProbeResult r)
        {
            if (m.Key == "vpn") router.VpnSample(r.Ok, r.Time);
            var snap = m.Stats.Snap();
            if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => OnSample(m, r, snap))); } catch { }
        }

        Level LatLevel(double ms) { return ms < st.GoodMs ? Level.Good : ms < st.WarnMs ? Level.Fair : Level.Slow; }
        static string F0(double v) { return v < 0 ? "—" : v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v < 0 ? "—" : v.ToString("0.0", CultureInfo.InvariantCulture); }

        void OnSample(ChannelMonitor m, ProbeResult r, StatsSnapshot s)
        {
            if (!m.Running || cardVpn == null || cardVpn.IsDisposed) return;
            bool isVpn = m.Key == "vpn";
            string name = isVpn ? "VPN" : Tx("Напрямую");
            var cs = chs[m.Key];
            var card = isVpn ? cardVpn : cardDir;
            Level lv = r.Ok ? LatLevel(r.Latency) : Level.Down;
            if (r.Ok && s.Count >= 10 && s.LossPct >= 5 && lv < Level.Slow) lv = Level.Slow;

            if (cs.Ok == null)
                Logger.Write(r.Ok ? LogLevel.Ok : LogLevel.Error, name + ": " + (r.Ok ? Tx("есть связь · ") + F0(r.Latency) + Tx(" мс") + (r.Ip != "" ? " · IP " + r.Ip + " " + r.Loc : "") : Tx("нет связи — ") + r.Error));
            else if (cs.Ok == true && !r.Ok)
            {
                cs.DownSince = DateTime.Now;
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
                Logger.Write(LogLevel.Warn, name + Tx(": публичный IP сменился ") + cs.Ip + " → " + r.Ip + " (" + r.Loc + ")");
            if (cs.Ok == null) cs.DownSince = DateTime.Now;
            if (!r.Ok && cs.Ok != false) cs.DownSince = DateTime.Now;
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
            chart.Add(isVpn, r.Ok ? r.Latency : double.NaN);

            cs.Lv = lv; cs.Val = r.Ok ? F0(r.Latency) + Tx(" мс") : Tx("нет связи");
            widget.SetRow(isVpn ? 0 : 1, lv, cs.Val, r.Ok ? r.Latency : double.NaN);
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
        void Connect()
        {
            if (!Tunnels.Exists(st.PrimaryTunnel))
            {
                RestoreFromTray(); SelectPage(PgConfigs); ShowCfgTab(0);
                MessageBox.Show(this, Tx("Не выбран основной VPN-туннель. Добавьте его в «Конфиги»."), "NetProbe", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StartMonitoring();
            router.Connect();
        }

        void Disconnect() { router.Disconnect(); }

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
                if (s == RouteState.Off && !monitoringWanted && monVpn.Running) StopMonitoring(false);
            }
            if (miConnect != null) miConnect.Text = router.Active ? Tx("Отключить") : Tx("Подключить");
            UpdateConnectUi();
            UpdateServersUi();
            UpdateSide();
            UpdateTray();
            if (widget != null) { widget.Header = router.Active ? Router.StateName(s).ToUpperInvariant() : ""; widget.Invalidate(); }
        }

        void OnUiTick()
        {
            if (curPage == PgConnect) UpdateConnectUi();
            if (curPage == PgTasks && chkTasksAuto != null && chkTasksAuto.Checked) RefreshTasks();
        }

        // ================= Tray & widget =================
        WidgetForm NewWidget()
        {
            var w = new WidgetForm(trayMenu) { TopMost = st.WidgetTopMost, Opacity = st.WidgetOpacity / 100.0 };
            w.OpenRequested += (s, e) => RestoreFromTray();
            w.Moved += (s, e) => { st.WidgetX = w.Left; st.WidgetY = w.Top; st.Save(); };
            w.Header = router.Active ? Router.StateName(router.State).ToUpperInvariant() : "";
            return w;
        }

        void BuildTray()
        {
            BuildTrayMenu();
            tray = new NotifyIcon { Text = "NetProbe", ContextMenuStrip = trayMenu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) { if (hiddenToTray) RestoreFromTray(); else HideToTray(false); } };
            widget = NewWidget();
            UpdateTray();
        }

        void BuildTrayMenu()
        {
            trayMenu = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body, ShowImageMargin = true };
            trayMenu.Items.Add(Tx("Открыть NetProbe"), null, (s, e) => RestoreFromTray());
            miConnect = new ToolStripMenuItem(Tx("Подключить"));
            miConnect.Click += (s, e) => { if (router.Active) Disconnect(); else Connect(); };
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
            trayMenu.Items.Add(miWidget);
            miMonitor = new ToolStripMenuItem(Tx("Мониторинг"));
            miMonitor.Click += (s, e) => { if (monVpn.Running) StopMonitoring(); else StartMonitoring(); };
            trayMenu.Items.Add(miMonitor);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add(Tx("Выход"), null, (s, e) => ExitApp());
            if (miMonitor != null) miMonitor.Checked = monVpn.Running;
        }

        void RebuildTrayMenu()
        {
            var old = trayMenu;
            BuildTrayMenu();
            tray.ContextMenuStrip = trayMenu;
            widget.ContextMenuStrip = trayMenu;
            try { old.Dispose(); } catch { }
            OnRouteChanged();
        }

        // Карточки режимов выбирают, чьи списки настраиваются; VPN при этом не переподключается.
        // DPI/VPN и Только VPN заодно задают поведение кнопки VPN (с переключением на DPI или без).
        void SetMode(RouteMode m)
        {
            if (st.RMode == m) return;
            st.Mode = (int)m;
            bool runChanged = false;
            if (m != RouteMode.DpiOnly && st.VpnMode != (m == RouteMode.VpnOnly ? 1 : 0)) { st.VpnMode = m == RouteMode.VpnOnly ? 1 : 0; runChanged = true; }
            st.Save();
            if (miModes != null) for (int i = 0; i < 3; i++) miModes[i].Checked = i == st.Mode;
            LoadConnectUi();
            if (runChanged && router.Active) router.Reapply();
            UpdateSide();
        }

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
            if (string.IsNullOrWhiteSpace(st.BgImage) || !File.Exists(st.BgImage)) return null;
            try
            {
                using (var img = Image.FromFile(st.BgImage))
                {
                    var b = new Bitmap(img.Width, img.Height);
                    using (var g = Graphics.FromImage(b))
                    {
                        g.DrawImage(img, 0, 0, img.Width, img.Height);
                        using (var br = new SolidBrush(Color.FromArgb((int)(255 * Math.Max(0, Math.Min(95, st.BgDim)) / 100.0), Theme.Bg))) g.FillRectangle(br, 0, 0, b.Width, b.Height);
                    }
                    pageBg = b;
                }
            }
            catch { pageBg = null; }
            return pageBg;
        }
        // Подписи с непрозрачным фоном карточки/страницы делаем прозрачными, чтобы фон просвечивал
        void MakeSeeThrough(Control c)
        {
            if (c is Label && (c.BackColor == Theme.Card || c.BackColor == Theme.Bg)) c.BackColor = Color.Transparent;
            if (c is CardPanel || c is Panel && !(c is ScrollHost)) c.ControlAdded += (s, e) => MakeSeeThrough(e.Control);
            foreach (Control ch in c.Controls) MakeSeeThrough(ch);
        }

        void ResetPageBackground() { var o = pageBg; pageBg = null; if (o != null) BeginInvoke((Action)(() => o.Dispose())); }

        void UpdateTray()
        {
            if (tray == null) return;
            var a = chs["vpn"]; var b = chs["direct"];
            string tip = "NetProbe · " + Router.StateName(router.State) + "\nVPN: " + a.Val + Tx("\nНапрямую: ") + b.Val;
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
            if (hiddenToTray && st.ShowWidget)
            {
                if (!widget.Visible) { widget.EnsureOnScreen(st.WidgetX, st.WidgetY); widget.Show(); }
                widget.TopMost = st.WidgetTopMost; widget.Opacity = st.WidgetOpacity / 100.0; widget.Invalidate();
            }
            else if (widget.Visible) widget.Hide();
        }

        void HideToTray(bool silent)
        {
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
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && st != null && st.MinimizeToTray && !hiddenToTray && Visible)
                BeginInvoke((Action)(() => HideToTray(false)));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing && st.CloseToTray)
            {
                e.Cancel = true; HideToTray(false); return;
            }
            exiting = true;
            monVpn.Stop(); monDir.Stop();
            if (runner != null) runner.Cf.Cancelled = true;
            if (!exitForUpdate) // при самообновлении VPN/DPI/xray продолжают работать, новая версия их подхватит
            {
                try { router.ShutdownSync(); } catch { }
                try { xm.ShutdownSync(); } catch { }
            }
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
            if (show) btnAppUpd.Text = Tx("Обновить до ") + appRelease.Tag;
            int y = Theme.S(show ? 112 : 80);
            foreach (var nb in navs) { nb.Top = y; y += nb.Height + Theme.S(1); }
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
                        bool newer = rel != null && rel.AssetUrl != "" && SelfUpdate.Newer(rel.Tag);
                        appRelease = newer ? rel : null;
                        LayoutNav();
                        if (lAppVer != null && !lAppVer.IsDisposed) lAppVer.Text = "v" + Version + (newer ? "  →  " + rel.Tag + " " + Tx("доступна") : err != null ? "  ·  " + err : "  ·  " + Tx("последняя"));
                        if (newer) Logger.Write(LogLevel.Info, Tx("Доступна новая версия NetProbe: ") + rel.Tag);
                        if (newer && !manual && st.UpdNotify && tray != null) try { tray.ShowBalloonTip(5000, Tx("NetProbe: есть обновления"), "NetProbe " + rel.Tag, ToolTipIcon.Info); } catch { }
                        if (manual && !newer) MessageBox.Show(this, err != null ? err : Tx("Установлена последняя версия: ") + "v" + Version, "NetProbe");
                    }));
                }
                catch { }
            });
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
            base.WndProc(ref m);
        }
    }
}
