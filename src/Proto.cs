using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

using static NetProbe.Tr;

namespace NetProbe
{
    // Фаза 2 (ТЗ, раздел 11): протокол — свойство конфига туннеля. WireGuard — как раньше (config\tunnels\<имя>.conf → WireSock),
    // VLESS / VMess / Trojan — config\tunnels\<имя>.json → уже встроенные xray + sing-box TUN (process_path_regex).
    // Сверху всё одинаково: те же состояния Router, kill switch, авто-failover на DPI, гибрид и источники.
    public static class TunnelProto
    {
        public static readonly string[] Names = { "WireGuard", "VLESS", "VMess", "Trojan" };
        public static readonly string[] Ids = { "wireguard", "vless", "vmess", "trojan" };

        public static string JsonPath(string name) { return Path.Combine(Tunnels.Dir, Tunnels.Safe(name) + ".json"); }
        // движок туннеля для подписей: «VLESS · xray» / «VMess · xray» / «Trojan · xray» или «WireSock» (WireGuard)
        public static string EngineName(string name) { return IsXray(name) ? Names[IndexOf(name)] + " · xray" : "WireSock"; }
        // нужен ли WireSock: основной или включённый резервный туннель — WireGuard
        public static bool NeedsWiresock(Settings st) { return (!string.IsNullOrWhiteSpace(st.PrimaryTunnel) && !IsXray(st.PrimaryTunnel)) || (st.BackupEnabled && !string.IsNullOrWhiteSpace(st.BackupTunnel) && !IsXray(st.BackupTunnel)); }
        public static bool IsXray(string name) { return !string.IsNullOrWhiteSpace(name) && !File.Exists(Tunnels.PathOf(name)) && File.Exists(JsonPath(name)); }
        public static int IndexOf(string name) { if (!IsXray(name)) return 0; var e = Load(name); int i = e == null ? 1 : Array.IndexOf(Ids, e.Protocol); return i < 1 ? 1 : i; }

        static IEnumerable<FieldInfo> Fields()
        {
            string[] skip = { "Id", "Group", "Favorite", "Delay", "Tested", "Country", "Error", "SpeedErr", "Speed", "TotalDown", "TotalUp" };
            return typeof(ServerEntry).GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => !skip.Contains(f.Name) && (f.FieldType == typeof(string) || f.FieldType == typeof(int) || f.FieldType == typeof(bool)));
        }

