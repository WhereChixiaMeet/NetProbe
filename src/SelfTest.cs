using System;
using System.IO;
using System.Linq;
using System.Text;

namespace NetProbe
{
    // Отладка: --selftest <файл> — проверка генерации конфигов без запуска туннеля/DPI
    static class SelfTest
    {
        public static void Run(string outFile)
        {
            var sb = new StringBuilder();
            var st = Settings.Load();
            sb.AppendLine("zapret: " + st.ZapretPath + " check=" + (ZapretRunner.Check(st.ZapretPath) ?? "ok"));
            sb.AppendLine("imported strategies: " + DpiConfigs.ImportAll(st.ZapretPath, false) + " -> " + string.Join(", ", DpiConfigs.Names()));
            sb.AppendLine("--- general.txt ---\r\n" + DpiConfigs.Read("general"));
            st.GameFilter = 1;
            sb.AppendLine("--- args(general, game=all) ---\r\n" + ZapretRunner.BuildArgs(st, DpiConfigs.Read("general")).Replace(" --new ", " --new\r\n"));
            var ex = ExclusionSet.From(new[] { "Discord.exe", @"C:\Games\x.exe", "youtube.com", "*.ggpht.com", "1.2.3.0/24", "2001:db8::/32", "steam" });
            sb.AppendLine("--- exclusions --- procs=" + string.Join("|", ex.Processes) + " domains=" + string.Join("|", ex.Domains) + " ips=" + string.Join("|", ex.Ips));
            foreach (var t in Tunnels.Names())
            {
                sb.AppendLine("--- runtime full: " + t + " ---\r\n" + Tunnels.Runtime(t, new[] { "Discord.exe", "chrome.exe", Router.VpnProbe }.ToList(), new[] { "steam.exe" }.ToList(), false));
                sb.AppendLine("--- runtime full-tunnel (no apps): " + t + " ---\r\n" + Tunnels.Runtime(t, new System.Collections.Generic.List<string>(), new[] { Router.DirectProbe }.ToList(), true));
            }
            var ips = ProcInfo.RemoteIpsOf(new[] { "chrome" }.ToList());
            sb.AppendLine("chrome remote IPs: " + ips.Count + " e.g. " + string.Join(", ", ips.Take(5)));
            sb.AppendLine("paths of 'discord': " + string.Join(", ", ProcInfo.PathsOf(new[] { "discord" }.ToList())));
            sb.AppendLine("wiresock service running: " + WiresockRunner.ServiceRunning());
            sb.AppendLine("dpi conflicts: " + (ZapretRunner.Conflicts(-1) ?? "none"));
            // 3.0: i18n — словари загрузились (дубли ключей уронили бы статический инициализатор), покрытие ES / ZH относительно EN
            var en = Tr.Dict("en"); var es = Tr.Dict("es"); var zh = Tr.Dict("zh");
            var missEs = en.Keys.Where(k => !es.ContainsKey(k)).ToList(); var missZh = en.Keys.Where(k => !zh.ContainsKey(k)).ToList();
            sb.AppendLine("--- i18n --- en=" + en.Count + " es=" + es.Count + " zh=" + zh.Count + " · no ES pair: " + missEs.Count + " · no ZH pair: " + missZh.Count);
            foreach (var k in missEs.Take(40)) sb.AppendLine("  ES missing: " + k);
            foreach (var k in missZh.Take(40)) sb.AppendLine("  ZH missing: " + k);
            // 3.0: протоколы — разбор ссылок и генерация конфигов xray-туннеля (ничего не запускается и не подключается)
            string err;
            var demo = TunnelProto.FromLink("vless://00000000-0000-0000-0000-000000000000@example.invalid:443?security=reality&sni=example.invalid&pbk=AAAA&sid=01&type=tcp&flow=xtls-rprx-vision#demo", out err);
            sb.AppendLine("--- vless link parse: " + (demo != null ? demo.Transport + " " + demo.Endpoint + " missing=" + (TunnelProto.Missing(demo) ?? "none") : err));
            string bad; TunnelProto.FromLink("vless://broken", out bad); sb.AppendLine("--- broken link: " + (bad ?? "parsed?!"));
            if (demo != null)
            {
                var cs = st.Clone(); cs.SocksPort = st.TunnelSocksPort; cs.TunMode = true;
                sb.AppendLine("--- xray tunnel config ---\r\n" + XrayConfig.Build(cs, demo));
                sb.AppendLine("--- sing-box TUN config (Include=Discord.exe, Exclude=steam.exe) ---\r\n" + SingBoxConfig.Build(cs, new TunLists { Include = new[] { "Discord.exe" }.ToList(), Exclude = new[] { "steam.exe" }.ToList() }));
            }
            sb.AppendLine("--- tunnels: " + string.Join(", ", Tunnels.Names().Select(n => n + (TunnelProto.IsXray(n) ? " [" + TunnelProto.Names[TunnelProto.IndexOf(n)] + "]" : " [WireGuard]"))));
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }

