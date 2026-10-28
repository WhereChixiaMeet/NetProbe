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
    // Списки TUN: Include → сервер, Exclude → напрямую, Domains — (приложение, домен) мимо сервера под DPI
    public class TunLists
    {
        public List<string> Include = new List<string>(), Exclude = new List<string>();
        // 3.0.4: домены/IP из Include и «Строго VPN» — в TUN (sing-box) идут через сервер (у WireSock такого нет)
        public List<string> IncludeHosts = new List<string>();
        // все записи списков (приложения + домены/IP): приложения отделяются, домены/IP попадают в IncludeHosts / Exclude
        public static TunLists FromLists(IEnumerable<string> include, IEnumerable<string> exclude)
        {
            var inc = include.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return new TunLists { Include = inc.Where(AppLists.IsAppEntry).ToList(), IncludeHosts = inc.Where(x => !AppLists.IsAppEntry(x)).ToList(), Exclude = exclude.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() };
        }
        public List<KeyValuePair<string, string>> Domains = new List<KeyValuePair<string, string>>();
    }

    public static class SingBoxConfig
    {
        static string Cidr(string i) { return i.Contains("/") ? i : i + (i.Contains(":") ? "/128" : "/32"); }

        static List<KeyValuePair<string, List<string>>> DomainGroups(List<KeyValuePair<string, string>> pairs)
        {
            return pairs.GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new KeyValuePair<string, List<string>>(g.Key, g.SelectMany(p => ExclusionSet.IsIp(p.Value) ? new List<string> { p.Value } : DomainRoutes.Expand(p.Value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList())).ToList();
        }

        static string Rx(string entry) { return "(?i)" + Regex.Escape(entry.Trim()); } // подстрока пути, как у WireSock

        public static string Build(Settings st, TunLists l)
        {
            var include = l.Include;
            var ex = ExclusionSet.From(l.Exclude);
            var rules = new List<object>
            {
                new Dictionary<string, object> { { "action", "sniff" } },
                new Dictionary<string, object> { { "protocol", "dns" }, { "action", "hijack-dns" } },
                new Dictionary<string, object> { { "ip_is_private", true }, { "outbound", "direct" } },
                // собственные процессы — всегда напрямую, иначе петля
                new Dictionary<string, object> { { "process_name", new[] { "xray.exe", "sing-box.exe", "netprobe-direct.exe", "wiresock-client.exe", "winws.exe" } }, { "outbound", "direct" } }
            };
            // домены под приложением (Include DPI): это приложение + этот домен → напрямую (winws обработает), остальное как обычно
            var dnsRules = new List<object>();
            foreach (var g in DomainGroups(l.Domains))
            {
                var r = new Dictionary<string, object> { { "process_path_regex", new[] { Rx(g.Key) } }, { "outbound", "direct" } };
                var rd = new Dictionary<string, object> { { "process_path_regex", new[] { Rx(g.Key) } }, { "server", "local" } };
                var doms = g.Value.Where(d => !ExclusionSet.IsIp(d)).ToArray();
                var ips = g.Value.Where(ExclusionSet.IsIp).Select(Cidr).ToArray();
                if (doms.Length > 0) { r["domain_suffix"] = doms; rd["domain_suffix"] = doms; dnsRules.Add(rd); rules.Add(r); }
                if (ips.Length > 0) rules.Add(new Dictionary<string, object> { { "process_path_regex", new[] { Rx(g.Key) } }, { "ip_cidr", ips }, { "outbound", "direct" } });
            }
            if (ex.Processes.Count > 0) rules.Add(new Dictionary<string, object> { { "process_path_regex", ex.Processes.Select(Rx).ToArray() }, { "outbound", "direct" } });
            if (ex.Domains.Count > 0) rules.Add(new Dictionary<string, object> { { "domain_suffix", ex.Domains.ToArray() }, { "outbound", "direct" } });
            if (ex.Ips.Count > 0) rules.Add(new Dictionary<string, object> { { "ip_cidr", ex.Ips.Select(Cidr).ToArray() }, { "outbound", "direct" } });
            var ih = ExclusionSet.From(l.IncludeHosts ?? new List<string>());
            if (ih.Domains.Count > 0) rules.Add(new Dictionary<string, object> { { "domain_suffix", ih.Domains.ToArray() }, { "outbound", "proxy" } });
            if (ih.Ips.Count > 0) rules.Add(new Dictionary<string, object> { { "ip_cidr", ih.Ips.Select(Cidr).ToArray() }, { "outbound", "proxy" } });
            bool all = include.Count == 0 && ih.Domains.Count == 0 && ih.Ips.Count == 0;
            if (include.Count > 0)
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
                        { "rules", dnsRules
                            .Concat(ih.Domains.Count > 0 ? new object[] { new Dictionary<string, object> { { "domain_suffix", ih.Domains.ToArray() }, { "server", "remote" } } } : new object[0])
                            .Concat(include.Count == 0 ? new object[0] : new object[] { new Dictionary<string, object> { { "process_path_regex", include.Select(Rx).ToArray() }, { "server", "remote" } } }).ToArray() },
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

        public string Start(Settings st, string json, string cfgName = "singbox.json")
        {
            Stop();
            string exe = Exe(st);
            if (!File.Exists(exe)) return Tx("Не найден sing-box.exe — установите ядро TUN (кнопка «sing-box»)");
            string cfg = Path.Combine(Settings.Sub("runtime"), cfgName);
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
