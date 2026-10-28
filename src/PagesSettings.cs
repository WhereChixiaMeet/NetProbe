using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        Action loadProxyUi; System.Windows.Forms.Timer charRebuild;
        Panel setBody; ScrollHost setHost; bool loadingSettings; readonly System.Windows.Forms.Timer autoSave = new System.Windows.Forms.Timer { Interval = 700 };
        Label lZapretWarn; ThemedCheck cHybrid;
        TextBox tbProbe, tbCurl, tbProxy, tbPingHost, tbDns, tbSites, tbWiresock, tbZapret, tbLogDirSet, tbGameTcp, tbGameUdp;
        Spin nTimeout, nPingCount, nDown, nUp, nStreams, nGood, nWarn, nExclRefresh;
        DarkCombo cbLang, cbMode, cbAdapter, cbTheme, cbWsLog, cbRetSet;
        ThemedCheck cMinTray, cCloseTray, cWidget, cWidgetTop, cNotify, cAutoMon, cAutorun, cStartMin, cLogFile, cWsAdapter;
        Slider sOpacity; Label lOpacity, lSvc;

        class AdItem { public string Id, Text; public override string ToString() { return Text; } }

        void BuildSettingsPage()
        {
            if (bgDimTimer == null) { bgDimTimer = new System.Windows.Forms.Timer { Interval = 700 }; bgDimTimer.Tick += (s, e) => { bgDimTimer.Stop(); if (!string.IsNullOrEmpty(st.BgImage)) { ResetPageBackground(); RebuildUi(); SelectPage(PgSettings); } }; }
            var p = NewPage(Tx("Настройки"), Tx("Оформление, пути, мониторинг, лог"));
            var bReset = new FlatButton(Tx("По умолчанию")) { Glyph = "\uE777" }.AutoW();
            var bData = new FlatButton(Tx("Папка настроек")) { Glyph = "\uE838" }.AutoW();
            bReset.Click += (s, e) =>
            {
                if (MessageBox.Show(this, Tx("Сбросить все настройки по умолчанию? Туннели, списки приложений и стратегии DPI не удаляются."), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                ApplySettings(new Settings { WidgetX = st.WidgetX, WidgetY = st.WidgetY, PrimaryTunnel = st.PrimaryTunnel, BackupTunnel = st.BackupTunnel, DpiStrategy = st.DpiStrategy });
                LoadSettingsUi();
            };
            bData.Click += (s, e) => Process.Start("explorer.exe", "\"" + Settings.Dir + "\"");
            setHost = new ScrollHost { BackColor = Theme.Bg }; setBody = setHost.Content;
            var bExp = new FlatButton(Tx("Экспорт")) { Glyph = "\uEDE1" }.AutoW();
            var bImpAll = new FlatButton(Tx("Импорт")) { Glyph = "\uE8B5" }.AutoW();
            bExp.Click += (s, e) =>
            {
                using (var d = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "netprobe-settings-" + DateTime.Now.ToString("yyyyMMdd") + ".json" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    try { File.WriteAllText(d.FileName, Backup.Export(), new System.Text.UTF8Encoding(false)); Logger.Write(LogLevel.Info, Tx("Настройки экспортированы: ") + d.FileName); MessageBox.Show(this, Tx("Сохранено. В файле есть ключи туннелей и серверов — храните его в надёжном месте."), "NetProbe", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "NetProbe"); }
                }
            };
            bImpAll.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = "JSON (*.json)|*.json" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    if (MessageBox.Show(this, Tx("Импортировать настройки? Текущие настройки, списки, туннели и серверы с теми же именами будут заменены."), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    string err = Backup.Import(File.ReadAllText(d.FileName, System.Text.Encoding.UTF8));
                    if (err != null) { MessageBox.Show(this, err, "NetProbe"); return; }
                    Logger.Write(LogLevel.Info, Tx("Настройки импортированы: ") + d.FileName);
                    ReloadAll();
                }
            };
            p.Controls.AddRange(new Control[] { bReset, bData, bExp, bImpAll, setHost });

            int y = 0; int lw = Theme.S(270), cw = Theme.S(420); // влезает и в минимальное окно (1040 px)
            Action<string> section = t =>
            {
                y += y == 0 ? 0 : Theme.S(18);
                var l = Ui.Label(t.ToUpper(), Theme.Bold, Theme.Muted); l.Location = new Point(0, y); setBody.Controls.Add(l);
                var line = new Label { BackColor = Theme.Border, Location = new Point(0, y + Theme.S(22)), Size = new Size(lw + cw, 1) }; setBody.Controls.Add(line);
                y += Theme.S(32);
            };
            Action<string, Control, string> row = (label, c, hint) =>
            {
                var l = Ui.Label(label, Theme.Body, Theme.Text); l.Location = new Point(0, y + Theme.S(4)); setBody.Controls.Add(l);
                c.Location = new Point(lw, y); if (c is TextBox || c is DarkCombo) c.Width = cw;
                setBody.Controls.Add(c);
                int h = Math.Max(c.Height, Theme.S(26));
                if (hint != null)
                {
                    var hl = Ui.Hint(hint, cw); hl.BackColor = Theme.Bg;
                    hl.Location = new Point(lw, y + h + Theme.S(2)); setBody.Controls.Add(hl);
                    h += hl.Height + Theme.S(2);
                }
                y += h + Theme.S(12);
            };
            Action<ThemedCheck> check = c => { c.BackColor = Theme.Bg; c.Location = new Point(lw, y); setBody.Controls.Add(c); y += Theme.S(30); };
            Func<TextBox, bool, FlatButton> browse = (tb, folder) =>
            {
                var b = new FlatButton("…") { Width = Theme.S(36), Height = Theme.S(26) };
                b.Click += (s, e) =>
                {
                    if (folder) { using (var d = new FolderBrowserDialog { SelectedPath = tb.Text }) if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath; }
                    else { using (var d = new OpenFileDialog { Filter = Tx("Программы (*.exe)|*.exe"), FileName = tb.Text }) if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.FileName; }
                };
                b.Location = new Point(lw + cw + Theme.S(6), tb.Top); setBody.Controls.Add(b);
                return b;
            };

            section(Tx("Оформление"));
            cbTheme = new DarkCombo(); cbTheme.Items.AddRange(new object[] { Tx("Тёмная (монохром)"), Tx("Светлая (монохром)"), Tx("Классическая Windows"), "Chisa (Personal)", "Kurumi Tokisaki (Personal)" });
            row(Tx("Тема"), cbTheme, null);
            var accent = new AccentPicker { Value = Theme.AccentColor };
            accent.Picked += c => { st.Accent = Theme.ColorStr(c); st.Save(); Theme.AccentColor = c; BeginInvoke((Action)(() => { Theme.Apply((ThemeKind)st.Theme); RebuildUi(); SelectPage(PgSettings); })); };
            row(Tx("Цвет акцента"), accent, null);
            cbLang = new DarkCombo(); cbLang.Items.AddRange(Tr.Names);
            row(Tx("Язык / Language"), cbLang, null);
            Func<string, string, Action<string>, FlatButton> pick = (label, filter, set) =>
            {
                var b = new FlatButton(Tx("Выбрать…")) { Glyph = "\uE8B9" }.AutoW();
                b.Click += (s, e) => { using (var d = new OpenFileDialog { Filter = filter }) if (d.ShowDialog(this) == DialogResult.OK) set(d.FileName); };
                row(label, b, null);
                var clr = new FlatButton(Tx("Сбросить")) { Glyph = "\uE777" }.AutoW();
                clr.Location = new Point(b.Right + Theme.S(8), b.Top); clr.Click += (s, e) => set(""); setBody.Controls.Add(clr);
                return b;
            };
            pick(Tx("Фон приложения"), Tx("Картинки") + " (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp", f => { st.BgImage = f; st.Save(); ResetPageBackground(); BeginInvoke((Action)(() => { RebuildUi(); SelectPage(PgSettings); })); });
            var nDim = Ui.Num(0, 95, st.BgDim); nDim.ValueChanged += (s, e) => { st.BgDim = (int)nDim.Value; st.Save(); bgDimTimer.Stop(); bgDimTimer.Start(); };
            row(Tx("Затемнение фона, %"), nDim, null);
            var nOp = Ui.Num(20, 100, st.CardOpacity); nOp.ValueChanged += (s, e) => { st.CardOpacity = (int)nOp.Value; st.Save(); bgDimTimer.Stop(); bgDimTimer.Start(); };
            row(Tx("Непрозрачность карточек, %"), nOp, null);
            pick(Tx("Иконка приложения"), Tx("Иконки и картинки") + " (*.ico;*.png;*.jpg;*.exe)|*.ico;*.png;*.jpg;*.jpeg;*.exe", f => { st.AppIconPath = f; st.Save(); ApplyAppIcon(); });
            if (Theme.Personal)
            {
                // Personal-темы: персонаж — картинки только локально (config\themes\<chisa|kurumi>\), в git и в exe их нет.
                // 3.0.6: картинку выбираешь сам (любой PNG копируется в папку темы), положение — мышкой («Разместить»).
                // пересборка страниц (картинка внутри окна живёт в фоне страниц) — отложенно, вне обработчика нажатия
                if (charRebuild == null) { charRebuild = new System.Windows.Forms.Timer { Interval = 300 }; charRebuild.Tick += (s2, e2) => { charRebuild.Stop(); if (IsDisposed) return; ResetPageBackground(); RebuildUi(); SelectPage(PgSettings); }; }
                Action rebuild = () => { charRebuild.Stop(); charRebuild.Start(); };
                Func<string, Panel> artRow = file =>
                {
                    var pn = new Panel { BackColor = Color.Transparent, Height = Theme.S(30) };
                    var bPick = new FlatButton(Tx("Выбрать PNG…")) { Glyph = "\uE8B9" }.AutoW();
                    var bDel = new FlatButton(Tx("Убрать")) { Glyph = "\uE74D" }.AutoW();
                    var lInfo = new Label { AutoSize = true, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, UseMnemonic = false };
                    Action info = () => { lInfo.Text = CharOverlay.HasArt(Theme.Kind, file) ? Tx("есть") : Tx("нет картинки"); };
                    bPick.Click += (s2, e2) =>
                    {
                        using (var d = new OpenFileDialog { Filter = "PNG (*.png)|*.png", Title = Tx("Картинка персонажа (PNG с прозрачностью)") })
                        {
                            if (d.ShowDialog(this) != DialogResult.OK) return;
                            try
                            {
                                using (var test = new Bitmap(d.FileName)) { } // битый файл — сразу понятная ошибка, а не падение потом
                                File.Copy(d.FileName, Path.Combine(CharOverlay.Folder(Theme.Kind), file), true);
                                Logger.Write(LogLevel.Info, Tx("Персонаж: ") + Path.GetFileName(d.FileName) + " → " + file);
                            }
                            catch (Exception ex) { MessageBox.Show(this, ex.Message, "NetProbe"); return; }
                        }
                        info(); UpdateChars(); if (file == "window.png" || !st.CharFrame) rebuild();
                    };
                    bDel.Click += (s2, e2) =>
                    {
                        try { File.Delete(Path.Combine(CharOverlay.Folder(Theme.Kind), file)); } catch { }
                        info(); UpdateChars(); if (file == "window.png" || !st.CharFrame) rebuild();
                    };
                    bDel.Location = new Point(bPick.Right + Theme.S(8), 0); lInfo.Location = new Point(bDel.Right + Theme.S(10), Theme.S(6));
                    pn.Controls.AddRange(new Control[] { bPick, bDel, lInfo }); pn.Width = cw; info();
                    return pn;
                };
                var cCI = Ui.Check(Tx("Персонаж у окна"), st.CharWindow); check(cCI);
                cCI.CheckedChanged += (s, e) => { if (loadingSettings) return; st.CharWindow = cCI.Checked; st.Save(); UpdateChars(); if (!st.CharFrame) rebuild(); };
                var cbWhere = new DarkCombo(); cbWhere.Items.AddRange(new object[] { Tx("За рамкой окна (можно двигать мышкой)"), Tx("Внутри окна, за карточками") });
                cbWhere.SelectedIndex = st.CharFrame ? 0 : 1;
                cbWhere.SelectedIndexChanged += (s, e) => { if (!cbWhere.UserPick) return; st.CharFrame = cbWhere.SelectedIndex == 0; st.Save(); UpdateChars(); rebuild(); };
                row(Tx("Где"), cbWhere, null);
                row(Tx("Картинка за рамкой"), artRow("frame.png"), null);
                row(Tx("Картинка внутри окна"), artRow("window.png"), Tx("Нет своей — берётся картинка «за рамкой»"));
                var nIn = Ui.Num(10, 100, st.CharInsideSize > 0 ? st.CharInsideSize : (Theme.Kurumi ? 78 : 55));
                nIn.ValueChanged += (s, e) => { if (loadingSettings) return; st.CharInsideSize = (int)nIn.Value; st.Save(); if (!st.CharFrame) rebuild(); };
                row(Tx("Внутри: высота, % окна"), nIn, null);
                var cbAnchor = new DarkCombo(); cbAnchor.Items.AddRange(new object[] { Tx("Слева снизу"), Tx("Справа снизу"), Tx("Справа сверху"), Tx("Слева сверху"), Tx("По центру") });
                cbAnchor.SelectedIndex = st.CharInsideAnchor >= 0 ? st.CharInsideAnchor : (Theme.Kurumi ? 0 : 2);
                cbAnchor.SelectedIndexChanged += (s, e) => { if (!cbAnchor.UserPick) return; st.CharInsideAnchor = cbAnchor.SelectedIndex; st.Save(); if (!st.CharFrame) rebuild(); };
                row(Tx("Внутри: угол"), cbAnchor, null);
                var cCW = Ui.Check(Tx("Персонаж у виджета"), st.CharWidget); check(cCW); cCW.CheckedChanged += (s, e) => { if (loadingSettings) return; st.CharWidget = cCW.Checked; st.Save(); UpdateChars(); };
                row(Tx("Картинка у виджета"), artRow("widget.png"), null);
                // положение мышкой (за рамкой окна и у виджета)
                var pPos = new Panel { BackColor = Color.Transparent, Height = Theme.S(30), Width = cw };
                var bPlace = new FlatButton(Tx("Разместить мышкой")) { Glyph = "\uE7C2" }.AutoW();
                var bResetPos = new FlatButton(Tx("Сбросить положение")) { Glyph = "\uE777" }.AutoW();
                bPlace.Click += (s, e) => SetCharEdit(!charEdit);
                Action placeText = () => { bPlace.Text = charEdit ? Tx("Готово") : Tx("Разместить мышкой"); bPlace.Primary = charEdit; bPlace.AutoW(); bResetPos.Left = bPlace.Right + Theme.S(8); bPlace.Invalidate(); };
                charEditChanged = () => { if (!bPlace.IsDisposed) placeText(); };
                bResetPos.Click += (s, e) => { st.SetCharLayout((Theme.Kurumi ? "kurumi." : "chisa.") + "frame", null); st.SetCharLayout((Theme.Kurumi ? "kurumi." : "chisa.") + "widget", null); st.Save(); UpdateChars(); };
                bResetPos.Location = new Point(bPlace.Right + Theme.S(8), 0);
                pPos.Controls.AddRange(new Control[] { bPlace, bResetPos });
                row(Tx("Положение"), pPos, Tx("Тяни персонажа мышкой, колёсико — размер, правая кнопка или «Готово» — закончить"));
                placeText();
                var nCO = Ui.Num(20, 100, st.CharOpacity); nCO.ValueChanged += (s, e) => { if (loadingSettings) return; st.CharOpacity = (int)nCO.Value; st.Save(); UpdateChars(); };
                row(Tx("Непрозрачность персонажа, %"), nCO, null);
            }
            var cIcons = Ui.Check(Tx("Иконки приложений в списках"), st.ShowAppIcons); check(cIcons);
            cIcons.CheckedChanged += (s, e) => { st.ShowAppIcons = cIcons.Checked; st.Save(); SetupListCards(); };
            cbLang.SelectedIndexChanged += (s, e) => { if (!cbLang.UserPick) return; string l = Tr.Codes[Math.Max(0, cbLang.SelectedIndex)]; if (l == st.Lang) return; st.Lang = l; st.Save(); Tr.Set(l); BeginInvoke((Action)(() => { RebuildUi(); RebuildTrayMenu(); SelectPage(PgSettings); })); }; // применяется без перезапуска
            cbTheme.SelectedIndexChanged += (s, e) => { if (cbTheme.UserPick && cbTheme.SelectedIndex != st.Theme) { st.Theme = cbTheme.SelectedIndex; st.Save(); BeginInvoke((Action)(() => { Theme.Apply((ThemeKind)st.Theme); RebuildUi(); SelectPage(PgSettings); })); } };

            section("VPN · WireSock");
            tbWiresock = Ui.TextBox(); row("wiresock-client.exe", tbWiresock, null);
            browse(tbWiresock, false);
            cbWsLog = new DarkCombo(); cbWsLog.Items.AddRange(new object[] { "none", "info", "debug", "all" });
            row(Tx("Уровень лога WireSock"), cbWsLog, null);
            cWsAdapter = Ui.Check(Tx("Виртуальный сетевой адаптер (-lac)"), false); check(cWsAdapter);
            lSvc = Ui.Label("", Theme.Small, Theme.Muted);
            var bSvc = new FlatButton(Tx("Остановить службу WireSock / TunnlTo")) { Glyph = "\uE71A" }.AutoW();
            bSvc.Click += (s, e) =>
            {
                if (MessageBox.Show(this, Tx("Остановить службу wiresock-client-service? Туннель TunnlTo будет выключен. Чтобы TunnlTo не включал её снова, отключите в нём «Enable on system start»."), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                bool ok = WiresockRunner.StopService(); Logger.Write(ok ? LogLevel.Ok : LogLevel.Error, ok ? Tx("Служба wiresock-client-service остановлена") : Tx("Не удалось остановить wiresock-client-service")); UpdateSvc();
            };
            row(Tx("Служба TunnlTo"), bSvc, null);

            section("DPI · zapret");
            tbZapret = Ui.TextBox(); ((ThemedTextBox)tbZapret).Cue = System.IO.Path.Combine(Settings.Root, "zapret");
            row(Tx("Папка zapret"), tbZapret, null);
            browse(tbZapret, true);
            // не найдено → жёлтая плашка (по умолчанию — папка zapret рядом с NetProbe.exe)
            lZapretWarn = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Yellow, BackColor = Theme.Mix(Theme.Yellow, Theme.Bg, 0.86), UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.S(8), 0, 0, 0) };
            lZapretWarn.SetBounds(lw, y - Theme.S(6), cw, Theme.S(24)); setBody.Controls.Add(lZapretWarn); y += Theme.S(24);
            tbGameTcp = Ui.TextBox(); row(Tx("Игровой фильтр · TCP порты"), tbGameTcp, null);
            tbGameUdp = Ui.TextBox(); row(Tx("Игровой фильтр · UDP порты"), tbGameUdp, null);
            nExclRefresh = Ui.Num(3, 300, 10); row(Tx("Обновлять IP исключённых приложений, с"), nExclRefresh, null);
            section(Tx("Источники"));
            cHybrid = Ui.Check(Tx("Режим гибрида"), st.HybridMode); check(cHybrid);
            var lHy = Ui.Hint(Tx("Primary VPN и подписки разрешены одновременно; на вкладке Subscriptions появляется тумблер «Primary VPN ⇄ Subscriptions». Выкл — авто: есть туннель → VPN, нет → подписки."), cw); lHy.BackColor = Theme.Bg;
            lHy.Location = new Point(lw, y - Theme.S(4)); setBody.Controls.Add(lHy); y += lHy.Height + Theme.S(8);
            cHybrid.CheckedChanged += (s, e) =>
            {
                if (cHybrid.Checked == st.HybridMode) return;
                if (cHybrid.Checked && !ConfirmHybrid()) { cHybrid.Checked = false; return; }
                st.HybridMode = cHybrid.Checked; st.Save();
                Logger.Write(LogLevel.Info, Tx("Режим гибрида ") + (st.HybridMode ? Tx("включён") : Tx("выключен")));
                if (!st.HybridMode && xm.Active && TunnelCount > 0) xm.Disconnect(); // вне гибрида при наличии туннелей подписки выключены
                UpdateModeUi(); UpdateServersUi();
            };
            section(Tx("Подписки"));
            // 3.0.2: настройки прокси xray перенесены сюда с вкладки Subscriptions
            var cSys = Ui.Check(Tx("Системный прокси Windows"), st.SysProxy); cSys.CheckedChanged += (s, e) => { if (loadingSettings) return; st.SysProxy = cSys.Checked; st.Save(); if (xm.Active && !st.TunMode) { if (st.SysProxy) SysProxy.Set("127.0.0.1:" + st.HttpPort); else SysProxy.Clear(); } };
            var cRu = Ui.Check(Tx("RU-сайты напрямую"), st.BypassRu); cRu.CheckedChanged += (s, e) => { if (loadingSettings) return; st.BypassRu = cRu.Checked; st.Save(); xm.Reapply(); };
            var cAds = Ui.Check(Tx("Блокировать рекламу"), st.BlockAds); cAds.CheckedChanged += (s, e) => { if (loadingSettings) return; st.BlockAds = cAds.Checked; st.Save(); xm.Reapply(); };
            var cLan = Ui.Check(Tx("Разрешить подключения из LAN"), st.AllowLan); cLan.CheckedChanged += (s, e) => { if (loadingSettings) return; st.AllowLan = cLan.Checked; st.Save(); xm.Reapply(); };
            var cXAuto = Ui.Check(Tx("Подключаться при запуске"), st.XrayAutoConnect); cXAuto.CheckedChanged += (s, e) => { if (loadingSettings) return; st.XrayAutoConnect = cXAuto.Checked; st.Save(); };
            foreach (var c in new[] { cSys, cRu, cAds, cLan, cXAuto }) check(c);
            y += Theme.S(4);
            var nSocks = Ui.Num(1024, 65000, st.SocksPort); nSocks.ValueChanged += (s, e) => { if (loadingSettings) return; st.SocksPort = (int)nSocks.Value; st.Save(); xm.Reapply(); };
            var nHttp = Ui.Num(1024, 65000, st.HttpPort); nHttp.ValueChanged += (s, e) => { if (loadingSettings) return; st.HttpPort = (int)nHttp.Value; st.Save(); xm.Reapply(); };
            row("SOCKS", nSocks, null); row("HTTP", nHttp, null);
            // URL проверки / скорости: своё значение или выбор из списка (как в v2rayN)
            Func<TextBox, string[], FlatButton> presets = (tb, list) =>
            {
                var b = new FlatButton("") { Glyph = "\uE70D", Width = Theme.S(36), Height = Theme.S(26) };
                var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
                foreach (var u in list) { var uu = u; cm.Items.Add(uu, null, (s2, e2) => tb.Text = uu); }
                b.Click += (s2, e2) => cm.Show(b, new Point(b.Width - cm.Width, b.Height));
                b.Location = new Point(lw + cw + Theme.S(6), tb.Top); setBody.Controls.Add(b);
                Ui.Tip().SetToolTip(b, Tx("Выбрать из списка"));
                return b;
            };
            var tbTestUrl = Ui.TextBox(st.TestUrl); tbTestUrl.TextChanged += (s, e) => { if (loadingSettings) return; st.TestUrl = tbTestUrl.Text.Trim(); st.Save(); };
            row(Tx("URL проверки"), tbTestUrl, null); presets(tbTestUrl, Settings.TestUrls);
            var tbSpeedUrl = Ui.TextBox(st.SpeedUrl); tbSpeedUrl.TextChanged += (s, e) => { if (loadingSettings) return; st.SpeedUrl = tbSpeedUrl.Text.Trim(); st.Save(); };
            row(Tx("URL скорости"), tbSpeedUrl, null); presets(tbSpeedUrl, Settings.SpeedUrls);
            var nSpdSec = Ui.Num(3, 60, st.SpeedSec); nSpdSec.ValueChanged += (s, e) => { if (loadingSettings) return; st.SpeedSec = (int)nSpdSec.Value; st.Save(); };
            row(Tx("Замер скорости, с"), nSpdSec, null);
            loadProxyUi = () =>
            {
                cSys.Checked = st.SysProxy; cRu.Checked = st.BypassRu; cAds.Checked = st.BlockAds; cLan.Checked = st.AllowLan; cXAuto.Checked = st.XrayAutoConnect;
                nSocks.Value = Math.Max(1024, Math.Min(65000, st.SocksPort)); nHttp.Value = Math.Max(1024, Math.Min(65000, st.HttpPort));
                tbTestUrl.Text = st.TestUrl; tbSpeedUrl.Text = st.SpeedUrl; nSpdSec.Value = Math.Max(3, Math.Min(60, st.SpeedSec));
            };
            var bSrvView = new FlatButton(Tx("Сбросить вид таблицы")) { Glyph = "\uE777" }.AutoW();
            bSrvView.Click += (s, e) => ResetSrvColumns();
            row(Tx("Таблица серверов"), bSrvView, Tx("Ширина, порядок и видимость колонок — как по умолчанию"));
            section(Tx("Обновления"));
            var bZUp = new FlatButton(Tx("Обновить zapret")) { Glyph = "\uE896" }.AutoW();
            bZUp.Click += (s, e) => UpdateZapretUi(bZUp);
            row("zapret", bZUp, null);
            lZVer = Ui.Label("", Theme.Small, Theme.Muted); lZVer.Location = new Point(bZUp.Right + Theme.S(12), bZUp.Top + Theme.S(8)); setBody.Controls.Add(lZVer);
            var bXUp = new FlatButton(Tx("Обновить xray")) { Glyph = "\uE896" }.AutoW();
            bXUp.Click += (s, e) => { SelectPage(PgServers); UpdateCore(true); };
            row("xray", bXUp, null);
            lXVer = Ui.Label("", Theme.Small, Theme.Muted); lXVer.Location = new Point(bXUp.Right + Theme.S(12), bXUp.Top + Theme.S(8)); setBody.Controls.Add(lXVer);
            var bSUp = new FlatButton(Tx("Обновить sing-box")) { Glyph = "\uE896" }.AutoW();
            bSUp.Click += (s, e) => { SelectPage(PgServers); UpdateSingBoxUi(true); };
            row("sing-box (TUN)", bSUp, null);
            lSVer = Ui.Label("", Theme.Small, Theme.Muted); lSVer.Location = new Point(bSUp.Right + Theme.S(12), bSUp.Top + Theme.S(8)); setBody.Controls.Add(lSVer);
            var bCheck = new FlatButton(Tx("Проверить обновления")) { Glyph = "\uE72C" }.AutoW();
            bCheck.Click += (s, e) => CheckUpdates(true);
            row("", bCheck, null);
            bCheck.Click += (s, e) => CheckAppUpdate(false);
            var bApp = new FlatButton(Tx("Проверить обновления NetProbe")) { Glyph = "\uE72C" }.AutoW();
            bApp.Click += (s, e) => CheckAppUpdate(true);
            row("NetProbe", bApp, null);
            lAppVer = Ui.Label("v" + Version, Theme.Small, Theme.Muted); lAppVer.Location = new Point(bApp.Right + Theme.S(12), bApp.Top + Theme.S(8)); setBody.Controls.Add(lAppVer);
            var cNotifyUpd = Ui.Check(Tx("Уведомлять о новых версиях"), st.UpdNotify); check(cNotifyUpd);
            var cbNotifyEvery = new DarkCombo(); cbNotifyEvery.Items.AddRange(new object[] { Tx("Сразу"), Tx("День"), Tx("Неделя"), Tx("Месяц") });
            cbNotifyEvery.SelectedIndex = st.AppNotifyEvery;
            cbNotifyEvery.SelectedIndexChanged += (s, e) => { if (!cbNotifyEvery.UserPick) return; st.AppNotifyEvery = Math.Max(0, cbNotifyEvery.SelectedIndex); st.Save(); ScheduleAppNotify(); };
            row(Tx("Частота уведомлений"), cbNotifyEvery, Tx("Одно уведомление на версию. «Сразу» — как только версия вышла; День / Неделя / Месяц — не чаще этого. Кнопка «Обновить» в меню слева остаётся, пока не обновишься."));
            var cDelay = Ui.Check(Tx("Уведомлять с задержкой после выхода версии, мин"), st.AppNotifyDelayOn);
            var nDelay = Ui.Num(0, 1440, st.AppNotifyDelayMin); nDelay.Width = Theme.S(80);
            cDelay.BackColor = Theme.Bg; cDelay.Location = new Point(lw, y); setBody.Controls.Add(cDelay);
            nDelay.Location = new Point(lw + cDelay.PreferredSize.Width + Theme.S(12), y - Theme.S(2)); setBody.Controls.Add(nDelay); y += Theme.S(34);
            nDelay.Enabled = st.AppNotifyDelayOn && st.UpdNotify; cbNotifyEvery.Enabled = cDelay.Enabled = st.UpdNotify;
            cDelay.CheckedChanged += (s, e) => { st.AppNotifyDelayOn = cDelay.Checked; st.Save(); nDelay.Enabled = cDelay.Checked; ScheduleAppNotify(); };
            nDelay.ValueChanged += (s, e) => { st.AppNotifyDelayMin = (int)nDelay.Value; st.Save(); ScheduleAppNotify(); };
            Ui.Tip().SetToolTip(cDelay, Tx("Уведомление придёт не раньше чем через N мин после публикации релиза — чтобы не ловить релиз, к которому ещё не загрузился exe или который сразу заменили хотфиксом"));
            cNotifyUpd.CheckedChanged += (s, e) => { st.UpdNotify = cNotifyUpd.Checked; st.Save(); cbNotifyEvery.Enabled = cDelay.Enabled = st.UpdNotify; nDelay.Enabled = st.UpdNotify && st.AppNotifyDelayOn; ScheduleAppNotify(); };
            var cAutoUpd = Ui.Check(Tx("Проверять обновления автоматически"), st.CheckUpdates); cAutoUpd.CheckedChanged += (s, e) => { st.CheckUpdates = cAutoUpd.Checked; st.Save(); }; check(cAutoUpd);
            var cUz = Ui.Check(Tx("Следить за zapret"), st.UpdZapret); cUz.CheckedChanged += (s, e) => { st.UpdZapret = cUz.Checked; st.Save(); }; check(cUz);
            var cUx = Ui.Check(Tx("Следить за xray"), st.UpdXray); cUx.CheckedChanged += (s, e) => { st.UpdXray = cUx.Checked; st.Save(); }; check(cUx);
            var cbEvery = new DarkCombo(); cbEvery.Items.AddRange(new object[] { Tx("Только при запуске"), Tx("Каждые 6 часов"), Tx("Каждые 12 часов"), Tx("Раз в день"), Tx("Раз в неделю") });
            cbEvery.SelectedIndex = st.UpdEvery; cbEvery.SelectedIndexChanged += (s, e) => { st.UpdEvery = Math.Max(0, cbEvery.SelectedIndex); st.Save(); };
            row(Tx("Как часто (zapret / xray / sing-box)"), cbEvery, null);


            section(Tx("Мониторинг · канал VPN"));
            tbCurl = Ui.TextBox(); row(Tx("Путь к curl.exe"), tbCurl, null);
            tbProxy = Ui.TextBox(); row(Tx("Прокси для VPN-канала"), tbProxy, null);
            string binDir = Path.Combine(CurlProber.WorkDir, "bin");
            var bBin = new FlatButton(Tx("Папка пробников")) { Glyph = "\uE838" }.AutoW();
            bBin.Click += (s, e) => { Directory.CreateDirectory(binDir); Process.Start("explorer.exe", "\"" + binDir + "\""); };
            row(Tx("Пробники"), bBin, null);

            section(Tx("Мониторинг · канал напрямую"));
            cbMode = new DarkCombo(); cbMode.Items.AddRange(new object[] { Tx("netprobe-direct.exe (копия curl) с привязкой к физическому адаптеру"), "PowerShell → .NET HttpWebRequest" });
            row(Tx("Способ"), cbMode, null);
            cbAdapter = new DarkCombo();
            row(Tx("Физический адаптер"), cbAdapter, null);
            var bRef = new FlatButton(Tx("Обновить адаптеры")) { Glyph = "\uE72C" }.AutoW();
            bRef.Click += (s, e) => { RefreshAdapters(false); FillAdapters(cbAdapter.SelectedItem is AdItem ? ((AdItem)cbAdapter.SelectedItem).Id : "auto"); };
            bRef.Location = new Point(lw, y); setBody.Controls.Add(bRef); y += bRef.Height + Theme.S(12);
            tbProbe = Ui.TextBox(); row(Tx("URL опроса"), tbProbe, null);
            nTimeout = Ui.Num(2, 30, 5); row(Tx("Таймаут, с"), nTimeout, null);

            section(Tx("Тесты"));
            tbPingHost = Ui.TextBox(); row(Tx("Хост теста MTU (ICMP)"), tbPingHost, null);
            nPingCount = Ui.Num(5, 200, 20); row(Tx("Замеров задержки за тест"), nPingCount, null);
            nDown = Ui.Num(1, 500, 25); row(Tx("Объём загрузки, МБ"), nDown, null);
            nUp = Ui.Num(1, 200, 10); row(Tx("Объём отдачи, МБ"), nUp, null);
            nStreams = Ui.Num(1, 8, 4); row(Tx("Параллельных потоков"), nStreams, null);
            nGood = Ui.Num(10, 2000, 120); row(Tx("Хорошая задержка до, мс"), nGood, null);
            nWarn = Ui.Num(20, 5000, 300); row(Tx("Медленная задержка от, мс"), nWarn, null);
            tbDns = Ui.TextBox(); row(Tx("Домены для теста DNS"), tbDns, Tx("Через пробел."));
            tbSites = Ui.TextBox("", true); tbSites.Height = Theme.S(140); tbSites.Font = Theme.Body; row(Tx("Сайты для проверки"), tbSites, Tx("Один адрес на строку."));

            section(Tx("Лог"));
            tbLogDirSet = Ui.TextBox(); row(Tx("Папка лога"), tbLogDirSet, null);
            browse(tbLogDirSet, true);
            cbRetSet = new DarkCombo(); cbRetSet.Items.AddRange(Settings.RetentionNames);
            row(Tx("Хранить и показывать лог"), cbRetSet, null);
            cLogFile = Ui.Check(Tx("Писать лог в файл"), true); check(cLogFile);

            section(Tx("Окно, трей и виджет"));
            cMinTray = Ui.Check(Tx("Сворачивать в трей"), true); check(cMinTray);
            cCloseTray = Ui.Check(Tx("Крестик прячет в трей (выход — из меню трея)"), true); check(cCloseTray);
            cWidget = Ui.Check(Tx("Показывать виджет на рабочем столе в трее"), true); check(cWidget);
            cWidgetTop = Ui.Check(Tx("Виджет поверх всех окон"), true); check(cWidgetTop);
            sOpacity = new Slider { Min = 30, Max = 100, Step = 1, Width = Theme.S(300) };
            lOpacity = Ui.Label("", Theme.Body, Theme.Muted);
            sOpacity.ValueChanged += (s, e) => lOpacity.Text = ((int)sOpacity.Value) + " %";
            row(Tx("Прозрачность виджета"), sOpacity, null);
            cNotify = Ui.Check(Tx("Уведомлять об обрывах, переключениях и восстановлении"), true); check(cNotify);
            cAutoMon = Ui.Check(Tx("Запускать мониторинг при старте"), true); check(cAutoMon);
            cAutorun = Ui.Check(Tx("Запускать вместе с Windows (планировщик, с правами администратора)"), false); check(cAutorun);
            cStartMin = Ui.Check(Tx("Запускать свёрнутым в трей"), false); check(cStartMin);
            setBody.Height = y + Theme.S(20);
            // автосохранение: любое изменение сохраняется через 0.7 с, без кнопки Save
            autoSave.Tick += (s, e) => { autoSave.Stop(); if (!loadingSettings && !IsDisposed) SaveSettingsUi(true); };
            foreach (Control c in setBody.Controls)
            {
                if (c == cbTheme || c == cbLang) continue;
                EventHandler kick = (s, e) => { if (!loadingSettings) { autoSave.Stop(); autoSave.Start(); } };
                if (c is TextBox) ((TextBox)c).TextChanged += kick;
                else if (c is Spin) ((Spin)c).ValueChanged += kick;
                else if (c is DarkCombo) ((DarkCombo)c).SelectedIndexChanged += kick;
                else if (c is ThemedCheck) ((ThemedCheck)c).CheckedChanged += kick;
                else if (c is Slider) ((Slider)c).ValueChanged += kick;
            }

            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2;
                bReset.Location = new Point(L + W - bReset.Width, Theme.S(24));
                bData.Location = new Point(bReset.Left - Theme.S(8) - bData.Width, Theme.S(24));
                bImpAll.Location = new Point(bData.Left - Theme.S(8) - bImpAll.Width, Theme.S(24));
                bExp.Location = new Point(bImpAll.Left - Theme.S(8) - bExp.Width, Theme.S(24));
                setHost.SetBounds(L, Theme.S(86), W, p.ClientSize.Height - Theme.S(86) - Theme.S(10));
                setHost.Relayout();
            };
            p.Resize += (s, e) => lay();
            lay();
        }

        Label lZVer, lXVer, lSVer, lAppVer;
        System.Windows.Forms.Timer bgDimTimer;
        bool updZapret, updXray;

        void ApplyUpdateBadges()
        {
            if (navs.Count > PgSettings) navs[PgSettings].Badge = updZapret || updXray;
            if (navs.Count > PgServers) navs[PgServers].Badge = updXray;
        }

        // Плановая проверка: при запуске и далее по расписанию из настроек
        void ScheduledUpdateCheck(bool startup)
        {
            if (!st.CheckUpdates) return;
            double hours = st.UpdEvery == 1 ? 6 : st.UpdEvery == 2 ? 12 : st.UpdEvery == 3 ? 24 : st.UpdEvery == 4 ? 168 : -1;
            var last = st.LastUpdCheck > 0 ? DateTime.FromOADate(st.LastUpdCheck) : DateTime.MinValue;
            if (startup || (hours > 0 && (DateTime.Now - last).TotalHours >= hours)) CheckUpdates(false); // NetProbe проверяется отдельно (appUpdTimer)
        }

        // Установленная и последняя версии zapret / xray (GitHub API)
        void CheckUpdates(bool manual)
        {
            if (lZVer != null && !lZVer.IsDisposed) { lZVer.Text = Tx("проверка…"); lXVer.Text = Tx("проверка…"); }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string zc = Updater.ZapretVersion(st), xc = Updater.XrayVersion(st), sc = Updater.SingBoxVersion(st), zl = null, xl = null, sl = null, err = null;
                if ((manual || st.UpdXray) && (sc != "" || manual)) try { sl = Updater.SingBoxLatest().Tag; } catch { }
                if (manual || st.UpdZapret) try { zl = Updater.Latest(Updater.ZapretRepo, n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).Tag; } catch (Exception ex) { err = ex.Message; }
                if (manual || st.UpdXray) try { xl = Updater.Latest(Updater.XrayRepo, n => n.StartsWith("Xray-windows", StringComparison.OrdinalIgnoreCase)).Tag; } catch (Exception ex) { err = ex.Message; }
                st.LastUpdCheck = DateTime.Now.ToOADate(); st.Save();
                Func<string, string, string> fmt = (cur, lat) => (cur == "" ? Tx("не установлен") : cur) + (lat == null ? "" : lat.TrimStart('v') == cur.TrimStart('v') ? "  ·  " + Tx("последняя") : "  →  " + lat + " " + Tx("доступна"));
                bool zNew = zl != null && zl.TrimStart('v') != zc.TrimStart('v'), xNew = xl != null && xc != "" && xl.TrimStart('v') != xc.TrimStart('v');
                xrayLatest = xl; sbLatest = sl;
                bool sNew = sl != null && sc != "" && sl.TrimStart('v') != sc.TrimStart('v');
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (lZVer != null && !lZVer.IsDisposed) { lZVer.Text = fmt(zc, zl) + (err != null && zl == null ? "  ·  " + err : ""); lXVer.Text = fmt(xc, xl); if (lSVer != null) lSVer.Text = fmt(sc, sl); }
                        UpdateCoreLabel();
                        updZapret = zNew; updXray = xNew || sNew; ApplyUpdateBadges();
                        if (!manual && (zNew || xNew || sNew) && st.UpdNotify && tray != null)
                            try { tray.ShowBalloonTip(5000, Tx("NetProbe: есть обновления"), (zNew ? "zapret " + zl + "  " : "") + (xNew ? "xray " + xl + "  " : "") + (sNew ? "sing-box " + sl : ""), System.Windows.Forms.ToolTipIcon.Info); } catch { }
                        if (zNew || xNew) Logger.Write(LogLevel.Info, Tx("Доступны обновления: ") + (zNew ? "zapret " + zl + " " : "") + (xNew ? "xray " + xl : ""));
                    }));
                }
                catch { }
            });
        }

        void UpdateZapretUi(FlatButton b)
        {
            if (router.Active) { MessageBox.Show(this, Tx("Сначала отключите VPN/DPI на вкладке «Подключение»."), "NetProbe"); return; }
            if (MessageBox.Show(this, Tx("Скачать последнюю версию zapret с GitHub (Flowseal/zapret-discord-youtube)? Ваши списки *-user.txt сохранятся."), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            b.Enabled = false; string old = b.Text;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string res;
                try { res = Updater.UpdateZapret(st, m => { try { BeginInvoke((Action)(() => b.Text = m.Length > 40 ? m.Substring(0, 40) + "…" : m)); } catch { } }); }
                catch (Exception ex) { res = Tx("Ошибка обновления: ") + ex.Message; }
                Logger.Write(LogLevel.Info, res);
                try { BeginInvoke((Action)(() => { b.Enabled = true; b.Text = old; MessageBox.Show(this, res, "NetProbe"); FillStrategies(st.DpiStrategy); LoadConnectUi(); CheckUpdates(true); })); } catch { }
            });
        }

        void UpdateSvc()
        {
            if (lSvc == null || lSvc.IsDisposed) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                bool run = WiresockRunner.ServiceRunning();
                try { BeginInvoke((Action)(() => { if (!lSvc.IsDisposed) lSvc.Text = run ? Tx("служба запущена (конфликтует с NetProbe)") : Tx("служба не запущена"); })); } catch { }
            });
        }

        void FillAdapters(string sel)
        {
            cbAdapter.Items.Clear();
            var auto = Adapters.PickDirect(adapters, "auto");
            cbAdapter.Items.Add(new AdItem { Id = "auto", Text = Tx("Авто") + (auto != null ? "  (" + auto.Name + " — " + auto.Ip + ")" : "") });
            foreach (var a in adapters)
                cbAdapter.Items.Add(new AdItem { Id = a.Id, Text = a.Name + " — " + a.Ip + (a.IsVpn ? "  [VPN]" : a.IsVirtual ? Tx("  [виртуальный]") : a.Gateway == null ? Tx("  [нет шлюза]") : "") });
            cbAdapter.SelectedIndex = 0;
            for (int i = 0; i < cbAdapter.Items.Count; i++) if (((AdItem)cbAdapter.Items[i]).Id == sel) cbAdapter.SelectedIndex = i;
        }

        void LoadSettingsUi()
        {
            if (tbCurl == null || tbCurl.IsDisposed) return;
            loadingSettings = true;
            try { LoadSettingsCore(); if (loadProxyUi != null) loadProxyUi(); } finally { loadingSettings = false; }
        }

        void LoadSettingsCore()
        {
            cbTheme.SelectedIndex = st.Theme; cbLang.SelectedIndex = Tr.IndexOf(st.Lang);
            tbWiresock.Text = st.WiresockPath; cbWsLog.SelectedItem = st.WiresockLogLevel; if (cbWsLog.SelectedIndex < 0) cbWsLog.SelectedIndex = 1; cWsAdapter.Checked = st.WiresockAdapter;
            tbZapret.Text = st.ZapretDir;
            string zc = ZapretRunner.Check(st.ZapretPath); lZapretWarn.Visible = zc != null; lZapretWarn.Text = zc != null ? "⚠  " + Tx("zapret не найден: ") + st.ZapretPath : "";
            cHybrid.Checked = st.HybridMode; tbGameTcp.Text = st.GameTcp; tbGameUdp.Text = st.GameUdp; nExclRefresh.Value = st.ExclRefreshSec;
            tbCurl.Text = st.CurlPath; tbProxy.Text = st.VpnProxy; cbMode.SelectedIndex = st.DirectMode;
            FillAdapters(st.DirectInterface);
            tbProbe.Text = st.ProbeUrl; nTimeout.Value = st.TimeoutSec;
            tbPingHost.Text = st.PingHost; nPingCount.Value = st.PingCount; nDown.Value = st.DownloadMB; nUp.Value = st.UploadMB; nStreams.Value = st.Streams;
            nGood.Value = st.GoodMs; nWarn.Value = st.WarnMs; tbDns.Text = st.DnsDomains; tbSites.Text = st.Sites.Replace("\n", "\r\n");
            tbLogDirSet.Text = st.LogDir; cbRetSet.SelectedIndex = st.LogRetention; cLogFile.Checked = st.LogToFile;
            cMinTray.Checked = st.MinimizeToTray; cCloseTray.Checked = st.CloseToTray; cWidget.Checked = st.ShowWidget; cWidgetTop.Checked = st.WidgetTopMost;
            sOpacity.Value = st.WidgetOpacity; lOpacity.Text = st.WidgetOpacity + " %";
            cNotify.Checked = st.Notify; cAutoMon.Checked = st.AutoStartMonitor; cAutorun.Checked = st.Autorun; cStartMin.Checked = st.StartMinimized;
            UpdateSvc();
        }

        void SaveSettingsUi(bool auto = false)
        {
            var n = st.Clone();
            n.WiresockPath = tbWiresock.Text.Trim(); n.WiresockLogLevel = (string)cbWsLog.SelectedItem ?? "info"; n.WiresockAdapter = cWsAdapter.Checked;
            n.ZapretDir = tbZapret.Text.Trim(); n.GameTcp = tbGameTcp.Text.Trim(); n.GameUdp = tbGameUdp.Text.Trim(); n.ExclRefreshSec = (int)nExclRefresh.Value;
            n.CurlPath = tbCurl.Text.Trim(); n.VpnProxy = tbProxy.Text.Trim(); n.DirectMode = Math.Max(0, cbMode.SelectedIndex);
            n.DirectInterface = cbAdapter.SelectedItem is AdItem ? ((AdItem)cbAdapter.SelectedItem).Id : "auto";
            n.ProbeUrl = tbProbe.Text.Trim(); n.TimeoutSec = (int)nTimeout.Value;
            n.PingHost = tbPingHost.Text.Trim(); n.PingCount = (int)nPingCount.Value; n.DownloadMB = (int)nDown.Value; n.UploadMB = (int)nUp.Value; n.Streams = (int)nStreams.Value;
            n.GoodMs = (int)nGood.Value; n.WarnMs = (int)nWarn.Value; n.DnsDomains = tbDns.Text.Trim(); n.Sites = tbSites.Text.Replace("\r", "").Trim();
            n.LogDir = tbLogDirSet.Text.Trim(); n.LogRetention = Math.Max(0, cbRetSet.SelectedIndex); n.LogToFile = cLogFile.Checked;
            n.MinimizeToTray = cMinTray.Checked; n.CloseToTray = cCloseTray.Checked; n.ShowWidget = cWidget.Checked; n.WidgetTopMost = cWidgetTop.Checked;
            n.WidgetOpacity = (int)sOpacity.Value; n.Notify = cNotify.Checked; n.AutoStartMonitor = cAutoMon.Checked; n.Autorun = cAutorun.Checked;
            n.StartMinimized = cStartMin.Checked;
            if (n.LogDir != "" && !Directory.Exists(n.LogDir)) { try { Directory.CreateDirectory(n.LogDir); } catch (Exception ex) { MessageBox.Show(this, Tx("Папка лога недоступна: ") + ex.Message, "NetProbe"); return; } }
            ApplySettings(n);
            if (!auto) LoadSettingsUi();
            Logger.Write(LogLevel.Info, Tx("Настройки сохранены"));
        }

        bool? autorunApplied;

        // После импорта: перечитать всё и пересобрать интерфейс
        void ReloadAll()
        {
            router.Disconnect(); xm.Disconnect();
            st = Settings.Load();
            Theme.AccentColor = Theme.ParseColor(st.Accent); Tr.Set(st.Lang); Theme.Apply((ThemeKind)st.Theme);
            AppLists.ZapretLists = Path.Combine(st.ZapretPath, "lists");
            Logger.Dir = st.LogPath; Logger.Retention = st.Retention; Logger.ToFile = st.LogToFile;
            ServerStore.Load();
            RebuildUi(); RebuildTrayMenu(); SelectPage(PgSettings);
            autorunApplied = null; SetAutorun(st.Autorun); autorunApplied = st.Autorun;
            MessageBox.Show(this, Tx("Настройки импортированы."), "NetProbe", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        void ApplySettings(Settings n)
        {
            n.Clamp();
            bool dpiChanged = n.ZapretDir != st.ZapretDir || n.GameTcp != st.GameTcp || n.GameUdp != st.GameUdp;
            bool themeChanged = n.Theme != st.Theme;
            st = n; st.Save();
            Logger.ToFile = st.LogToFile; Logger.Dir = st.LogPath; Logger.Retention = st.Retention;
            AppLists.ZapretLists = Path.Combine(st.ZapretPath, "lists");
            chart.Good = st.GoodMs; chart.Warn = st.WarnMs; chart.Invalidate();
            pollSlider.Max = st.MaxPollRate; pollSlider.Value = st.PollRate; st.PollRate = pollSlider.Value;
            var upd = pollSlider.Tag as Action; if (upd != null) upd();
            pollValue.Text = PollText();
            chkLogPolls.Checked = st.LogPolls;
            if (cbRetention != null) cbRetention.SelectedIndex = st.LogRetention;
            UpdateLogDirLabel(); FillLogDays();
            miWidget.Checked = st.ShowWidget;
            widget.TopMost = st.WidgetTopMost; widget.Opacity = st.WidgetOpacity / 100.0;
            if (autorunApplied == null || st.Autorun != autorunApplied.Value) { SetAutorun(st.Autorun); autorunApplied = st.Autorun; }
            RefreshAdapters(false);
            monVpn.Kick(); monDir.Kick();
            if (dpiChanged) router.RestartDpi();
            if (themeChanged) { Theme.Apply((ThemeKind)st.Theme); RebuildUi(); SelectPage(PgSettings); }
            LoadConnectUi();
        }

        // Автозапуск через планировщик задач: NetProbe требует прав администратора, а ключ Run их не даёт
        // своя задача на каждую копию приложения (основная и чистая не мешают друг другу)
        static string TaskName { get { return Settings.Root.TrimEnd('\\').EndsWith("\\NetProbe", StringComparison.OrdinalIgnoreCase) ? "NetProbe" : "NetProbe " + ServerEntry.MakeId(Application.ExecutablePath.ToLowerInvariant()).Substring(0, 6); } }

        void SetAutorun(bool on)
        {
            try
            {
                if (on)
                {
                    var r = Proc.Run("schtasks.exe", "/Create /F /TN \"" + TaskName + "\" /SC ONLOGON /RL HIGHEST /DELAY 0000:15 /TR \"\\\"" + Application.ExecutablePath + "\\\" --minimized\"", 15000, Proc.Oem);
                    if (r.Exit != 0) Logger.Write(LogLevel.Error, Tx("Автозапуск: ") + (r.Out + r.Err).Trim());
                }
                else
                {
                    var q = Proc.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"", 8000, Proc.Oem);
                    if (q.Exit == 0) Proc.Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"", 8000, Proc.Oem);
                }
            }
            catch (Exception ex) { Logger.Write(LogLevel.Error, Tx("Автозапуск: ") + ex.Message); }
        }
    }
}
