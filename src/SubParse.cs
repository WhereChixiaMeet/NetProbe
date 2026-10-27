using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

using static NetProbe.Tr;

namespace NetProbe
{
    // 3.0.1: ответ подписки в любом из распространённых форматов → серверы.
    //  • список ссылок (как раньше; base64 или открытым текстом);
    //  • JSON xray: массив полных конфигов (так отдают Remnawave/Marzban клиентам вроде Happ) или один конфиг;
    //  • JSON sing-box: { outbounds: [ { type: vless, server, server_port, … } ] };
    //  • YAML Clash / mihomo: proxies: [ … ].
    // Что NetProbe запустить не может (hysteria2, tuic, wireguard, зашифрованные happ://crypt …), не теряется молча — считается в Skipped.
    public class SubResult
    {
        public readonly List<ServerEntry> Servers = new List<ServerEntry>();
        public readonly SortedDictionary<string, int> Skipped = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public string Format = "links";
        public void Skip(string why) { why = string.IsNullOrEmpty(why) ? "?" : why.ToLowerInvariant(); int n; Skipped.TryGetValue(why, out n); Skipped[why] = n + 1; }
        public int SkippedCount { get { return Skipped.Values.Sum(); } }
        public string SkippedText { get { return string.Join(", ", Skipped.Select(k => k.Key + " ×" + k.Value)); } }
        public void Add(ServerEntry e) { if (e != null && !Servers.Any(x => x.Id == e.Id)) Servers.Add(e); }
    }

    public static class SubParse
    {
        static readonly string[] Supported = { "vless", "vmess", "trojan", "ss", "socks", "http" };

        public static SubResult Parse(string text)
        {
            var r = new SubResult();
            if (string.IsNullOrWhiteSpace(text)) return r;
            string t = text.Trim().TrimStart('﻿');
            if (t.StartsWith("{") || t.StartsWith("["))
            {
                object j = null;
                try { j = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(t); } catch { }
                if (j != null) { ParseJson(j, r); return r; }
            }
            if (Regex(t, @"(?m)^\s*proxies\s*:")) { r.Format = "clash"; ParseClash(t, r); return r; }
            if (!t.Contains("://")) { try { var d = ServerEntry.B64(t); if (d.Contains("://") || d.TrimStart().StartsWith("{") || d.TrimStart().StartsWith("[")) return Parse(d); } catch { } }
            foreach (var raw in t.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;
                int sch = line.IndexOf("://", StringComparison.Ordinal);
                if (sch <= 0) continue;
                string proto = line.Substring(0, sch).ToLowerInvariant();
                if (proto == "happ") { r.Skip(line.StartsWith("happ://crypt", StringComparison.OrdinalIgnoreCase) ? "happ://crypt" : "happ://"); continue; }
                if (proto == "hy2") proto = "hysteria2";
                if (!Supported.Contains(proto)) { r.Skip(proto); continue; }
                if (proto == "ss" && line.IndexOf("plugin=", StringComparison.OrdinalIgnoreCase) > 0) { r.Skip("ss+plugin"); continue; } // obfs / v2ray-plugin xray не умеет
                // пробел в имени без %20 (бывает в подписках) — оставляем всю строку; пробел до '#' — конец ссылки
                int sp = line.IndexOf(' '); int h = line.IndexOf('#');
                if (sp > 0 && (h < 0 || sp < h)) line = line.Substring(0, sp);
                var e = ServerEntry.Parse(line);
                if (e == null) r.Skip(proto + ": " + Tx("ошибка разбора")); else r.Add(e);
            }
            return r;
        }

        static bool Regex(string s, string p) { return System.Text.RegularExpressions.Regex.IsMatch(s, p); }

        // ---------- JSON (xray / sing-box) ----------
        static void ParseJson(object j, SubResult r)
        {
            var list = j as object[] ?? (j is ArrayList ? ((ArrayList)j).ToArray() : null);
            if (list != null)
            {
                r.Format = "xray-json";
                foreach (var it in list)
                {
                    var d = it as Dictionary<string, object>;
                    if (d == null) { var s = it as string; if (s != null) foreach (var e in Parse(s).Servers) r.Add(e); continue; }
                    ParseConfig(d, r);
                }
                return;
            }
            var obj = j as Dictionary<string, object>;
            if (obj == null) return;
            if (obj.ContainsKey("outbounds") && Arr(obj["outbounds"]).Any(o => o is Dictionary<string, object> && ((Dictionary<string, object>)o).ContainsKey("type") && !((Dictionary<string, object>)o).ContainsKey("protocol")))
            { r.Format = "sing-box"; foreach (var o in Arr(obj["outbounds"])) FromSingBox(o as Dictionary<string, object>, r); return; }
            if (obj.ContainsKey("protocol")) { r.Format = "xray-json"; var e = FromXray(obj, S(obj, "tag"), r); if (e != null) r.Add(e); return; }
            r.Format = "xray-json"; ParseConfig(obj, r);
        }

