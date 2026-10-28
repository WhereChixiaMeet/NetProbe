using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        // ================= Monitor page =================
        IndicatorCard cardVpn, cardDir; LatencyChart chart; FlatButton btnMon; Label infoLabel, warnLabel; TabStrip monPeriod;
        volatile bool periodBusy;

        // расчёт в фоне, отрисовка в UI-потоке
        void LoadPeriod(string k)
        {
            if (k == "s") { chart.HA = chart.HB = null; chart.HistSub = chart.HistEmpty = null; chart.Invalidate(); return; }
            int days = k == "d" ? 1 : 7;
            chart.HA = chart.HB = null; chart.HistSub = days == 1 ? Tx("сегодня · 1 точка в минуту · пропуски = нет данных") : Tx("7 дней · 1 точка в 30 мин · пропуски = нет данных"); chart.HistEmpty = Tx("Загрузка…"); chart.Invalidate();
            if (periodBusy) return; periodBusy = true;
            string dir = st.LogPath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Tuple<List<double>, List<double>> r = null;
                try { r = History.Load(dir, days); } catch { }
                periodBusy = false;
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (monPeriod.SelectedKey != k) return;
                        if (r == null) { chart.HistEmpty = Tx("Нет данных за период — мониторинг пишет точку в лог раз в минуту, пока запущен"); chart.HA = chart.HB = null; }
                        else { chart.HA = r.Item1; chart.HB = r.Item2; chart.HistEmpty = null; }
                        chart.Invalidate();
                    }));
                }
                catch { }
            });
        }

        SpeedChart speedChart; Action layMonitor;
        void BuildMonitorPage()
        {
            var p = NewPage(Tx("Мониторинг"), Tx("Задержка и потери на обоих маршрутах"));
            cardVpn = new IndicatorCard { Title = "VPN", Series = Theme.Vpn, Sub = "curl.exe" };
            cardDir = new IndicatorCard { Title = Tx("Напрямую (без VPN)"), Series = Theme.Direct, Sub = "netprobe-direct.exe" };
            chart = new LatencyChart { Good = st.GoodMs, Warn = st.WarnMs };
            btnMon = new FlatButton(Tx("Запустить мониторинг"), true) { Glyph = "\uE768" };
            btnMon.Width = Theme.S(170);
            btnMon.Click += (s, e) => { if (MonOn) StopMonitoring(); else StartMonitoring(); };
            var btnReset = new FlatButton(Tx("Сбросить статистику")) { Glyph = "\uE72C" }.AutoW();
            btnReset.Click += (s, e) => { monVpn.Stats.Reset(); monDir.Stats.Reset(); chart.Clear(); Logger.Write(LogLevel.Info, Tx("Статистика сброшена")); };
            // период графика: Session (живые опросы) / Day (сегодня из лога) / Week (7 дневных логов, 1 точка / 30 мин)
            monPeriod = new TabStrip { Segmented = true, Font = Theme.Small, Height = Theme.S(28), Width = Theme.S(240) };
            monPeriod.SetTabs(new[] { new TabStrip.Tab { Key = "s", Text = "Session", Tip = Tx("Текущая сессия: последние опросы") }, new TabStrip.Tab { Key = "d", Text = "Day", Tip = Tx("Сегодня: из дневного лога, 1 точка в минуту") }, new TabStrip.Tab { Key = "w", Text = "Week", Tip = Tx("7 дней: из дневных логов, 1 точка в 30 мин") } });
            monPeriod.SelectedKey = "s";
            monPeriod.Selected += k => LoadPeriod(k);
            infoLabel = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Bg, AutoEllipsis = true, UseMnemonic = false };
            warnLabel = new Label { AutoSize = false, ForeColor = Theme.Orange, Font = Theme.Small, BackColor = Theme.Bg, AutoEllipsis = true, Visible = false };
            var oldSpeed = speedChart; speedChart = new SpeedChart();
            if (oldSpeed != null) { speedChart.Down.AddRange(oldSpeed.Down); speedChart.Up.AddRange(oldSpeed.Up); speedChart.Adapter = oldSpeed.Adapter; }
            p.Controls.AddRange(new Control[] { cardVpn, cardDir, chart, speedChart, btnMon, btnReset, monPeriod, infoLabel, warnLabel });
            Action lay = () =>
            {
                // сверху справа — общие кнопки «Маршрут · DPI · VPN»; управление монитором — строкой ниже
                int L = Theme.S(26), T = Theme.S(126), W = PW(p) - L * 2, gap = Theme.S(16), ry = Theme.S(80);
                btnMon.Location = new Point(PW(p) - L - btnMon.Width, ry);
                btnReset.Location = new Point(btnMon.Left - Theme.S(10) - btnReset.Width, ry);
                monPeriod.Location = new Point(L, ry + (btnMon.Height - monPeriod.Height) / 2);
                int cw = (W - gap) / 2, ch = Theme.S(200);
                cardVpn.SetBounds(L, T, cw, ch);
                cardDir.SetBounds(L + cw + gap, T, W - cw - gap, ch);
                int by = p.ClientSize.Height - Theme.S(20);
                infoLabel.SetBounds(L, by - Theme.S(18), W, Theme.S(18));
                warnLabel.SetBounds(L, by - Theme.S(38), W, Theme.S(18));
                int cy = T + ch + gap;
                int avail = Math.Max(Theme.S(120), by - Theme.S(44) - cy);
                // под графиком задержки — график скорости (можно выключить в настройках)
                speedChart.Visible = st.SpeedChartOn;
                if (speedChart.Visible)
                {
                    int sh = Math.Max(Theme.S(90), Math.Min(Theme.S(220), (avail - gap) * 2 / 5));
                    chart.SetBounds(L, cy, W, avail - gap - sh);
                    speedChart.SetBounds(L, cy + avail - sh, W, sh);
                }
                else chart.SetBounds(L, cy, W, avail);
            };
            layMonitor = lay;
            p.Resize += (s, e) => lay();
            lay();
        }

        // ================= Tests page =================
        DataGridView grid; ProgressLine prog; Label progLabel; ThemedCheck chkTVpn, chkTDir;
        readonly List<FlatButton> testButtons = new List<FlatButton>(); FlatButton btnStop;
        readonly Dictionary<string, DataGridViewRow> rows = new Dictionary<string, DataGridViewRow>();
        TestRunner runner;
        static readonly string[] RowOrder = { "ip", "geo", "isp", "http", "ping", "jitter", "loss", "dns", "dnssrv0", "dnssrv1", "dnssrv2", "dnssrv3", "down", "up", "mtu", "score", "suit" };

        class RowTag { public double[] V = { double.NaN, double.NaN }; public bool? Hib; }

        void BuildTestsPage()
        {
            var p = NewPage(Tx("Тесты"), Tx("Скорость и стабильность обоих маршрутов"));
            var flow = new FlowLayoutPanel { BackColor = Theme.Bg, WrapContents = true, AutoSize = false };
            Func<string, string, bool, Action<TestRunner>, FlatButton> add = (glyph, text, primary, act) =>
            {
                var b = new FlatButton(text, primary) { Glyph = glyph, Margin = new Padding(0, 0, Theme.S(8), Theme.S(8)) }.AutoW();
                b.Click += (s, e) => RunTest(text, act);
                flow.Controls.Add(b); testButtons.Add(b); return b;
            };
            add("\uE768", Tx("Полный тест"), true, r => r.Full());
            add("\uE701", Tx("Пинг · джиттер · потери"), false, r => r.Ping(0, 1));
            add("\uE774", Tx("Проверка DNS"), false, r => r.Dns(0, 1));
            add("\uE896", Tx("Загрузка"), false, r => r.Download(0, 1));
            add("\uE898", Tx("Отдача"), false, r => r.Upload(0, 1));
            add("\uE8CB", Tx("HTTPS-рукопожатие"), false, r => r.Handshake(0, 1));
            add("\uE9CE", "Path MTU", false, r => r.Mtu(0, 1));
            add("\uE909", Tx("Публичный IP"), false, r => { r.PublicIp(0, 1); });
            btnStop = new FlatButton(Tx("Стоп")) { Danger = true, Glyph = "\uE71A", Enabled = false, Margin = new Padding(0, 0, Theme.S(8), Theme.S(8)) }.AutoW();
            btnStop.Click += (s, e) => { if (runner != null) { runner.Cf.Cancelled = true; progLabel.Text = Tx("Остановка…"); } };
            flow.Controls.Add(btnStop);

            chkTVpn = Ui.Check(Tx("Канал VPN"), true); chkTDir = Ui.Check(Tx("Канал напрямую"), true);
            chkTVpn.BackColor = chkTDir.BackColor = Theme.Bg;
            prog = new ProgressLine();
            progLabel = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Bg, Text = Tx("Выберите тест"), AutoEllipsis = true };

            var gridCard = new CardPanel();
            grid = new DataGridView();
            Ui.StyleGrid(grid);
            grid.Columns.Add("t", Tx("ТЕСТ")); grid.Columns.Add("v", "VPN"); grid.Columns.Add("d", Tx("НАПРЯМУЮ (БЕЗ VPN)")); grid.Columns.Add("x", Tx("РАЗНИЦА"));
            grid.Columns[0].FillWeight = 26; grid.Columns[1].FillWeight = 32; grid.Columns[2].FillWeight = 32; grid.Columns[3].FillWeight = 14;
            grid.Columns[0].DefaultCellStyle.ForeColor = Theme.Muted;
            foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            gridCard.Controls.Add(grid);
            gridCard.Resize += (s, e) => grid.SetBounds(Theme.S(8), Theme.S(8), gridCard.Width - Theme.S(16), gridCard.Height - Theme.S(16));

            var bCopy = new FlatButton(Tx("Копировать")) { Glyph = "\uE8C8" }.AutoW();
            bCopy.Click += (s, e) => { var t = GridText(grid, "\t"); if (t.Length > 0) Clipboard.SetText(t); };
            var bCsv = new FlatButton(Tx("Экспорт CSV…")) { Glyph = "\uE74E" }.AutoW();
            bCsv.Click += (s, e) => ExportCsv(grid, "netprobe-tests");
            var bHist = new FlatButton(Tx("История")) { Glyph = "\uE81C" }.AutoW();
            bHist.Click += (s, e) => { string f = Path.Combine(Settings.Dir, "history.csv"); if (File.Exists(f)) Process.Start(f); else MessageBox.Show(this, Tx("Истории пока нет — сначала запустите полный тест."), "NetProbe"); };
            var bClear = new FlatButton(Tx("Очистить")) { Glyph = "\uE894" }.AutoW();
            bClear.Click += (s, e) => { grid.Rows.Clear(); rows.Clear(); };

            p.Controls.AddRange(new Control[] { flow, chkTVpn, chkTDir, prog, progLabel, gridCard, bCopy, bCsv, bHist, bClear });
            Action lay = () =>
            {
                int L = Theme.S(26), W = PW(p) - L * 2;
                flow.SetBounds(L, Theme.S(86), W, Theme.S(90));
                int fh = flow.Controls.Count > 0 ? flow.Controls[flow.Controls.Count - 1].Bottom + Theme.S(4) : Theme.S(42);
                flow.Height = fh;
                int y = flow.Bottom + Theme.S(4);
                chkTVpn.Location = new Point(L, y); chkTDir.Location = new Point(L + chkTVpn.Width + Theme.S(20), y);
                y += Theme.S(30);
                prog.SetBounds(L, y, W, Theme.S(4)); progLabel.SetBounds(L, y + Theme.S(8), W, Theme.S(20));
                y += Theme.S(36);
                int by = p.ClientSize.Height - Theme.S(20) - Theme.S(34);
                bCopy.Location = new Point(L, by); bCsv.Location = new Point(bCopy.Right + Theme.S(8), by); bHist.Location = new Point(bCsv.Right + Theme.S(8), by);
                bClear.Location = new Point(L + W - bClear.Width, by);
                gridCard.SetBounds(L, y, W, by - Theme.S(12) - y);
            };
            p.Resize += (s, e) => lay();
            lay();
        }

        List<TestChannel> TestChannels(bool vpn, bool dir)
        {
            var l = new List<TestChannel>();
            if (vpn) l.Add(new TestChannel { Key = "vpn", Name = "VPN", P = pVpn, BindIp = null });
            if (dir) l.Add(new TestChannel { Key = "direct", Name = Tx("Напрямую"), P = pDir, BindIp = direct != null ? direct.Ip : null });
            return l;
        }

        void RunTest(string name, Action<TestRunner> act)
        {
            if (runner != null) return;
            var chans = TestChannels(chkTVpn.Checked, chkTDir.Checked);
            if (chans.Count == 0) { progLabel.Text = Tx("Выберите хотя бы один канал."); return; }
            var r = new TestRunner { St = st.Clone(), Channels = chans };
            r.Cell = (row, title, ch, text, v, hib, lv) => BeginInvoke((Action)(() => SetCell(row, title, ch, text, v, hib, lv)));
            r.Status = (s, pv) => BeginInvoke((Action)(() => { if (s != "") progLabel.Text = s; prog.Value = pv; }));
            r.PauseMonitors = on => { monVpn.Paused = on; monDir.Paused = on; };
            runner = r;
            foreach (var b in testButtons) b.Enabled = false;
            btnStop.Enabled = true; prog.Value = 0;
            progLabel.Text = name + "…";
            var t0 = DateTime.Now;
            Logger.Write(LogLevel.Test, name + Tx(" — старт"));
            var th = new Thread(() =>
            {
                try { act(r); }
                catch (Exception ex) { Logger.Write(LogLevel.Error, name + ": " + ex.Message); }
                finally { monVpn.Paused = false; monDir.Paused = false; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        bool c = r.Cf.Cancelled;
                        runner = null;
                        foreach (var b in testButtons) b.Enabled = true;
                        btnStop.Enabled = false; prog.Value = c ? 0 : 1;
                        progLabel.Text = name + (c ? Tx(" — остановлено.") : Tx(" — готово за ") + (int)(DateTime.Now - t0).TotalSeconds + Tx(" с."));
                        if (siteRunning) { siteRunning = false; btnSites.Enabled = true; btnSitesStop.Enabled = false; siteLabel.Text = c ? Tx("Остановлено.") : Tx("Готово за ") + (int)(DateTime.Now - t0).TotalSeconds + Tx(" с."); siteProg.Value = c ? 0 : 1; }
                    }));
                }
                catch { }
            }) { IsBackground = true };
            th.Start();
        }

        void SetCell(string row, string title, string ch, string text, double v, bool? hib, Level lv)
        {
            DataGridViewRow r;
            if (!rows.TryGetValue(row, out r))
            {
                int order = Array.IndexOf(RowOrder, row); if (order < 0) order = 999;
                int pos = 0;
                foreach (DataGridViewRow ex in grid.Rows)
                {
                    int o = Array.IndexOf(RowOrder, (string)ex.Cells[0].Tag); if (o < 0) o = 999;
                    if (o <= order) pos = ex.Index + 1;
                }
                grid.Rows.Insert(pos, title, "", "", "");
                r = grid.Rows[pos]; r.Cells[0].Tag = row; r.Tag = new RowTag(); rows[row] = r;
            }
            r.Cells[0].Value = title;
            int ci = ch == "vpn" ? 1 : 2;
            r.Cells[ci].Value = text;
            r.Cells[ci].Style.ForeColor = lv == Level.Off ? Theme.Text : lv == Level.Good ? Theme.Text : Theme.LevelColor(lv);
            var tag = (RowTag)r.Tag; tag.V[ci - 1] = v; tag.Hib = hib;
            r.Cells[3].Value = Diff(row, tag);
            r.Cells[3].Style.ForeColor = DiffColor(row, tag);
        }

        static string Diff(string row, RowTag t)
        {
            double a = t.V[0], b = t.V[1];
            if (t.Hib == null || double.IsNaN(a) || double.IsNaN(b)) return "";
            if (row == "down" || row == "up")
            {
                if (b <= 0) return "";
                double pct = (a - b) / b * 100;
                return (pct >= 0 ? "+" : "−") + Math.Abs(pct).ToString("0", CultureInfo.InvariantCulture) + " %";
            }
            double d = a - b;
            string unit = row == "loss" ? Tx(" п.п.") : row == "mtu" ? Tx(" Б") : row == "score" ? Tx(" балл.") : Tx(" мс");
            return (d >= 0 ? "+" : "−") + Math.Abs(d).ToString(row == "loss" || row == "jitter" ? "0.0" : "0", CultureInfo.InvariantCulture) + unit;
        }

        static Color DiffColor(string row, RowTag t)
        {
            double a = t.V[0], b = t.V[1];
            if (t.Hib == null || double.IsNaN(a) || double.IsNaN(b)) return Theme.Muted;
            bool better = t.Hib == true ? a >= b : a <= b;
            return better ? Theme.Green : Theme.Orange;
        }

        static string GridText(DataGridView g, string sep)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(sep, g.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText))).Append("\r\n");
            foreach (DataGridViewRow r in g.Rows)
                sb.Append(string.Join(sep, r.Cells.Cast<DataGridViewCell>().Select(c => Convert.ToString(c.Value).Replace(sep, " ")))).Append("\r\n");
            return g.Rows.Count == 0 ? "" : sb.ToString();
        }

        void ExportCsv(DataGridView g, string name)
        {
            if (g.Rows.Count == 0) return;
            using (var d = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv" })
                if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, GridText(g, ";"), new UTF8Encoding(true));
        }

        // ================= Sites page =================
        DataGridView siteGrid; FlatButton btnSites, btnSitesStop; ProgressLine siteProg; Label siteLabel; bool siteRunning;
        readonly Dictionary<string, ProbeResult[]> siteRes = new Dictionary<string, ProbeResult[]>();

        void BuildSitesPage()
        {
            var p = NewPage(Tx("Сайты"), Tx("Какие сервисы открываются с VPN и без него"));
            btnSites = new FlatButton(Tx("Проверить сайты"), true) { Glyph = "\uE768" }.AutoW();
            btnSitesStop = new FlatButton(Tx("Стоп")) { Danger = true, Glyph = "\uE71A", Enabled = false }.AutoW();
            var btnEdit = new FlatButton(Tx("Изменить список")) { Glyph = "\uE70F" }.AutoW();
            btnEdit.Click += (s, e) => SelectPage(PgSettings);
            btnSitesStop.Click += (s, e) => { if (runner != null) runner.Cf.Cancelled = true; };
            siteProg = new ProgressLine();
            siteLabel = new Label { AutoSize = false, ForeColor = Theme.Muted, Font = Theme.Small, BackColor = Theme.Bg, Text = Tx("Запрос через оба канала"), AutoEllipsis = true };
            var card = new CardPanel();
            siteGrid = new DataGridView(); Ui.StyleGrid(siteGrid);
            siteGrid.Columns.Add("s", Tx("САЙТ")); siteGrid.Columns.Add("v", "VPN"); siteGrid.Columns.Add("d", Tx("НАПРЯМУЮ (БЕЗ VPN)")); siteGrid.Columns.Add("r", Tx("ИТОГ"));
            siteGrid.Columns[0].FillWeight = 28; siteGrid.Columns[1].FillWeight = 26; siteGrid.Columns[2].FillWeight = 26; siteGrid.Columns[3].FillWeight = 20;
            card.Controls.Add(siteGrid);
            card.Resize += (s, e) => siteGrid.SetBounds(Theme.S(8), Theme.S(8), card.Width - Theme.S(16), card.Height - Theme.S(16));
            var bCsv = new FlatButton(Tx("Экспорт CSV…")) { Glyph = "\uE74E" }.AutoW();
            bCsv.Click += (s, e) => ExportCsv(siteGrid, "netprobe-sites");

            btnSites.Click += (s, e) =>
            {
                if (runner != null) return;
                var sites = st.SiteList();
                siteGrid.Rows.Clear(); siteRes.Clear();
                foreach (var u in sites) { siteGrid.Rows.Add(Host(u), "…", "…", ""); siteRes[u] = new ProbeResult[2]; }
                siteRunning = true; btnSites.Enabled = false; btnSitesStop.Enabled = true; siteProg.Value = 0;
                siteLabel.Text = Tx("Проверка сайтов: ") + sites.Count + "…";
                RunTestSites(sites);
            };
            p.Controls.AddRange(new Control[] { btnSites, btnSitesStop, btnEdit, siteProg, siteLabel, card, bCsv });
            Action lay = () =>
            {
                int L = Theme.S(26), W = PW(p) - L * 2, y = Theme.S(86);
                btnSites.Location = new Point(L, y); btnSitesStop.Location = new Point(btnSites.Right + Theme.S(8), y); btnEdit.Location = new Point(btnSitesStop.Right + Theme.S(8), y);
                y += Theme.S(46);
                siteProg.SetBounds(L, y, W, Theme.S(4)); siteLabel.SetBounds(L, y + Theme.S(8), W, Theme.S(20));
                y += Theme.S(36);
                int by = p.ClientSize.Height - Theme.S(20) - Theme.S(34);
                bCsv.Location = new Point(L, by);
                card.SetBounds(L, y, W, by - Theme.S(12) - y);
            };
            p.Resize += (s, e) => lay();
            lay();
        }

        static string Host(string u) { try { return new Uri(u).Host; } catch { return u; } }

        void RunTestSites(List<string> sites)
        {
            var chans = TestChannels(true, true);
            var r = new TestRunner { St = st.Clone(), Channels = chans };
            r.Status = (s, pv) => BeginInvoke((Action)(() => { siteLabel.Text = s; siteProg.Value = pv; }));
            r.PauseMonitors = on => { };
            var t0 = DateTime.Now;
            runner = r;
            foreach (var b in testButtons) b.Enabled = false;
            Logger.Write(LogLevel.Test, Tx("Проверка сайтов (") + sites.Count + ")");
            var th = new Thread(() =>
            {
                try
                {
                    r.Sites(sites, (url, ch, res) => BeginInvoke((Action)(() => SiteResult(sites, url, ch, res))));
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, "Sites: " + ex.Message); }
                BeginInvoke((Action)(() =>
                {
                    bool c = r.Cf.Cancelled;
                    runner = null; siteRunning = false;
                    foreach (var b in testButtons) b.Enabled = true;
                    btnSites.Enabled = true; btnSitesStop.Enabled = false; siteProg.Value = c ? 0 : 1;
                    int onlyVpn = siteRes.Values.Count(v => v[0] != null && v[1] != null && Reach(v[0]) && !Reach(v[1]));
                    siteLabel.Text = (c ? Tx("Остановлено.") : Tx("Готово за ") + (int)(DateTime.Now - t0).TotalSeconds + Tx(" с.")) + (onlyVpn > 0 ? "  " + onlyVpn + " site(s) open only through the VPN." : "");
                    Logger.Write(LogLevel.Test, Tx("Проверка сайтов завершена") + (onlyVpn > 0 ? Tx(": только через VPN — ") + onlyVpn : ""));
                }));
            }) { IsBackground = true };
            th.Start();
        }

        static bool Reach(ProbeResult r) { return r.Exit == 0 && r.Code >= 200 && r.Code < 400 || (r.Exit == 0 && r.Code > 0 && r.Code < 500 && r.Code != 403 && r.Code != 451); }

        void SiteResult(List<string> sites, string url, string ch, ProbeResult res)
        {
            int i = sites.IndexOf(url); if (i < 0 || i >= siteGrid.Rows.Count) return;
            var row = siteGrid.Rows[i];
            int ci = ch == "vpn" ? 1 : 2;
            siteRes[url][ci - 1] = res;
            string txt = res.Exit == 0 && res.Code > 0 ? "HTTP " + res.Code + "  ·  " + F0(res.Ttfb) + Tx(" мс") : "✗ " + (res.Error.Length > 60 ? res.Error.Substring(0, 60) : res.Error);
            row.Cells[ci].Value = txt;
            row.Cells[ci].Style.ForeColor = Reach(res) ? Theme.Text : res.Exit == 0 ? Theme.Yellow : Theme.Red;
            var v = siteRes[url];
            if (v[0] != null && v[1] != null)
            {
                bool a = Reach(v[0]), b = Reach(v[1]);
                string verdict = a && b ? Tx("Открывается везде") : a ? Tx("Только через VPN") : b ? Tx("Только без VPN") : Tx("Недоступен");
                row.Cells[3].Value = verdict;
                row.Cells[3].Style.ForeColor = a && b ? Theme.Green : a || b ? Theme.Orange : Theme.Red;
                if (a != b) Logger.Write(LogLevel.Warn, Host(url) + ": " + verdict.ToLower());
            }
        }

    }
}
