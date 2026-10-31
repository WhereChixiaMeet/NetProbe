using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NetProbe
{
    // 3.4.3 rule format. Kept only to migrate old settings and to read runtime Xray rules (RoutingSnapshot).
    public sealed class ConfigRouteRule
    {
        public bool Enabled = true;
        public string Name = "New rule", Kind = "domain", Values = "", Outbound = "direct", Network = "", Ports = "";
    }

    // One rule, field for field the v2rayN RulesItem (same JSON, so rules can be copied between the two apps).
    // Domain / IP / Process are separate matchers (any of them), each combined with port / network / protocol (all of them).
    public sealed class RouteRule
    {
        public bool Enabled = true;
        public string Remarks = "", OutboundTag = "proxy", Port = "", Network = "";
        public int RuleType; // v2rayN ERuleType: 0 ALL (routing + DNS), 1 Routing, 2 DNS
        public List<string> Domain = new List<string>(), Ip = new List<string>(), Process = new List<string>(), Protocol = new List<string>(), InboundTag = new List<string>();
        public RouteRule Clone() { return RoutingStore.RuleFromDict(RoutingStore.RuleToDict(this)); }
        public bool HasMatch { get { return Domain.Count + Ip.Count + Process.Count > 0 || Port != "" || Network != "" || Protocol.Count > 0; } }
    }

    // A rule set (v2rayN RoutingItem): only the active set is used.
    public sealed class RouteSet
    {
        public string Id = Guid.NewGuid().ToString("N"), Remarks = "", Url = "", DomainStrategy = "";
        public int Sort;
        public List<RouteRule> Rules = new List<RouteRule>();
        public RouteSet Clone() { var s = RoutingStore.SetFromDict(RoutingStore.SetToDict(this)); s.Id = Id; return s; }
    }

    public sealed class RoutingConfig
    {
        public string DomainStrategy = "AsIs", ActiveId = "";
        public List<RouteSet> Sets = new List<RouteSet>();
        public RouteSet Active { get { return Sets.FirstOrDefault(s => s.Id == ActiveId) ?? Sets.OrderBy(s => s.Sort).FirstOrDefault(); } }
        public RoutingConfig Clone() { return RoutingStore.FromJson(RoutingStore.ToJson(this)); }
    }

    // config\routing.json: the rule sets. Created on first use from built-in sets and the 3.4.3 table.
    public static class RoutingStore
    {
        public const int MaxRules = 1000, MaxSets = 64;
        public static string FilePath { get { return Path.Combine(Settings.Dir, "routing.json"); } }

        public static RoutingConfig Load(Settings st)
        {
            if (File.Exists(FilePath)) return FromJson(File.ReadAllText(FilePath, Encoding.UTF8));
            var c = new RoutingConfig { Sets = RoutingPresets.Builtin(st) };
            c.ActiveId = c.Sets[0].Id;
            List<ConfigRouteRule> old = null;
            try { old = ConfigRouting.Read(st.ConfigRulesJson); } catch { }
            if (old != null && old.Count > 0)
            {
                // kept for reference, not made active: 3.4.6 dropped the old table on purpose
                var mine = Migrate(old, st.ConfigRulesDefault);
                mine.Sort = c.Sets.Count + 1; c.Sets.Add(mine);
            }
            return c;
        }
        public static void Save(RoutingConfig c)
        {
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, ToJson(c), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null); else File.Move(tmp, FilePath);
        }

        // 3.4.3 table → one set. Plain old values were domain suffixes; v2rayN treats a plain value as a keyword, so add "domain:".
        public static RouteSet Migrate(List<ConfigRouteRule> old, string fallback)
        {
            var set = new RouteSet { Remarks = "My rules (3.4)", Sort = 1 };
            foreach (var o in old)
            {
                var r = new RouteRule { Enabled = o.Enabled, Remarks = o.Name ?? "", OutboundTag = o.Outbound ?? "direct", Network = o.Network ?? "", Port = string.Join(",", ConfigRouting.Tokens(o.Ports)) };
                var vals = ConfigRouting.Tokens(o.Values).ToList();
                if (o.Kind == "domain") r.Domain = vals.Select(v => v.IndexOf(':') < 0 ? "domain:" + v : v).ToList();
                else if (o.Kind == "ip") r.Ip = vals;
                else if (o.Kind == "process") r.Process = vals;
                else if (r.Port == "" && r.Network == "") r.Port = "0-65535";
                set.Rules.Add(r);
            }
            if (ConfigRouting.Outbounds.Contains(fallback ?? "") && fallback != "proxy")
                set.Rules.Add(new RouteRule { Remarks = "Unmatched → " + fallback, Port = "0-65535", OutboundTag = fallback });
            return set;
        }

        // ---- JSON (v2rayN key names) ----
        static JavaScriptSerializer Json() { return ConfigRouting.Json(); }
        public static string ToJson(RoutingConfig c)
        {
            return Json().Serialize(new Dictionary<string, object> { { "version", 2 }, { "domainStrategy", c.DomainStrategy }, { "activeId", c.ActiveId }, { "sets", c.Sets.Select(SetToDict).ToArray() } });
        }
        public static RoutingConfig FromJson(string json)
        {
            var root = Json().DeserializeObject(json ?? "") as Dictionary<string, object>;
            if (root == null) throw new FormatException("Invalid routing.json.");
            var c = new RoutingConfig { DomainStrategy = Str(root, "domainStrategy", "AsIs"), ActiveId = Str(root, "activeId", "") };
            if (!ConfigRouting.DomainStrategies.Contains(c.DomainStrategy)) c.DomainStrategy = "AsIs";
            foreach (var o in Items(Get(root, "sets"))) { var d = o as Dictionary<string, object>; if (d != null) c.Sets.Add(SetFromDict(d)); }
            if (c.Sets.Count > MaxSets) throw new FormatException("Too many rule sets (maximum " + MaxSets + ").");
            return c;
        }
        public static Dictionary<string, object> SetToDict(RouteSet s)
        {
            return new Dictionary<string, object> { { "id", s.Id }, { "remarks", s.Remarks }, { "url", s.Url }, { "sort", s.Sort }, { "domainStrategy", s.DomainStrategy }, { "rules", s.Rules.Select(RuleToDict).ToArray() } };
        }
        public static RouteSet SetFromDict(Dictionary<string, object> d)
        {
            var s = new RouteSet { Id = Str(d, "id", Guid.NewGuid().ToString("N")), Remarks = Str(d, "remarks", ""), Url = Str(d, "url", ""), DomainStrategy = Str(d, "domainStrategy", "") };
            int sort; if (int.TryParse(Convert.ToString(Get(d, "sort")), out sort)) s.Sort = sort;
            if (s.DomainStrategy != "" && !ConfigRouting.DomainStrategies.Contains(s.DomainStrategy)) s.DomainStrategy = "";
            object rules = Get(d, "rules");
            // v2rayN keeps the rules of a set as a JSON string ("ruleSet")
            if (rules == null && Get(d, "ruleSet") is string) rules = Json().DeserializeObject((string)Get(d, "ruleSet"));
            s.Rules = ParseRuleList(rules);
            return s;
        }
        public static Dictionary<string, object> RuleToDict(RouteRule r)
        {
            var d = new Dictionary<string, object>();
            if (r.Remarks != "") d["remarks"] = r.Remarks;
            d["outboundTag"] = r.OutboundTag;
            if (r.Port != "") d["port"] = r.Port;
            if (r.Network != "") d["network"] = r.Network;
            if (r.Protocol.Count > 0) d["protocol"] = r.Protocol.ToArray();
            if (r.InboundTag.Count > 0) d["inboundTag"] = r.InboundTag.ToArray();
            if (r.Domain.Count > 0) d["domain"] = r.Domain.ToArray();
            if (r.Ip.Count > 0) d["ip"] = r.Ip.ToArray();
            if (r.Process.Count > 0) d["process"] = r.Process.ToArray();
            if (r.RuleType != 0) d["ruleType"] = r.RuleType;
            d["enabled"] = r.Enabled;
            return d;
        }
        public static RouteRule RuleFromDict(Dictionary<string, object> d)
        {
            var r = new RouteRule
            {
                Remarks = Str(d, "remarks", ""), OutboundTag = Str(d, "outboundTag", "proxy"), Network = Str(d, "network", "").Replace(" ", "").ToLowerInvariant(),
                Port = Convert.ToString(Get(d, "port") ?? "").Replace(" ", ""),
                Domain = List(d, "domain"), Ip = List(d, "ip"), Process = List(d, "process"), Protocol = List(d, "protocol").Select(x => x.ToLowerInvariant()).ToList(), InboundTag = List(d, "inboundTag")
            };
            object en = Get(d, "enabled"); if (en is bool) r.Enabled = (bool)en;
            object rt = Get(d, "ruleType");
            if (rt != null)
            {
                string t = Convert.ToString(rt).Trim().ToLowerInvariant();
                r.RuleType = t == "1" || t == "routing" ? 1 : t == "2" || t == "dns" ? 2 : 0;
            }
            if (r.OutboundTag == "") r.OutboundTag = "proxy";
            return r;
        }
        // Rules from a file / clipboard / URL: a v2rayN rule array, an Xray "routing" object or a whole Xray config.
        public static List<RouteRule> ParseRules(string text)
        {
            object o;
            try { o = Json().DeserializeObject((text ?? "").Trim()); }
            catch (Exception ex) { throw new FormatException("Not a JSON rule list: " + ex.Message); }
            var d = o as Dictionary<string, object>;
            if (d != null)
            {
                object x = Get(d, "routing") ?? d; var dr = x as Dictionary<string, object>;
                o = dr == null ? null : Get(dr, "rules") ?? (Get(dr, "ruleSet") is string ? Json().DeserializeObject((string)Get(dr, "ruleSet")) : null);
            }
            if (!(o is IEnumerable) || o is string) throw new FormatException("No rules found. Expected a JSON array of rules (v2rayN / Xray format).");
            var res = ParseRuleList(o);
            if (res.Count == 0) throw new FormatException("The rule list is empty.");
            return res;
        }
        static List<RouteRule> ParseRuleList(object o)
        {
            var res = new List<RouteRule>();
            foreach (var x in Items(o))
            {
                var d = x as Dictionary<string, object>; if (d == null) throw new FormatException("Invalid rule entry.");
                if (Get(d, "balancerTag") != null && Get(d, "outboundTag") == null) continue; // Xray balancers do not exist here
                res.Add(RuleFromDict(d));
            }
            if (res.Count > MaxRules) throw new FormatException("Too many rules (maximum " + MaxRules + ").");
            return res;
        }
        public static string ExportRules(IEnumerable<RouteRule> rules)
        {
            // pretty enough to read and to paste into v2rayN
            var sb = new StringBuilder("[\r\n");
            var list = rules.ToList();
            for (int i = 0; i < list.Count; i++) sb.Append("  ").Append(Json().Serialize(RuleToDict(list[i]))).Append(i < list.Count - 1 ? ",\r\n" : "\r\n");
            return sb.Append("]").ToString();
        }

        static object Get(Dictionary<string, object> d, string key)
        {
            object v; if (d.TryGetValue(key, out v)) return v;
            foreach (var kv in d) if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
        static string Str(Dictionary<string, object> d, string key, string def) { var v = Get(d, key); return v == null ? def : Convert.ToString(v).Trim(); }
        static IEnumerable<object> Items(object o) { return o is IEnumerable && !(o is string) ? ((IEnumerable)o).Cast<object>() : new object[0]; }
        static List<string> List(Dictionary<string, object> d, string key)
        {
            var v = Get(d, key);
            var raw = v is string ? ((string)v).Split(',') : Items(v).Select(x => Convert.ToString(x));
            return raw.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
        }
    }

    // Builds the sing-box route/DNS rules for the active set, the same way v2rayN does for sing-box.
    // Xray only transports the proxy outbound.
    public static class ConfigRouting
    {
        public static readonly string[] Kinds = { "domain", "ip", "process", "all" };
        public static readonly string[] Outbounds = { "proxy", "direct", "block" };
        public static readonly string[] DomainStrategies = { "AsIs", "IPIfNonMatch", "IPOnDemand" };
        public static readonly string[] Networks = { "", "tcp", "udp", "tcp,udp" };
        public static readonly string[] RuleTypes = { "ALL", "Routing", "DNS" };
        public static readonly string[] Protocols = { "http", "tls", "quic", "bittorrent" };
        static readonly string[] SniffProtocols = { "http", "tls", "quic", "bittorrent", "dtls", "stun", "ssh", "rdp", "ntp", "dns" };
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        public static List<ConfigRouteRule> Read(string json)
        {
            var r = Json().Deserialize<List<ConfigRouteRule>>(json ?? "[]");
            if (r == null || r.Count > 256 || r.Any(x => x == null)) throw new FormatException("Invalid rule list (maximum 256 rules).");
            return r;
        }
        public static string[] Tokens(string s) { return (s ?? "").Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); }
        public static Dictionary<string, object> Field(string key, object value) { return new Dictionary<string, object> { { key, value } }; }
        public static Dictionary<string, object> Logical(string mode, IEnumerable<object> rules) { return new Dictionary<string, object> { { "type", "logical" }, { "mode", mode }, { "rules", rules.ToArray() } }; }

        // ---- v2rayN value syntax ----
        static void Add(Dictionary<string, List<string>> f, string key, string v) { List<string> l; if (!f.TryGetValue(key, out l)) f[key] = l = new List<string>(); if (!l.Contains(v)) l.Add(v); }
        static void AddGeo(Dictionary<string, List<string>> f, IEnumerable<object> geo)
        {
            foreach (Dictionary<string, object> g in geo) foreach (var kv in g) foreach (var v in (IEnumerable<string>)kv.Value) Add(f, kv.Key, v);
        }
        static string Ascii(string host) { return new System.Globalization.IdnMapping().GetAscii(host.Trim().TrimEnd('.')).ToLowerInvariant(); }
        // domain:x (suffix), full:x, keyword:x, regexp:x, geosite:x; plain text = keyword (as in v2ray / v2rayN); # = comment
        public static Dictionary<string, List<string>> Domains(IEnumerable<string> values, Settings st)
        {
            var f = new Dictionary<string, List<string>>();
            foreach (var raw in values)
            {
                string val = raw.Trim(); if (val.Length == 0 || val.StartsWith("#")) continue;
                if (val.StartsWith("ext:") || val.StartsWith("ext-domain:")) throw new FormatException("External geodata files (ext:) are not supported: " + val);
                int colon = val.IndexOf(':');
                string kind = colon < 0 ? "" : val.Substring(0, colon).ToLowerInvariant(), v = colon < 0 ? val : val.Substring(colon + 1).Trim();
                if (kind == "geosite") { AddGeo(f, GeoData.Read(st, true, v)); continue; }
                if (v.Length == 0) throw new FormatException("Empty domain value: " + val);
                if (kind == "domain") Add(f, "domain_suffix", Ascii(v));
                else if (kind == "full") Add(f, "domain", Ascii(v));
                else if (kind == "regexp") { new Regex(v, RegexOptions.None, TimeSpan.FromMilliseconds(50)); Add(f, "domain_regex", v); }
                else if (kind == "keyword" || kind == "dotless") Add(f, "domain_keyword", v.ToLowerInvariant());
                else if (kind == "") Add(f, "domain_keyword", v.ToLowerInvariant());
                else throw new FormatException("Unknown domain prefix: " + val + " (use domain:, full:, keyword:, regexp: or geosite:)");
            }
            return f;
        }
        // CIDR / IP, geoip:x, geoip:private; "!" in front = everything except
        public static Dictionary<string, object> Ips(IEnumerable<string> values, Settings st, bool negative)
        {
            var f = new Dictionary<string, List<string>>(); bool priv = false;
            foreach (var raw in values)
            {
                string val = raw.Trim(); if (val.Length == 0 || val.StartsWith("#") || val.StartsWith("!") != negative) continue;
                if (negative) val = val.Substring(1).Trim();
                if (val.StartsWith("ext:") || val.StartsWith("ext-ip:")) throw new FormatException("External geodata files (ext:) are not supported: " + val);
                if (val.Equals("geoip:private", StringComparison.OrdinalIgnoreCase)) { priv = true; continue; }
                if (val.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)) { AddGeo(f, GeoData.Read(st, false, val.Substring(6).Trim())); continue; }
                string[] p = val.Split('/'); IPAddress ip; int bits;
                if (p.Length > 2 || !IPAddress.TryParse(p[0], out ip) || (p.Length == 2 && (!int.TryParse(p[1], out bits) || bits < 0 || bits > ip.GetAddressBytes().Length * 8))) throw new FormatException("Invalid IP/CIDR: " + raw);
                Add(f, "ip_cidr", p.Length == 1 ? ip + (ip.GetAddressBytes().Length == 4 ? "/32" : "/128") : ip + "/" + p[1]);
            }
            var res = f.ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToArray());
            if (priv) res["ip_is_private"] = true;
            return res;
        }
        // exe name (".exe" is added, sing-box compares it exactly on Windows) or full path
        public static Dictionary<string, object> Processes(IEnumerable<string> values)
        {
            var names = new List<string>(); var paths = new List<string>();
            foreach (var raw in values)
            {
                string v = raw.Trim(); if (v.Length == 0 || v.StartsWith("#")) continue;
                if (v.IndexOfAny(new[] { '\\', '/' }) >= 0)
                {
                    v = v.Replace('/', '\\');
                    if (!Path.IsPathRooted(v)) throw new FormatException("Use a full process path or an executable name: " + raw);
                    v = Path.GetFullPath(v);
                    if (!paths.Contains(v, StringComparer.OrdinalIgnoreCase)) paths.Add(v);
                }
                else
                {
                    if (!v.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) v += ".exe";
                    if (!names.Contains(v, StringComparer.OrdinalIgnoreCase)) names.Add(v);
                }
            }
            var res = new Dictionary<string, object>();
            if (names.Count > 0) res["process_name"] = names.ToArray();
            if (paths.Count > 0) res["process_path"] = paths.ToArray();
            return res;
        }
        // port / network / protocol: all of them must match
        public static Dictionary<string, object> Conditions(RouteRule r)
        {
            var c = new Dictionary<string, object>();
            var ports = new List<int>(); var ranges = new List<string>();
            foreach (var token in Tokens(r.Port))
            {
                var p = token.Split('-'); int lo, hi;
                if (p.Length > 2 || !int.TryParse(p[0].Trim(), out lo) || lo < 0 || lo > 65535) throw new FormatException("Invalid port: " + token);
                if (p.Length == 1) { ports.Add(lo); continue; }
                if (!int.TryParse(p[1].Trim(), out hi) || hi < lo || hi > 65535) throw new FormatException("Invalid port range: " + token);
                ranges.Add(lo + ":" + hi);
            }
            if (ports.Count > 0) c["port"] = ports.ToArray();
            if (ranges.Count > 0) c["port_range"] = ranges.ToArray();
            var nets = Tokens(r.Network).Select(x => x.ToLowerInvariant()).Distinct().ToArray();
            if (nets.Any(n => n != "tcp" && n != "udp")) throw new FormatException("Network must be tcp, udp or both.");
            if (nets.Length == 1) c["network"] = nets[0]; // tcp,udp = any
            if (r.Protocol.Count > 0)
            {
                var bad = r.Protocol.FirstOrDefault(p => !SniffProtocols.Contains(p));
                if (bad != null) throw new FormatException("Unknown protocol: " + bad + " (" + string.Join(", ", SniffProtocols) + ")");
                c["protocol"] = r.Protocol.Distinct().ToArray();
            }
            return c;
        }
        static Dictionary<string, object> Merge(Dictionary<string, object> a, Dictionary<string, object> b)
        {
            var d = new Dictionary<string, object>(a); foreach (var kv in b) d[kv.Key] = kv.Value; return d;
        }
        static Dictionary<string, object> WithAction(Dictionary<string, object> m, string outbound)
        {
            var d = new Dictionary<string, object>(m);
            if (outbound == "block") d["action"] = "reject"; else d["outbound"] = outbound;
            return d;
        }
        static Dictionary<string, object> WithDns(Dictionary<string, object> m, string outbound, Settings st)
        {
            var d = new Dictionary<string, object>(m);
            if (outbound == "block") { d["action"] = "predefined"; d["rcode"] = "NXDOMAIN"; }
            else d["server"] = outbound == "direct" || !st.DnsViaTunnel ? "local" : "remote";
            return d;
        }

        // One rule → 0..4 sing-box rules (domain, IP, process, or only port/network/protocol), like v2rayN.
        // ipOnly: the second IP pass of IPIfNonMatch.
        public static void AddRule(List<object> routes, List<object> dns, RouteRule r, Settings st, bool ipOnly = false)
        {
            if (!r.Enabled) return;
            if (!Outbounds.Contains(r.OutboundTag)) throw new FormatException("Unknown outboundTag: " + r.OutboundTag + " (proxy, direct or block).");
            if (r.Remarks.Length > 256 || r.Port.Length > 4096) throw new FormatException("Rule is too large.");
            // The only inbound here is TUN: rules for other inbounds (socks, http…) never match, as in v2rayN with TUN.
            if (r.InboundTag.Count > 0 && !r.InboundTag.Any(t => t.Equals("tun", StringComparison.OrdinalIgnoreCase))) return;
            var cond = Conditions(r);
            var dom = Domains(r.Domain, st).ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToArray());
            var ip = Ips(r.Ip, st, false); var ipNot = Ips(r.Ip, st, true);
            var proc = Processes(r.Process);
            bool routing = r.RuleType != 2, toDns = r.RuleType != 1 && dns != null && !ipOnly && cond.Count == 0;
            if (routing)
            {
                if (!ipOnly && dom.Count > 0) routes.Add(WithAction(Merge(cond, dom), r.OutboundTag));
                if (ipNot.Count > 0)
                {
                    var parts = new List<object>();
                    if (ip.Count > 0) parts.Add(Merge(cond, ip));
                    var neg = new Dictionary<string, object>(ipNot); neg["invert"] = true; parts.Add(cond.Count > 0 ? Logical("and", new object[] { cond, neg }) : neg);
                    routes.Add(WithAction(Logical("or", parts), r.OutboundTag));
                }
                else if (ip.Count > 0) routes.Add(WithAction(Merge(cond, ip), r.OutboundTag));
                if (!ipOnly && proc.Count > 0) routes.Add(WithAction(Merge(cond, proc), r.OutboundTag));
                if (!ipOnly && dom.Count == 0 && ip.Count == 0 && ipNot.Count == 0 && proc.Count == 0 && cond.Count > 0) routes.Add(WithAction(cond, r.OutboundTag));
            }
            // DNS cannot see the destination IP, port or transport of the later connection.
            if (toDns)
            {
                if (dom.Count > 0) dns.Add(WithDns(dom, r.OutboundTag, st));
                if (proc.Count > 0) dns.Add(WithDns(proc, r.OutboundTag, st));
            }
        }

        public static string StrategyOf(RoutingConfig c, RouteSet s) { return s != null && DomainStrategies.Contains(s.DomainStrategy) ? s.DomainStrategy : c.DomainStrategy; }

        // The active set, with the domain strategy of v2rayN: IPOnDemand resolves first, IPIfNonMatch resolves after a miss and repeats the IP rules.
        public static void AddSet(List<object> routes, List<object> dns, RoutingConfig c, RouteSet set, Settings st, bool ipv4)
        {
            string strategy = StrategyOf(c, set);
            var resolve = new Dictionary<string, object> { { "action", "resolve" }, { "server", st.DnsViaTunnel ? "remote" : "local" }, { "strategy", ipv4 ? "ipv4_only" : "prefer_ipv4" } };
            if (set.Rules.Count > RoutingStore.MaxRules) throw new FormatException("Too many rules (maximum " + RoutingStore.MaxRules + ").");
            if (strategy == "IPOnDemand") routes.Add(resolve);
            for (int i = 0; i < set.Rules.Count; i++)
            {
                try { AddRule(routes, dns, set.Rules[i], st); }
                catch (Exception ex) { throw new FormatException("Rule " + (i + 1) + (set.Rules[i].Remarks != "" ? " «" + set.Rules[i].Remarks + "»" : "") + ": " + ex.Message); }
            }
            if (strategy == "IPIfNonMatch" && set.Rules.Any(r => r.Enabled && r.Ip.Count > 0))
            {
                routes.Add(resolve);
                foreach (var r in set.Rules.Where(r => r.Ip.Count > 0)) AddRule(routes, null, r, st, true);
            }
        }
        // Throws a readable error for the first bad rule.
        public static void Validate(RoutingConfig c, RouteSet set, Settings st) { AddSet(new List<object>(), new List<object>(), c, set, st, st.TunBlockIpv6); }

        // ---- legacy (3.4.3) single-kind matcher, used by RoutingSnapshot for runtime Xray rules ----
        public static Dictionary<string, object> Matcher(ConfigRouteRule r, Settings st)
        {
            if (!Kinds.Contains(r.Kind ?? "")) throw new FormatException("Unsupported runtime rule.");
            var vals = Tokens(r.Values);
            if (r.Kind == "all") return new Dictionary<string, object>();
            Dictionary<string, object> m;
            if (r.Kind == "process") m = Processes(vals);
            else if (r.Kind == "ip") m = Ips(vals, st, false);
            else m = Domains(vals, st).ToDictionary(kv => kv.Key, kv => (object)kv.Value.ToArray()); // Xray: plain value = keyword
            if (m.Count == 0) throw new FormatException("Rule values cannot be empty.");
            return m;
        }
    }

    // Built-in sets (v2rayN "Import built-in rules" + the Russia regional preset of runetfreedom).
    public static class RoutingPresets
    {
        const string Rf = "https://raw.githubusercontent.com/runetfreedom/russia-v2ray-custom-routing-list/main/v2rayN/";
        public const string IdExceptRu = "builtin-except-ru", IdBlocked = "builtin-ru-blocked", IdAll = "builtin-all", IdDirect = "builtin-direct";

        static RouteRule R(string remarks, string outbound, string[] domain = null, string[] ip = null, string port = "", string network = "", string[] protocol = null, bool enabled = true)
        {
            return new RouteRule { Remarks = remarks, OutboundTag = outbound, Domain = (domain ?? new string[0]).ToList(), Ip = (ip ?? new string[0]).ToList(), Port = port, Network = network, Protocol = (protocol ?? new string[0]).ToList(), Enabled = enabled };
        }
        static IEnumerable<RouteRule> Common(Settings st)
        {
            yield return R("Ads → block", "block", domain: new[] { "geosite:category-ads-all" }, enabled: false);
            yield return R("Private networks → direct", "direct", ip: new[] { "geoip:private" });
            if (GeoData.Has(st, true, "private")) yield return R("Private domains → direct", "direct", domain: new[] { "geosite:private" });
        }
        // "RU sites detect": RU domains of whichever geodata is installed + .ru/.su/.рф + RU IP ranges.
        public static RouteRule RuDirect(Settings st)
        {
            return R("RU sites → direct", "direct", domain: RuDetect.DirectDomains(st).ToArray(), ip: RuDetect.DirectIps(st).ToArray());
        }
        public static List<RouteSet> Builtin(Settings st)
        {
            var exceptRu = new RouteSet { Id = IdExceptRu, Remarks = "RU direct, rest proxy", Url = Rf + "all_except_ru.json", DomainStrategy = "IPOnDemand", Sort = 1 };
            exceptRu.Rules.Add(R("Torrent → direct", "direct", protocol: new[] { "bittorrent" }));
            exceptRu.Rules.AddRange(Common(st));
            exceptRu.Rules.Add(RuDirect(st));
            exceptRu.Rules.Add(R("Rest → proxy", "proxy", port: "0-65535"));

            var blocked = new RouteSet { Id = IdBlocked, Remarks = "Blocked in RU → proxy, rest direct", Url = Rf + "only_blocked.json", DomainStrategy = "IPOnDemand", Sort = 2 };
            blocked.Rules.Add(R("Torrent → direct", "direct", protocol: new[] { "bittorrent" }));
            blocked.Rules.AddRange(Common(st));
            blocked.Rules.Add(R("Public DNS → proxy", "proxy", ip: new[] { "1.0.0.1", "1.1.1.1", "8.8.8.8", "8.8.4.4" }));
            blocked.Rules.Add(R("Discord voice → proxy", "proxy", port: "50000-65535", network: "udp"));
            blocked.Rules.Add(R("Blocked networks → proxy", "proxy", ip: new[] { "geoip:ru-blocked" }));
            blocked.Rules.Add(R("Blocked domains → proxy", "proxy", domain: new[] { "geosite:ru-blocked" }));
            blocked.Rules.Add(R("Rest → direct", "direct", port: "0-65535"));

            var all = new RouteSet { Id = IdAll, Remarks = "Everything via proxy", Url = Rf + "all.json", Sort = 3 };
            all.Rules.AddRange(Common(st));
            all.Rules.Add(R("Rest → proxy", "proxy", port: "0-65535"));

            var direct = new RouteSet { Id = IdDirect, Remarks = "Everything direct", Sort = 4 };
            direct.Rules.Add(R("Rest → direct", "direct", port: "0-65535"));
            return new List<RouteSet> { exceptRu, blocked, all, direct };
        }
    }

    // Which RU lists are installed, and whether a domain / IP is in them.
    public static class RuDetect
    {
        public static readonly string[] SiteCategories = { "category-ru", "ru-available-only-inside", "ru-blocked" };
        public static readonly string[] IpCategories = { "ru", "ru-blocked" };
        public const string RuGeoBase = "https://github.com/runetfreedom/russia-v2ray-rules-dat/releases/latest/download/";

        public static List<string> DirectDomains(Settings st)
        {
            var l = new List<string>();
            foreach (var c in new[] { "category-ru", "ru-available-only-inside" }) if (GeoData.Has(st, true, c)) l.Add("geosite:" + c);
            l.AddRange(new[] { "domain:ru", "domain:su", "domain:xn--p1ai" });
            return l;
        }
        public static List<string> DirectIps(Settings st) { return GeoData.Has(st, false, "ru") ? new List<string> { "geoip:ru" } : new List<string>(); }
        public static bool RuGeoInstalled(Settings st) { return File.Exists(GeoData.RuFile(st, true)) && File.Exists(GeoData.RuFile(st, false)); }

        // host → which RU lists contain it. ips: addresses it resolves to (may be empty).
        public static string Report(Settings st, string host, IPAddress[] ips)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(host))
            {
                string h; try { h = new System.Globalization.IdnMapping().GetAscii(host.TrimEnd('.')).ToLowerInvariant(); } catch { h = host.ToLowerInvariant(); }
                bool tld = h.EndsWith(".ru") || h.EndsWith(".su") || h.EndsWith(".xn--p1ai") || h == "ru" || h == "su";
                parts.Add((tld ? "✓" : "✗") + " .ru/.su/.рф");
                foreach (var c in SiteCategories)
                {
                    if (!GeoData.Has(st, true, c)) { parts.Add("· geosite:" + c + " (not installed)"); continue; }
                    bool hit = GeoData.Read(st, true, c).Any(g => RoutingSnapshot.Matches((Dictionary<string, object>)g, null, null, null, h) == true);
                    parts.Add((hit ? "✓" : "✗") + " geosite:" + c);
                }
            }
            foreach (var c in IpCategories)
            {
                if (!GeoData.Has(st, false, c)) { parts.Add("· geoip:" + c + " (not installed)"); continue; }
                if (ips == null || ips.Length == 0) continue;
                var geo = GeoData.Read(st, false, c);
                var hits = ips.Where(ip => geo.Any(g => RoutingSnapshot.Matches((Dictionary<string, object>)g, null, null, ip, null) == true)).ToList();
                parts.Add((hits.Count > 0 ? "✓" : "✗") + " geoip:" + c);
            }
            return string.Join("   ", parts);
        }

        // RU geodata of runetfreedom (v2rayN Russia preset): geosite ru-blocked / ru-available-only-inside, geoip ru-blocked.
        // Saved next to the Xray geodata under separate names, so an Xray update does not replace them.
        public static string Install(Settings st, Action<string> status)
        {
            Directory.CreateDirectory(st.XrayPath);
            foreach (var site in new[] { true, false })
            {
                string name = site ? "geosite.dat" : "geoip.dat", dst = GeoData.RuFile(st, site), tmp = dst + ".download";
                status("Downloading " + name + "…");
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                var rq = (HttpWebRequest)WebRequest.Create(RuGeoBase + name);
                rq.UserAgent = "NetProbe/" + MainForm.Version; rq.Timeout = 60000; rq.ReadWriteTimeout = 60000;
                using (var r = rq.GetResponse()) using (var src = r.GetResponseStream()) using (var f = File.Create(tmp)) src.CopyTo(f);
                GeoData.Verify(tmp, site);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
            }
            return "RU geodata installed";
        }
    }

    // Read the installed Xray geodata, without downloading extra routing databases.
    // Wire format: v2ray routercommon/common.proto (GeoSiteList / GeoIPList).
    // Categories are indexed once per file and decoded on demand (the RU lists are large).
    public static class GeoData
    {
        sealed class Reader
        {
            public byte[] Data; public int Pos, End;
            public Reader(byte[] data) { Data = data; End = data.Length; }
            public ulong Varint()
            {
                ulong n = 0;
                for (int shift = 0; shift < 64; shift += 7) { if (Pos >= End) throw new FormatException("Truncated geodata."); byte b = Data[Pos++]; if (shift == 63 && b > 1) throw new FormatException("Invalid geodata integer."); n |= (ulong)(b & 127) << shift; if (b < 128) return n; }
                throw new FormatException("Invalid geodata integer.");
            }
            public Reader Message() { ulong length = Varint(); if (length > (ulong)(End - Pos)) throw new FormatException("Invalid geodata length."); var r = new Reader(Data) { Pos = Pos, End = Pos + (int)length }; Pos = r.End; return r; }
            public string Str() { var r = Message(); return Encoding.UTF8.GetString(Data, r.Pos, r.End - r.Pos); }
            public void Skip(int wire) { if (wire == 0) Varint(); else if (wire == 2) Message(); else { int n = wire == 1 ? 8 : wire == 5 ? 4 : -1; if (n < 0 || n > End - Pos) throw new FormatException("Invalid geodata field."); Pos += n; } }
        }
        sealed class Cache { public long Length; public DateTime Time; public Dictionary<string, int[]> Index; public Dictionary<string, List<object>> Groups; }
        static readonly Dictionary<string, Cache> cache = new Dictionary<string, Cache>(StringComparer.OrdinalIgnoreCase);

        public static string MainFile(Settings st, bool site) { return Path.Combine(st.XrayPath, site ? "geosite.dat" : "geoip.dat"); }
        public static string RuFile(Settings st, bool site) { return Path.Combine(st.XrayPath, site ? "geosite-ru.dat" : "geoip-ru.dat"); }

        static Cache Open(string path)
        {
            var fi = new FileInfo(path); if (!fi.Exists) return null;
            if (fi.Length > 256 * 1024 * 1024) throw new FormatException("Geodata file too large.");
            Cache c;
            if (cache.TryGetValue(path, out c) && c.Length == fi.Length && c.Time == fi.LastWriteTimeUtc) return c;
            c = new Cache { Length = fi.Length, Time = fi.LastWriteTimeUtc, Index = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase), Groups = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase) };
            var root = new Reader(File.ReadAllBytes(path));
            while (root.Pos < root.End)
            {
                int tag = (int)root.Varint(); if (tag != 10) { root.Skip(tag & 7); continue; }
                var group = root.Message(); int start = group.Pos;
                while (group.Pos < group.End)
                {
                    tag = (int)group.Varint();
                    if (tag == 10) { string code = group.Str(); if (!c.Index.ContainsKey(code)) c.Index[code] = new[] { start, group.End }; break; }
                    group.Skip(tag & 7);
                }
            }
            cache[path] = c;
            return c;
        }
        static List<object> Decode(byte[] data, int start, int end, bool site)
        {
            var group = new Reader(data) { Pos = start, End = end }; bool inverse = false; var fields = new Dictionary<string, List<string>>();
            while (group.Pos < group.End)
            {
                int tag = (int)group.Varint();
                if (!site && tag == 24) { inverse = group.Varint() != 0; continue; }
                if (tag != 18) { group.Skip(tag & 7); continue; }
                var e = group.Message(); string key, value;
                if (site)
                {
                    int type = 0; value = null;
                    while (e.Pos < e.End) { tag = (int)e.Varint(); if (tag == 8) type = (int)e.Varint(); else if (tag == 18) value = e.Str(); else e.Skip(tag & 7); }
                    key = type == 0 ? "domain_keyword" : type == 1 ? "domain_regex" : type == 2 ? "domain_suffix" : type == 3 ? "domain" : null;
                }
                else
                {
                    byte[] ip = null; int bits = 0;
                    while (e.Pos < e.End) { tag = (int)e.Varint(); if (tag == 10) { var b = e.Message(); ip = new byte[b.End - b.Pos]; Buffer.BlockCopy(b.Data, b.Pos, ip, 0, ip.Length); } else if (tag == 16) bits = (int)e.Varint(); else e.Skip(tag & 7); }
                    if (ip == null || (ip.Length != 4 && ip.Length != 16) || bits < 0 || bits > ip.Length * 8) throw new FormatException("Invalid geodata CIDR.");
                    key = "ip_cidr"; value = new IPAddress(ip) + "/" + bits;
                }
                if (key == null || string.IsNullOrEmpty(value)) throw new FormatException("Invalid geodata entry.");
                List<string> l; if (!fields.TryGetValue(key, out l)) fields[key] = l = new List<string>(); l.Add(value);
            }
            if (inverse) return new List<object>();
            return fields.Select(kv => (object)ConfigRouting.Field(kv.Key, kv.Value.ToArray())).ToList();
        }
        static List<object> Find(string path, string category, bool site)
        {
            var c = Open(path); int[] at;
            if (c == null || !c.Index.TryGetValue(category, out at)) return null;
            List<object> res;
            if (!c.Groups.TryGetValue(category, out res)) c.Groups[category] = res = Decode(File.ReadAllBytes(path), at[0], at[1], site);
            return res.Count == 0 ? null : res;
        }
        public static List<object> Read(Settings st, bool site, string category)
        {
            if (!Regex.IsMatch(category ?? "", "^[a-zA-Z0-9_!.-]+$")) throw new FormatException("Invalid geodata category: " + category);
            string main = MainFile(st, site), ru = RuFile(st, site);
            lock (cache)
            {
                if (!File.Exists(main) && !File.Exists(ru)) throw new FileNotFoundException("Install the Xray geodata first: " + Path.GetFileName(main));
                var r = Find(main, category, site) ?? Find(ru, category, site);
                if (r == null) throw new FormatException("Geodata category not found: " + (site ? "geosite:" : "geoip:") + category + (RuDetect.RuGeoInstalled(st) ? "" : " (RU lists need the RU geodata)"));
                return r;
            }
        }
        public static bool Has(Settings st, bool site, string category)
        {
            try { lock (cache) { foreach (var p in new[] { MainFile(st, site), RuFile(st, site) }) { var c = Open(p); if (c != null && c.Index.ContainsKey(category)) return true; } } }
            catch { }
            return false;
        }
        public static void Verify(string path, bool site)
        {
            lock (cache)
            {
                var c = Open(path); cache.Remove(path);
                if (c == null || c.Index.Count == 0) throw new FormatException("Downloaded geodata is empty or invalid: " + Path.GetFileName(path));
                if (!c.Index.ContainsKey(site ? "ru-blocked" : "ru")) throw new FormatException("Downloaded geodata has no RU lists: " + Path.GetFileName(path));
            }
        }
    }
}
