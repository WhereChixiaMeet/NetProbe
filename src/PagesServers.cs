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
        StatusBoard xBoard; FlatButton btnXConnect, btnCore; DataGridView srvGrid; TextBox tbSrvSearch; Label lSrvInfo;
        bool loadingAs, xTesting; CancelFlag xTestCf; FlatButton btnSb;
        readonly System.Windows.Forms.Timer tunReapply = new System.Windows.Forms.Timer { Interval = 1200 };

        void InitXray()
        {
            ServerStore.Load();
            SysProxy.ClearIfStale(st.HttpPort);
            xm.GetSettings = () => st;
            xm.GetCurl = () => curlPath;
            XState lastX = XState.Off;
            xm.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => { if ((xm.State == XState.Off) != (lastX == XState.Off)) RebuildProbers(); lastX = xm.State; UpdateServersUi(); UpdateSide(); })); } catch { } };
            xm.DpiFallback = on => router.StandaloneDpi(on);
            // TUN берёт списки текущего режима с вкладки «Подключение»: Include + Strict VPN → сервер, Exclude → напрямую
            xm.GetTunLists = () =>
            {
                var inc = AppLists.Get(AppLists.HybridApps).Concat(AppLists.Get(AppLists.VpnOnlyApps)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var exc = st.RMode == RouteMode.Hybrid ? AppLists.Get(AppLists.HybridExcl) : AppLists.Get(AppLists.VpnOnlyExcl);
                return Tuple.Create(inc, exc);
            };
            xm.TunBlocked = () => router.Wg.Running ? Tx("TUN не запущен: работает туннель WireSock (вкладка «Подключение»)") : null;
            AppLists.Changed += key => { if (xm.Active && st.TunMode) { tunReapply.Stop(); tunReapply.Start(); } };
            tunReapply.Tick += (s, e) => { tunReapply.Stop(); xm.ReapplyTun(); };
            ServerStore.Changed += () => { if (IsHandleCreated && !IsDisposed) try { BeginInvoke((Action)(() => FillServers())); } catch { } };
        }

        void BuildServersPage()
        {
            var p = NewPage(Tx("Серверы"), Tx("xray · подписки · автосвитч"));
            var tt = new ToolTip();
            btnXConnect = new FlatButton(Tx("Подключить"), true) { Glyph = "", Width = Theme.S(170), Height = Theme.S(36) };
            btnXConnect.Click += (s, e) =>
            {
                if (xm.Active) { xm.Disconnect(); return; }
                var sel = SelServers().FirstOrDefault();
                string id = sel != null ? sel.Id : st.ActiveServer;
                if (ServerStore.Find(id) == null) { lSrvInfo.Text = Tx("Сервер не выбран"); return; }
                StartMonitoringIfProxy();
                xm.Connect(id);
            };
            xBoard = new StatusBoard();

            // панель кнопок
            var bImp = new FlatButton(Tx("Из буфера")) { Glyph = "" }.AutoW();
            bImp.Click += (s, e) =>
            {
                string t = ""; try { t = Clipboard.GetText(); } catch { }
                var list = ServerStore.ParseMany(t);
                int n = ServerStore.AddMany(list, "");
                lSrvInfo.Text = Tx("Добавлено серверов: ") + n + (list.Count == 0 ? " · " + Tx("в буфере нет ссылок vless/vmess/trojan/ss") : "");
            };
            var bSubs = new FlatButton(Tx("Подписки")) { Glyph = "" }.AutoW();
            bSubs.Click += (s, e) => { using (var d = new SubsDialog()) d.ShowDialog(this); FillServers(); };
            var bUpd = IconBtn("\uE72C", Tx("Обновить подписки"), tt);
            bUpd.Click += (s, e) => UpdateSubs();
            var bTest = new FlatButton(Tx("Замер")) { Glyph = "" }.AutoW();
            bTest.Click += (s, e) => TestServers(SelServers().Count > 1 ? SelServers() : ServerStore.Snapshot());
            var bFav = IconBtn("", Tx("Избранное"), tt);
            bFav.Click += (s, e) => { foreach (var x in SelServers()) x.Favorite = !x.Favorite; ServerStore.Save(); };
            var bDel = IconBtn("", Tx("Удалить"), tt);
            bDel.Click += (s, e) => { var sel = SelServers(); if (sel.Count > 0 && MessageBox.Show(this, Tx("Удалить серверов: ") + sel.Count + "?", "NetProbe", MessageBoxButtons.YesNo) == DialogResult.Yes) ServerStore.Remove(sel.Select(x => x.Id)); };
            tbSrvSearch = Ui.TextBox(); tbSrvSearch.Width = Theme.S(180); ((ThemedTextBox)tbSrvSearch).Cue = Tx("Поиск");
            tbSrvSearch.TextChanged += (s, e) => FillServers();
            btnCore = new FlatButton(Tx("Ядро xray")) { Glyph = "" }.AutoW();
            btnCore.Click += (s, e) => UpdateCore(true);
            btnSb = new FlatButton("sing-box") { Glyph = "\uE896" }.AutoW();
            btnSb.Click += (s, e) => UpdateSingBoxUi(true);
            tt.SetToolTip(btnSb, Tx("Ядро TUN-режима (sing-box)"));
            lSrvInfo = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false, AutoEllipsis = true };

            // таблица серверов
            var gridCard = new CardPanel();
            srvGrid = new DataGridView(); Ui.StyleGrid(srvGrid);
            srvGrid.Columns.Add("f", "★"); srvGrid.Columns.Add("n", Tx("ИМЯ")); srvGrid.Columns.Add("t", Tx("ТИП")); srvGrid.Columns.Add("a", Tx("АДРЕС")); srvGrid.Columns.Add("g", Tx("ГРУППА")); srvGrid.Columns.Add("d", Tx("ЗАДЕРЖКА"));
            float[] fw = { 4, 32, 15, 23, 12, 14 };
            for (int i = 0; i < fw.Length; i++) srvGrid.Columns[i].FillWeight = fw[i];
            srvGrid.Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter; srvGrid.Columns[0].DefaultCellStyle.ForeColor = Theme.Yellow;
            srvGrid.Columns[5].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            srvGrid.Columns[2].DefaultCellStyle.ForeColor = Theme.Muted; srvGrid.Columns[3].DefaultCellStyle.ForeColor = Theme.Muted; srvGrid.Columns[4].DefaultCellStyle.ForeColor = Theme.Muted;
            srvGrid.CellDoubleClick += (s, e) => { if (e.RowIndex < 0) return; var x = srvGrid.Rows[e.RowIndex].Tag as ServerEntry; if (x == null) return; StartMonitoringIfProxy(); xm.Connect(x.Id); };
            srvGrid.SortCompare += (s, e) =>
            {
                if (e.Column.Index != 5) return;
                var a = (srvGrid.Rows[e.RowIndex1].Tag as ServerEntry); var b = (srvGrid.Rows[e.RowIndex2].Tag as ServerEntry);
                Func<ServerEntry, int> k = x => x == null || x.Delay < 0 ? int.MaxValue - (x != null && x.Delay == -2 ? 0 : 1) : x.Delay;
                e.SortResult = k(a).CompareTo(k(b)); e.Handled = true;
            };
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            cm.Items.Add(Tx("Подключить"), null, (s, e) => { var x = SelServers().FirstOrDefault(); if (x != null) { StartMonitoringIfProxy(); xm.Connect(x.Id); } });
            cm.Items.Add(Tx("Замер выбранных"), null, (s, e) => TestServers(SelServers()));
            cm.Items.Add(Tx("Избранное"), null, (s, e) => { foreach (var x in SelServers()) x.Favorite = !x.Favorite; ServerStore.Save(); });
            cm.Items.Add(Tx("Копировать ссылку"), null, (s, e) => { var l = string.Join("\r\n", SelServers().Select(x => x.Link)); if (l != "") Clipboard.SetText(l); });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tx("Удалить"), null, (s, e) => bDel.PerformClick2());
            srvGrid.ContextMenuStrip = cm;
            srvGrid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) bDel.PerformClick2(); if (e.KeyCode == Keys.V && e.Control) bImp.PerformClick2(); };
            gridCard.Controls.Add(srvGrid);
            gridCard.Resize += (s, e) => srvGrid.SetBounds(Theme.S(8), Theme.S(8), gridCard.Width - Theme.S(16), gridCard.Height - Theme.S(16));

            // автосвитч и прокси
            var asHost = new ScrollHost { BackColor = Theme.Bg };
            var asBody = asHost.Content;
            BuildAutoSwitch(asBody);

            p.Controls.AddRange(new Control[] { btnXConnect, xBoard, bImp, bSubs, bUpd, bTest, bFav, bDel, tbSrvSearch, btnCore, btnSb, lSrvInfo, gridCard, asHost });
            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, gap = Theme.S(12);
                btnXConnect.Location = new Point(p.ClientSize.Width - L - btnXConnect.Width, Theme.S(22));
                int y = Theme.S(80);
                xBoard.SetBounds(L, y, W, Theme.S(84)); y = xBoard.Bottom + gap;
                int x = L;
                foreach (var b in new Control[] { bImp, bSubs, bUpd, bTest, bFav, bDel }) { b.Location = new Point(x, y); x = b.Right + Theme.S(6); }
                btnCore.Location = new Point(L + W - btnCore.Width, y);
                btnSb.Location = new Point(btnCore.Left - Theme.S(6) - btnSb.Width, y);
                tbSrvSearch.SetBounds(x + Theme.S(6), y + (bImp.Height - tbSrvSearch.Height) / 2, Math.Max(Theme.S(60), btnSb.Left - x - Theme.S(18)), tbSrvSearch.Height);
                y += bImp.Height + Theme.S(6);
                lSrvInfo.SetBounds(L, y, W, Theme.S(18)); y += Theme.S(22);
                int rw = Theme.S(360), h = p.ClientSize.Height - Theme.S(18) - y;
                gridCard.SetBounds(L, y, W - rw - gap, h);
                asHost.SetBounds(L + W - rw, y, rw, h); asHost.Relayout();
            };
            relayServers = lay;
            p.Resize += (s, e) => lay(); lay();
            FillServers();
            UpdateServersUi();
        }

        void BuildAutoSwitch(Panel body)
        {
            int y = 0, w = Theme.S(344);
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
            var cTun = Ui.Check(Tx("TUN: приложения из Include через сервер"), st.TunMode);
            cTun.CheckedChanged += (s, e) =>
            {
                st.TunMode = cTun.Checked; st.Save();
                if (st.TunMode && !File.Exists(SingBoxProcess.Exe(st))) UpdateSingBoxUi(true);
                if (xm.Active) { if (st.TunMode) SysProxy.Clear(); else if (st.SysProxy) SysProxy.Set("127.0.0.1:" + st.HttpPort); xm.ReapplyTun(); }
            };
            var cSys = Ui.Check(Tx("Системный прокси Windows"), st.SysProxy); cSys.CheckedChanged += (s, e) => { st.SysProxy = cSys.Checked; st.Save(); if (xm.Active && !st.TunMode) { if (st.SysProxy) SysProxy.Set("127.0.0.1:" + st.HttpPort); else SysProxy.Clear(); } };
            var cRu = Ui.Check(Tx("RU-сайты напрямую"), st.BypassRu); cRu.CheckedChanged += (s, e) => { st.BypassRu = cRu.Checked; st.Save(); xm.Reapply(); };
            var cAds = Ui.Check(Tx("Блокировать рекламу"), st.BlockAds); cAds.CheckedChanged += (s, e) => { st.BlockAds = cAds.Checked; st.Save(); xm.Reapply(); };
            var cLan = Ui.Check(Tx("Разрешить подключения из LAN"), st.AllowLan); cLan.CheckedChanged += (s, e) => { st.AllowLan = cLan.Checked; st.Save(); xm.Reapply(); };
            var cAuto = Ui.Check(Tx("Подключаться при запуске"), st.XrayAutoConnect); cAuto.CheckedChanged += (s, e) => { st.XrayAutoConnect = cAuto.Checked; st.Save(); };
            foreach (var c in new[] { cTun, cSys, cRu, cAds, cLan, cAuto }) { c.BackColor = Theme.Card; c.Font = Theme.Small; c.Location = new Point(lx, cy); pc.Controls.Add(c); cy += Theme.S(28); }
            Action<string, Control> prow = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = false, ForeColor = Theme.Text, BackColor = Theme.Card, Font = Theme.Small, TextAlign = ContentAlignment.MiddleLeft };
                l.SetBounds(lx, cy, Theme.S(120), Theme.S(26)); c.SetBounds(lx + Theme.S(122), cy, w - Theme.S(28) - Theme.S(122), Theme.S(26));
                pc.Controls.Add(l); pc.Controls.Add(c); cy += Theme.S(32);
            };
            var nS = Ui.Num(1024, 65000, st.SocksPort); nS.ValueChanged += (s, e) => { st.SocksPort = (int)nS.Value; st.Save(); xm.Reapply(); };
            var nH = Ui.Num(1024, 65000, st.HttpPort); nH.ValueChanged += (s, e) => { st.HttpPort = (int)nH.Value; st.Save(); xm.Reapply(); };
            var tbUrl = Ui.TextBox(st.TestUrl); tbUrl.TextChanged += (s, e) => { st.TestUrl = tbUrl.Text.Trim(); st.Save(); };
            prow("SOCKS", nS); prow("HTTP", nH); prow(Tx("URL проверки"), tbUrl);
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

        static string DelayText(ServerEntry x) { return x.Delay == -2 ? Tx("нет") : x.Delay < 0 ? "" : x.Delay + Tx(" мс"); }

        void FillServers()
        {
            if (srvGrid == null || srvGrid.IsDisposed) return;
            var sel = new HashSet<string>(SelServers().Select(x => x.Id));
            int first = srvGrid.FirstDisplayedScrollingRowIndex;
            string q = tbSrvSearch.Text.Trim();
            var list = ServerStore.Snapshot().Where(x => q == "" || x.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || x.Address.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || ServerStore.GroupName(x.Group).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var sc = srvGrid.SortedColumn; var so = srvGrid.SortOrder;
            srvGrid.Rows.Clear();
            foreach (var x in list)
            {
                int i = srvGrid.Rows.Add(x.Favorite ? "★" : "", x.Name, x.Transport, x.Endpoint, ServerStore.GroupName(x.Group), DelayText(x));
                var row = srvGrid.Rows[i]; row.Tag = x;
                if (x.Id == st.ActiveServer && xm.Active) { row.Cells[1].Style.ForeColor = Theme.Accent; row.Cells[1].Style.Font = Theme.Bold; }
                row.Cells[5].Style.ForeColor = x.Delay == -2 ? Theme.Red : x.Delay < 0 ? Theme.Muted : x.Delay < st.GoodMs * 3 ? Theme.Green : x.Delay < st.WarnMs * 3 ? Theme.Yellow : Theme.Orange;
                if (sel.Contains(x.Id)) row.Selected = true;
            }
            if (sc != null && so != SortOrder.None) srvGrid.Sort(sc, so == SortOrder.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            if (first >= 0 && first < srvGrid.Rows.Count) try { srvGrid.FirstDisplayedScrollingRowIndex = first; } catch { }
            if (sel.Count == 0) srvGrid.ClearSelection();
            if (!xTesting) lSrvInfo.Text = Tx("Серверов: ") + list.Count + (ServerStore.Subs.Count > 0 ? "  ·  " + Tx("подписок: ") + ServerStore.Subs.Count : "") + "  ·  " + Tx("двойной клик — подключить, Ctrl+V — импорт");
        }

        void UpdateServersUi()
        {
            if (xBoard == null || xBoard.IsDisposed) return;
            var s = xm.State;
            var cur = ServerStore.Find(st.ActiveServer);
            btnXConnect.Text = xm.Active ? Tx("Отключить") : Tx("Подключить"); btnXConnect.Primary = !xm.Active; btnXConnect.Glyph = xm.Active ? "" : ""; btnXConnect.Invalidate();
            xBoard.State = XrayManager.StateName(s) + (cur != null && xm.Active ? "  ·  " + cur.Name : "");
            xBoard.Lv = s == XState.Off ? Level.Off : s == XState.Connected ? (xm.LastDelay == -2 ? Level.Slow : Level.Good) : s == XState.AllDown ? Level.Down : Level.Wait;
            xBoard.Line1 = xm.Active
                ? (cur != null ? cur.Transport + " · " + cur.Endpoint : "") + "  ·  " + Tx("задержка ") + (xm.LastDelay > 0 ? xm.LastDelay + Tx(" мс") : xm.LastDelay == -2 ? Tx("нет ответа") : "…") + "  ·  " + Tx("переключений: ") + xm.Switches + (st.AsEnabled ? "" : "  ·  " + Tx("автосвитч выкл"))
                : Tx("Выберите сервер и нажмите «Подключить» (или двойной клик)");
            xBoard.Line2 = "SOCKS 127.0.0.1:" + st.SocksPort + "  ·  HTTP 127.0.0.1:" + st.HttpPort + (st.TunMode ? "  ·  TUN" + (xm.Active ? (xm.TunRunning ? " ✓" : " ✗") : "") : st.SysProxy ? "  ·  " + Tx("системный прокси") : "") + (xm.Detail != "" && s != XState.Connected ? "  ·  " + xm.Detail : "");
            xBoard.Invalidate();
            if (srvGrid != null && !srvGrid.IsDisposed && curPage == PgServers) FillServers();
        }

        void TestServers(List<ServerEntry> list)
        {
            if (xTesting) { if (xTestCf != null) xTestCf.Cancelled = true; return; }
            if (list.Count == 0) return;
            if (!File.Exists(XrayProcess.Exe(st))) { UpdateCore(true); return; }
            xTesting = true; xTestCf = new CancelFlag();
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
                        try { BeginInvoke((Action)(() => { lSrvInfo.Text = Tx("Замер: ") + d + "/" + total + "…"; foreach (DataGridViewRow r in srvGrid.Rows) if (r.Tag == x) { r.Cells[5].Value = DelayText(x); r.Cells[5].Style.ForeColor = x.Delay == -2 ? Theme.Red : Theme.Green; } })); } catch { }
                    }, cf);
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, "xray test: " + ex.Message); }
                ServerStore.Save();
                try { BeginInvoke((Action)(() => { xTesting = false; int ok = list.Count(x => x.Delay > 0); lSrvInfo.Text = Tx("Замер готов: работают ") + ok + "/" + total; })); } catch { }
            });
        }

        void UpdateSubs()
        {
            if (ServerStore.Subs.Count == 0) { using (var d = new SubsDialog()) d.ShowDialog(this); return; }
            lSrvInfo.Text = Tx("Обновление подписок…");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var rep = ServerStore.Subs.Where(s => s.Enabled).ToList().Select(ServerStore.UpdateSub).ToList();
                Logger.Write(LogLevel.Info, Tx("Подписки: ") + string.Join(" · ", rep));
                try { BeginInvoke((Action)(() => { FillServers(); lSrvInfo.Text = Tx("Подписки: ") + string.Join(" · ", rep); })); } catch { }
            });
        }

        void UpdateCore(bool ask)
        {
            string cur = Updater.XrayVersion(st);
            if (ask && MessageBox.Show(this, (cur == "" ? Tx("Ядро xray не установлено.") : "xray " + cur + ".") + "\r\n" + Tx("Скачать последнюю версию с GitHub (XTLS/Xray-core)?"), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            btnCore.Enabled = false;
            bool was = xm.Active; string id = st.ActiveServer;
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
                try { BeginInvoke((Action)(() => { btnCore.Enabled = true; lSrvInfo.Text = res; UpdateCoreLabel(); CheckUpdates(true); if (was) xm.Connect(id); })); } catch { }
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
                    bool supd = sv != "" && sbLatest != null && sbLatest.TrimStart('v') != sv.TrimStart('v');
                    btnSb.Text = sv == "" ? Tx("Установить sing-box") : "sing-box " + sv + (supd ? " → " + sbLatest : ""); btnSb.AutoW(); btnSb.Invalidate();
                    bool upd = v != "" && xrayLatest != null && xrayLatest.TrimStart('v') != v.TrimStart('v');
                    btnCore.Text = v == "" ? Tx("Установить xray") : "xray " + v + (upd ? " → " + xrayLatest : "");
                    btnCore.Primary = false;
                    btnCore.AutoW(); btnCore.Invalidate();
                    if (relayServers != null) relayServers();
                })); } catch { }
            });
        }
    }

    // Подписки: имя + URL, включение, обновление
    public class SubsDialog : Form
    {
        readonly ThemedList list = new ThemedList();
        readonly TextBox tbName = Ui.TextBox(), tbUrl = Ui.TextBox();
        readonly Label info = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent };

        public SubsDialog()
        {
            Text = Tx("Подписки"); StartPosition = FormStartPosition.CenterParent; Size = new Size(Theme.S(620), Theme.S(440)); BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            MinimizeBox = false; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
            var card = new CardPanel(); card.Controls.Add(list);
            card.Resize += (s, e) => list.SetBounds(Theme.S(6), Theme.S(6), card.Width - Theme.S(12), card.Height - Theme.S(12));
            list.RightText = n => { var sb = ServerStore.Subs.FirstOrDefault(x => x.Name == n); return sb == null ? null : (sb.Enabled ? "" : Tx("выкл") + "  ") + ServerStore.Snapshot().Count(x => x.Group == sb.Id); };
            list.SelectedIndexChanged += (s, e) => { var sb = Cur(); if (sb != null) { tbName.Text = sb.Name; tbUrl.Text = sb.Url; } };
            var lN = Ui.Label(Tx("Имя"), Theme.Small, Theme.Muted); var lU = Ui.Label("URL", Theme.Small, Theme.Muted);
            var bAdd = new FlatButton(Tx("Добавить"), true) { Glyph = "" }.AutoW();
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
            var bUpd = new FlatButton(Tx("Обновить")) { Glyph = "" }.AutoW(); bUpd.Click += (s, e) => { var sb = Cur(); if (sb != null) { info.Text = Tx("Загрузка…"); Refresh(); info.Text = ServerStore.UpdateSub(sb); Fill(); } };
            var bDel = new FlatButton(Tx("Удалить")) { Glyph = "", Danger = true }.AutoW();
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
        }
        Subscription Cur() { var n = list.SelectedItem; return n == null ? null : ServerStore.Subs.FirstOrDefault(x => x.Name == n); }
        void Fill() { var sel = list.SelectedItem; list.Items.Clear(); list.Items.AddRange(ServerStore.Subs.Select(x => x.Name)); list.EndUpdate(); if (sel != null && list.Items.Contains(sel)) list.SelectedItem = sel; }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); }
    }
}
