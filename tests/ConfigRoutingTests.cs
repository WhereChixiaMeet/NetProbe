using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;
using NetProbe;

static class ConfigRoutingTests
{
    static int checks;
    static BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    static void Check(bool v, string label) { if (!v) throw new Exception(label); checks++; Console.WriteLine("PASS: " + label); }
    static Dictionary<string, object> Obj(object o) { return (Dictionary<string, object>)o; }
    static object[] Arr(object o) { return ((IEnumerable)o).Cast<object>().ToArray(); }
    static Settings Tun(Settings st) { return (Settings)typeof(XrayTunnel).GetMethod("TunSettings", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { st }); }
    static Dictionary<string, object> Build(Settings st, TunLists l, string filename)
    {
        string json = SingBoxConfig.Build(Tun(st), l); File.WriteAllText(filename, json); return Obj(ConfigRouting.Json().DeserializeObject(json));
    }
    static RoutingSnapshot Snapshot(Dictionary<string, object> config)
    {
        var route = Obj(config["route"]);
        return new RoutingSnapshot { Final = (string)route["final"], Routes = Arr(route["rules"]).Select(Obj).ToList() };
    }
    static string Decide(Dictionary<string, object> c, string host, string app = "brave.exe", string ip = "203.0.113.1")
    {
        string why; return Snapshot(c).Decide(app, @"C:\Apps\" + app, IPAddress.Parse(ip), host, out why);
    }
    static void Invalid(Action a, string label) { try { a(); } catch { Check(true, label); return; } throw new Exception(label); }
    [STAThread] static int Main(string[] args)
    {
        try
        {
            Logger.ToFile = false; Application.EnableVisualStyles(); Theme.Apply(ThemeKind.Dark); Theme.MakeFonts();
            var st = new Settings { XrayDir = args[0], SingBoxDir = args[1], DnsViaTunnel = true, BypassRu = true };
            var l = TunLists.FromLists(new[] { "brave.exe" }, new[] { "chrome.exe", "yandex.ru" });
            var legacy = Build(st, l, "legacy-v4.json");
            Check(Arr(Obj(Arr(legacy["inbounds"])[0])["address"]).Length == 1, "IPv4-only TUN has no IPv6 address");
            Check(Decide(legacy, "yandex.ru", "chrome.exe", "2001:db8::1") == "block", "IPv6 packets reaching TUN are rejected before Direct exceptions");
            var dns = Obj(legacy["dns"]);
            var dr = Arr(dns["rules"]).Select(Obj).ToList();
            Check(dr.Any(r => r.ContainsKey("query_type") && (string)r["rcode"] == "NOERROR"), "AAAA returns an empty successful response in IPv4-only mode");
            Check((string)dns["final"] == "remote", "VPN DNS fallback covers svchost queries without app attribution");
            Check(dr.Any(r => r.ContainsKey("domain_suffix") && Arr(r["domain_suffix"]).Contains("yandex.ru") && (string)r["server"] == "local"), "Excluded domain uses local DNS before Include rules");
            st.DnsViaTunnel = false; var local = Build(st, l, "legacy-local-dns.json");
            Check((string)Obj(local["dns"])["final"] == "local", "DNS through VPN off changes generated config");
            st.TunBlockIpv6 = false; var dual = Build(st, l, "legacy-dual-stack.json");
            Check(Arr(Obj(Arr(dual["inbounds"])[0])["address"]).Length == 2 && (string)Obj(dual["dns"])["strategy"] == "prefer_ipv4", "Optional dual-stack has consistent address / resolver settings");
            st.TunBlockIpv6 = true; st.ConfigRulesEnabled = true; st.DnsViaTunnel = true;
            var rules = ConfigRouting.Preset(l); st.ConfigRulesJson = ConfigRouting.Json().Serialize(rules);
            var custom = Build(st, l, "ordered-ru.json");
            Check(!Tun(st).BypassRu && !Tun(st).BlockAds, "Ordered editor disables second-stage Xray overrides");
            Check(Decide(custom, "yandex.ru") == "direct", "RU domain takes priority over included Brave");
            Check(Decide(custom, "example.com") == "proxy", "Non-RU Brave traffic goes to VPN");
            Check(Decide(custom, "example.com", "chrome.exe") == "direct", "Excluded Chrome stays Direct");
            Check(Decide(custom, "example.com", "other.exe") == "direct", "Unmatched traffic uses explicit Direct fallback");
            Check(Decide(custom, "example.com", "brave.exe", "192.168.1.2") == "direct", "Service local-network exception stays ahead of editable rules");
            Check(Decide(custom, "example.com", "netprobe-vpn.exe") == "proxy", "VPN health probe cannot accidentally test Direct fallback");
            var strictLists = TunLists.FromLists(new[] { "strict.exe", "netprobe-vpn.exe" }, new string[0]); strictLists.StrictOnly = true;
            var strictConfig = Build(st, strictLists, "strict-recovery.json");
            Check(Decide(strictConfig, "example.com", "brave.exe") == "direct" && Decide(strictConfig, "example.com", "strict.exe") == "proxy", "Strict-only recovery does not enable ordinary editor VPN rules");
            var bootstrap = Tun(st); typeof(Settings).GetField("TunServerDomain", Hidden).SetValue(bootstrap, "vpn.example.invalid");
            var bootstrapConfig = Obj(ConfigRouting.Json().DeserializeObject(SingBoxConfig.Build(bootstrap, l)));
            var bootstrapRules = Arr(Obj(bootstrapConfig["dns"])["rules"]).Select(Obj).ToList();
            Check(bootstrapRules[1].ContainsKey("domain") && Arr(bootstrapRules[1]["domain"]).Contains("vpn.example.invalid") && (string)bootstrapRules[1]["server"] == "local", "VPN server DNS bootstraps locally before remote DNS rules");
            var ruFirst = rules[0]; var appFirst = rules.First(r => r.Name == "Include → VPN"); rules.Remove(appFirst); rules.Insert(0, appFirst);
            st.ConfigRulesJson = ConfigRouting.Json().Serialize(rules); var reordered = Build(st, l, "ordered-app-first.json");
            Check(Decide(reordered, "yandex.ru") == "proxy", "Moving process rule ahead of RU actually changes the selected route");
            appFirst.Enabled = false; st.ConfigRulesJson = ConfigRouting.Json().Serialize(rules);
            Check(Decide(Build(st, l, "ordered-disabled.json"), "yandex.ru") == "direct", "Disabled rule is ignored");
            rules = new List<ConfigRouteRule> { new ConfigRouteRule { Name = "Block domain", Values = "domain:example.com", Outbound = "block" } };
            st.ConfigRulesJson = ConfigRouting.Json().Serialize(rules); st.ConfigRulesDefault = "block";
            var blocked = Build(st, l, "ordered-block.json");
            Check(Decide(blocked, "www.example.com") == "block" && Decide(blocked, "other.com") == "block", "Domain blocking and Block fallback compile to reject actions");
            st.ConfigRulesDefault = "direct"; st.ConfigRulesJson = "[]";
            Check(Decide(Build(st, TunLists.FromLists(new string[0], new string[0]), "empty-editor.json"), "example.com") == "direct", "Empty enabled editor does not silently switch all traffic to VPN");
            var portRule = new ConfigRouteRule { Kind = "domain", Values = "example.com", Outbound = "proxy", Ports = "443", Network = "tcp" };
            st.ConfigRulesJson = ConfigRouting.Json().Serialize(new[] { portRule });
            Check(Decide(Build(st, l, "ordered-port.json"), "example.com") == "unknown", "Route inspector does not invent port / network information");
            string w;
            Check(Snapshot(custom).Decide("brave.exe", @"C:\Apps\brave.exe", IPAddress.Parse("203.0.113.1"), null, out w) == "proxy", "Live connection without a known domain is not Unknown");
            Check(Snapshot(custom).Decide("brave.exe", @"C:\Apps\brave.exe", null, null, out w) == "proxy", "App-only check is not Unknown");
            Check(Snapshot(custom).Decide("", "", IPAddress.Parse("203.0.113.1"), "example.com", out w) == "direct", "Domain-only check means an app outside the lists");
            var tunXray = ConfigRouting.Json().Serialize(ConfigRouting.Json().DeserializeObject(XrayConfig.Build(Tun(st), new ServerEntry { Protocol = "vless", Address = "example.invalid", Port = 443, UserId = "00000000-0000-0000-0000-000000000000", Security = "none", Network = "tcp", Method = "none" })));
            Check(tunXray.Contains("\"auth\":\"password\"") && tunXray.Contains(TunnelAuth.Pass) && !tunXray.Contains("\"protocol\":\"http\""), "Router tunnel SOCKS port needs a password and has no HTTP inbound");
            Check(ConfigRouting.Json().Serialize(Obj(Arr(custom["outbounds"])[0])).Contains(TunnelAuth.Pass), "sing-box authenticates to the tunnel SOCKS port");
            Invalid(() => ConfigRouting.Matcher(new ConfigRouteRule { Kind = "ip", Values = "192.0.2.0/99" }, st), "Invalid CIDR is rejected");
            Invalid(() => ConfigRouting.Matcher(new ConfigRouteRule { Values = "example.com", Ports = "65536" }, st), "Invalid port is rejected");
            Invalid(() => ConfigRouting.Matcher(new ConfigRouteRule { Values = "geosite:missing-category-xyz" }, st), "Missing geodata category fails clearly");
            Invalid(() => ConfigRouting.Matcher(new ConfigRouteRule { Values = "geosite:../../file" }, st), "Geodata category cannot select arbitrary files");
            Check(GeoData.Read(st, true, "category-ru").Count > 0 && GeoData.Read(st, false, "ru").Count > 0, "Installed real Xray geodata is read without downloads");
            var plain = new ServerEntry { Protocol = "vless", Address = "example.invalid", Port = 443, UserId = "00000000-0000-0000-0000-000000000000", Security = "none", Network = "tcp", Method = "none" };
            Check(TunnelProto.Missing(plain) == null, "VLESS without TLS remains permitted as requested");
            plain.Security = "tls"; plain.Insecure = true;
            Check(ConfigRouting.Json().Serialize(plain.Outbound("proxy")).Contains("\"allowInsecure\":true"), "allowInsecure remains unchanged as requested");
            plain.Insecure = false; st.BypassRu = false; st.ConfigRulesJson = "[]";
            File.WriteAllText("sample-xray.json", XrayConfig.Build(Tun(st), plain));
            // Actual runtime inspector, including the legacy Xray RU routing stage.
            st.ConfigRulesEnabled = false; st.BypassRu = true;
            Directory.CreateDirectory(Settings.Sub("runtime"));
            File.WriteAllText(Path.Combine(Settings.Sub("runtime"), "singbox-tunnel.json"), SingBoxConfig.Build(Tun(st), TunLists.FromLists(new[] { "brave.exe" }, new string[0])));
            File.WriteAllText(Path.Combine(Settings.Sub("runtime"), "xray-tunnel.json"), XrayConfig.Build(Tun(st), plain));
            string reason; var snapshot = RoutingSnapshot.Load(st);
            Check(snapshot.Error == null && snapshot.Decide("brave.exe", @"C:\Apps\brave.exe", IPAddress.Parse("203.0.113.1"), "yandex.ru", out reason) == "direct", "Runtime inspector includes the legacy Xray RU bypass stage");
            // Process-lifecycle integration with an offline double, never the real VPN core.
            var fake = new Settings { TunMode = true, SingBoxDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fake-core") };
            var manager = (XrayManager)FormatterServices.GetUninitializedObject(typeof(XrayManager));
            var sb = new SingBoxProcess(); typeof(XrayManager).GetField("sb", Hidden).SetValue(manager, sb);
            manager.GetSettings = () => fake; manager.GetTunLists = () => l;
            var ensure = typeof(XrayManager).GetMethod("EnsureTun", Hidden);
            try
            {
                ensure.Invoke(manager, null); Check(sb.Running, "Offline fake core starts for lifecycle test");
                var pField = typeof(SingBoxProcess).GetField("p", Hidden); int pid = ((System.Diagnostics.Process)pField.GetValue(sb)).Id;
                ensure.Invoke(manager, null); Check(((System.Diagnostics.Process)pField.GetValue(sb)).Id == pid, "Unchanged TUN config preserves its process");
                fake.SocksPort++; ensure.Invoke(manager, null);
                Check(((System.Diagnostics.Process)pField.GetValue(sb)).Id != pid, "Changed SOCKS port rebuilds the running TUN");
                var saved = Obj(ConfigRouting.Json().DeserializeObject(File.ReadAllText(Path.Combine(Settings.Sub("runtime"), "singbox.json"))));
                Check(Convert.ToInt32(Obj(Arr(saved["outbounds"])[0])["server_port"]) == fake.SocksPort, "Restarted TUN uses the new SOCKS port");
            }
            finally { sb.Stop(); }
            // Render just the editor; MainForm startup and networking are never invoked.
            st.ConfigRulesEnabled = true; st.ConfigRulesJson = ConfigRouting.Json().Serialize(ConfigRouting.Preset(l));
            foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
            {
                Theme.Apply(theme); Theme.MakeFonts();
                using (var form = new RoutingRulesDialog(st, () => true, () => l))
                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    // Mark logical visibility only after creating the hidden HWND; never Show the form.
                    var handle = form.Handle;
                    typeof(Control).GetMethod("SetState", Hidden).Invoke(form, new object[] { 2, true });
                    typeof(Control).GetMethod("CreateControl", Hidden, null, new[] { typeof(bool) }, null).Invoke(form, new object[] { true });
                    form.PerformLayout(); form.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size)); bmp.Save("routing-editor-" + theme + ".png");
                    var colors = new HashSet<int>(); for (int x = 10; x < bmp.Width; x += 5) for (int y = 40; y < bmp.Height; y += 5) colors.Add(bmp.GetPixel(x, y).ToArgb());
                    Check(colors.Count > 10, "Editor renders controls in " + theme + " theme");
                }
            }
            bool editAllowed = true;
            using (var form = new RoutingRulesDialog(st, () => editAllowed, () => l))
            {
                var rows = (List<ConfigRouteRule>)typeof(RoutingRulesDialog).GetField("rules", Hidden).GetValue(form);
                typeof(RoutingRulesDialog).GetMethod("Fill", Hidden).Invoke(form, new object[] { 1 });
                var initialFirst = rows[0]; var initialSecond = rows[1];
                typeof(RoutingRulesDialog).GetMethod("MoveRule", Hidden).Invoke(form, new object[] { -1 });
                Check(rows[0] == initialSecond && rows[1] == initialFirst, "Editor Up button changes the draft order");
                editAllowed = false;
                typeof(RoutingRulesDialog).GetMethod("MoveRule", Hidden).Invoke(form, new object[] { 1 });
                Check(rows[0] == initialSecond, "Editor refuses reorder when VPN state becomes locked");
                string before = st.ConfigRulesJson;
                typeof(RoutingRulesDialog).GetMethod("Save", Hidden).Invoke(form, null);
                Check(st.ConfigRulesJson == before, "Editor refuses save while locked");
                editAllowed = true; rows[0].Name = "Saved \\ path\nsecond line";
                typeof(RoutingRulesDialog).GetMethod("Save", Hidden).Invoke(form, null);
                var reloaded = Settings.Load();
                Check(reloaded.ConfigRulesEnabled && ConfigRouting.Read(reloaded.ConfigRulesJson)[0].Name == rows[0].Name, "Rule order and escaped text persist through settings save/load");
            }
            Console.WriteLine("TOTAL: " + checks + " checks. Real VPN engines were not started; one offline fake process was used for lifecycle checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