        // один полный конфиг xray: все proxy-outbound'ы (direct/block/dns пропускаем)
        static void ParseConfig(Dictionary<string, object> cfg, SubResult r)
        {
            string remarks = S(cfg, "remarks");
            var outs = Arr(Get(cfg, "outbounds")).OfType<Dictionary<string, object>>().ToList();
            var proxies = outs.Where(o => !new[] { "freedom", "blackhole", "dns", "loopback" }.Contains(S(o, "protocol").ToLowerInvariant())).ToList();
            foreach (var o in proxies)
            {
                string name = remarks != "" ? (proxies.Count > 1 && S(o, "tag") != "" && S(o, "tag") != "proxy" ? remarks + " · " + S(o, "tag") : remarks) : S(o, "tag");
                var e = FromXray(o, name, r);
                if (e != null) r.Add(e);
            }
        }

        static ServerEntry FromXray(Dictionary<string, object> o, string name, SubResult r)
        {
            string proto = S(o, "protocol").ToLowerInvariant();
            var set = D(o, "settings");
            var e = new ServerEntry { Name = name };
            switch (proto)
            {
                case "vless":
                case "vmess":
                    {
                        e.Protocol = proto;
                        var vn = Arr(Get(set, "vnext")).OfType<Dictionary<string, object>>().FirstOrDefault();
                        var src = vn ?? set; // новая «плоская» форма: settings { address, port, id }
                        e.Address = S(src, "address"); e.Port = I(src, "port");
                        var u = vn != null ? Arr(Get(vn, "users")).OfType<Dictionary<string, object>>().FirstOrDefault() : set;
                        if (u == null) break;
                        e.UserId = S(u, "id"); e.Flow = S(u, "flow");
                        if (proto == "vless") e.Method = S(u, "encryption") == "" ? "none" : S(u, "encryption");
                        else { e.AlterId = I(u, "alterId"); e.Method = S(u, "security") == "" ? "auto" : S(u, "security"); }
                        break;
                    }
                case "trojan":
                case "shadowsocks":
                    {
                        e.Protocol = proto;
                        var sv = Arr(Get(set, "servers")).OfType<Dictionary<string, object>>().FirstOrDefault() ?? set;
                        e.Address = S(sv, "address"); e.Port = I(sv, "port"); e.UserId = S(sv, "password"); e.Method = S(sv, "method");
                        break;
                    }
                case "socks":
                case "http":
                    {
                        e.Protocol = proto;
                        var sv = Arr(Get(set, "servers")).OfType<Dictionary<string, object>>().FirstOrDefault() ?? set;
                        e.Address = S(sv, "address"); e.Port = I(sv, "port");
                        var u = Arr(Get(sv, "users")).OfType<Dictionary<string, object>>().FirstOrDefault();
                        if (u != null) e.UserId = S(u, "user") + ":" + S(u, "pass");
                        break;
                    }
                default: r.Skip(proto); return null;
            }
            var ss = D(o, "streamSettings");
            if (ss != null && proto != "shadowsocks" && proto != "socks" && proto != "http")
            {
                e.Network = S(ss, "network") == "" ? "tcp" : S(ss, "network").ToLowerInvariant();
                if (e.Network == "raw") e.Network = "tcp";
                if (e.Network == "splithttp") e.Network = "xhttp";
                e.Security = S(ss, "security") == "" ? "none" : S(ss, "security").ToLowerInvariant();
                var tls = D(ss, "tlsSettings");
                if (tls != null) { e.Sni = S(tls, "serverName"); e.Fp = S(tls, "fingerprint"); e.Alpn = Join(Get(tls, "alpn")); e.Insecure = B(tls, "allowInsecure"); }
                var rl = D(ss, "realitySettings");
                if (rl != null) { e.Sni = S(rl, "serverName"); e.Fp = S(rl, "fingerprint"); e.Pbk = S(rl, "publicKey"); e.Sid = S(rl, "shortId"); e.Spx = S(rl, "spiderX"); }
                switch (e.Network)
                {
                    case "ws": { var w = D(ss, "wsSettings"); e.Path = S(w, "path"); e.Host = S(w, "host") != "" ? S(w, "host") : S(D(w, "headers"), "Host"); break; }
                    case "grpc": { var g = D(ss, "grpcSettings"); e.ServiceName = S(g, "serviceName"); e.Mode = B(g, "multiMode") ? "multi" : ""; break; }
                    case "xhttp": { var x = D(ss, "xhttpSettings") ?? D(ss, "splithttpSettings"); e.Path = S(x, "path"); e.Host = S(x, "host"); e.Mode = S(x, "mode"); break; }
                    case "httpupgrade": { var x = D(ss, "httpupgradeSettings"); e.Path = S(x, "path"); e.Host = S(x, "host"); break; }
                    case "http": case "h2": { var x = D(ss, "httpSettings"); e.Path = S(x, "path"); e.Host = Join(Get(x, "host")); e.Network = "h2"; break; }
                    case "kcp": { e.HeaderType = S(D(D(ss, "kcpSettings"), "header"), "type"); break; }
                    case "tcp":
                        {
                            var hd = D(D(ss, "tcpSettings") ?? D(ss, "rawSettings"), "header");
                            if (S(hd, "type") == "http") { e.HeaderType = "http"; var rq = D(hd, "request"); e.Path = Join(Get(rq, "path")); e.Host = Join(Get(D(rq, "headers"), "Host")); }
                            break;
                        }
                }
            }
            return Finish(e, r, proto);
        }

