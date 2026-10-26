using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        ThemedList lbStrat; TextBox tbStratName, tbStratArgs, tbZList; DarkCombo cbZList; Label lStratInfo, lZInfo;
        Panel dpiStratTab, dpiListTab; FlatButton tabStrat, tabLists, bStratUse;
        string zListLoaded; bool loadingStrat, loadingZ;
        readonly System.Windows.Forms.Timer stratSave = new System.Windows.Forms.Timer { Interval = 900 }, zSave = new System.Windows.Forms.Timer { Interval = 900 };
        static readonly string[] ZLists = { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt", "list-general.txt", "list-google.txt", "list-exclude.txt", "ipset-exclude.txt", "ipset-all.txt" };

        FlatButton IconBtn(string glyph, string tip, ToolTip tt)
        {
            var b = new FlatButton("") { Glyph = glyph, Width = Theme.S(32), Height = Theme.S(28) };
            tt.SetToolTip(b, tip);
            return b;
        }

        void BuildDpiPanel(Panel p)
        {
            var tt = new ToolTip();
            // ---- слева: список стратегий + маленькая панель кнопок ----
            var left = new CardPanel { Title = Tx("Стратегии") };
            lbStrat = new ThemedList { BoldItem = n => n == st.DpiStrategy, RightText = n => n == st.DpiStrategy ? Tx("активна") : null };
            lbStrat.SelectedIndexChanged += (s, e) => LoadStrategy();
            lbStrat.DoubleClick += (s, e) => UseStrategy();
            var bNew = IconBtn("", Tx("Новая"), tt);
            bNew.Click += (s, e) => CreateStrategy("new", "--wf-tcp=80,443 --wf-udp=443\r\n--filter-tcp=80,443 --hostlist=\"%LISTS%list-general.txt\" --dpi-desync=multisplit --dpi-desync-split-pos=1\r\n");
            var bCopy = IconBtn("", Tx("Копия"), tt);
            bCopy.Click += (s, e) => { var n = lbStrat.SelectedItem; if (n != null) CreateStrategy(n + Tx(" (копия)"), DpiConfigs.Read(n)); };
            var bDel = IconBtn("", Tx("Удалить"), tt);
            bDel.Click += (s, e) =>
            {
                var n = lbStrat.SelectedItem; if (n == null) return;
                if (MessageBox.Show(this, Tx("Удалить стратегию «") + n + "»?", "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                DpiConfigs.Delete(n); FillStrategies(null);
            };
            var bImpAll = IconBtn("", Tx("Импорт из zapret"), tt);
            bImpAll.Click += (s, e) =>
            {
                string dir = st.ZapretPath;
                if (!Directory.Exists(dir)) { using (var d = new FolderBrowserDialog { Description = Tx("Папка zapret-discord-youtube (с general*.bat)") }) { if (d.ShowDialog(this) != DialogResult.OK) return; dir = d.SelectedPath; } }
                bool ow = MessageBox.Show(this, Tx("Перезаписать стратегии с такими же именами?"), Tx("Импорт"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                int n = DpiConfigs.ImportAll(dir, ow);
                Logger.Write(LogLevel.Info, Tx("Импортировано стратегий DPI: ") + n + Tx(" из ") + dir);
                FillStrategies(null);
            };
            var bImpBat = IconBtn("", Tx("Импорт .bat…"), tt);
            bImpBat.Click += (s, e) =>
            {
                using (var d = new OpenFileDialog { Filter = Tx("Стратегия zapret (*.bat;*.cmd)|*.bat;*.cmd") })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    try { CreateStrategy(Path.GetFileNameWithoutExtension(d.FileName), DpiConfigs.FromBat(d.FileName)); }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "NetProbe"); }
                }
            };
            var leftBtns = new[] { bNew, bCopy, bDel, bImpAll, bImpBat };
            left.Controls.Add(lbStrat); left.Controls.AddRange(leftBtns);

            // ---- справа: вкладки «Стратегия» / «Списки zapret» ----
            var right = new CardPanel();
            tabStrat = new FlatButton(Tx("Стратегия")).AutoW(); tabLists = new FlatButton(Tx("Списки zapret")).AutoW();
            tabStrat.Click += (s, e) => ShowDpiTab(0); tabLists.Click += (s, e) => ShowDpiTab(1);
            dpiStratTab = new Panel { BackColor = Theme.Card }; dpiListTab = new Panel { BackColor = Theme.Card };
            right.Controls.AddRange(new Control[] { tabStrat, tabLists, dpiStratTab, dpiListTab });

            var lName = new Label { Text = Tx("Имя:"), AutoSize = true, ForeColor = Theme.Muted, BackColor = Theme.Card };
            tbStratName = Ui.TextBox();
            tbStratName.Leave += (s, e) => RenameStrategy();
            tbStratName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { RenameStrategy(); e.SuppressKeyPress = true; } };
            bStratUse = new FlatButton(Tx("Сделать активной"), true) { Glyph = "" }.AutoW();
            bStratUse.Click += (s, e) => UseStrategy();
            var bPrev = IconBtn("", Tx("Итоговая команда"), tt);
            bPrev.Click += (s, e) =>
            {
                string cmd = "\"" + Path.Combine(st.ZapretPath, "bin", "winws.exe") + "\" " + ZapretRunner.BuildArgs(st, tbStratArgs.Text);
                using (var d = new TextEditDialog(Tx("Команда winws.exe"), cmd.Replace(" --new ", " --new\r\n"), Tx("Так NetProbe запустит winws.exe (только просмотр; правки здесь не сохраняются)."))) d.ShowDialog(this);
            };
            tbStratArgs = Ui.TextBox("", true);
            tbStratArgs.TextChanged += (s, e) => { if (!loadingStrat) { stratSave.Stop(); stratSave.Start(); lStratInfo.Text = "…"; } };
            stratSave.Tick += (s, e) => { stratSave.Stop(); AutoSaveStrategy(); };
            var hint = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, Text = Tx("Один профиль (--new) на строку · %BIN%, %LISTS%, %GameFilterTCP%, %GameFilterUDP%") };
            lStratInfo = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, TextAlign = ContentAlignment.MiddleRight };
            dpiStratTab.Controls.AddRange(new Control[] { lName, tbStratName, bStratUse, bPrev, tbStratArgs, hint, lStratInfo });

            cbZList = new DarkCombo(); foreach (var z in ZLists) cbZList.Items.Add(z);
            cbZList.SelectedIndexChanged += (s, e) => LoadZList();
            var bZFolder = IconBtn("", Tx("Папка zapret"), tt);
            bZFolder.Click += (s, e) => { if (Directory.Exists(st.ZapretPath)) System.Diagnostics.Process.Start("explorer.exe", "\"" + st.ZapretPath + "\""); };
            tbZList = Ui.TextBox("", true);
            tbZList.TextChanged += (s, e) => { if (!loadingZ) { zSave.Stop(); zSave.Start(); lZInfo.Text = "…"; } };
            zSave.Tick += (s, e) => { zSave.Stop(); AutoSaveZList(); };
            lZInfo = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, AutoEllipsis = true };
            dpiListTab.Controls.AddRange(new Control[] { cbZList, bZFolder, tbZList, lZInfo });

            p.Controls.AddRange(new Control[] { left, right });
            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, H = p.ClientSize.Height - Theme.S(20), gap = Theme.S(14), ip = Theme.S(12);
                int lw = Theme.S(260);
                left.SetBounds(L, 0, lw, H);
                int bx = lw - ip;
                for (int i = leftBtns.Length - 1; i >= 0; i--) { bx -= leftBtns[i].Width; leftBtns[i].Location = new Point(bx, Theme.S(8)); bx -= Theme.S(3); }
                lbStrat.SetBounds(Theme.S(8), Theme.S(44), lw - Theme.S(16), H - Theme.S(52));
                right.SetBounds(L + lw + gap, 0, W - lw - gap, H);
                int rw = right.Width;
                tabStrat.Location = new Point(ip, Theme.S(10)); tabLists.Location = new Point(tabStrat.Right + Theme.S(6), Theme.S(10));
                dpiStratTab.SetBounds(ip, Theme.S(50), rw - ip * 2, H - Theme.S(60));
                dpiListTab.SetBounds(ip, Theme.S(50), rw - ip * 2, H - Theme.S(60));
                int tw = dpiStratTab.Width, th = dpiStratTab.Height;
                lName.Location = new Point(0, Theme.S(6));
                bPrev.Location = new Point(tw - bPrev.Width, 0); bStratUse.Location = new Point(bPrev.Left - Theme.S(6) - bStratUse.Width, 0);
                tbStratName.SetBounds(lName.Right + Theme.S(6), Theme.S(2), Math.Max(60, bStratUse.Left - lName.Right - Theme.S(18)), tbStratName.Height);
                hint.SetBounds(0, th - Theme.S(18), tw - Theme.S(150), Theme.S(18)); lStratInfo.SetBounds(tw - Theme.S(150), th - Theme.S(18), Theme.S(150), Theme.S(18));
                tbStratArgs.SetBounds(0, Theme.S(38), tw, th - Theme.S(62));
                bZFolder.Location = new Point(tw - bZFolder.Width, 0); cbZList.SetBounds(0, 0, Math.Min(Theme.S(300), tw - bZFolder.Width - Theme.S(8)), cbZList.Height);
                lZInfo.SetBounds(0, th - Theme.S(18), tw, Theme.S(18));
                tbZList.SetBounds(0, Theme.S(38), tw, th - Theme.S(62));
            };
            p.Resize += (s, e) => lay(); lay();
            FillStrategies(st.DpiStrategy);
            cbZList.SelectedIndex = 0;
            ShowDpiTab(0);
        }

        void ShowDpiTab(int i)
        {
            dpiStratTab.Visible = i == 0; dpiListTab.Visible = i == 1;
            tabStrat.Primary = i == 0; tabLists.Primary = i == 1; tabStrat.Invalidate(); tabLists.Invalidate();
        }

        void FillStrategies(string select)
        {
            if (lbStrat == null || lbStrat.IsDisposed) return;
            lbStrat.BeginUpdate(); lbStrat.Items.Clear();
            lbStrat.Items.AddRange(DpiConfigs.Names());
            lbStrat.EndUpdate();
            if (select != null && lbStrat.Items.Contains(select)) lbStrat.SelectedItem = select;
            else if (lbStrat.Items.Count > 0) lbStrat.SelectedIndex = 0;
            if (cbStrategy != null && !cbStrategy.IsDisposed) LoadConnectUi();
        }

        void LoadStrategy()
        {
            FlushStrategy();
            var n = lbStrat.SelectedItem; if (n == null) return;
            loadingStrat = true;
            try { tbStratName.Text = n; tbStratArgs.Text = DpiConfigs.Read(n); } finally { loadingStrat = false; }
            lStratInfo.Text = n == st.DpiStrategy ? Tx("активна") : "";
            bStratUse.Enabled = n != st.DpiStrategy;
        }

        void FlushStrategy() { if (stratSave.Enabled) { stratSave.Stop(); AutoSaveStrategy(); } }

        // Изменения стратегии сохраняются сами; активная стратегия сразу перезапускает winws
        void AutoSaveStrategy()
        {
            var n = lbStrat.SelectedItem; if (n == null) return;
            string args = tbStratArgs.Text;
            if (DpiConfigs.Flatten(args).Length == 0) { lStratInfo.Text = Tx("Пустая стратегия не сохранена."); return; }
            DpiConfigs.Write(n, args);
            if (n == st.DpiStrategy) router.RestartDpi();
            lStratInfo.Text = Tx("Сохранено") + (n == st.DpiStrategy && router.Dpi.Running ? Tx(" · winws перезапущен") : "");
        }

        void CreateStrategy(string name, string args)
        {
            FlushStrategy();
            string n = Tunnels.Safe(name), baseName = n; int k = 2;
            while (File.Exists(DpiConfigs.PathOf(n))) n = baseName + " " + k++;
            DpiConfigs.Write(n, args);
            Logger.Write(LogLevel.Info, Tx("Стратегия DPI «") + n + Tx("» сохранена"));
            FillStrategies(n);
            tbStratName.Focus(); tbStratName.SelectAll();
        }

        void RenameStrategy()
        {
            var old = lbStrat.SelectedItem; if (old == null) return;
            string name = Tunnels.Safe(tbStratName.Text);
            if (name == old || name.Length == 0) return;
            if (File.Exists(DpiConfigs.PathOf(name))) { lStratInfo.Text = Tx("Имя занято"); return; }
            FlushStrategy();
            DpiConfigs.Write(name, DpiConfigs.Read(old)); DpiConfigs.Delete(old);
            if (st.DpiStrategy == old) { st.DpiStrategy = name; st.Save(); }
            FillStrategies(name);
        }

        void UseStrategy()
        {
            var n = lbStrat.SelectedItem; if (n == null) return;
            FlushStrategy();
            st.DpiStrategy = n; st.Save();
            Logger.Write(LogLevel.Info, Tx("Стратегия DPI: ") + n);
            router.RestartDpi();
            lbStrat.Invalidate(); bStratUse.Enabled = false; lStratInfo.Text = Tx("активна");
            LoadConnectUi();
        }

        void LoadZList()
        {
            if (zSave.Enabled) { zSave.Stop(); AutoSaveZList(); }
            string f = Path.Combine(st.ZapretPath, "lists", (string)cbZList.SelectedItem);
            zListLoaded = f;
            loadingZ = true;
            try
            {
                var fi = new FileInfo(f);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024) { tbZList.Text = ""; tbZList.ReadOnly = true; lZInfo.Text = Tx("Файл большой (") + (fi.Length / 1024) + Tx(" КБ) — откройте его в редакторе: ") + f; zListLoaded = null; return; }
                tbZList.ReadOnly = false;
                tbZList.Text = fi.Exists ? File.ReadAllText(f, Encoding.UTF8) : "";
                lZInfo.Text = fi.Exists ? f : Tx("Файл будет создан: ") + f;
            }
            catch (Exception ex) { lZInfo.Text = ex.Message; }
            finally { loadingZ = false; }
        }

        void AutoSaveZList()
        {
            if (zListLoaded == null) return;
            try
            {
                string text = tbZList.Text.Trim();
                if (text.Length == 0) text = Path.GetFileName(zListLoaded).StartsWith("ipset") ? "203.0.113.113/32" : "domain.example.abc"; // пустой файл zapret трактует иначе
                Directory.CreateDirectory(Path.GetDirectoryName(zListLoaded));
                File.WriteAllText(zListLoaded, text.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n", new UTF8Encoding(false));
                lZInfo.Text = Tx("Сохранено: ") + zListLoaded + (router.Dpi.Running ? Tx(" · winws перечитает список") : "");
            }
            catch (Exception ex) { lZInfo.Text = ex.Message; }
        }
    }
}
