using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public enum RouteKind { Vpn, Server, Dpi, Direct, Blocked }

    // «Куда пойдёт домен / соединение» — по тому, что реально запущено сейчас:
    // конфиг WireSock (config\runtime\tunnel.conf: AllowedApps / DisallowedApps / DisallowedIPs), списки winws из стратегии,
    // xray (системный прокси) / sing-box (TUN) и kill switch. Ничего не меняет и в сеть не ходит (кроме DNS-запроса домена).
    public class RouteFacts
    {
        public bool Wg, WgStrict, Dpi, Xray, Tun, Proxy, Kill;
        public string Tunnel = "", Strategy = "", Server = "";
        public List<string> Allow = new List<string>(), Deny = new List<string>(), Strict = new List<string>();
        public List<Cidr> AllowedIps = new List<Cidr>(), DisallowedIps = new List<Cidr>();
        public List<DpiProfile> Profiles = new List<DpiProfile>();
        public TunLists TunL;
        public List<string> XrayDirect = new List<string>();

        public class DpiProfile
        {
            public List<string> Hosts, HostsEx; public List<Cidr> Ips, IpsEx;
        }

        public static RouteFacts Take(Router router, XrayManager xm, Settings st)
        {
            var f = new RouteFacts();
            f.Strict = AppLists.Get(AppLists.VpnOnlyApps);
            f.Kill = KillSwitch.Active;
            f.Wg = router.Wg.Running;
            if (f.Wg)
            {
                f.Tunnel = router.Wg.TunnelName; f.WgStrict = router.StrictOnly;
                try
                {
                    foreach (var raw in File.ReadAllLines(WiresockRunner.RuntimeFile))
                    {
                        int i = raw.IndexOf('='); if (i <= 0) continue;
                        string k = raw.Substring(0, i).Trim().ToLowerInvariant();
                        var vals = raw.Substring(i + 1).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                        if (k == "allowedapps") f.Allow.AddRange(vals);
                        else if (k == "disallowedapps") f.Deny.AddRange(vals);
                        else if (k == "allowedips") f.AllowedIps.AddRange(vals.Select(Cidr.Parse).Where(c => c != null));
                        else if (k == "disallowedips") f.DisallowedIps.AddRange(vals.Select(Cidr.Parse).Where(c => c != null));
                    }
                }
                catch { }
            }
            f.Dpi = router.Dpi.Running;
            if (f.Dpi)
            {
                f.Strategy = router.Dpi.Strategy;
                try { f.Profiles = ParseProfiles(ZapretRunner.BuildArgs(st, DpiConfigs.Read(f.Strategy))); } catch { }
            }
            f.Xray = xm.Active && xm.State != XState.Off;
            if (f.Xray)
            {
                var srv = ServerStore.Find(st.ActiveServer); f.Server = srv != null ? srv.Name : "";
                f.Tun = xm.TunRunning;
                f.Proxy = !f.Tun && (st.SysProxy || SysProxy.IsOurs());
                try { if (f.Tun && xm.GetTunLists != null) f.TunL = xm.GetTunLists(); } catch { }
                try { if (!f.Tun && XrayConfig.DirectDomains != null) f.XrayDirect = XrayConfig.DirectDomains(); } catch { }
            }
            return f;
        }

        // профили winws (--new): какие hostlist / ipset в каждом
        static List<DpiProfile> ParseProfiles(string args)
        {
            var res = new List<DpiProfile>();
            var files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Func<string, List<string>> read = p =>
            {
                List<string> l;
                if (files.TryGetValue(p, out l)) return l;
                l = new List<string>();
                try { if (File.Exists(p)) l = File.ReadAllLines(p).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0 && !x.StartsWith("#")).ToList(); } catch { }
                files[p] = l; return l;
            };
            foreach (var part in Regex.Split(args, @"\s+--new(?=\s|$)"))
            {
                var pr = new DpiProfile();
                foreach (Match m in Regex.Matches(part, "--(hostlist|hostlist-exclude|hostlist-domains|ipset|ipset-exclude)=(\"([^\"]*)\"|(\\S+))"))
                {
                    string k = m.Groups[1].Value, v = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
                    if (k == "hostlist-domains") (pr.Hosts = pr.Hosts ?? new List<string>()).AddRange(v.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
                    else if (k == "hostlist") (pr.Hosts = pr.Hosts ?? new List<string>()).AddRange(read(v));
                    else if (k == "hostlist-exclude") (pr.HostsEx = pr.HostsEx ?? new List<string>()).AddRange(read(v));
                    else if (k == "ipset") (pr.Ips = pr.Ips ?? new List<Cidr>()).AddRange(read(v).Select(Cidr.Parse).Where(c => c != null));
                    else (pr.IpsEx = pr.IpsEx ?? new List<Cidr>()).AddRange(read(v).Select(Cidr.Parse).Where(c => c != null));
                }
                res.Add(pr);
            }
            return res;
        }

        public static bool HostIn(string host, IEnumerable<string> list)
        {
            if (string.IsNullOrEmpty(host) || list == null) return false;
            host = host.TrimEnd('.').ToLowerInvariant();
            foreach (var e in list) { var s = e.TrimStart('*', '.', '^'); if (s.Length > 0 && (host == s || host.EndsWith("." + s))) return true; }
            return false;
        }

        static bool IpIn(IPAddress ip, IEnumerable<Cidr> list) { return ip != null && list != null && list.Any(c => c.Contains(ip)); }

        // winws обработает соединение? null = нет (с причиной в why)
        public bool DpiTouches(string host, IPAddress ip, out string why)
        {
            why = "";
            if (!Dpi) { why = Tx("DPI (winws) выключен"); return false; }
            bool excluded = false;
            foreach (var p in Profiles)
            {
                if (HostIn(host, p.HostsEx) || IpIn(ip, p.IpsEx)) { excluded = true; continue; }
                bool hostOk = p.Hosts == null || HostIn(host, p.Hosts);
                bool ipOk = p.Ips == null || IpIn(ip, p.Ips);
                if (hostOk && ipOk) { why = Tx("winws обрабатывает (стратегия «") + Strategy + "»)"; return true; }
            }
            why = excluded ? Tx("в исключениях DPI") : string.IsNullOrEmpty(host) ? Tx("имя домена неизвестно, по IP winws не трогает") : Tx("домена нет в списках DPI — winws его не трогает");
            return false;
        }

        static readonly string[] Browsers = { "chrome", "brave", "msedge", "firefox", "opera", "browser", "vivaldi", "yandex", "thorium", "librewolf", "floorp", "zen" };
        static bool UsesProxy(string name) { return Browsers.Any(b => name.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0); }

        // маршрут одного соединения: приложение (имя + путь) → IP (+ имя хоста, если известно)
        public RouteKind Decide(string name, string path, IPAddress ip, string host, out string why)
        {
            name = name ?? ""; path = path ?? "";
            Func<IEnumerable<string>, bool> m = l => l.Any(r => ProcInfo.Matches(r, name, path));
            if (Kill && m(Strict)) { why = Tx("kill switch: «Строго VPN», а VPN недоступен"); return RouteKind.Blocked; }
            if (ip != null && IsPrivate(ip)) { why = Tx("локальная сеть"); return RouteKind.Direct; }
            if (Tun && TunL != null)
            {
                foreach (var d in TunL.Domains)
                    if (ProcInfo.Matches(d.Key, name, path) && (ExclusionSet.IsIp(d.Value) ? IpIn(ip, new[] { Cidr.Parse(d.Value) }.Where(c => c != null)) : HostIn(host, DomainRoutes.Expand(d.Value)) || DomainRoutes.IsLearned(ip, d.Value)))
                        return Dpi_(host, ip, Tx("домен в «Через DPI» под ") + d.Key + Tx(" — мимо сервера"), out why);
                var ex = ExclusionSet.From(TunL.Exclude);
                if (m(ex.Processes) || HostIn(host, ex.Domains) || IpIn(ip, ex.Ips.Select(Cidr.Parse).Where(c => c != null))) return Dpi_(host, ip, Tx("в исключениях «Серверов» (TUN)"), out why);
                if (TunL.Include.Count == 0 || m(TunL.Include)) { why = "TUN → " + Tx("сервер «") + Server + "»"; return RouteKind.Server; }
                return Dpi_(host, ip, Tx("не в Include «Серверов» — мимо TUN"), out why);
            }
            if (Proxy && UsesProxy(name))
            {
                if (HostIn(host, XrayDirect.Where(x => !ExclusionSet.IsIp(x)))) return Dpi_(host, ip, Tx("домен в «Через DPI» «Серверов» — xray пускает напрямую"), out why);
                why = Tx("системный прокси → сервер «") + Server + "»"; return RouteKind.Server;
            }
            if (Wg)
            {
                bool inTun = (Allow.Count == 0 || m(Allow)) && !m(Deny);
                if (inTun)
                {
                    if (ip != null && IpIn(ip, DisallowedIps)) return Dpi_(host, ip, Tx("IP исключён из туннеля (Домены через DPI)"), out why);
                    if (ip == null || AllowedIps.Count == 0 || IpIn(ip, AllowedIps)) { why = Tx("в туннеле «") + Tunnel + "»" + (WgStrict ? Tx(" (авария: только «Строго VPN»)") : ""); return RouteKind.Vpn; }
                    return Dpi_(host, ip, Tx("IP вне AllowedIPs туннеля"), out why);
                }
                return Dpi_(host, ip, WgStrict ? Tx("авария VPN: в туннеле только «Строго VPN»") : Tx("приложение не в списках туннеля"), out why);
            }
            return Dpi_(host, ip, Tx("VPN выключен"), out why);
        }

        RouteKind Dpi_(string host, IPAddress ip, string reason, out string why)
        {
            string dw;
            bool d = DpiTouches(host, ip, out dw);
            why = reason + " · " + dw;
            return d ? RouteKind.Dpi : RouteKind.Direct;
        }

        static bool IsPrivate(IPAddress a)
        {
            if (IPAddress.IsLoopback(a)) return true;
            if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (a.GetAddressBytes()[0] & 0xFE) == 0xFC;
            var b = a.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] < 128);
        }

        public static string Name(RouteKind k)
        {
            switch (k) { case RouteKind.Vpn: return "VPN"; case RouteKind.Server: return Tx("Сервер"); case RouteKind.Dpi: return "DPI"; case RouteKind.Blocked: return Tx("Блок"); default: return Tx("Напрямую"); }
        }

        public static Color ColorOf(RouteKind k)
        {
            switch (k) { case RouteKind.Vpn: case RouteKind.Server: return Theme.Vpn; case RouteKind.Dpi: return Theme.Yellow; case RouteKind.Blocked: return Theme.Red; default: return Theme.Direct; }
        }
    }

    public class Cidr
    {
        byte[] net; int bits;
        public static Cidr Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Trim(); int bits = -1; int i = s.IndexOf('/');
            if (i > 0) { if (!int.TryParse(s.Substring(i + 1), out bits)) return null; s = s.Substring(0, i); }
            IPAddress a; if (!IPAddress.TryParse(s, out a)) return null;
            var b = a.GetAddressBytes();
            if (bits < 0 || bits > b.Length * 8) bits = b.Length * 8;
            return new Cidr { net = b, bits = bits };
        }
        public bool Contains(IPAddress ip)
        {
            var b = ip.GetAddressBytes();
            if (b.Length != net.Length) { if (ip.IsIPv4MappedToIPv6 && net.Length == 4) b = ip.MapToIPv4().GetAddressBytes(); else return false; }
            int full = bits / 8, rest = bits % 8;
            for (int i = 0; i < full; i++) if (b[i] != net[i]) return false;
            if (rest == 0) return true;
            int mask = 0xFF << (8 - rest) & 0xFF;
            return (b[full] & mask) == (net[full] & mask);
        }
    }

    // Окно «Маршрут»: приложение + домен → VPN / DPI / напрямую / сервер, с причиной; ниже — живые соединения
    public class RouteDialog : Form
    {
        readonly Func<RouteFacts> facts;
        readonly TextBox tbApp, tbDom;
        readonly Label lVerdict, lWhy, lConns;
        readonly DataGridView grid;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 3000 };
        volatile bool busy;
        string resolvedFor = ""; List<IPAddress> resolved = new List<IPAddress>();

        public RouteDialog(Func<RouteFacts> facts, string app, string domain)
        {
            this.facts = facts;
            Text = Tx("Маршрут"); StartPosition = FormStartPosition.CenterParent; Size = new Size(Theme.S(820), Theme.S(600)); MinimumSize = new Size(Theme.S(600), Theme.S(420));
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body; MinimizeBox = false; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
            var lApp = Ui.Label(Tx("Приложение"), Theme.Small, Theme.Muted);
            var lDom = Ui.Label(Tx("Домен"), Theme.Small, Theme.Muted);
            tbApp = Ui.TextBox(app ?? ""); tbDom = Ui.TextBox(domain ?? "");
            var bPick = new FlatButton("") { Glyph = "", Width = Theme.S(34), Height = tbApp.Height + Theme.S(2) };
            bPick.Click += (s, e) =>
            {
                using (var d = new ProcPicker(Tx("Приложение"), AppLists.Recent()))
                    if (d.ShowDialog(this) == DialogResult.OK && d.Result.Count > 0) { tbApp.Text = d.Result[0]; Run(); }
            };
            var bGo = new FlatButton(Tx("Проверить"), true) { Glyph = "" }.AutoW();
            bGo.Click += (s, e) => Run();
            KeyEventHandler enter = (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Run(); } };
            tbApp.KeyDown += enter; tbDom.KeyDown += enter;
            var tip = Ui.Tip();
            tip.SetToolTip(tbApp, Tx("Имя процесса (brave.exe) или путь. Пусто — все приложения"));
            tip.SetToolTip(tbDom, Tx("Например youtube.com. Пусто — все соединения приложения"));

            var card = new CardPanel();
            lVerdict = new Label { AutoSize = false, Font = Theme.H1, ForeColor = Theme.Text, BackColor = Color.Transparent, UseMnemonic = false };
            lWhy = new Label { AutoSize = false, Font = Theme.Body, ForeColor = Theme.Muted, BackColor = Color.Transparent, UseMnemonic = false };
            card.Controls.AddRange(new Control[] { lVerdict, lWhy });

            lConns = Ui.Label(Tx("Соединения сейчас"), Theme.Small, Theme.Muted);
            var gcard = new CardPanel();
            grid = new DataGridView(); Ui.StyleGrid(grid);
            grid.Columns.Add("a", Tx("ПРИЛОЖЕНИЕ")); grid.Columns.Add("h", Tx("ДОМЕН")); grid.Columns.Add("ip", "IP"); grid.Columns.Add("r", Tx("МАРШРУТ")); grid.Columns.Add("w", Tx("ПОЧЕМУ"));
            float[] fw = { 14, 22, 15, 9, 40 };
            for (int i = 0; i < fw.Length; i++) grid.Columns[i].FillWeight = fw[i];
            grid.Columns[2].DefaultCellStyle.ForeColor = grid.Columns[4].DefaultCellStyle.ForeColor = Theme.Muted;
            grid.ShowCellToolTips = true;
            gcard.Controls.Add(grid);
            gcard.Resize += (s, e) => grid.SetBounds(Theme.S(8), Theme.S(8), gcard.Width - Theme.S(16), gcard.Height - Theme.S(16));

            Controls.AddRange(new Control[] { lApp, lDom, tbApp, bPick, tbDom, bGo, card, lConns, gcard });
            Action lay = () =>
            {
                int p = Theme.S(16), W = ClientSize.Width - p * 2, half = (W - bGo.Width - Theme.S(16)) / 2;
                lApp.Location = new Point(p, p); lDom.Location = new Point(p + half + Theme.S(8), p);
                int y = lApp.Bottom + Theme.S(4);
                tbApp.SetBounds(p, y, half - bPick.Width - Theme.S(4), tbApp.Height); bPick.Location = new Point(tbApp.Right + Theme.S(4), y - Theme.S(1));
                tbDom.SetBounds(p + half + Theme.S(8), y, half, tbDom.Height);
                bGo.Location = new Point(p + W - bGo.Width, y - Theme.S(4));
                y = tbApp.Bottom + Theme.S(14);
                card.SetBounds(p, y, W, Theme.S(118));
                lVerdict.SetBounds(Theme.S(16), Theme.S(12), W - Theme.S(32), Theme.S(34));
                lWhy.SetBounds(Theme.S(16), lVerdict.Bottom + Theme.S(2), W - Theme.S(32), card.Height - lVerdict.Bottom - Theme.S(10));
                y = card.Bottom + Theme.S(12);
                lConns.Location = new Point(p, y); y = lConns.Bottom + Theme.S(6);
                gcard.SetBounds(p, y, W, ClientSize.Height - p - y);
            };
            Resize += (s, e) => lay(); lay();
            timer.Tick += (s, e) => Run(true);
            Shown += (s, e) => { Run(); timer.Start(); };
            FormClosed += (s, e) => timer.Dispose();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); }

        static string NormDomain(string d)
        {
            d = (d ?? "").Trim().ToLowerInvariant();
            int i = d.IndexOf("://"); if (i >= 0) d = d.Substring(i + 3);
            i = d.IndexOfAny(new[] { '/', ':', '?' }); if (i >= 0) d = d.Substring(0, i);
            return d.TrimEnd('.');
        }

        class Row { public string App, Host, Ip, Why; public RouteKind Kind; }

        void Run(bool quiet = false)
        {
            if (busy) return;
            busy = true;
            string app = tbApp.Text.Trim(), dom = NormDomain(tbDom.Text);
            if (!quiet && dom == "" && app == "") { lVerdict.Text = Tx("Введите приложение и/или домен"); lVerdict.ForeColor = Theme.Muted; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string verdict = null, why = null; Color vc = Theme.Text; var rows = new List<Row>();
                try
                {
                    var f = facts();
                    // IP домена: свежий DNS (раз на домен) + кэш DNS Windows по «семье» домена (youtube → googlevideo …)
                    var family = dom != "" ? DomainRoutes.Expand(dom) : new List<string>();
                    if (dom != "" && dom != resolvedFor)
                    {
                        var l = new List<IPAddress>();
                        try { l.AddRange(Dns.GetHostAddresses(dom)); } catch { }
                        resolved = l.Where(a => !IPAddress.IsLoopback(a)).Distinct().ToList(); resolvedFor = dom;
                    }
                    var ipHost = new Dictionary<string, string>();
                    try { foreach (var kv in DnsCache.Read(h => true)) if (!ipHost.ContainsKey(kv.Value)) ipHost[kv.Value] = kv.Key; } catch { }
                    if (dom != "") foreach (var a in resolved) ipHost[a.ToString()] = dom;
                    Func<string, string> hostOf = ip => { string h; if (ipHost.TryGetValue(ip, out h)) return h; return DomainRoutes.LearnedHost(ip); };

                    // приложение: имя и путь (по запущенному процессу, если есть)
                    string appName = app, appPath = app.Contains("\\") ? app : "";
                    if (app != "" && !app.Contains("\\"))
                        foreach (var pr in Process.GetProcesses())
                        {
                            try { if (ProcInfo.Matches(app, pr.ProcessName + ".exe", "")) { appName = pr.ProcessName + ".exe"; appPath = ProcInfo.ImagePath(pr.Id); break; } }
                            catch { }
                            finally { pr.Dispose(); }
                        }
                    if (app != "" && !appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !app.Contains("\\")) appName += ".exe";

                    if (dom != "")
                    {
                        var ips = resolved.Count > 0 ? resolved : new List<IPAddress> { null };
                        var kinds = new List<KeyValuePair<RouteKind, string>>();
                        foreach (var ip in ips) { string w; var k = f.Decide(app == "" ? "" : appName, appPath, ip, dom, out w); kinds.Add(new KeyValuePair<RouteKind, string>(k, w)); }
                        var main = kinds.GroupBy(k => k.Key).OrderByDescending(g => g.Count()).First();
                        string who = app != "" ? appName : Tx("приложение не из списков");
                        verdict = dom + "  →  " + RouteFacts.Name(main.Key) + (main.Key == RouteKind.Dpi ? Tx("  (мимо VPN, через winws)") : main.Key == RouteKind.Direct ? Tx("  (без VPN и без DPI)") : "");
                        vc = RouteFacts.ColorOf(main.Key);
                        why = who + ": " + main.First().Value;
                        if (resolved.Count == 0) why += "\r\n" + Tx("Домен не резолвится — IP неизвестны");
                        else
                        {
                            int diff = kinds.Count(k => k.Key != main.Key);
                            why += "\r\nIP: " + string.Join(", ", resolved.Take(4).Select(a => a.ToString())) + (resolved.Count > 4 ? " …" : "");
                            if (diff > 0) why += "\r\n" + diff + Tx(" из ") + kinds.Count + Tx(" IP пойдут иначе: ") + string.Join("; ", kinds.Where(k => k.Key != main.Key).Select(k => RouteFacts.Name(k.Key) + " — " + k.Value).Distinct());
                        }
                        if (main.Key == RouteKind.Vpn && f.Wg && app != "" && AppLists.Domains(AppLists.DpiApps).Any(d => ProcInfo.Matches(d.Key, appName, appPath) && RouteFacts.HostIn(dom, DomainRoutes.Expand(d.Value))))
                            why += "\r\n" + Tx("Домен уже в «Через DPI», но его новые IP ещё не исключены — исключатся при перезапуске туннеля (раз в 10 мин или переподключение)");
                    }
                    else if (app != "")
                    {
                        string w; var k = f.Decide(appName, appPath, null, null, out w);
                        verdict = appName + "  →  " + RouteFacts.Name(k); vc = RouteFacts.ColorOf(k); why = w;
                    }

                    // живые TCP-соединения (их ошибка не мешает вердикту)
                    try { rows = Live(f, app, dom, family, hostOf); } catch { }
                }
                catch (Exception ex) { verdict = Tx("Ошибка проверки"); why = ex.Message; vc = Theme.Red; }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        busy = false;
                        if (IsDisposed) return;
                        if (verdict != null) { lVerdict.Text = verdict; lVerdict.ForeColor = vc; lWhy.Text = why ?? ""; }
                        else if (app == "" && dom == "") { lWhy.Text = Tx("Ниже — все соединения всех приложений"); }
                        Fill(rows, app, dom);
                    }));
                }
                catch { busy = false; }
            });
        }

        List<Row> Live(RouteFacts f, string app, string dom, List<string> family, Func<string, string> hostOf)
        {
            var rows = new List<Row>();
            var names = new Dictionary<int, Tuple<string, string>>();
            foreach (var c in ProcInfo.Connections())
            {
                if (IPAddress.IsLoopback(c.Item2)) continue;
                Tuple<string, string> np;
                if (!names.TryGetValue(c.Item1, out np))
                {
                    string n = "", pth = "";
                    try { using (var p = Process.GetProcessById(c.Item1)) n = p.ProcessName + ".exe"; } catch { }
                    try { pth = ProcInfo.ImagePath(c.Item1); } catch { }
                    names[c.Item1] = np = Tuple.Create(n, pth);
                }
                if (app != "" && !ProcInfo.Matches(app, np.Item1, np.Item2)) continue;
                string ipS = c.Item2.IsIPv4MappedToIPv6 ? c.Item2.MapToIPv4().ToString() : c.Item2.ToString();
                string host = hostOf(ipS);
                if (dom != "" && !(RouteFacts.HostIn(host, family) || resolved.Any(a => a.ToString() == ipS))) continue;
                string rw; var rk = f.Decide(np.Item1, np.Item2, c.Item2, host, out rw);
                rows.Add(new Row { App = np.Item1, Host = host ?? "", Ip = ipS, Kind = rk, Why = rw });
            }
            rows = rows.GroupBy(r => r.App + "|" + r.Ip).Select(g => g.First()).OrderBy(r => r.App, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Host == "").ThenBy(r => r.Host).Take(400).ToList();
            return rows;
        }

        void Fill(List<Row> rows, string app, string dom)
        {
            int first = grid.FirstDisplayedScrollingRowIndex;
            grid.SuspendLayout(); grid.Rows.Clear();
            foreach (var r in rows)
            {
                int i = grid.Rows.Add(r.App, r.Host == "" ? "—" : r.Host, r.Ip, RouteFacts.Name(r.Kind), r.Why);
                grid.Rows[i].Cells[3].Style.ForeColor = RouteFacts.ColorOf(r.Kind);
            }
            if (first >= 0 && first < grid.Rows.Count) try { grid.FirstDisplayedScrollingRowIndex = first; } catch { }
            grid.ClearSelection(); grid.ResumeLayout();
            var by = rows.GroupBy(r => r.Kind).Select(g => RouteFacts.Name(g.Key) + " " + g.Count());
            lConns.Text = Tx("Соединения сейчас") + (rows.Count == 0 ? Tx(": нет") + (dom != "" ? Tx(" (откройте сайт — появятся)") : "") : ": " + string.Join(" · ", by));
        }
    }
}
