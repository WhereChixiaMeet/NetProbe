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

        public XrayTunnel()
        {
            x.Exited += c => { if (!stopping) { var h = Exited; if (h != null) h(c); } };
            sb.Exited += c => { if (!stopping) { var h = Exited; if (h != null) h(c); } };
            sb.Output += s => { if (s.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0) { var h = Output; if (h != null) h("[sing-box] " + s); } };
        }

        public bool Running { get { return x.Running && sb.Running; } }
        public int Pid { get { return x.Pid; } }

        public static int Port(Settings st) { return st.TunnelSocksPort; }

        public string Start(Settings st, string name, TunLists lists)
        {
            Stop();
            var e = TunnelProto.Load(name);
            string miss = TunnelProto.Missing(e);
            if (miss != null) return Tx("Туннель «") + name + "»: " + miss;
            if (!File.Exists(XrayProcess.Exe(st))) return Tx("Не найден xray.exe — установите ядро на вкладке Subscriptions («Ядро xray»)");
            if (!File.Exists(SingBoxProcess.Exe(st))) return Tx("Не найден sing-box.exe — установите ядро TUN на вкладке Subscriptions («sing-box»)");
            // свои порты, чтобы не мешать xray подписок; DirectDomains / статистика здесь не нужны
            var cs = st.Clone(); cs.SocksPort = Port(st); cs.HttpPort = Port(st) + 1; cs.TunMode = true; cs.AllowLan = false;
            string err = x.Start(cs, XrayConfig.Build(cs, e), Path.Combine(Settings.Sub("runtime"), "xray-tunnel.json"));
            if (err != null) return err;
            System.Threading.Thread.Sleep(400);
            if (!x.Running) return Tx("xray туннеля сразу завершился — проверь поля конфига (лог xray)");
            err = sb.Start(cs, SingBoxConfig.Build(cs, lists), "singbox-tunnel.json");
            if (err != null) { x.Stop(); return err; }
            TunnelName = name; StartedAt = DateTime.Now; stopping = false;
            // для окна «Маршрут» (RouteFacts): те же ключи, что у WireSock — только факты запущенного
            try
            {
                File.WriteAllText(WiresockRunner.RuntimeFile, "# xray tunnel " + name + " (" + e.Transport + " " + e.Endpoint + ")\r\n" +
                    (lists.Include.Count > 0 ? "AllowedApps = " + string.Join(", ", lists.Include.Concat(new[] { "netprobe-vpn.exe" })) + "\r\n" : "") +
                    (lists.Exclude.Count > 0 ? "DisallowedApps = " + string.Join(", ", lists.Exclude) + "\r\n" : ""), new UTF8Encoding(false));
            }
            catch { }
            return null;
        }

        public void Attach(Process xp, Process sp, string name) { x.Attach(xp); sb.Attach(sp); TunnelName = name; StartedAt = DateTime.Now; }

        public void Stop()
        {
            stopping = true;
            try { sb.Stop(); } catch { }
            try { x.Stop(); } catch { }
            stopping = false;
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
        public void Attach(Process p, string tunnel) { IsXray = false; Wire.Attach(p, tunnel); }
        public void AttachXray(Process xp, Process sp, string tunnel) { IsXray = true; Xr.Attach(xp, sp, tunnel); }
        public void Stop() { Wire.Stop(); Xr.Stop(); }
    }
}
