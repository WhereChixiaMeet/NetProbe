using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using static NetProbe.Tr;

namespace NetProbe
{
    public class TestChannel { public string Key, Name; public Prober P; public string BindIp; }

    public class ChannelResult
    {
        public double Ping = -1, Jitter = -1, Loss = -1, Down = -1, Up = -1, Dns = -1;
        public bool HasPing, HasDown, HasUp;
    }

    public static class DnsProbe
    {
        // Прямой UDP-запрос к DNS-серверу (минуя кэш ОС). Возвращает мс или -1.
        public static double Query(string server, string domain, string bindIp, int timeoutMs, out string err)
        {
            err = "";
            try
            {
                var id = (ushort)new Random(Guid.NewGuid().GetHashCode()).Next(1, 65535);
                var q = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
                foreach (var part in domain.Trim('.').Split('.'))
                {
                    var b = Encoding.ASCII.GetBytes(part); q.Add((byte)b.Length); q.AddRange(b);
                }
                q.AddRange(new byte[] { 0, 0, 1, 0, 1 });
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.ReceiveTimeout = timeoutMs;
                    if (!string.IsNullOrEmpty(bindIp)) s.Bind(new IPEndPoint(IPAddress.Parse(bindIp), 0));
                    var ep = new IPEndPoint(IPAddress.Parse(server), 53);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    s.SendTo(q.ToArray(), ep);
                    var buf = new byte[1500];
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    while (true)
                    {
                        int n = s.ReceiveFrom(buf, ref from);
                        if (n >= 12 && buf[0] == (byte)(id >> 8) && buf[1] == (byte)id)
                        {
                            double ms = sw.Elapsed.TotalMilliseconds;
                            int rcode = buf[3] & 0x0F;
                            int an = (buf[6] << 8) | buf[7];
                            if (rcode == 3) { err = "NXDOMAIN"; return -1; }
                            if (rcode != 0) { err = "RCODE " + rcode; return -1; }
                            if (an == 0) { err = "empty answer"; }
                            return ms;
                        }
                        if (sw.ElapsedMilliseconds > timeoutMs) { err = "timeout"; return -1; }
                    }
                }
            }
            catch (SocketException ex) { err = ex.SocketErrorCode == SocketError.TimedOut ? "timeout" : ex.SocketErrorCode.ToString(); return -1; }
            catch (Exception ex) { err = ex.Message; return -1; }
        }
    }

    public class TestRunner
    {
        public Settings St;
        public List<TestChannel> Channels = new List<TestChannel>();
        public CancelFlag Cf = new CancelFlag();
        public Dictionary<string, ChannelResult> Results = new Dictionary<string, ChannelResult>();

        // rowKey, rowTitle, channelKey, text, numeric value (NaN = нет), higherIsBetter (null = не сравнивать), level
        public Action<string, string, string, string, double, bool?, Level> Cell;
        public Action<string, double> Status;
        public Action<bool> PauseMonitors;

        ChannelResult R(TestChannel c) { lock (Results) { ChannelResult r; if (!Results.TryGetValue(c.Key, out r)) Results[c.Key] = r = new ChannelResult(); return r; } }
        bool Cancelled { get { return Cf.Cancelled; } }
        void Stat(string s, double p) { var h = Status; if (h != null) h(s, p); }
        void Put(string row, string title, TestChannel c, string text, double v, bool? hib, Level lv) { var h = Cell; if (h != null) h(row, title, c.Key, text, v, hib, lv); }

        static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        static string F1(double v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }
        static string Mbps(double bps) { double m = bps * 8 / 1e6; return m >= 100 ? F0(m) : m >= 10 ? F1(m) : m.ToString("0.00", CultureInfo.InvariantCulture); }

        Level LatLevel(double ms) { return ms < 0 ? Level.Down : ms < St.GoodMs ? Level.Good : ms < St.WarnMs ? Level.Fair : Level.Slow; }

        void Parallel(Action<TestChannel> a)
        {
            var ths = Channels.Select(c => { var t = new Thread(() => { try { a(c); } catch (Exception ex) { Logger.Write(LogLevel.Error, c.Name + ": " + ex); } }) { IsBackground = true }; t.Start(); return t; }).ToList();
            foreach (var t in ths) t.Join();
        }

        // ---------- Ping / jitter / loss ----------
        // Measured through each channel's own executable (HTTP request round-trip), not ping.exe:
        // per-app tunnelers route by process name, so ICMP from ping.exe can't be split between channels.
        public void Ping(double p0, double p1)
        {
            int n = St.PingCount;
            int done = 0, total = n * Channels.Count;
            Stat(Tx("Серия замеров ×") + n + Tx(" на канал…"), p0);
            Parallel(c =>
            {
                var res = new PingResult { Sent = n };
                string lastErr = "";
                for (int i = 0; i < n && !Cancelled; i++)
                {
                    var t0 = DateTime.Now;
                    var r = c.P.Http(St.ProbeUrl, Math.Min(St.TimeoutSec, 5));
                    if (r.Ok && r.Latency >= 0) res.Rtts.Add((int)Math.Round(Math.Max(1, r.Latency))); else lastErr = r.Error;
                    int d = Interlocked.Increment(ref done);
                    Stat(Tx("Серия замеров ×") + n + " per channel… " + d + "/" + total, p0 + (p1 - p0) * d / total);
                    int wait = 300 - (int)(DateTime.Now - t0).TotalMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
                if (Cancelled) return;
                Pinger.Compute(res);
                var cr = R(c);
                if (res.Recv > 0)
                {
                    cr.Ping = res.Avg; cr.Jitter = res.Jitter; cr.Loss = res.Loss; cr.HasPing = true;
                    Put("ping", Tx("Задержка (RTT, ср.)"), c, F0(res.Avg) + " ms   (min " + F0(res.Min) + " / max " + F0(res.Max) + ")", res.Avg, false, LatLevel(res.Avg));
                    Put("jitter", Tx("Джиттер"), c, F1(res.Jitter) + " ms", res.Jitter, false, res.Jitter < 10 ? Level.Good : res.Jitter < 30 ? Level.Fair : Level.Slow);
                }
                else
                {
                    cr.Loss = 100; cr.HasPing = true;
                    Put("ping", Tx("Задержка (RTT, ср.)"), c, Tx("нет ответов — ") + lastErr, double.NaN, null, Level.Down);
                    Put("jitter", Tx("Джиттер"), c, "—", double.NaN, null, Level.Off);
                }
                Put("loss", Tx("Потери запросов"), c, F1(res.Loss) + " %   (" + res.Recv + "/" + res.Sent + ")", res.Loss, false, res.Loss == 0 ? Level.Good : res.Loss < 2 ? Level.Fair : res.Loss < 10 ? Level.Slow : Level.Down);
                Logger.Write(LogLevel.Test, c.Name + ": latency avg " + (res.Avg < 0 ? "—" : F0(res.Avg)) + " ms, jitter " + (res.Jitter < 0 ? "—" : F1(res.Jitter)) + " ms, loss " + F1(res.Loss) + "%");
            });
        }

        // ---------- HTTP handshake breakdown ----------
        public void Handshake(double p0, double p1)
        {
            Stat(Tx("Разбор HTTPS-соединения…"), p0);
            Parallel(c =>
            {
                var list = new List<ProbeResult>();
                for (int i = 0; i < 5 && !Cancelled; i++) list.Add(c.P.Http(St.ProbeUrl, St.TimeoutSec));
                var ok = list.Where(x => x.Ok).ToList();
                if (ok.Count == 0) { Put("http", Tx("HTTPS-запрос"), c, Tx("ошибка — ") + list.Last().Error, double.NaN, null, Level.Down); return; }
                Func<Func<ProbeResult, double>, string> avg = f => { var v = ok.Select(f).Where(x => x >= 0).ToList(); return v.Count == 0 ? "—" : F0(v.Average()); };
                double lat = ok.Average(x => x.Latency);
                Put("http", "HTTPS: DNS · TCP · TLS · RTT", c, avg(x => x.Dns) + " · " + avg(x => x.Connect) + " · " + avg(x => x.Tls) + " · " + F0(lat) + " ms   (" + ok.Count + "/" + list.Count + " ok)", lat, false, LatLevel(lat));
            });
            Stat("", p1);
        }

        // ---------- DNS ----------
        public void Dns(double p0, double p1)
        {
            var domains = St.DnsList();
            if (domains.Count == 0) return;
            Stat(Tx("DNS: системный резолвер…"), p0);
            Parallel(c =>
            {
                var times = new List<double>(); var failed = new List<string>();
                foreach (var d in domains)
                {
                    if (Cancelled) return;
                    var r = c.P.Http("https://" + d + "/robots.txt", St.TimeoutSec, false, true);
                    if (r.Dns > 0 && r.Exit != 6) times.Add(r.Dns); else failed.Add(d);
                }
                double avg = times.Count > 0 ? times.Average() : -1;
                R(c).Dns = avg;
                string txt = times.Count == 0 ? "all lookups failed" : F0(avg) + " ms avg   (" + times.Count + "/" + domains.Count + " resolved" + (failed.Count > 0 ? "; failed: " + string.Join(", ", failed.Take(3)) : "") + ")";
                Put("dns", Tx("DNS (системный резолвер)"), c, txt, avg >= 0 ? avg : double.NaN, false, times.Count == 0 ? Level.Down : failed.Count > 0 ? Level.Slow : avg < 50 ? Level.Good : avg < 150 ? Level.Fair : Level.Slow);
                Logger.Write(LogLevel.Test, c.Name + ": DNS " + txt);
            });
            if (Cancelled) return;
            Stat(Tx("DNS: запросы DNS-over-HTTPS через каждый канал…"), p0 + (p1 - p0) * 0.5);
            var doh = new[] { Tuple.Create("dnssrv0", "DNS server 1.1.1.1 (Cloudflare, DoH)", "https://1.1.1.1/dns-query"), Tuple.Create("dnssrv1", "DNS server 8.8.8.8 (Google, DoH)", "https://8.8.8.8/dns-query") };
            foreach (var d in doh)
            {
                if (Cancelled) return;
                Parallel(c =>
                {
                    var cp = c.P as CurlProber;
                    if (cp == null) { Put(d.Item1, d.Item2, c, Tx("н/д в режиме PowerShell"), double.NaN, null, Level.Off); return; }
                    var t = new List<double>(); string lastErr = "";
                    int n = Math.Min(5, domains.Count);
                    foreach (var dom in domains.Take(n))
                    {
                        if (Cancelled) return;
                        var r = cp.Doh("https://" + dom + "/robots.txt", d.Item3, St.TimeoutSec);
                        if (r.Dns > 0 && r.Exit != 6 && r.Exit != 2) t.Add(r.Dns); else lastErr = r.Exit == 2 ? "curl too old for --doh-url" : r.Error;
                    }
                    if (t.Count == 0) Put(d.Item1, d.Item2, c, Tx("нет ответа (") + lastErr + ")", double.NaN, null, Level.Down);
                    else Put(d.Item1, d.Item2, c, F0(t.Average()) + " ms   (" + t.Count + "/" + n + " answered)", t.Average(), false, t.Count < n ? Level.Slow : t.Average() < 80 ? Level.Good : t.Average() < 200 ? Level.Fair : Level.Slow);
                });
            }
            Stat("", p1);
        }

        // ---------- Speed ----------
        public void Download(double p0, double p1)
        {
            PauseMonitors(true);
            try
            {
                int i = 0;
                foreach (var c in Channels)
                {
                    if (Cancelled) return;
                    double a = p0 + (p1 - p0) * i / Channels.Count;
                    Stat(Tx("Загрузка ") + St.DownloadMB + " MB × " + St.Streams + " streams — " + c.Name + "…", a);
                    long per = (long)St.DownloadMB * 1024 * 1024 / St.Streams;
                    var res = new ProbeResult[St.Streams];
                    var ths = new List<Thread>();
                    for (int s = 0; s < St.Streams; s++)
                    {
                        int si = s;
                        var t = new Thread(() => { res[si] = c.P.Download("https://speed.cloudflare.com/__down?bytes=" + per + "&r=" + Guid.NewGuid().ToString("N").Substring(0, 8), 60, Cf); }) { IsBackground = true };
                        t.Start(); ths.Add(t);
                    }
                    foreach (var t in ths) t.Join();
                    i++;
                    if (Cancelled) return;
                    double bytes = res.Sum(x => x.SizeDown), time = res.Max(x => x.Total);
                    var cr = R(c); cr.HasDown = true;
                    if (bytes < 256 * 1024 || time <= 0)
                    {
                        var err = res.FirstOrDefault(x => x.Error != "");
                        Put("down", Tx("Загрузка"), c, Tx("ошибка — ") + (err != null ? err.Error : Tx("нет данных")), double.NaN, null, Level.Down);
                        Logger.Write(LogLevel.Test, c.Name + ": download failed");
                        continue;
                    }
                    double bps = bytes / (time / 1000);
                    cr.Down = bps * 8 / 1e6;
                    Put("down", Tx("Загрузка"), c, Mbps(bps) + " Mbit/s   (" + F1(bytes / 1048576) + " MB in " + F1(time / 1000) + " s)", cr.Down, true, cr.Down >= 50 ? Level.Good : cr.Down >= 10 ? Level.Fair : Level.Slow);
                    Logger.Write(LogLevel.Test, c.Name + ": download " + Mbps(bps) + " Mbit/s");
                }
            }
            finally { PauseMonitors(false); }
        }

        public void Upload(double p0, double p1)
        {
            PauseMonitors(true);
            try
            {
                long per = (long)St.UploadMB * 1024 * 1024 / St.Streams;
                string file = Path.Combine(CurlProber.WorkDir, "netprobe_up_" + per + ".bin");
                if (!File.Exists(file) || new FileInfo(file).Length != per)
                {
                    var rnd = new Random(); var buf = new byte[1 << 16];
                    using (var fs = File.Create(file)) { long left = per; while (left > 0) { rnd.NextBytes(buf); int n = (int)Math.Min(buf.Length, left); fs.Write(buf, 0, n); left -= n; } }
                }
                int i = 0;
                foreach (var c in Channels)
                {
                    if (Cancelled) return;
                    Stat(Tx("Отдача ") + St.UploadMB + " MB × " + St.Streams + " streams — " + c.Name + "…", p0 + (p1 - p0) * i / Channels.Count);
                    var res = new ProbeResult[St.Streams];
                    var ths = new List<Thread>();
                    for (int s = 0; s < St.Streams; s++)
                    {
                        int si = s;
                        var t = new Thread(() => { res[si] = c.P.Upload("https://speed.cloudflare.com/__up", file, 60, Cf); }) { IsBackground = true };
                        t.Start(); ths.Add(t);
                    }
                    foreach (var t in ths) t.Join();
                    i++;
                    if (Cancelled) return;
                    double bytes = res.Where(x => x.Exit == 0 || x.SizeUp > 0).Sum(x => x.SizeUp), time = res.Max(x => x.Total);
                    var cr = R(c); cr.HasUp = true;
                    if (bytes < 128 * 1024 || time <= 0)
                    {
                        var err = res.FirstOrDefault(x => x.Error != "");
                        Put("up", Tx("Отдача"), c, Tx("ошибка — ") + (err != null ? err.Error : Tx("нет данных")), double.NaN, null, Level.Down);
                        continue;
                    }
                    double bps = bytes / (time / 1000);
                    cr.Up = bps * 8 / 1e6;
                    Put("up", Tx("Отдача"), c, Mbps(bps) + " Mbit/s   (" + F1(bytes / 1048576) + " MB in " + F1(time / 1000) + " s)", cr.Up, true, cr.Up >= 20 ? Level.Good : cr.Up >= 5 ? Level.Fair : Level.Slow);
                    Logger.Write(LogLevel.Test, c.Name + ": upload " + Mbps(bps) + " Mbit/s");
                }
            }
            finally { PauseMonitors(false); }
        }

        // ---------- MTU ----------
        public void Mtu(double p0, double p1)
        {
            Stat(Tx("Поиск MTU пути (ping.exe с флагом DF — ICMP не делится по приложениям)…"), p0);
            Parallel(c =>
            {
                int m = Pinger.FindMtu(St.PingHost, c.P.PingSource, Cf);
                if (Cancelled) return;
                if (m < 0) Put("mtu", Tx("MTU пути (ICMP)"), c, Tx("неизвестно (ICMP заблокирован)"), double.NaN, null, Level.Off);
                else Put("mtu", Tx("MTU пути (ICMP)"), c, m + " bytes" + (m < 1400 ? "   (low — possible fragmentation)" : ""), m, true, m >= 1400 ? Level.Good : m >= 1280 ? Level.Fair : Level.Slow);
            });
            Stat("", p1);
        }

        // ---------- Public IP / Geo ----------
        static string J(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        public void PublicIp(double p0, double p1)
        {
            Stat(Tx("Публичный IP и местоположение…"), p0);
            var ips = new Dictionary<string, string>();
            Parallel(c =>
            {
                var r = c.P.Http("http://ip-api.com/json/?fields=status,message,country,countryCode,city,isp,org,as,query", St.TimeoutSec + 3);
                string ip = "", geo = "", isp = "";
                if (r.Ok && J(r.Body, "status") == "success")
                {
                    ip = J(r.Body, "query"); geo = J(r.Body, "city") + ", " + J(r.Body, "country") + " (" + J(r.Body, "countryCode") + ")"; isp = J(r.Body, "isp"); string asn = J(r.Body, "as"); if (asn != "") isp += "  ·  " + asn;
                }
                else
                {
                    var t = c.P.Http(St.ProbeUrl, St.TimeoutSec);
                    if (t.Ok) { ip = t.Ip; geo = t.Loc + "  (Cloudflare colo " + t.Colo + ")"; }
                }
                lock (ips) ips[c.Key] = ip;
                if (ip == "") { Put("ip", Tx("Публичный IP"), c, Tx("недоступно — ") + r.Error, double.NaN, null, Level.Down); return; }
                Put("ip", Tx("Публичный IP"), c, ip, double.NaN, null, Level.Off);
                Put("geo", Tx("Местоположение"), c, geo, double.NaN, null, Level.Off);
                Put("isp", Tx("Провайдер / AS"), c, isp == "" ? "—" : isp, double.NaN, null, Level.Off);
                Logger.Write(LogLevel.Test, c.Name + ": public IP " + ip + " · " + geo + " · " + isp);
            });
            string a, b;
            if (ips.TryGetValue("vpn", out a) && ips.TryGetValue("direct", out b) && a != "" && a == b)
                Logger.Write(LogLevel.Warn, "VPN and Direct report the same public IP (" + a + ") — VPN is off, or traffic is not split.");
            Stat("", p1);
        }

        // ---------- Score ----------
        public void Score()
        {
            foreach (var c in Channels)
            {
                var r = R(c);
                if (!r.HasPing && !r.HasDown) continue;
                double s = 100;
                if (r.Ping >= 0) s -= Math.Min(40, Math.Max(0, (r.Ping - 20) / 4)); else s -= 40;
                if (r.Jitter >= 0) s -= Math.Min(20, r.Jitter);
                if (r.Loss >= 0) s -= Math.Min(30, r.Loss * 5);
                if (r.Down >= 0) s -= r.Down < 5 ? 20 : r.Down < 25 ? 10 : r.Down < 50 ? 5 : 0; else if (r.HasDown) s -= 25;
                s = Math.Max(0, Math.Min(100, s));
                string grade = s >= 90 ? "A" : s >= 75 ? "B" : s >= 60 ? "C" : s >= 40 ? "D" : "F";
                Put("score", Tx("Оценка качества"), c, F0(s) + Tx(" / 100   (класс ") + grade + ")", s, true, s >= 75 ? Level.Good : s >= 50 ? Level.Fair : Level.Slow);
                bool games = r.Ping >= 0 && r.Ping < 60 && r.Jitter < 15 && r.Loss < 1;
                bool calls = r.Ping >= 0 && r.Ping < 150 && r.Jitter < 30 && r.Loss < 2 && (r.Down < 0 || r.Down >= 3) && (r.Up < 0 || r.Up >= 2);
                bool hd = r.Down >= 25;
                Put("suit", Tx("Подходит для"), c, (games ? "✓" : "✗") + Tx(" игр   ") + (calls ? "✓" : "✗") + Tx(" видеозвонков   ") + (r.Down < 0 ? "?" : hd ? "✓" : "✗") + Tx(" 4K-видео"), double.NaN, null, Level.Off);
                Logger.Write(LogLevel.Test, c.Name + ": score " + F0(s));
            }
        }

        public void Full()
        {
            Results.Clear();
            Logger.Write(LogLevel.Test, Tx("Полный тест — старт (") + string.Join(", ", Channels.Select(c => c.Name)) + ")");
            PublicIp(0.00, 0.05); if (Cancelled) return;
            Handshake(0.05, 0.12); if (Cancelled) return;
            Ping(0.12, 0.42); if (Cancelled) return;
            Dns(0.42, 0.52); if (Cancelled) return;
            Download(0.52, 0.72); if (Cancelled) return;
            Upload(0.72, 0.88); if (Cancelled) return;
            Mtu(0.88, 0.99); if (Cancelled) return;
            Score();
            SaveHistory();
            Logger.Write(LogLevel.Test, Tx("Полный тест завершён"));
        }

        void SaveHistory()
        {
            try
            {
                string f = Path.Combine(Settings.Dir, "history.csv");
                bool head = !File.Exists(f);
                var sb = new StringBuilder();
                if (head) sb.Append("time;channel;ping_ms;jitter_ms;loss_pct;down_mbps;up_mbps;dns_ms\r\n");
                foreach (var c in Channels)
                {
                    var r = R(c);
                    sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(';').Append(c.Key).Append(';')
                      .Append(F1(r.Ping)).Append(';').Append(F1(r.Jitter)).Append(';').Append(F1(r.Loss)).Append(';')
                      .Append(F1(r.Down)).Append(';').Append(F1(r.Up)).Append(';').Append(F1(r.Dns)).Append("\r\n");
                }
                File.AppendAllText(f, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ---------- Site availability ----------
        public void Sites(List<string> sites, Action<string, string, ProbeResult> onResult)
        {
            var queue = new Queue<Tuple<string, TestChannel>>();
            foreach (var s in sites) foreach (var c in Channels) queue.Enqueue(Tuple.Create(s, c));
            int total = queue.Count, done = 0;
            var ths = new List<Thread>();
            for (int w = 0; w < 4; w++)
            {
                var t = new Thread(() =>
                {
                    while (!Cancelled)
                    {
                        Tuple<string, TestChannel> job;
                        lock (queue) { if (queue.Count == 0) return; job = queue.Dequeue(); }
                        var r = job.Item2.P.Http(job.Item1, St.TimeoutSec + 3, true, true, Cf);
                        if (Cancelled) return;
                        onResult(job.Item1, job.Item2.Key, r);
                        int d = Interlocked.Increment(ref done);
                        Stat(Tx("Проверено ") + d + " / " + total, (double)d / total);
                    }
                }) { IsBackground = true };
                t.Start(); ths.Add(t);
            }
            foreach (var t in ths) t.Join();
        }
    }
}
