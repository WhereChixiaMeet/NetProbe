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
        Label tgpState, tgpLink, tgpStats, tgpErr, tgpDcRes, tgpCfRes;
        Toggle tgpOn;
        System.Windows.Forms.Timer tgpTimer, tgpApply;
        bool tgpLogHooked, tgpLoading;
        static int TgCardW { get { return Theme.S(460); } }

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
            tgpState.Text = run ? "● " + Tx("Работает") + "  ·  " + c.Host + ":" + c.Port : st.TgOn && TgProxy.LastError != "" ? "● " + Tx("Ошибка: ") + TgProxy.LastError : "○ " + Tx("Выключен");
            tgpState.ForeColor = run ? Theme.Green : st.TgOn && TgProxy.LastError != "" ? Theme.Red : Theme.Muted;
            tgpLink.Text = cur != null ? TgProxy.Link(cur) : "";
            if (tgpErr != null) { tgpErr.Text = err ?? ""; tgpErr.Visible = err != null; }
            if (tgpOn != null && tgpOn.On != st.TgOn) { tgpLoading = true; tgpOn.On = st.TgOn; tgpLoading = false; }
            tgpStats.Text = Tx("Соединения: ") + Interlocked.Read(ref TgStats.Active) + Tx(" активных · ") + TgStats.Total + Tx(" всего") +
                            "  ·  WS " + TgStats.Ws + "  CF " + TgStats.Cf + "  H2 " + TgStats.H2 + "  TCP " + TgStats.TcpFb + (TgStats.Bad > 0 ? "  ·  " + Tx("чужих: ") + TgStats.Bad : "") +
                            "\n↑ " + TgConst.Human(Interlocked.Read(ref TgStats.Up)) + "   ↓ " + TgConst.Human(Interlocked.Read(ref TgStats.Down)) + "  ·  " + Tx("готово в пуле: ") + (run ? TgProxy.WsPool.Ready : 0);
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

        void BuildTgPage()
        {
            var p = NewPage("TG Proxy", Tx("Telegram Desktop через WebSocket: свой MTProto-прокси на этом компьютере"));
            tgHost = new ScrollHost { BackColor = Theme.Bg };
            tgBody = tgHost.Content;
            p.Controls.Add(tgHost);
            int W = TgCardW, ip = Theme.S(16), lx = Theme.S(170), cw = W - lx - ip;
            var tip = Ui.Tip();
            EventHandler save = (s, e) => { if (tgpLoading) return; st.Save(); TgUpdateUi(); if (st.TgOn) TgApplySoon(); };
            Func<string, CardPanel> card = t => new CardPanel { Title = t, Width = W };
            Func<CardPanel, string, Control, int, int> row = (cardP, label, ctl, ry) =>
            {
                var l = Ui.Label(label, Theme.Body, Theme.Text); l.BackColor = Theme.Card; l.Location = new Point(ip, ry + Theme.S(4)); cardP.Controls.Add(l);
                ctl.Location = new Point(lx, ry); if (ctl is TextBox) ctl.Width = cw; cardP.Controls.Add(ctl);
                return ry + Math.Max(ctl.Height, Theme.S(26)) + Theme.S(10);
            };
            Func<CardPanel, ThemedCheck, int, int> check = (cardP, chk, ry) => { chk.BackColor = Theme.Card; chk.Location = new Point(ip, ry); cardP.Controls.Add(chk); return ry + Theme.S(28); };
            Func<string, Label> res = txt => new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, Width = W - ip * 2, Height = Theme.S(18), UseMnemonic = false, Text = txt };

            // ---- состояние: вкл/выкл, ссылка, кнопки, счётчики ----
            var cs = card(Tx("TG Proxy · состояние"));
            tgpOn = new Toggle { On = st.TgOn };
            tgpOn.Changed += (s, e) => { if (tgpLoading) return; st.TgOn = tgpOn.On; st.Save(); TgApply(); TgUpdateUi(); };
            tip.SetToolTip(tgpOn, Tx("Включить прокси. Включённый запускается вместе с NetProbe"));
            tgpState = new Label { AutoSize = false, Font = Theme.H2, BackColor = Theme.Card, Width = W - ip * 2 - tgpOn.Width - Theme.S(8), Height = Theme.S(26), UseMnemonic = false };
            tgpLink = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, Width = W - ip * 2, Height = Theme.S(18), AutoEllipsis = true, UseMnemonic = false };
            var bOpen = new FlatButton(Tx("Открыть в Telegram"), true) { Glyph = "" }.AutoW();
            bOpen.Click += (s, e) => TgOpenInTelegram();
            tip.SetToolTip(bOpen, Tx("Telegram сам предложит подключить этот прокси (ссылка tg://proxy)"));
            var bCopy = new FlatButton(Tx("Скопировать ссылку")) { Glyph = "" }.AutoW();
            bCopy.Click += (s, e) => { try { Clipboard.SetText(tgpLink.Text); Logger.Write(LogLevel.Info, Tx("Ссылка TG Proxy скопирована")); } catch { } };
            var bRestart = new FlatButton(Tx("Перезапустить")) { Glyph = "" }.AutoW();
            bRestart.Click += (s, e) => { if (!st.TgOn) { st.TgOn = true; st.Save(); } TgApply(); };
            tgpStats = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, Width = W - ip * 2, Height = Theme.S(34), UseMnemonic = false };
            tgpErr = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Red, BackColor = Theme.Card, Width = W - ip * 2, Height = Theme.S(18), UseMnemonic = false, Visible = false };
            var how = Ui.Hint(Tx("Вручную: Telegram → Настройки → Продвинутые → Тип соединения → Прокси → MTProto: адрес, порт и секрет «dd…» из ссылки."), W - ip * 2);
            how.BackColor = Theme.Card;
            cs.Controls.AddRange(new Control[] { tgpOn, tgpState, tgpLink, bOpen, bCopy, bRestart, tgpStats, tgpErr, how });
            tgpOn.Location = new Point(W - ip - tgpOn.Width, Theme.S(12));
            tgpState.Location = new Point(ip, Theme.S(44)); tgpLink.Location = new Point(ip, tgpState.Bottom + Theme.S(2));
            bOpen.Location = new Point(ip, tgpLink.Bottom + Theme.S(10)); bCopy.Location = new Point(bOpen.Right + Theme.S(8), bOpen.Top); bRestart.Location = new Point(bCopy.Right + Theme.S(8), bOpen.Top);
            tgpStats.Location = new Point(ip, bOpen.Bottom + Theme.S(10)); tgpErr.Location = new Point(ip, tgpStats.Bottom + Theme.S(2));
            how.Location = new Point(ip, tgpErr.Bottom + Theme.S(4));
            cs.Height = how.Bottom + Theme.S(14);
            tgBody.Controls.Add(cs); RegisterModule(cs, "tg.state", PgTg, "subs");

            // ---- MTProto ----
            var cm = card(Tx("MTProto-подключение"));
            int y = Theme.S(48);
            var tbHost = Ui.TextBox(st.TgHost); tbHost.TextChanged += (s, e) => { st.TgHost = tbHost.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbHost, Tx("Адрес, на котором слушает прокси: 127.0.0.1 — только этот компьютер, 0.0.0.0 — вся сеть"));
            y = row(cm, Tx("IP-адрес"), tbHost, y);
            var nPort = Ui.Num(1, 65535, st.TgPort); nPort.ValueChanged += (s, e) => { st.TgPort = (int)nPort.Value; save(s, e); };
            tip.SetToolTip(nPort, Tx("Порт прокси — тот же должен стоять в Telegram"));
            y = row(cm, Tx("Порт"), nPort, y);
            var tbSecret = Ui.TextBox(st.TgSecret); tbSecret.Width = cw - Theme.S(44);
            tbSecret.TextChanged += (s, e) => { st.TgSecret = tbSecret.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbSecret, Tx("Секрет для авторизации клиента: 32 шестнадцатеричных символа"));
            var bNew = new FlatButton("") { Glyph = "", IconOnly = true }.AutoW();
            bNew.Click += (s, e) => { tbSecret.Text = TgConst.NewSecret(); };
            tip.SetToolTip(bNew, Tx("Новый случайный секрет (в Telegram понадобится новая ссылка)"));
            y = row(cm, Tx("Секрет"), tbSecret, y); tbSecret.Width = cw - bNew.Width - Theme.S(6);
            bNew.Location = new Point(tbSecret.Right + Theme.S(6), tbSecret.Top - (bNew.Height - tbSecret.Height) / 2); cm.Controls.Add(bNew);
            var tbFake = Ui.TextBox(st.TgFakeTls); tbFake.TextChanged += (s, e) => { st.TgFakeTls = tbFake.Text.Trim(); save(s, e); };
            tip.SetToolTip(tbFake, Tx("Fake TLS (ee-секрет): прокси выглядит как HTTPS этого сайта; чужие подключения уходят на сам сайт. Пусто — выключено"));
            y = row(cm, Tx("Fake TLS (домен)"), tbFake, y);
            var cPP = Ui.Check(Tx("Принимать PROXY protocol (за nginx/haproxy)"), st.TgProxyProto); cPP.CheckedChanged += (s, e) => { st.TgProxyProto = cPP.Checked; save(s, e); };
            y = check(cm, cPP, y);
            cm.Height = y + Theme.S(8);
            tgBody.Controls.Add(cm); RegisterModule(cm, "tg.mtproto", PgTg, "subs");

            // ---- DC → IP ----
            var cd2 = card(Tx("Дата-центры Telegram (DC → IP)"));
            var hint = Ui.Hint(Tx("По правилу на строку: номер:IP (например 4:149.154.167.220). Эти DC идут прямым WebSocket к web.telegram.org; остальные и при сбое — запасными путями."), W - ip * 2);
            hint.BackColor = Theme.Card; hint.Location = new Point(ip, Theme.S(46)); cd2.Controls.Add(hint);
            var tbDc = Ui.TextBox(string.Join("\r\n", TgConst.DomainList(st.TgDcIp)), true); tbDc.SetBounds(ip, hint.Bottom + Theme.S(8), W - ip * 2, Theme.S(84));
            tbDc.TextChanged += (s, e) => { st.TgDcIp = string.Join(", ", TgConst.DomainList(tbDc.Text)); save(s, e); };
            cd2.Controls.Add(tbDc);
            var bDcDef = new FlatButton(Tx("По умолчанию")) { Glyph = "" }.AutoW();
            bDcDef.Click += (s, e) => tbDc.Text = TgConst.DefaultDcIp.Replace(", ", "\r\n");
            var bDcTest = new FlatButton(Tx("Проверить")) { Glyph = "" }.AutoW();
            tgpDcRes = res("");
            bDcTest.Click += (s, e) =>
            {
                Dictionary<int, string> rules; try { rules = TgConst.ParseDcIp(st.TgDcIp); } catch (FormatException ex) { tgpDcRes.Text = Tx("Неверная запись DC:IP: ") + ex.Message; return; }
                tgpDcRes.Text = Tx("Проверка…"); bDcTest.Enabled = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var r = TgHttp.TestDirect(rules);
                    string text = r.Count == 0 ? Tx("нет правил — весь трафик пойдёт запасными путями") : string.Join("  ", r.Select(kv => "DC" + kv.Key + (kv.Value == null ? " ✓" : " ✗ " + kv.Value)));
                    try { BeginInvoke((Action)(() => { if (tgpDcRes.IsDisposed) return; tgpDcRes.Text = text; bDcTest.Enabled = true; })); } catch { }
                });
            };
            tip.SetToolTip(bDcTest, Tx("Проверить прямой WebSocket к каждому DC из списка"));
            bDcDef.Location = new Point(ip, tbDc.Bottom + Theme.S(10)); bDcTest.Location = new Point(bDcDef.Right + Theme.S(8), bDcDef.Top);
            tgpDcRes.Location = new Point(ip, bDcDef.Bottom + Theme.S(6)); tgpDcRes.AutoEllipsis = true;
            cd2.Controls.AddRange(new Control[] { bDcDef, bDcTest, tgpDcRes });
            cd2.Height = tgpDcRes.Bottom + Theme.S(12);
            tgBody.Controls.Add(cd2); RegisterModule(cd2, "tg.dc", PgTg, "subs");

            // ---- Cloudflare: CF-прокси, HTTP/2, свои домены, Worker ----
            var cc = card("Cloudflare");
            y = Theme.S(48);
            var cCf = Ui.Check(Tx("CF-прокси для недоступных DC"), st.TgCf); cCf.CheckedChanged += (s, e) => { st.TgCf = cCf.Checked; save(s, e); };
            tip.SetToolTip(cCf, Tx("Запасной путь через домены за Cloudflare. Это сторонние серверы (по умолчанию — домены автора tg-ws-proxy)"));
            y = check(cc, cCf, y);
            var cH2 = Ui.Check(Tx("Медиа через HTTP/2 (одно соединение на DC)"), st.TgH2); cH2.CheckedChanged += (s, e) => { st.TgH2 = cH2.Checked; save(s, e); };
            tip.SetToolTip(cH2, Tx("Загрузка медиа через CF одним HTTP/2-соединением. Нужны CF-прокси и TLS; Windows 10 1607+"));
            y = check(cc, cH2, y);
            var cUser = Ui.Check(Tx("Свои домены CF-прокси"), st.TgCfUserOn); cUser.CheckedChanged += (s, e) => { st.TgCfUserOn = cUser.Checked; save(s, e); };
            y = check(cc, cUser, y);
            var tbUser = Ui.TextBox(st.TgCfUser); tbUser.SetBounds(ip + Theme.S(24), y, W - ip * 2 - Theme.S(24), tbUser.Height);
            tbUser.TextChanged += (s, e) => { st.TgCfUser = tbUser.Text.Trim(); save(s, e); }; cc.Controls.Add(tbUser);
            tip.SetToolTip(tbUser, Tx("Свои домены за Cloudflare через запятую. Пусто/выключено — выбираются автоматически"));
            y = tbUser.Bottom + Theme.S(10);
            var cWorker = Ui.Check("Cloudflare Worker", st.TgWorkerOn); cWorker.CheckedChanged += (s, e) => { st.TgWorkerOn = cWorker.Checked; save(s, e); };
            tip.SetToolTip(cWorker, Tx("Свои Worker-домены (name.account.workers.dev): через них прокси идёт к DC по IP. Пробуются первыми"));
            y = check(cc, cWorker, y);
            var tbWorker = Ui.TextBox(st.TgWorker); tbWorker.SetBounds(ip + Theme.S(24), y, W - ip * 2 - Theme.S(24), tbWorker.Height);
            tbWorker.TextChanged += (s, e) => { st.TgWorker = tbWorker.Text.Trim(); save(s, e); }; cc.Controls.Add(tbWorker);
            y = tbWorker.Bottom + Theme.S(10);
            var cNoTls = Ui.Check(Tx("Без TLS для CF-прокси и Worker (порт 80)"), st.TgNoSecure); cNoTls.CheckedChanged += (s, e) => { st.TgNoSecure = cNoTls.Checked; save(s, e); };
            y = check(cc, cNoTls, y);
            var bCfTest = new FlatButton(Tx("Проверить CF-прокси")) { Glyph = "" }.AutoW();
            var bWTest = new FlatButton(Tx("Проверить Worker")) { Glyph = "" }.AutoW();
            tgpCfRes = res(""); tgpCfRes.Height = Theme.S(36);
            Action<bool> runTest = worker =>
            {
                var list = worker ? TgConst.DomainList(st.TgWorker) : (st.TgCfUserOn && TgConst.DomainList(st.TgCfUser).Count > 0 ? TgConst.DomainList(st.TgCfUser) : TgCfDomains.Defaults());
                if (worker && list.Count == 0) { tgpCfRes.Text = Tx("Сначала впиши домены Worker"); return; }
                bool auto = !worker && !(st.TgCfUserOn && TgConst.DomainList(st.TgCfUser).Count > 0);
                bool secure = !st.TgNoSecure;
                tgpCfRes.Text = Tx("Проверка…"); bCfTest.Enabled = bWTest.Enabled = false;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    var lines = new List<string>();
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
                        int ok = merged.Count(kv => kv.Value == null);
                        lines.Add(best != null ? "✓ " + Tx("CF-прокси работает: ") + ok + "/" + TgHttp.TestDcs.Length + " DC" : "✗ " + Tx("ни один автоматический домен CF не отвечает"));
                    }
                    else
                        foreach (var d in list)
                        {
                            var r = worker ? TgHttp.TestWorker(d, secure) : TgHttp.TestCfProxy(d, secure);
                            int ok = r.Count(kv => kv.Value == null);
                            lines.Add((ok == r.Count ? "✓ " : ok == 0 ? "✗ " : "~ ") + TgConst.Censor(d) + ": " + (ok == r.Count ? Tx("все DC") : string.Join(", ", r.Where(kv => kv.Value != null).Select(kv => "DC" + kv.Key + " " + kv.Value))));
                        }
                    var text = string.Join("\n", lines);
                    try { BeginInvoke((Action)(() => { if (tgpCfRes.IsDisposed) return; tgpCfRes.Text = text; bCfTest.Enabled = bWTest.Enabled = true; })); } catch { }
                });
            };
            bCfTest.Click += (s, e) => runTest(false); bWTest.Click += (s, e) => runTest(true);
            bCfTest.Location = new Point(ip, y + Theme.S(2)); bWTest.Location = new Point(bCfTest.Right + Theme.S(8), bCfTest.Top);
            tgpCfRes.Location = new Point(ip, bCfTest.Bottom + Theme.S(6));
            cc.Controls.AddRange(new Control[] { bCfTest, bWTest, tgpCfRes });
            cc.Height = tgpCfRes.Bottom + Theme.S(12);
            tgBody.Controls.Add(cc); RegisterModule(cc, "tg.cf", PgTg, "subs");

            // ---- дополнительно ----
            var ca = card(Tx("Дополнительно"));
            y = Theme.S(48);
            var nBuf = Ui.Num(4, 4096, st.TgBufKb); nBuf.ValueChanged += (s, e) => { st.TgBufKb = (int)nBuf.Value; save(s, e); };
            tip.SetToolTip(nBuf, Tx("Буфер приёма/отправки сокета, КБ (по умолчанию 256)"));
            y = row(ca, Tx("Буфер, КБ"), nBuf, y);
            var nPool = Ui.Num(0, 32, st.TgPool); nPool.ValueChanged += (s, e) => { st.TgPool = (int)nPool.Value; save(s, e); };
            tip.SetToolTip(nPool, Tx("Сколько готовых WebSocket-соединений держать на DC (по умолчанию 4). 0 — без прямого WS"));
            y = row(ca, Tx("Пул WebSocket на DC"), nPool, y);
            var cTest = Ui.Check(Tx("Только тестовые DC Telegram"), st.TgTestDc); cTest.CheckedChanged += (s, e) => { st.TgTestDc = cTest.Checked; save(s, e); };
            tip.SetToolTip(cTest, Tx("Весь трафик — в тестовые дата-центры. Для Telegram Desktop не нужно (тестовые DC определяются сами)"));
            y = check(ca, cTest, y);
            var cVerb = Ui.Check(Tx("Подробный лог"), st.TgVerbose); cVerb.CheckedChanged += (s, e) => { st.TgVerbose = cVerb.Checked; TgLog.Verbose = cVerb.Checked; st.Save(); };
            tip.SetToolTip(cVerb, Tx("Больше подробностей в лог NetProbe (вкладка «Лог и опрос», с «Каждый опрос») — для разбора проблем"));
            y = check(ca, cVerb, y);
            var credit = Ui.Hint(Tx("Основано на tg-ws-proxy (Flowseal, лицензия MIT)."), W - ip * 2); credit.BackColor = Theme.Card; credit.Location = new Point(ip, y + Theme.S(4)); ca.Controls.Add(credit);
            ca.Height = credit.Bottom + Theme.S(12);
            tgBody.Controls.Add(ca); RegisterModule(ca, "tg.adv", PgTg, "subs");

            foreach (var c in new[] { cs, cm, cd2, cc, ca }) c.RememberBase();

            Action lay = () =>
            {
                int L = Theme.S(26), T = Theme.S(84), w = PW(p) - L * 2, gap = Theme.S(14);
                tgHost.SetBounds(L, T, Math.Max(10, w), Math.Max(10, p.ClientSize.Height - T - Theme.S(14)));
                RelayTg();
            };
            relayTg = lay;
            p.Resize += (s, e) => lay(); lay();

            if (tgpTimer == null) { tgpTimer = new System.Windows.Forms.Timer { Interval = 1000 }; tgpTimer.Tick += (s, e) => { if (curPage == PgTg || moduleKeys.Any(k => k == "tg.state" && PageOf(k) == curPage)) TgUpdateUi(); }; tgpTimer.Start(); }
            TgUpdateUi();
        }
        Action relayTg;

        // карточки вкладки — колонками по ширине (две, если влезают), порядок — как расставил пользователь
        void RelayTg()
        {
            if (tgBody == null || tgBody.IsDisposed) return;
            var mine = tgBody.Controls.OfType<CardPanel>().OrderBy(c => moduleKeys.IndexOf(c.ModuleKey)).ToList();
            int gap = Theme.S(14), w = tgHost.Width - Theme.S(14);
            int cw = mine.Count == 0 ? TgCardW : mine.Max(c => c.BaseW);
            int cols = Math.Max(1, (w + gap) / (cw + gap));
            var colY = new int[cols];
            foreach (var c in mine) { if (c.Width != c.BaseW) c.Width = c.BaseW; int col = Array.IndexOf(colY, colY.Min()); c.Location = new Point(col * (cw + gap), colY[col]); colY[col] = c.Bottom + gap; }
            tgBody.Height = (mine.Count == 0 ? 0 : colY.Max()) + Theme.S(10);
            tgHost.MinContentWidth = mine.Count == 0 ? 0 : cw + Theme.S(2);
            tgHost.Relayout();
        }
    }
}
