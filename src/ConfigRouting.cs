using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace NetProbe
{
    // Only used to inspect the active Xray routing stage; not a user-configurable rules engine.
    public sealed class ConfigRouteRule
    {
        public string Kind, Values, Outbound;
    }
    public static class ConfigRouting
    {
        public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }; }
        public static Dictionary<string, object> Field(string key, object value) { return new Dictionary<string, object> { { key, value } }; }
        public static Dictionary<string, object> Logical(string mode, IEnumerable<object> rules) { return new Dictionary<string, object> { { "type", "logical" }, { "mode", mode }, { "rules", rules.ToArray() } }; }
        public static Dictionary<string, object> Matcher(ConfigRouteRule r, Settings st)
        {
            if (r.Kind != "domain" && r.Kind != "ip") throw new FormatException("Unsupported runtime rule.");
            var matches = new List<object>();
            foreach (var val in (r.Values ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
            {
                if (r.Kind == "ip")
                {
                    if (val.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)) matches.AddRange(GeoData.Read(st, false, val.Substring(6)));
                    else { var cidr = Cidr.Parse(val); if (cidr == null) throw new FormatException("Invalid runtime IP rule."); matches.Add(Field("ip_cidr", new[] { val })); }
                }
                else if (val.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase)) matches.AddRange(GeoData.Read(st, true, val.Substring(8)));
                else
                {
                    int i = val.IndexOf(':'); string kind = i < 0 ? "keyword" : val.Substring(0, i), value = i < 0 ? val : val.Substring(i + 1);
                    string field = kind == "domain" ? "domain_suffix" : kind == "full" ? "domain" : kind == "keyword" ? "domain_keyword" : kind == "regexp" ? "domain_regex" : null;
                    if (field == null || value.Length == 0) throw new FormatException("Unsupported runtime domain rule.");
                    matches.Add(Field(field, new[] { value }));
                }
            }
            if (matches.Count == 0) throw new FormatException("Empty runtime rule.");
            return Logical("or", matches);
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
