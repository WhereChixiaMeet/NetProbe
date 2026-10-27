using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // Фаза 2: выбор протокола туннеля и динамическая форма полей — только на вкладке «Конфиги» (ТЗ 11.4).
    public partial class MainForm
    {
        TabStrip tsProto; Panel xrForm; Label lXrInfo, lXrWarn;
        TextBox tbXAddr, tbXUser, tbXSni, tbXPbk, tbXSid, tbXPath, tbXHost, tbXLink; Spin nXPort;
        DarkCombo cbXFlow, cbXSec, cbXFp, cbXNet, cbXCipher; ThemedCheck cXInsecure;
        readonly Dictionary<Control, Label> xrLabels = new Dictionary<Control, Label>();
        readonly System.Windows.Forms.Timer xrSave = new System.Windows.Forms.Timer { Interval = 800 };
        bool loadingXr;

        void BuildProtoUi(CardPanel ed, ToolTip tt)
        {
            tsProto = new TabStrip { Segmented = true, Font = Theme.Small, Height = Theme.S(28), Width = Theme.S(340) };
            tsProto.SetTabs(TunnelProto.Names.Select((n, i) => new TabStrip.Tab { Key = TunnelProto.Ids[i], Text = n, Tip = i == 0 ? Tx("WireGuard через WireSock (как раньше)") : n + Tx(" через xray + TUN (sing-box): те же списки, kill switch и авто-переход на DPI") }));
            tsProto.SelectedKey = "wireguard";
            tsProto.Selected += k => SwitchProto(k);
            ed.Controls.Add(tsProto);

            xrForm = new Panel { BackColor = Theme.Card, Visible = false };
            ed.Controls.Add(xrForm);
            Func<string, Control, Control> add = (label, c) =>
            {
                var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false };
                xrForm.Controls.Add(l); xrForm.Controls.Add(c); xrLabels[c] = l;
                if (c is TextBox) ((TextBox)c).TextChanged += (s, e) => XrChanged();
                if (c is DarkCombo) ((DarkCombo)c).SelectedIndexChanged += (s, e) => { XrChanged(); XrLayout(); };
                if (c is Spin) ((Spin)c).ValueChanged += (s, e) => XrChanged();
                return c;
            };
            tbXLink = Ui.TextBox(); ((ThemedTextBox)tbXLink).Cue = "vless:// · vmess:// · trojan://";
            var bLink = new FlatButton(Tx("Из ссылки")) { Glyph = "" }.AutoW();
            bLink.Click += (s, e) => XrFromLink(tbXLink.Text.Trim() != "" ? tbXLink.Text.Trim() : SafeClip());
            tt.SetToolTip(bLink, Tx("Заполнить поля из ссылки (из поля слева или из буфера обмена)"));
            xrForm.Controls.Add(tbXLink); xrForm.Controls.Add(bLink);
            xrLabels[bLink] = null;
            add(Tx("Адрес"), tbXAddr = Ui.TextBox());
            add(Tx("Порт"), nXPort = Ui.Num(0, 65535, 443));
            add("UUID", tbXUser = Ui.TextBox());
            cbXFlow = new DarkCombo(); cbXFlow.Items.AddRange(new object[] { "", "xtls-rprx-vision" }); add("flow", cbXFlow);
            cbXCipher = new DarkCombo(); cbXCipher.Items.AddRange(new object[] { "auto", "aes-128-gcm", "chacha20-poly1305", "none" }); add("cipher", cbXCipher);
            cbXSec = new DarkCombo(); add("security", cbXSec);
            add("SNI", tbXSni = Ui.TextBox());
            cbXFp = new DarkCombo(); cbXFp.Items.AddRange(new object[] { "", "chrome", "firefox", "safari", "edge", "ios", "android", "random" }); add("fingerprint", cbXFp);
            add("publicKey", tbXPbk = Ui.TextBox());
            add("shortId", tbXSid = Ui.TextBox());
            cbXNet = new DarkCombo(); cbXNet.Items.AddRange(new object[] { "tcp", "ws", "grpc", "httpupgrade" }); add(Tx("транспорт"), cbXNet);
            add("path / serviceName", tbXPath = Ui.TextBox());
            add("host", tbXHost = Ui.TextBox());
            cXInsecure = Ui.Check(Tx("Разрешить небезопасные сертификаты"), false); cXInsecure.BackColor = Theme.Card;
            cXInsecure.CheckedChanged += (s, e) => { XrChanged(); XrLayout(); };
            xrForm.Controls.Add(cXInsecure);
            lXrWarn = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Yellow, BackColor = Theme.Mix(Theme.Yellow, Theme.Card, 0.86), UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(Theme.S(8), 0, 0, 0),
                Text = "⚠  " + Tx("Проверка TLS-сертификата выключена: соединение можно перехватить. Включай только если понимаешь зачем.") };
            xrForm.Controls.Add(lXrWarn);
            lXrInfo = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, UseMnemonic = false, AutoEllipsis = true };
            xrForm.Controls.Add(lXrInfo);
            xrSave.Tick += (s, e) => { xrSave.Stop(); SaveXr(); };
            xrForm.Resize += (s, e) => XrLayout();
        }

        static string SafeClip() { try { return Clipboard.GetText(); } catch { return ""; } }

        string CurProto { get { return tsProto != null ? tsProto.SelectedKey ?? "wireguard" : "wireguard"; } }

        // какие поля нужны выбранному протоколу (лишние скрыты)
        void XrLayout()
        {
            if (xrForm == null || xrForm.IsDisposed) return;
            string p = CurProto, sec = cbXSec.SelectedItem as string ?? "none", net = cbXNet.SelectedItem as string ?? "tcp";
            var vis = new Dictionary<Control, bool>
            {
                { tbXAddr, true }, { nXPort, true }, { tbXUser, true },
                { cbXFlow, p == "vless" }, { cbXCipher, p == "vmess" }, { cbXSec, true },
                { tbXSni, sec != "none" }, { cbXFp, sec != "none" }, { tbXPbk, sec == "reality" }, { tbXSid, sec == "reality" },
                { cbXNet, true }, { tbXPath, net != "tcp" }, { tbXHost, net == "ws" || net == "httpupgrade" }
            };
            xrLabels[tbXUser].Text = p == "trojan" ? Tx("пароль") : "UUID";
            int ip = 0, lw = Theme.S(130), W = xrForm.Width, y = 0;
            var link = xrForm.Controls.OfType<FlatButton>().First();
            link.Location = new Point(W - link.Width, 0); tbXLink.SetBounds(0, Theme.S(2), Math.Max(60, link.Left - Theme.S(8)), tbXLink.Height);
            y = Theme.S(40);
            foreach (var kv in vis)
            {
                var c = kv.Key; var l = xrLabels[c];
                c.Visible = l.Visible = kv.Value;
                if (!kv.Value) continue;
                l.Location = new Point(ip, y + Theme.S(4));
                c.Location = new Point(lw, y); c.Width = c is Spin ? Theme.S(110) : Math.Min(Theme.S(420), W - lw);
                y += Math.Max(c.Height, Theme.S(26)) + Theme.S(8);
            }
            bool tls = sec == "tls";
            cXInsecure.Visible = tls; cXInsecure.Location = new Point(lw, y); if (tls) y += Theme.S(30);
            lXrWarn.Visible = tls && cXInsecure.Checked; lXrWarn.SetBounds(lw, y, Math.Min(Theme.S(560), W - lw), Theme.S(24)); if (lXrWarn.Visible) y += Theme.S(32);
            lXrInfo.SetBounds(0, Math.Max(y + Theme.S(4), xrForm.Height - Theme.S(20)), W, Theme.S(18));
        }

        void FillSec(string p, string cur)
        {
            cbXSec.Items.Clear();
            if (p == "vless") cbXSec.Items.AddRange(new object[] { "none", "tls", "reality" });
            else if (p == "trojan") cbXSec.Items.AddRange(new object[] { "tls", "none" });
            else cbXSec.Items.AddRange(new object[] { "none", "tls" });
            cbXSec.SelectedItem = cbXSec.Items.Contains(cur) ? cur : cbXSec.Items[0];
        }

        // показать редактор выбранного туннеля по его протоколу
        void ShowProtoEditor(string name)
        {
            if (tsProto == null) return;
            int idx = name == null ? 0 : TunnelProto.IndexOf(name);
            tsProto.SelectedKey = TunnelProto.Ids[idx]; tsProto.Invalidate();
            bool x = idx > 0;
            xrForm.Visible = x; tbTunText.Visible = !x;
            if (!x || name == null) return;
            var e = TunnelProto.Load(name) ?? new ServerEntry { Protocol = TunnelProto.Ids[idx] };
            loadingXr = true;
            try
            {
                tbXLink.Text = "";
                tbXAddr.Text = e.Address; nXPort.Value = Math.Max(0, Math.Min(65535, e.Port)); tbXUser.Text = e.UserId;
                cbXFlow.SelectedItem = cbXFlow.Items.Contains(e.Flow) ? e.Flow : "";
                cbXCipher.SelectedItem = cbXCipher.Items.Contains(e.Method) ? e.Method : "auto";
                FillSec(e.Protocol, e.Security);
                tbXSni.Text = e.Sni; cbXFp.SelectedItem = cbXFp.Items.Contains(e.Fp) ? e.Fp : "";
                tbXPbk.Text = e.Pbk; tbXSid.Text = e.Sid;
                string net = e.Network == "" ? "tcp" : e.Network; if (!cbXNet.Items.Contains(net)) cbXNet.Items.Add(net); cbXNet.SelectedItem = net;
                tbXPath.Text = e.Network == "grpc" && e.ServiceName != "" ? e.ServiceName : e.Path; tbXHost.Text = e.Host;
                cXInsecure.Checked = e.Insecure;
            }
            finally { loadingXr = false; }
            XrLayout();
            string miss = TunnelProto.Missing(e);
            lXrInfo.Text = miss ?? (e.Transport + " · " + e.Endpoint); lXrInfo.ForeColor = miss != null ? Theme.Yellow : Theme.Muted;
        }

        void XrChanged() { if (loadingXr) return; xrSave.Stop(); xrSave.Start(); lXrInfo.Text = "…"; }

        ServerEntry XrFromForm()
        {
            string p = CurProto, net = cbXNet.SelectedItem as string ?? "tcp";
            return new ServerEntry
            {
                Protocol = p, Name = lbTun.SelectedItem ?? "", Address = tbXAddr.Text.Trim(), Port = (int)nXPort.Value, UserId = tbXUser.Text.Trim(),
                Flow = p == "vless" ? cbXFlow.SelectedItem as string ?? "" : "", Method = p == "vmess" ? cbXCipher.SelectedItem as string ?? "auto" : p == "vless" ? "none" : "",
                Security = cbXSec.SelectedItem as string ?? "none", Sni = tbXSni.Text.Trim(), Fp = cbXFp.SelectedItem as string ?? "", Pbk = tbXPbk.Text.Trim(), Sid = tbXSid.Text.Trim(),
                Network = net, Path = net == "grpc" ? "" : tbXPath.Text.Trim(), ServiceName = net == "grpc" ? tbXPath.Text.Trim() : "", Host = tbXHost.Text.Trim(),
                Insecure = (cbXSec.SelectedItem as string) == "tls" && cXInsecure.Checked
            };
        }

        void SaveXr()
        {
            var n = lbTun.SelectedItem; if (n == null || CurProto == "wireguard") return;
            var e = XrFromForm();
            TunnelProto.Save(n, e);
            string miss = TunnelProto.Missing(e);
            lXrInfo.Text = miss ?? (Tx("Сохранено") + " · " + e.Transport + " · " + e.Endpoint); lXrInfo.ForeColor = miss != null ? Theme.Yellow : Theme.Muted;
            if (router.Active && (n == st.PrimaryTunnel || n == st.BackupTunnel) && miss == null) router.Reapply();
            UpdateModeUi();
        }

        void XrFromLink(string link)
        {
            string err; var e = TunnelProto.FromLink(link, out err);
            if (e == null) { lXrInfo.Text = err; lXrInfo.ForeColor = Theme.Yellow; return; }
            var n = lbTun.SelectedItem; if (n == null) return;
            if (Tunnels.Exists(n) && !TunnelProto.IsXray(n)) Tunnels.Delete(n);
            TunnelProto.Save(n, e);
            ShowProtoEditor(n);
            Logger.Write(LogLevel.Info, Tx("Туннель «") + n + Tx("» заполнен из ссылки: ") + e.Transport + " · " + e.Endpoint);
        }

        // смена протокола у выбранного туннеля
        void SwitchProto(string p)
        {
            var n = lbTun.SelectedItem; if (n == null) return;
            bool wasX = TunnelProto.IsXray(n);
            if (p == "wireguard")
            {
                if (!wasX) return;
                bool d;
                if (Notice.Show(this, Tx("Сменить протокол"), Tx("Туннель «") + n + Tx("» станет WireGuard; поля VLESS/VMess/Trojan будут удалены."), new[] { Tx("Сменить"), Tx("Отмена") }, null, out d) != 0) { ShowProtoEditor(n); return; }
                try { System.IO.File.Delete(TunnelProto.JsonPath(n)); } catch { }
                Tunnels.Write(n, "[Interface]\r\nPrivateKey = \r\nAddress = \r\nMTU = 1420\r\n\r\n[Peer]\r\nPublicKey = \r\nEndpoint = \r\nAllowedIPs = 0.0.0.0/0, ::/0\r\nPersistentKeepalive = 25\r\n");
                LoadTunnel(); return;
            }
            if (!wasX)
            {
                string text = Tunnels.Read(n);
                bool hasKeys = System.Text.RegularExpressions.Regex.IsMatch(text, @"PrivateKey\s*=\s*\S+");
                bool d;
                if (hasKeys && Notice.Show(this, Tx("Сменить протокол"), Tx("Туннель «") + n + Tx("» станет ") + p.ToUpperInvariant() + Tx("; конфиг WireGuard будет удалён."), new[] { Tx("Сменить"), Tx("Отмена") }, null, out d) != 0) { ShowProtoEditor(n); return; }
                try { System.IO.File.Delete(Tunnels.PathOf(n)); } catch { }
                TunnelProto.Save(n, new ServerEntry { Protocol = p, Name = n, Security = p == "trojan" ? "tls" : "none", Network = "tcp", Port = 443 });
            }
            else
            {
                var e = TunnelProto.Load(n) ?? new ServerEntry(); e.Protocol = p;
                if (p != "vless") { e.Flow = ""; if (e.Security == "reality") e.Security = "tls"; }
                TunnelProto.Save(n, e);
            }
            ShowProtoEditor(n);
            UpdateModeUi();
        }

        // «новый из ссылки» (кнопка в списке туннелей): vless/vmess/trojan из буфера → новый туннель
        void NewTunnelFromLink()
        {
            string err; var e = TunnelProto.FromLink(SafeClip(), out err);
            if (e == null) { Notice.Show(this, Tx("Новый туннель из ссылки"), err + "\r\n" + Tx("Скопируй ссылку vless:// / vmess:// / trojan:// в буфер обмена и нажми ещё раз.")); return; }
            FlushTunnel();
            string baseName = Tunnels.Safe(string.IsNullOrWhiteSpace(e.Name) ? e.Protocol + " " + e.Address : e.Name), n = baseName; int k = 2;
            while (Tunnels.Exists(n)) n = baseName + " " + k++;
            e.Name = n; TunnelProto.Save(n, e);
            Logger.Write(LogLevel.Info, Tx("Туннель «") + n + Tx("» добавлен из ссылки: ") + e.Transport + " · " + e.Endpoint);
            if (!Tunnels.Exists(st.PrimaryTunnel)) { st.PrimaryTunnel = n; st.Save(); }
            FillTunnels(n); LoadConnectUi(); UpdateModeUi();
        }
    }
}
