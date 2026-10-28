using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetProbe
{
    // Сам прокси: слушает host:port, принимает MTProto (dd / ee Fake TLS) от Telegram и ведёт в Telegram по WS / запасным путям
    public static class TgProxy
    {
        static TgConfig cfg; static byte[] secret;
        static TcpListener listener; static CancellationTokenSource cts;
        static readonly object gate = new object();
        static readonly HashSet<TgBufStream> clients = new HashSet<TgBufStream>();
        public static readonly TgWsPool WsPool = new TgWsPool();
        public static readonly TgWorkerPool WorkerPool = new TgWorkerPool();
        public static TgH2Pool H2Pool;
        static Timer statsTimer;
        public static bool Running { get { return listener != null; } }
        public static string LastError = "";
        public static TgConfig Config { get { return cfg; } }

        // ссылки для Telegram: dd (обычная) или ee (Fake TLS)
        public static string Link(TgConfig c)
        {
            string host = LinkHost(c.Host);
            if (!string.IsNullOrEmpty(c.FakeTlsDomain))
                return "tg://proxy?server=" + host + "&port=" + c.Port + "&secret=ee" + c.Secret + TgConst.ToHex(Encoding.ASCII.GetBytes(c.FakeTlsDomain));
            return "tg://proxy?server=" + host + "&port=" + c.Port + "&secret=dd" + c.Secret;
        }
        public static string LinkHost(string host)
        {
            if (host != "0.0.0.0") return host;
            try { using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)) { s.Connect("8.8.8.8", 80); return ((IPEndPoint)s.LocalEndPoint).Address.ToString(); } }
            catch { return "127.0.0.1"; }
        }

        public static bool Start(TgConfig c)
        {
            Stop();
            lock (gate)
            {
                cfg = c; secret = TgConst.Hex(c.Secret); TgLog.Verbose = c.Verbose; LastError = "";
                cts = new CancellationTokenSource();
                try
                {
                    var l = new TcpListener(IPAddress.Parse(c.Host), c.Port);
                    l.Server.NoDelay = true;
                    l.Start(256);
                    listener = l;
                }
                catch (SocketException ex)
                {
                    LastError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse ? Tr.Tx("порт занят другой программой") :
                                ex.SocketErrorCode == SocketError.AccessDenied ? Tr.Tx("нет доступа к порту") :
                                ex.SocketErrorCode == SocketError.AddressNotAvailable ? Tr.Tx("такого адреса нет на этом компьютере") : ex.Message;
                    TgLog.E("Failed to start proxy on " + c.Host + ":" + c.Port + ": " + LastError);
                    listener = null; return false;
                }
                WsPool.Reset(c); WorkerPool.Reset(c); TgBridge.ResetTcpBackoff();
                H2Pool = c.H2Enabled ? new TgH2Pool(c) : null;
                TgCfDomains.Start(c);
            }
            TgLog.I("============================================================");
            TgLog.I("  Telegram MTProto WS Bridge Proxy");
            TgLog.I("  Listening on   " + c.Host + ":" + c.Port);
            if (c.FakeTlsDomain != "") TgLog.I("  Fake TLS:      " + c.FakeTlsDomain);
            foreach (var kv in c.DcRedirects.OrderBy(k => k.Key)) TgLog.I("  DC" + kv.Key + ": " + kv.Value);
            if (c.FallbackCf) TgLog.I("  CF proxy:      enabled (" + (c.CfUserDomains.Count > 0 ? string.Join(", ", c.CfUserDomains) : "auto") + ")");
            if (c.CfWorkerDomains.Count > 0) TgLog.I("  CF worker:     enabled (" + string.Join(", ", c.CfWorkerDomains) + ")");
            if (H2Pool != null) TgLog.I("  CF H2 media:   enabled");
            if (c.DisableSecure) TgLog.I("  No secure:     enabled (port 80 for CF proxy/worker)");
            TgLog.I("============================================================");
            var tok = cts.Token;
            Task.Run(() => AcceptLoop(tok));
            WsPool.Warmup(); WorkerPool.Warmup();
            statsTimer = new Timer(_ => { if (Running) TgLog.I("stats: " + TgStats.Summary()); }, null, 60000, 60000);
            return true;
        }

        public static void Stop()
        {
            TcpListener l; CancellationTokenSource t; List<TgBufStream> cl;
            lock (gate)
            {
                l = listener; listener = null; t = cts; cts = null;
                cl = clients.ToList(); clients.Clear();
            }
            if (l == null && t == null) return;
            if (t != null) t.Cancel();
            try { if (l != null) l.Stop(); } catch { }
            foreach (var c in cl) c.Close();
            var st = statsTimer; statsTimer = null; if (st != null) st.Dispose();
            TgCfDomains.Stop();
            if (cfg != null) { WsPool.Reset(cfg); WorkerPool.Reset(cfg); }
            var h = H2Pool; H2Pool = null; if (h != null) h.Close();
            TgLog.I("Proxy stopped. Final stats: " + TgStats.Summary());
        }

        static async Task AcceptLoop(CancellationToken tok)
        {
            while (!tok.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    if (tok.IsCancellationRequested) return;
                    // слушающий сокет «умер» — перезапуск (как listener watchdog)
                    TgLog.Wn("Listening socket died, restarting server: " + TgNetUtil.Err(ex));
                    try { listener.Stop(); } catch { }
                    await Task.Delay(1000).ConfigureAwait(false);
                    try { var l = new TcpListener(IPAddress.Parse(cfg.Host), cfg.Port); l.Start(256); lock (gate) { if (tok.IsCancellationRequested) { l.Stop(); return; } listener = l; } TgLog.Wn("Server restored, listening on " + cfg.Host + ":" + cfg.Port); }
                    catch (Exception e2) { TgLog.E("Failed to restart server: " + TgNetUtil.Err(e2)); lock (gate) listener = null; return; }
                    continue;
                }
                var _ = Task.Run(() => Handle(c, tok));
            }
        }

        static async Task Handle(TcpClient tcp, CancellationToken tok)
        {
            Interlocked.Increment(ref TgStats.Total); Interlocked.Increment(ref TgStats.Active);
            string label = "?"; try { label = tcp.Client.RemoteEndPoint.ToString(); } catch { }
            TgNetUtil.Opts(tcp.Client, cfg.BufferSize);
            var bs = new TgBufStream(tcp.GetStream(), tcp);
            lock (gate) { if (tok.IsCancellationRequested) { bs.Close(); Interlocked.Decrement(ref TgStats.Active); return; } clients.Add(bs); }
            try
            {
                // PROXY protocol v1 (за nginx/haproxy)
                if (cfg.ProxyProtocol)
                {
                    var line = (await TgNetUtil.WithTimeout(bs.ReadLine(), 10).ConfigureAwait(false)).Trim();
                    if (line.StartsWith("PROXY ")) { var p = line.Split(' '); if (p.Length >= 6) label = p[2] + ":" + p[4]; TgLog.D("[" + label + "] PROXY protocol: " + line); }
                    else TgLog.D("[" + label + "] expected PROXY header, got: " + (line.Length > 60 ? line.Substring(0, 60) : line));
                }
                byte[] first = await TgNetUtil.WithTimeout(bs.ReadExactly(1), 10).ConfigureAwait(false);
                ITgStream cs = bs; byte[] hs;
                string mask = cfg.FakeTlsDomain;
                if (first[0] == TgFakeTls.RecHandshake && !string.IsNullOrEmpty(mask))
                {
                    var rest = await TgNetUtil.WithTimeout(bs.ReadExactly(4), 10).ConfigureAwait(false);
                    int recLen = rest[2] << 8 | rest[3];
                    var body = await TgNetUtil.WithTimeout(bs.ReadExactly(recLen), 10).ConfigureAwait(false);
                    var hello = new byte[5 + recLen]; hello[0] = first[0]; Buffer.BlockCopy(rest, 0, hello, 1, 4); Buffer.BlockCopy(body, 0, hello, 5, recLen);
                    byte[] cr, sid; uint ts;
                    if (!TgFakeTls.Verify(hello, secret, out cr, out sid, out ts))
                    {
                        TgLog.D("[" + label + "] Fake TLS verify failed (size=" + hello.Length + ") -> masking");
                        await TgFakeTls.ProxyToMasking(bs, hello, mask, label, cfg.BufferSize).ConfigureAwait(false);
                        return;
                    }
                    TgLog.D("[" + label + "] Fake TLS handshake ok (ts=" + ts + ")");
                    var sh = TgFakeTls.ServerHello(secret, cr, sid);
                    await bs.WriteAsync(sh, 0, sh.Length).ConfigureAwait(false);
                    var fs = new TgFakeTls.Stream(bs); cs = fs;
                    hs = await TgNetUtil.WithTimeout(fs.ReadExactly(TgConst.HandshakeLen), 10).ConfigureAwait(false);
                }
                else if (!string.IsNullOrEmpty(mask))
                {
                    TgLog.D("[" + label + "] non-TLS byte 0x" + first[0].ToString("X2") + " -> HTTP redirect");
                    var r = Encoding.ASCII.GetBytes("HTTP/1.1 301 Moved Permanently\r\nLocation: https://" + mask + "/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await bs.WriteAsync(r, 0, r.Length).ConfigureAwait(false);
                    return;
                }
                else
                {
                    var rest = await TgNetUtil.WithTimeout(bs.ReadExactly(TgConst.HandshakeLen - 1), 10).ConfigureAwait(false);
                    hs = new byte[TgConst.HandshakeLen]; hs[0] = first[0]; Buffer.BlockCopy(rest, 0, hs, 1, rest.Length);
                }

                var res = TgHandshake.Try(hs, secret);
                if (res == null)
                {
                    Interlocked.Increment(ref TgStats.Bad);
                    TgLog.Wn("[" + label + "] bad handshake (wrong secret or proto)");
                    try { var b = new byte[4096]; while (await cs.ReadAsync(b, 0, b.Length).ConfigureAwait(false) > 0) { } } catch { }
                    return;
                }
                var hr = res.Value; int dc = hr.Dc;
                bool isTest = cfg.ForceTestDc || dc >= 10000;
                if (dc >= 10000) { TgLog.I("[" + label + "] test DC" + dc + " -> DC" + (dc - 10000)); dc -= 10000; }
                int dcIdx = hr.Media ? -dc : dc;
                string mt = hr.Media ? " media" : "";
                TgLog.D("[" + label + "] handshake ok: DC" + dc + mt + " proto=0x" + hr.Proto.ToString("X8"));
                var relayInit = TgHandshake.RelayInit(hr.Proto, dcIdx);
                var ctx = TgHandshake.Build(hr.PrekeyIv, secret, relayInit);

                var ws = WsPool.Get(dc, hr.Media, isTest);
                if (ws == null)
                {
                    TgLog.I("[" + label + "] DC" + dc + mt + " WS pool unavailable -> fallback");
                    bool ok = await TgBridge.Fallback(cfg, cs, relayInit, label, dc, isTest, hr.Media, ctx, new TgSplitter(hr.Proto), hr.Proto).ConfigureAwait(false);
                    if (ok) TgLog.I("[" + label + "] DC" + dc + mt + " fallback closed");
                    else TgLog.Wn("[" + label + "] DC" + dc + mt + " no fallback available");
                    return;
                }
                TgLog.I("[" + label + "] DC" + dc + mt + " -> WS pool hit");
                Interlocked.Increment(ref TgStats.Ws);
                await ws.Send(relayInit).ConfigureAwait(false);
                await TgBridge.Ws(cs, ws, label, ctx, dc, hr.Media, new TgSplitter(hr.Proto)).ConfigureAwait(false);
            }
            catch (TimeoutException) { TgLog.Wn("[" + label + "] timeout during handshake"); }
            catch (EndOfStreamException) { TgLog.D("[" + label + "] client disconnected"); }
            catch (IOException ex) { TgLog.D("[" + label + "] connection closed: " + ex.Message); }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { TgLog.E("[" + label + "] unexpected: " + TgNetUtil.Err(ex)); }
            finally
            {
                Interlocked.Decrement(ref TgStats.Active);
                lock (gate) clients.Remove(bs);
                bs.Close();
            }
        }
    }

    public static class TgBridge
    {
        static readonly object gate = new object();
        static readonly Dictionary<string, int> tcpFail = new Dictionary<string, int>();
        static readonly Dictionary<string, DateTime> tcpRetry = new Dictionary<string, DateTime>();
        static readonly HashSet<string> tcpConnecting = new HashSet<string>();
        public static void ResetTcpBackoff() { lock (gate) { tcpFail.Clear(); tcpRetry.Clear(); tcpConnecting.Clear(); } }

        // запасные пути, по порядку: CF Worker → CF-прокси (медиа — HTTP/2) → прямой TCP к DC
        public static async Task<bool> Fallback(TgConfig cfg, ITgStream cs, byte[] relayInit, string label, int dc, bool isTest, bool media,
                                                TgCrypto ctx, TgSplitter splitter, uint proto)
        {
            string dst; (isTest ? TgConst.DcTestIps : TgConst.DcDefaultIps).TryGetValue(dc, out dst);
            bool useCf = cfg.FallbackCf && !isTest;
            var methods = new List<string>();
            if (cfg.CfWorkerDomains.Count > 0 && dst != null) methods.Add("worker");
            if (useCf) methods.Add("cf");
            if (dst != null) methods.Add("tcp");
            string mt = media ? " media" : "";
            foreach (var m in methods)
            {
                if (m == "worker")
                {
                    var pooled = isTest ? null : TgProxy.WorkerPool.Get(dc, dst, cfg.CfWorkerDomains);
                    TgWs ws = null;
                    if (pooled != null) { ws = pooled.Item1; TgLog.I("[" + label + "] DC" + dc + mt + " -> CF worker pool hit via " + pooled.Item2 + " for " + dst); }
                    else
                    {
                        foreach (var wd in TgProxy.WorkerPool.Available(cfg.CfWorkerDomains))
                        {
                            TgLog.I("[" + label + "] DC" + dc + mt + " -> trying CF worker " + wd + " for " + dst);
                            try { ws = await TgWs.Connect(wd, wd, 10, TgWorkerPool.PathFor(dst, dc), null, !cfg.DisableSecure, cfg.BufferSize).ConfigureAwait(false); break; }
                            catch (Exception ex) { TgLog.Wn("[" + label + "] DC" + dc + mt + " CF worker " + wd + " failed: " + TgNetUtil.Err(ex)); }
                        }
                    }
                    if (ws == null) continue;
                    Interlocked.Increment(ref TgStats.Cf);
                    await ws.Send(relayInit).ConfigureAwait(false);
                    await Ws(cs, ws, label, ctx, dc, media, null).ConfigureAwait(false);
                    return true;
                }
                if (m == "cf")
                {
                    var h2 = TgProxy.H2Pool;
                    if (media && h2 != null)
                    {
                        var ch = await h2.Open(dc, label).ConfigureAwait(false);
                        if (ch != null)
                        {
                            Interlocked.Increment(ref TgStats.H2); Interlocked.Increment(ref TgStats.Cf);
                            await TgH2Bridge.Run(cs, ch, ctx, proto).ConfigureAwait(false);
                            return true;
                        }
                    }
                    TgLog.I("[" + label + "] DC" + dc + mt + " -> trying CF proxy");
                    TgWs ws = null; string chosen = null;
                    foreach (var bd in TgBalancer.For(dc))
                    {
                        var domain = "kws" + dc + "." + bd;
                        try { ws = await TgWs.Connect(domain, domain, 10, TgConst.WsPath, null, !cfg.DisableSecure, cfg.BufferSize).ConfigureAwait(false); chosen = bd; break; }
                        catch (Exception ex) { TgLog.Wn("[" + label + "] DC" + dc + mt + " CF proxy failed: " + TgNetUtil.Err(ex)); }
                    }
                    if (ws == null) continue;
                    if (chosen != null && TgBalancer.SetFor(dc, chosen)) TgLog.I("[" + label + "] Switched active CF domain");
                    Interlocked.Increment(ref TgStats.Cf);
                    await ws.Send(relayInit).ConfigureAwait(false);
                    await Ws(cs, ws, label, ctx, dc, media, splitter).ConfigureAwait(false);
                    return true;
                }
                if (m == "tcp" && await Tcp(cfg, cs, dst, 443, relayInit, label, ctx).ConfigureAwait(false)) return true;
            }
            return false;
        }

        static async Task<bool> Tcp(TgConfig cfg, ITgStream cs, string dst, int port, byte[] relayInit, string label, TgCrypto ctx)
        {
            string key = dst + ":" + port;
            lock (gate)
            {
                DateTime after;
                if (tcpConnecting.Contains(key) || (tcpRetry.TryGetValue(key, out after) && DateTime.UtcNow < after)) { TgLog.D("[" + label + "] TCP fallback to " + key + " skipped (backoff or connecting)"); return false; }
                tcpConnecting.Add(key);
            }
            TcpClient rc = null;
            try
            {
                try
                {
                    TgLog.I("[" + label + "] TCP fallback to " + key);
                    rc = await TgNetUtil.Connect(dst, port, 10, cfg.BufferSize).ConfigureAwait(false);
                    await rc.GetStream().WriteAsync(relayInit, 0, relayInit.Length).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    double delay;
                    lock (gate)
                    {
                        int f; tcpFail.TryGetValue(key, out f); tcpFail[key] = ++f;
                        delay = Math.Min(30 * Math.Pow(2, Math.Min(f - 1, 7)), 3600);
                        tcpRetry[key] = DateTime.UtcNow.AddSeconds(delay);
                    }
                    TgLog.Wn("[" + label + "] TCP fallback to " + key + " failed: " + TgNetUtil.Err(ex) + "; retry in " + delay.ToString("0") + "s");
                    if (rc != null) rc.Close();
                    return false;
                }
                finally { lock (gate) tcpConnecting.Remove(key); }
                lock (gate) { tcpFail.Remove(key); tcpRetry.Remove(key); }
                Interlocked.Increment(ref TgStats.TcpFb);
                var rs = new TgBufStream(rc.GetStream(), rc);
                Func<ITgStream, ITgStream, bool, Task> fwd = async (src, dstS, up) =>
                {
                    var b = new byte[65536];
                    try
                    {
                        int n;
                        while ((n = await src.ReadAsync(b, 0, b.Length).ConfigureAwait(false)) > 0)
                        {
                            if (up) { Interlocked.Add(ref TgStats.Up, n); ctx.CltDec.Transform(b, 0, n); ctx.TgEnc.Transform(b, 0, n); }
                            else { Interlocked.Add(ref TgStats.Down, n); ctx.TgDec.Transform(b, 0, n); ctx.CltEnc.Transform(b, 0, n); }
                            await dstS.WriteAsync(b, 0, n).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) { TgLog.D("[" + label + "] forward ended: " + TgNetUtil.Err(ex)); }
                };
                var t1 = fwd(cs, rs, true); var t2 = fwd(rs, cs, false);
                await Task.WhenAny(t1, t2).ConfigureAwait(false);
                rs.Close(); cs.Close();
                await Task.WhenAll(t1, t2).ConfigureAwait(false);
                return true;
            }
            finally { if (rc != null) rc.Close(); }
        }

        // клиент (TCP) ⇄ Telegram (WS) с перешифровкой
        public static async Task Ws(ITgStream cs, TgWs ws, string label, TgCrypto ctx, int dc, bool media, TgSplitter splitter)
        {
            string dcTag = "DC" + dc + (media ? "m" : "");
            long up = 0, down = 0, upP = 0, downP = 0; var started = DateTime.UtcNow; string reason = "normal";
            var t1 = Task.Run(async () =>
            {
                var b = new byte[65536];
                try
                {
                    while (true)
                    {
                        int n = await cs.ReadAsync(b, 0, b.Length).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            if (splitter != null) { var tail = splitter.Flush(); if (tail != null) await ws.Send(tail).ConfigureAwait(false); }
                            break;
                        }
                        Interlocked.Add(ref TgStats.Up, n); up += n; upP++;
                        var chunk = new byte[n]; Buffer.BlockCopy(b, 0, chunk, 0, n);
                        ctx.CltDec.Transform(chunk, 0, n);
                        var plain = splitter != null ? (byte[])chunk.Clone() : null;
                        ctx.TgEnc.Transform(chunk, 0, n);
                        if (splitter != null)
                        {
                            var parts = splitter.Split(chunk, plain);
                            if (parts.Count == 0) continue;
                            if (parts.Count > 1) await ws.SendBatch(parts).ConfigureAwait(false); else await ws.Send(parts[0]).ConfigureAwait(false);
                        }
                        else await ws.Send(chunk).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) { if (reason == "normal") reason = "client: " + ex.GetType().Name; }
            });
            var t2 = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var data = await ws.Recv().ConfigureAwait(false);
                        if (data == null) { if (reason == "normal") reason = "upstream: ws_close"; break; }
                        int n = data.Length; Interlocked.Add(ref TgStats.Down, n); down += n; downP++;
                        ctx.TgDec.Transform(data, 0, n); ctx.CltEnc.Transform(data, 0, n);
                        await cs.WriteAsync(data, 0, n).ConfigureAwait(false);
                    }
                }
                catch (EndOfStreamException) { if (reason == "normal") reason = "upstream: tcp_reset"; }
                catch (Exception ex) { if (reason == "normal") reason = "upstream: " + ex.GetType().Name; }
            });
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
            await ws.CloseAsync().ConfigureAwait(false); cs.Close();
            try { await Task.WhenAll(t1, t2).ConfigureAwait(false); } catch { }
            TgLog.I("[" + label + "] " + dcTag + " WS session closed (" + reason + "): ^" + TgConst.Human(up) + " (" + upP + " pkts) v" + TgConst.Human(down) + " (" + downP + " pkts) in " +
                    (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s");
        }
    }

    // HTTP(S) для служебных запросов: список доменов с GitHub, проверки CF-прокси / Worker
    public static class TgHttp
    {
        static readonly Dictionary<string, string> pinned = new Dictionary<string, string> { { "raw.githubusercontent.com", "185.199.109.133" }, { "release-assets.githubusercontent.com", "185.199.109.133" } };
        public static string GetText(string url)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                var rq = (HttpWebRequest)WebRequest.Create(url); rq.UserAgent = "tg-ws-proxy"; rq.Timeout = 10000; rq.Proxy = null;
                using (var r = rq.GetResponse()) using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
            catch
            {
                // DNS GitHub подменён/недоступен — тот же запрос на известный IP GitHub
                var u = new Uri(url); string ip; if (!pinned.TryGetValue(u.Host, out ip)) throw;
                using (var c = new TcpClient())
                {
                    c.Connect(ip, 443); c.ReceiveTimeout = c.SendTimeout = 10000;
                    using (var ssl = new SslStream(c.GetStream(), false))
                    {
                        ssl.AuthenticateAsClient(u.Host, null, TgNetUtil.Tls, false);
                        var req = Encoding.ASCII.GetBytes("GET " + u.PathAndQuery + " HTTP/1.1\r\nHost: " + u.Host + "\r\nUser-Agent: tg-ws-proxy\r\nConnection: close\r\n\r\n");
                        ssl.Write(req); var ms = new MemoryStream(); ssl.CopyTo(ms);
                        var text = Encoding.UTF8.GetString(ms.ToArray()); int i = text.IndexOf("\r\n\r\n");
                        if (!text.StartsWith("HTTP/1.1 200")) throw new IOException(text.Split('\r')[0]);
                        return i >= 0 ? text.Substring(i + 4) : "";
                    }
                }
            }
        }

        // проверка: доступен ли WebSocket-апгрейд (101) через домен — для CF-прокси и CF Worker
        public static string WsProbe(string connectHost, string sni, string reqHost, string path, bool secure)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(connectHost, secure ? 443 : 80, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) return Tr.Tx("таймаут");
                    c.EndConnect(ar); c.ReceiveTimeout = c.SendTimeout = 5000;
                    Stream s = c.GetStream();
                    if (secure) { var ssl = new SslStream(s, false); ssl.AuthenticateAsClient(sni, null, TgNetUtil.Tls, false); s = ssl; }
                    var req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: " + reqHost + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " +
                        Convert.ToBase64String(TgConst.Rand(16)) + "\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n");
                    s.Write(req, 0, req.Length);
                    var buf = new List<byte>(); var b = new byte[512];
                    while (true)
                    {
                        int n = s.Read(b, 0, b.Length); if (n <= 0) break;
                        buf.AddRange(b.Take(n)); var t = Encoding.UTF8.GetString(buf.ToArray());
                        if (t.Contains("\r\n\r\n")) break;
                    }
                    var first = Encoding.UTF8.GetString(buf.ToArray()).Split('\r')[0];
                    return first.Contains(" 101") ? null : (first == "" ? Tr.Tx("нет ответа") : first);
                }
            }
            catch (IOException ex) when (ex.InnerException is SocketException && ((SocketException)ex.InnerException).SocketErrorCode == SocketError.TimedOut) { return Tr.Tx("таймаут"); }
            catch (Exception ex) { var m = ex.InnerException != null ? ex.InnerException.Message : ex.Message; return m.Length > 60 ? m.Substring(0, 60) : m; }
        }
        public static readonly int[] TestDcs = { 1, 2, 3, 4, 5, 203 };
        public static Dictionary<int, string> TestCfProxy(string domain, bool secure)
        { return TestDcs.ToDictionary(dc => dc, dc => { var h = "kws" + dc + "." + domain; return WsProbe(h, h, h, "/apiws", secure); }); }
        public static Dictionary<int, string> TestWorker(string domain, bool secure)
        { return TestDcs.ToDictionary(dc => dc, dc => WsProbe(domain, domain, domain, "/apiws?dst=" + TgConst.DcDefaultIps[dc] + "&dc=" + dc + "&media=0", secure)); }
        // прямой путь (WS к kwsN.web.telegram.org по IP из правил DC → IP)
        public static Dictionary<int, string> TestDirect(Dictionary<int, string> redirects)
        { return redirects.ToDictionary(kv => kv.Key, kv => { var d = "kws" + kv.Key + ".web.telegram.org"; return WsProbe(kv.Value, d, d, "/apiws", true); }); }
    }
}
