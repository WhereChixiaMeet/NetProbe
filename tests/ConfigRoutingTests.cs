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
            st.TunBlockIpv6 = true; st.DnsViaTunnel = true;
            var standard = Build(st, l, "standard-lists.json");
            Check(Decide(standard, "example.com") == "proxy", "Standard Include sends Brave through VPN");
            Check(Decide(standard, "example.com", "chrome.exe") == "direct", "Standard Exclude sends Chrome directly");
            Check(Decide(standard, "other.com", "other.exe") == "direct", "Unlisted apps keep standard Direct fallback");
            Check(Decide(standard, "yandex.ru") == "direct", "Domain exclusions remain effective");
            Check(Decide(Build(st, TunLists.FromLists(new string[0], new string[0]), "standard-empty.json"), "example.com") == "proxy", "Empty Include retains original all-traffic VPN behavior");
            Check(typeof(Settings).GetField("ConfigRulesEnabled") == null && typeof(Settings).GetField("ConfigRulesJson") == null, "Removed editor settings cannot activate a second policy");
            st.Save(); File.AppendAllText(Path.Combine(Settings.Dir, "settings.ini"), "\nConfigRulesEnabled=1\nConfigRulesDefault=block\nConfigRulesJson=invalid old table\n");
            var migrated = Settings.Load(); migrated.XrayDir = st.XrayDir;
            Check(Decide(Build(migrated, l, "standard-migrated.json"), "example.com") == "proxy", "Old enabled editor settings are ignored on upgrade");
            Check(GeoData.Read(st, true, "category-ru").Count > 0 && GeoData.Read(st, false, "ru").Count > 0, "Standard RU geodata remains available to route inspection");
            string routeReason;
            Check(Snapshot(standard).Decide("brave.exe", @"C:\Apps\brave.exe", IPAddress.Parse("203.0.113.1"), null, out routeReason) == "proxy", "3.4.5 fix preserved: live app connection without a known domain is not Unknown");
            Check(Snapshot(standard).Decide("brave.exe", @"C:\Apps\brave.exe", null, null, out routeReason) == "proxy", "3.4.5 fix preserved: app-only route check is not Unknown");
            Check(Snapshot(standard).Decide("", "", IPAddress.Parse("203.0.113.1"), "example.com", out routeReason) == "direct", "3.4.5 fix preserved: domain-only check uses an app outside lists");
            var plain = new ServerEntry { Protocol = "vless", Address = "example.invalid", Port = 443, UserId = "00000000-0000-0000-0000-000000000000", Security = "none", Network = "tcp", Method = "none" };
            string authenticatedTunnel = XrayConfig.Build(Tun(st), plain);
            Check(authenticatedTunnel.Contains("\"auth\":\"password\"") && authenticatedTunnel.Contains(TunnelAuth.Pass) && !authenticatedTunnel.Contains("\"protocol\":\"http\""), "3.4.5 fix preserved: Configs SOCKS is authenticated and has no HTTP inbound");
            Check(ConfigRouting.Json().Serialize(Obj(Arr(standard["outbounds"])[0])).Contains(TunnelAuth.Pass), "3.4.5 fix preserved: sing-box uses the matching SOCKS credentials");
            var clash = Obj(Obj(standard["experimental"])["clash_api"]);
            Check((string)clash["external_controller"] == "127.0.0.1:" + (Tun(st).SocksPort + 1) && (string)clash["secret"] == TunnelAuth.Pass, "3.4.7: passive VPN check uses a local Clash API with the per-run secret");
            Check(st.ProbeUrls().Count > 1 && st.ProbeUrls()[0] == st.ProbeUrl, "3.4.7: probe rotation starts with the trace URL");
            Check(TunnelProto.Missing(plain) == null, "VLESS without TLS remains permitted as requested");
            plain.Security = "tls"; plain.Insecure = true;
            Check(ConfigRouting.Json().Serialize(plain.Outbound("proxy")).Contains("\"allowInsecure\":true"), "allowInsecure remains unchanged as requested");
            plain.Insecure = false; st.BypassRu = false;
            File.WriteAllText("sample-xray.json", XrayConfig.Build(Tun(st), plain));
            // Actual runtime inspector, including the legacy Xray RU routing stage.
            st.BypassRu = true;
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
            Console.WriteLine("TOTAL: " + checks + " checks. Real VPN engines were not started; one offline fake process was used for lifecycle checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
