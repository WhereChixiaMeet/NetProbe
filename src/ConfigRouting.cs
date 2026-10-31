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
    public sealed class ConfigRouteRule
    {
        public bool Enabled = true;
        public string Name = "New rule", Kind = "domain", Values = "", Outbound = "direct", Network = "", Ports = "";
    }

    // One ordered routing stage in sing-box. Xray only transports its proxy outbound.
    public static class ConfigRouting
    {
        public static readonly string[] Kinds = { "domain", "ip", "process", "all" };
        public static readonly string[] Outbounds = { "direct", "proxy", "block" };
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }; }
        public static List<ConfigRouteRule> Read(string json)
        {
            var r = Json().Deserialize<List<ConfigRouteRule>>(json ?? "[]");
            if (r == null || r.Count > 256 || r.Any(x => x == null)) throw new FormatException("Invalid rule list (maximum 256 rules).");
            return r;
        }
        public static string[] Tokens(string s) { return (s ?? "").Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); }
        public static Dictionary<string, object> Field(string key, object value) { return new Dictionary<string, object> { { key, value } }; }
        public static Dictionary<string, object> Logical(string mode, IEnumerable<object> rules) { return new Dictionary<string, object> { { "type", "logical" }, { "mode", mode }, { "rules", rules.ToArray() } }; }
        public static Dictionary<string, object> Matcher(ConfigRouteRule r, Settings st)
        {
            if (!Kinds.Contains(r.Kind) || !Outbounds.Contains(r.Outbound)) throw new FormatException("Unknown rule type or outbound.");
            if ((r.Values ?? "").Length > 256 * 1024 || (r.Ports ?? "").Length > 4096 || (r.Name ?? "").Length > 256) throw new FormatException("Rule is too large.");
            var parts = new List<object>();
            var vals = Tokens(r.Values);
            if (r.Kind != "all" && vals.Length == 0) throw new FormatException("Rule values cannot be empty.");
            if (r.Kind != "all")
            {
                var matches = new List<object>();
                foreach (var val in vals)
                {
                    if (r.Kind == "process")
                    {
                        if (val.IndexOfAny(new[] { '\\', '/' }) >= 0)
                        {
                            if (!Path.IsPathRooted(val)) throw new FormatException("Use a full process path or an executable name: " + val);
                            matches.Add(Field("process_path", new[] { Path.GetFullPath(val) }));
                        }
                        else matches.Add(Field("process_name", new[] { val }));
                    }
                    else if (r.Kind == "ip")
                    {
                        if (val.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)) matches.AddRange(GeoData.Read(st, false, val.Substring(6)));
                        else
                        {
                            string[] p = val.Split('/'); IPAddress ip; int bits;
                            if (p.Length > 2 || !IPAddress.TryParse(p[0], out ip) || (p.Length == 2 && (!int.TryParse(p[1], out bits) || bits < 0 || bits > ip.GetAddressBytes().Length * 8))) throw new FormatException("Invalid IP/CIDR: " + val);
                            matches.Add(Field("ip_cidr", new[] { p.Length == 1 ? val + (ip.GetAddressBytes().Length == 4 ? "/32" : "/128") : val }));
                        }
                    }
                    else if (val.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)) matches.AddRange(GeoData.Read(st, true, val.Substring(8)));
                    else
                    {
                        int colon = val.IndexOf(':'); string kind = colon < 0 ? "domain" : val.Substring(0, colon).ToLowerInvariant(), v = colon < 0 ? val : val.Substring(colon + 1);
                        string key = kind == "domain" ? "domain_suffix" : kind == "full" ? "domain" : kind == "keyword" ? "domain_keyword" : kind == "regexp" ? "domain_regex" : null;
                        if (key == null || v.Length == 0) throw new FormatException("Use domain:, full:, keyword:, regexp: or geosite: for domains.");
                        if (key == "domain_regex") new Regex(v, RegexOptions.None, TimeSpan.FromMilliseconds(50));
                        else if (key != "domain_keyword") v = new System.Globalization.IdnMapping().GetAscii(v.TrimEnd('.')).ToLowerInvariant();
                        matches.Add(Field(key, new[] { v }));
                    }
                }
                parts.Add(Logical("or", matches));
            }
            if (!string.IsNullOrEmpty(r.Network))
            {
                if (r.Network != "tcp" && r.Network != "udp") throw new FormatException("Network must be tcp, udp or empty.");
                parts.Add(Field("network", r.Network));
            }
            var ports = new List<object>();
            foreach (var token in Tokens(r.Ports))
            {
                var p = token.Split('-'); int lo, hi;
                if (p.Length > 2 || !int.TryParse(p[0], out lo) || lo < 1 || lo > 65535) throw new FormatException("Invalid port: " + token);
                hi = lo;
                if (p.Length == 2 && (!int.TryParse(p[1], out hi) || hi < lo || hi > 65535)) throw new FormatException("Invalid port range: " + token);
                ports.Add(p.Length == 1 ? Field("port", new[] { lo }) : Field("port_range", new[] { lo + ":" + hi }));
            }
            if (ports.Count > 0) parts.Add(Logical("or", ports));
            return parts.Count == 0 ? new Dictionary<string, object>() : Logical("and", parts);
        }
        public static void AddRule(List<object> routes, List<object> dns, ConfigRouteRule r, Settings st)
        {
            if (!r.Enabled) return;
            var match = Matcher(r, st);
            var route = new Dictionary<string, object>(match);
            if (r.Outbound == "block") route["action"] = "reject";
            else route["outbound"] = r.Outbound;
            routes.Add(route);
            // DNS cannot identify an app whose queries are issued by the Windows DNS service.
            // Destination IP/port and transport rules have no equivalent for a name-only DNS query.
            if (r.Kind == "ip" || r.Network != "" || Tokens(r.Ports).Length > 0) return;
            var dr = new Dictionary<string, object>(match);
            if (r.Outbound == "block") { dr["action"] = "predefined"; dr["rcode"] = "NXDOMAIN"; }
            else dr["server"] = r.Outbound == "direct" || !st.DnsViaTunnel ? "local" : "remote";
            dns.Add(dr);
        }
        public static List<ConfigRouteRule> Preset(TunLists lists)
        {
            var res = new List<ConfigRouteRule> {
                new ConfigRouteRule { Name = "RU domains → Direct", Kind = "domain", Values = "geosite:category-ru,domain:ru,domain:su,domain:xn--p1ai" },
                new ConfigRouteRule { Name = "RU IP → Direct", Kind = "ip", Values = "geoip:ru" }
            };
            var ex = ExclusionSet.From(lists.Exclude);
            if (ex.Processes.Count > 0) res.Add(new ConfigRouteRule { Name = "Exclude → Direct", Kind = "process", Values = string.Join(",", ex.Processes) });
            if (ex.Domains.Count > 0) res.Add(new ConfigRouteRule { Name = "Excluded domains → Direct", Values = string.Join(",", ex.Domains) });
            if (ex.Ips.Count > 0) res.Add(new ConfigRouteRule { Name = "Excluded IP → Direct", Kind = "ip", Values = string.Join(",", ex.Ips) });
            if (lists.Include.Count > 0) res.Add(new ConfigRouteRule { Name = "Include → VPN", Kind = "process", Values = string.Join(",", lists.Include), Outbound = "proxy" });
            var inc = ExclusionSet.From(lists.IncludeHosts);
            if (inc.Domains.Count > 0) res.Add(new ConfigRouteRule { Name = "Included domains → VPN", Values = string.Join(",", inc.Domains), Outbound = "proxy" });
            if (inc.Ips.Count > 0) res.Add(new ConfigRouteRule { Name = "Included IP → VPN", Kind = "ip", Values = string.Join(",", inc.Ips), Outbound = "proxy" });
            return res;
        }
    }

    // Read the installed Xray geodata, without downloading extra routing databases.
    // Wire format: v2ray routercommon/common.proto (GeoSiteList / GeoIPList).
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
        sealed class Cache { public long Length; public DateTime Time; public Dictionary<string, List<object>> Groups; }
        static readonly Dictionary<string, Cache> cache = new Dictionary<string, Cache>(StringComparer.OrdinalIgnoreCase);
        public static List<object> Read(Settings st, bool site, string category)
        {
            if (!Regex.IsMatch(category, "^[a-zA-Z0-9_-]+$")) throw new FormatException("Invalid geodata category: " + category);
            string path = Path.Combine(st.XrayPath, site ? "geosite.dat" : "geoip.dat");
            lock (cache)
            {
                var fi = new FileInfo(path); if (!fi.Exists) throw new FileNotFoundException("Install the Xray geodata first: " + fi.Name);
                if (fi.Length > 256 * 1024 * 1024) throw new FormatException("Geodata file too large.");
                Cache c;
                if (!cache.TryGetValue(path, out c) || c.Length != fi.Length || c.Time != fi.LastWriteTimeUtc)
                {
                    c = new Cache { Length = fi.Length, Time = fi.LastWriteTimeUtc, Groups = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase) };
                    var root = new Reader(File.ReadAllBytes(path));
                    while (root.Pos < root.End)
                    {
                        int tag = (int)root.Varint(); if (tag != 10) { root.Skip(tag & 7); continue; }
                        var group = root.Message(); string code = null; bool inverse = false; var entries = new List<Reader>();
                        while (group.Pos < group.End) { tag = (int)group.Varint(); if (tag == 10) code = group.Str(); else if (tag == 18) entries.Add(group.Message()); else if (!site && tag == 24) inverse = group.Varint() != 0; else group.Skip(tag & 7); }
                        var fields = new Dictionary<string, List<string>>();
                        foreach (var e in entries)
                        {
                            string key, value;
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
                            if (!fields.ContainsKey(key)) fields[key] = new List<string>(); fields[key].Add(value);
                        }
                        if (code != null && !inverse) c.Groups[code] = fields.Select(kv => (object)ConfigRouting.Field(kv.Key, kv.Value.ToArray())).ToList();
                    }
                    cache[path] = c;
                }
                List<object> result;
                if (!c.Groups.TryGetValue(category, out result) || result.Count == 0) throw new FormatException("Geodata category not found: " + category);
                return result;
            }
        }
    }
}
