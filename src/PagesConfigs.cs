using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        Panel cfgVpn, cfgDpi; FlatButton tabVpn, tabDpi; int cfgTab;
        ThemedList lbTun; TextBox tbTunName, tbTunText; Label lTunInfo;

        void BuildConfigsPage()
        {
            var p = NewPage(Tx("Конфиги"), Tx("VPN-туннели и стратегии DPI"));
            tabVpn = new FlatButton("VPN") { Glyph = "" }.AutoW();
            tabDpi = new FlatButton("DPI") { Glyph = "" }.AutoW();
            tabVpn.Width = tabDpi.Width = Math.Max(tabVpn.Width, tabDpi.Width) + Theme.S(20);
            tabVpn.Click += (s, e) => ShowCfgTab(0); tabDpi.Click += (s, e) => ShowCfgTab(1);
            cfgVpn = new Panel { BackColor = Theme.Bg }; cfgDpi = new Panel { BackColor = Theme.Bg };
            p.Controls.AddRange(new Control[] { tabVpn, tabDpi, cfgVpn, cfgDpi });
            BuildVpnPanel(cfgVpn);
            BuildDpiPanel(cfgDpi);
            Action lay = () =>
            {
                int L = Theme.S(26);
                tabDpi.Location = new Point(p.ClientSize.Width - L - tabDpi.Width, Theme.S(24));
                tabVpn.Location = new Point(tabDpi.Left - Theme.S(6) - tabVpn.Width, Theme.S(24));
                cfgVpn.SetBounds(0, Theme.S(84), p.ClientSize.Width, p.ClientSize.Height - Theme.S(84));
                cfgDpi.SetBounds(0, Theme.S(84), p.ClientSize.Width, p.ClientSize.Height - Theme.S(84));
            };
            p.Resize += (s, e) => lay(); lay();
            ShowCfgTab(cfgTab);
        }

        void ShowCfgTab(int i)
        {
            cfgTab = i;
            cfgVpn.Visible = i == 0; cfgDpi.Visible = i == 1;
            tabVpn.Primary = i == 0; tabDpi.Primary = i == 1; tabVpn.Invalidate(); tabDpi.Invalidate();
        }

        void BuildVpnPanel(Panel p)
        {
            var tt = Ui.Tip();
            var left = new CardPanel { Title = Tx("Туннели") };
            lbTun = new ThemedList { RightText = n => n == st.PrimaryTunnel ? Tx("основной") : n == st.BackupTunnel ? Tx("резервный") : null };
            lbTun.SelectedIndexChanged += (s, e) => LoadTunnel();
            var bNew = IconBtn("", Tx("Новый"), tt);
            bNew.Click += (s, e) =>
            {
                FlushTunnel();
                string n = "tunnel"; int k = 2; while (Tunnels.Exists(n)) n = "tunnel " + k++;
                Tunnels.Write(n, "[Interface]\r\nPrivateKey = \r\nAddress = \r\nMTU = 1420\r\n\r\n[Peer]\r\nPublicKey = \r\nEndpoint = \r\nAllowedIPs = 0.0.0.0/0, ::/0\r\nPersistentKeepalive = 25\r\n");
                FillTunnels(n); tbTunName.Focus(); tbTunName.SelectAll();
            };
            var bImp = IconBtn("", Tx("Импорт .conf"), tt); bImp.Click += (s, e) => ImportConf();
            var bDel = IconBtn("", Tx("Удалить"), tt); bDel.Click += (s, e) => DeleteTunnel(lbTun.SelectedItem);
            var leftBtns = new[] { bNew, bImp, bDel };
            left.Controls.Add(lbTun); left.Controls.AddRange(leftBtns);

            var ed = new CardPanel { Title = "WireGuard" };
            var lName = new Label { Text = Tx("Имя:"), AutoSize = true, ForeColor = Theme.Muted, BackColor = Theme.Card };
            tbTunName = Ui.TextBox();
            tbTunName.Leave += (s, e) => RenameTunnel();
            tbTunName.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { RenameTunnel(); e.SuppressKeyPress = true; } };
            tbTunText = Ui.TextBox("", true);
            tbTunText.TextChanged += (s, e) => { if (!loadingTun) { tunSave.Stop(); tunSave.Start(); lTunInfo.Text = "…"; } };
            tunSave.Tick += (s, e) => { tunSave.Stop(); SaveTunnel(); };
            var bPrim = new FlatButton(Tx("Основной")) { Glyph = "" }.AutoW();
            bPrim.Click += (s, e) => { var n = lbTun.SelectedItem; if (n == null) return; FlushTunnel(); st.PrimaryTunnel = n; st.Save(); lbTun.Invalidate(); LoadConnectUi(); if (router.Active) router.Reapply(); };
            var bBack = new FlatButton(Tx("Резервный")) { Glyph = "" }.AutoW();
            bBack.Click += (s, e) => { var n = lbTun.SelectedItem; if (n == null) return; FlushTunnel(); st.BackupTunnel = n; st.BackupEnabled = true; st.Save(); lbTun.Invalidate(); LoadConnectUi(); };
            lTunInfo = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, TextAlign = ContentAlignment.MiddleRight };
            ed.Controls.AddRange(new Control[] { lName, tbTunName, tbTunText, bPrim, bBack, lTunInfo });
            p.Controls.AddRange(new Control[] { left, ed });

            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, H = p.ClientSize.Height - Theme.S(20), gap = Theme.S(14), lw = Theme.S(260), ip = Theme.S(12);
                left.SetBounds(L, 0, lw, H);
                int bx = lw - ip;
                for (int i = leftBtns.Length - 1; i >= 0; i--) { bx -= leftBtns[i].Width; leftBtns[i].Location = new Point(bx, Theme.S(8)); bx -= Theme.S(3); }
                lbTun.SetBounds(Theme.S(8), Theme.S(44), lw - Theme.S(16), H - Theme.S(52));
                ed.SetBounds(L + lw + gap, 0, W - lw - gap, H);
                int ew = ed.Width - ip * 2;
                bBack.Location = new Point(ed.Width - ip - bBack.Width, Theme.S(8)); bPrim.Location = new Point(bBack.Left - Theme.S(6) - bPrim.Width, Theme.S(8));
                lName.Location = new Point(ip, Theme.S(54)); tbTunName.SetBounds(lName.Right + Theme.S(6), Theme.S(50), Math.Min(Theme.S(320), ew - lName.Width), tbTunName.Height);
                lTunInfo.SetBounds(ip, H - Theme.S(28), ew, Theme.S(18));
                tbTunText.SetBounds(ip, Theme.S(84), ew, H - Theme.S(118));
            };
            p.Resize += (s, e) => lay(); lay();
            FillTunnels(st.PrimaryTunnel);
        }

        bool loadingTun;
        readonly System.Windows.Forms.Timer tunSave = new System.Windows.Forms.Timer { Interval = 900 };

        void FlushTunnel() { if (tunSave.Enabled) { tunSave.Stop(); SaveTunnel(); } }

        void FillTunnels(string select)
        {
            if (lbTun == null || lbTun.IsDisposed) return;
            lbTun.BeginUpdate(); lbTun.Items.Clear();
            lbTun.Items.AddRange(Tunnels.Names());
            lbTun.EndUpdate();
            if (select != null && lbTun.Items.Contains(select)) lbTun.SelectedItem = select;
            else if (lbTun.Items.Count > 0) lbTun.SelectedIndex = 0;
            else { loadingTun = true; tbTunName.Text = ""; tbTunText.Text = ""; loadingTun = false; }
        }

        void LoadTunnel()
        {
            FlushTunnel();
            var n = lbTun.SelectedItem; if (n == null) return;
            loadingTun = true;
            try { tbTunName.Text = n; tbTunText.Text = Tunnels.Read(n).Replace("\r\n", "\n").Replace("\n", "\r\n"); } finally { loadingTun = false; }
            lTunInfo.Text = Tunnels.Endpoint(n);
        }

        // Конфиг сохраняется сам через секунду после правки
        void SaveTunnel()
        {
            var n = lbTun.SelectedItem; if (n == null) return;
            string text = tbTunText.Text;
            if (text.IndexOf("[Interface]", StringComparison.OrdinalIgnoreCase) < 0 || text.IndexOf("[Peer]", StringComparison.OrdinalIgnoreCase) < 0) { lTunInfo.Text = Tx("Нужны секции [Interface] и [Peer]"); return; }
            Tunnels.Write(n, text);
            lTunInfo.Text = Tx("Сохранено") + " · " + Tunnels.Endpoint(n);
            if (router.Active && (n == st.PrimaryTunnel || n == st.BackupTunnel)) router.Reapply();
        }

        void RenameTunnel()
        {
            var old = lbTun.SelectedItem; if (old == null) return;
            string name = Tunnels.Safe(tbTunName.Text);
            if (name == old || name.Length == 0) return;
            if (Tunnels.Exists(name)) { lTunInfo.Text = Tx("Имя занято"); return; }
            FlushTunnel();
            Tunnels.Write(name, Tunnels.Read(old)); Tunnels.Delete(old);
            if (st.PrimaryTunnel == old) st.PrimaryTunnel = name;
            if (st.BackupTunnel == old) st.BackupTunnel = name;
            st.Save();
            Logger.Write(LogLevel.Info, Tx("Туннель «") + name + Tx("» сохранён"));
            FillTunnels(name); LoadConnectUi();
        }

        void ImportConf()
        {
            using (var d = new OpenFileDialog { Filter = Tx("WireGuard (*.conf)|*.conf|Все файлы|*.*"), Multiselect = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string last = null;
                foreach (var f in d.FileNames)
                {
                    try
                    {
                        string text = File.ReadAllText(f);
                        if (text.IndexOf("[Interface]", StringComparison.OrdinalIgnoreCase) < 0 || text.IndexOf("[Peer]", StringComparison.OrdinalIgnoreCase) < 0) { MessageBox.Show(this, Path.GetFileName(f) + Tx(": это не конфиг WireGuard."), "NetProbe"); continue; }
                        last = Tunnels.Safe(Path.GetFileNameWithoutExtension(f));
                        Tunnels.Write(last, text);
                        Logger.Write(LogLevel.Info, Tx("Импортирован туннель «") + last + "»");
                    }
                    catch (Exception ex) { MessageBox.Show(this, ex.Message, "NetProbe"); }
                }
                if (last != null && !Tunnels.Exists(st.PrimaryTunnel)) { st.PrimaryTunnel = last; st.Save(); }
                FillTunnels(last); LoadConnectUi();
            }
        }

        void DeleteTunnel(string name)
        {
            if (!Tunnels.Exists(name)) return;
            if (MessageBox.Show(this, Tx("Удалить туннель «") + name + "»?", "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Tunnels.Delete(name);
            if (st.PrimaryTunnel == name) st.PrimaryTunnel = "";
            if (st.BackupTunnel == name) st.BackupTunnel = "";
            st.Save(); FillTunnels(null); LoadConnectUi();
        }
    }
}