        static void FromSingBox(Dictionary<string, object> o, SubResult r)
        {
            if (o == null) return;
            string type = S(o, "type").ToLowerInvariant();
            if (new[] { "direct", "block", "dns", "selector", "urltest" }.Contains(type)) return;
            var e = new ServerEntry { Name = S(o, "tag"), Address = S(o, "server"), Port = I(o, "server_port") };
            switch (type)
            {
                case "vless": e.Protocol = "vless"; e.UserId = S(o, "uuid"); e.Flow = S(o, "flow"); e.Method = "none"; break;
                case "vmess": e.Protocol = "vmess"; e.UserId = S(o, "uuid"); e.AlterId = I(o, "alter_id"); e.Method = S(o, "security") == "" ? "auto" : S(o, "security"); break;
                case "trojan": e.Protocol = "trojan"; e.UserId = S(o, "password"); break;
                case "shadowsocks": e.Protocol = "shadowsocks"; e.Method = S(o, "method"); e.UserId = S(o, "password"); break;
                case "socks": e.Protocol = "socks"; if (S(o, "username") != "") e.UserId = S(o, "username") + ":" + S(o, "password"); break;
                case "http": e.Protocol = "http"; if (S(o, "username") != "") e.UserId = S(o, "username") + ":" + S(o, "password"); break;
                default: r.Skip(type); return;
            }
            var tls = D(o, "tls");
            if (tls != null && B(tls, "enabled"))
            {
                e.Security = "tls"; e.Sni = S(tls, "server_name"); e.Insecure = B(tls, "insecure"); e.Alpn = Join(Get(tls, "alpn")); e.Fp = S(D(tls, "utls"), "fingerprint");
                var rl = D(tls, "reality");
                if (rl != null && B(rl, "enabled")) { e.Security = "reality"; e.Pbk = S(rl, "public_key"); e.Sid = S(rl, "short_id"); if (e.Fp == "") e.Fp = "chrome"; }
            }
            var tr = D(o, "transport");
            if (tr != null)
            {
                string tt = S(tr, "type").ToLowerInvariant();
                if (tt == "ws") { e.Network = "ws"; e.Path = S(tr, "path"); e.Host = S(D(tr, "headers"), "Host"); }
                else if (tt == "grpc") { e.Network = "grpc"; e.ServiceName = S(tr, "service_name"); }
                else if (tt == "http") { e.Network = "h2"; e.Path = S(tr, "path"); e.Host = Join(Get(tr, "host")); }
                else if (tt == "httpupgrade") { e.Network = "httpupgrade"; e.Path = S(tr, "path"); e.Host = S(tr, "host"); }
                else if (tt != "") { r.Skip(type + "+" + tt); return; }
            }
            var f = Finish(e, r, type); if (f != null) r.Add(f);
        }