        public static ServerEntry Load(string name)
        {
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(JsonPath(name), Encoding.UTF8));
                var e = new ServerEntry();
                foreach (var f in Fields())
                {
                    object v; if (!d.TryGetValue(f.Name, out v) || v == null) continue;
                    if (f.FieldType == typeof(string)) f.SetValue(e, Convert.ToString(v));
                    else if (f.FieldType == typeof(int)) f.SetValue(e, Convert.ToInt32(v));
                    else if (f.FieldType == typeof(bool)) f.SetValue(e, Convert.ToBoolean(v));
                }
                if (e.Name == "") e.Name = name;
                return e;
            }
            catch { return null; }
        }

        public static void Save(string name, ServerEntry e)
        {
            var d = new Dictionary<string, object>();
            foreach (var f in Fields()) d[f.Name] = f.GetValue(e);
            File.WriteAllText(JsonPath(name), new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
        }

        // Чего не хватает для подключения (null — всё заполнено)
        public static string Missing(ServerEntry e)
        {
            if (e == null) return Tx("конфиг не читается");
            var m = new List<string>();
            if (string.IsNullOrWhiteSpace(e.Address)) m.Add(Tx("адрес"));
            if (e.Port <= 0 || e.Port > 65535) m.Add(Tx("порт"));
            if (string.IsNullOrWhiteSpace(e.UserId)) m.Add(e.Protocol == "trojan" ? Tx("пароль") : "UUID");
            if (e.Security == "reality" && string.IsNullOrWhiteSpace(e.Pbk)) m.Add("publicKey");
            if (e.Security == "reality" && string.IsNullOrWhiteSpace(e.Sni)) m.Add("SNI");
            return m.Count == 0 ? null : Tx("не заполнены поля: ") + string.Join(", ", m);
        }

        // ссылка vless:// vmess:// trojan:// (ss:// — опционально) → поля; битая ссылка → понятная ошибка, без падения
        public static ServerEntry FromLink(string link, out string err)
        {
            err = null;
            link = (link ?? "").Trim();
            if (!(link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)))
            { err = Tx("Нужна ссылка vless://, vmess:// или trojan://"); return null; }
            try
            {
                var e = ServerEntry.Parse(link);
                if (e == null || string.IsNullOrEmpty(e.Address) || e.Port <= 0) { err = Tx("Ссылка не разобрана: нет адреса или порта"); return null; }
                return e;
            }
            catch (Exception ex) { err = Tx("Ссылка не разобрана: ") + ex.Message; return null; }
        }

        public static string Endpoint(string name) { var e = Load(name); return e != null ? e.Protocol.ToUpperInvariant() + " · " + e.Endpoint : ""; }
    }

    // Туннель xray-семейства: xray (socks на своём порту) + sing-box TUN со списками приложений Routing.
    // Снаружи ведёт себя как WiresockRunner: Running / Exited / Stop / TunnelName.
    public class XrayTunnel
    {
        readonly XrayProcess x = new XrayProcess();
        readonly SingBoxProcess sb = new SingBoxProcess();
        public string TunnelName = "";
        public DateTime StartedAt;
        public event Action<int> Exited;
        public event Action<string> Output;
        volatile bool stopping;
        string runningXrayJson; // конфиг запущенного xray туннеля — чтобы при смене списков не трогать xray, если он тот же

        public XrayTunnel()
        {
            x.Exited += c => { if (!stopping) { var h = Exited; if (h != null) h(c); } };
            sb.Exited += c => { if (!stopping) { var h = Exited; if (h != null) h(c); } };
            sb.Output += s => { if (s.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0) { var h = Output; if (h != null) h("[sing-box] " + s); } };
        }

        public bool Running { get { return x.Running && sb.Running; } }
        public int Pid { get { return x.Pid; } }

        public static int Port(Settings st) { return st.TunnelSocksPort; }

        // Порт туннеля занят: зависший xray.exe из папки NetProbe (не этого запуска) — закрываем;
        // чужая программа или xray подписок этого запуска — понятная ошибка вместо «xray сразу завершился»
        static string FreePorts(Settings st, int[] ports)
        {
            foreach (int port in ports)
                foreach (int pid in ProcInfo.ListenerPids(port))
                {
                    string path = "";
                    try { path = ProcInfo.ImagePath(pid); } catch { }
                    bool ourCore = path != "" && path.StartsWith(st.XrayPath, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(path), "xray.exe", StringComparison.OrdinalIgnoreCase);
                    if (ourCore && !XrayProcess.IsOurs(pid))
                    {
                        try { using (var p = Process.GetProcessById(pid)) { p.Kill(); p.WaitForExit(3000); } } catch { }
                        Logger.Write(LogLevel.Warn, Tx("Зависший xray.exe RouteX от прошлого запуска держал порт туннеля ") + port + " (PID " + pid + Tx(") — закрыт"));
                        continue;
                    }
                    string who = path != "" ? Path.GetFileName(path) : "PID " + pid;
                    return Tx("Порт туннеля ") + port + Tx(" занят: ") + who + " (PID " + pid + Tx(") — закрой эту программу или поменяй TunnelSocksPort в config\\settings.ini");
                }
            return null;
        }

        public string Start(Settings st, string name, TunLists lists)
        {
            Stop();
            var e = TunnelProto.Load(name);
            string miss = TunnelProto.Missing(e);
            if (miss != null) return Tx("Туннель «") + name + "»: " + miss;
            if (!File.Exists(XrayProcess.Exe(st))) return Tx("Не найден xray.exe — установите ядро на вкладке Subscriptions («Ядро xray»)");
            if (!File.Exists(SingBoxProcess.Exe(st))) return Tx("Не найден sing-box.exe — установите ядро TUN на вкладке Subscriptions («sing-box»)");
            // свои порты, чтобы не мешать xray подписок; DirectDomains / статистика здесь не нужны
            var cs = TunSettings(st);
            cs.TunServerDomain = e.Address;
            string sbjson;
            try { sbjson = SingBoxConfig.Build(cs, lists, SingLog(st)); }
            catch (Exception ex) { return "Invalid TUN routing configuration: " + ex.Message; }
            string busy = FreePorts(st, new[] { cs.SocksPort, cs.HttpPort });
            if (busy != null) return busy;
            RotateLogs(st);
            string xjson = TunXrayJson(st, cs, e);
            string err = x.Start(cs, xjson, Path.Combine(Settings.Sub("runtime"), "xray-tunnel.json"));
            if (err != null) return err;
            System.Threading.Thread.Sleep(400);
            if (!x.Running) return Tx("xray туннеля сразу завершился — проверь поля конфига (лог xray)");
            err = sb.Start(cs, sbjson, "singbox-tunnel.json");
            if (err != null) { x.Stop(); return err; }
            TunnelName = name; StartedAt = DateTime.Now; stopping = false; runningXrayJson = xjson;
            WriteRuntime(name, e, lists);
            return null;
        }

        // свои порты, чтобы не мешать xray подписок; DirectDomains / статистика здесь не нужны
        // логи движков туннеля в папке логов RouteX: вывод процессов не перехватывается (они переживают обновление RouteX)
        public static string XrayLog(Settings st) { return Path.Combine(st.LogPath, "xray-tunnel.log"); }
        public static string SingLog(Settings st) { return Path.Combine(st.LogPath, "singbox-tunnel.log"); }
        static void RotateLogs(Settings st)
        {
            foreach (var f in new[] { XrayLog(st), SingLog(st) })
                try { if (File.Exists(f) && new FileInfo(f).Length > 1024 * 1024) { File.Copy(f, f + ".old", true); File.Delete(f); } } catch { }
        }
        static string TunXrayJson(Settings st, Settings cs, ServerEntry e)
        {
            string j = XrayConfig.Build(cs, e);
            string esc = XrayLog(st).Replace("\\", "\\\\").Replace("\"", "\\\"");
            return j.Replace("\"log\":{\"loglevel\":\"warning\"}", "\"log\":{\"loglevel\":\"warning\",\"error\":\"" + esc + "\"}");
        }
        // последние строки логов xray и sing-box — в общий лог при падении VPN
        public static string LogTails(Settings st, int n)
        {
            var sb = new StringBuilder();
            foreach (var f in new[] { XrayLog(st), SingLog(st) })
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    string[] lines;
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var r = new StreamReader(fs, Encoding.UTF8)) lines = r.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var recent = lines.Skip(Math.Max(0, lines.Length - n)).ToArray();
                    if (recent.Length > 0) sb.Append("\r\n  [" + Path.GetFileName(f) + "] " + string.Join("\r\n  [" + Path.GetFileName(f) + "] ", recent));
                }
                catch { }
            }
            return sb.ToString();
        }

        static Settings TunSettings(Settings st)
        {
            var cs = st.Clone(); cs.SocksPort = Port(st); cs.HttpPort = Port(st) + 1; cs.TunMode = true; cs.AllowLan = false; cs.PrimaryXrayTunnel = true;
            if (cs.RuleSetsEnabled) { cs.BypassRu = false; cs.BlockAds = false; } // the active rule set decides RU / ads
            return cs;
        }

        // Смена списков или режима «строго VPN» у того же туннеля: если конфиг xray не изменился, перезапускается только
        // sing-box (TUN и правила), а xray и его соединение с сервером продолжают работать — новое рукопожатие не нужно.
        // false — так нельзя (другой туннель, xray не запущен, конфиг xray другой): нужен полный перезапуск.
        public bool TryRestartTun(Settings st, string name, TunLists lists, out string err)
        {
            err = null;
            if (name != TunnelName || runningXrayJson == null || !x.Running) return false;
            var e = TunnelProto.Load(name);
            if (e == null || TunnelProto.Missing(e) != null) return false;
            var cs = TunSettings(st);
            cs.TunServerDomain = e.Address;
            if (TunXrayJson(st, cs, e) != runningXrayJson) return false;
            if (!File.Exists(SingBoxProcess.Exe(st))) return false;
            err = sb.Start(cs, SingBoxConfig.Build(cs, lists, SingLog(st)), "singbox-tunnel.json"); // Start сам останавливает прежний sing-box
            if (err != null) return true;
            WriteRuntime(name, e, lists);
            return true;
        }

        // диагностика: живы ли xray и sing-box, сколько у xray TCP-соединений с сервером туннеля
        public string Diag(Settings st)
        {
            string xs = x.Running ? "xray PID " + x.Pid : Tr.Tx("xray не работает");
            string ss = sb.Running ? "sing-box OK" : Tr.Tx("sing-box не работает");
            int n = -1;
            try
            {
                var e = TunnelProto.Load(TunnelName);
                var ips = new List<System.Net.IPAddress>();
                System.Net.IPAddress ip;
                if (e != null && System.Net.IPAddress.TryParse(e.Address, out ip)) ips.Add(ip);
                else if (e != null) ips.AddRange(System.Net.Dns.GetHostAddresses(e.Address));
                int pid = x.Pid;
                if (pid > 0 && ips.Count > 0) n = ProcInfo.Connections().Count(c => c.Item1 == pid && ips.Any(a => a.Equals(c.Item2)));
            }
            catch { }
            return xs + " · " + ss + (n >= 0 ? " · " + Tr.Tx("соединений xray с сервером: ") + n : "");
        }

        void WriteRuntime(string name, ServerEntry e, TunLists lists)
        {
            // для окна «Маршрут» (RouteFacts): те же ключи, что у WireSock — только факты запущенного
            try
            {
                File.WriteAllText(WiresockRunner.RuntimeFile, "# xray tunnel " + name + " (" + e.Transport + " " + e.Endpoint + ")\r\n" +
                    (lists.Include.Count > 0 ? "AllowedApps = " + string.Join(", ", lists.Include.Concat(new[] { "netprobe-vpn.exe" })) + "\r\n" : "") +
                    (lists.Exclude.Count > 0 ? "DisallowedApps = " + string.Join(", ", lists.Exclude) + "\r\n" : ""), new UTF8Encoding(false));
            }
            catch { }
        }

        public void Attach(Process xp, Process sp, string name) { x.Attach(xp); sb.Attach(sp); TunnelName = name; StartedAt = DateTime.Now; runningXrayJson = null; }

        public void Stop()
        {
            stopping = true; runningXrayJson = null;
            try { sb.Stop(); } catch { }
            try { x.Stop(); } catch { }
            stopping = false;
        }
    }

    // Пассивная проверка VLESS / VMess / Trojan туннеля: Clash API sing-box (127.0.0.1, секрет на запуск) отдаёт счётчики
    // соединений. Пришли данные через outbound «proxy» — сервер работает, сайт опрашивать не нужно.
    // Отправка идёт, а ответа нет несколько секунд — трафик встал, нужен активный опрос сразу.
    public enum TunnelFlowState { Unavailable, Flowing, Idle, Stalled }
    public static class TunnelFlow
    {
        static readonly object lk = new object();
        static Dictionary<string, long[]> prev = new Dictionary<string, long[]>();
        static DateTime sendNoReply = DateTime.MinValue;
        public const long MinUp = 512;

        public static void Reset() { lock (lk) { prev = new Dictionary<string, long[]>(); sendNoReply = DateTime.MinValue; } }
        // активный опрос прошёл: «отправили, а ответа нет» больше не считается
        public static void ProbeOk() { lock (lk) sendNoReply = DateTime.MinValue; }

        public static TunnelFlowState Read(int port)
        {
            string json;
            try
            {
                var rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create("http://127.0.0.1:" + port + "/connections");
                rq.Proxy = null; rq.Timeout = 1500; rq.ReadWriteTimeout = 1500;
                rq.Headers[System.Net.HttpRequestHeader.Authorization] = "Bearer " + TunnelAuth.Pass;
                using (var resp = rq.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) json = rd.ReadToEnd();
            }
            catch { Reset(); return TunnelFlowState.Unavailable; }
            return Parse(json, DateTime.Now);
        }

        // отдельно от HTTP — для офлайн-проверок
        public static TunnelFlowState Parse(string json, DateTime now)
        {
            var cur = new Dictionary<string, long[]>();
            try
            {
                var root = new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
                var conns = root != null && root.ContainsKey("connections") ? root["connections"] as object[] : null;
                if (conns != null)
                    foreach (var o in conns)
                    {
                        var c = o as Dictionary<string, object>; if (c == null || !c.ContainsKey("id")) continue;
                        var chains = c.ContainsKey("chains") ? c["chains"] as object[] : null;
                        if (chains == null || !chains.Any(x => string.Equals(Convert.ToString(x), "proxy", StringComparison.OrdinalIgnoreCase))) continue;
                        var md = c.ContainsKey("metadata") ? c["metadata"] as Dictionary<string, object> : null;
                        string proc = md != null && md.ContainsKey("processPath") ? Convert.ToString(md["processPath"]) : "";
                        if (proc.IndexOf("netprobe-", StringComparison.OrdinalIgnoreCase) >= 0) continue; // свои пробники не считаются
                        cur[Convert.ToString(c["id"])] = new[] { Convert.ToInt64(c.ContainsKey("upload") ? c["upload"] : 0), Convert.ToInt64(c.ContainsKey("download") ? c["download"] : 0) };
                    }
            }
            catch { Reset(); return TunnelFlowState.Unavailable; }
            lock (lk)
            {
                bool first = prev.Count == 0 && cur.Count > 0 && sendNoReply == DateTime.MinValue;
                long up = 0, down = 0;
                foreach (var kv in cur)
                {
                    long[] p;
                    if (prev.TryGetValue(kv.Key, out p)) { up += Math.Max(0, kv.Value[0] - p[0]); down += Math.Max(0, kv.Value[1] - p[1]); }
                    else if (!first) { up += kv.Value[0]; down += kv.Value[1]; } // новое соединение — всё, что оно успело передать
                }
                prev = cur;
                if (first) return TunnelFlowState.Idle; // первое чтение — не с чем сравнить
                if (down > 0) { sendNoReply = DateTime.MinValue; return TunnelFlowState.Flowing; }
                if (up >= MinUp && sendNoReply == DateTime.MinValue) sendNoReply = now;
                if (sendNoReply != DateTime.MinValue)
                {
                    double wait = (now - sendNoReply).TotalSeconds;
                    if (wait > 20) sendNoReply = DateTime.MinValue; // давно и без новых отправок — разговор закончился
                    else if (wait >= 3) return TunnelFlowState.Stalled;
                }
                return TunnelFlowState.Idle;
            }
        }
    }

    // То, с чем работает Router: WireSock или xray-туннель — по типу конфига.
    public class TunnelRunner
    {
        public readonly WiresockRunner Wire = new WiresockRunner();
        public readonly XrayTunnel Xr = new XrayTunnel();
        public bool IsXray;
        public event Action<int> Exited;
        public event Action<string> Output;

        public TunnelRunner()
        {
            Wire.Exited += c => { if (!IsXray) { var h = Exited; if (h != null) h(c); } };
            Xr.Exited += c => { if (IsXray) { var h = Exited; if (h != null) h(c); } };
            Wire.Output += s => { var h = Output; if (h != null) h(s); };
            Xr.Output += s => { var h = Output; if (h != null) h(s); };
        }

        public bool Running { get { return IsXray ? Xr.Running : Wire.Running; } }
        public string TunnelName { get { return IsXray ? Xr.TunnelName : Wire.TunnelName; } }
        public DateTime StartedAt { get { return IsXray ? Xr.StartedAt : Wire.StartedAt; } }
        public int Pid { get { return IsXray ? Xr.Pid : Wire.Pid; } }
        public string Engine { get { return IsXray ? "xray+TUN" : "WireSock"; } }

        public string Start(Settings st, string tunnel, string config) { Stop(); IsXray = false; return Wire.Start(st, tunnel, config); }
        public string StartXray(Settings st, string tunnel, TunLists lists) { Stop(); IsXray = true; return Xr.Start(st, tunnel, lists); }
        public bool TryRestartTun(Settings st, string tunnel, TunLists lists, out string err) { err = null; return IsXray && Xr.TryRestartTun(st, tunnel, lists, out err); }
        public void Attach(Process p, string tunnel) { IsXray = false; Wire.Attach(p, tunnel); }
        public void AttachXray(Process xp, Process sp, string tunnel) { IsXray = true; Xr.Attach(xp, sp, tunnel); }
        public void Stop() { Wire.Stop(); Xr.Stop(); }
    }
}