        // --speedtest <файл> [ссылки.txt]: диагностика замера задержки/скорости серверов (ничего не подключает, только тестовый xray на 127.0.0.1)
        public static void Speed(string outFile, string linksFile)
        {
            var sb = new StringBuilder();
            Action<string> w = l => { sb.AppendLine(l); try { File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8); } catch { } };
            var st = Settings.Load();
            ServerStore.Load();
            string curl = "curl.exe";
            w("xray: " + XrayProcess.Exe(st) + " exists=" + File.Exists(XrayProcess.Exe(st)) + " version=" + Updater.XrayVersion(st));
            w("curl: " + Proc.Run(curl, "--version", 8000).Out.Split('\n').FirstOrDefault());
            w("SpeedUrl=" + st.SpeedUrl + " SpeedSec=" + st.SpeedSec + " TestUrl=" + st.TestUrl);
            var all = linksFile != null ? ServerStore.ParseMany(File.ReadAllText(linksFile)) : ServerStore.Snapshot();
            var pick = all.Where(x => x.Delay > 0).OrderBy(x => x.Delay).Take(3).ToList();
            if (pick.Count == 0) pick = all.Take(3).ToList();
            w("servers: " + all.Count + ", testing: " + string.Join(" | ", pick.Select(x => x.Name + " (" + x.Transport + ")")));
            var urls = new[] { st.SpeedUrl, "https://speed.cloudflare.com/__down?bytes=25000000" }.Concat(DelayTester.SpareSpeedUrls).Distinct().ToList();
            foreach (var s in pick)
            {
                int port = 23900;
                var x = new XrayProcess();
                string cfg = Path.Combine(Settings.Sub("runtime"), "xray-diag.json");
                string err = x.Start(st, XrayConfig.BuildTest(new System.Collections.Generic.List<ServerEntry> { s }, port), cfg, true);
                System.Threading.Thread.Sleep(1500);
                w("=== " + s.Name + " · start=" + (err ?? "ok") + " running=" + x.Running + " last=" + x.LastLine);
                if (x.Running)
                {
                    var d = Proc.Run(curl, "-sS -o NUL --ssl-no-revoke -x socks5h://127.0.0.1:" + port + " --max-time 8 -w \"code=%{http_code} t=%{time_total}\" \"" + st.TestUrl + "\"", 14000);
                    w("  delay: exit=" + d.Exit + " out=" + d.Out.Trim() + " err=" + d.Err.Trim());
                    foreach (var u in urls)
                    {
                        var r = Proc.Run(curl, "-sS -o NUL --ssl-no-revoke -x socks5h://127.0.0.1:" + port + " --connect-timeout 8 --max-time " + st.SpeedSec + " -w \"code=%{http_code} size=%{size_download} speed=%{speed_download} t=%{time_total}\" \"" + u + "\"", (st.SpeedSec + 14) * 1000);
                        string perr; double pv = DelayTester.Speed(curl, port, u, 4, null, out perr); w("  " + u + "\r\n    exit=" + r.Exit + " timedOut=" + r.TimedOut + " out=" + r.Out.Trim() + " err=" + r.Err.Trim() + " parsed=" + pv + " " + perr);
                    }
                }
                x.Stop();
            }
            w("done");
        }
    }
}
