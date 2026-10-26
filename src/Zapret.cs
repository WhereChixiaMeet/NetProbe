using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using static NetProbe.Tr;

namespace NetProbe
{
    // Стратегия DPI = аргументы winws.exe. Хранится в config\dpi\<имя>.txt, по одному профилю (--new) на строку.
    // Плейсхолдеры как в .bat zapret-discord-youtube: %BIN%, %LISTS%, %GameFilter%, %GameFilterTCP%, %GameFilterUDP%
    public static class DpiConfigs
    {
        public static string Dir { get { return Settings.Sub("dpi"); } }

        public static List<string> Names()
        {
            try { return Directory.GetFiles(Dir, "*.txt").Select(Path.GetFileNameWithoutExtension).OrderBy(x => x == "general" ? "" : x, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch { return new List<string>(); }
        }

        public static string PathOf(string n) { return Path.Combine(Dir, Tunnels.Safe(n) + ".txt"); }
        public static string Read(string n) { try { return File.ReadAllText(PathOf(n), Encoding.UTF8); } catch { return ""; } }
        public static void Write(string n, string text) { File.WriteAllText(PathOf(n), Pretty(text), new UTF8Encoding(false)); }
        public static void Delete(string n) { try { File.Delete(PathOf(n)); } catch { } }

        // Одна строка = один профиль, разделитель --new
        public static string Pretty(string args)
        {
            var flat = Flatten(args);
            var parts = Regex.Split(flat, @"\s+--new(?=\s|$)").Select(x => x.Trim()).Where(x => x.Length > 0);
            return string.Join(" --new\r\n", parts) + "\r\n";
        }

        public static string Flatten(string args)
        {
            var lines = args.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("::"));
            return Regex.Replace(string.Join(" ", lines.Select(l => l.TrimEnd('^').Trim())), @"\s+", " ").Trim();
        }

        // Достаёт аргументы winws.exe из .bat-файла zapret-discord-youtube
        public static string FromBat(string file)
        {
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            var sb = new StringBuilder();
            bool cap = false;
            foreach (var raw in lines)
            {
                string l = raw.Trim();
                if (!cap)
                {
                    int i = l.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    cap = true; l = l.Substring(i + 10);
                }
                bool cont = l.EndsWith("^");
                sb.Append(l.TrimEnd('^').Trim()).Append(' ');
                if (!cont) break;
            }
            return Pretty(sb.ToString());
        }

        // Хэши «заводских» стратегий из zapret: config\dpi\.stock (имя=хэш). Изменённые пользователем не перезаписываются.
        static string StockFile { get { return Path.Combine(Dir, ".stock"); } }
        static Dictionary<string, string> LoadStock()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { if (File.Exists(StockFile)) foreach (var l in File.ReadAllLines(StockFile)) { int i = l.LastIndexOf('='); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1); } } catch { }
            return d;
        }
        static string Hash(string text) { return ServerEntry.MakeId(Pretty(text)); }

