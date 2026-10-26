using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

using static NetProbe.Tr;

namespace NetProbe
{
    public class ProcRow
    {
        public int Pid;
        public string Name = "", Path = "", Title = "";
        public long MemBytes;
        public double Cpu;
        public int Conns;
        public TimeSpan CpuTime;
    }

    // Встроенный диспетчер задач: процессы, CPU, память, TCP-соединения
    public static class ProcInfo
    {
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("iphlpapi.dll")] static extern uint GetExtendedTcpTable(IntPtr tbl, ref int size, bool order, int af, int cls, int reserved);

        static readonly Dictionary<int, TimeSpan> lastCpu = new Dictionary<int, TimeSpan>();
        static DateTime lastSample = DateTime.MinValue;

        public static string ImagePath(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return "";
            try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : ""; }
            finally { CloseHandle(h); }
        }

        public static List<ProcRow> Snapshot()
        {
            var rows = new List<ProcRow>();
            var conns = Connections();
            var counts = conns.GroupBy(c => c.Item1).ToDictionary(g => g.Key, g => g.Count());
            var now = DateTime.Now;
            double dt = lastSample == DateTime.MinValue ? 0 : (now - lastSample).TotalMilliseconds;
            var newCpu = new Dictionary<int, TimeSpan>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var r = new ProcRow { Pid = p.Id, Name = p.ProcessName + ".exe" };
                    if (p.Id == 0) { r.Name = "System Idle"; p.Dispose(); continue; }
                    try { r.MemBytes = p.WorkingSet64; } catch { }
                    try { r.Path = ImagePath(p.Id); } catch { }
                    try { r.Title = p.MainWindowTitle; } catch { }
                    try
                    {
                        r.CpuTime = p.TotalProcessorTime; newCpu[p.Id] = r.CpuTime;
                        TimeSpan prev;
                        if (dt > 0 && lastCpu.TryGetValue(p.Id, out prev)) r.Cpu = Math.Max(0, (r.CpuTime - prev).TotalMilliseconds / dt / Environment.ProcessorCount * 100);
                    }
                    catch { }
                    int c; if (counts.TryGetValue(p.Id, out c)) r.Conns = c;
                    rows.Add(r);
                }
                catch { }
                finally { p.Dispose(); }
            }
            lock (lastCpu) { lastCpu.Clear(); foreach (var kv in newCpu) lastCpu[kv.Key] = kv.Value; }
            lastSample = now;
            return rows;
        }

        // (pid, удалённый IP) для всех TCP-соединений IPv4 и IPv6
        public static List<Tuple<int, IPAddress>> Connections()
        {
            var res = new List<Tuple<int, IPAddress>>();
            foreach (int af in new[] { 2, 23 })
            {
                int size = 0;
                GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5, 0); // TCP_TABLE_OWNER_PID_ALL
                if (size <= 0) continue;
                IntPtr buf = Marshal.AllocHGlobal(size + 1024); size += 1024;
                try
                {
                    if (GetExtendedTcpTable(buf, ref size, false, af, 5, 0) != 0) continue;
                    int n = Marshal.ReadInt32(buf);
                    IntPtr row = buf + 4;
                    for (int i = 0; i < n; i++)
                    {
                        if (af == 2)
                        {
                            // state, localAddr, localPort, remoteAddr, remotePort, pid
                            uint state = (uint)Marshal.ReadInt32(row);
                            uint raddr = (uint)Marshal.ReadInt32(row + 12);
                            int pid = Marshal.ReadInt32(row + 20);
                            if (state != 2 && raddr != 0) res.Add(Tuple.Create(pid, new IPAddress(raddr)));
                            row += 24;
                        }
                        else
                        {
                            // localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                            var ra = new byte[16]; Marshal.Copy(row + 24, ra, 0, 16);
                            uint state = (uint)Marshal.ReadInt32(row + 48);
                            int pid = Marshal.ReadInt32(row + 52);
                            if (state != 2 && ra.Any(b => b != 0)) res.Add(Tuple.Create(pid, new IPAddress(ra)));
                            row += 56;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return res;
        }

        // Совпадение элемента списка с процессом так же, как это делает WireSock: подстрока имени/пути без учёта регистра
        public static bool Matches(string rule, string name, string path)
        {
            if (string.IsNullOrWhiteSpace(rule)) return false;
            string r = rule.Trim();
            return name.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0 || (!string.IsNullOrEmpty(path) && path.IndexOf(r, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Удалённые IP процессов, подходящих под правила (для исключений DPI)
        public static List<string> RemoteIpsOf(List<string> rules)
        {
            var res = new HashSet<string>();
            if (rules.Count == 0) return res.ToList();
            var pidOk = new Dictionary<int, bool>();
            foreach (var c in Connections())
            {
                bool ok;
                if (!pidOk.TryGetValue(c.Item1, out ok))
                {
                    string name = "", path = "";
                    try { using (var p = Process.GetProcessById(c.Item1)) name = p.ProcessName + ".exe"; } catch { }
                    try { path = ImagePath(c.Item1); } catch { }
                    ok = rules.Any(r => Matches(r, name, path));
                    pidOk[c.Item1] = ok;
                }
                if (ok && !IPAddress.IsLoopback(c.Item2)) res.Add(c.Item2.ToString());
            }
            return res.ToList();
        }

        // Полные пути запущенных программ, подходящих под правила (для kill switch в брандмауэре)
        public static List<string> PathsOf(List<string> rules)
        {
            var res = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rules) if (r.Contains("\\") && File.Exists(r)) res.Add(r);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName + ".exe", path = ImagePath(p.Id);
                    if (path != "" && rules.Any(r => Matches(r, name, path))) res.Add(path);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return res.ToList();
        }
    }

    // Kill switch: блокировка исходящего трафика «только VPN» приложений правилами брандмауэра Windows, пока VPN недоступен
    public static class KillSwitch
    {
        public const string RuleName = "NetProbe KillSwitch";
        static readonly HashSet<string> blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static bool Active { get { lock (blocked) return blocked.Count > 0; } }
        public static int Count { get { lock (blocked) return blocked.Count; } }

        public static int Block(IEnumerable<string> paths)
        {
            int added = 0;
            foreach (var p in paths)
            {
                lock (blocked) if (blocked.Contains(p)) continue;
                if (p.Contains("\"") || p.Contains("netprobe-")) continue;
                var r = Proc.Run("netsh.exe", "advfirewall firewall add rule name=\"" + RuleName + "\" dir=out action=block enable=yes program=\"" + p + "\"", 10000, Proc.Oem);
                if (r.Exit == 0) { lock (blocked) blocked.Add(p); added++; }
                else Logger.Write(LogLevel.Error, Tx("Kill switch: не удалось заблокировать ") + p + " — " + (r.Out + r.Err).Trim());
            }
            return added;
        }

        public static void Clear()
        {
            Proc.Run("netsh.exe", "advfirewall firewall delete rule name=\"" + RuleName + "\"", 15000, Proc.Oem);
            lock (blocked) blocked.Clear();
        }
    }
}
