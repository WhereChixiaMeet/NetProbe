using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace NetProbe
{
    // Сервер из ссылки vless:// vmess:// trojan:// ss:// socks:// (как в v2rayN)
    public class ServerEntry
    {
        public string Id = "", Group = "", Name = "", Protocol = "", Address = "", UserId = "", Method = "", Flow = "", Network = "tcp", Security = "none";
        public string Sni = "", Fp = "", Pbk = "", Sid = "", Spx = "", Path = "", Host = "", Alpn = "", ServiceName = "", Mode = "", HeaderType = "", Link = "";
        public int Port, AlterId;
        public bool Favorite, Insecure;
        public int Delay = -1;            // мс; -1 = не проверялся, -2 = недоступен
        public DateTime Tested = DateTime.MinValue;

        public string Endpoint { get { return Address + ":" + Port; } }
        public string Transport { get { return Protocol + (Network != "tcp" ? "+" + Network : "") + (Security != "none" ? "+" + Security : ""); } }

        static string Q(Dictionary<string, string> q, string k) { string v; return q.TryGetValue(k, out v) ? v : ""; }

        static Dictionary<string, string> Query(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in s.Split('&'))
            {
                if (p.Length == 0) continue;
                int i = p.IndexOf('=');
                string k = i < 0 ? p : p.Substring(0, i), v = i < 0 ? "" : p.Substring(i + 1);
                d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace("+", "%20"));
            }
            return d;
        }

        public static string B64(string s)
        {
            s = s.Trim().Replace('-', '+').Replace('_', '/').Replace("\r", "").Replace("\n", "");
            while (s.Length % 4 != 0) s += "=";
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }

        static void HostPort(string hp, ServerEntry e)
        {
            int c = hp.LastIndexOf(':');
            if (hp.StartsWith("[")) { int b = hp.IndexOf(']'); e.Address = hp.Substring(1, b - 1); c = hp.IndexOf(':', b); }
            else e.Address = c > 0 ? hp.Substring(0, c) : hp;
            int port; if (c > 0 && int.TryParse(hp.Substring(c + 1).TrimEnd('/'), out port)) e.Port = port;
        }

        public static ServerEntry Parse(string link)
        {
            link = (link ?? "").Trim();
            if (link.Length == 0) return null;
            try
            {
                int sch = link.IndexOf("://", StringComparison.Ordinal); if (sch < 0) return null;
                string proto = link.Substring(0, sch).ToLowerInvariant();
                var e = new ServerEntry { Link = link };
                if (proto == "vmess")
                {
                    var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(B64(link.Substring(8)));
                    Func<string, string> g = k => j.ContainsKey(k) && j[k] != null ? Convert.ToString(j[k], CultureInfo.InvariantCulture) : "";
                    e.Protocol = "vmess"; e.Name = g("ps"); e.Address = g("add"); int.TryParse(g("port"), out e.Port); e.UserId = g("id");
                    int.TryParse(g("aid"), out e.AlterId); e.Method = g("scy") == "" ? "auto" : g("scy");
                    e.Network = g("net") == "" ? "tcp" : g("net"); e.HeaderType = g("type"); e.Host = g("host"); e.Path = g("path");
                    e.Security = g("tls") == "tls" ? "tls" : g("tls") == "reality" ? "reality" : "none"; e.Sni = g("sni"); e.Alpn = g("alpn"); e.Fp = g("fp");
                    if (e.Network == "grpc") e.ServiceName = e.Path;
                }
                else
                {
                    string rest = link.Substring(sch + 3), name = "";
                    int h = rest.IndexOf('#'); if (h >= 0) { name = Uri.UnescapeDataString(rest.Substring(h + 1)); rest = rest.Substring(0, h); }
                    string query = ""; int qm = rest.IndexOf('?'); if (qm >= 0) { query = rest.Substring(qm + 1); rest = rest.Substring(0, qm); }
                    rest = rest.TrimEnd('/');
                    var q = Query(query);
                    e.Name = name;
                    if (proto == "ss")
                    {
                        e.Protocol = "shadowsocks";
                        int at = rest.LastIndexOf('@');
                        if (at < 0) { rest = B64(rest); at = rest.LastIndexOf('@'); }
                        string cred = rest.Substring(0, at); HostPort(rest.Substring(at + 1), e);
                        if (!cred.Contains(":")) cred = B64(cred);
                        int c = cred.IndexOf(':'); e.Method = cred.Substring(0, c); e.UserId = Uri.UnescapeDataString(cred.Substring(c + 1));
                    }
                    else if (proto == "vless" || proto == "trojan" || proto == "socks" || proto == "http")
                    {
                        e.Protocol = proto;
                        int at = rest.LastIndexOf('@');
                        string user = at >= 0 ? Uri.UnescapeDataString(rest.Substring(0, at)) : "";
                        HostPort(at >= 0 ? rest.Substring(at + 1) : rest, e);
                        if (proto == "socks" && user != "" && !user.Contains(":")) try { user = B64(user); } catch { }
                        e.UserId = user;
                        e.Method = Q(q, "encryption") == "" ? "none" : Q(q, "encryption");
                        e.Flow = Q(q, "flow"); e.Network = Q(q, "type") == "" ? "tcp" : Q(q, "type");
                        e.Security = Q(q, "security") == "" ? (proto == "trojan" ? "tls" : "none") : Q(q, "security");
                        e.Sni = Q(q, "sni") != "" ? Q(q, "sni") : Q(q, "peer"); e.Fp = Q(q, "fp"); e.Pbk = Q(q, "pbk"); e.Sid = Q(q, "sid"); e.Spx = Q(q, "spx");
                        e.Path = Q(q, "path"); e.Host = Q(q, "host"); e.Alpn = Q(q, "alpn"); e.ServiceName = Q(q, "serviceName"); e.Mode = Q(q, "mode"); e.HeaderType = Q(q, "headerType");
                        e.Insecure = Q(q, "allowInsecure") == "1" || Q(q, "insecure") == "1";
                    }
                    else return null;
                }
                if (e.Address == "" || e.Port <= 0) return null;
                if (e.Name == "") e.Name = e.Endpoint;
                e.Id = MakeId(e.Link);
                return e;
            }
            catch { return null; }
        }

        public static string MakeId(string s)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        // ---- outbound для xray ----
        public Dictionary<string, object> Outbound(string tag)
        {
            var o = new Dictionary<string, object> { { "tag", tag } };
            if (Protocol == "vless" || Protocol == "vmess")
            {
                var user = new Dictionary<string, object> { { "id", UserId } };
                if (Protocol == "vless") { user["encryption"] = Method == "" ? "none" : Method; if (Flow != "") user["flow"] = Flow; }
                else { user["alterId"] = AlterId; user["security"] = Method == "" ? "auto" : Method; }
                o["protocol"] = Protocol;
                o["settings"] = new Dictionary<string, object> { { "vnext", new object[] { new Dictionary<string, object> { { "address", Address }, { "port", Port }, { "users", new object[] { user } } } } } };
            }
            else if (Protocol == "trojan")
            {
                o["protocol"] = "trojan";
                o["settings"] = new Dictionary<string, object> { { "servers", new object[] { new Dictionary<string, object> { { "address", Address }, { "port", Port }, { "password", UserId } } } } };
            }
            else if (Protocol == "shadowsocks")
            {
                o["protocol"] = "shadowsocks";
                o["settings"] = new Dictionary<string, object> { { "servers", new object[] { new Dictionary<string, object> { { "address", Address }, { "port", Port }, { "method", Method }, { "password", UserId } } } } };
            }
            else
            {
                o["protocol"] = Protocol; // socks / http
                var srv = new Dictionary<string, object> { { "address", Address }, { "port", Port } };
                int c = UserId.IndexOf(':');
                if (c > 0) srv["users"] = new object[] { new Dictionary<string, object> { { "user", UserId.Substring(0, c) }, { "pass", UserId.Substring(c + 1) } } };
                o["settings"] = new Dictionary<string, object> { { "servers", new object[] { srv } } };
            }
            if (Protocol != "shadowsocks" && Protocol != "socks" && Protocol != "http") o["streamSettings"] = Stream();
            return o;
        }

        Dictionary<string, object> Stream()
        {
            string net = Network == "" ? "tcp" : Network;
            if (net == "h2") net = "http";
            var s = new Dictionary<string, object> { { "network", net }, { "security", Security } };
            string[] hosts = Host == "" ? new string[0] : Host.Split(',');
            if (Security == "tls")
            {
                var t = new Dictionary<string, object> { { "serverName", Sni != "" ? Sni : (Host != "" ? hosts[0] : Address) }, { "allowInsecure", Insecure } };
                if (Fp != "") t["fingerprint"] = Fp;
                if (Alpn != "") t["alpn"] = Alpn.Split(',');
                s["tlsSettings"] = t;
            }
            else if (Security == "reality")
                s["realitySettings"] = new Dictionary<string, object> { { "serverName", Sni }, { "fingerprint", Fp == "" ? "chrome" : Fp }, { "publicKey", Pbk }, { "shortId", Sid }, { "spiderX", Spx } };
            switch (net)
            {
                case "ws": s["wsSettings"] = new Dictionary<string, object> { { "path", Path == "" ? "/" : Path }, { "headers", new Dictionary<string, object> { { "Host", Host } } } }; break;
                case "grpc": s["grpcSettings"] = new Dictionary<string, object> { { "serviceName", ServiceName != "" ? ServiceName : Path }, { "multiMode", Mode == "multi" } }; break;
                case "http": s["httpSettings"] = new Dictionary<string, object> { { "path", Path == "" ? "/" : Path }, { "host", hosts } }; break;
                case "xhttp": case "splithttp": s["network"] = "xhttp"; s["xhttpSettings"] = new Dictionary<string, object> { { "path", Path == "" ? "/" : Path }, { "host", Host }, { "mode", Mode == "" ? "auto" : Mode } }; break;
                case "httpupgrade": s["httpupgradeSettings"] = new Dictionary<string, object> { { "path", Path == "" ? "/" : Path }, { "host", Host } }; break;
                case "kcp": s["kcpSettings"] = new Dictionary<string, object> { { "header", new Dictionary<string, object> { { "type", HeaderType == "" ? "none" : HeaderType } } } }; break;
                case "tcp":
                    if (HeaderType == "http")
                        s["tcpSettings"] = new Dictionary<string, object> { { "header", new Dictionary<string, object> { { "type", "http" }, { "request", new Dictionary<string, object> { { "path", new[] { Path == "" ? "/" : Path } }, { "headers", new Dictionary<string, object> { { "Host", hosts } } } } } } } };
                    break;
            }
            return s;
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "link", Link }, { "group", Group }, { "fav", Favorite }, { "delay", Delay }, { "tested", Tested.Ticks }, { "name", Name } };
        }
    }

    public class Subscription { public string Name = "", Url = ""; public bool Enabled = true; public DateTime Updated = DateTime.MinValue; public string Id = ""; }

    // Хранилище серверов и подписок: config\servers.json, config\subscriptions.json
    public static class ServerStore
    {
        static readonly object lk = new object();
        public static List<ServerEntry> Servers = new List<ServerEntry>();
        public static List<Subscription> Subs = new List<Subscription>();
        public static event Action Changed;

        static string SrvFile { get { return System.IO.Path.Combine(Settings.Dir, "servers.json"); } }
        static string SubFile { get { return System.IO.Path.Combine(Settings.Dir, "subscriptions.json"); } }
        static JavaScriptSerializer Js() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

        public static void Load()
        {
            lock (lk)
            {
                Servers = new List<ServerEntry>(); Subs = new List<Subscription>();
                try
                {
                    if (File.Exists(SrvFile))
                        foreach (var d in Js().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(SrvFile, Encoding.UTF8)))
                        {
                            var e = ServerEntry.Parse(Convert.ToString(d["link"]));
                            if (e == null) continue;
                            e.Group = d.ContainsKey("group") ? Convert.ToString(d["group"]) : "";
                            e.Favorite = d.ContainsKey("fav") && Convert.ToBoolean(d["fav"]);
                            if (d.ContainsKey("delay")) e.Delay = Convert.ToInt32(d["delay"]);
                            if (d.ContainsKey("tested")) e.Tested = new DateTime(Convert.ToInt64(d["tested"]));
                            if (d.ContainsKey("name") && Convert.ToString(d["name"]) != "") e.Name = Convert.ToString(d["name"]);
                            Servers.Add(e);
                        }
                    if (File.Exists(SubFile))
                        foreach (var d in Js().Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(SubFile, Encoding.UTF8)))
                            Subs.Add(new Subscription { Id = Convert.ToString(d["id"]), Name = Convert.ToString(d["name"]), Url = Convert.ToString(d["url"]), Enabled = Convert.ToBoolean(d["enabled"]), Updated = new DateTime(Convert.ToInt64(d["updated"])) });
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, "servers.json: " + ex.Message); }
            }
        }

        public static void Save()
        {
            lock (lk)
            {
                try
                {
                    File.WriteAllText(SrvFile, Js().Serialize(Servers.Select(s => s.ToDict()).ToList()), new UTF8Encoding(false));
                    File.WriteAllText(SubFile, Js().Serialize(Subs.Select(s => new Dictionary<string, object> { { "id", s.Id }, { "name", s.Name }, { "url", s.Url }, { "enabled", s.Enabled }, { "updated", s.Updated.Ticks } }).ToList()), new UTF8Encoding(false));
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, "servers.json: " + ex.Message); }
            }
            var h = Changed; if (h != null) try { h(); } catch { }
        }

        public static ServerEntry Find(string id) { lock (lk) return Servers.FirstOrDefault(s => s.Id == id); }
        public static List<ServerEntry> Snapshot() { lock (lk) return Servers.ToList(); }

        // Ссылки из текста (буфер обмена / ответ подписки, в т.ч. base64)
        public static List<ServerEntry> ParseMany(string text)
        {
            var res = new List<ServerEntry>();
            if (string.IsNullOrWhiteSpace(text)) return res;
            string t = text.Trim();
            if (!t.Contains("://")) { try { t = ServerEntry.B64(t); } catch { } }
            foreach (var line in t.Split(new[] { '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var e = ServerEntry.Parse(line);
                if (e != null && !res.Any(x => x.Id == e.Id)) res.Add(e);
            }
            return res;
        }

        public static int AddMany(IEnumerable<ServerEntry> list, string group)
        {
            int n = 0;
            lock (lk)
                foreach (var e in list)
                {
                    if (Servers.Any(x => x.Id == e.Id)) continue;
                    e.Group = group; Servers.Add(e); n++;
                }
            Save();
            return n;
        }

        public static void Remove(IEnumerable<string> ids) { var set = new HashSet<string>(ids); lock (lk) Servers.RemoveAll(s => set.Contains(s.Id)); Save(); }

        public static string Download(string url, int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "v2rayN/7.0 NetProbe"; rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs;
            rq.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            using (var r = rq.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        // Обновить подписку: серверы группы заменяются, избранное и замеры сохраняются
        public static string UpdateSub(Subscription s)
        {
            try
            {
                var list = ParseMany(Download(s.Url, 20000));
                if (list.Count == 0) return s.Name + ": 0";
                lock (lk)
                {
                    var old = Servers.Where(x => x.Group == s.Id).ToDictionary(x => x.Id);
                    Servers.RemoveAll(x => x.Group == s.Id);
                    foreach (var e in list)
                    {
                        ServerEntry o; e.Group = s.Id;
                        if (old.TryGetValue(e.Id, out o)) { e.Favorite = o.Favorite; e.Delay = o.Delay; e.Tested = o.Tested; }
                        Servers.Add(e);
                    }
                    s.Updated = DateTime.Now;
                }
                Save();
                return s.Name + ": " + list.Count;
            }
            catch (Exception ex) { return s.Name + ": " + ex.Message; }
        }

        public static string GroupName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            var s = Subs.FirstOrDefault(x => x.Id == id); return s != null ? s.Name : id;
        }
    }

    // Генерация конфига xray и запуск процесса
    public static class XrayConfig
    {
        public static string Build(Settings st, ServerEntry srv)
        {
            var inbounds = new List<object>
            {
                new Dictionary<string, object> { { "tag", "socks" }, { "port", st.SocksPort }, { "listen", st.AllowLan ? "0.0.0.0" : "127.0.0.1" }, { "protocol", "socks" },
                    { "settings", new Dictionary<string, object> { { "udp", true }, { "auth", "noauth" } } }, { "sniffing", new Dictionary<string, object> { { "enabled", true }, { "destOverride", new[] { "http", "tls", "quic" } }, { "routeOnly", true } } } },
                new Dictionary<string, object> { { "tag", "http" }, { "port", st.HttpPort }, { "listen", st.AllowLan ? "0.0.0.0" : "127.0.0.1" }, { "protocol", "http" }, { "settings", new Dictionary<string, object>() } }
            };
            var rules = new List<object>();
            rules.Add(new Dictionary<string, object> { { "type", "field" }, { "ip", new[] { "geoip:private" } }, { "outboundTag", "direct" } });
            if (st.BypassRu)
            {
                rules.Add(new Dictionary<string, object> { { "type", "field" }, { "domain", new[] { "geosite:category-ru", "domain:ru", "domain:su", "domain:xn--p1ai" } }, { "outboundTag", "direct" } });
                rules.Add(new Dictionary<string, object> { { "type", "field" }, { "ip", new[] { "geoip:ru" } }, { "outboundTag", "direct" } });
            }
            if (st.BlockAds) rules.Add(new Dictionary<string, object> { { "type", "field" }, { "domain", new[] { "geosite:category-ads-all" } }, { "outboundTag", "block" } });
            var cfg = new Dictionary<string, object>
            {
                { "log", new Dictionary<string, object> { { "loglevel", "warning" } } },
                { "inbounds", inbounds },
                { "outbounds", new object[] { srv.Outbound("proxy"), new Dictionary<string, object> { { "tag", "direct" }, { "protocol", "freedom" } }, new Dictionary<string, object> { { "tag", "block" }, { "protocol", "blackhole" } } } },
                { "routing", new Dictionary<string, object> { { "domainStrategy", "IPIfNonMatch" }, { "rules", rules } } }
            };
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(cfg);
        }

        // Конфиг для замера задержки: на каждый сервер свой socks-порт
        public static string BuildTest(List<ServerEntry> list, int basePort)
        {
            var inb = new List<object>(); var outb = new List<object>(); var rules = new List<object>();
            for (int i = 0; i < list.Count; i++)
            {
                inb.Add(new Dictionary<string, object> { { "tag", "in" + i }, { "port", basePort + i }, { "listen", "127.0.0.1" }, { "protocol", "socks" }, { "settings", new Dictionary<string, object> { { "udp", false } } } });
                outb.Add(list[i].Outbound("out" + i));
                rules.Add(new Dictionary<string, object> { { "type", "field" }, { "inboundTag", new[] { "in" + i } }, { "outboundTag", "out" + i } });
            }
            var cfg = new Dictionary<string, object> { { "log", new Dictionary<string, object> { { "loglevel", "none" } } }, { "inbounds", inb }, { "outbounds", outb }, { "routing", new Dictionary<string, object> { { "rules", rules } } } };
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(cfg);
        }
    }

    public class XrayProcess
    {
        Process p;
        public event Action<string> Output;
        public event Action<int> Exited;
        public bool Running { get { try { return p != null && !p.HasExited; } catch { return false; } } }

        public static string Exe(Settings st) { return System.IO.Path.Combine(st.XrayPath, "xray.exe"); }

        public string Start(Settings st, string json, string cfgFile)
        {
            Stop();
            string exe = Exe(st);
            if (!File.Exists(exe)) return Tr.Tx("Не найден xray.exe — обновите ядро (кнопка «Ядро xray»)");
            File.WriteAllText(cfgFile, json, new UTF8Encoding(false));
            var psi = new ProcessStartInfo(exe, "run -c \"" + cfgFile + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = st.XrayPath };
            psi.EnvironmentVariables["XRAY_LOCATION_ASSET"] = st.XrayPath;
            try
            {
                var pr = new Process { StartInfo = psi, EnableRaisingEvents = true };
                pr.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { var h = Output; if (h != null) h(e.Data.Trim()); } };
                pr.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { var h = Output; if (h != null) h(e.Data.Trim()); } };
                pr.Exited += (s, e) => { int c = -1; try { c = pr.ExitCode; } catch { } if (pr == p) { var h = Exited; if (h != null) h(c); } };
                pr.Start();
                p = pr;
                return null;
            }
            catch (Exception ex) { return "xray: " + ex.Message; }
        }

        public void Attach(Process pr)
        {
            pr.EnableRaisingEvents = true;
            pr.Exited += (s, e) => { int c = -1; try { c = pr.ExitCode; } catch { } if (pr == p) { var h = Exited; if (h != null) h(c); } };
            p = pr;
        }

        public void Stop()
        {
            var pr = p; p = null;
            if (pr == null) return;
            try { if (!pr.HasExited) { pr.Kill(); pr.WaitForExit(3000); } } catch { }
            try { pr.Dispose(); } catch { }
        }
    }

    // Замер реальной задержки серверов через xray (как «Real delay» в v2rayN)
    public static class DelayTester
    {
        public static int ViaProxy(string curl, int port, string url, int timeoutSec)
        {
            var r = Proc.Run(curl, "-s -o NUL --ssl-no-revoke -x socks5h://127.0.0.1:" + port + " --connect-timeout " + timeoutSec + " --max-time " + timeoutSec + " -w \"%{http_code} %{time_total}\" \"" + url + "\"", timeoutSec * 1000 + 4000);
            var parts = r.Out.Trim().Split(' ');
            int code; double t;
            if (r.Exit == 0 && parts.Length == 2 && int.TryParse(parts[0], out code) && code > 0 && code < 500 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out t))
                return (int)Math.Max(1, t * 1000);
            return -2;
        }

        public static void TestMany(Settings st, string curl, List<ServerEntry> list, Action<ServerEntry> done, CancelFlag cf)
        {
            const int chunk = 40;
            for (int off = 0; off < list.Count; off += chunk)
            {
                if (cf != null && cf.Cancelled) return;
                var part = list.Skip(off).Take(chunk).ToList();
                int basePort = 21800;
                var x = new XrayProcess();
                string err = x.Start(st, XrayConfig.BuildTest(part, basePort), System.IO.Path.Combine(Settings.Sub("runtime"), "xray-test.json"));
                if (err != null) { foreach (var s in part) { s.Delay = -2; done(s); } Logger.Write(LogLevel.Error, err); return; }
                Thread.Sleep(700);
                int next = -1;
                var threads = new List<Thread>();
                for (int t = 0; t < Math.Min(8, part.Count); t++)
                {
                    var th = new Thread(() =>
                    {
                        while (true)
                        {
                            int i = Interlocked.Increment(ref next);
                            if (i >= part.Count || (cf != null && cf.Cancelled)) return;
                            part[i].Delay = ViaProxy(curl, basePort + i, st.TestUrl, st.AsTimeout);
                            part[i].Tested = DateTime.Now;
                            done(part[i]);
                        }
                    }) { IsBackground = true };
                    th.Start(); threads.Add(th);
                }
                foreach (var th in threads) th.Join();
                x.Stop();
            }
        }
    }
}