        // overwrite=true: обновить заводские стратегии; если пользователь менял стратегию — новая версия ляжет рядом как «имя (new)»
        public static int ImportAll(string zapretDir, bool overwrite)
        {
            int n = 0;
            var stock = LoadStock();
            try
            {
                foreach (var f in Directory.GetFiles(zapretDir, "general*.bat"))
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    name = Regex.Replace(name, @"^general\s*\((.*)\)$", "$1").Trim();
                    if (name == "" || name.Equals("general", StringComparison.OrdinalIgnoreCase)) name = "general";
                    string a = FromBat(f);
                    if (a.Trim().Length == 0) continue;
                    string target = name;
                    if (File.Exists(PathOf(name)))
                    {
                        if (!overwrite) continue;
                        string cur = Hash(Read(name)), was;
                        if (Hash(a) == cur) { stock[name] = cur; continue; }
                        if (!stock.TryGetValue(name, out was) || was != cur) target = name + " (new)"; // пользователь правил — не трогаем
                    }
                    Write(target, a); n++;
                    if (target == name) stock[name] = Hash(a);
                }
                File.WriteAllLines(StockFile, stock.Select(kv => kv.Key + "=" + kv.Value).ToArray());
            }
            catch { }
            return n;
        }
    }

    public class ExclusionSet
    {
        public List<string> Processes = new List<string>(), Domains = new List<string>(), Ips = new List<string>();

        public static ExclusionSet From(IEnumerable<string> items)
        {
            var e = new ExclusionSet();
            foreach (var raw in items)
            {
                string s = raw.Trim(); if (s.Length == 0) continue;
                if (IsIp(s)) e.Ips.Add(s);
                else if (s.Contains("\\") || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !s.Contains(".")) e.Processes.Add(s);
                else e.Domains.Add(s.TrimStart('*', '.'));
            }
            return e;
        }

        public static bool IsIp(string s)
        {
            string a = s; int sl = s.IndexOf('/');
            if (sl > 0) a = s.Substring(0, sl);
            IPAddress ip; return IPAddress.TryParse(a, out ip) && (a.Contains(".") || a.Contains(":"));
        }
    }

    // Управление winws.exe (zapret). Работает только с правами администратора (WinDivert).
    public class ZapretRunner
    {
        Process proc;
        readonly object lk = new object();
        public string Strategy = "";
        public DateTime StartedAt;
        public event Action<string> Output;
        public event Action<int> Exited;
        public bool Running { get { lock (lk) { try { return proc != null && !proc.HasExited; } catch { return false; } } } }

        public static string ExclIpFile { get { return Path.Combine(Settings.Sub("runtime"), "ipset-exclude-netprobe.txt"); } }
        public static string ExclHostFile { get { return Path.Combine(Settings.Sub("runtime"), "list-exclude-netprobe.txt"); } }
        // list-general-user.txt + домены приложений из Include DPI — подставляется в стратегию вместо list-general-user.txt
        public static string UserHostFile { get { return Path.Combine(Settings.Sub("runtime"), "list-general-netprobe.txt"); } }
        public static Func<List<string>> ExtraDomains;

        // winws сам перечитывает hostlist при изменении файла — перезапуск не нужен
        public static void WriteUserHosts(string zdir)
        {
            var lines = new List<string>();
            try { string f = Path.Combine(zdir, "lists", "list-general-user.txt"); if (File.Exists(f)) lines.AddRange(File.ReadAllLines(f).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#"))); } catch { }
            if (ExtraDomains != null) try { lines.AddRange(ExtraDomains().Where(d => !ExclusionSet.IsIp(d)).SelectMany(DomainRoutes.Expand)); } catch { }
            if (lines.Count == 0) lines.Add("domain.example.abc");
            WriteIfChanged(UserHostFile, lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        public static string Check(string zdir)
        {
            if (!File.Exists(Path.Combine(zdir, "bin", "winws.exe"))) return Tx("Не найден winws.exe в ") + Path.Combine(zdir, "bin") + Tx(". Укажите папку zapret в Настройках.");
            if (!File.Exists(Path.Combine(zdir, "bin", "WinDivert64.sys"))) return Tx("Не найден WinDivert64.sys в папке zapret\\bin.");
            return null;
        }

        // Конфликты: служба zapret из service.bat, чужой winws.exe, GoodbyeDPI
        public static string Conflicts(int except)
        {
            var l = new List<string>();
            try { foreach (var p in Process.GetProcessesByName("winws")) { if (p.Id != except) l.Add("winws.exe (PID " + p.Id + ")"); p.Dispose(); } } catch { }
            foreach (var svc in new[] { "zapret", "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" })
            {
                var r = Proc.Run("sc.exe", "query " + svc, 4000);
                if (r.Out.Contains("RUNNING")) l.Add(Tx("служба ") + svc);
            }
            return l.Count == 0 ? null : string.Join(", ", l);
        }

        public static void StopConflicts(int except)
        {
            foreach (var svc in new[] { "zapret", "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" }) Proc.Run("sc.exe", "stop " + svc, 8000);
            try { foreach (var p in Process.GetProcessesByName("winws")) { if (p.Id != except) try { p.Kill(); p.WaitForExit(3000); } catch { } p.Dispose(); } } catch { }
        }

        public static void WriteExclusions(ExclusionSet ex, IEnumerable<string> dynamicIps)
        {
            var ips = new List<string> { "203.0.113.113/32" }; // файл не должен быть пустым
            ips.AddRange(ex.Ips); ips.AddRange(dynamicIps);
            var hosts = new List<string> { "domain.example.abc" };
            hosts.AddRange(ex.Domains);
            WriteIfChanged(ExclIpFile, ips.Distinct().ToList());
            WriteIfChanged(ExclHostFile, hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        static void WriteIfChanged(string f, List<string> lines)
        {
            string text = string.Join("\r\n", lines) + "\r\n";
            try { if (File.Exists(f) && File.ReadAllText(f) == text) return; File.WriteAllText(f, text, new UTF8Encoding(false)); } catch { }
        }

        public static void EnsureUserLists(string zdir)
        {
            string ld = Path.Combine(zdir, "lists");
            try
            {
                Directory.CreateDirectory(ld);
                var def = new Dictionary<string, string> {
                    { "ipset-exclude-user.txt", "203.0.113.113/32\r\n" },
                    { "list-general-user.txt", "# Never leave this file empty\r\ndomain.example.abc\r\n" },
                    { "list-exclude-user.txt", "domain.example.abc\r\n" } };
                foreach (var kv in def) { string f = Path.Combine(ld, kv.Key); if (!File.Exists(f)) File.WriteAllText(f, kv.Value); }
            }
            catch { }
        }

        public static string BuildArgs(Settings st, string strategyText)
        {
            string zdir = st.ZapretPath;
            string bin = Path.Combine(zdir, "bin") + "\\", lists = Path.Combine(zdir, "lists") + "\\";
            string gt = "12", gu = "12";
            if (st.GameFilter == 1 || st.GameFilter == 2) gt = st.GameTcp.Trim();
            if (st.GameFilter == 1 || st.GameFilter == 3) gu = st.GameUdp.Trim();
            string g = st.GameFilter == 3 ? gu : gt;
            string flat = DpiConfigs.Flatten(strategyText);
            flat = Regex.Replace(flat, "%BIN%", bin.Replace("$", "$$"), RegexOptions.IgnoreCase);
            flat = Regex.Replace(flat, "%LISTS%", lists.Replace("$", "$$"), RegexOptions.IgnoreCase);
            flat = Regex.Replace(flat, "%GameFilterTCP%", gt, RegexOptions.IgnoreCase);
            flat = Regex.Replace(flat, "%GameFilterUDP%", gu, RegexOptions.IgnoreCase);
            flat = Regex.Replace(flat, "%GameFilter%", g, RegexOptions.IgnoreCase);
            flat = Regex.Replace(flat, Regex.Escape(lists + "list-general-user.txt"), UserHostFile.Replace("$", "$$"), RegexOptions.IgnoreCase);
            // исключения NetProbe добавляются в каждый профиль
            string add = " --ipset-exclude=\"" + ExclIpFile + "\" --hostlist-exclude=\"" + ExclHostFile + "\"";
            var parts = Regex.Split(flat, @"\s+--new(?=\s|$)").Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            for (int i = 0; i < parts.Count; i++)
            {
                // в первом профиле --wf-* относится ко всей программе; добавляем после него без разницы
                parts[i] = parts[i] + add;
            }
            return string.Join(" --new ", parts);
        }

        public string Start(Settings st, string strategy)
        {
            Stop();
            string zdir = st.ZapretPath;
            string err = Check(zdir); if (err != null) return err;
            string text = DpiConfigs.Read(strategy);
            if (text.Trim().Length == 0) return Tx("Стратегия DPI «") + strategy + Tx("» пуста или не найдена.");
            EnsureUserLists(zdir);
            if (!File.Exists(ExclIpFile)) WriteExclusions(new ExclusionSet(), new string[0]);
            WriteUserHosts(zdir);
            string args = BuildArgs(st, text);
            var psi = new ProcessStartInfo(Path.Combine(zdir, "bin", "winws.exe"), args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.Combine(zdir, "bin")
            };
            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { var h = Output; if (h != null) try { h(e.Data.Trim()); } catch { } } };
                p.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) { var h = Output; if (h != null) try { h(e.Data.Trim()); } catch { } } };
                p.Exited += (s, e) =>
                {
                    int code = -1; try { code = p.ExitCode; } catch { }
                    bool mine; lock (lk) mine = proc == p;
                    if (mine) { var h = Exited; if (h != null) try { h(code); } catch { } }
                };
                p.Start();
                lock (lk) { proc = p; Strategy = strategy; StartedAt = DateTime.Now; }
                return null;
            }
            catch (Exception ex) { return Tx("Не удалось запустить winws.exe: ") + ex.Message; }
        }

        public void Attach(Process p, string strategy)
        {
            p.EnableRaisingEvents = true;
            p.Exited += (s, e) => { int code = -1; try { code = p.ExitCode; } catch { } bool mine; lock (lk) mine = proc == p; if (mine) { var h = Exited; if (h != null) try { h(code); } catch { } } };
            lock (lk) { proc = p; Strategy = strategy; StartedAt = DateTime.Now; }
        }

        public void Stop()
        {
            Process p;
            lock (lk) { p = proc; proc = null; }
            if (p == null) return;
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(4000); } } catch { }
            try { p.Dispose(); } catch { }
        }
    }
}
