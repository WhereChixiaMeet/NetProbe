using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace NetProbe
{
    // Read the generated runtime rules, rather than predicting from currently edited settings.
    public sealed class RoutingSnapshot
    {
        public List<Dictionary<string, object>> Routes = new List<Dictionary<string, object>>(), XrayRoutes = new List<Dictionary<string, object>>();
        public string Final = "direct", Error;
        public static RoutingSnapshot Load(Settings st)
        {
            var s = new RoutingSnapshot();
            try
            {
                var root = (Dictionary<string, object>)ConfigRouting.Json().DeserializeObject(File.ReadAllText(Path.Combine(Settings.Sub("runtime"), "singbox-tunnel.json")));
                var route = (Dictionary<string, object>)root["route"]; s.Final = (string)route["final"];
                s.Routes = Items(route["rules"]).Cast<Dictionary<string, object>>().ToList();
                root = (Dictionary<string, object>)ConfigRouting.Json().DeserializeObject(File.ReadAllText(Path.Combine(Settings.Sub("runtime"), "xray-tunnel.json")));
                foreach (Dictionary<string, object> r in Items(((Dictionary<string, object>)root["routing"])["rules"]))
                {
                    object values; string kind = r.TryGetValue("domain", out values) ? "domain" : r.TryGetValue("ip", out values) ? "ip" : null;
                    if (kind == null) throw new FormatException("Unsupported runtime Xray routing rule.");
                    var rule = new ConfigRouteRule { Kind = kind, Values = string.Join(",", Items(values).Select(x => x.ToString())), Outbound = (string)r["outboundTag"] };
                    var m = ConfigRouting.Matcher(rule, st); m["outbound"] = rule.Outbound; s.XrayRoutes.Add(m);
                }
            }
            catch (Exception ex) { s.Error = "Cannot read active routing rules: " + ex.Message; }
            return s;
        }
        static IEnumerable<object> Items(object o) { return ((IEnumerable)o).Cast<object>(); }
        static readonly Cidr[] Private = new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "169.254.0.0/16", "fc00::/7", "fe80::/10", "::1/128" }.Select(Cidr.Parse).ToArray();
        static bool? Or(IEnumerable<bool?> vals) { bool unknown = false; foreach (var v in vals) { if (v == true) return true; if (!v.HasValue) unknown = true; } return unknown ? (bool?)null : false; }
        static bool? And(IEnumerable<bool?> vals) { bool unknown = false; foreach (var v in vals) { if (v == false) return false; if (!v.HasValue) unknown = true; } return unknown ? (bool?)null : true; }
        // Missing facts: a check without an app means "an app outside the lists" (process conditions are false);
        // without a domain, domain conditions are assumed not to match (the caller reports which rule could change that);
        // without both a domain and an IP (app-only check), destination conditions describe the app's ordinary traffic.
        public static bool? Matches(Dictionary<string, object> r, string name, string path, IPAddress ip, string host) { return Matches(r, name, path, ip, host, false, false, false); }
        public static bool? Matches(Dictionary<string, object> r, string name, string path, IPAddress ip, string host, bool noApp, bool noDomain, bool noDest) { return Matches(r, name, path, ip, host, noApp, noDomain, noDest, 0, null, null); }
        static KeyValuePair<string, bool?> C(string key, bool? v) { return new KeyValuePair<string, bool?>(key, v); }
        // sing-box: fields of one group are alternatives (domain / domain_suffix…, ip_cidr / ip_is_private, process_*, port / port_range)
        static string Group(string key)
        {
            if (key.StartsWith("domain")) return "domain";
            if (key == "ip_cidr" || key == "ip_is_private") return "ip";
            if (key.StartsWith("process_")) return "process";
            if (key == "port" || key == "port_range") return "port";
            return key;
        }
        static bool? Not(bool? v) { return v.HasValue ? !v.Value : (bool?)null; }
        static bool CatchAll(string key, object value) { return key == "port_range" && value is IEnumerable && Items(value).Any(x => { var t = x.ToString(); return t == "0:65535" || t == "1:65535"; }); }
        // port / network / protocol: what the caller assumes about the connection (0 / null = unknown). "invert": inverted rule (!geoip:…).
        public static bool? Matches(Dictionary<string, object> r, string name, string path, IPAddress ip, string host, bool noApp, bool noDomain, bool noDest, int port, string network, string protocol)
        {
            object type, inv;
            bool invert = r.TryGetValue("invert", out inv) && inv is bool && (bool)inv;
            if (r.TryGetValue("type", out type) && (string)type == "logical")
            {
                var v = Items(r["rules"]).Cast<Dictionary<string, object>>().Select(x => Matches(x, name, path, ip, host, noApp, noDomain, noDest, port, network, protocol)).ToList();
                var lr = (string)r["mode"] == "or" ? Or(v) : And(v);
                return invert ? Not(lr) : lr;
            }
            var conditions = new List<KeyValuePair<string, bool?>>();
            foreach (var kv in r)
            {
                if (kv.Key == "outbound" || kv.Key == "action" || kv.Key == "invert") continue;
                if (CatchAll(kv.Key, kv.Value)) { conditions.Add(C(kv.Key, true)); continue; } // "0-65535" = any connection
                if (noDest && (kv.Key.StartsWith("domain") || kv.Key.StartsWith("ip_") || kv.Key.StartsWith("port") || kv.Key == "network" || kv.Key == "protocol")) { conditions.Add(C(kv.Key, false)); continue; }
                if (kv.Key == "ip_version") { conditions.Add(C(kv.Key, ip == null ? (bool?)null : (ip.GetAddressBytes().Length == 4 ? 4 : 6) == Convert.ToInt32(kv.Value))); continue; }
                if (kv.Key == "ip_is_private") { conditions.Add(C(kv.Key, ip == null ? (bool?)null : Private.Any(c => c.Contains(ip)))); continue; }
                var vals = kv.Value is IEnumerable && !(kv.Value is string) ? Items(kv.Value).Select(x => x.ToString()).ToArray() : new[] { kv.Value.ToString() };
                if (kv.Key.StartsWith("domain"))
                {
                    if (string.IsNullOrEmpty(host)) { conditions.Add(C(kv.Key, noDomain ? false : (bool?)null)); continue; }
                    host = host.TrimEnd('.').ToLowerInvariant();
                    try
                    {
                        conditions.Add(C(kv.Key, vals.Any(v => kv.Key == "domain" ? string.Equals(v, host, StringComparison.OrdinalIgnoreCase) : kv.Key == "domain_suffix" ? host == v.ToLowerInvariant() || host.EndsWith("." + v, StringComparison.OrdinalIgnoreCase) : kv.Key == "domain_keyword" ? host.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0 : kv.Key == "domain_regex" && Regex.IsMatch(host, v, RegexOptions.None, TimeSpan.FromMilliseconds(50)))));
                    }
                    catch { conditions.Add(C(kv.Key, null)); }
                }
                else if (kv.Key == "ip_cidr") conditions.Add(C(kv.Key, ip == null ? (bool?)null : vals.Any(v => Cidr.Parse(v).Contains(ip))));
                else if (kv.Key.StartsWith("process_") && noApp) conditions.Add(C(kv.Key, false));
                else if (kv.Key == "process_name") conditions.Add(C(kv.Key, string.IsNullOrEmpty(name) ? (bool?)null : vals.Contains(name, StringComparer.OrdinalIgnoreCase)));
                else if (kv.Key == "process_path") conditions.Add(C(kv.Key, string.IsNullOrEmpty(path) ? (bool?)null : vals.Contains(path, StringComparer.OrdinalIgnoreCase)));
                else if (kv.Key == "process_path_regex")
                {
                    if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(name)) conditions.Add(C(kv.Key, null));
                    else try { conditions.Add(C(kv.Key, vals.Any(v => Regex.IsMatch(string.IsNullOrEmpty(path) ? name : path, v, RegexOptions.None, TimeSpan.FromMilliseconds(50))))); } catch { conditions.Add(C(kv.Key, null)); }
                }
                else if (kv.Key == "port") conditions.Add(C(kv.Key, port <= 0 ? (bool?)null : vals.Contains(port.ToString())));
                else if (kv.Key == "port_range") conditions.Add(C(kv.Key, port <= 0 ? (bool?)null : vals.Any(x => { var p = x.Split(':'); int lo, hi; return p.Length == 2 && int.TryParse(p[0], out lo) && int.TryParse(p[1], out hi) && port >= lo && port <= hi; })));
                else if (kv.Key == "network") conditions.Add(C(kv.Key, network == null ? (bool?)null : vals.Contains(network, StringComparer.OrdinalIgnoreCase)));
                else if (kv.Key == "protocol") conditions.Add(C(kv.Key, protocol == null ? (bool?)null : vals.Contains(protocol, StringComparer.OrdinalIgnoreCase)));
                else conditions.Add(C(kv.Key, null)); // No destination port / transport supplied by RouteDialog.
            }
            var res = And(conditions.GroupBy(x => Group(x.Key)).Select(g => Or(g.Select(x => x.Value))));
            return invert ? Not(res) : res;
        }
        public string Decide(string name, string path, IPAddress ip, string host, out string why)
        {
            if (Error != null) { why = Error; return "unknown"; }
            string result = DecideStage(Routes, Final, name, path, ip, host, out why);
            if (ip == null && string.IsNullOrEmpty(host)) why += " · ordinary traffic of the app (domain / IP rules may send some sites elsewhere)";
            if (result != "proxy") return result;
            string first = why;
            string second = DecideStage(XrayRoutes, "proxy", name, path, ip, host, out why);
            why = "sing-box: " + first + " → Xray: " + why;
            return second;
        }
        static string Outbound(Dictionary<string, object> r) { object o; return r.TryGetValue("outbound", out o) ? (string)o : "block"; }
        // First match wins. A rule that cannot be decided from the given facts (port / transport, or a domain that the
        // connection table does not know) only makes the answer Unknown when it would lead somewhere else.
        static string DecideStage(List<Dictionary<string, object>> rules, string fallback, string name, string path, IPAddress ip, string host, out string why)
        {
            bool noApp = string.IsNullOrEmpty(name) && string.IsNullOrEmpty(path);
            bool noDomain = string.IsNullOrEmpty(host);
            bool noDest = noDomain && ip == null;
            var maybe = new List<KeyValuePair<int, string>>();   // rules that need a port / transport
            var domainOnly = new List<KeyValuePair<int, string>>(); // rules that would decide if the domain were known
            string result = null, reason = null;
            for (int i = 0; i < rules.Count; i++)
            {
                object action; var r = rules[i];
                if (r.TryGetValue("action", out action) && ((string)action == "sniff" || (string)action == "hijack-dns" || (string)action == "resolve")) continue;
                if (SingBoxConfig.IsQuicGuard(r)) continue; // only QUIC (UDP 443) to the server; the route check is about the site
                bool? match = Matches(r, name, path, ip, host, noApp, noDomain, noDest);
                if (!match.HasValue) { maybe.Add(new KeyValuePair<int, string>(i + 1, Outbound(r))); continue; }
                if (match == true) { result = Outbound(r); reason = "Runtime rule " + (i + 1); break; }
                if (noDomain && !noDest && Matches(r, name, path, ip, host, noApp, false, false) != false) domainOnly.Add(new KeyValuePair<int, string>(i + 1, Outbound(r)));
            }
            if (result == null) { result = fallback; reason = "Runtime fallback: " + fallback; }
            var other = maybe.Where(m => m.Value != result).ToList();
            if (other.Count > 0)
            {
                why = "Rule " + other[0].Key + " (" + other[0].Value + ") needs a port / transport that is not known here; otherwise " + reason + " → " + result + ".";
                return "unknown";
            }
            why = reason;
            var dom = domainOnly.Where(m => m.Value != result).ToList();
            if (dom.Count > 0) why += " · domain unknown: rule " + string.Join(", ", dom.Select(m => m.Key + " → " + m.Value)) + " applies if the site matches it";
            return result;
        }
    }
}
