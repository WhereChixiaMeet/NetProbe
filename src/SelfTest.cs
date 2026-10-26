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
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }
    }
}
