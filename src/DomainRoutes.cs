using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using static NetProbe.Tr;

namespace NetProbe
{
    // Домены под приложением в Include DPI: «brave.exe в Include VPN + youtube.com под brave.exe в Include DPI»
    // → весь brave через VPN, кроме youtube.com — он идёт мимо туннеля под DPI (winws).
    //
    // WireSock умеет только DisallowedIPs (IP/подсети, без доменов и без привязки к приложению), поэтому:
    //  • домены разворачиваются в «семью» (youtube.com → googlevideo.com, ytimg.com …),
    //  • IP берутся из DNS при подключении и из кэша DNS Windows (видео-серверы googlevideo заранее не известны),
    //    выученные IP хранятся в config\runtime\domain-ips.txt и попадают в DisallowedIPs при следующем перезапуске туннеля;
    //  • DisallowedIPs действует на все приложения в туннеле, не только на это.
    // sing-box (TUN «Серверов») умеет «процесс + домен» напрямую — там всё точно.
    public static class DomainRoutes
    {
        static readonly Dictionary<string, string[]> Families = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "youtube.com", new[] { "youtube.com", "youtu.be", "googlevideo.com", "ytimg.com", "ggpht.com", "youtube-nocookie.com", "youtubei.googleapis.com", "youtube.googleapis.com", "yt.be" } },
            { "discord.com", new[] { "discord.com", "discord.gg", "discordapp.com", "discordapp.net", "discord.media", "discordcdn.com", "discord.co", "discord.app" } },
            { "instagram.com", new[] { "instagram.com", "cdninstagram.com", "ig.me", "instagr.am" } },
            { "facebook.com", new[] { "facebook.com", "fbcdn.net", "fb.com", "facebook.net", "fbsbx.com" } },
            { "twitter.com", new[] { "twitter.com", "x.com", "twimg.com", "t.co" } },
        };
        static DomainRoutes()
        {
            Families["youtu.be"] = Families["youtube.com"]; Families["googlevideo.com"] = Families["youtube.com"];
            Families["discord.gg"] = Families["discord.com"]; Families["discordapp.com"] = Families["discord.com"];
            Families["x.com"] = Families["twitter.com"];
        }

        // домен + связанные с ним (CDN), без «www.» и «*.»
        public static List<string> Expand(string domain)
        {
            string d = Norm(domain);
            var res = new List<string> { d };
            foreach (var kv in Families)
                if (d.Equals(kv.Key, StringComparison.OrdinalIgnoreCase) || d.EndsWith("." + kv.Key, StringComparison.OrdinalIgnoreCase)) res.AddRange(kv.Value);
            return res.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static string Norm(string d) { d = (d ?? "").Trim().TrimStart('*', '.').ToLowerInvariant(); return d.StartsWith("www.") ? d.Substring(4) : d; }

        static bool Matches(string host, IEnumerable<string> suffixes)
        {
            host = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            foreach (var s in suffixes) if (host == s || host.EndsWith("." + s)) return true;
            return false;
        }

        // ---------- выученные IP ----------
        static readonly object lk = new object();
        static Dictionary<string, KeyValuePair<string, DateTime>> learned; // ip → (домен, когда видели)
        static string File_ { get { return Path.Combine(Settings.Sub("runtime"), "domain-ips.txt"); } }
        public static int Version; // растёт, когда появляются новые IP

        static void Load()
        {
            if (learned != null) return;
            learned = new Dictionary<string, KeyValuePair<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(File_)) return;
                foreach (var l in File.ReadAllLines(File_))
                {
                    var p = l.Split(' '); long t;
                    if (p.Length >= 3 && ExclusionSet.IsIp(p[0]) && long.TryParse(p[2], out t)) learned[p[0]] = new KeyValuePair<string, DateTime>(p[1], new DateTime(t));
                }
            }
            catch { }
        }

        static void Save()
        {
            try
            {
                var cut = DateTime.Now.AddDays(-14); // старые адреса забываем
                var lines = learned.Where(kv => kv.Value.Value > cut).OrderByDescending(kv => kv.Value.Value).Take(3000).Select(kv => kv.Key + " " + kv.Value.Key + " " + kv.Value.Value.Ticks).ToArray();
                File.WriteAllLines(File_, lines);
            }
            catch { }
        }

        static bool Remember(string ip, string host)
        {
            bool isNew = !learned.ContainsKey(ip);
            learned[ip] = new KeyValuePair<string, DateTime>(host, DateTime.Now);
            return isNew;
        }

        // IP для DisallowedIPs: IP/подсети, заданные прямо, + выученные + свежий DNS-запрос (до ~3 с)
        public static List<string> IpsFor(IEnumerable<string> entries)
        {
            var list = entries.ToList();
            var res = new List<string>();
            var doms = new List<string>();
            foreach (var e in list) { if (ExclusionSet.IsIp(e)) res.Add(e.Contains("/") ? e : e + (e.Contains(":") ? "/128" : "/32")); else doms.AddRange(Expand(e)); }
            doms = doms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (doms.Count == 0) return res.Distinct().ToList();
            var hosts = doms.Concat(doms.Select(d => "www." + d)).Concat(doms.Where(d => d == "youtube.com").Select(d => "m." + d)).ToList();
            var tasks = hosts.Select(h => { try { return Tuple.Create(h, Dns.GetHostAddressesAsync(h)); } catch { return null; } }).Where(t => t != null).ToList();
            try { Task.WaitAll(tasks.Select(t => (Task)t.Item2).ToArray(), 3000); } catch { }
            lock (lk)
            {
                Load();
                bool changed = false;
                foreach (var t in tasks)
                {
                    if (t.Item2.Status != TaskStatus.RanToCompletion) continue;
                    foreach (var a in t.Item2.Result) if (Usable(a)) changed |= Remember(a.ToString(), t.Item1);
                }
                if (changed) Save();
                foreach (var kv in learned) if (Matches(kv.Value.Key, doms)) res.Add(kv.Key + (kv.Key.Contains(":") ? "/128" : "/32"));
            }
            return res.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // для окна «Маршрут»: какой домен выучен за этим IP / относится ли IP к домену (с его «семьёй»)
        public static string LearnedHost(string ip)
        {
            lock (lk) { Load(); KeyValuePair<string, DateTime> v; return learned.TryGetValue(ip, out v) ? v.Key : null; }
        }
        public static bool IsLearned(IPAddress ip, string domain)
        {
            if (ip == null) return false;
            string h = LearnedHost(ip.ToString());
            return h != null && Matches(h, Expand(domain));
        }

        static bool Usable(IPAddress a)
        {
            if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return false;
            if (a.AddressFamily == AddressFamily.InterNetworkV6) return !a.IsIPv6LinkLocal && !a.IsIPv6SiteLocal;
            var b = a.GetAddressBytes();
            return !(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || b[0] == 0 || b[0] >= 224 || (b[0] == 198 && b[1] == 18));
        }

        // Кэш DNS Windows: имена, похожие на домены из списков, → их IP. Возвращает число новых адресов.
        public static int Learn(IEnumerable<string> entries)
        {
            var doms = entries.Where(e => !ExclusionSet.IsIp(e)).SelectMany(Expand).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (doms.Count == 0) return 0;
            var found = new List<KeyValuePair<string, string>>();
            try { found = DnsCache.Read(h => Matches(h, doms)); } catch { }
            int n = 0;
            lock (lk)
            {
                Load();
                foreach (var kv in found) { IPAddress a; if (IPAddress.TryParse(kv.Value, out a) && Usable(a) && Remember(a.ToString(), kv.Key)) n++; }
                if (found.Count > 0) Save();
            }
            if (n > 0) Interlocked.Increment(ref Version);
            return n;
        }
    }

    // Чтение кэша DNS-клиента Windows: DnsGetCacheDataTable (имена) + DnsQuery без запроса в сеть (адреса из кэша)
    static class DnsCache
    {
        [StructLayout(LayoutKind.Sequential)]
        struct CacheEntry { public IntPtr Next; public IntPtr Name; public ushort Type; public ushort DataLength; public uint Flags; }

        [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable")] static extern bool DnsGetCacheDataTable(out IntPtr table);
        [DllImport("dnsapi.dll", EntryPoint = "DnsQuery_W", CharSet = CharSet.Unicode)] static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);
        [DllImport("dnsapi.dll")] static extern void DnsRecordListFree(IntPtr list, int freeType);
        [DllImport("dnsapi.dll")] static extern void DnsFree(IntPtr data, int freeType);

        const ushort A = 1, AAAA = 28;
        const uint NoWireQuery = 0x10;

        public static List<KeyValuePair<string, string>> Read(Func<string, bool> want)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr t;
            if (DnsGetCacheDataTable(out t) && t != IntPtr.Zero)
            {
                for (IntPtr p = t; p != IntPtr.Zero; )
                {
                    var e = (CacheEntry)Marshal.PtrToStructure(p, typeof(CacheEntry));
                    string n = e.Name != IntPtr.Zero ? Marshal.PtrToStringUni(e.Name) : null;
                    if (!string.IsNullOrEmpty(n) && want(n)) names.Add(n);
                    var next = e.Next;
                    try { if (e.Name != IntPtr.Zero) DnsFree(e.Name, 0); DnsFree(p, 0); } catch { }
                    p = next;
                }
            }
            var res = new List<KeyValuePair<string, string>>();
            foreach (var n in names.Take(500))
                foreach (var type in new[] { A, AAAA })
                {
                    IntPtr r;
                    if (DnsQuery(n, type, NoWireQuery, IntPtr.Zero, out r, IntPtr.Zero) != 0 || r == IntPtr.Zero) continue;
                    try
                    {
                        // DNS_RECORD: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, Data
                        int off = IntPtr.Size * 2;
                        for (IntPtr p = r; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p))
                        {
                            ushort rt = (ushort)Marshal.ReadInt16(p, off);
                            if (rt == A) res.Add(new KeyValuePair<string, string>(n, new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(p, off + 16))).ToString()));
                            else if (rt == AAAA) { var b = new byte[16]; Marshal.Copy(p + off + 16, b, 0, 16); res.Add(new KeyValuePair<string, string>(n, new IPAddress(b).ToString())); }
                        }
                    }
                    finally { try { DnsRecordListFree(r, 1); } catch { } }
                }
            return res;
        }
    }
}