        // ---------- Clash / mihomo YAML ----------
        static void ParseClash(string text, SubResult r)
        {
            var lines = text.Replace("\r", "").Split('\n');
            int i = 0;
            while (i < lines.Length && !System.Text.RegularExpressions.Regex.IsMatch(lines[i], @"^\s*proxies\s*:")) i++;
            if (i >= lines.Length) return;
            int baseIndent = Indent(lines[i]);
            string after = lines[i].Substring(lines[i].IndexOf(':') + 1).Trim();
            List<object> items;
            if (after.StartsWith("[")) items = (FlowValue(after) as List<object>) ?? new List<object>();
            else
            {
                i++;
                var block = new List<string>();
                for (; i < lines.Length; i++)
                {
                    var l = lines[i];
                    if (l.Trim().Length == 0 || l.TrimStart().StartsWith("#")) continue;
                    if (Indent(l) <= baseIndent && !l.TrimStart().StartsWith("-")) break;
                    if (Indent(l) < baseIndent) break;
                    block.Add(l);
                }
                int k = 0;
                items = (YBlock(block, ref k, block.Count > 0 ? Indent(block[0]) : 0) as List<object>) ?? new List<object>();
            }
            foreach (var it in items) FromClash(it as Dictionary<string, object>, r);
        }

        static void FromClash(Dictionary<string, object> p, SubResult r)
        {
            if (p == null) return;
            string type = S(p, "type").ToLowerInvariant();
            var e = new ServerEntry { Name = S(p, "name"), Address = S(p, "server"), Port = I(p, "port") };
            switch (type)
            {
                case "vless": e.Protocol = "vless"; e.UserId = S(p, "uuid"); e.Flow = S(p, "flow"); e.Method = "none"; break;
                case "vmess": e.Protocol = "vmess"; e.UserId = S(p, "uuid"); e.AlterId = I(p, "alterId"); e.Method = S(p, "cipher") == "" ? "auto" : S(p, "cipher"); break;
                case "trojan": e.Protocol = "trojan"; e.UserId = S(p, "password"); e.Security = "tls"; break;
                case "ss": if (S(p, "plugin") != "") { r.Skip("ss+" + S(p, "plugin")); return; } e.Protocol = "shadowsocks"; e.Method = S(p, "cipher"); e.UserId = S(p, "password"); break;
                case "socks5": e.Protocol = "socks"; if (S(p, "username") != "") e.UserId = S(p, "username") + ":" + S(p, "password"); break;
                case "http": e.Protocol = "http"; if (S(p, "username") != "") e.UserId = S(p, "username") + ":" + S(p, "password"); break;
                default: r.Skip(type); return;
            }
            if (B(p, "tls") || type == "trojan") e.Security = "tls";
            e.Sni = S(p, "servername") != "" ? S(p, "servername") : S(p, "sni");
            e.Insecure = B(p, "skip-cert-verify"); e.Fp = S(p, "client-fingerprint"); e.Alpn = Join(Get(p, "alpn"));
            var ro = D(p, "reality-opts");
            if (ro != null) { e.Security = "reality"; e.Pbk = S(ro, "public-key"); e.Sid = S(ro, "short-id"); if (e.Fp == "") e.Fp = "chrome"; }
            string net = S(p, "network").ToLowerInvariant();
            if (net == "ws") { e.Network = "ws"; var w = D(p, "ws-opts"); e.Path = S(w, "path"); e.Host = S(D(w, "headers"), "Host"); }
            else if (net == "grpc") { e.Network = "grpc"; e.ServiceName = S(D(p, "grpc-opts"), "grpc-service-name"); }
            else if (net == "h2") { e.Network = "h2"; var h = D(p, "h2-opts"); e.Path = S(h, "path"); e.Host = Join(Get(h, "host")); }
            else if (net == "http") { e.HeaderType = "http"; var h = D(p, "http-opts"); e.Path = Join(Get(h, "path")); e.Host = Join(Get(D(h, "headers"), "Host")); }
            else if (net == "httpupgrade") { e.Network = "httpupgrade"; var w = D(p, "ws-opts"); e.Path = S(w, "path"); e.Host = S(D(w, "headers"), "Host"); }
            else if (net != "" && net != "tcp") { r.Skip(type + "+" + net); return; }
            var f = Finish(e, r, type); if (f != null) r.Add(f);
        }

