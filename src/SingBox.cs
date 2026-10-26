using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

using static NetProbe.Tr;

namespace NetProbe
{
    // TUN-режим для раздела Servers: sing-box поднимает виртуальный адаптер и по процессам решает,
    // что отправить в локальный socks xray (выбранный сервер), а что напрямую. Протоколы по-прежнему обслуживает xray.
    public static class SingBoxConfig
    {
        static string Rx(string entry) { return "(?i)" + Regex.Escape(entry.Trim()); } // подстрока пути, как у WireSock

        public static string Build(Settings st, List<string> include, List<string> exclude)
        {
            var ex = ExclusionSet.From(exclude);
            var rules = new List<object>
            {
                new Dictionary<string, object> { { "action", "sniff" } },
                new Dictionary<string, object> { { "protocol", "dns" }, { "action", "hijack-dns" } },
                new Dictionary<string, object> { { "ip_is_private", true }, { "outbound", "direct" } },
                // собственные процессы — всегда напрямую, иначе петля
                new Dictionary<string, object> { { "process_name", new[] { "xray.exe", "sing-box.exe", "netprobe-direct.exe", "wiresock-client.exe", "winws.exe" } }, { "outbound", "direct" } }
            };
            if (ex.Processes.Count > 0) rules.Add(new Dictionary<string, object> { { "process_path_regex", ex.Processes.Select(Rx).ToArray() }, { "outbound", "direct" } });
            if (ex.Domains.Count > 0) rules.Add(new Dictionary<string, object> { { "domain_suffix", ex.Domains.ToArray() }, { "outbound", "direct" } });
            if (ex.Ips.Count > 0) rules.Add(new Dictionary<string, object> { { "ip_cidr", ex.Ips.Select(i => i.Contains("/") ? i : i + (i.Contains(":") ? "/128" : "/32")).ToArray() }, { "outbound", "direct" } });
            bool all = include.Count == 0;
            if (!all)
            {
                var inc = include.Concat(new[] { "netprobe-vpn.exe" }).Distinct(StringComparer.OrdinalIgnoreCase).Select(Rx).ToArray();
                rules.Add(new Dictionary<string, object> { { "process_path_regex", inc }, { "outbound", "proxy" } });
            }
            var cfg = new Dictionary<string, object>
            {
                { "log", new Dictionary<string, object> { { "level", "warn" }, { "timestamp", true } } },
                { "dns", new Dictionary<string, object>
                    {
                        { "servers", new object[] {
                            new Dictionary<string, object> { { "tag", "remote" }, { "type", "tcp" }, { "server", "1.1.1.1" }, { "detour", "proxy" } },
                            new Dictionary<string, object> { { "tag", "local" }, { "type", "local" } } } },
                        { "final", all ? "remote" : "local" },
                        { "rules", all ? new object[0] : new object[] { new Dictionary<string, object> { { "process_path_regex", include.Select(Rx).ToArray() }, { "server", "remote" } } } },
                        { "strategy", "ipv4_only" }
                    } },
                { "inbounds", new object[] { new Dictionary<string, object> {
                    { "type", "tun" }, { "tag", "tun-in" }, { "interface_name", "NetProbe TUN" }, { "address", new[] { "172.19.0.1/30" } },
                    { "mtu", 9000 }, { "auto_route", true }, { "strict_route", true }, { "stack", "mixed" } } } },
                { "outbounds", new object[] {
                    new Dictionary<string, object> { { "type", "socks" }, { "tag", "proxy" }, { "server", "127.0.0.1" }, { "server_port", st.SocksPort }, { "version", "5" } },
                    new Dictionary<string, object> { { "type", "direct" }, { "tag", "direct" } } } },
                { "route", new Dictionary<string, object> { { "rules", rules }, { "final", all ? "proxy" : "direct" }, { "auto_detect_interface", true }, { "default_domain_resolver", "local" } } }
            };
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(cfg);
        }
    }

    public class SingBoxProcess
    {
        Process p;
        public event Action<string> Output;
        public event Action<int> Exited;
        public bool Running { get { try { return p != null && !p.HasExited; } catch { return false; } } }
        public static string Exe(Settings st) { return Path.Combine(st.SingBoxPath, "sing-box.exe"); }

        public string Start(Settings st, string json)
        {
            Stop();
            string exe = Exe(st);
            if (!File.Exists(exe)) return Tx("Не найден sing-box.exe — установите ядро TUN (кнопка «sing-box»)");
            string cfg = Path.Combine(Settings.Sub("runtime"), "singbox.json");
            File.WriteAllText(cfg, json, new UTF8Encoding(false));
            var psi = new ProcessStartInfo(exe, "run -c \"" + cfg + "\" -D \"" + st.SingBoxPath + "\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = st.SingBoxPath };
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
            catch (Exception ex) { return "sing-box: " + ex.Message; }
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
            try { if (!pr.HasExited) { pr.Kill(); pr.WaitForExit(4000); } } catch { }
            try { pr.Dispose(); } catch { }
        }
    }
}
