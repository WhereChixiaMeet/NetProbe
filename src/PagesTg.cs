using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // Вкладка TG Proxy: локальный MTProto-прокси для Telegram Desktop (перенос tg-ws-proxy, Flowseal, MIT).
    // Карточки — модули (tg.*): их можно переносить на другие страницы, как остальные.
    public partial class MainForm
    {
        Panel tgBody; ScrollHost tgHost;
        Label tgpState, tgpAddr, tgpLink, tgpErr, tgpDcRes, tgpCfRes;
        TgStatsView tgpStats; Action tgpLayState;
        Toggle tgpOn;
        System.Windows.Forms.Timer tgpTimer, tgpApply;
        bool tgpLogHooked, tgpLoading;
        static int TgCardW { get { return Theme.S(420); } } // исходная (минимальная) ширина карточек вкладки

        TgConfig TgConfigFromSettings(out string error)
        {
            error = null;
            var c = new TgConfig();
            System.Net.IPAddress ip;
            if (!System.Net.IPAddress.TryParse((st.TgHost ?? "").Trim(), out ip)) { error = Tx("Неверный IP-адрес"); return null; }
            c.Host = st.TgHost.Trim(); c.Port = st.TgPort;
            if (!TgConst.ValidSecret(st.TgSecret)) { error = Tx("Секрет — ровно 32 шестнадцатеричных символа"); return null; }
            c.Secret = st.TgSecret.Trim().ToLowerInvariant();
            try { c.DcRedirects = TgConst.ParseDcIp(st.TgDcIp); }
            catch (FormatException ex) { error = Tx("Неверная запись DC:IP: ") + ex.Message; return null; }
            c.BufferSize = Math.Max(4, st.TgBufKb) * 1024; c.PoolSize = Math.Max(0, st.TgPool);
            c.FallbackCf = st.TgCf; c.H2Media = st.TgH2; c.DisableSecure = st.TgNoSecure; c.ProxyProtocol = st.TgProxyProto; c.ForceTestDc = st.TgTestDc; c.Verbose = st.TgVerbose;
            c.CfUserDomains = st.TgCfUserOn ? TgConst.DomainList(st.TgCfUser) : new List<string>();
            c.CfWorkerDomains = st.TgWorkerOn ? TgConst.DomainList(st.TgWorker) : new List<string>();
            c.FakeTlsDomain = (st.TgFakeTls ?? "").Trim();
            return c;
        }

        // запуск / остановка / перезапуск по текущим настройкам
        void TgApply()
        {
            if (ShotsDir != null) return; // тестовые скриншоты — прокси не поднимаем
            HookTgLog();
            if (!st.TgOn) { if (TgProxy.Running) TgProxy.Stop(); TgUpdateUi(); return; }
            string err; var c = TgConfigFromSettings(out err);
            if (c == null) { TgProxy.Stop(); TgProxy.LastError = err; Logger.Write(LogLevel.Error, "[TG] " + err); TgUpdateUi(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool ok = TgProxy.Start(c);
                try { BeginInvoke((Action)(() => { if (ok) Logger.Write(LogLevel.Ok, Tx("TG Proxy запущен: ") + c.Host + ":" + c.Port); TgUpdateUi(); })); } catch { }
            });
        }
        void TgApplySoon() { if (tgpApply == null) { tgpApply = new System.Windows.Forms.Timer { Interval = 800 }; tgpApply.Tick += (s, e) => { tgpApply.Stop(); TgApply(); }; } tgpApply.Stop(); tgpApply.Start(); }

        void HookTgLog()
        {
            if (tgpLogHooked) return; tgpLogHooked = true;
            // события отдельных соединений — уровнем «опрос» (как частые записи мониторинга), остальное — обычным
            TgLog.Line += (lv, msg) => Logger.Write(lv == TgLv.Error ? LogLevel.Error : lv == TgLv.Warn ? LogLevel.Warn : lv == TgLv.Debug || msg.StartsWith("[") ? LogLevel.Poll : LogLevel.Info, "[TG] " + msg);
        }

        void TgUpdateUi()
        {
            if (tgpState == null || tgpState.IsDisposed) return;
            var c = TgProxy.Config; bool run = TgProxy.Running;
            string err; var cur = TgConfigFromSettings(out err);
            bool failed = st.TgOn && !run && TgProxy.LastError != "";
            tgpState.Text = run ? Tx("Работает") : failed ? Tx("Ошибка") : st.TgOn ? Tx("Запуск…") : Tx("Выключен");
            tgpState.ForeColor = run ? Theme.Green : failed ? Theme.Red : Theme.Text;
            tgpState.Tag = run ? Theme.Green : failed ? Theme.Red : st.TgOn ? Theme.Yellow : Theme.Dim; // цвет точки
            var dcs = cur != null && cur.DcRedirects.Count > 0 ? "DC" + string.Join(", DC", cur.DcRedirects.Keys.OrderBy(k => k)) + Tx(" напрямую") : Tx("все DC — запасными путями");
            tgpAddr.Text = (failed ? TgProxy.LastError + "  ·  " : "") + (cur != null ? cur.Host + ":" + cur.Port + "  ·  " + dcs + (cur.FakeTlsDomain != "" ? "  ·  Fake TLS" : "") : "");
            tgpLink.Text = cur != null ? TgProxy.Link(cur) : "";
            bool showErr = err != null;
            if (tgpErr != null && (tgpErr.Text != (err ?? "") || tgpErr.Visible != showErr)) { tgpErr.Text = err ?? ""; tgpErr.Visible = showErr; if (tgpLayState != null) tgpLayState(); }
            if (tgpOn != null && tgpOn.On != st.TgOn) { tgpLoading = true; tgpOn.On = st.TgOn; tgpLoading = false; }
            if (tgpStats != null) tgpStats.Set(run);
            tgpState.Invalidate(); if (tgpState.Parent != null) tgpState.Parent.Invalidate(new Rectangle(0, tgpState.Top, Theme.S(40), tgpState.Height));
        }

        void TgOpenInTelegram()
        {
            string err; var c = TgConfigFromSettings(out err); if (c == null) { MessageBox.Show(this, err, "TG Proxy"); return; }
            var link = TgProxy.Link(c);
            try { Process.Start(link); }
            catch
            {
                try { Clipboard.SetText(link); } catch { }
                MessageBox.Show(this, Tx("Не удалось открыть Telegram автоматически. Ссылка скопирована — отправь её себе в Telegram и нажми:\n") + link, "TG Proxy");
            }
        }

        // ---- форма карточки: строки «подпись — поле», галочки, кнопки, результат; раскладка по ширине карточки ----
        sealed class TgForm
        {
            enum K { Row, Check, Full, Buttons, Note, Indent, CheckField }
            readonly List<Tuple<K, Control, Control[], int>> items = new List<Tuple<K, Control, Control[], int>>();
            readonly CardPanel card; public int Top = Theme.S(44);
            public static int Pad { get { return Theme.S(18); } }
            public static int LabelW { get { return Theme.S(140); } }
            public TgForm(CardPanel c) { card = c; c.Resize += (s, e) => Lay(); }
            Label Lbl(string t) { var l = new Label { Text = t, AutoSize = false, Font = Theme.Body, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }; card.Controls.Add(l); return l; }
            public void Row(string label, Control ctl, int fixedW = 0) { card.Controls.Add(ctl); items.Add(Tuple.Create(K.Row, ctl, new Control[] { Lbl(label) }, fixedW)); }
            public void Check(ThemedCheck c) { c.BackColor = Theme.Card; card.Controls.Add(c); items.Add(Tuple.Create(K.Check, (Control)c, (Control[])null, 0)); }
            public void Indent(Control c) { card.Controls.Add(c); items.Add(Tuple.Create(K.Indent, c, (Control[])null, 0)); }
            // галочка и поле в одну строку (поле — до правого края)
            public void CheckField(ThemedCheck chk, Control field) { chk.BackColor = Theme.Card; card.Controls.Add(chk); card.Controls.Add(field); items.Add(Tuple.Create(K.CheckField, field, new Control[] { chk }, 0)); }
            // MinH — растянуть карточку вниз (чтобы колонки на вкладке кончались вровень); Natural — высота по содержимому
            public int MinH, Natural;
            public void Full(Control c, int h) { card.Controls.Add(c); items.Add(Tuple.Create(K.Full, c, (Control[])null, h)); }
            public void Buttons(params Control[] bs) { foreach (var b in bs) card.Controls.Add(b); items.Add(Tuple.Create(K.Buttons, (Control)null, bs, 0)); }
            public void Note(Label l) { l.BackColor = Theme.Card; card.Controls.Add(l); items.Add(Tuple.Create(K.Note, (Control)l, (Control[])null, 0)); }
            bool laying;
            public void Lay()
            {
                if (laying) return; laying = true;
                try
                {
                    int W = card.Width, ip = Pad, y = Top, lx = ip + LabelW, gap = Theme.S(7);
                    foreach (var it in items)
                    {
                        var c = it.Item2;
                        switch (it.Item1)
                        {
                            case K.Row:
                                int h = Math.Max(c.Height, Theme.S(26));
                                it.Item3[0].SetBounds(ip, y, LabelW - Theme.S(8), h);
                                if (it.Item4 > 0) c.SetBounds(lx, y + (h - c.Height) / 2, it.Item4, c.Height);
                                else if (c is Spin) c.Location = new Point(lx, y + (h - c.Height) / 2);
                                else c.SetBounds(lx, y + (h - c.Height) / 2, W - lx - ip, c.Height);
                                y += h + gap; break;
                            case K.Check: c.Location = new Point(ip - Theme.S(2), y); y += Theme.S(26); break;
                            case K.CheckField:
                                var chk = it.Item3[0]; int fh = Math.Max(c.Height, Theme.S(26));
                                chk.Location = new Point(ip - Theme.S(2), y + (fh - chk.Height) / 2);
                                int fx = Math.Max(ip + LabelW + Theme.S(40), chk.Right + Theme.S(10));
                                c.SetBounds(fx, y + (fh - c.Height) / 2, Math.Max(Theme.S(60), W - fx - ip), c.Height);
                                y += fh + gap; break;
                            case K.Indent: c.SetBounds(ip + Theme.S(26), y, W - ip * 2 - Theme.S(26), c.Height); y += c.Height + gap; break;
                            case K.Full: c.SetBounds(ip, y, W - ip * 2, it.Item4); y += it.Item4 + gap; break;
                            case K.Buttons:
                                int x = ip; y += Theme.S(4); int bh = 0;
                                foreach (var b in it.Item3) { if (x > ip && x + b.Width > W - ip) { x = ip; y += bh + Theme.S(8); } b.Location = new Point(x, y); x = b.Right + Theme.S(8); bh = Math.Max(bh, b.Height); }
                                y += bh + gap; break;
                            case K.Note:
                                var l = (Label)c;
                                if (!l.Visible || string.IsNullOrEmpty(l.Text)) { l.SetBounds(ip, y, W - ip * 2, 0); break; }
                                int nh = TextRenderer.MeasureText(l.Text, l.Font, new Size(W - ip * 2, 2000), TextFormatFlags.WordBreak).Height + 2;
                                l.SetBounds(ip, y, W - ip * 2, nh); y += nh + gap; break;
                        }
                    }
                    int ch = y - gap + Theme.S(14); Natural = ch;
                    ch = Math.Max(ch, MinH);
                    if (card.Height != ch) card.Height = ch;
                }
                finally { laying = false; }
            }
        }

        void BuildTgPage()
        {
            tgForms.Clear();
            var p = NewPage("TG Proxy", Tx("Telegram Desktop через WebSocket: свой MTProto-прокси на этом компьютере"));
            tgHost = new ScrollHost { BackColor = Theme.Bg };
            tgBody = tgHost.Content;
            p.Controls.Add(tgHost);
            int W = TgCardW, ip = TgForm.Pad;
            var tip = Ui.Tip();
            EventHandler save = (s, e) => { if (tgpLoading) return; st.Save(); TgUpdateUi(); if (st.TgOn) TgApplySoon(); };
            Func<string, CardPanel> card = t => new CardPanel { Title = t, Width = W, Height = Theme.S(120) };
            Func<Label> result = () => new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, UseMnemonic = false };
            Action relay = () => { if (relayTg != null) relayTg(); PageFrame pf; if (frames.TryGetValue(curPage, out pf)) LayFrame(pf); };

            // ---- состояние: крупно статус, адрес, ссылка, кнопки; справа — плитки со счётчиками ----
            var cs = new CardPanel { Title = "TG Proxy", Width = W, Height = Theme.S(200) };
            tgpOn = new Toggle { On = st.TgOn };
            tgpOn.Changed += (s, e) => { if (tgpLoading) return; st.TgOn = tgpOn.On; st.Save(); TgApply(); TgUpdateUi(); };
            tip.SetToolTip(tgpOn, Tx("Включить прокси. Включённый запускается вместе с RouteX"));
            tgpState = new Label { AutoSize = false, Font = Theme.H1, BackColor = Theme.Card, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft };
            var dot = new DoubleBufferedControl { BackColor = Theme.Card };
            dot.Paint += (s, e) => { var g = e.Graphics; Gfx.Hq(g); var col = tgpState.Tag is Color ? (Color)tgpState.Tag : Theme.Dim; Gfx.Dot(g, col, dot.Width / 2f, dot.Height / 2f, Theme.S(6), TgProxy.Running); };
            tgpAddr = new Label { AutoSize = false, Font = Theme.Body, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, AutoEllipsis = true };
            tgpLink = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Dim, BackColor = Theme.Card, UseMnemonic = false, AutoEllipsis = true };
            tip.SetToolTip(tgpLink, Tx("Ссылка для Telegram — «Скопировать ссылку» или «Открыть в Telegram»"));
            var bOpen = new FlatButton(Tx("Открыть в Telegram"), true) { Glyph = "" }.AutoW();
            bOpen.Click += (s, e) => TgOpenInTelegram();
            tip.SetToolTip(bOpen, Tx("Telegram сам предложит подключить этот прокси (ссылка tg://proxy)"));
            var bCopy = new FlatButton(Tx("Скопировать ссылку")) { Glyph = "" }.AutoW();
            bCopy.Click += (s, e) => { try { Clipboard.SetText(tgpLink.Text); Logger.Write(LogLevel.Info, Tx("Ссылка TG Proxy скопирована")); } catch { } };
            var bRestart = new FlatButton(Tx("Перезапустить")) { Glyph = "" }.AutoW();
            bRestart.Click += (s, e) => { if (!st.TgOn) { st.TgOn = true; st.Save(); } TgApply(); };
            tgpStats = new TgStatsView();
            tgpErr = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Red, BackColor = Theme.Card, UseMnemonic = false, Visible = false };
            var how = new Label { Text = Tx("Вручную: Telegram → Настройки → Продвинутые → Тип соединения → Прокси → MTProto: адрес, порт и секрет «dd…» из ссылки."), AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false };
            tip.SetToolTip(tgpLink, how.Text); tip.SetToolTip(tgpAddr, how.Text);
            cs.Controls.AddRange(new Control[] { tgpOn, dot, tgpState, tgpAddr, tgpLink, bOpen, bCopy, bRestart, tgpStats, tgpErr });
            bool layingState = false;
            tgpLayState = () =>
            {
                if (layingState) return; layingState = true;
                try
                {
                    int w = cs.Width, top = Theme.S(46);
                    tgpOn.Location = new Point(w - ip - tgpOn.Width, Theme.S(12));
                    // широкая карточка — плитки справа; узкая — под кнопками
                    bool side = w >= Theme.S(760);
                    int statsW = side ? Math.Min(Theme.S(380), w / 2 - ip) : w - ip * 2;
                    int leftW = side ? w - ip * 3 - statsW : w - ip * 2;
                    dot.SetBounds(ip - Theme.S(4), top + Theme.S(4), Theme.S(24), Theme.S(28));
                    tgpState.SetBounds(ip + Theme.S(22), top, leftW - Theme.S(22), Theme.S(36));
                    tgpAddr.SetBounds(ip, tgpState.Bottom + Theme.S(2), leftW, Theme.S(20));
                    tgpLink.SetBounds(ip, tgpAddr.Bottom + Theme.S(2), leftW, Theme.S(18));
                    int y = tgpLink.Bottom + Theme.S(12), x = ip, bh = bOpen.Height;
                    foreach (var b in new[] { bOpen, bCopy, bRestart }) { if (x > ip && x + b.Width > ip + leftW) { x = ip; y += bh + Theme.S(8); } b.Location = new Point(x, y); x = b.Right + Theme.S(8); }
                    y = bOpen.Bottom; foreach (var b in new[] { bCopy, bRestart }) y = Math.Max(y, b.Bottom);
                    if (tgpErr.Visible) { tgpErr.SetBounds(ip, y + Theme.S(8), leftW, Theme.S(18)); y = tgpErr.Bottom; }
                    int sh = tgpStats.Needed(statsW);
                    if (side) { tgpStats.SetBounds(w - ip - statsW, top, statsW, sh); y = Math.Max(y, tgpStats.Bottom); }
                    else { tgpStats.SetBounds(ip, y + Theme.S(14), statsW, sh); y = tgpStats.Bottom; }
                    int ch = y + Theme.S(14);
                    if (cs.Height != ch) { cs.Height = ch; if (relayTg != null && !layingTg && cs.Parent == tgBody) relayTg(); }
                }
                finally { layingState = false; }
            };
            cs.Resize += (s, e) => tgpLayState(); tgpLayState();
            tgBody.Controls.Add(cs); RegisterModule(cs, "tg.state", PgTg, "subs");

            // ---- MTProto ----
            var cm = card(Tx("MTProto-подключение")); var fm = new TgForm(cm);
            var tbHost = Ui.TextBox(st.TgHost); tbHost.TextChanged += (s, e) => { st.TgHost = tbHost.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbHost, Tx("Адрес, на котором слушает прокси: 127.0.0.1 — только этот компьютер, 0.0.0.0 — вся сеть"));
            fm.Row(Tx("IP-адрес"), tbHost);
            var nPort = Ui.Num(1, 65535, st.TgPort); nPort.ValueChanged += (s, e) => { st.TgPort = (int)nPort.Value; save(s, e); };
            tip.SetToolTip(nPort, Tx("Порт прокси — тот же должен стоять в Telegram"));
            fm.Row(Tx("Порт"), nPort);
            var secretRow = new Panel { BackColor = Theme.Card, Height = Theme.S(30) };
            var tbSecret = Ui.TextBox(st.TgSecret); tbSecret.Font = Theme.Mono;
            tbSecret.TextChanged += (s, e) => { st.TgSecret = tbSecret.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbSecret, Tx("Секрет для авторизации клиента: 32 шестнадцатеричных символа"));
            var bNew = new FlatButton("") { Glyph = "", IconOnly = true }.AutoW();
            bNew.Click += (s, e) => { tbSecret.Text = TgConst.NewSecret(); };
            tip.SetToolTip(bNew, Tx("Новый случайный секрет (в Telegram понадобится новая ссылка)"));
            secretRow.Controls.AddRange(new Control[] { tbSecret, bNew });
            secretRow.Resize += (s, e) => { bNew.Location = new Point(secretRow.Width - bNew.Width, (secretRow.Height - bNew.Height) / 2); tbSecret.SetBounds(0, (secretRow.Height - tbSecret.Height) / 2, Math.Max(10, bNew.Left - Theme.S(6)), tbSecret.Height); };
            fm.Row(Tx("Секрет"), secretRow);
            var tbFake = Ui.TextBox(st.TgFakeTls); tbFake.TextChanged += (s, e) => { st.TgFakeTls = tbFake.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbFake, Tx("Fake TLS (ee-секрет): прокси выглядит как HTTPS этого сайта; чужие подключения уходят на сам сайт. Пусто — выключено"));
            fm.Row("Fake TLS", tbFake);
            var cPP = Ui.Check(Tx("Принимать PROXY protocol (за nginx/haproxy)"), st.TgProxyProto); cPP.CheckedChanged += (s, e) => { st.TgProxyProto = cPP.Checked; save(s, e); };
            fm.Check(cPP);
            fm.Lay(); tgForms[cm] = fm;
            tgBody.Controls.Add(cm); RegisterModule(cm, "tg.mtproto", PgTg, "subs");

            // ---- DC → IP ----
            var cd2 = card(Tx("Дата-центры Telegram (DC → IP)")); var fd = new TgForm(cd2);
            var hint = new Label { Text = Tx("По правилу на строку: номер:IP (например 4:149.154.167.220). Эти DC идут прямым WebSocket к web.telegram.org; остальные и при сбое — запасными путями."), Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = false, UseMnemonic = false };
            fd.Note(hint);
            var tbDc = Ui.TextBox(string.Join("\r\n", TgConst.DomainList(st.TgDcIp)), true); tbDc.ScrollBars = ScrollBars.Vertical;
            tbDc.TextChanged += (s, e) => { st.TgDcIp = string.Join(", ", TgConst.DomainList(tbDc.Text)); save(s, e); };
            fd.Full(tbDc, Theme.S(56));
            var bDcDef = new FlatButton(Tx("По умолчанию")) { Glyph = "" }.AutoW();
            bDcDef.Click += (s, e) => tbDc.Text = TgConst.DefaultDcIp.Replace(", ", "\r\n");
            var bDcTest = new FlatButton(Tx("Проверить")) { Glyph = "" }.AutoW();
            tip.SetToolTip(bDcTest, Tx("Проверить прямой WebSocket к каждому DC из списка"));
            fd.Buttons(bDcDef, bDcTest);
            tgpDcRes = result(); fd.Note(tgpDcRes);
            bDcTest.Click += (s, e) =>
            {
                Dictionary<int, string> rules; try { rules = TgConst.ParseDcIp(st.TgDcIp); } catch (FormatException ex) { tgpDcRes.Text = Tx("Неверная запись DC:IP: ") + ex.Message; fd.Lay(); relay(); return; }
                tgpDcRes.Text = Tx("Проверка…"); bDcTest.Enabled = false; fd.Lay(); relay();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var r = TgHttp.TestDirect(rules);
                    string text = r.Count == 0 ? Tx("нет правил — весь трафик пойдёт запасными путями") : string.Join("   ", r.Select(kv => "DC" + kv.Key + (kv.Value == null ? " ✓" : " ✗ " + kv.Value)));
                    bool allOk = r.Count > 0 && r.Values.All(v => v == null);
                    try { BeginInvoke((Action)(() => { if (tgpDcRes.IsDisposed) return; tgpDcRes.Text = text; tgpDcRes.ForeColor = allOk ? Theme.Green : Theme.Muted; bDcTest.Enabled = true; fd.Lay(); relay(); })); } catch { }
                });
            };
            fd.Lay(); tgForms[cd2] = fd;
            tgBody.Controls.Add(cd2); RegisterModule(cd2, "tg.dc", PgTg, "subs");

            // ---- Cloudflare ----
            var cc = card("Cloudflare"); var fc = new TgForm(cc);
            var cCf = Ui.Check(Tx("CF-прокси для недоступных DC"), st.TgCf); cCf.CheckedChanged += (s, e) => { st.TgCf = cCf.Checked; save(s, e); };
            tip.SetToolTip(cCf, Tx("Запасной путь через домены за Cloudflare. Это сторонние серверы (по умолчанию — домены автора tg-ws-proxy)"));
            fc.Check(cCf);
            var cH2 = Ui.Check(Tx("Медиа через HTTP/2 (одно соединение на DC)"), st.TgH2); cH2.CheckedChanged += (s, e) => { st.TgH2 = cH2.Checked; save(s, e); };
            tip.SetToolTip(cH2, Tx("Загрузка медиа через CF одним HTTP/2-соединением. Нужны CF-прокси и TLS; Windows 10 1607+"));
            fc.Check(cH2);
            var cUser = Ui.Check(Tx("Свои домены CF-прокси"), st.TgCfUserOn);
            var tbUser = Ui.TextBox(st.TgCfUser); tbUser.Enabled = st.TgCfUserOn;
            cUser.CheckedChanged += (s, e) => { st.TgCfUserOn = cUser.Checked; tbUser.Enabled = cUser.Checked; save(s, e); };
            tbUser.TextChanged += (s, e) => { st.TgCfUser = tbUser.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbUser, Tx("Свои домены за Cloudflare через запятую. Пусто/выключено — выбираются автоматически"));
            fc.CheckField(cUser, tbUser);
            var cWorker = Ui.Check("Cloudflare Worker", st.TgWorkerOn);
            var tbWorker = Ui.TextBox(st.TgWorker); tbWorker.Enabled = st.TgWorkerOn;
            cWorker.CheckedChanged += (s, e) => { st.TgWorkerOn = cWorker.Checked; tbWorker.Enabled = cWorker.Checked; save(s, e); };
            tbWorker.TextChanged += (s, e) => { st.TgWorker = tbWorker.Text.Trim(); save(s, e); };
            tip.SetToolTip(cWorker, Tx("Свои Worker-домены (name.account.workers.dev): через них прокси идёт к DC по IP. Пробуются первыми"));
            fc.CheckField(cWorker, tbWorker);
            var cNoTls = Ui.Check(Tx("Без TLS для CF-прокси и Worker (порт 80)"), st.TgNoSecure); cNoTls.CheckedChanged += (s, e) => { st.TgNoSecure = cNoTls.Checked; save(s, e); };
            fc.Check(cNoTls);
            var bCfTest = new FlatButton(Tx("Проверить CF-прокси")) { Glyph = "" }.AutoW();
            var bWTest = new FlatButton(Tx("Проверить Worker")) { Glyph = "" }.AutoW();
            fc.Buttons(bCfTest, bWTest);
            tgpCfRes = result(); fc.Note(tgpCfRes);
            Action<bool> runTest = worker =>
            {
                var list = worker ? TgConst.DomainList(st.TgWorker) : (st.TgCfUserOn && TgConst.DomainList(st.TgCfUser).Count > 0 ? TgConst.DomainList(st.TgCfUser) : TgCfDomains.Defaults());
                if (worker && list.Count == 0) { tgpCfRes.Text = Tx("Сначала впиши домены Worker"); fc.Lay(); relay(); return; }
                bool auto = !worker && !(st.TgCfUserOn && TgConst.DomainList(st.TgCfUser).Count > 0);
                bool secure = !st.TgNoSecure;
                tgpCfRes.Text = Tx("Проверка…"); tgpCfRes.ForeColor = Theme.Muted; bCfTest.Enabled = bWTest.Enabled = false; fc.Lay(); relay();
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var lines = new List<string>(); bool allOk = true;
                    if (auto)
                    {
                        // как run_cfproxy_auto_test: до первого домена, где работают все DC
                        string best = null; var merged = new Dictionary<int, string>();
                        foreach (var d in Enumerable.Reverse(list))
                        {
                            var r = TgHttp.TestCfProxy(d, secure);
                            if (r.Values.All(v => v == null)) { best = d; merged = r; break; }
                            foreach (var kv in r) if (kv.Value == null) { merged[kv.Key] = null; best = best ?? d; } else if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
                        }
                        int ok = merged.Count(kv => kv.Value == null); allOk = best != null && ok == TgHttp.TestDcs.Length;
                        lines.Add(best != null ? "✓ " + Tx("CF-прокси работает: ") + ok + "/" + TgHttp.TestDcs.Length + " DC" : "✗ " + Tx("ни один автоматический домен CF не отвечает"));
                    }
                    else
                        foreach (var d in list)
                        {
                            var r = worker ? TgHttp.TestWorker(d, secure) : TgHttp.TestCfProxy(d, secure);
                            int ok = r.Count(kv => kv.Value == null); if (ok != r.Count) allOk = false;
                            lines.Add((ok == r.Count ? "✓ " : ok == 0 ? "✗ " : "~ ") + TgConst.Censor(d) + ": " + (ok == r.Count ? Tx("все DC") : string.Join(", ", r.Where(kv => kv.Value != null).Select(kv => "DC" + kv.Key + " " + kv.Value))));
                        }
                    var text = string.Join("\n", lines);
                    try { BeginInvoke((Action)(() => { if (tgpCfRes.IsDisposed) return; tgpCfRes.Text = text; tgpCfRes.ForeColor = allOk ? Theme.Green : Theme.Muted; bCfTest.Enabled = bWTest.Enabled = true; fc.Lay(); relay(); })); } catch { }
                });
            };
            bCfTest.Click += (s, e) => runTest(false); bWTest.Click += (s, e) => runTest(true);
            fc.Lay(); tgForms[cc] = fc;
            tgBody.Controls.Add(cc); RegisterModule(cc, "tg.cf", PgTg, "subs");

            // ---- дополнительно ----
            var ca = card(Tx("Дополнительно")); var fa = new TgForm(ca);
            var nBuf = Ui.Num(4, 4096, st.TgBufKb); nBuf.ValueChanged += (s, e) => { st.TgBufKb = (int)nBuf.Value; save(s, e); };
            tip.SetToolTip(nBuf, Tx("Буфер приёма/отправки сокета, КБ (по умолчанию 256)"));
            fa.Row(Tx("Буфер, КБ"), nBuf);
            var nPool = Ui.Num(0, 32, st.TgPool); nPool.ValueChanged += (s, e) => { st.TgPool = (int)nPool.Value; save(s, e); };
            tip.SetToolTip(nPool, Tx("Сколько готовых WebSocket-соединений держать на DC (по умолчанию 4). 0 — без прямого WS"));
            fa.Row(Tx("Пул WebSocket на DC"), nPool);
            var cTest = Ui.Check(Tx("Только тестовые DC Telegram"), st.TgTestDc); cTest.CheckedChanged += (s, e) => { st.TgTestDc = cTest.Checked; save(s, e); };
            tip.SetToolTip(cTest, Tx("Весь трафик — в тестовые дата-центры. Для Telegram Desktop не нужно (тестовые DC определяются сами)"));
            fa.Check(cTest);
            var cVerb = Ui.Check(Tx("Подробный лог"), st.TgVerbose); cVerb.CheckedChanged += (s, e) => { st.TgVerbose = cVerb.Checked; TgLog.Verbose = cVerb.Checked; st.Save(); };
            tip.SetToolTip(cVerb, Tx("Больше подробностей в лог RouteX (вкладка «Лог и опрос», с «Каждый опрос») — для разбора проблем"));
            fa.Check(cVerb);
            var credit = new Label { Text = Tx("Основано на tg-ws-proxy (Flowseal, лицензия MIT)."), Font = Theme.Small, ForeColor = Theme.Dim, AutoSize = false, UseMnemonic = false };
            fa.Note(credit);
            fa.Lay(); tgForms[ca] = fa;
            tgBody.Controls.Add(ca); RegisterModule(ca, "tg.adv", PgTg, "subs");

            Action lay = () =>
            {
                int L = Theme.S(26), T = Theme.S(84), w = PW(p) - L * 2;
                tgHost.SetBounds(L, T, Math.Max(10, w), Math.Max(10, p.ClientSize.Height - T - Theme.S(14)));
                RelayTg();
            };
            relayTg = () => RelayTg();
            p.Resize += (s, e) => lay(); lay();

            if (tgpTimer == null) { tgpTimer = new System.Windows.Forms.Timer { Interval = 1000 }; tgpTimer.Tick += (s, e) => { if (curPage == PgTg || moduleKeys.Any(k => k == "tg.state" && PageOf(k) == curPage)) TgUpdateUi(); }; tgpTimer.Start(); }
            TgUpdateUi();
        }
        Action relayTg;
        readonly Dictionary<CardPanel, TgForm> tgForms = new Dictionary<CardPanel, TgForm>();

        // раскладка вкладки: «состояние» — во всю ширину сверху; остальное — в 1–2 колонки, карточки тянутся по ширине колонки
        bool layingTg;
        void RelayTg()
        {
            if (tgBody == null || tgBody.IsDisposed || layingTg) return;
            layingTg = true;
            try
            {
                var mine = tgBody.Controls.OfType<CardPanel>().OrderBy(c => moduleKeys.IndexOf(c.ModuleKey)).ToList();
                foreach (var c in mine) { TgForm f; if (tgForms.TryGetValue(c, out f) && f.MinH != 0) { f.MinH = 0; f.Lay(); } }
                int gap = Theme.S(14), avail = tgHost.Width - (tgBody.Height > tgHost.Height ? Theme.S(14) : 0);
                int w = Math.Max(mine.Count == 0 ? 0 : mine.Max(c => c.BaseW), avail); // во всю ширину страницы
                int minCol = mine.Where(c => c.ModuleKey != "tg.state").Select(c => c.BaseW).DefaultIfEmpty(TgCardW).Max();
                int cols = w >= minCol * 3 + gap * 2 ? 3 : w >= minCol * 2 + gap ? 2 : 1, colW = (w - gap * (cols - 1)) / cols;
                int y0 = 0;
                var state = mine.FirstOrDefault(c => c.ModuleKey == "tg.state");
                if (state != null) { state.SetBounds(0, 0, w, state.Height); y0 = state.Bottom + gap; }
                var colY = Enumerable.Repeat(y0, cols).ToArray(); var last = new CardPanel[cols];
                foreach (var c in mine)
                {
                    if (c == state) continue;
                    int col = Array.IndexOf(colY, colY.Min());
                    c.SetBounds(col * (colW + gap), colY[col], Math.Max(c.BaseW, colW), c.Height);
                    colY[col] = c.Bottom + gap; last[col] = c;
                }
                // колонки кончаются вровень — по самой длинной или по низу окна (без пустых «дыр» под короткой колонкой)
                int bottom = Math.Max(colY.Max() - gap, tgHost.Height - Theme.S(2));
                for (int k = 0; k < cols; k++)
                {
                    TgForm f; var c = last[k];
                    if (c == null || !tgForms.TryGetValue(c, out f)) continue;
                    int extra = bottom - (colY[k] - gap);
                    if (extra > 0) { f.MinH = c.Height + extra; f.Lay(); }
                }
                tgBody.Height = Math.Max(y0, bottom) + (bottom > tgHost.Height ? Theme.S(10) : 0);
                tgHost.MinContentWidth = mine.Count == 0 ? 0 : w;
                tgHost.Relayout();
            }
            finally { layingTg = false; }
        }
    }

    // плитки со счётчиками TG Proxy: соединения, маршруты, трафик, пул
    public sealed class TgStatsView : DoubleBufferedControl
    {
        string[] vals = { "0", "—", "0 B", "0 B" }, subs = { "", "", "", "" };
        static string[] Heads { get { return new[] { Tr.Tx("Соединения"), Tr.Tx("Маршруты"), Tr.Tx("Отправлено"), Tr.Tx("Получено") }; } }
        public int Needed(int w) { return Theme.S(62) * 2 + Theme.S(8); }
        public void Set(bool run)
        {
            vals = new[] { Interlocked.Read(ref TgStats.Active).ToString(), "WS " + TgStats.Ws + " · CF " + TgStats.Cf, TgConst.Human(Interlocked.Read(ref TgStats.Up)), TgConst.Human(Interlocked.Read(ref TgStats.Down)) };
            subs = new[] { Tr.Tx("активных · всего ") + TgStats.Total + (TgStats.Bad > 0 ? " · " + Tr.Tx("чужих: ") + TgStats.Bad : ""), "H2 " + TgStats.H2 + " · TCP " + TgStats.TcpFb + " · " + Tr.Tx("в пуле ") + (run ? TgProxy.WsPool.Ready : 0), "↑", "↓" };
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            int gap = Theme.S(8), tw = (Width - gap) / 2, th = (Height - gap) / 2, pad = Theme.S(12);
            var tile = Theme.Mix(Theme.Card, Theme.CardHi, 0.75);
            var heads = Heads;
            for (int i = 0; i < 4; i++)
            {
                int x = (i % 2) * (tw + gap), y = (i / 2) * (th + gap);
                var r = new RectangleF(x + 0.5f, y + 0.5f, tw - 1, th - 1);
                Gfx.FillRound(g, tile, r, Theme.R(8));
                Gfx.Text(g, heads[i], Theme.Small, Theme.Muted, new Rectangle(x + pad, y + Theme.S(7), tw - pad * 2, Theme.S(16)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                Gfx.Text(g, vals[i], Theme.H2, Theme.Text, new Rectangle(x + pad, y + Theme.S(21), tw - pad * 2, Theme.S(22)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                if (i < 2) Gfx.Text(g, subs[i], Theme.Small, Theme.Dim, new Rectangle(x + pad, y + th - Theme.S(17), tw - pad * 2, Theme.S(16)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
