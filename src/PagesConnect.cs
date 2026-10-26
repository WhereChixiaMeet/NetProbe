using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public class ModeCard : DoubleBufferedControl
    {
        public string Glyph = "", Title = "", Desc = "";
        bool sel, hover;
        public bool Selected { get { return sel; } set { sel = value; Invalidate(); } }
        public ModeCard() { Cursor = Cursors.Hand; Height = Theme.S(64); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Color fill = sel ? Theme.CardHi : hover ? Theme.Mix(Theme.Card, Theme.CardHi, 0.5) : Theme.Card;
            if (Theme.Translucent) fill = Color.FromArgb(Theme.CardFill.A, fill);
            if (Theme.Classic) { using (var b = new SolidBrush(fill)) g.FillRectangle(b, r); Gfx.Bevel(g, Rectangle.Round(r), !sel); if (sel) using (var p = new Pen(Theme.Accent, 2)) g.DrawRectangle(p, 2, 2, Width - 5, Height - 5); }
            else { Gfx.FillRound(g, fill, r, Theme.R(10)); Gfx.DrawRound(g, sel ? Theme.Accent : Theme.Border, r, Theme.R(10), sel ? 1.6f : 1f); }
            int pad = Theme.S(14);
            using (var f = new Font("Segoe MDL2 Assets", 15f))
                Gfx.Text(g, Glyph, f, sel ? Theme.Accent : Theme.Muted, new Rectangle(pad, 0, Theme.S(28), Height), TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            Gfx.Text(g, Title, Theme.H2, Theme.Text, new Rectangle(pad + Theme.S(36), Theme.S(10), Width - pad * 2 - Theme.S(40), Theme.S(24)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Desc, Theme.Small, Theme.Muted, new Rectangle(pad + Theme.S(36), Theme.S(34), Width - pad * 2 - Theme.S(40), Theme.S(20)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    public class StatusBoard : DoubleBufferedControl
    {
        public string State = "", Line1 = "", Line2 = "";
        public Level Lv = Level.Off;
        public StatusBoard() { Height = Theme.S(84); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Panel(g, r, Theme.R(12), Theme.Card);
            int pad = Theme.S(20);
            Gfx.Dot(g, Theme.LevelColor(Lv), pad + Theme.S(10), Theme.S(30), Theme.S(10), Lv != Level.Off);
            using (var f = new Font(Theme.H1.FontFamily, Theme.Classic ? 13f : 15f, Theme.Classic ? FontStyle.Bold : FontStyle.Regular))
                Gfx.Text(g, State, f, Theme.Text, new Rectangle(pad + Theme.S(32), Theme.S(12), Width - pad * 2 - Theme.S(32), Theme.S(36)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Line1, Theme.Small, Theme.Text, new Rectangle(pad, Theme.S(48), Width - pad * 2, Theme.S(16)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            Gfx.Text(g, Line2, Theme.Small, Theme.Muted, new Rectangle(pad, Theme.S(64), Width - pad * 2, Theme.S(16)), TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
    }

    // Карточка списка приложений (include / exclude) прямо на вкладке «Подключение»
    public class ListCard : CardPanel
    {
        public string Key;
        public ThemedList Box;
        public const string BuiltIn = "netprobe-vpn.exe";
        public FlatButton BAdd, BDel, BPaste;
        public ListCard()
        {
            Box = new ThemedList { MultiSelect = true, RightText = n => n == BuiltIn ? Tr.Tx("встроено") : null };
            BAdd = new FlatButton("") { Glyph = "", Width = Theme.S(30), Height = Theme.S(26) };
            BDel = new FlatButton("") { Glyph = "", Width = Theme.S(30), Height = Theme.S(26) };
            BPaste = new FlatButton("") { Glyph = "\uE77F", Width = Theme.S(30), Height = Theme.S(26) };
            Controls.AddRange(new Control[] { Box, BPaste, BAdd, BDel });
            Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) BDel.PerformClick2(); if (e.KeyCode == Keys.V && e.Control) BPaste.PerformClick2(); };
        }
        public void Fill()
        {
            if (Key == null) return;
            Box.BeginUpdate(); Box.Items.Clear();
            if (Key == AppLists.HybridApps) Box.Items.Add(BuiltIn); // пробник VPN всегда в туннеле — показываем, но не храним в списке
            foreach (var x in AppLists.Display(Key)) if (!x.Equals(BuiltIn, StringComparison.OrdinalIgnoreCase)) Box.Items.Add(x);
            Box.EndUpdate();
        }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            int ip = Theme.S(10);
            BDel.Location = new Point(Width - ip - BDel.Width, Theme.S(10));
            BAdd.Location = new Point(BDel.Left - Theme.S(4) - BAdd.Width, Theme.S(10));
            BPaste.Location = new Point(BAdd.Left - Theme.S(4) - BPaste.Width, Theme.S(10));
            Box.SetBounds(ip, Theme.S(46), Width - ip * 2, Math.Max(10, Height - Theme.S(54)));
        }
    }

    public class TextEditDialog : Form
    {
        public TextBox Box;
        public TextEditDialog(string title, string text, string hint)
        {
            Text = title; StartPosition = FormStartPosition.CenterParent; Size = new Size(Theme.S(760), Theme.S(560)); BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            MinimizeBox = false; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
            Box = Ui.TextBox(text, true);
            var h = Ui.Hint(hint ?? "", Theme.S(700));
            var ok = new FlatButton(Tx("Сохранить"), true) { Glyph = "" }.AutoW(); ok.Click += (s, e) => { DialogResult = DialogResult.OK; Close(); };
            var cancel = new FlatButton(Tx("Отмена")).AutoW(); cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { Box, h, ok, cancel });
            Action lay = () =>
            {
                int p = Theme.S(14), W = ClientSize.Width - p * 2;
                h.Width = W; h.Location = new Point(p, p);
                int by = ClientSize.Height - p - ok.Height;
                ok.Location = new Point(ClientSize.Width - p - ok.Width, by); cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, by);
                Box.SetBounds(p, h.Bottom + Theme.S(6), W, by - Theme.S(10) - h.Bottom - Theme.S(6));
            };
            Resize += (s, e) => lay(); lay();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); }
    }

    public partial class MainForm
    {
        StatusBoard board; FlatButton btnConnect, btnDpi, btnRoute; ModeCard[] modeCards; ListCard[] listCards;
        DarkCombo cbPrimary, cbBackup, cbStrategy, cbGame; Toggle tgBackup; Spin nFailover, nRecover, nRetry;
        ThemedCheck cKill, cDnsTun, cAutoConnect;
        bool loadingConnect;

        void BuildConnectPage()
        {
            var p = NewPage(Tx("Подключение"), Tx("VPN с автопереключением на DPI"));
            btnConnect = new FlatButton("VPN") { Glyph = "\uE7E8", Width = Theme.S(150), Height = Theme.S(36) };
            btnConnect.Click += (s, e) => { if (router.Active) Disconnect(); else Connect(); };
            btnDpi = new FlatButton("DPI") { Glyph = "\uE7E8", Width = Theme.S(150), Height = Theme.S(36) };
            btnDpi.Click += (s, e) => router.SetManualDpi(!router.ManualDpi);
            btnRoute = new FlatButton(Tx("Маршрут")) { Glyph = "\uE721", Height = Theme.S(36) }.AutoW();
            btnRoute.Click += (s, e) => ShowRoute(null, null);
            p.Controls.Add(btnRoute);
            p.Controls.Add(btnDpi);
            p.Controls.Add(btnConnect);

            board = new StatusBoard();
            modeCards = new ModeCard[3];
            string[][] md = { new[] { "", "DPI / VPN", Tx("VPN, при сбое — DPI") }, new[] { "", Tx("Только VPN"), Tx("Основной → резервный") }, new[] { "", Tx("Только DPI"), Tx("Без VPN") } };
            for (int i = 0; i < 3; i++)
            {
                int m = i;
                modeCards[i] = new ModeCard { Glyph = md[i][0], Title = md[i][1], Desc = md[i][2] };
                modeCards[i].Click += (s, e) => SetMode((RouteMode)m);
                p.Controls.Add(modeCards[i]);
            }

            var cPrim = new CardPanel { Title = Tx("Основной VPN") };
            cbPrimary = new DarkCombo();
            cbPrimary.SelectedIndexChanged += (s, e) => { if (loadingConnect) return; st.PrimaryTunnel = cbPrimary.SelectedItem as string ?? ""; st.Save(); if (router.Active && !router.OnBackup && st.RMode != RouteMode.DpiOnly) router.Reapply(); };
            cPrim.Controls.Add(cbPrimary);

            var cBack = new CardPanel { Title = Tx("Резервный VPN") };
            tgBackup = new Toggle();
            tgBackup.Changed += (s, e) => { if (loadingConnect) return; st.BackupEnabled = tgBackup.On; st.Save(); Logger.Write(LogLevel.Info, Tx("Резервный VPN ") + (st.BackupEnabled ? Tx("включён") : Tx("выключен"))); LoadConnectUi(); };
            cbBackup = new DarkCombo();
            cbBackup.SelectedIndexChanged += (s, e) => { if (loadingConnect) return; st.BackupTunnel = cbBackup.SelectedItem as string ?? ""; st.Save(); };
            cBack.Controls.AddRange(new Control[] { tgBackup, cbBackup });

            var cDpi = new CardPanel { Title = "DPI" };
            cbStrategy = new DarkCombo();
            cbStrategy.SelectedIndexChanged += (s, e) => { if (loadingConnect) return; st.DpiStrategy = cbStrategy.SelectedItem as string ?? "general"; st.Save(); Logger.Write(LogLevel.Info, Tx("Стратегия DPI: ") + st.DpiStrategy); router.RestartDpi(); };
            cbGame = new DarkCombo(); cbGame.Items.AddRange(new object[] { Tx("Игры: выкл"), Tx("Игры: TCP+UDP"), Tx("Игры: TCP"), Tx("Игры: UDP") });
            cbGame.SelectedIndexChanged += (s, e) => { if (loadingConnect) return; st.GameFilter = Math.Max(0, cbGame.SelectedIndex); st.Save(); router.RestartDpi(); };
            cDpi.Controls.AddRange(new Control[] { cbStrategy, cbGame });

            // --- аварийное переключение: одна компактная строка ---
            var cFail = new CardPanel();
            Func<string, Label> lab = t => new Label { Text = t, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card, UseMnemonic = false };
            var lF = lab(Tx("Переключать через, с")); var lR = lab(Tx("Возврат через, с")); var lT = lab(Tx("Пробовать основной, мин"));
            nFailover = Ui.Num(3, 120, st.FailoverSec); nFailover.Width = Theme.S(72);
            nFailover.ValueChanged += (s, e) => { if (loadingConnect) return; st.FailoverSec = (int)nFailover.Value; st.Save(); };
            nRecover = Ui.Num(3, 600, st.RecoverSec); nRecover.Width = Theme.S(72);
            nRecover.ValueChanged += (s, e) => { if (loadingConnect) return; st.RecoverSec = (int)nRecover.Value; st.Save(); };
            nRetry = Ui.Num(0, 240, st.RetryPrimaryMin); nRetry.Width = Theme.S(72);
            nRetry.ValueChanged += (s, e) => { if (loadingConnect) return; st.RetryPrimaryMin = (int)nRetry.Value; st.Save(); };
            cKill = Ui.Check("Kill switch", st.KillSwitch); cKill.BackColor = Theme.Card;
            cKill.CheckedChanged += (s, e) => { if (loadingConnect) return; st.KillSwitch = cKill.Checked; st.Save(); if (!st.KillSwitch && KillSwitch.Active) System.Threading.ThreadPool.QueueUserWorkItem(_ => KillSwitch.Clear()); };
            cDnsTun = Ui.Check(Tx("DNS через VPN"), st.DnsViaTunnel); cDnsTun.BackColor = Theme.Card;
            cDnsTun.CheckedChanged += (s, e) => { if (loadingConnect) return; st.DnsViaTunnel = cDnsTun.Checked; st.Save(); router.Reapply(); };
            cAutoConnect = Ui.Check(Tx("Автоподключение"), st.AutoConnect); cAutoConnect.BackColor = Theme.Card;
            cAutoConnect.CheckedChanged += (s, e) => { if (loadingConnect) return; st.AutoConnect = cAutoConnect.Checked; st.Save(); };
            var tip = Ui.Tip();
            tip.SetToolTip(btnRoute, Tx("Куда идёт домен или приложение: VPN, DPI или напрямую"));
            tip.SetToolTip(cKill, Tx("Блокировать приложения «Строго VPN», пока VPN недоступен"));
            tip.SetToolTip(lF, Tx("VPN не отвечает дольше — переключение")); tip.SetToolTip(lR, Tx("VPN стабилен столько — возврат с DPI"));
            cFail.Controls.AddRange(new Control[] { lF, nFailover, lR, nRecover, lT, nRetry, cKill, cDnsTun, cAutoConnect });

            // --- списки include / exclude ---
            listCards = new ListCard[3];
            for (int i = 0; i < 3; i++)
            {
                var lc = new ListCard(); listCards[i] = lc;
                lc.BAdd.Click += (s, e) =>
                {
                    if (lc.Key == AppLists.DpiDomains)
                    {
                        string v = Prompt(lc.Title, Tx("Домены через DPI (через запятую):"), "");
                        if (!string.IsNullOrWhiteSpace(v)) foreach (var x in v.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) AppLists.Add(lc.Key, x.Trim());
                        return;
                    }
                    using (var d = new ProcPicker(lc.Title, AppLists.Recent().Where(r => !AppLists.Get(lc.Key).Contains(r, StringComparer.OrdinalIgnoreCase)).ToList()))
                        if (d.ShowDialog(this) == DialogResult.OK) foreach (var x in d.Result) AddToList(lc.Key, x);
                };
                lc.Box.DoubleClick += (s, e) => lc.BAdd.PerformClick2();
                lc.BPaste.Click += (s, e) => PasteToList(lc.Key, lc.Box);
                AttachPathMenu(lc.Box, this);
                var lcRef = lc;
                lc.Box.RightText = n => n == ListCard.BuiltIn ? Tx("встроено") : DomainTag(lcRef.Key, n);
                var miRen = new ToolStripMenuItem(Tx("Переименовать…"));
                miRen.Click += (s, e) => { var old = lcRef.Box.SelectedItem; if (old != null && old != ListCard.BuiltIn) RenameInList(lcRef.Key, old); };
                var miDom = new ToolStripMenuItem(Tx("Домены через DPI…"));
                miDom.Click += (s, e) => AddDomainsTo(lcRef.Key, lcRef.Box.SelectedItem);
                lc.Box.ContextMenuStrip.Opening += (s, e) => miDom.Visible = AppLists.DomKey(lcRef.Key) != null;
                lc.Box.ContextMenuStrip.Items.Add(miDom);
                var miRoute = new ToolStripMenuItem(Tx("Проверить маршрут…"));
                miRoute.Click += (s, e) => ShowRouteFor(lcRef.Box.SelectedItem);
                lc.Box.ContextMenuStrip.Items.Add(miRoute);
                var miIco = new ToolStripMenuItem(Tx("Сменить иконку…"));
                miIco.Click += (s, e) =>
                {
                    var it = lcRef.Box.SelectedItem; if (it == null) return;
                    using (var d = new OpenFileDialog { Filter = Tx("Иконки и программы") + " (*.exe;*.ico;*.png;*.jpg)|*.exe;*.ico;*.png;*.jpg;*.jpeg;*.bmp" })
                        if (d.ShowDialog(this) == DialogResult.OK) { AppIcons.SetCustom(it, d.FileName); lcRef.Box.Invalidate(); }
                };
                var miIcoReset = new ToolStripMenuItem(Tx("Иконка по умолчанию"));
                miIcoReset.Click += (s, e) => { var it = lcRef.Box.SelectedItem; if (it != null) { AppIcons.SetCustom(it, null); lcRef.Box.Invalidate(); } };
                lc.Box.ContextMenuStrip.Items.Add(new ToolStripSeparator());
                lc.Box.ContextMenuStrip.Items.AddRange(new ToolStripItem[] { miRen, miIco, miIcoReset });
                lc.BDel.Click += (s, e) => { var sel = lc.Box.SelectedItems.Where(x => x != ListCard.BuiltIn).ToList(); if (sel.Count > 0) RemoveFromList(lc.Key, sel); };
                tip.SetToolTip(lc.BAdd, Tx("Добавить")); tip.SetToolTip(lc.BDel, Tx("Удалить")); tip.SetToolTip(lc.BPaste, Tx("Добавить список из буфера (по строкам или через запятую)"));
                p.Controls.Add(lc);
            }

            p.Controls.AddRange(new Control[] { board, cPrim, cBack, cDpi, cFail });
            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, gap = Theme.S(12);
                btnConnect.Location = new Point(p.ClientSize.Width - L - btnConnect.Width, Theme.S(22));
                btnDpi.Location = new Point(btnConnect.Left - Theme.S(8) - btnDpi.Width, Theme.S(22));
                btnRoute.Location = new Point(btnDpi.Left - Theme.S(8) - btnRoute.Width, Theme.S(22));
                int y = Theme.S(80);
                board.SetBounds(L, y, W, Theme.S(84)); y = board.Bottom + gap;
                int mw = (W - gap * 2) / 3, lastW = W - 2 * (mw + gap);
                for (int i = 0; i < 3; i++) modeCards[i].SetBounds(L + i * (mw + gap), y, i == 2 ? lastW : mw, Theme.S(64));
                y += Theme.S(64) + gap;
                int ch = Theme.S(92), ip = Theme.S(16);
                cPrim.SetBounds(L, y, mw, ch); cBack.SetBounds(L + mw + gap, y, mw, ch); cDpi.SetBounds(L + 2 * (mw + gap), y, lastW, ch);
                cbPrimary.SetBounds(ip, Theme.S(48), mw - ip * 2, cbPrimary.Height);
                tgBackup.Location = new Point(mw - ip - tgBackup.Width, Theme.S(12));
                cbBackup.SetBounds(ip, Theme.S(48), mw - ip * 2, cbBackup.Height);
                int dw = lastW - ip * 2, gw = Theme.S(120);
                cbStrategy.SetBounds(ip, Theme.S(48), dw - gw - Theme.S(8), cbStrategy.Height);
                cbGame.SetBounds(ip + dw - gw, Theme.S(48), gw, cbGame.Height);
                y += ch + gap;
                cFail.SetBounds(L, y, W, Theme.S(48));
                int x = ip, cy = Theme.S(12);
                foreach (var pair in new[] { Tuple.Create((Control)lF, (Control)nFailover), Tuple.Create((Control)lR, (Control)nRecover), Tuple.Create((Control)lT, (Control)nRetry) })
                {
                    pair.Item1.Location = new Point(x, cy + Theme.S(3)); x = pair.Item1.Right + Theme.S(6);
                    pair.Item2.Location = new Point(x, cy); x = pair.Item2.Right + Theme.S(14);
                }
                foreach (var c in new Control[] { cKill, cDnsTun, cAutoConnect }) { c.Location = new Point(x, cy + Theme.S(2)); x = c.Right + Theme.S(10); }
                y += Theme.S(48) + gap;
                var vis = listCards.Where(c => c.Key != null || listCards.All(k => k.Key == null)).ToList();
                int n = Math.Max(1, vis.Count), lw = (W - gap * (n - 1)) / n, lh = Math.Max(Theme.S(90), p.ClientSize.Height - Theme.S(18) - y);
                for (int i = 0; i < vis.Count; i++) vis[i].SetBounds(L + i * (lw + gap), y, i == n - 1 ? W - (n - 1) * (lw + gap) : lw, lh);
            };
            relayConnect = lay;
            p.Resize += (s, e) => lay();
            lay();
            LoadConnectUi();
        }
        Action relayConnect; readonly ToolTip listTip = Ui.Tip();

        // ПКМ по элементу списка: путь к запускаемому файлу (по запущенному процессу или полному пути)
        public static void AttachPathMenu(ThemedList list, Form owner)
        {
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            var miPath = new ToolStripMenuItem { Enabled = false };
            var miCopy = new ToolStripMenuItem(Tx("Копировать путь"));
            var miOpen = new ToolStripMenuItem(Tx("Открыть расположение файла"));
            cm.Items.AddRange(new ToolStripItem[] { miPath, new ToolStripSeparator(), miCopy, miOpen });
            string path = null;
            cm.Opening += (s, e) =>
            {
                var item = list.SelectedItem;
                if (item == null) { e.Cancel = true; return; }
                list.Focus();
                path = AppIcons.PathOf(item);
                miPath.Text = path ?? Tx("Путь неизвестен — процесс сейчас не запущен");
                miCopy.Enabled = miOpen.Enabled = path != null;
            };
            miCopy.Click += (s, e) => { if (path != null) try { Clipboard.SetText(path); } catch { } };
            miOpen.Click += (s, e) => { if (path != null) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); };
            list.ContextMenuStrip = cm;
        }

        // окно «Маршрут»: по тому, что запущено сейчас (WireSock, winws, xray / TUN)
        void ShowRoute(string app, string domain)
        {
            using (var d = new RouteDialog(() => RouteFacts.Take(router, xm, st), app, domain)) d.ShowDialog(this);
        }

        // из списка: приложение, «приложение|домен» или просто домен
        void ShowRouteFor(string item)
        {
            if (item == null || item == ListCard.BuiltIn) { ShowRoute(null, null); return; }
            if (AppLists.IsChild(item)) { ShowRoute(AppLists.ParentOf(item), AppLists.ChildOf(item)); return; }
            bool isApp = item.Contains("\\") || item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !item.Contains(".");
            if (isApp) ShowRoute(item, null); else if (!ExclusionSet.IsIp(item)) ShowRoute(null, item); else ShowRoute(null, null);
        }

        // ПКМ → «Домены через DPI…»: домены под приложением. Приложение может быть и в Include VPN —
        // тогда через VPN идёт всё, кроме этих доменов (они мимо туннеля, под DPI)
        void AddDomainsTo(string key, string item)
        {
            key = AppLists.DomKey(key);
            if (item == null || item == ListCard.BuiltIn || key == null) return;
            string app = AppLists.ParentOf(item);
            if (ExclusionSet.IsIp(app) || (!app.Contains("\\") && !app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && app.Contains("."))) return; // это домен, а не приложение
            string how = key == AppLists.SrvDpiApps
                ? Tx("Если приложение есть и в Include: всё через сервер, кроме этих доменов — они напрямую под DPI (в TUN — только для этого приложения).")
                : Tx("Если приложение есть и в Include VPN: всё через VPN, кроме этих доменов — они мимо туннеля под DPI. WireSock исключает IP доменов для всех приложений в туннеле; новые IP — при переподключении или раз в 10 мин.");
            string v = Prompt(Tx("Домены через DPI"), app + Tx(" — домены через DPI (через запятую), например youtube.com") + "\r\n\r\n" + how, "");
            if (string.IsNullOrWhiteSpace(v)) return;
            var added = new List<string>();
            foreach (var x in v.Split(new[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (AppLists.AddDomain(key, app, x)) added.Add(AppLists.ChildOf(AppLists.Get(key).Last()));
            if (added.Count > 0) Logger.Write(LogLevel.Info, AppLists.Title(key) + " · " + app + Tx(": домены через DPI ") + string.Join(", ", added));
        }

        // метка справа в Include DPI: как на самом деле пойдёт приложение и его домены
        string DomainTag(string key, string item)
        {
            if (key == null || item == null) return null;
            if (AppLists.IsChild(item)) return "→ DPI";
            if (!AppLists.HasDomains(key)) return null;
            bool srv = key == AppLists.SrvDpiApps;
            var inc = srv ? AppLists.Get(AppLists.SrvApps) : AppLists.Get(AppLists.HybridApps).Concat(AppLists.Get(AppLists.VpnOnlyApps)).ToList();
            bool viaTunnel = inc.Count == 0 || AppLists.InList(inc, item);
            if (!viaTunnel) return null;
            bool hasDom = AppLists.Domains(key).Any(d => d.Key.Equals(item, StringComparison.OrdinalIgnoreCase));
            return srv ? (hasDom ? Tx("сервер, кроме доменов") : Tx("через сервер")) : (hasDom ? Tx("VPN, кроме доменов") : Tx("через VPN"));
        }

        // домены под приложением в Include VPN лежат в Include DPI — удаляются оттуда
        void RemoveFromList(string key, List<string> sel)
        {
            var kids = sel.Where(AppLists.IsChild).ToList(); var rest = sel.Where(x => !AppLists.IsChild(x)).ToList();
            string dk = AppLists.DomKey(key) ?? key;
            if (kids.Count > 0) AppLists.Remove(dk, kids);
            if (rest.Count > 0) AppLists.Remove(key, rest);
            Logger.Write(LogLevel.Info, AppLists.Title(key) + Tx(": удалено ") + string.Join(", ", sel.Select(x => AppLists.IsChild(x) ? AppLists.ParentOf(x) + " · " + AppLists.ChildOf(x) : x)));
        }

        void RenameInList(string key, string old)
        {
            bool child = AppLists.IsChild(old);
            if (child) key = AppLists.DomKey(key) ?? key;
            string v = Prompt(Tx("Переименовать"), child ? Tx("Домен:") : Tx("Имя процесса, путь, домен или IP:"), child ? AppLists.ChildOf(old) : old);
            if (string.IsNullOrWhiteSpace(v)) return;
            v = v.Trim();
            if (child) { if (v.IndexOf(AppLists.Sep) >= 0) return; v = AppLists.ParentOf(old) + AppLists.Sep + v.ToLowerInvariant(); }
            if (v == old || (!child && v.IndexOf(AppLists.Sep) >= 0)) return;
            AppLists.Rename(key, old, v);
        }

        void SetupListCards()
        {
            string[] keys; string[][] titles;
            switch (st.RMode)
            {
                case RouteMode.DpiOnly:
                    keys = new[] { AppLists.DpiApps, AppLists.DpiDomains, AppLists.DpiOnlyExcl };
                    titles = new[] { new[] { Tx("Через VPN"), Tx("приложения мимо VPN под DPI · ПКМ → домены приложения через DPI, остальное через VPN") }, new[] { Tx("Домены"), Tx("домены через DPI") }, new[] { Tx("Исключения"), Tx("мимо DPI") } };
                    break;
                case RouteMode.VpnOnly:
                    keys = new[] { AppLists.HybridApps, AppLists.VpnOnlyApps, AppLists.VpnOnlyExcl };
                    titles = new[] { new[] { Tx("Через VPN"), Tx("пусто = весь трафик") }, new[] { Tx("Строго VPN"), Tx("kill switch при обрыве") }, new[] { Tx("Исключения"), Tx("мимо VPN") } };
                    break;
                default:
                    keys = new[] { AppLists.HybridApps, AppLists.VpnOnlyApps, AppLists.HybridExcl };
                    titles = new[] { new[] { Tx("Через VPN"), Tx("при сбое — DPI · пусто = всё") }, new[] { Tx("Строго VPN"), Tx("даже при сбое") }, new[] { Tx("Исключения"), Tx("мимо VPN и DPI") } };
                    break;
            }
            for (int i = 0; i < 3; i++)
            {
                var c = listCards[i];
                c.Key = keys[i]; c.Visible = keys[i] != null;
                if (keys[i] == null) continue;
                c.Title = titles[i][0]; c.Sub = null; listTip.SetToolTip(c, titles[i][1]); c.Box.ShowIcons = st.ShowAppIcons && c.Key != AppLists.DpiDomains; c.Fill(); c.Invalidate();
            }
            if (relayConnect != null) relayConnect();
        }

        void LoadConnectUi()
        {
            if (cbPrimary == null || cbPrimary.IsDisposed) return;
            loadingConnect = true;
            try
            {
                var names = Tunnels.Names();
                cbPrimary.Items.Clear(); cbBackup.Items.Clear();
                foreach (var n in names) { cbPrimary.Items.Add(n); cbBackup.Items.Add(n); }
                cbPrimary.SelectedItem = names.Contains(st.PrimaryTunnel) ? st.PrimaryTunnel : null;
                cbBackup.SelectedItem = names.Contains(st.BackupTunnel) ? st.BackupTunnel : null;
                tgBackup.On = st.BackupEnabled;
                cbBackup.Enabled = st.BackupEnabled; nRetry.Enabled = st.BackupEnabled;
                var strat = DpiConfigs.Names();
                cbStrategy.Items.Clear(); foreach (var n in strat) cbStrategy.Items.Add(n);
                cbStrategy.SelectedItem = strat.Contains(st.DpiStrategy) ? st.DpiStrategy : null;
                cbGame.SelectedIndex = st.GameFilter;
                for (int i = 0; i < 3; i++) modeCards[i].Selected = st.Mode == i;
                nFailover.Value = st.FailoverSec; nRecover.Value = st.RecoverSec; nRetry.Value = st.RetryPrimaryMin;
                cKill.Checked = st.KillSwitch; cDnsTun.Checked = st.DnsViaTunnel; cAutoConnect.Checked = st.AutoConnect;
                SetupListCards();
            }
            finally { loadingConnect = false; }
            UpdateConnectUi();
        }

        static string Dur(TimeSpan t) { return t.TotalHours >= 1 ? ((int)t.TotalHours) + Tx(" ч ") + t.Minutes + Tx(" мин") : t.TotalMinutes >= 1 ? ((int)t.TotalMinutes) + Tx(" мин ") + t.Seconds + Tx(" с") : ((int)t.TotalSeconds) + Tx(" с"); }

        void UpdateConnectUi()
        {
            if (board == null || board.IsDisposed) return;
            var s = router.State;
            // одинаковые кнопки-переключатели: заливка = включено, контур = выключено
            btnConnect.Primary = router.Active; btnConnect.Text = "VPN  ·  " + (router.Active ? Tx("ВКЛ") : Tx("ВЫКЛ")); btnConnect.Glyph = "\uE7E8"; btnConnect.Invalidate();
            btnDpi.Primary = router.ManualDpi || router.Dpi.Running; btnDpi.Text = "DPI  ·  " + (btnDpi.Primary ? Tx("ВКЛ") : Tx("ВЫКЛ")); btnDpi.Glyph = "\uE7E8"; btnDpi.Invalidate();
            board.State = Router.StateName(s) + (router.Active ? "  ·  " + Router.ModeName(st.RunMode) : "");
            board.Lv = s == RouteState.Off ? Level.Off : s == RouteState.Connecting ? Level.Wait : s == RouteState.Vpn || s == RouteState.DpiOnly ? Level.Good : s == RouteState.VpnDown ? Level.Down : Level.Fair;
            if (!router.Active)
            {
                string miss = (!File.Exists(st.WiresockPath) ? Tx("WireSock не найден  ") : "") + (ZapretRunner.Check(st.ZapretPath) != null ? Tx("zapret не найден") : "");
                board.Line1 = "VPN: " + (st.PrimaryTunnel != "" ? st.PrimaryTunnel : "—") + (st.BackupEnabled ? Tx("  ·  резерв: ") + (st.BackupTunnel != "" ? st.BackupTunnel : "—") : "") + "  ·  DPI: " + st.DpiStrategy;
                board.Line2 = miss;
            }
            else
            {
                var now = DateTime.Now;
                string tun = router.Wg.Running ? "VPN «" + router.Wg.TunnelName + "»" + (router.StrictOnly ? Tx(" (строго VPN)") : "") + " " + Dur(now - router.Wg.StartedAt) : Tx("VPN выкл");
                string dpi = router.Dpi.Running ? "DPI «" + router.Dpi.Strategy + "» " + Dur(now - router.Dpi.StartedAt) : Tx("DPI выкл");
                board.Line1 = tun + "  ·  " + dpi + Tx("  ·  переключений: ") + router.Failovers + (KillSwitch.Active ? "  ·  kill switch: " + KillSwitch.Count : "");
                board.Line2 = "VPN " + chs["vpn"].Val + Tx(" · напрямую ") + chs["direct"].Val + (router.Detail != "" && s != RouteState.Vpn && s != RouteState.Backup ? "  ·  " + router.Detail : "");
            }
            board.Invalidate();
        }
    }
}
