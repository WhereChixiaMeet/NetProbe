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
    static void FakeCore(TunLists l)
    {
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
    }
    static readonly FieldInfo Override = typeof(Settings).GetField("RoutingOverride", BindingFlags.NonPublic | BindingFlags.Instance);
    static void Use(Settings st, RoutingConfig c) { Override.SetValue(st, c); }
    static List<Dictionary<string, object>> Rule(Settings st, RouteRule r, List<object> dns = null)
    {
        var routes = new List<object>(); ConfigRouting.AddRule(routes, dns ?? new List<object>(), r, st); return routes.Select(Obj).ToList();
    }
    static string RuIp(Settings st)
    {
        var cidr = (string)Arr(Obj(GeoData.Read(st, false, "ru")[0])["ip_cidr"]).First(x => !((string)x).Contains(":"));
        var b = IPAddress.Parse(cidr.Split('/')[0]).GetAddressBytes(); b[3] |= 1; return new IPAddress(b).ToString();
    }
    static void Shot(Form form, string name)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT) { form.Dispose(); Console.WriteLine("SKIP: render " + name + " (needs Windows)"); return; }
        using (form)
        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            // Mark logical visibility only after creating the hidden HWND; never Show the form.
            var handle = form.Handle;
            typeof(Control).GetMethod("SetState", Hidden).Invoke(form, new object[] { 2, true });
            typeof(Control).GetMethod("CreateControl", Hidden, null, new[] { typeof(bool) }, null).Invoke(form, new object[] { true });
            form.PerformLayout(); form.DrawToBitmap(bmp, new Rectangle(Point.Empty, bmp.Size)); bmp.Save(name + ".png");
            var colors = new HashSet<int>(); for (int x = 10; x < bmp.Width; x += 5) for (int y = 40; y < bmp.Height; y += 5) colors.Add(bmp.GetPixel(x, y).ToArgb());
            Check(colors.Count > 10, "Window renders controls: " + name);
        }
    }
    static void V2rayNSemantics(Settings st, TunLists l)
    {
        var dns = new List<object>();
        var kw = Rule(st, new RouteRule { Domain = { "example" }, OutboundTag = "direct" }, dns);
        Check(kw.Count == 1 && Arr(kw[0]["domain_keyword"]).Contains("example") && (string)kw[0]["outbound"] == "direct", "Plain domain value is a keyword, as in v2ray");
        Check(dns.Count == 1 && (string)Obj(dns[0])["server"] == "local", "Direct domain rule also picks the local DNS server");
        var three = Rule(st, new RouteRule { Domain = { "domain:a.com", "full:b.com", "regexp:^c\\." }, Ip = { "1.2.3.4" }, Process = Environment.OSVersion.Platform == PlatformID.Win32NT ? new List<string> { "app", @"C:\Apps\x.exe" } : new List<string> { "app" }, Port = "443,8000-9000", Network = "tcp", OutboundTag = "proxy" });
        Check(three.Count == 3 && three.All(r => Arr(r["port"]).Contains(443) && Arr(r["port_range"]).Contains("8000:9000") && (string)r["network"] == "tcp"), "Domain / IP / Process become separate rules sharing port and network");
        Check(three[0].ContainsKey("domain_suffix") && three[0].ContainsKey("domain") && three[0].ContainsKey("domain_regex"), "Domain prefixes map to sing-box fields");
        Check(Arr(three[1]["ip_cidr"]).Contains("1.2.3.4/32") && Arr(three[2]["process_name"]).Contains("app.exe"), "Bare IP gets /32 and bare app name gets .exe");
        Check(Rule(st, new RouteRule { Port = "0-65535", OutboundTag = "direct" }).Single().ContainsKey("port_range"), "Port-only catch-all rule is kept");
        Check(Rule(st, new RouteRule { Protocol = { "bittorrent" }, OutboundTag = "direct" }).Single().ContainsKey("protocol"), "Sniffed protocol rule");
        Check(Rule(st, new RouteRule { Domain = { "x.com" }, InboundTag = { "socks" } }).Count == 0, "Rule for a non-TUN inbound never matches");
        var neg = Rule(st, new RouteRule { Ip = { "!geoip:ru" }, OutboundTag = "proxy" }).Single();
        Check((string)neg["type"] == "logical" && Arr(neg["rules"]).Select(Obj).Any(r => r.ContainsKey("invert")), "!geoip:ru compiles to an inverted IP rule");
        var negSnap = new RoutingSnapshot { Final = "direct", Routes = new List<Dictionary<string, object>> { neg } };
        string why;
        Check(negSnap.Decide("a.exe", null, IPAddress.Parse("203.0.113.9"), null, out why) == "proxy" && negSnap.Decide("a.exe", null, IPAddress.Parse(RuIp(st)), null, out why) == "direct", "Inverted geoip matches everything except RU");
        Check(Rule(st, new RouteRule { Ip = { "geoip:private" } }).Single().ContainsKey("ip_is_private"), "geoip:private maps to ip_is_private");
        var dnsOnly = new List<object>();
        Check(Rule(st, new RouteRule { Domain = { "domain:a.com" }, RuleType = 2 }, dnsOnly).Count == 0 && dnsOnly.Count == 1, "DNS-type rule only adds a DNS rule");
        var routeOnly = new List<object>();
        Check(Rule(st, new RouteRule { Domain = { "domain:a.com" }, RuleType = 1 }, routeOnly).Count == 1 && routeOnly.Count == 0, "Routing-type rule adds no DNS rule");
        var portDns = new List<object>(); Rule(st, new RouteRule { Domain = { "domain:a.com" }, Port = "443" }, portDns);
        Check(portDns.Count == 0, "Port-limited rule is not copied into DNS");
        // domain strategy
        var s = new RouteSet { Rules = { new RouteRule { Ip = { "geoip:ru" }, OutboundTag = "direct" }, new RouteRule { Domain = { "domain:x.com" }, OutboundTag = "block" } } };
        Func<string, List<Dictionary<string, object>>> strat = k => { var r = new List<object>(); ConfigRouting.AddSet(r, new List<object>(), new RoutingConfig { DomainStrategy = k }, s, st, true); return r.Select(Obj).ToList(); };
        Check(strat("AsIs").Count == 2 && !strat("AsIs").Any(r => r.ContainsKey("action") && (string)r["action"] == "resolve"), "AsIs adds no resolve");
        var od = strat("IPOnDemand"); Check((string)od[0]["action"] == "resolve" && od.Count == 3, "IPOnDemand resolves before the rules");
        var nm = strat("IPIfNonMatch"); Check(nm.Count == 4 && (string)nm[2]["action"] == "resolve" && nm[3].ContainsKey("ip_cidr"), "IPIfNonMatch resolves after a miss and repeats the IP rules");
        s.DomainStrategy = "AsIs"; Check(strat("IPOnDemand").Count == 2, "A set's own strategy overrides the global one");
        // v2rayN JSON: import, export, migration
        string rf = Environment.GetEnvironmentVariable("RF_DIR");
        if (!string.IsNullOrEmpty(rf))
            foreach (var f in new[] { "all.json", "all_except_ru.json", "only_blocked.json" })
            {
                var imported = RoutingStore.ParseRules(File.ReadAllText(Path.Combine(rf, f)));
                var again = RoutingStore.ParseRules(RoutingStore.ExportRules(imported));
                Check(imported.Count > 3 && RoutingStore.ExportRules(again) == RoutingStore.ExportRules(imported), "runetfreedom " + f + " imports and round-trips");
                ConfigRouting.Validate(new RoutingConfig(), new RouteSet { Rules = imported }, st);
                Check(true, "runetfreedom " + f + " compiles with the RU geodata");
            }
        Check(RoutingStore.ParseRules("{\"routing\":{\"rules\":[{\"type\":\"field\",\"outboundTag\":\"direct\",\"domain\":[\"geosite:category-ru\"]}]}}").Single().Domain[0] == "geosite:category-ru", "Xray routing object imports");
        Invalid(() => RoutingStore.ParseRules("not json"), "Garbage import is rejected");
        var old = new List<ConfigRouteRule> { new ConfigRouteRule { Name = "Old", Values = "example.com", Outbound = "proxy" }, new ConfigRouteRule { Kind = "process", Values = "a.exe", Outbound = "direct" } };
        var mig = RoutingStore.Migrate(old, "direct");
        Check(mig.Rules[0].Domain[0] == "domain:example.com" && mig.Rules[1].Process[0] == "a.exe" && mig.Rules.Last().Port == "0-65535" && mig.Rules.Last().OutboundTag == "direct", "3.4.3 table migrates with its suffix semantics and Direct fallback");
        var all = RoutingPresets.Builtin(st);
        foreach (var b in all) { ConfigRouting.Validate(new RoutingConfig(), b, st); Check(true, "Built-in set compiles: " + b.Remarks); }
        Check(all[0].Rules.Any(r => r.Domain.Contains("geosite:ru-available-only-inside")) == GeoData.Has(st, true, "ru-available-only-inside"), "RU detect uses only installed categories");
        var cfgj = RoutingStore.FromJson(RoutingStore.ToJson(new RoutingConfig { Sets = all, ActiveId = all[1].Id, DomainStrategy = "IPOnDemand" }));
        Check(cfgj.Active.Id == all[1].Id && cfgj.Sets.Count == all.Count && cfgj.DomainStrategy == "IPOnDemand", "routing.json round-trips");
        // RU sites detect
        string rep = RuDetect.Report(st, "yandex.ru", new IPAddress[0]);
        Check(rep.Contains("✓ geosite:category-ru") && rep.Contains("✓ .ru"), "yandex.ru is detected as a RU site: " + rep);
        string g = RuDetect.Report(st, "google.com", new IPAddress[0]);
        Check(g.Contains("✗ geosite:category-ru"), "google.com is not a RU site: " + g);
        var line = RoutingSettingsDialog.Describe(st, new RoutingConfig { Sets = all, ActiveId = all[0].Id }, all[0], RuIp(st));
        Check(line.Contains("DIRECT") && line.Contains("✓ geoip:ru"), "Detect: RU address → direct in the RU set: " + line);
    }
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
            var set = new RouteSet { Remarks = "Test" };
            set.Rules.Add(RoutingPresets.RuDirect(st));
            set.Rules.Add(new RouteRule { Remarks = "Exclude", OutboundTag = "direct", Process = { "chrome" }, Domain = { "domain:yandex.ru" } });
            set.Rules.Add(new RouteRule { Remarks = "Include", OutboundTag = "proxy", Process = { "brave.exe" } });
            set.Rules.Add(new RouteRule { Remarks = "Rest", OutboundTag = "direct", Port = "0-65535" });
            var rc = new RoutingConfig { Sets = { set }, ActiveId = set.Id };
            Use(st, rc);
            var custom = Build(st, l, "ordered-ru.json");
            Check(!Tun(st).BypassRu && !Tun(st).BlockAds, "Ordered editor disables second-stage Xray overrides");
            Check(Decide(custom, "yandex.ru") == "direct", "RU domain takes priority over included Brave");
            Check(Decide(custom, "example.com") == "proxy", "Non-RU Brave traffic goes to VPN");
            Check(Decide(custom, "example.com", "chrome.exe") == "direct", "Excluded Chrome stays Direct (.exe added to a bare name, as in v2rayN)");
            Check(Decide(custom, "example.com", "other.exe") == "direct", "Catch-all port rule sends unmatched traffic Direct");
            Check(Decide(custom, "example.com", "brave.exe", "192.168.1.2") == "direct", "Service local-network exception stays ahead of editable rules");
            Check(Decide(custom, "example.com", "netprobe-vpn.exe") == "proxy", "VPN health probe cannot accidentally test Direct fallback");
            Check(Decide(custom, "example.com", "brave.exe", RuIp(st)) == "direct", "geoip:ru of the RU rule catches a non-RU name on a RU address");
            var strictLists = TunLists.FromLists(new[] { "strict.exe", "netprobe-vpn.exe" }, new string[0]); strictLists.StrictOnly = true;
            var strictConfig = Build(st, strictLists, "strict-recovery.json");
            Check(Decide(strictConfig, "example.com", "brave.exe") == "direct" && Decide(strictConfig, "example.com", "strict.exe") == "proxy", "Strict-only recovery does not enable ordinary editor VPN rules");
            var bootstrap = Tun(st); typeof(Settings).GetField("TunServerDomain", Hidden).SetValue(bootstrap, "vpn.example.invalid");
            var bootstrapConfig = Obj(ConfigRouting.Json().DeserializeObject(SingBoxConfig.Build(bootstrap, l)));
            var bootstrapRules = Arr(Obj(bootstrapConfig["dns"])["rules"]).Select(Obj).ToList();
            Check(bootstrapRules[1].ContainsKey("domain") && Arr(bootstrapRules[1]["domain"]).Contains("vpn.example.invalid") && (string)bootstrapRules[1]["server"] == "local", "VPN server DNS bootstraps locally before remote DNS rules");
            var appFirst = set.Rules[2]; set.Rules.Remove(appFirst); set.Rules.Insert(0, appFirst);
            Check(Decide(Build(st, l, "ordered-app-first.json"), "yandex.ru") == "proxy", "Moving process rule ahead of RU actually changes the selected route");
            appFirst.Enabled = false;
            Check(Decide(Build(st, l, "ordered-disabled.json"), "yandex.ru") == "direct", "Disabled rule is ignored");
            var bset = new RouteSet { Rules = { new RouteRule { Domain = { "domain:example.com" }, OutboundTag = "block" }, new RouteRule { Port = "0-65535", OutboundTag = "block" } } };
            Use(st, new RoutingConfig { Sets = { bset }, ActiveId = bset.Id });
            var blocked = Build(st, l, "ordered-block.json");
            Check(Decide(blocked, "www.example.com") == "block" && Decide(blocked, "other.com") == "block", "Domain blocking and a catch-all block compile to reject actions");
            var empty = new RouteSet(); Use(st, new RoutingConfig { Sets = { empty }, ActiveId = empty.Id });
            Check(Decide(Build(st, TunLists.FromLists(new string[0], new string[0]), "empty-editor.json"), "example.com") == "proxy", "Empty set: unmatched traffic goes to the proxy, as in v2rayN");
            var pset = new RouteSet { Rules = { new RouteRule { Domain = { "domain:example.com" }, OutboundTag = "direct", Port = "443", Network = "tcp" } } };
            Use(st, new RoutingConfig { Sets = { pset }, ActiveId = pset.Id });
            Check(Decide(Build(st, l, "ordered-port.json"), "example.com") == "unknown", "Route inspector does not invent port / network information");
            Invalid(() => Rule(st, new RouteRule { Ip = { "192.0.2.0/99" } }), "Invalid CIDR is rejected");
            Invalid(() => Rule(st, new RouteRule { Domain = { "example.com" }, Port = "65536" }), "Invalid port is rejected");
            Invalid(() => Rule(st, new RouteRule { Domain = { "geosite:missing-category-xyz" } }), "Missing geodata category fails clearly");
            Invalid(() => Rule(st, new RouteRule { Domain = { "geosite:../../file" } }), "Geodata category cannot select arbitrary files");
            Invalid(() => Rule(st, new RouteRule { Domain = { "x" }, OutboundTag = "warp" }), "Unknown outboundTag is rejected");
            Invalid(() => Rule(st, new RouteRule { Protocol = { "smtp" } }), "Unknown sniff protocol is rejected");
            V2rayNSemantics(st, l);
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
            if (Environment.OSVersion.Platform == PlatformID.Win32NT) FakeCore(l);
            // Render the three windows; MainForm startup and networking are never invoked.
            st.ConfigRulesEnabled = true;
            var shown = RoutingStore.Load(st); RoutingStore.Save(shown);
            foreach (var theme in new[] { ThemeKind.Dark, ThemeKind.Light })
            {
                Theme.Apply(theme); Theme.MakeFonts();
                Shot(new RoutingSettingsDialog(st, () => true), "routing-sets-" + theme);
                Shot(new RuleSetDialog(st, shown, shown.Active, () => true), "routing-rules-" + theme);
                Shot((Form)Activator.CreateInstance(typeof(RuleDetailsDialog), Hidden | BindingFlags.Public, null, new object[] { st, shown.Active.Rules.First(r => r.Domain.Count > 0) }, null), "routing-rule-" + theme);
            }
            bool editAllowed = true;
            using (var form = new RuleSetDialog(st, shown, shown.Active, () => editAllowed))
            {
                var rows = (List<RouteRule>)typeof(RuleSetDialog).GetField("rules", Hidden).GetValue(form);
                var grid = (DataGridView)typeof(RuleSetDialog).GetField("grid", Hidden).GetValue(form);
                var initialFirst = rows[0]; var initialSecond = rows[1];
                grid.ClearSelection(); grid.Rows[1].Selected = true;
                typeof(RuleSetDialog).GetMethod("MoveRows", Hidden).Invoke(form, new object[] { -1 });
                Check(rows[0] == initialSecond && rows[1] == initialFirst, "Move up changes the draft order");
                grid.ClearSelection(); grid.Rows[0].Selected = true;
                typeof(RuleSetDialog).GetMethod("MoveRows", Hidden).Invoke(form, new object[] { int.MaxValue });
                Check(rows[rows.Count - 1] == initialSecond, "Move to bottom");
                editAllowed = false;
                typeof(RuleSetDialog).GetMethod("MoveRows", Hidden).Invoke(form, new object[] { int.MinValue });
                Check(rows[rows.Count - 1] == initialSecond, "Rule set refuses reorder when VPN state becomes locked");
            }
            using (var form = new RoutingSettingsDialog(st, () => editAllowed))
            {
                string before = File.ReadAllText(RoutingStore.FilePath);
                typeof(RoutingSettingsDialog).GetMethod("Save", Hidden).Invoke(form, null);
                Check(File.ReadAllText(RoutingStore.FilePath) == before, "Routing settings refuse save while locked");
                editAllowed = true;
                var cfg = (RoutingConfig)typeof(RoutingSettingsDialog).GetField("cfg", Hidden).GetValue(form);
                cfg.Active.Rules[0].Remarks = "Saved \\ path\nsecond line"; cfg.DomainStrategy = "IPIfNonMatch";
                typeof(RoutingSettingsDialog).GetMethod("Save", Hidden).Invoke(form, null);
                var reloaded = RoutingStore.Load(st);
                Check(Settings.Load().ConfigRulesEnabled && reloaded.Active.Rules[0].Remarks == cfg.Active.Rules[0].Remarks && reloaded.DomainStrategy == "IPIfNonMatch", "Rule sets, strategy and escaped text persist through save/load");
            }
            Console.WriteLine("TOTAL: " + checks + " checks. Real VPN engines were not started; one offline fake process was used for lifecycle checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
