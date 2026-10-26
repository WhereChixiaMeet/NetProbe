using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NetProbe
{
    public class CancelFlag { public volatile bool Cancelled; }

    public static class Proc
    {
        public class Res { public int Exit = -1; public string Out = "", Err = "", StartError; public bool TimedOut, Cancelled; }

        public static Res Run(string file, string args, int timeoutMs, Encoding enc = null, CancelFlag cf = null, string stdin = null)
        {
            var r = new Res();
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = enc ?? Encoding.UTF8,
                StandardErrorEncoding = enc ?? Encoding.UTF8,
                RedirectStandardInput = stdin != null
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    var so = new StringBuilder(); var se = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (so) so.Append(e.Data).Append('\n'); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (se) se.Append(e.Data).Append('\n'); };
                    p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (stdin != null)
                    {
                        try
                        {
                            var bytes = new UTF8Encoding(false).GetBytes(stdin);
                            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                            p.StandardInput.Close();
                        }
                        catch { }
                    }
                    var sw = Stopwatch.StartNew();
                    bool exited = false;
                    while (!(exited = p.WaitForExit(100)))
                    {
                        if (cf != null && cf.Cancelled) { r.Cancelled = true; break; }
                        if (sw.ElapsedMilliseconds > timeoutMs) { r.TimedOut = true; break; }
                    }
                    if (!exited) { KillTree(p); try { p.WaitForExit(3000); } catch { } }
                    else { try { p.WaitForExit(); } catch { } }
                    try { r.Exit = p.HasExited ? p.ExitCode : -1; } catch { }
                    lock (so) r.Out = so.ToString();
                    lock (se) r.Err = se.ToString();
                }
            }
            catch (Exception ex) { r.StartError = ex.Message; }
            return r;
        }

        static void KillTree(Process p)
        {
            try
            {
                var k = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
                if (k != null) k.WaitForExit(3000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public static Encoding Oem
        {
            get { try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); } catch { return Encoding.Default; } }
        }
    }

    public class ProbeResult
    {
        public bool Ok;
        public int Code, Exit = -1;
        public double Dns = -1, Connect = -1, Tls = -1, Ttfb = -1, Total = -1, Lat = -1;
        public double SpeedDown, SpeedUp, SizeDown, SizeUp;
        public string RemoteIp = "", Ip = "", Loc = "", Colo = "", Error = "", Body = "";
        public DateTime Time = DateTime.Now;
        public double Latency { get { return Lat >= 0 ? Lat : Total; } }
    }

    public abstract class Prober
    {
        public string PingSource;   // IP источника для ping -S (null = маршрут по умолчанию)
        public abstract string Describe { get; }
        public abstract ProbeResult Http(string url, int timeoutSec, bool follow = false, bool discardBody = false, CancelFlag cf = null);
        public abstract ProbeResult Download(string url, int timeoutSec, CancelFlag cf);
        public abstract ProbeResult Upload(string url, string file, int timeoutSec, CancelFlag cf);

        protected static double D(string s)
        {
            double v;
            if (double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return -1;
        }

        protected static void ParseTrace(ProbeResult r)
        {
            foreach (var line in r.Body.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("ip=")) r.Ip = l.Substring(3);
                else if (l.StartsWith("loc=")) r.Loc = l.Substring(4);
                else if (l.StartsWith("colo=")) r.Colo = l.Substring(5);
            }
        }

        // Формат вывода: тело, затем строка @@NPX@@code|dns|connect|tls|lat|total|rip|spdDown|spdUp|sizeDown|sizeUp (мс, байт/с)
        protected static void ParseX(ProbeResult r, string output)
        {
            int ie = output.IndexOf("@@NPERR@@", StringComparison.Ordinal);
            if (ie >= 0) { r.Error = output.Substring(ie + 9).Split('\n')[0].Trim(); output = output.Substring(0, ie); }
            int i = output.LastIndexOf("@@NPX@@", StringComparison.Ordinal);
            if (i < 0) { if (r.Error == "") r.Error = "No response from shell"; return; }
            r.Body = output.Substring(0, i);
            var f = output.Substring(i + 7).Split('\n')[0].Trim().Split('|');
            if (f.Length < 11) return;
            r.Code = (int)Math.Max(0, D(f[0]));
            r.Dns = D(f[1]); r.Connect = D(f[2]); r.Tls = D(f[3]); r.Lat = D(f[4]); r.Ttfb = r.Lat; r.Total = D(f[5]);
            r.RemoteIp = f[6]; r.SpeedDown = Math.Max(0, D(f[7])); r.SpeedUp = Math.Max(0, D(f[8]));
            r.SizeDown = Math.Max(0, D(f[9])); r.SizeUp = Math.Max(0, D(f[10]));
            ParseTrace(r);
        }
    }

    public class CurlProber : Prober
    {
        public string CurlPath = "curl.exe", BindIp, Proxy;
        public bool ViaShell, NoProxy;
        public static string FmtFile;

        // curl.exe gets file paths on its command line, so keep them ASCII-only (user names may be Cyrillic)
        public static string WorkDir
        {
            get
            {
                foreach (var d in new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetProbe"), Path.GetTempPath() })
                {
                    try
                    {
                        if (d.Any(ch => ch > 127)) continue;
                        Directory.CreateDirectory(d);
                        string t = Path.Combine(d, "w.tmp"); File.WriteAllText(t, "1"); File.Delete(t);
                        return d;
                    }
                    catch { }
                }
                return Path.GetTempPath();
            }
        }

        public static void EnsureFormatFile()
        {
            try
            {
                FmtFile = Path.Combine(WorkDir, "netprobe_fmt.txt");
                File.WriteAllText(FmtFile, "\n@@NP@@%{http_code}|%{time_namelookup}|%{time_connect}|%{time_appconnect}|%{time_pretransfer}|%{time_starttransfer}|%{time_total}|%{remote_ip}|%{speed_download}|%{speed_upload}|%{size_download}|%{size_upload}\n", new UTF8Encoding(false));
            }
            catch { }
        }

        public override string Describe
        {
            get
            {
                string s = ViaShell ? "cmd.exe → " + Path.GetFileName(CurlPath) : Path.GetFileName(CurlPath);
                if (!string.IsNullOrEmpty(BindIp)) s += " · source " + BindIp;
                if (!string.IsNullOrEmpty(Proxy)) s += " · proxy " + Proxy;
                return s;
            }
        }

        string Common(int t)
        {
            var sb = new StringBuilder();
            sb.Append("-s -S --ssl-no-revoke -A \"NetProbe/1.0\" --connect-timeout ").Append(t).Append(" --max-time ").Append(t);
            if (!string.IsNullOrEmpty(BindIp)) sb.Append(" -4 --interface ").Append(BindIp);
            if (!string.IsNullOrEmpty(Proxy)) sb.Append(" -x \"").Append(Proxy).Append('"');
            else if (NoProxy) sb.Append(" --noproxy \"*\"");
            sb.Append(" -w @\"").Append(FmtFile).Append('"');
            return sb.ToString();
        }

        public override ProbeResult Http(string url, int t, bool follow, bool discard, CancelFlag cf)
        {
            string a = Common(t) + " -H \"Cache-Control: no-cache\"" + (follow ? " -L --max-redirs 5" : "") + (discard ? " -o NUL" : "") + " \"" + url + "\"";
            return Exec(a, t, cf);
        }

        // DNS-over-HTTPS lookup done by curl itself (so it runs inside this channel's process)
        public ProbeResult Doh(string url, string dohUrl, int t)
        {
            return Exec(Common(t) + " --doh-url \"" + dohUrl + "\" -o NUL \"" + url + "\"", t, null);
        }

        public override ProbeResult Download(string url, int t, CancelFlag cf)
        {
            return Exec(Common(t) + " -o NUL \"" + url + "\"", t, cf);
        }

        public override ProbeResult Upload(string url, string file, int t, CancelFlag cf)
        {
            return Exec(Common(t) + " -o NUL -X POST -H \"Content-Type: application/octet-stream\" --data-binary @\"" + file + "\" \"" + url + "\"", t, cf);
        }

        ProbeResult Exec(string args, int t, CancelFlag cf)
        {
            var r = new ProbeResult();
            Proc.Res pr;
            if (ViaShell) pr = Proc.Run("cmd.exe", "/d /s /c \"\"" + CurlPath + "\" " + args + "\"", t * 1000 + 5000, null, cf);
            else pr = Proc.Run(CurlPath, args, t * 1000 + 5000, null, cf);
            r.Exit = pr.Exit;
            if (pr.StartError != null) { r.Error = (ViaShell ? "cmd.exe: " : "curl.exe: ") + pr.StartError; return r; }
            if (pr.Cancelled) { r.Error = "Cancelled"; return r; }
            string o = pr.Out;
            int i = o.LastIndexOf("@@NP@@", StringComparison.Ordinal);
            if (i >= 0)
            {
                r.Body = o.Substring(0, i);
                var f = o.Substring(i + 6).Split('\n')[0].Trim().Split('|');
                if (f.Length >= 12)
                {
                    r.Code = (int)Math.Max(0, D(f[0]));
                    double nl = D(f[1]), co = D(f[2]), ap = D(f[3]), pre = D(f[4]), st = D(f[5]), tot = D(f[6]);
                    r.Dns = nl * 1000;
                    r.Connect = co > 0 ? (co - Math.Max(0, nl)) * 1000 : -1;
                    r.Tls = ap > 0 ? (ap - co) * 1000 : -1;
                    r.Ttfb = st * 1000;
                    r.Total = tot * 1000;
                    r.Lat = (st > 0 && pre >= 0 && st >= pre) ? (st - pre) * 1000 : -1;
                    r.RemoteIp = f[7];
                    r.SpeedDown = Math.Max(0, D(f[8])); r.SpeedUp = Math.Max(0, D(f[9]));
                    r.SizeDown = Math.Max(0, D(f[10])); r.SizeUp = Math.Max(0, D(f[11]));
                }
                ParseTrace(r);
            }
            if (r.Exit != 0)
            {
                string err = pr.Err.Trim();
                int c = err.LastIndexOf("curl:", StringComparison.Ordinal);
                if (c >= 0) err = err.Substring(c).Trim();
                r.Error = CurlError(r.Exit, pr.TimedOut) + (err.Length > 0 && err.Length < 160 ? " (" + err.Replace("\n", " ") + ")" : "");
                if (i < 0 && r.Exit == 1 && ViaShell) r.Error = "curl.exe not found or failed to start via cmd.exe";
            }
            r.Ok = r.Exit == 0 && r.Code > 0 && r.Code < 500;
            if (r.Exit == 0 && !r.Ok && r.Error == "") r.Error = "HTTP " + r.Code;
            return r;
        }

        public static string CurlError(int code, bool timedOut)
        {
            if (timedOut) return "Process timeout";
            switch (code)
            {
                case 5: return "Could not resolve proxy";
                case 6: return "DNS error: host not resolved";
                case 7: return "Connection failed";
                case 9: case 35: return "TLS handshake failed";
                case 28: return "Timeout";
                case 45: return "Interface bind failed";
                case 47: return "Too many redirects";
                case 52: return "Empty reply from server";
                case 55: return "Send failure";
                case 56: return "Receive failure (connection reset)";
                case 60: return "Certificate error";
                case 97: return "Proxy error";
                case -1: return "Process did not exit";
                default: return "curl error " + code;
            }
        }
    }

    public class PsProber : Prober
    {
        public static string PsExe = "powershell.exe";
        public override string Describe { get { return "PowerShell → .NET (outside curl.exe)"; } }

        static string Q(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        const string Head =
"$ErrorActionPreference='Stop'\n" +
"try{[Console]::OutputEncoding=[Text.Encoding]::UTF8}catch{}\n" +
"try{[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12 -bor 12288}catch{try{[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12}catch{}}\n" +
"[Net.ServicePointManager]::Expect100Continue=$false\n" +
"$ci=[Globalization.CultureInfo]::InvariantCulture\n" +
"function F($x){ return ([double]$x).ToString('0.###',$ci) }\n" +
"function Req($u,$m){ $q=[Net.HttpWebRequest]::Create($u); $q.Timeout=$T; $q.ReadWriteTimeout=$T; $q.Proxy=$null; $q.UserAgent='NetProbe/1.0'; $q.KeepAlive=$true; $q.Method=$m; $q.AllowAutoRedirect=$FOLLOW; $q.Headers.Add('Cache-Control','no-cache'); return $q }\n" +
"function ErrOf($e){ while($e.InnerException -and -not ($e -is [Net.WebException])){ $e=$e.InnerException }; return $e }\n" +
"try{[void][Net.Dns]::GetHostAddresses('localhost'); [void][Net.HttpWebRequest]::Create('http://localhost/')}catch{}\n" +
"$sw=[Diagnostics.Stopwatch]::StartNew()\n" +
"$dns=-1;$con=-1;$lat=-1;$tot=-1;$code=0;$rip='';$body='';$err='';$sd=0;$su=0;$nd=0;$nu=0\n";

        const string Tail = "\n$body\n\"@@NPX@@$code|$(F $dns)|$(F $con)|-1|$(F $lat)|$(F $tot)|$rip|$(F $sd)|$(F $su)|$(F $nd)|$(F $nu)\"\nif($err){\"@@NPERR@@$err\"}\n";

        const string Resolve =
" $d0=$sw.Elapsed.TotalMilliseconds; $all=[Net.Dns]::GetHostAddresses($u.Host); $dns=$sw.Elapsed.TotalMilliseconds-$d0\n" +
" $a=$null; foreach($x in $all){ if($x.AddressFamily -eq 'InterNetwork'){ $a=$x; break } }\n" +
" if(-not $a){throw 'DNS: no IPv4 address'}\n" +
" $rip=$a.ToString()\n";

        static string Script(string url, int t, bool follow, string body)
        {
            return "$u=[Uri]" + Q(url) + "; $T=" + (t * 1000) + "; $FOLLOW=$" + (follow ? "true" : "false") + "\n" + Head +
                "try{\n" + Resolve + body + "\n}catch{ $e=ErrOf $_.Exception; if($e -is [Net.WebException] -and $e.Response){ $code=[int]$e.Response.StatusCode; if($tot -lt 0){$tot=$sw.Elapsed.TotalMilliseconds}; if($lat -lt 0){$lat=$tot} } else { $err=$e.Message -replace '[\\r\\n|]',' ' } }" + Tail;
        }

        public override ProbeResult Http(string url, int t, bool follow, bool discard, CancelFlag cf)
        {
            string b =
" $tc=New-Object Net.Sockets.TcpClient; $c0=$sw.Elapsed.TotalMilliseconds\n" +
" $ar=$tc.BeginConnect($a,$u.Port,$null,$null)\n" +
" if(-not $ar.AsyncWaitHandle.WaitOne($T)){ $tc.Close(); throw 'TCP connect timeout' }\n" +
" $tc.EndConnect($ar); $con=$sw.Elapsed.TotalMilliseconds-$c0; $tc.Close()\n" +
" $t0=$sw.Elapsed.TotalMilliseconds; $r=(Req $u 'GET').GetResponse(); $sr=New-Object IO.StreamReader($r.GetResponseStream()); $body=$sr.ReadToEnd(); $r.Close(); $tot=$sw.Elapsed.TotalMilliseconds-$t0; $code=[int]$r.StatusCode\n" +
" try{ $t1=$sw.Elapsed.TotalMilliseconds; $r=(Req $u 'GET').GetResponse(); $lat=$sw.Elapsed.TotalMilliseconds-$t1; $code=[int]$r.StatusCode; $r.Close() }catch{ $lat=$tot }\n" +
(discard ? " $body=''\n" : "");
            return Run(Script(url, t, follow, b), t, cf);
        }

        public override ProbeResult Download(string url, int t, CancelFlag cf)
        {
            string b =
" $t0=$sw.Elapsed.TotalMilliseconds; $r=(Req $u 'GET').GetResponse(); $lat=$sw.Elapsed.TotalMilliseconds-$t0; $code=[int]$r.StatusCode\n" +
" $s=$r.GetResponseStream(); $buf=New-Object byte[] 262144\n" +
" while(($k=$s.Read($buf,0,$buf.Length)) -gt 0){ $nd+=$k; if($sw.ElapsedMilliseconds -gt $T){break} }\n" +
" $r.Close(); $tot=$sw.Elapsed.TotalMilliseconds-$t0; if($tot -gt 0){$sd=$nd/($tot/1000)}\n";
            return Run(Script(url, t, true, b), t, cf);
        }

        public override ProbeResult Upload(string url, string file, int t, CancelFlag cf)
        {
            string b =
" $fs=[IO.File]::OpenRead(" + Q(file) + "); $q=Req $u 'POST'; $q.ContentType='application/octet-stream'; $q.ContentLength=$fs.Length; $q.AllowWriteStreamBuffering=$false\n" +
" $t0=$sw.Elapsed.TotalMilliseconds; $st=$q.GetRequestStream(); $fs.CopyTo($st,262144); $st.Close(); $nu=$fs.Length; $fs.Close()\n" +
" $r=$q.GetResponse(); $code=[int]$r.StatusCode; $r.Close(); $tot=$sw.Elapsed.TotalMilliseconds-$t0; if($tot -gt 0){$su=$nu/($tot/1000)}\n";
            return Run(Script(url, t, true, b), t, cf);
        }

        ProbeResult Run(string script, int t, CancelFlag cf)
        {
            var r = new ProbeResult();
            // the script is passed through stdin, not on the command line
            var pr = Proc.Run(PsExe, "-NoProfile -NonInteractive -Command \"[Console]::InputEncoding=[Text.Encoding]::UTF8; & ([scriptblock]::Create([Console]::In.ReadToEnd()))\"", t * 1000 + 15000, Encoding.UTF8, cf, script);
            r.Exit = pr.Exit;
            if (pr.StartError != null) { r.Error = "powershell.exe: " + pr.StartError; return r; }
            if (pr.Cancelled) { r.Error = "Cancelled"; return r; }
            ParseX(r, pr.Out);
            if (r.Error == "" && pr.TimedOut) r.Error = "Process timeout";
            if (r.Error == "" && r.Code == 0 && pr.Err.Trim().Length > 0) r.Error = pr.Err.Trim().Split('\n')[0];
            r.Ok = r.Error == "" && r.Code > 0 && r.Code < 500;
            if (!r.Ok && r.Error == "") r.Error = "HTTP " + r.Code;
            return r;
        }
    }

    public class PingResult
    {
        public int Sent, Recv;
        public List<int> Rtts = new List<int>();
        public double Avg = -1, Min = -1, Max = -1, Jitter = -1, Loss = 100;
        public string Error = "";
    }

    public static class Pinger
    {
        static readonly Regex RttRx = new Regex(@"[=<]\s*(\d+)\s*(ms|мс)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static PingResult Run(string host, int count, string src, CancelFlag cf)
        {
            var res = new PingResult { Sent = count };
            string args = "-n " + count + " -w 1000 -4 " + (string.IsNullOrEmpty(src) ? "" : "-S " + src + " ") + host;
            var pr = Proc.Run("ping.exe", args, count * 1500 + 5000, Proc.Oem, cf);
            if (pr.StartError != null) { res.Error = "ping.exe: " + pr.StartError; return res; }
            if (pr.Cancelled) { res.Error = "Cancelled"; return res; }
            foreach (var line in pr.Out.Split('\n'))
            {
                if (line.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var m = RttRx.Match(line);
                if (!m.Success) continue;
                int v = int.Parse(m.Groups[1].Value);
                res.Rtts.Add(Math.Max(v, line.Contains("<") ? 1 : v));
            }
            Compute(res);
            if (res.Recv == 0)
            {
                string o = pr.Out.Trim();
                var lines = o.Split('\n').Where(x => x.Trim().Length > 0).ToArray();
                res.Error = lines.Length > 1 ? lines[1].Trim() : "No replies";
                if (res.Error.Length > 120) res.Error = res.Error.Substring(0, 120);
            }
            return res;
        }

        public static void Compute(PingResult res)
        {
            res.Recv = res.Rtts.Count;
            res.Loss = res.Sent > 0 ? 100.0 * (res.Sent - res.Recv) / res.Sent : 100;
            if (res.Recv > 0)
            {
                res.Avg = res.Rtts.Average(); res.Min = res.Rtts.Min(); res.Max = res.Rtts.Max();
                double j = 0; for (int i = 1; i < res.Rtts.Count; i++) j += Math.Abs(res.Rtts[i] - res.Rtts[i - 1]);
                res.Jitter = res.Rtts.Count > 1 ? j / (res.Rtts.Count - 1) : 0;
            }
        }

        static bool Df(string host, int size, string src, CancelFlag cf)
        {
            string args = "-n 1 -w 1500 -4 -f -l " + size + " " + (string.IsNullOrEmpty(src) ? "" : "-S " + src + " ") + host;
            var pr = Proc.Run("ping.exe", args, 6000, Proc.Oem, cf);
            return pr.Out.IndexOf("TTL=", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Возвращает MTU пути (payload + 28) либо -1
        public static int FindMtu(string host, string src, CancelFlag cf)
        {
            int lo = 500, hi = 1472, best = -1;
            if (!Df(host, lo, src, cf) && !Df(host, lo, src, cf)) return -1;
            best = lo; lo++;
            while (lo <= hi)
            {
                if (cf != null && cf.Cancelled) return -1;
                int mid = (lo + hi) / 2;
                if (Df(host, mid, src, cf) || Df(host, mid, src, cf)) { best = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return best + 28;
        }
    }

    public class AdapterInfo
    {
        public string Id, Name, Desc, Ip, Gateway;
        public int Index = -1;
        public bool IsVpn, IsVirtual, IsCandidate;
        public NetworkInterfaceType Type;
        public override string ToString() { return Name + " — " + Ip; }
    }

    public static class Adapters
    {
        static readonly string[] VpnKeys = { "vpn", "wireguard", "wintun", "openvpn", "openconnect", "tap-", "tap0", "tun", "ppp", "amnezia", "outline", "v2ray", "xray", "sing-box", "singbox", "hiddify", "clash", "mihomo", "nekoray", "throne", "zerotier", "tailscale", "hamachi", "radmin", "softether", "fortinet", "forticlient", "anyconnect", "cisco", "globalprotect", "pangp", "juniper", "pulse secure", "nord", "proton", "surfshark", "windscribe", "mullvad", "kaspersky", "psiphon", "lantern", "adguard", "warp", "cloudflare", "sstp", "l2tp", "pptp", "ikev2", "happ" };
        static readonly string[] VirtKeys = { "virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "loopback", "bluetooth", "npcap", "pseudo", "docker", "wsl", "kernel debug" };

        [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint dest, out uint index);

        public static List<AdapterInfo> List()
        {
            var list = new List<AdapterInfo>();
            NetworkInterface[] all;
            try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { return list; }
            foreach (var ni in all)
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    var p = ni.GetIPProperties();
                    var ua = p.UnicastAddresses.FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !u.Address.ToString().StartsWith("169.254."));
                    if (ua == null) continue;
                    var gw = p.GatewayAddresses.FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                    var a = new AdapterInfo { Id = ni.Id, Name = ni.Name, Desc = ni.Description, Ip = ua.Address.ToString(), Gateway = gw != null ? gw.Address.ToString() : null, Type = ni.NetworkInterfaceType };
                    try { a.Index = p.GetIPv4Properties().Index; } catch { }
                    string s = (ni.Name + " " + ni.Description).ToLowerInvariant();
                    a.IsVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Ppp || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel || VpnKeys.Any(k => s.Contains(k));
                    a.IsVirtual = VirtKeys.Any(k => s.Contains(k));
                    bool physType = a.Type == NetworkInterfaceType.Ethernet || a.Type == NetworkInterfaceType.Wireless80211 || a.Type == NetworkInterfaceType.GigabitEthernet ||
                                    a.Type == NetworkInterfaceType.FastEthernetT || a.Type == NetworkInterfaceType.FastEthernetFx || a.Type == NetworkInterfaceType.Ethernet3Megabit ||
                                    a.Type == NetworkInterfaceType.Wman || a.Type == NetworkInterfaceType.Wwanpp || a.Type == NetworkInterfaceType.Wwanpp2;
                    a.IsCandidate = !a.IsVpn && !a.IsVirtual && a.Gateway != null && physType;
                    list.Add(a);
                }
                catch { }
            }
            return list;
        }

        // Физический адаптер для канала «без ВПН»
        public static AdapterInfo PickDirect(List<AdapterInfo> list, string pref)
        {
            if (!string.IsNullOrEmpty(pref) && pref != "auto")
            {
                var m = list.FirstOrDefault(a => a.Id == pref || a.Ip == pref);
                if (m != null) return m;
            }
            var best = BestIndex();
            return list.FirstOrDefault(a => a.IsCandidate && a.Index == best)
                ?? list.FirstOrDefault(a => a.IsCandidate && a.Type != NetworkInterfaceType.Wireless80211)
                ?? list.FirstOrDefault(a => a.IsCandidate)
                ?? list.FirstOrDefault(a => !a.IsVpn && a.Gateway != null);
        }

        public static int BestIndex()
        {
            try
            {
                uint idx;
                uint dest = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
                if (GetBestInterface(dest, out idx) == 0) return (int)idx;
            }
            catch { }
            return -1;
        }

        public static AdapterInfo DefaultRoute(List<AdapterInfo> list)
        {
            int b = BestIndex();
            return list.FirstOrDefault(a => a.Index == b);
        }

        public static string DnsServers()
        {
            var l = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    foreach (var d in ni.GetIPProperties().DnsAddresses)
                        if (d.AddressFamily == AddressFamily.InterNetwork && !l.Contains(d.ToString())) l.Add(d.ToString());
                }
            }
            catch { }
            return l.Count == 0 ? "—" : string.Join(", ", l.Take(4));
        }
    }

    public enum LogLevel { Info, Ok, Warn, Error, Poll, Test, Route }

    // Лог по дням: <папка лога>\yyyy-MM-dd.log. Срок хранения задаётся в настройках (1 час … 1 месяц).
    public static class Logger
    {
        public static event Action<DateTime, LogLevel, string> Line;
        public static bool ToFile = true;
        public static string Dir = "log";
        public static TimeSpan Retention = TimeSpan.FromDays(7);
        static readonly object fl = new object();
        static DateTime lastPrune = DateTime.MinValue;

        public static void Write(LogLevel lv, string text)
        {
            var now = DateTime.Now;
            var h = Line; if (h != null) try { h(now, lv, text); } catch { }
            if (!ToFile) return;
            try
            {
                lock (fl)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(Path.Combine(Dir, now.ToString("yyyy-MM-dd") + ".log"), now.ToString("HH:mm:ss.fff") + " [" + Tag(lv) + "] " + text + Environment.NewLine, Encoding.UTF8);
                }
                if ((now - lastPrune).TotalMinutes >= 10) { lastPrune = now; ThreadPool.QueueUserWorkItem(_ => Prune()); }
            }
            catch { }
        }

        public static List<DateTime> Days()
        {
            var l = new List<DateTime>();
            try
            {
                foreach (var f in Directory.GetFiles(Dir, "????-??-??.log"))
                {
                    DateTime d;
                    if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) l.Add(d);
                }
            }
            catch { }
            l.Sort(); l.Reverse();
            return l;
        }

        public static List<Tuple<DateTime, LogLevel, string>> ReadDay(DateTime day)
        {
            var res = new List<Tuple<DateTime, LogLevel, string>>();
            string f = Path.Combine(Dir, day.ToString("yyyy-MM-dd") + ".log");
            string[] lines;
            try { lock (fl) lines = File.Exists(f) ? File.ReadAllLines(f, Encoding.UTF8) : new string[0]; } catch { return res; }
            foreach (var ln in lines)
            {
                DateTime t; LogLevel lv; string txt;
                if (Parse(day, ln, out t, out lv, out txt)) res.Add(Tuple.Create(t, lv, txt));
            }
            return res;
        }

        static bool Parse(DateTime day, string ln, out DateTime t, out LogLevel lv, out string txt)
        {
            t = day; lv = LogLevel.Info; txt = ln;
            if (ln.Length < 20 || ln[12] != ' ' || ln[13] != '[') return false;
            TimeSpan ts;
            if (!TimeSpan.TryParseExact(ln.Substring(0, 12), @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out ts)) return false;
            t = day.Date + ts;
            string tag = ln.Substring(14, 4);
            foreach (LogLevel x in Enum.GetValues(typeof(LogLevel))) if (Tag(x) == tag) lv = x;
            txt = ln.Length > 20 ? ln.Substring(20) : "";
            return true;
        }

        // Удаляет дни старше срока хранения; при сроке < 1 дня обрезает и сегодняшний файл
        public static void Prune()
        {
            try
            {
                var cut = DateTime.Now - Retention;
                lock (fl)
                {
                    foreach (var d in Days())
                    {
                        string f = Path.Combine(Dir, d.ToString("yyyy-MM-dd") + ".log");
                        if (d.Date.AddDays(1) <= cut) { File.Delete(f); continue; }
                        if (d.Date < cut)
                        {
                            var keep = new List<string>();
                            foreach (var ln in File.ReadAllLines(f, Encoding.UTF8))
                            {
                                DateTime t; LogLevel lv; string txt;
                                if (!Parse(d, ln, out t, out lv, out txt) || t >= cut) keep.Add(ln);
                            }
                            File.WriteAllLines(f, keep.ToArray(), Encoding.UTF8);
                        }
                    }
                }
            }
            catch { }
        }

        public static string Tag(LogLevel lv)
        {
            switch (lv) { case LogLevel.Ok: return "OK  "; case LogLevel.Warn: return "WARN"; case LogLevel.Error: return "ERR "; case LogLevel.Poll: return "POLL"; case LogLevel.Test: return "TEST"; case LogLevel.Route: return "ROUT"; default: return "INFO"; }
        }
    }
}