        // ---------- общая часть: проверка, имя, ссылка (серверы хранятся ссылками) ----------
        static ServerEntry Finish(ServerEntry e, SubResult r, string proto)
        {
            if (string.IsNullOrEmpty(e.Address) || e.Port <= 0 || e.Port > 65535) { r.Skip(proto + ": " + Tx("нет адреса или порта")); return null; }
            if (string.IsNullOrEmpty(e.Name)) e.Name = e.Endpoint;
            e.Link = ToLink(e);
            var back = ServerEntry.Parse(e.Link); // проверка: ссылка читается обратно так же
            if (back == null) { r.Skip(proto + ": " + Tx("ошибка разбора")); return null; }
            back.Name = e.Name; back.Country = ServerEntry.FlagCountry(e.Name);
            return back;
        }

        static string U(string s) { return Uri.EscapeDataString(s ?? ""); }

        public static string ToLink(ServerEntry e)
        {
            string host = e.Address.Contains(":") && !e.Address.StartsWith("[") ? "[" + e.Address + "]" : e.Address;
            if (e.Protocol == "vmess")
            {
                var j = new Dictionary<string, object> { { "v", "2" }, { "ps", e.Name }, { "add", e.Address }, { "port", e.Port.ToString(CultureInfo.InvariantCulture) }, { "id", e.UserId },
                    { "aid", e.AlterId.ToString(CultureInfo.InvariantCulture) }, { "scy", e.Method == "" ? "auto" : e.Method }, { "net", e.Network == "" ? "tcp" : e.Network }, { "type", e.HeaderType == "" ? "none" : e.HeaderType },
                    { "host", e.Host }, { "path", e.Network == "grpc" ? e.ServiceName : e.Path }, { "tls", e.Security == "none" ? "" : e.Security }, { "sni", e.Sni }, { "alpn", e.Alpn }, { "fp", e.Fp } };
                return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(j)));
            }
            if (e.Protocol == "shadowsocks")
                return "ss://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(e.Method + ":" + e.UserId)).TrimEnd('=') + "@" + host + ":" + e.Port + "#" + U(e.Name);
            if (e.Protocol == "socks" || e.Protocol == "http")
                return e.Protocol + "://" + (e.UserId != "" ? (e.Protocol == "socks" ? Convert.ToBase64String(Encoding.UTF8.GetBytes(e.UserId)) : U(e.UserId)) + "@" : "") + host + ":" + e.Port + "#" + U(e.Name);
            var q = new List<string>();
            Action<string, string> add = (k, v) => { if (!string.IsNullOrEmpty(v)) q.Add(k + "=" + U(v)); };
            if (e.Protocol == "vless") add("encryption", e.Method == "" ? "none" : e.Method);
            add("flow", e.Flow); add("security", e.Security == "" ? "none" : e.Security); add("sni", e.Sni); add("fp", e.Fp); add("pbk", e.Pbk); add("sid", e.Sid); add("spx", e.Spx);
            add("type", e.Network == "" ? "tcp" : e.Network); add("path", e.Path); add("host", e.Host); add("serviceName", e.ServiceName); add("mode", e.Mode); add("headerType", e.HeaderType); add("alpn", e.Alpn);
            if (e.Insecure) q.Add("allowInsecure=1");
            return e.Protocol + "://" + U(e.UserId) + "@" + host + ":" + e.Port + "?" + string.Join("&", q) + "#" + U(e.Name);
        }

        // ---------- мелкие помощники ----------
        static object Get(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) ? v : null; }
        static Dictionary<string, object> D(Dictionary<string, object> d, string k) { return Get(d, k) as Dictionary<string, object>; }
        static string S(Dictionary<string, object> d, string k)
        {
            var v = Get(d, k); if (v == null) return "";
            var a = v as IEnumerable; if (a != null && !(v is string)) return Join(v);
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        static int I(Dictionary<string, object> d, string k) { int n; return int.TryParse(S(d, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : 0; }
        static bool B(Dictionary<string, object> d, string k) { var s = S(d, k).ToLowerInvariant(); return s == "true" || s == "1" || s == "yes"; }
        static IEnumerable<object> Arr(object v) { var a = v as IEnumerable; if (a == null || v is string || v is IDictionary) return Enumerable.Empty<object>(); return a.Cast<object>(); }
        static string Join(object v)
        {
            if (v == null) return "";
            if (v is string) return (string)v;
            return string.Join(",", Arr(v).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)));
        }

        // ---------- минимальный YAML (подмножество, которого хватает для proxies: у Clash) ----------
        static int Indent(string l) { int n = 0; while (n < l.Length && l[n] == ' ') n++; return n; }

        static object YBlock(List<string> lines, ref int i, int indent)
        {
            if (i >= lines.Count) return null;
            if (lines[i].Substring(Indent(lines[i])).StartsWith("-"))
            {
                var list = new List<object>();
                while (i < lines.Count && Indent(lines[i]) == indent && lines[i].Substring(indent).StartsWith("-"))
                {
                    string rest = lines[i].Substring(indent + 1).TrimStart();
                    int itemIndent = indent + 1 + (lines[i].Length - indent - 1 - lines[i].Substring(indent + 1).TrimStart().Length);
                    if (rest == "") { i++; list.Add(i < lines.Count && Indent(lines[i]) > indent ? YBlock(lines, ref i, Indent(lines[i])) : null); continue; }
                    if (rest.StartsWith("{") || rest.StartsWith("[") || KeyPos(rest) < 0) { list.Add(FlowValue(rest)); i++; continue; }
                    // "- key: value" + продолжение с отступом itemIndent
                    var sub = new List<string> { new string(' ', itemIndent) + rest };
                    i++;
                    while (i < lines.Count && Indent(lines[i]) > indent) { sub.Add(lines[i]); i++; }
                    int k = 0; list.Add(YBlock(sub, ref k, itemIndent));
                }
                return list;
            }
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (i < lines.Count && Indent(lines[i]) == indent && !lines[i].Substring(indent).StartsWith("-"))
            {
                string l = lines[i].Substring(indent);
                int c = KeyPos(l); if (c < 0) { i++; continue; }
                string key = Unq(l.Substring(0, c).Trim()), val = StripComment(l.Substring(c + 1).Trim());
                i++;
                if (val == "" && i < lines.Count && (Indent(lines[i]) > indent || (Indent(lines[i]) == indent && lines[i].Substring(indent).StartsWith("-"))))
                    map[key] = YBlock(lines, ref i, Indent(lines[i]));
                else map[key] = FlowValue(val);
            }
            return map;
        }

        static int KeyPos(string s)
        {
            bool q1 = false, q2 = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\'' && !q2) q1 = !q1; else if (ch == '"' && !q1) q2 = !q2;
                else if (ch == ':' && !q1 && !q2 && (i + 1 == s.Length || s[i + 1] == ' ')) return i;
                else if ((ch == '{' || ch == '[') && !q1 && !q2) return -1;
            }
            return -1;
        }

        static string StripComment(string s)
        {
            bool q1 = false, q2 = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\'' && !q2) q1 = !q1; else if (ch == '"' && !q1) q2 = !q2;
                else if (ch == '#' && !q1 && !q2 && (i == 0 || s[i - 1] == ' ')) return s.Substring(0, i).TrimEnd();
            }
            return s;
        }

        static string Unq(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\''))) s = s.Substring(1, s.Length - 2);
            return s;
        }

        // { a: 1, b: { c: d }, e: [x, y] } / [ … ] / скаляр
        static object FlowValue(string s)
        {
            s = s.Trim(); int i = 0;
            return Flow(s, ref i);
        }

        static object Flow(string s, ref int i)
        {
            while (i < s.Length && s[i] == ' ') i++;
            if (i >= s.Length) return "";
            if (s[i] == '{')
            {
                i++; var m = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                while (i < s.Length)
                {
                    while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    string key = Scalar(s, ref i, true);
                    while (i < s.Length && s[i] == ' ') i++;
                    if (i < s.Length && s[i] == ':') i++;
                    m[Unq(key)] = Flow(s, ref i);
                }
                return m;
            }
            if (s[i] == '[')
            {
                i++; var l = new List<object>();
                while (i < s.Length)
                {
                    while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    l.Add(Flow(s, ref i));
                }
                return l;
            }
            return Unq(Scalar(s, ref i, false));
        }

        static string Scalar(string s, ref int i, bool key)
        {
            var sb = new StringBuilder();
            if (i < s.Length && (s[i] == '"' || s[i] == '\''))
            {
                char q = s[i++];
                while (i < s.Length && s[i] != q) { if (s[i] == '\\' && q == '"' && i + 1 < s.Length) i++; sb.Append(s[i++]); }
                i++;
                return sb.ToString();
            }
            while (i < s.Length)
            {
                char ch = s[i];
                if (ch == ',' || ch == '}' || ch == ']') break;
                if (key && ch == ':' && (i + 1 >= s.Length || s[i + 1] == ' ')) break;
                sb.Append(ch); i++;
            }
            return sb.ToString().Trim();
        }
    }
}
