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
        ThemedList lbTun; TextBox tbTunName, tbTunText; Label lTunInfo;

        // 3.0: VPN-конфиги и DPI-конфиги — два отдельных пункта меню (раньше одна страница «Конфиги» с переключателем VPN/DPI)
        void BuildConfigsPage()
        {
            var p = NewPage(Tx("VPN-конфиги"), Tx("Туннели: WireGuard, VLESS, VMess, Trojan"));
            var host = new Panel { BackColor = Theme.Bg };
            p.Controls.Add(host);
            BuildVpnPanel(host);
            Action lay = () => host.SetBounds(0, Theme.S(84), PW(p), p.ClientSize.Height - Theme.S(84));
            p.Resize += (s, e) => lay(); lay();
        }

        void BuildDpiConfigsPage()
        {
            var p = NewPage(Tx("DPI-конфиги"), Tx("Стратегии zapret, сайты и исключения"));
            var host = new Panel { BackColor = Theme.Bg };
            p.Controls.Add(host);
            BuildDpiPanel(host);
            Action lay = () => host.SetBounds(0, Theme.S(84), PW(p), p.ClientSize.Height - Theme.S(84));
            p.Resize += (s, e) => lay(); lay();
        }

        void BuildVpnPanel(Panel p)
        {
            var tt = Ui.Tip();
            var left = new CardPanel { Title = Tx("Туннели") };
            lbTun = new ThemedList { RightText = n => (TunnelProto.IsXray(n) ? TunnelProto.Names[TunnelProto.IndexOf(n)] + (n == st.PrimaryTunnel || n == st.BackupTunnel ? " · " : "") : "") + (n == st.PrimaryTunnel ? Tx("основной") : n == st.BackupTunnel ? Tx("резервный") : "") };
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
            var bLinkNew = IconBtn("\uE71B", Tx("Новый из ссылки (vless / vmess / trojan из буфера)"), tt); bLinkNew.Click += (s, e) => NewTunnelFromLink();
            var bDel = IconBtn("", Tx("Удалить"), tt); bDel.Click += (s, e) => DeleteTunnel(lbTun.SelectedItem);
            var leftBtns = new[] { bNew, bLinkNew, bImp, bDel };
            left.Controls.Add(lbTun); left.Controls.AddRange(leftBtns);

            var ed = new CardPanel { Title = Tx("Туннель") };
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
            BuildProtoUi(ed, tt);
            p.Controls.AddRange(new Control[] { left, ed });

            Action lay = () =>
            {
                int L = Theme.S(26), W = PW(p) - L * 2, H = p.ClientSize.Height - Theme.S(20), gap = Theme.S(14), lw = Math.Max(Theme.S(200), Math.Min(Theme.S(260), W / 3)), ip = Theme.S(12);
                left.SetBounds(L, 0, lw, H);
                int bx = lw - ip;
                for (int i = leftBtns.Length - 1; i >= 0; i--) { bx -= leftBtns[i].Width; leftBtns[i].Location = new Point(bx, Theme.S(8)); bx -= Theme.S(3); }
                lbTun.SetBounds(Theme.S(8), Theme.S(44), lw - Theme.S(16), H - Theme.S(52));
                ed.SetBounds(L + lw + gap, 0, W - lw - gap, H);
                int ew = ed.Width - ip * 2;
                bBack.Location = new Point(ed.Width - ip - bBack.Width, Theme.S(8)); bPrim.Location = new Point(bBack.Left - Theme.S(6) - bPrim.Width, Theme.S(8));
                lName.Location = new Point(ip, Theme.S(54)); tbTunName.SetBounds(lName.Right + Theme.S(6), Theme.S(50), Math.Min(Theme.S(320), ew - lName.Width), tbTunName.Height);
                lTunInfo.SetBounds(ip, H - Theme.S(28), ew, Theme.S(18));
                tsProto.SetBounds(ip, Theme.S(86), Math.Min(Theme.S(340), ew), tsProto.Height);
                tbTunText.SetBounds(ip, Theme.S(124), ew, H - Theme.S(158));
                xrForm.SetBounds(ip, Theme.S(124), ew, H - Theme.S(158));
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
            FlushTunnel(); if (xrSave.Enabled) { xrSave.Stop(); SaveXr(); }
            var n = lbTun.SelectedItem; if (n == null) return;
            loadingTun = true;
            try { tbTunName.Text = n; tbTunText.Text = TunnelProto.IsXray(n) ? "" : Tunnels.Read(n).Replace("\r\n", "\n").Replace("\n", "\r\n"); } finally { loadingTun = false; }
            lTunInfo.Text = Tunnels.Endpoint(n);
            ShowProtoEditor(n);
        }

        // Конфиг сохраняется сам через секунду после правки
        void SaveTunnel()
        {
            var n = lbTun.SelectedItem; if (n == null || TunnelProto.IsXray(n)) return;
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
            if (xrSave.Enabled) { xrSave.Stop(); SaveXr(); }
            if (TunnelProto.IsXray(old)) { var xe = TunnelProto.Load(old); if (xe != null) { xe.Name = name; TunnelProto.Save(name, xe); } Tunnels.Delete(old); }
            else { Tunnels.Write(name, Tunnels.Read(old)); Tunnels.Delete(old); }
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
