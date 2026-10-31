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
        public int Reserve; // справа под заголовком занято (выбор режима)
        public Level Lv = Level.Off;
        public StatusBoard() { Height = Theme.S(84); BackColor = Theme.Card; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; Gfx.Backdrop(this, g); Gfx.Hq(g);
            var r = Theme.Classic ? new RectangleF(0, 0, Width, Height) : new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            Gfx.Panel(g, r, Theme.R(12), Theme.Card);
            int pad = Theme.S(20);
            Gfx.Dot(g, Theme.LevelColor(Lv), pad + Theme.S(10), Theme.S(30), Theme.S(10), Lv != Level.Off);
            Gfx.Text(g, State, Theme.H1, Theme.Text, new Rectangle(pad + Theme.S(32), Theme.S(12), Width - pad * 2 - Theme.S(32) - Reserve, Theme.S(36)), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
        StatusBoard board; ListCard[] listCards; TopButtons topRouting;
        DarkCombo cbPrimary, cbBackup; Toggle tgBackup; Spin nFailover, nRecover, nRetry;
        ThemedCheck cKill, cDnsTun, cAutoConnect;
        bool loadingConnect;

        DarkCombo cbRunMode; Label lRouteHint;

        void BuildConnectPage()
        {
            var p = NewPage(Tx("Маршрутизация"), Tx("Приложения: туннель, «Строго VPN», исключения"));
            topRouting = NewTopButtons(p, null);
            // режим маршрутизации — выпадающий список на панели состояния (кнопки сверху только включают/выключают)
            cbRunMode = new DarkCombo { Width = Theme.S(190) };
            foreach (var m in new[] { RouteMode.Hybrid, RouteMode.VpnOnly, RouteMode.DpiOnly }) cbRunMode.Items.Add(Router.ModeName(m));
            cbRunMode.SelectedIndex = st.Mode;
            cbRunMode.SelectedIndexChanged += (s, e) =>
            {
                if (!cbRunMode.UserPick || cbRunMode.SelectedIndex < 0 || cbRunMode.SelectedIndex == st.Mode) return;
                int want = cbRunMode.SelectedIndex; string why = ChipReasons()[want];
                if (why != null) { BeginInvoke((Action)(() => { UpdateModeUi(); Notice.Show(this, Router.ModeName((RouteMode)want), why); })); return; }
                ChipClick(want);
            };

            board = new StatusBoard { Reserve = Theme.S(204) };

            var cPrim = new CardPanel { Title = Tx("Основной VPN") };
            cbPrimary = new DarkCombo();
            cbPrimary.SelectedIndexChanged += (s, e) => { if (loadingConnect) return; st.PrimaryTunnel = cbPrimary.SelectedItem as string ?? ""; st.Save(); if (router.Active && !router.OnBackup && st.RunMode != RouteMode.DpiOnly) router.Reapply(); };
            cPrim.Controls.Add(cbPrimary);

            var cBack = new CardPanel { Title = Tx("Резервный VPN") };
            tgBackup = new Toggle();
            tgBackup.Changed += (s, e) => { if (loadingConnect) return; st.BackupEnabled = tgBackup.On; st.Save(); Logger.Write(LogLevel.Info, Tx("Резервный VPN ") + (st.BackupEnabled ? Tx("включён") : Tx("выключен"))); LoadConnectUi(); };
            cbBackup = new DarkCombo();
            cbBackup.SelectedIndexChanged += (s, e) => { if (loadingConnect) return; st.BackupTunnel = cbBackup.SelectedItem as string ?? ""; st.Save(); };
            cBack.Controls.AddRange(new Control[] { tgBackup, cbBackup });

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
            cKill.CheckedChanged += (s, e) => { if (loadingConnect) return; st.KillSwitch = cKill.Checked; st.Save(); if (!st.KillSwitch && KillSwitch.NeedsClear) System.Threading.ThreadPool.QueueUserWorkItem(_ => KillSwitch.Clear()); };
            cDnsTun = Ui.Check(Tx("DNS через VPN"), st.DnsViaTunnel); cDnsTun.BackColor = Theme.Card;
            cDnsTun.CheckedChanged += (s, e) => { if (loadingConnect || cDnsTun.Checked == st.DnsViaTunnel) return; if (!CanEditTunSettings()) { cDnsTun.Checked = st.DnsViaTunnel; return; } st.DnsViaTunnel = cDnsTun.Checked; st.Save(); };
            cAutoConnect = Ui.Check(Tx("Автоподключение"), st.AutoConnect); cAutoConnect.BackColor = Theme.Card;
            cAutoConnect.CheckedChanged += (s, e) => { if (loadingConnect) return; st.AutoConnect = cAutoConnect.Checked; st.Save(); };
            var tip = Ui.Tip();
            tip.SetToolTip(cKill, Tx("Блокировать приложения «Строго VPN», пока VPN недоступен"));
            tip.SetToolTip(cDnsTun, "Configs: use VPN DNS by default, with local DNS for explicit Direct domain rules. Windows DNS service queries may not identify the original process. Changes apply on reconnect.");
            tip.SetToolTip(lF, Tx("VPN не отвечает дольше — переключение")); tip.SetToolTip(lR, Tx("VPN стабилен столько — возврат с DPI"));
            cFail.Controls.AddRange(new Control[] { lF, nFailover, lR, nRecover, lT, nRetry, cKill, cDnsTun, cAutoConnect });

            // строка-подсказка контракта маршрутизации
            lRouteHint = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true,
                Text = Tx("DPI глобален: касается всего вне туннеля. Домены для DPI — на вкладке DPI  ·  Приоритет: «Строго VPN» > домен > приложение") };
            tip.SetToolTip(lRouteHint, Tx("Домены и IP в этих списках работают в TUN (подписки, туннели VLESS / VMess / Trojan); WireSock понимает только приложения. Приложение из Include при активном туннеле автоматически вне DPI."));

            // --- списки приложений: Include · Строго VPN · Exclude ---
            listCards = new ListCard[3];
            for (int i = 0; i < 3; i++)
            {
                var lc = new ListCard(); listCards[i] = lc;
                lc.BAdd.Click += (s, e) =>
                {
                    if (!CanEditList(lc.Key)) return;
                    using (var d = new ProcPicker(lc.Title, AppLists.Recent().Where(r => !AppLists.Get(lc.Key).Contains(r, StringComparer.OrdinalIgnoreCase)).ToList()))
                        if (d.ShowDialog(this) == DialogResult.OK) foreach (var x in d.Result) AddToList(lc.Key, x);
                };
                lc.Box.DoubleClick += (s, e) => lc.BAdd.PerformClick2();
                lc.BPaste.Click += (s, e) => PasteToList(lc.Key, lc.Box);
                // домены/IP серые только когда источник — WireGuard (WireSock понимает только приложения); в TUN (подписки, VLESS/VMess/Trojan) они работают
                lc.Box.DimReason = n => DomainsGray() ? AppLists.NotAppReason(n) : null;
                AttachPathMenu(lc.Box, this);
                var lcRef = lc;
                lc.Box.RightText = n => n == ListCard.BuiltIn ? Tx("встроено") : null; // домены в списках приложений — без подписи «not used» (только серым + подсказка)
                var miRen = new ToolStripMenuItem(Tx("Переименовать…"));
                miRen.Click += (s, e) => { var old = lcRef.Box.SelectedItem; if (old != null && old != ListCard.BuiltIn) RenameInList(lcRef.Key, old); };
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
                // копирование выделенных строк (Ctrl+C) и перетаскивание между Include / «Строго VPN» / Exclude
                Func<List<string>, List<string>> movable = l => l.Where(x => x != ListCard.BuiltIn && !AppLists.IsChild(x)).ToList();
                lc.Box.CopyItems = movable;
                lc.Box.DragItems = l => CanEditList(lcRef.Key) ? movable(l) : new List<string>();
                lc.Box.AllowDrop = true;
                lc.Box.ItemsDropped += (from, items) => MoveBetweenLists(from, lcRef, items);
                var miCopySel = new ToolStripMenuItem(Tx("Копировать выделенное") + "   Ctrl+C");
                miCopySel.Click += (s, e) => lcRef.Box.CopySelected();
                lc.Box.ContextMenuStrip.Items.Insert(0, miCopySel);
                lc.Box.ContextMenuStrip.Items.Insert(1, new ToolStripSeparator());
                lc.BDel.Click += (s, e) => { var sel = lc.Box.SelectedItems.Where(x => x != ListCard.BuiltIn).ToList(); if (sel.Count > 0) RemoveFromList(lc.Key, sel); };
                tip.SetToolTip(lc.BAdd, Tx("Добавить")); tip.SetToolTip(lc.BDel, Tx("Удалить")); tip.SetToolTip(lc.BPaste, Tx("Добавить список из буфера (по строкам или через запятую)"));
                p.Controls.Add(lc);
            }

            p.Controls.AddRange(new Control[] { board, cPrim, cBack, cFail, lRouteHint });
            board.Controls.Add(cbRunMode);
            Action lay = () =>
            {
                int L = Theme.S(26), W = PW(p) - L * 2, gap = Theme.S(12); // кнопки сверху — по всей ширине окна, содержимое — левее колонки модулей
                PlaceTopButtons(topRouting, p.ClientSize.Width - L, Theme.S(22));
                int y = Theme.S(80);
                board.SetBounds(L, y, W, Theme.S(84));
                cbRunMode.Location = new Point(board.Width - Theme.S(14) - cbRunMode.Width, Theme.S(16));
                y = board.Bottom + gap;
                int ch = Theme.S(92), ip = Theme.S(16), hw = (W - gap) / 2;
                cPrim.SetBounds(L, y, hw, ch); cBack.SetBounds(L + hw + gap, y, W - hw - gap, ch);
                cbPrimary.SetBounds(ip, Theme.S(48), hw - ip * 2, cbPrimary.Height);
                tgBackup.Location = new Point(cBack.Width - ip - tgBackup.Width, Theme.S(12));
                cbBackup.SetBounds(ip, Theme.S(48), cBack.Width - ip * 2, cbBackup.Height);
                y += ch + gap;
                // строка аварийного переключения; в узком окне переносится на вторую строку
                int x = ip, cy = Theme.S(12), rowH = Theme.S(32), maxX = W - ip;
                foreach (var pair in new[] { Tuple.Create((Control)lF, (Control)nFailover), Tuple.Create((Control)lR, (Control)nRecover), Tuple.Create((Control)lT, (Control)nRetry) })
                {
                    int need = pair.Item1.Width + Theme.S(6) + pair.Item2.Width;
                    if (x > ip && x + need > maxX) { x = ip; cy += rowH; }
                    pair.Item1.Location = new Point(x, cy + Theme.S(3)); x = pair.Item1.Right + Theme.S(6);
                    pair.Item2.Location = new Point(x, cy); x = pair.Item2.Right + Theme.S(14);
                }
                foreach (var c in new Control[] { cKill, cDnsTun, cAutoConnect })
                {
                    if (x > ip && x + c.Width > maxX) { x = ip; cy += rowH; }
                    c.Location = new Point(x, cy + Theme.S(2)); x = c.Right + Theme.S(10);
                }
                int failH = cy + Theme.S(36);
                cFail.SetBounds(L, y, W, failH);
                y += failH + Theme.S(6);
                lRouteHint.SetBounds(L + Theme.S(2), y, W, Theme.S(18)); y += Theme.S(18) + Theme.S(6);
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

        // Кнопка DPI (Подключение и Серверы): переключает то, что видно на кнопке. «Выкл» глушит winws сразу,
        // даже если его держат домены приложений или «Серверы»; выбор сохраняется в settings.ini (DpiButton).
        void ToggleDpi()
        {
            bool on = !(router.ManualDpi || router.Dpi.Running);
            st.DpiButton = on ? 1 : 2; st.Save();
            router.SetManualDpi(on);
            if (!on && router.DpiIsRoute) Logger.Write(LogLevel.Info, Tx("DPI сейчас — основной маршрут (аварийный режим или «Только DPI»): он выключается кнопкой VPN"));
        }

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

        // домены под приложением в Include VPN лежат в Include DPI — удаляются оттуда
        // перетаскивание строк между списками Routing: в новом списке добавить (AddToList сам убирает конфликты), из старого убрать
        void MoveBetweenLists(ThemedList from, ListCard to, List<string> items)
        {
            var src = listCards.FirstOrDefault(c => c.Box == from);
            if (src == null || src.Key == null || to.Key == null || src.Key == to.Key || items.Count == 0) return;
            if (!CanEditList(src.Key) || !CanEditList(to.Key)) return;
            foreach (var x in items) AddToList(to.Key, x);
            var left = AppLists.Get(src.Key);
            var still = items.Where(x => left.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            if (still.Count > 0) AppLists.Remove(src.Key, still);
            Logger.Write(LogLevel.Info, AppLists.Title(src.Key) + " → " + AppLists.Title(to.Key) + ": " + string.Join(", ", items));
            RefreshListsUi();
        }

        void RemoveFromList(string key, List<string> sel)
        {
            if (!CanEditList(key)) return;
            var kids = sel.Where(AppLists.IsChild).ToList(); var rest = sel.Where(x => !AppLists.IsChild(x)).ToList();
            string dk = AppLists.DomKey(key) ?? key;
            if (kids.Count > 0) AppLists.Remove(dk, kids);
            if (rest.Count > 0) AppLists.Remove(key, rest);
            Logger.Write(LogLevel.Info, AppLists.Title(key) + Tx(": удалено ") + string.Join(", ", sel.Select(x => AppLists.IsChild(x) ? AppLists.ParentOf(x) + " · " + AppLists.ChildOf(x) : x)));
        }

        void RenameInList(string key, string old)
        {
            if (!CanEditList(key)) return;
            bool child = AppLists.IsChild(old);
            if (child) key = AppLists.DomKey(key) ?? key;
            string v = Prompt(Tx("Переименовать"), child ? Tx("Домен:") : Tx("Имя процесса, путь, домен или IP:"), child ? AppLists.ChildOf(old) : old);
            if (string.IsNullOrWhiteSpace(v)) return;
            v = v.Trim();
            if (child) { if (v.IndexOf(AppLists.Sep) >= 0) return; v = AppLists.ParentOf(old) + AppLists.Sep + v.ToLowerInvariant(); }
            if (v == old || (!child && v.IndexOf(AppLists.Sep) >= 0)) return;
            if (!CanEditList(key)) return; // Connection may have started while the dialog was open.
            AppLists.Rename(key, old, v);
        }

        void SetupListCards()
        {
            var m = st.RunMode;
            string exKey = Router.TunExclKey(st);
            string[] keys = { AppLists.HybridApps, AppLists.VpnOnlyApps, exKey };
            string[][] titles = m == RouteMode.VpnOnly
                ? new[] { new[] { "Include", Tx("в туннель · пусто = весь трафик") }, new[] { Tx("Строго VPN"), Tx("kill switch при обрыве") }, new[] { "Exclude", Tx("мимо туннеля (DisallowedApps); DPI на них действует — он глобален") } }
                : new[] { new[] { "Include", Tx("в туннель, при сбое — DPI · пусто = всё") }, new[] { Tx("Строго VPN"), Tx("всегда туннель, никогда DPI · kill switch при обрыве") }, new[] { "Exclude", Tx("мимо туннеля (DisallowedApps); DPI на них действует — он глобален") } };
            string dpiNote = m == RouteMode.DpiOnly ? "  ·  " + Tx("в режиме DPI списки туннеля не применяются") : "";
            for (int i = 0; i < 3; i++)
            {
                var c = listCards[i];
                c.Key = keys[i]; c.Visible = true;
                c.Title = titles[i][0]; c.Sub = null; listTip.SetToolTip(c, titles[i][1] + dpiNote); c.Box.ShowIcons = st.ShowAppIcons; c.Fill(); c.Invalidate();
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
            // источник — подписка (xray), маршрутизатор при этом выключен: показываем реальное состояние подключения, как на Subscriptions
            if (!router.Active && xm.Active)
            {
                var xs = xm.State; var cur = ServerStore.Find(st.ActiveServer);
                board.State = XrayManager.StateName(xs) + "  ·  " + Router.ModeName(st.RunMode) + (cur != null ? "  ·  " + cur.Name : "");
                board.Lv = xs == XState.Connected ? (xm.LastDelay == -2 ? Level.Slow : Level.Good) : xs == XState.AllDown ? Level.Down : Level.Wait;
                board.Line1 = Tx("Подписка (xray)") + (cur != null ? ": " + cur.Transport + " · " + cur.Endpoint : "") + "  ·  " + Tx("задержка ") + (xm.LastDelay > 0 ? xm.LastDelay + Tx(" мс") : xm.LastDelay == -2 ? Tx("нет ответа") : "…")
                    + "  ·  " + (st.TunMode ? "TUN" + (xm.TunRunning ? " ✓" : " ✗") : st.SysProxy ? Tx("системный прокси") : "SOCKS 127.0.0.1:" + st.SocksPort);
                int dp = router.Dpi.Pid;
                board.Line2 = (dp > 0 ? "winws «" + router.Dpi.Strategy + "» PID " + dp : "DPI —") + "  ·  VPN " + chs["vpn"].Val + Tx(" · напрямую ") + chs["direct"].Val + "  ·  " + Tx("переключений: ") + xm.Switches;
                board.Invalidate();
                return;
            }
            var s = router.State;
            board.State = Router.StateName(s) + (router.Active ? "  ·  " + Router.ModeName(st.RunMode) : "");
            board.Lv = s == RouteState.Off ? Level.Off : s == RouteState.Connecting ? Level.Wait : s == RouteState.Vpn || s == RouteState.DpiOnly ? Level.Good : s == RouteState.VpnDown ? Level.Down : Level.Fair;
            if (!router.Active)
            {
                string miss = (TunnelProto.NeedsWiresock(st) && !File.Exists(st.WiresockPath) ? Tx("WireSock не найден  ") : "") + (ZapretRunner.Check(st.ZapretPath) != null ? Tx("zapret не найден") : "");
                board.Line1 = "VPN: " + (st.PrimaryTunnel != "" ? st.PrimaryTunnel : "—") + (st.BackupEnabled ? Tx("  ·  резерв: ") + (st.BackupTunnel != "" ? st.BackupTunnel : "—") : "") + "  ·  DPI: " + st.DpiStrategy;
                board.Line2 = miss;
            }
            else
            {
                // только факты: процессы (PID), стратегия, ответ пробника; неизвестно — «—»
                var now = DateTime.Now;
                int wp = router.Wg.Pid, dp = router.Dpi.Pid;
                string tun = wp > 0 ? TunnelProto.EngineName(router.Wg.TunnelName) + " «" + router.Wg.TunnelName + "» PID " + wp + (router.StrictOnly ? Tx(" (строго VPN)") : "") + " · " + Dur(now - router.Wg.StartedAt) : TunnelProto.EngineName(router.OnBackup ? st.BackupTunnel : st.PrimaryTunnel) + " —";
                string dpi = dp > 0 ? "winws «" + router.Dpi.Strategy + "» PID " + dp + " · " + Dur(now - router.Dpi.StartedAt) : "winws —";
                string probe = router.LastVpnOk == DateTime.MinValue ? Tx("пробник VPN: —") : Tx("пробник VPN: ответ ") + Dur(now - router.LastVpnOk) + Tx(" назад");
                board.Line1 = tun + "  ·  " + dpi + "  ·  " + probe + (KillSwitch.Active ? "  ·  kill switch: " + KillSwitch.Count : "");
                board.Line2 = "VPN " + chs["vpn"].Val + Tx(" · напрямую ") + chs["direct"].Val + Tx("  ·  переключений: ") + router.Failovers + (router.Detail != "" && s != RouteState.Vpn && s != RouteState.Backup ? "  ·  " + router.Detail : "");
            }
            board.Invalidate();
        }
    }
}
