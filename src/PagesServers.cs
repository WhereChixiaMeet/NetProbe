using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        readonly XrayManager xm = new XrayManager();
        StatusBoard xBoard; FlatButton btnXConnect, btnXDpi, btnCore, btnUpdSub; DataGridView srvGrid; TextBox tbSrvSearch; Label lSrvInfo;
        TabStrip srvTabs, srvListTabs; ThemedList srvListBox; Toggle tgTun; ToolTip srvTip;
        bool loadingAs, xTesting; CancelFlag xTestCf; FlatButton btnSb, btnTest, btnSpeed; ServerEntry speedNow;
        readonly System.Windows.Forms.Timer tunReapply = new System.Windows.Forms.Timer { Interval = 1200 }, srvProxyReapply = new System.Windows.Forms.Timer { Interval = 1500 };
        static readonly string[] SrvListKeys = { AppLists.SrvApps, AppLists.SrvExcl, AppLists.SrvDpiApps, AppLists.SrvDpiExcl };

        void InitXray()
        {
            ServerStore.Load();
            AppLists.SeedServers();
            SysProxy.ClearIfStale(st.HttpPort);
            xm.GetSettings = () => st;
            xm.GetCurl = () => curlPath;
            XState lastX = XState.Off;
            xm.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => { if ((xm.State == XState.Off) != (lastX == XState.Off)) RebuildProbers(); lastX = xm.State; UpdateServersUi(); UpdateSide(); })); } catch { } };
            // DPI «Серверов» не зависит от кнопки VPN: запасной вариант автосвитча и приложения «через DPI»
            xm.DpiFallback = on => router.StandaloneDpi(on);
            // 3.0: DPI для подписок — тот же глобальный winws: нужен в режиме ROUTE (DPI/VPN), кнопка DPI «выкл» его глушит
            xm.NeedDpi = () => st.RunMode == RouteMode.Hybrid && !router.DpiForcedOff;
            router.DpiHold = () => xm.DpiWanted;
            router.ExtraDpiExcl = () => new List<string>();
            // TUN берёт те же списки приложений, что и туннель (вкладка Routing): Include + «Строго VPN» → сервер, Exclude → напрямую.
            // Домены/IP в карточках приложений движку не передаются.
            xm.GetTunLists = () =>
            {
                var inc = AppLists.AppsOnly(AppLists.HybridApps).Concat(AppLists.AppsOnly(AppLists.VpnOnlyApps)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return new TunLists { Include = inc, Exclude = AppLists.AppsOnly(Router.TunExclKey(st)), Domains = new List<KeyValuePair<string, string>>() };
            };
            ZapretRunner.ExtraDomains = () => new List<string>();
            XrayConfig.DirectDomains = () => new List<string>();
            xm.TunBlocked = () => router.Wg.Running ? Tx("TUN не запущен: работает туннель WireSock (вкладка Routing)") : null;
            AppLists.Changed += key =>
            {
                bool appKey = key == AppLists.HybridApps || key == AppLists.VpnOnlyApps || key == AppLists.HybridExcl || key == AppLists.VpnOnlyExcl;
                if (appKey && xm.Active && st.TunMode) { tunReapply.Stop(); tunReapply.Start(); }
            };
            tunReapply.Tick += (s, e) => { tunReapply.Stop(); xm.ReapplyTun(); };
            srvProxyReapply.Tick += (s, e) => { srvProxyReapply.Stop(); if (xm.Active) xm.Reapply(); };
            ServerStore.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => FillServers())); } catch { } };
        }

        void BuildServersPage()
        {
            var p = NewPage(Tx("Серверы"), Tx("xray · подписки · автосвитч"));
            var tt = Ui.Tip(); srvTip = tt;
            // отдельно от VPN на «Подключении»: xray работает и при выключенном VPN
            btnXConnect = new FlatButton(Tx("Подключить"), true) { Glyph = "", Width = Theme.S(170), Height = Theme.S(36) };
            btnXConnect.Click += (s, e) => XConnectClick();
            btnXDpi = new FlatButton("DPI") { Glyph = "", Width = Theme.S(150), Height = Theme.S(36) };
            btnXDpi.Click += (s, e) => ToggleDpi();
            tt.SetToolTip(btnXDpi, Tx("DPI (zapret) — тот же, что на «Подключении»"));
            xBoard = new StatusBoard();

            // панель кнопок
            var bImp = new FlatButton(Tx("Из буфера")) { Glyph = "" }.AutoW();
            bImp.Click += (s, e) =>
            {
                string t = ""; try { t = Clipboard.GetText(); } catch { }
                var list = ServerStore.ParseMany(t);
                int n = ServerStore.AddMany(list, "");
                lSrvInfo.Text = Tx("Добавлено серверов: ") + n + (list.Count == 0 ? " · " + Tx("в буфере нет ссылок vless/vmess/trojan/ss") : "");
                if (n > 0 && srvTabs.SelectedKey != "all") srvTabs.Select("manual");
            };
            var bSubs = new FlatButton(Tx("Подписки")) { Glyph = "" }.AutoW();
            bSubs.Click += (s, e) => { using (var d = new SubsDialog(CurSub() != null ? CurSub().Id : null)) d.ShowDialog(this); FillServers(); };
            btnUpdSub = IconBtn("", Tx("Обновить подписки"), tt);
            btnUpdSub.Click += (s, e) => { var sub = CurSub(); UpdateSubs(sub != null ? new List<Subscription> { sub } : null); };
            var bTest = btnTest = new FlatButton(Tx("Замер")) { Glyph = "" };
            bTest.Width = Math.Max(bTest.PreferredW, StopW(bTest));
            bTest.Click += (s, e) => TestServers(SelServers().Count > 1 ? SelServers() : TabServers());
            tt.SetToolTip(bTest, Tx("Задержка: выбранные или все на вкладке"));
            var bSpeed = btnSpeed = new FlatButton(Tx("Скорость")) { Glyph = "" };
            bSpeed.Width = Math.Max(bSpeed.PreferredW, StopW(bSpeed));
            bSpeed.Click += (s, e) => SpeedServers(SelServers().Count > 0 ? SelServers() : null);
            tt.SetToolTip(bSpeed, Tx("Скорость загрузки: выбранные или рабочие на вкладке"));
            var bFav = IconBtn("", Tx("Избранное"), tt);
            bFav.Click += (s, e) => ToggleFav();
            var bDel = IconBtn("", Tx("Удалить"), tt);
            bDel.Click += (s, e) => { var sel = SelServers(); if (sel.Count > 0 && MessageBox.Show(this, Tx("Удалить серверов: ") + sel.Count + "?", "NetProbe", MessageBoxButtons.YesNo) == DialogResult.Yes) ServerStore.Remove(sel.Select(x => x.Id)); };
            tbSrvSearch = Ui.TextBox(); tbSrvSearch.Width = Theme.S(180); ((ThemedTextBox)tbSrvSearch).Cue = Tx("Поиск");
            tbSrvSearch.TextChanged += (s, e) => FillServers();
            btnCore = new FlatButton(Tx("Ядро xray")) { Glyph = "" }.AutoW();
            btnCore.Click += (s, e) => CoreButton(true);
            btnSb = new FlatButton("sing-box") { Glyph = "" }.AutoW();
            btnSb.Click += (s, e) => CoreButton(false);
            tt.SetToolTip(btnSb, Tx("Ядро TUN-режима (sing-box)"));
            tt.SetToolTip(btnCore, Tx("Ядро xray"));
            lSrvInfo = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false, AutoEllipsis = true };

            // вкладки: все · рабочие · избранные · вручную · по одной на подписку
            srvTabs = new TabStrip { SelectedKey = st.SrvTab };
            srvTabs.Selected += k => { st.SrvTab = k; st.Save(); srvGrid.ClearSelection(); FillServers(); };
            srvTabs.RightClicked += (k, pt) => SrvTabMenu(k).Show(srvTabs, pt);

            // таблица серверов
            var gridCard = new CardPanel();
            srvGrid = new DataGridView(); Ui.StyleGrid(srvGrid);
            // свои подсказки ячеек вместо встроенных: у DataGridView нельзя задать задержку
            srvGrid.ShowCellToolTips = false;
            srvGrid.CellMouseEnter += (s, e) => { string t = e.RowIndex >= 0 && e.ColumnIndex >= 0 ? srvGrid[e.ColumnIndex, e.RowIndex].ToolTipText : ""; if (tt.GetToolTip(srvGrid) != (t ?? "")) tt.SetToolTip(srvGrid, t); };
            srvGrid.MouseLeave += (s, e) => tt.SetToolTip(srvGrid, "");
            string[][] cols = { new[] { "f", "★" }, new[] { "n", Tx("ИМЯ") }, new[] { "cc", Tx("ГЕО") }, new[] { "t", Tx("ТИП") }, new[] { "a", Tx("АДРЕС") }, new[] { "p", Tx("ПОРТ") },
                                new[] { "g", Tx("ГРУППА") }, new[] { "d", Tx("ЗАДЕРЖКА") }, new[] { "sp", Tx("СКОРОСТЬ") }, new[] { "td", Tx("ВСЕГО ↓") } };
            for (int i = 0; i < cols.Length; i++) srvGrid.Columns.Add(cols[i][0], cols[i][1]);
            srvGrid.AllowUserToOrderColumns = true;
            ApplySrvColumns();
            srvGrid.ColumnWidthChanged += (s, e) => SaveSrvColumns();
            srvGrid.ColumnDisplayIndexChanged += (s, e) => SaveSrvColumns();
            // GEO: флаг вместо кода страны (картинка с flagcdn, кэш в config\runtime\flags)
            srvGrid.CellPainting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex != srvGrid.Columns["cc"].Index) return;
                var x = srvGrid.Rows[e.RowIndex].Tag as ServerEntry; if (x == null) return;
                var img = Flags.Get(x.Country); if (img == null) return;
                e.PaintBackground(e.ClipBounds, (e.State & DataGridViewElementStates.Selected) != 0);
                int h = Math.Min(e.CellBounds.Height - Theme.S(10), Theme.S(14)), w = Math.Min(e.CellBounds.Width - Theme.S(6), h * img.Width / Math.Max(1, img.Height));
                var g = e.Graphics; g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(img, new Rectangle(e.CellBounds.X + (e.CellBounds.Width - w) / 2, e.CellBounds.Y + (e.CellBounds.Height - h) / 2, w, h));
                e.Handled = true;
            };
            Flags.Loaded += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => { if (srvGrid != null && !srvGrid.IsDisposed) srvGrid.InvalidateColumn(srvGrid.Columns["cc"].Index); })); } catch { } };
            srvGrid.ColumnHeaderMouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                var hm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
                hm.Items.Add(Tx("Сбросить вид таблицы"), null, (s2, e2) => ResetSrvColumns());
                hm.Show(srvGrid, srvGrid.PointToClient(Cursor.Position));
            };
            srvGrid.Columns["f"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; srvGrid.Columns["f"].DefaultCellStyle.ForeColor = Theme.Yellow;
            foreach (var c in new[] { "p", "d", "sp", "td" }) srvGrid.Columns[c].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            foreach (var c in new[] { "t", "a", "p", "g", "td" }) srvGrid.Columns[c].DefaultCellStyle.ForeColor = Theme.Muted;
            srvGrid.Columns["cc"].DefaultCellStyle.ForeColor = Theme.Text;
            srvGrid.CellDoubleClick += (s, e) => { if (e.RowIndex < 0) return; var x = srvGrid.Rows[e.RowIndex].Tag as ServerEntry; if (x != null) XConnect(x.Id); };
            srvGrid.SortCompare += (s, e) =>
            {
                string c = e.Column.Name;
                if (c != "d" && c != "sp" && c != "td" && c != "p") return;
                var a = srvGrid.Rows[e.RowIndex1].Tag as ServerEntry; var b = srvGrid.Rows[e.RowIndex2].Tag as ServerEntry;
                if (a == null || b == null) return;
                if (c == "d") { Func<ServerEntry, int> k = x => x.Delay < 0 ? int.MaxValue - (x.Delay == -2 ? 0 : 1) : x.Delay; e.SortResult = k(a).CompareTo(k(b)); }
                else if (c == "sp") e.SortResult = b.Speed.CompareTo(a.Speed); // по убыванию — быстрые сверху
                else if (c == "td") e.SortResult = b.TotalDown.CompareTo(a.TotalDown);
                else e.SortResult = a.Port.CompareTo(b.Port);
                e.Handled = true;
            };
            srvGrid.CellMouseDown += (s, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !srvGrid.Rows[e.RowIndex].Selected) { srvGrid.ClearSelection(); srvGrid.Rows[e.RowIndex].Selected = true; } };
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            cm.Items.Add(Tx("Подключить"), null, (s, e) => { var x = SelServers().FirstOrDefault(); if (x != null) XConnect(x.Id); });
            cm.Items.Add(Tx("Замер выбранных"), null, (s, e) => TestServers(SelServers()));
            cm.Items.Add(Tx("Скорость выбранных"), null, (s, e) => SpeedServers(SelServers()));
            cm.Items.Add(Tx("Избранное"), null, (s, e) => ToggleFav());
            cm.Items.Add(Tx("Копировать ссылку"), null, (s, e) => { var l = string.Join("\r\n", SelServers().Select(x => x.Link)); if (l != "") Clipboard.SetText(l); });
            cm.Items.Add(Tx("Сбросить счётчик трафика"), null, (s, e) => { foreach (var x in SelServers()) { x.TotalDown = 0; x.TotalUp = 0; } ServerStore.Save(); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tx("Удалить"), null, (s, e) => bDel.PerformClick2());
            srvGrid.ContextMenuStrip = cm;
            srvGrid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) bDel.PerformClick2(); if (e.KeyCode == Keys.V && e.Control) bImp.PerformClick2(); };
            gridCard.Controls.Add(srvGrid);
            gridCard.Resize += (s, e) => { srvGrid.SetBounds(Theme.S(8), Theme.S(8), gridCard.Width - Theme.S(16), gridCard.Height - Theme.S(16)); if (string.IsNullOrEmpty(st.SrvCols)) ApplySrvColumns(); };

            // справа: списки приложений, автосвитч, прокси
            var asHost = new ScrollHost { BackColor = Theme.Bg };
            var asBody = asHost.Content;
            int ry = BuildSrvLists(asBody);
            BuildAutoSwitch(asBody, ry);

            p.Controls.AddRange(new Control[] { btnXDpi, btnXConnect, xBoard, bImp, bSubs, btnUpdSub, bTest, bSpeed, bFav, bDel, tbSrvSearch, btnCore, btnSb, srvTabs, lSrvInfo, gridCard, asHost });
            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, gap = Theme.S(12);
                btnXConnect.Location = new Point(p.ClientSize.Width - L - btnXConnect.Width, Theme.S(22));
                btnXDpi.Location = new Point(btnXConnect.Left - Theme.S(8) - btnXDpi.Width, Theme.S(22));
                int y = Theme.S(80);
                xBoard.SetBounds(L, y, W, Theme.S(84)); y = xBoard.Bottom + gap;
                int x = L;
                foreach (var b in new Control[] { bImp, bSubs, btnUpdSub, bTest, bSpeed, bFav, bDel }) { b.Location = new Point(x, y); x = b.Right + Theme.S(6); }
                btnCore.Location = new Point(L + W - btnCore.Width, y);
                btnSb.Location = new Point(btnCore.Left - Theme.S(6) - btnSb.Width, y);
                // строка состояния — в свободном месте панели кнопок; поиск — справа от вкладок, над правой колонкой
                lSrvInfo.SetBounds(x + Theme.S(8), y + (bImp.Height - Theme.S(18)) / 2, Math.Max(0, btnSb.Left - x - Theme.S(16)), Theme.S(18));
                y += bImp.Height + Theme.S(6);
                int rw = Theme.S(360), lw = W - rw - gap;
                srvTabs.SetBounds(L, y, lw, Theme.S(32));
                tbSrvSearch.SetBounds(L + W - rw, y + (Theme.S(32) - tbSrvSearch.Height) / 2, rw, tbSrvSearch.Height); y += Theme.S(36);
                int h = p.ClientSize.Height - Theme.S(18) - y;
                gridCard.SetBounds(L, y, lw, h);
                asHost.SetBounds(L + W - rw, srvTabs.Top + Theme.S(36), rw, p.ClientSize.Height - Theme.S(18) - srvTabs.Top - Theme.S(36)); asHost.Relayout();
            };
            relayServers = lay;
            p.Resize += (s, e) => lay(); lay();
            FillServers();
            UpdateServersUi();
        }

        // ---------- подключение ----------
        void XConnectClick()
        {
            if (xm.Active) { xm.Disconnect(); return; }
            var sel = SelServers().FirstOrDefault();
            ServerEntry pick = sel ?? ServerStore.Find(st.ActiveServer);
            if (pick == null)
            {
                // ничего не выбрано: самый быстрый рабочий на вкладке, иначе первый
                var tab = TabServers();
                pick = tab.Where(x => x.Delay > 0).OrderBy(x => x.Delay).FirstOrDefault() ?? tab.FirstOrDefault() ?? ServerStore.Snapshot().FirstOrDefault();
            }
            if (pick == null) { lSrvInfo.Text = Tx("Нет серверов — добавьте ссылку (Ctrl+V) или подписку"); return; }
            XConnect(pick.Id);
        }

        void XConnect(string id)
        {
            if (!File.Exists(XrayProcess.Exe(st)))
            {
                // без ядра кнопка раньше «ничего не делала» — ставим xray и подключаемся
                pendingXConnect = id;
                UpdateCore(true);
                return;
            }
            StartMonitoringIfProxy();
            xm.Connect(id);
        }
        string pendingXConnect;

        // ---------- списки приложений «Серверов» (компактно, по аналогии с «Подключением») ----------
        int BuildSrvLists(Panel body)
        {
            int w = Theme.S(344);
            var card = new CardPanel { Title = Tx("Приложения") };
            var lTun = new Label { Text = "TUN", AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false };
            tgTun = new Toggle { On = st.TunMode };
            tgTun.Changed += (s, e) => SetTun(tgTun.On);
            srvTip.SetToolTip(tgTun, Tx("TUN: списки работают для всех программ (sing-box). Выкл — только системный прокси"));
            srvTip.SetToolTip(lTun, Tx("TUN: списки работают для всех программ (sing-box). Выкл — только системный прокси"));
            var bPaste = new FlatButton("") { Glyph = "", Width = Theme.S(30), Height = Theme.S(26) };
            var bAdd = new FlatButton("") { Glyph = "", Width = Theme.S(30), Height = Theme.S(26) };
            var bDel = new FlatButton("") { Glyph = "", Width = Theme.S(30), Height = Theme.S(26) };
            srvTip.SetToolTip(bPaste, Tx("Добавить список из буфера (по строкам или через запятую)")); srvTip.SetToolTip(bAdd, Tx("Добавить")); srvTip.SetToolTip(bDel, Tx("Удалить"));
            srvListTabs = new TabStrip { Segmented = true, Font = Theme.Small, Height = Theme.S(26) };
            srvListBox = new ThemedList { MultiSelect = true, ShowIcons = st.ShowAppIcons };
            srvListBox.RightText = n => DomainTag(SrvListKey(), n);
            srvListTabs.Selected += k => { st.SrvListTab = Math.Max(0, Array.IndexOf(SrvListKeys, k)); st.Save(); FillSrvLists(); };
            bAdd.Click += (s, e) =>
            {
                string key = SrvListKey();
                using (var d = new ProcPicker(AppLists.Title(key), AppLists.Recent().Where(r => !AppLists.Get(key).Contains(r, StringComparer.OrdinalIgnoreCase)).ToList()))
                    if (d.ShowDialog(this) == DialogResult.OK) foreach (var x in d.Result) AddToSrvList(key, x);
            };
            bPaste.Click += (s, e) => PasteToList(SrvListKey(), srvListBox);
            bDel.Click += (s, e) => { var sel = srvListBox.SelectedItems; if (sel.Count > 0) RemoveFromList(SrvListKey(), sel); };
            srvListBox.DoubleClick += (s, e) => bAdd.PerformClick2();
            srvListBox.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) bDel.PerformClick2(); if (e.KeyCode == Keys.V && e.Control) bPaste.PerformClick2(); };
            AttachPathMenu(srvListBox, this);
            var miRen = new ToolStripMenuItem(Tx("Переименовать…"));
            miRen.Click += (s, e) => { var old = srvListBox.SelectedItem; if (old != null) RenameInList(SrvListKey(), old); };
            var miDom = new ToolStripMenuItem(Tx("Домены через DPI…"));
            miDom.Click += (s, e) => AddDomainsTo(SrvListKey(), srvListBox.SelectedItem);
            srvListBox.ContextMenuStrip.Opening += (s, e) => miDom.Visible = AppLists.DomKey(SrvListKey()) != null;
            srvListBox.ContextMenuStrip.Items.Add(new ToolStripSeparator());
            srvListBox.ContextMenuStrip.Items.Add(miRen);
            srvListBox.ContextMenuStrip.Items.Add(miDom);
            var miRoute = new ToolStripMenuItem(Tx("Проверить маршрут…"));
            miRoute.Click += (s, e) => ShowRouteFor(srvListBox.SelectedItem);
            srvListBox.ContextMenuStrip.Items.Add(miRoute);
            card.Controls.AddRange(new Control[] { lTun, tgTun, bPaste, bAdd, bDel, srvListTabs, srvListBox });
            int ip = Theme.S(10), lh = Theme.S(130);
            bDel.Location = new Point(w - ip - bDel.Width, Theme.S(10));
            bAdd.Location = new Point(bDel.Left - Theme.S(4) - bAdd.Width, Theme.S(10));
            bPaste.Location = new Point(bAdd.Left - Theme.S(4) - bPaste.Width, Theme.S(10));
            tgTun.Location = new Point(bPaste.Left - Theme.S(10) - tgTun.Width, Theme.S(11));
            lTun.Location = new Point(tgTun.Left - Theme.S(4) - lTun.PreferredWidth, Theme.S(15));
            srvListTabs.SetBounds(ip, Theme.S(46), w - ip * 2, Theme.S(26));
            srvListBox.SetBounds(ip, Theme.S(78), w - ip * 2, lh);
            card.SetBounds(0, 0, w, srvListBox.Bottom + Theme.S(10));
            body.Controls.Add(card);
            FillSrvLists();
            return card.Bottom + Theme.S(12);
        }

        string SrvListKey() { return SrvListKeys[Math.Max(0, Math.Min(SrvListKeys.Length - 1, st.SrvListTab))]; }

        void FillSrvLists()
        {
            if (srvListTabs == null || srvListTabs.IsDisposed) return;
            string[] names = { "Include", "Exclude", "Include DPI", "Exclude DPI" };
            string[] tips = { Tx("Через сервер · пусто = весь трафик"), Tx("Мимо сервера, напрямую"), Tx("Мимо сервера, под DPI — zapret включается сам · ПКМ → домены приложения через DPI, остальное через сервер"), Tx("Мимо DPI") };
            srvListTabs.SelectedKey = SrvListKey();
            srvListTabs.SetTabs(SrvListKeys.Select((k, i) => { int n = AppLists.Apps(k).Count; return new TabStrip.Tab { Key = k, Text = names[i], Badge = n > 0 ? n.ToString() : null, Tip = tips[i] }; }));
            var items = AppLists.Display(SrvListKey());
            srvListBox.BeginUpdate(); srvListBox.Items.Clear(); srvListBox.Items.AddRange(items); srvListBox.EndUpdate();
        }

        // Exclude не совмещается с Include / «через DPI»; «через DPI» и «мимо DPI» — тоже
        void AddToSrvList(string key, string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return;
            item = item.Trim();
            var one = new[] { item };
            // Include и Include DPI не вычищают друг друга: приложение в обоих = через сервер, кроме своих доменов из Include DPI
            if (key == AppLists.SrvExcl) { AppLists.Remove(AppLists.SrvApps, one); AppLists.Remove(AppLists.SrvDpiApps, one); }
            if (key == AppLists.SrvApps || key == AppLists.SrvDpiApps) AppLists.Remove(AppLists.SrvExcl, one);
            if (key == AppLists.SrvDpiApps) AppLists.Remove(AppLists.SrvDpiExcl, one);
            if (key == AppLists.SrvDpiExcl) AppLists.Remove(AppLists.SrvDpiApps, one);
            AppLists.PushRecent(item);
            if (AppLists.Add(key, item)) Logger.Write(LogLevel.Info, AppLists.Title(key) + Tx(": добавлено ") + item);
        }

        // Список приложений из буфера: в любой список «Подключения» или «Серверов»
        void PasteToList(string key, Control near)
        {
            string t = ""; try { t = Clipboard.GetText(); } catch { }
            var items = AppLists.ParseClipboard(t);
            var before = AppLists.Get(key);
            int added = 0;
            foreach (var x in items)
            {
                if (x.Equals(ListCard.BuiltIn, StringComparison.OrdinalIgnoreCase) || before.Contains(x, StringComparer.OrdinalIgnoreCase)) continue;
                if (key == AppLists.DpiDomains) AppLists.Add(key, x);
                else if (AppLists.IsServers(key)) AddToSrvList(key, x);
                else AddToList(key, x);
                added++;
            }
            string msg = items.Count == 0 ? Tx("В буфере нет списка приложений") : Tx("Добавлено из буфера: ") + added + (added < items.Count ? " · " + Tx("уже были: ") + (items.Count - added) : "");
            if (added > 0) Logger.Write(LogLevel.Info, AppLists.Title(key) + ": " + msg);
            if (near != null && !near.IsDisposed) listTip.Show(msg, near, Theme.S(10), Theme.S(10), 2500);
        }

        void SetTun(bool on)
        {
            if (st.TunMode == on) return;
            st.TunMode = on; st.Save();
            if (st.TunMode && !File.Exists(SingBoxProcess.Exe(st))) UpdateSingBoxUi(true);
            if (xm.Active) { if (st.TunMode) SysProxy.Clear(); else if (st.SysProxy) SysProxy.Set("127.0.0.1:" + st.HttpPort); xm.ReapplyTun(); }
            UpdateServersUi();
        }

        void BuildAutoSwitch(Panel body, int y)
        {
            int w = Theme.S(344);
            var card = new CardPanel { Title = Tx("Автосвитч серверов") };
            var tg = new Toggle { On = st.AsEnabled };
            tg.Changed += (s, e) => { st.AsEnabled = tg.On; st.Save(); };
            card.Controls.Add(tg);
            int cy = Theme.S(46), lx = Theme.S(14), nx = w - Theme.S(14) - Theme.S(84);
            Action<string, Control> row = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = false, ForeColor = Theme.Text, BackColor = Theme.Card, UseMnemonic = false, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
                l.SetBounds(lx, cy, nx - lx - Theme.S(6), Theme.S(26));
                if (c is DarkCombo) { l.Width = Theme.S(110); c.SetBounds(lx + Theme.S(112), cy, w - Theme.S(28) - Theme.S(112), Theme.S(26)); }
                else { c.Width = Theme.S(84); c.Location = new Point(nx, cy); }
                card.Controls.Add(l); card.Controls.Add(c);
                cy += Theme.S(32);
            };
            Action<ThemedCheck> chk = c => { c.BackColor = Theme.Card; c.Font = Theme.Small; c.Location = new Point(lx, cy + Theme.S(2)); card.Controls.Add(c); cy += Theme.S(28); };
            Func<int, int, int, Action<int>, Spin> num = (min, max, val, set) => { var n = Ui.Num(min, max, val); n.ValueChanged += (s, e) => { if (!loadingAs) { set((int)n.Value); st.Save(); } }; return n; };

            row(Tx("Проверять каждые, с"), num(3, 600, st.AsInterval, v => st.AsInterval = v));
            row(Tx("Таймаут проверки, с"), num(2, 30, st.AsTimeout, v => st.AsTimeout = v));
            row(Tx("Переключать после сбоев подряд"), num(1, 20, st.AsFails, v => st.AsFails = v));
            row(Tx("Медленно, если больше, мс (0 = выкл)"), num(0, 10000, st.AsMaxLatency, v => st.AsMaxLatency = v));
            row(Tx("…столько проверок подряд"), num(1, 50, st.AsSlowCount, v => st.AsSlowCount = v));
            var cbPool = new DarkCombo(); cbPool.Items.AddRange(new object[] { Tx("Все серверы"), Tx("Та же группа"), Tx("Только избранные") }); cbPool.SelectedIndex = st.AsPool;
            cbPool.SelectedIndexChanged += (s, e) => { st.AsPool = Math.Max(0, cbPool.SelectedIndex); st.Save(); };
            row(Tx("Кандидаты"), cbPool);
            var cbStr = new DarkCombo(); cbStr.Items.AddRange(new object[] { Tx("Самый быстрый"), Tx("Следующий по списку"), Tx("Случайный из 3 лучших") }); cbStr.SelectedIndex = st.AsStrategy;
            cbStr.SelectedIndexChanged += (s, e) => { st.AsStrategy = Math.Max(0, cbStr.SelectedIndex); st.Save(); };
            row(Tx("Выбор"), cbStr);
            var cPre = Ui.Check(Tx("Замерять кандидатов перед переключением"), st.AsPreTest); cPre.CheckedChanged += (s, e) => { st.AsPreTest = cPre.Checked; st.Save(); }; chk(cPre);
            row(Tx("Фоновый замер, мин (0 = выкл)"), num(0, 1440, st.AsBgTestMin, v => st.AsBgTestMin = v));
            var cRet = Ui.Check(Tx("Возвращаться на выбранный вручную сервер"), st.AsReturnPreferred); cRet.CheckedChanged += (s, e) => { st.AsReturnPreferred = cRet.Checked; st.Save(); }; chk(cRet);
            row(Tx("…проверять его каждые, мин"), num(1, 1440, st.AsReturnMin, v => st.AsReturnMin = v));
            row(Tx("Пауза после переключения, с"), num(0, 3600, st.AsCooldown, v => st.AsCooldown = v));
            row(Tx("Не больше переключений в час"), num(1, 120, st.AsMaxPerHour, v => st.AsMaxPerHour = v));
            row(Tx("Не пробовать упавший сервер, мин"), num(0, 1440, st.AsSkipFailedMin, v => st.AsSkipFailedMin = v));
            var cDpi = Ui.Check(Tx("Нет рабочих серверов → включить DPI"), st.AsDpiFallback); cDpi.CheckedChanged += (s, e) => { st.AsDpiFallback = cDpi.Checked; st.Save(); }; chk(cDpi);
            card.SetBounds(0, y, w, cy + Theme.S(8));
            tg.Location = new Point(w - Theme.S(14) - tg.Width, Theme.S(12));
            body.Controls.Add(card);
            y = card.Bottom + Theme.S(12);

            var pc = new CardPanel { Title = Tx("Прокси") };
            cy = Theme.S(46);
            var cSys = Ui.Check(Tx("Системный прокси Windows"), st.SysProxy); cSys.CheckedChanged += (s, e) => { st.SysProxy = cSys.Checked; st.Save(); if (xm.Active && !st.TunMode) { if (st.SysProxy) SysProxy.Set("127.0.0.1:" + st.HttpPort); else SysProxy.Clear(); } };
            var cRu = Ui.Check(Tx("RU-сайты напрямую"), st.BypassRu); cRu.CheckedChanged += (s, e) => { st.BypassRu = cRu.Checked; st.Save(); xm.Reapply(); };
            var cAds = Ui.Check(Tx("Блокировать рекламу"), st.BlockAds); cAds.CheckedChanged += (s, e) => { st.BlockAds = cAds.Checked; st.Save(); xm.Reapply(); };
            var cLan = Ui.Check(Tx("Разрешить подключения из LAN"), st.AllowLan); cLan.CheckedChanged += (s, e) => { st.AllowLan = cLan.Checked; st.Save(); xm.Reapply(); };
            var cAuto = Ui.Check(Tx("Подключаться при запуске"), st.XrayAutoConnect); cAuto.CheckedChanged += (s, e) => { st.XrayAutoConnect = cAuto.Checked; st.Save(); };
            foreach (var c in new[] { cSys, cRu, cAds, cLan, cAuto }) { c.BackColor = Theme.Card; c.Font = Theme.Small; c.Location = new Point(lx, cy); pc.Controls.Add(c); cy += Theme.S(28); }
            Action<string, Control> prow = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = false, ForeColor = Theme.Text, BackColor = Theme.Card, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
                l.SetBounds(lx, cy, Theme.S(120), Theme.S(26)); c.SetBounds(lx + Theme.S(122), cy, w - Theme.S(28) - Theme.S(122), Theme.S(26));
                pc.Controls.Add(l); pc.Controls.Add(c); cy += Theme.S(32);
            };
            var nS = Ui.Num(1024, 65000, st.SocksPort); nS.ValueChanged += (s, e) => { st.SocksPort = (int)nS.Value; st.Save(); xm.Reapply(); };
            var nH = Ui.Num(1024, 65000, st.HttpPort); nH.ValueChanged += (s, e) => { st.HttpPort = (int)nH.Value; st.Save(); xm.Reapply(); };
            var tbUrl = Ui.TextBox(st.TestUrl); tbUrl.TextChanged += (s, e) => { st.TestUrl = tbUrl.Text.Trim(); st.Save(); };
            var tbSpd = Ui.TextBox(st.SpeedUrl); tbSpd.TextChanged += (s, e) => { st.SpeedUrl = tbSpd.Text.Trim(); st.Save(); };
            prow("SOCKS", nS); prow("HTTP", nH); prow(Tx("URL проверки"), tbUrl); prow(Tx("URL скорости"), tbSpd);
            var bResetView = new FlatButton(Tx("Сбросить вид таблицы")) { Glyph = "\uE777" }.AutoW();
            bResetView.Click += (s, e) => ResetSrvColumns();
            srvTip.SetToolTip(bResetView, Tx("Ширина, порядок и видимость колонок — как по умолчанию"));
            var lSec = new Label { Text = Tx("Замер скорости, с"), AutoSize = false, ForeColor = Theme.Text, BackColor = Theme.Card, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
            lSec.SetBounds(lx, cy, Theme.S(200), Theme.S(26));
            var nSec = Ui.Num(3, 60, st.SpeedSec); nSec.Width = Theme.S(84); nSec.Location = new Point(nx, cy);
            nSec.ValueChanged += (s, e) => { st.SpeedSec = (int)nSec.Value; st.Save(); };
            pc.Controls.Add(lSec); pc.Controls.Add(nSec); cy += Theme.S(32);
            bResetView.Location = new Point(lx, cy + Theme.S(2)); pc.Controls.Add(bResetView); cy += bResetView.Height + Theme.S(8);
            pc.SetBounds(0, y, w, cy + Theme.S(8));
            body.Controls.Add(pc);
            body.Height = pc.Bottom + Theme.S(10);
        }

        // мониторинг VPN-канала через прокси xray, если отдельный VPN (WireSock) не подключён
        void StartMonitoringIfProxy() { if (!monVpn.Running) StartMonitoring(); }

        List<ServerEntry> SelServers()
        {
            var l = new List<ServerEntry>();
            if (srvGrid == null) return l;
            foreach (DataGridViewRow r in srvGrid.SelectedRows) { var x = r.Tag as ServerEntry; if (x != null) l.Add(x); }
            return l;
        }

        void ToggleFav() { foreach (var x in SelServers()) x.Favorite = !x.Favorite; ServerStore.Save(); }

        Subscription CurSub() { return srvTabs == null ? null : ServerStore.Subs.FirstOrDefault(x => x.Id == srvTabs.SelectedKey); }

        // серверы открытой вкладки (без учёта поиска)
        List<ServerEntry> TabServers(string key = null)
        {
            key = key ?? (srvTabs != null ? srvTabs.SelectedKey : "all");
            var all = ServerStore.Snapshot();
            switch (key)
            {
                case "all": return all;
                case "active": return all.Where(x => x.Delay > 0).ToList();
                case "fav": return all.Where(x => x.Favorite).ToList();
                case "manual": return all.Where(x => x.Group == "" || !ServerStore.Subs.Any(sb => sb.Id == x.Group)).ToList();
                default: return all.Where(x => x.Group == key).ToList();
            }
        }

        ContextMenuStrip SrvTabMenu(string key)
        {
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            var sub = ServerStore.Subs.FirstOrDefault(x => x.Id == key);
            if (sub != null)
            {
                cm.Items.Add(Tx("Обновить подписку"), null, (s, e) => UpdateSubs(new List<Subscription> { sub }));
                cm.Items.Add(sub.Enabled ? Tx("Не обновлять со всеми") : Tx("Обновлять со всеми"), null, (s, e) => { sub.Enabled = !sub.Enabled; ServerStore.Save(); });
            }
            cm.Items.Add(Tx("Замер"), null, (s, e) => TestServers(TabServers(key)));
            cm.Items.Add(Tx("Скорость"), null, (s, e) => SpeedServers(TabServers(key).Where(x => x.Delay > 0).ToList()));
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tx("Подписки…"), null, (s, e) => { using (var d = new SubsDialog(sub != null ? sub.Id : null)) d.ShowDialog(this); FillServers(); });
            if (sub != null)
                cm.Items.Add(Tx("Удалить подписку"), null, (s, e) =>
                {
                    if (MessageBox.Show(this, Tx("Удалить подписку «") + sub.Name + Tx("» и её серверы?"), "NetProbe", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    ServerStore.Subs.Remove(sub); ServerStore.Remove(ServerStore.OfGroup(sub.Id).Select(x => x.Id));
                });
            return cm;
        }

        void FillSrvTabs()
        {
            var all = ServerStore.Snapshot();
            Func<int, string> n = c => c > 0 ? c.ToString() : null;
            var tabs = new List<TabStrip.Tab>
            {
                new TabStrip.Tab { Key = "all", Text = Tx("Все"), Badge = n(all.Count) },
                new TabStrip.Tab { Key = "active", Text = Tx("Рабочие"), Badge = n(all.Count(x => x.Delay > 0)), Tip = Tx("Ответили на последний замер") },
                new TabStrip.Tab { Key = "fav", Text = Tx("Избранные"), Badge = n(all.Count(x => x.Favorite)) }
            };
            int manual = all.Count(x => x.Group == "" || !ServerStore.Subs.Any(sb => sb.Id == x.Group));
            if (manual > 0 || st.SrvTab == "manual") tabs.Add(new TabStrip.Tab { Key = "manual", Text = Tx("Вручную"), Badge = n(manual), Tip = Tx("Добавлены из буфера") });
            foreach (var sb in ServerStore.Subs)
                tabs.Add(new TabStrip.Tab
                {
                    Key = sb.Id, Text = sb.Name, Badge = n(all.Count(x => x.Group == sb.Id)), Dim = !sb.Enabled,
                    Tip = (sb.Updated == DateTime.MinValue ? Tx("ещё не обновлялась") : Tx("обновлена ") + sb.Updated.ToString("dd.MM HH:mm")) + (sb.Enabled ? "" : " · " + Tx("не обновляется со всеми")) + " · " + Tx("ПКМ — меню")
                });
            srvTabs.SelectedKey = st.SrvTab;
            srvTabs.SetTabs(tabs);
            if (srvTabs.SelectedKey != st.SrvTab) { st.SrvTab = srvTabs.SelectedKey; st.Save(); }
            var cur = CurSub();
            srvTip.SetToolTip(btnUpdSub, cur != null ? Tx("Обновить подписку «") + cur.Name + "»" : Tx("Обновить подписки"));
        }

        static string DelayText(ServerEntry x) { return x.Delay == -2 ? (x.Error != "" && x.Error != Tx("нет ответа через сервер") ? Tx("ошибка") : Tx("таймаут")) : x.Delay < 0 ? "" : x.Delay + Tx(" мс"); }
        static string SpeedText(ServerEntry x) { return x.Speed == -2 ? Tx("ошибка") : x.Speed < 0 ? "" : x.Speed.ToString(x.Speed < 1 ? "0.00" : x.Speed < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " Mbit/s"; }

        // кнопка замера на время работы превращается в «Стоп»
        static int StopW(FlatButton b) { string t = b.Text; b.Text = Tx("Стоп"); int w = b.PreferredW; b.Text = t; return w; }
        void SetBusy(FlatButton b, bool busy)
        {
            if (b == null || b.IsDisposed) return;
            if (b.Tag == null) b.Tag = new[] { b.Text, b.Glyph };
            var n = (string[])b.Tag;
            b.Text = busy ? Tx("Стоп") : n[0]; b.Glyph = busy ? "\uE71A" : n[1]; b.Invalidate();
        }
        static string Bytes(long b)
        {
            if (b <= 0) return "";
            if (b < 1048576) return (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            if (b < 1073741824L) return (b / 1048576.0).ToString(b < 10485760 ? "0.0" : "0", CultureInfo.InvariantCulture) + " MB";
            return (b / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }
        Color DelayColor(ServerEntry x) { return x.Delay == -2 ? Theme.Red : x.Delay < 0 ? Theme.Muted : x.Delay < st.GoodMs * 3 ? Theme.Green : x.Delay < st.WarnMs * 3 ? Theme.Yellow : Theme.Orange; }
        static Color SpeedColor(ServerEntry x) { return x.Speed == -2 ? Theme.Red : x.Speed < 0 ? Theme.Muted : x.Speed >= 50 ? Theme.Green : x.Speed >= 10 ? Theme.Yellow : Theme.Orange; }

        void PaintRow(DataGridViewRow row, ServerEntry x)
        {
            var c = row.Cells;
            c["f"].Value = x.Favorite ? "★" : ""; c["n"].Value = x.Name; c["cc"].Value = x.Country; c["t"].Value = x.Transport; c["a"].Value = x.Address; c["p"].Value = x.Port.ToString();
            c["g"].Value = ServerStore.GroupName(x.Group); c["d"].Value = DelayText(x); c["sp"].Value = x == speedNow ? Tx("замер…") : SpeedText(x); c["td"].Value = Bytes(x.TotalDown);
            c["d"].Style.ForeColor = DelayColor(x); c["sp"].Style.ForeColor = x == speedNow ? Theme.Accent : SpeedColor(x);
            c["sp"].ToolTipText = x.Speed == -2 && x.SpeedErr != "" ? x.SpeedErr : "";
            c["d"].ToolTipText = x.Delay == -2 && x.Error != "" ? x.Error : x.Tested != DateTime.MinValue ? x.Tested.ToString("dd.MM HH:mm") : "";
            c["td"].ToolTipText = x.TotalUp > 0 ? "↑ " + Bytes(x.TotalUp) : "";
            c["cc"].ToolTipText = x.Country;
            bool act = x.Id == st.ActiveServer && xm.Active;
            c["n"].Style.ForeColor = act ? Theme.Accent : Color.Empty; c["n"].Style.Font = act ? Theme.Bold : null;
        }

        void FillServers()
        {
            if (srvGrid == null || srvGrid.IsDisposed) return;
            FillSrvTabs();
            var sel = new HashSet<string>(SelServers().Select(x => x.Id));
            int first = srvGrid.FirstDisplayedScrollingRowIndex;
            string q = tbSrvSearch.Text.Trim();
            Func<string, bool> has = v => (v ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            var list = TabServers().Where(x => q == "" || has(x.Name) || has(x.Address) || has(x.Country) || has(ServerStore.GroupName(x.Group))).ToList();
            var sc = srvGrid.SortedColumn; var so = srvGrid.SortOrder;
            srvGrid.Columns["g"].Visible = CurSub() == null; // на вкладке подписки группа одна и та же
            srvGrid.Rows.Clear();
            foreach (var x in list)
            {
                int i = srvGrid.Rows.Add();
                var row = srvGrid.Rows[i]; row.Tag = x;
                PaintRow(row, x);
                if (sel.Contains(x.Id)) row.Selected = true;
            }
            if (sc != null && so != SortOrder.None) srvGrid.Sort(sc, so == SortOrder.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            if (first >= 0 && first < srvGrid.Rows.Count) try { srvGrid.FirstDisplayedScrollingRowIndex = first; } catch { }
            if (sel.Count == 0) srvGrid.ClearSelection();
            if (!xTesting) lSrvInfo.Text = Tx("Серверов: ") + list.Count + "  ·  " + Tx("двойной клик — подключить, Ctrl+V — импорт");
        }

        void UpdateServersUi()
        {
            if (xBoard == null || xBoard.IsDisposed) return;
            var s = xm.State;
            var cur = ServerStore.Find(st.ActiveServer);
            btnXConnect.Text = xm.Active ? Tx("Отключить") : Tx("Подключить"); btnXConnect.Primary = !xm.Active; btnXConnect.Glyph = xm.Active ? "" : ""; btnXConnect.Invalidate();
            bool dpi = router.ManualDpi || router.Dpi.Running;
            btnXDpi.Primary = dpi; btnXDpi.Text = "DPI  ·  " + (dpi ? Tx("ВКЛ") : Tx("ВЫКЛ")); btnXDpi.Invalidate();
            if (tgTun != null && tgTun.On != st.TunMode) tgTun.On = st.TunMode;
            xBoard.State = XrayManager.StateName(s) + (cur != null && xm.Active ? "  ·  " + cur.Name : "");
            xBoard.Lv = s == XState.Off ? Level.Off : s == XState.Connected ? (xm.LastDelay == -2 ? Level.Slow : Level.Good) : s == XState.AllDown ? Level.Down : Level.Wait;
            string traffic = xm.SessionDown + xm.SessionUp > 0 ? "  ·  ↓ " + Bytes(xm.SessionDown) + " ↑ " + Bytes(xm.SessionUp) : "";
            xBoard.Line1 = xm.Active
                ? (cur != null ? (cur.Country != "" ? cur.Country + " · " : "") + cur.Transport + " · " + cur.Endpoint : "") + "  ·  " + Tx("задержка ") + (xm.LastDelay > 0 ? xm.LastDelay + Tx(" мс") : xm.LastDelay == -2 ? Tx("нет ответа") : "…") + traffic + "  ·  " + Tx("переключений: ") + xm.Switches + (st.AsEnabled ? "" : "  ·  " + Tx("автосвитч выкл"))
                : xm.Detail != "" ? xm.Detail : Tx("Выберите сервер и нажмите «Подключить» (или двойной клик) — работает и без VPN");
            string tun = st.TunMode ? "  ·  TUN" + (xm.Active ? (xm.TunRunning ? " ✓" : " ✗") : "") : st.SysProxy ? "  ·  " + Tx("системный прокси") : "";
            string blocked = st.TunMode && xm.Active && !xm.TunRunning && xm.TunBlocked != null ? xm.TunBlocked() : null;
            xBoard.Line2 = "SOCKS 127.0.0.1:" + st.SocksPort + "  ·  HTTP 127.0.0.1:" + st.HttpPort + tun + (xm.DpiWanted ? "  ·  DPI" : "") + (blocked != null ? "  ·  " + blocked : xm.Detail != "" && s != XState.Connected && xm.Active ? "  ·  " + xm.Detail : "");
            xBoard.Invalidate();
            if (srvGrid != null && !srvGrid.IsDisposed && curPage == PgServers)
            {
                // обновляем строки на месте, без пересборки таблицы (не сбивает прокрутку и выделение)
                foreach (DataGridViewRow r in srvGrid.Rows) { var x = r.Tag as ServerEntry; if (x != null) PaintRow(r, x); }
            }
        }

        void RefreshRow(ServerEntry x)
        {
            foreach (DataGridViewRow r in srvGrid.Rows) if (r.Tag == x) { PaintRow(r, x); break; }
        }

        void TestServers(List<ServerEntry> list)
        {
            if (xTesting) { if (xTestCf != null) xTestCf.Cancelled = true; return; }
            if (list.Count == 0) return;
            if (!File.Exists(XrayProcess.Exe(st))) { UpdateCore(true); return; }
            xTesting = true; xTestCf = new CancelFlag(); SetBusy(btnTest, true);
            int done = 0, total = list.Count;
            lSrvInfo.Text = Tx("Замер: ") + "0/" + total + "…";
            var cf = xTestCf;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DelayTester.TestMany(st, curlPath, list, x =>
                    {
                        int d = Interlocked.Increment(ref done);
                        try { BeginInvoke((Action)(() => { lSrvInfo.Text = Tx("Замер: ") + d + "/" + total + "…"; RefreshRow(x); })); } catch { }
                    }, cf);
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, "xray test: " + ex.Message); }
                ServerStore.Save();
                int ok = list.Count(x => x.Delay > 0);
                if (ok == 0 && total > 0) Logger.Write(LogLevel.Warn, Tx("Замер: ни один сервер не ответил. Проверьте, открывается ли ") + st.TestUrl + Tx(" и не блокирует ли xray антивирус/брандмауэр"));
                try { BeginInvoke((Action)(() => { xTesting = false; SetBusy(btnTest, false); lSrvInfo.Text = (cf.Cancelled ? Tx("Замер остановлен: работают ") : Tx("Замер готов: работают ")) + ok + "/" + total; })); } catch { }
            });
        }

        void SpeedServers(List<ServerEntry> list)
        {
            if (xTesting) { if (xTestCf != null) xTestCf.Cancelled = true; return; }
            if (list == null)
            {
                var tab = TabServers();
                list = tab.Where(x => x.Delay > 0).ToList();
                if (list.Count == 0) list = tab;
            }
            if (list.Count == 0) return;
            if (!File.Exists(XrayProcess.Exe(st))) { UpdateCore(true); return; }
            xTesting = true; xTestCf = new CancelFlag(); SetBusy(btnSpeed, true);
            int done = 0, total = list.Count;
            var cf = xTestCf;
            Func<int, string> left = d => { int m = (total - d) * (st.SpeedSec + 2); return m >= 60 ? "  ·  ≈" + ((m + 59) / 60) + Tx(" мин") : ""; };
            lSrvInfo.Text = Tx("Скорость: ") + "0/" + total + left(0);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    DelayTester.SpeedMany(st, curlPath, list, x =>
                    {
                        int d = done;
                        try { BeginInvoke((Action)(() => { speedNow = x; RefreshRow(x); lSrvInfo.Text = Tx("Скорость: ") + (d + 1) + "/" + total + "  ·  " + x.Name + left(d); })); } catch { }
                    }, x =>
                    {
                        Interlocked.Increment(ref done);
                        try { BeginInvoke((Action)(() => { if (speedNow == x) speedNow = null; RefreshRow(x); })); } catch { }
                    }, cf);
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, "xray speed: " + ex.Message); }
                ServerStore.Save();
                var best = list.Where(x => x.Speed > 0).OrderByDescending(x => x.Speed).FirstOrDefault();
                int ok = list.Count(x => x.Speed > 0);
                var why = list.Where(x => x.Speed == -2 && x.SpeedErr != "").GroupBy(x => x.SpeedErr).OrderByDescending(g => g.Count()).FirstOrDefault();
                string res = (cf.Cancelled ? Tx("Скорость остановлена") : Tx("Скорость готова")) + ": " + ok + "/" + total
                    + (best != null ? "  ·  " + Tx("лучший: ") + best.Name + " " + SpeedText(best) : "")
                    + (ok == 0 && why != null ? "  ·  " + why.Key : "");
                try { BeginInvoke((Action)(() => { var was = speedNow; speedNow = null; if (was != null) RefreshRow(was); xTesting = false; SetBusy(btnSpeed, false); lSrvInfo.Text = res; })); } catch { }
            });
        }

        // subs = null — все включённые подписки; иначе только эти (одну с вкладки)
        void UpdateSubs(List<Subscription> subs)
        {
            if (ServerStore.Subs.Count == 0) { using (var d = new SubsDialog()) d.ShowDialog(this); FillServers(); return; }
            var todo = subs ?? ServerStore.Subs.Where(s => s.Enabled).ToList();
            if (todo.Count == 0) { lSrvInfo.Text = Tx("Нет включённых подписок"); return; }
            lSrvInfo.Text = (todo.Count == 1 ? Tx("Обновление подписки «") + todo[0].Name + "»…" : Tx("Обновление подписок…"));
            btnUpdSub.Enabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var rep = todo.Select(ServerStore.UpdateSub).ToList();
                Logger.Write(LogLevel.Info, Tx("Подписки: ") + string.Join(" · ", rep));
                try { BeginInvoke((Action)(() => { btnUpdSub.Enabled = true; FillServers(); lSrvInfo.Text = Tx("Подписки: ") + string.Join(" · ", rep); })); } catch { }
            });
        }

        void UpdateCore(bool ask)
        {
            string cur = Updater.XrayVersion(st);
            if (ask && MessageBox.Show(this, (cur == "" ? Tx("Ядро xray не установлено.") : "xray " + cur + ".") + "\r\n" + Tx("Скачать последнюю версию с GitHub (XTLS/Xray-core)?"), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { pendingXConnect = null; return; }
            btnCore.Enabled = false;
            bool was = xm.Active; string id = pendingXConnect ?? st.ActiveServer; bool connect = was || pendingXConnect != null; pendingXConnect = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string res;
                try
                {
                    if (was) xm.ShutdownSync();
                    res = Updater.UpdateXray(st, m => { try { BeginInvoke((Action)(() => lSrvInfo.Text = m)); } catch { } });
                }
                catch (Exception ex) { res = Tx("Ошибка обновления: ") + ex.Message; }
                Logger.Write(LogLevel.Info, res);
                try { BeginInvoke((Action)(() => { btnCore.Enabled = true; lSrvInfo.Text = res; UpdateCoreLabel(); CheckUpdates(true); if (connect && File.Exists(XrayProcess.Exe(st))) { StartMonitoringIfProxy(); xm.Connect(id); } })); } catch { }
            });
        }

        Action relayServers;
        string xrayLatest, sbLatest;

        void UpdateSingBoxUi(bool ask)
        {
            string cur = Updater.SingBoxVersion(st);
            if (ask && MessageBox.Show(this, (cur == "" ? Tx("Ядро TUN (sing-box) не установлено.") : "sing-box " + cur + ".") + "\r\n" + Tx("Скачать последнюю версию с GitHub (SagerNet/sing-box)?"), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            btnSb.Enabled = false;
            bool was = xm.Active; string id = st.ActiveServer;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string res;
                try { if (was) xm.ShutdownSync(); res = Updater.UpdateSingBox(st, m => { try { BeginInvoke((Action)(() => lSrvInfo.Text = m)); } catch { } }); }
                catch (Exception ex) { res = Tx("Ошибка обновления: ") + ex.Message; }
                Logger.Write(LogLevel.Info, res);
                try { BeginInvoke((Action)(() => { btnSb.Enabled = true; lSrvInfo.Text = res; UpdateCoreLabel(); if (was) xm.Connect(id); })); } catch { }
            });
        }

        void UpdateCoreLabel()
        {
            if (btnCore == null || btnCore.IsDisposed) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string v = Updater.XrayVersion(st), sv = Updater.SingBoxVersion(st);
                try { BeginInvoke((Action)(() =>
                {
                    if (btnCore.IsDisposed) return;
                    // не установлено — «Установить» со значком загрузки; установлено — версия и «проверить обновления»
                    bool supd = sv != "" && sbLatest != null && sbLatest.TrimStart('v') != sv.TrimStart('v');
                    btnSb.Text = sv == "" ? Tx("Установить sing-box") : "sing-box " + sv + (supd ? " → " + sbLatest : "");
                    btnSb.Glyph = sv == "" || supd ? "\uE896" : "\uE72C"; btnSb.Primary = supd;
                    srvTip.SetToolTip(btnSb, sv == "" ? Tx("Ядро TUN-режима (sing-box)") : Tx("Проверить обновления sing-box"));
                    btnSb.AutoW(); btnSb.Invalidate();
                    bool upd = v != "" && xrayLatest != null && xrayLatest.TrimStart('v') != v.TrimStart('v');
                    btnCore.Text = v == "" ? Tx("Установить xray") : "xray " + v + (upd ? " → " + xrayLatest : "");
                    btnCore.Glyph = v == "" || upd ? "\uE896" : "\uE72C"; btnCore.Primary = upd;
                    srvTip.SetToolTip(btnCore, v == "" ? Tx("Скачать ядро xray с GitHub") : Tx("Проверить обновления xray"));
                    btnCore.AutoW(); btnCore.Invalidate();
                    if (relayServers != null) relayServers();
                })); } catch { }
            });
        }

        // Кнопки ядер: не установлено — установить; установлено — проверить новую версию и предложить обновить
        void CoreButton(bool xray)
        {
            string cur = xray ? Updater.XrayVersion(st) : Updater.SingBoxVersion(st);
            if (cur == "") { if (xray) UpdateCore(true); else UpdateSingBoxUi(true); return; }
            string name = xray ? "xray" : "sing-box";
            var b = xray ? btnCore : btnSb;
            b.Enabled = false;
            lSrvInfo.Text = Tx("Проверка обновлений ") + name + "…";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string lat = null, err = null;
                try { lat = xray ? Updater.Latest(Updater.XrayRepo, n => n.StartsWith("Xray-windows", StringComparison.OrdinalIgnoreCase)).Tag : Updater.SingBoxLatest().Tag; }
                catch (Exception ex) { err = ex.Message; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        b.Enabled = true;
                        if (lat == null) { lSrvInfo.Text = Tx("Не удалось проверить обновления: ") + err; return; }
                        if (xray) xrayLatest = lat; else sbLatest = lat;
                        UpdateCoreLabel();
                        if (lat.TrimStart('v') == cur.TrimStart('v')) { lSrvInfo.Text = name + " " + cur + " — " + Tx("последняя версия"); return; }
                        lSrvInfo.Text = name + ": " + Tx("доступна ") + lat;
                        if (MessageBox.Show(this, name + " " + cur + " → " + lat + "\r\n" + Tx("Скачать и установить новую версию?"), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        { if (xray) UpdateCore(false); else UpdateSingBoxUi(false); }
                    }));
                }
                catch { }
            });
        }

        // ---------- колонки таблицы серверов: ширина и порядок запоминаются, сброс — в «Прокси» и по ПКМ на заголовке ----------
        // Все колонки фиксированной ширины (как в Проводнике): тянешь одну — меняется только она, остальные просто сдвигаются,
        // не хватает места — появляется горизонтальная прокрутка. «Резиновые» колонки (Fill) отбирали ширину у соседей.
        static readonly string[] SrvColNames = { "f", "n", "cc", "t", "a", "p", "g", "d", "sp", "td" };
        static readonly int[] SrvColWidth = { 24, 220, 52, 120, 140, 64, 100, 72, 86, 74 };
        static readonly int[] SrvColMin = { 24, 36, 52, 36, 36, 64, 36, 36, 36, 36 }; // GEO и PORT уже не сжать — иначе «GEC», «308…»
        const string SrvColsVer = "w|"; // старый формат (веса Fill) не подходит — игнорируется
        bool applyingCols;

        void ApplySrvColumns()
        {
            applyingCols = true;
            try
            {
                srvGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                var saved = new Dictionary<string, string[]>();
                string raw = st.SrvCols ?? "";
                if (raw.StartsWith(SrvColsVer))
                    foreach (var part in raw.Substring(SrvColsVer.Length).Split(';'))
                    {
                        var f = part.Split(':');
                        if (f.Length == 3) saved[f[0]] = f;
                    }
                for (int i = 0; i < SrvColNames.Length; i++)
                {
                    var col = srvGrid.Columns[SrvColNames[i]];
                    col.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
                    // без минимальной ширины колонку можно сжать до невидимой
                    col.MinimumWidth = Theme.S(SrvColMin[i]);
                    string[] sv; int w;
                    bool has = saved.TryGetValue(SrvColNames[i], out sv) && int.TryParse(sv[1], out w) && w > 0;
                    col.Width = Math.Max(col.MinimumWidth, has ? int.Parse(sv[1]) : Theme.S(SrvColWidth[i]));
                }
                // пока вид не настраивали — имя забирает свободное место, чтобы таблица была во всю ширину
                if (saved.Count == 0 && srvGrid.ClientSize.Width > 0)
                {
                    var name = srvGrid.Columns["n"];
                    int others = srvGrid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible && c != name).Sum(c => c.Width);
                    name.Width = Math.Max(Theme.S(160), srvGrid.ClientSize.Width - others - SystemInformation.VerticalScrollBarWidth - 2);
                }
                // порядок колонок
                var order = SrvColNames.Select((n, i) => { string[] sv; int d; return Tuple.Create(n, saved.TryGetValue(n, out sv) && int.TryParse(sv[2], out d) ? d : i); }).OrderBy(t => t.Item2).ToList();
                for (int k = 0; k < order.Count; k++) srvGrid.Columns[order[k].Item1].DisplayIndex = k;
            }
            finally { applyingCols = false; }
        }

        void SaveSrvColumns()
        {
            if (applyingCols || srvGrid == null) return;
            string v0 = SrvColsVer + string.Join(";", SrvColNames.Select(n => { var c = srvGrid.Columns[n]; return n + ":" + c.Width + ":" + c.DisplayIndex; }));
            if (v0 == st.SrvCols) return;
            st.SrvCols = v0; st.Save();
        }

        void ResetSrvColumns()
        {
            st.SrvCols = ""; st.Save();
            if (srvGrid != null && !srvGrid.IsDisposed) { ApplySrvColumns(); srvGrid.Invalidate(); }
            Logger.Write(LogLevel.Info, Tx("Вид таблицы серверов сброшен"));
        }
    }

    // Подписки: имя + URL, включение, обновление
    public class SubsDialog : Form
    {
        readonly ThemedList list = new ThemedList();
        readonly TextBox tbName = Ui.TextBox(), tbUrl = Ui.TextBox();
        readonly Label info = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent };

        public SubsDialog(string selectId = null)
        {
            Text = Tx("Подписки"); StartPosition = FormStartPosition.CenterParent; Size = new Size(Theme.S(620), Theme.S(440)); BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            MinimizeBox = false; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
            var card = new CardPanel(); card.Controls.Add(list);
            card.Resize += (s, e) => list.SetBounds(Theme.S(6), Theme.S(6), card.Width - Theme.S(12), card.Height - Theme.S(12));
            list.RightText = n => { var sb = ServerStore.Subs.FirstOrDefault(x => x.Name == n); return sb == null ? null : (sb.Enabled ? "" : Tx("выкл") + "  ") + ServerStore.Snapshot().Count(x => x.Group == sb.Id); };
            list.SelectedIndexChanged += (s, e) => { var sb = Cur(); if (sb != null) { tbName.Text = sb.Name; tbUrl.Text = sb.Url; } };
            var lN = Ui.Label(Tx("Имя"), Theme.Small, Theme.Muted); var lU = Ui.Label("URL", Theme.Small, Theme.Muted);
            var bAdd = new FlatButton(Tx("Добавить"), true) { Glyph = "\uE710" }.AutoW();
            bAdd.Click += (s, e) =>
            {
                if (!tbUrl.Text.Trim().StartsWith("http")) { info.Text = Tx("Нужна ссылка http(s)://"); return; }
                var sb = Cur();
                if (sb != null && sb.Name == tbName.Text.Trim()) { sb.Url = tbUrl.Text.Trim(); }
                else ServerStore.Subs.Add(new Subscription { Id = ServerEntry.MakeId(tbUrl.Text.Trim() + DateTime.Now.Ticks), Name = tbName.Text.Trim() == "" ? "sub" + (ServerStore.Subs.Count + 1) : tbName.Text.Trim(), Url = tbUrl.Text.Trim() });
                ServerStore.Save(); Fill();
                var add = ServerStore.Subs.Last();
                info.Text = Tx("Загрузка…"); Refresh();
                info.Text = ServerStore.UpdateSub(sb ?? add); Fill();
            };
            var bToggle = new FlatButton(Tx("Вкл/выкл")).AutoW(); bToggle.Click += (s, e) => { var sb = Cur(); if (sb != null) { sb.Enabled = !sb.Enabled; ServerStore.Save(); Fill(); } };
            var bUpd = new FlatButton(Tx("Обновить")) { Glyph = "\uE72C" }.AutoW(); bUpd.Click += (s, e) => { var sb = Cur(); if (sb != null) { info.Text = Tx("Загрузка…"); Refresh(); info.Text = ServerStore.UpdateSub(sb); Fill(); } };
            var bDel = new FlatButton(Tx("Удалить")) { Glyph = "\uE74D", Danger = true }.AutoW();
            bDel.Click += (s, e) => { var sb = Cur(); if (sb == null) return; ServerStore.Subs.Remove(sb); ServerStore.Remove(ServerStore.Snapshot().Where(x => x.Group == sb.Id).Select(x => x.Id)); Fill(); };
            Controls.AddRange(new Control[] { card, lN, tbName, lU, tbUrl, bAdd, bToggle, bUpd, bDel, info });
            Action lay = () =>
            {
                int p = Theme.S(14), W = ClientSize.Width - p * 2;
                int by = ClientSize.Height - p - bAdd.Height;
                bAdd.Location = new Point(p, by); bToggle.Location = new Point(bAdd.Right + Theme.S(6), by); bUpd.Location = new Point(bToggle.Right + Theme.S(6), by); bDel.Location = new Point(ClientSize.Width - p - bDel.Width, by);
                info.SetBounds(p, by - Theme.S(24), W, Theme.S(18));
                tbUrl.SetBounds(p + Theme.S(50), info.Top - Theme.S(34), W - Theme.S(50), tbUrl.Height); lU.Location = new Point(p, tbUrl.Top + Theme.S(4));
                tbName.SetBounds(p + Theme.S(50), tbUrl.Top - Theme.S(34), W - Theme.S(50), tbName.Height); lN.Location = new Point(p, tbName.Top + Theme.S(4));
                card.SetBounds(p, p, W, tbName.Top - Theme.S(10) - p);
            };
            Resize += (s, e) => lay(); lay();
            Fill();
            var pre = ServerStore.Subs.FirstOrDefault(x => x.Id == selectId);
            if (pre != null) list.SelectedItem = pre.Name;
        }
        Subscription Cur() { var n = list.SelectedItem; return n == null ? null : ServerStore.Subs.FirstOrDefault(x => x.Name == n); }
        void Fill() { var sel = list.SelectedItem; list.Items.Clear(); list.Items.AddRange(ServerStore.Subs.Select(x => x.Name)); list.EndUpdate(); if (sel != null && list.Items.Contains(sel)) list.SelectedItem = sel; }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); }
    }
}
