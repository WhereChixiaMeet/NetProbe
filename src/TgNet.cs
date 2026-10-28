using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetProbe
{
    // Поток с буфером чтения: ReadExactly / ReadLine / Read — поверх сокета, TLS или Fake TLS
    public interface ITgStream
    {
        Task<int> ReadAsync(byte[] buf, int off, int count);
        Task WriteAsync(byte[] buf, int off, int count);
        void Close();
    }

    public sealed class TgBufStream : ITgStream
    {
        readonly Stream s; readonly TcpClient owner;
        byte[] buf = new byte[65536]; int pos, len;
        readonly SemaphoreSlim wlock = new SemaphoreSlim(1, 1);
        volatile bool closed;
        public TgBufStream(Stream s, TcpClient owner) { this.s = s; this.owner = owner; }
        public TcpClient Tcp { get { return owner; } }
        public bool Closed { get { return closed; } }
        public async Task<int> ReadAsync(byte[] b, int off, int count)
        {
            if (len > pos) { int n = Math.Min(count, len - pos); Buffer.BlockCopy(buf, pos, b, off, n); pos += n; return n; }
            return await s.ReadAsync(b, off, count).ConfigureAwait(false);
        }
        async Task<bool> Fill()
        {
            if (pos > 0 && pos == len) { pos = len = 0; }
            if (len == buf.Length) { if (pos > 0) { Buffer.BlockCopy(buf, pos, buf, 0, len - pos); len -= pos; pos = 0; } else Array.Resize(ref buf, buf.Length * 2); }
            int n = await s.ReadAsync(buf, len, buf.Length - len).ConfigureAwait(false);
            if (n <= 0) return false; len += n; return true;
        }
        public async Task<byte[]> ReadExactly(int n)
        {
            var r = new byte[n]; int got = 0;
            while (got < n)
            {
                if (len > pos) { int k = Math.Min(n - got, len - pos); Buffer.BlockCopy(buf, pos, r, got, k); pos += k; got += k; continue; }
                int m = await s.ReadAsync(r, got, n - got).ConfigureAwait(false);
                if (m <= 0) throw new EndOfStreamException("incomplete read " + got + "/" + n);
                got += m;
            }
            return r;
        }
        public async Task<string> ReadLine(int max = 8192)
        {
            while (true)
            {
                for (int i = pos; i < len; i++)
                    if (buf[i] == (byte)'\n') { var line = Encoding.UTF8.GetString(buf, pos, i + 1 - pos); pos = i + 1; return line; }
                if (len - pos > max) throw new IOException("line too long");
                if (!await Fill().ConfigureAwait(false)) { var rest = Encoding.UTF8.GetString(buf, pos, len - pos); pos = len; return rest; }
            }
        }
        public async Task WriteAsync(byte[] b, int off, int count)
        {
            await wlock.WaitAsync().ConfigureAwait(false);
            try { await s.WriteAsync(b, off, count).ConfigureAwait(false); await s.FlushAsync().ConfigureAwait(false); }
            finally { wlock.Release(); }
        }
        public void Close() { if (closed) return; closed = true; try { s.Dispose(); } catch { } try { if (owner != null) owner.Close(); } catch { } }
        // соединение закрыто удалённой стороной (для проверки «протухших» соединений в пуле)
        public bool RemoteClosed
        {
            get
            {
                if (closed) return true;
                try { var sk = owner.Client; return sk == null || (sk.Poll(0, SelectMode.SelectRead) && sk.Available == 0 && len == pos); } catch { return true; }
            }
        }
    }

    public static class TgNetUtil
    {
        public static void Opts(Socket sk, int bufSize)
        {
            try { sk.NoDelay = true; } catch { }
            try { sk.ReceiveBufferSize = bufSize; sk.SendBufferSize = bufSize; } catch { }
        }
        public static async Task<T> WithTimeout<T>(Task<T> t, double sec, Action onTimeout = null)
        {
            var d = Task.Delay(TimeSpan.FromSeconds(sec));
            if (await Task.WhenAny(t, d).ConfigureAwait(false) != t) { if (onTimeout != null) try { onTimeout(); } catch { } var _ = t.ContinueWith(x => { var e = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted); throw new TimeoutException(); }
            return await t.ConfigureAwait(false);
        }
        public static async Task WithTimeout(Task t, double sec, Action onTimeout = null)
        {
            var d = Task.Delay(TimeSpan.FromSeconds(sec));
            if (await Task.WhenAny(t, d).ConfigureAwait(false) != t) { if (onTimeout != null) try { onTimeout(); } catch { } var _ = t.ContinueWith(x => { var e = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted); throw new TimeoutException(); }
            await t.ConfigureAwait(false);
        }
        public static async Task<TcpClient> Connect(string host, int port, double timeout, int bufSize)
        {
            var c = new TcpClient();
            try
            {
                IPAddress ip;
                var t = IPAddress.TryParse(host, out ip) ? c.ConnectAsync(ip, port) : c.ConnectAsync(host, port);
                await WithTimeout(t, timeout, () => c.Close()).ConfigureAwait(false);
                Opts(c.Client, bufSize);
                return c;
            }
            catch { c.Close(); throw; }
        }
        public static string Err(Exception e)
        {
            var b = e is AggregateException ? ((AggregateException)e).InnerException ?? e : e;
            return b.GetType().Name + (string.IsNullOrEmpty(b.Message) ? "" : ": " + b.Message);
        }
    }

    public sealed class TgWsHandshakeError : Exception
    {
        public int Status; public string Location;
        public TgWsHandshakeError(int status, string line, string location) : base("HTTP " + status + ": " + line) { Status = status; Location = location; }
    }

    // WebSocket-клиент «вручную» (без System.Net.WebSockets): нужен свой SNI (фронтинг), IP вместо имени и порт 80 без TLS
    public sealed class TgWs
    {
        public readonly TgBufStream S; public readonly DateTime Created = DateTime.UtcNow; public string Tag;
        volatile bool closed; readonly List<byte> frag = new List<byte>();
        const int MaxMessage = 16 * 1024 * 1024;
        TgWs(TgBufStream s) { S = s; }
        public bool Closed { get { return closed || S.Closed; } }
        public bool Stale(double maxAgeSec) { return (DateTime.UtcNow - Created).TotalSeconds >= maxAgeSec || Closed || S.RemoteClosed; }

        public static async Task<TgWs> Connect(string host, string domain, double timeout, string path, string sni, bool secure, int bufSize)
        {
            var tcp = await TgNetUtil.Connect(host, secure ? 443 : 80, Math.Min(timeout, 10), bufSize).ConfigureAwait(false);
            Stream st = tcp.GetStream();
            try
            {
                if (secure)
                {
                    bool fronting = sni != null;
                    var ssl = new SslStream(st, false, (o, cert, chain, err) => fronting || err == SslPolicyErrors.None);
                    st = ssl;
                    await TgNetUtil.WithTimeout(ssl.AuthenticateAsClientAsync(sni ?? domain, null, SslProtocols.None, false), Math.Min(timeout, 10), () => tcp.Close()).ConfigureAwait(false);
                }
                var bs = new TgBufStream(st, tcp);
                var key = Convert.ToBase64String(TgConst.Rand(16));
                var req = "GET " + path + " HTTP/1.1\r\nHost: " + domain + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: " + key +
                          "\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n";
                var rb = Encoding.ASCII.GetBytes(req);
                await bs.WriteAsync(rb, 0, rb.Length).ConfigureAwait(false);
                var lines = new List<string>();
                while (true)
                {
                    var line = await TgNetUtil.WithTimeout(bs.ReadLine(), timeout, () => tcp.Close()).ConfigureAwait(false);
                    if (line == "\r\n" || line == "\n" || line == "") break;
                    lines.Add(line.Trim());
                }
                if (lines.Count == 0) throw new TgWsHandshakeError(0, "empty response", null);
                var parts = lines[0].Split(new[] { ' ' }, 3); int code = 0;
                if (parts.Length >= 2) int.TryParse(parts[1], out code);
                if (code == 101) return new TgWs(bs);
                string loc = lines.Skip(1).Where(l => l.StartsWith("location:", StringComparison.OrdinalIgnoreCase)).Select(l => l.Substring(9).Trim()).FirstOrDefault();
                throw new TgWsHandshakeError(code, lines[0], loc);
            }
            catch { try { st.Dispose(); } catch { } tcp.Close(); throw; }
        }

        static byte[] Frame(int op, byte[] data, int off, int len)
        {
            var mask = TgConst.Rand(4);
            int hdr = len < 126 ? 2 : len < 65536 ? 4 : 10;
            var f = new byte[hdr + 4 + len];
            f[0] = (byte)(0x80 | op);
            if (len < 126) f[1] = (byte)(0x80 | len);
            else if (len < 65536) { f[1] = 0x80 | 126; f[2] = (byte)(len >> 8); f[3] = (byte)len; }
            else { f[1] = 0x80 | 127; ulong L = (ulong)len; for (int i = 0; i < 8; i++) f[2 + i] = (byte)(L >> (56 - 8 * i)); }
            Buffer.BlockCopy(mask, 0, f, hdr, 4);
            for (int i = 0; i < len; i++) f[hdr + 4 + i] = (byte)(data[off + i] ^ mask[i & 3]);
            return f;
        }
        public Task Send(byte[] data) { if (Closed) throw new IOException("WebSocket closed"); var f = Frame(2, data, 0, data.Length); return S.WriteAsync(f, 0, f.Length); }
        public Task SendBatch(List<byte[]> parts)
        {
            if (Closed) throw new IOException("WebSocket closed");
            var frames = parts.Select(p => Frame(2, p, 0, p.Length)).ToList();
            var all = new byte[frames.Sum(f => f.Length)]; int o = 0;
            foreach (var f in frames) { Buffer.BlockCopy(f, 0, all, o, f.Length); o += f.Length; }
            return S.WriteAsync(all, 0, all.Length);
        }

        public async Task<byte[]> Recv()
        {
            while (!closed)
            {
                var h = await S.ReadExactly(2).ConfigureAwait(false);
                bool fin = (h[0] & 0x80) != 0; int op = h[0] & 0x0F; long len = h[1] & 0x7F;
                if (len == 126) { var e = await S.ReadExactly(2).ConfigureAwait(false); len = e[0] << 8 | e[1]; }
                else if (len == 127) { var e = await S.ReadExactly(8).ConfigureAwait(false); len = 0; for (int i = 0; i < 8; i++) len = len << 8 | e[i]; }
                if (len > MaxMessage || len < 0) throw new IOException("WS frame too large: " + len);
                byte[] payload;
                if ((h[1] & 0x80) != 0) { var m = await S.ReadExactly(4).ConfigureAwait(false); payload = await S.ReadExactly((int)len).ConfigureAwait(false); for (int i = 0; i < payload.Length; i++) payload[i] ^= m[i & 3]; }
                else payload = await S.ReadExactly((int)len).ConfigureAwait(false);
                if (op == 8)
                {
                    closed = true;
                    TgLog.D("WS OP_CLOSE from upstream: code=" + (payload.Length >= 2 ? (payload[0] << 8 | payload[1]).ToString() : "-"));
                    try { var f = Frame(8, payload, 0, Math.Min(2, payload.Length)); await S.WriteAsync(f, 0, f.Length).ConfigureAwait(false); } catch { }
                    return null;
                }
                if (op == 9) { try { var f = Frame(10, payload, 0, payload.Length); await S.WriteAsync(f, 0, f.Length).ConfigureAwait(false); } catch { } continue; }
                if (op == 10) continue;
                if (op == 0 || op == 1 || op == 2)
                {
                    if (fin && frag.Count == 0) return payload;
                    frag.AddRange(payload);
                    if (frag.Count > MaxMessage) throw new IOException("WS message too large");
                    if (!fin) continue;
                    var msg = frag.ToArray(); frag.Clear(); return msg;
                }
            }
            return null;
        }

        public async Task CloseAsync()
        {
            if (closed) { S.Close(); return; }
            closed = true;
            try { var f = Frame(8, new byte[0], 0, 0); await TgNetUtil.WithTimeout(S.WriteAsync(f, 0, f.Length), 2).ConfigureAwait(false); } catch { }
            S.Close();
        }
        public void Abort() { closed = true; S.Close(); }
    }

    // ---- Fake TLS (ee-секрет): клиент «здоровается» как TLS, проверка HMAC по секрету, ответ ServerHello ----
    public static class TgFakeTls
    {
        public const byte RecHandshake = 0x16, RecCcs = 0x14, RecApp = 0x17;
        public static bool Verify(byte[] data, byte[] secret, out byte[] clientRandom, out byte[] sessionId, out uint ts)
        {
            clientRandom = sessionId = null; ts = 0;
            if (data.Length < 43 || data[0] != RecHandshake || data[5] != 0x01) return false;
            clientRandom = new byte[32]; Buffer.BlockCopy(data, 11, clientRandom, 0, 32);
            var zeroed = (byte[])data.Clone(); for (int i = 0; i < 32; i++) zeroed[11 + i] = 0;
            byte[] exp; using (var h = new HMACSHA256(secret)) exp = h.ComputeHash(zeroed);
            int diff = 0; for (int i = 0; i < 28; i++) diff |= exp[i] ^ clientRandom[i];
            if (diff != 0) return false;
            var tsx = new byte[4]; for (int i = 0; i < 4; i++) tsx[i] = (byte)(clientRandom[28 + i] ^ exp[28 + i]);
            ts = BitConverter.ToUInt32(tsx, 0);
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            if (Math.Abs(now - ts) > 120) return false;
            sessionId = new byte[32];
            if (data.Length >= 76 && data[43] == 0x20) Buffer.BlockCopy(data, 44, sessionId, 0, 32);
            return true;
        }

        static readonly byte[] tmpl = BuildTemplate();
        static byte[] BuildTemplate()
        {
            var l = new List<byte> { 0x16, 0x03, 0x03, 0x00, 0x7a, 0x02, 0x00, 0x00, 0x76, 0x03, 0x03 };
            l.AddRange(new byte[32]); l.Add(0x20); l.AddRange(new byte[32]);
            l.AddRange(new byte[] { 0x13, 0x01, 0x00, 0x00, 0x2e, 0x00, 0x33, 0x00, 0x24, 0x00, 0x1d, 0x00, 0x20 });
            l.AddRange(new byte[32]); l.AddRange(new byte[] { 0x00, 0x2b, 0x00, 0x02, 0x03, 0x04 });
            return l.ToArray();
        }
        public static byte[] ServerHello(byte[] secret, byte[] clientRandom, byte[] sessionId)
        {
            var sh = (byte[])tmpl.Clone();
            Buffer.BlockCopy(sessionId, 0, sh, 44, 32);
            Buffer.BlockCopy(TgConst.Rand(32), 0, sh, 89, 32);
            int encSize = 1900 + TgConst.RandInt(201);
            var resp = new List<byte>(sh); resp.AddRange(new byte[] { 0x14, 0x03, 0x03, 0x00, 0x01, 0x01 });
            resp.AddRange(new byte[] { 0x17, 0x03, 0x03, (byte)(encSize >> 8), (byte)encSize }); resp.AddRange(TgConst.Rand(encSize));
            var r = resp.ToArray();
            var input = new byte[32 + r.Length]; Buffer.BlockCopy(clientRandom, 0, input, 0, 32); Buffer.BlockCopy(r, 0, input, 32, r.Length);
            byte[] srvRandom; using (var h = new HMACSHA256(secret)) srvRandom = h.ComputeHash(input);
            Buffer.BlockCopy(srvRandom, 0, r, 11, 32);
            return r;
        }

        // данные внутри TLS-записей типа ApplicationData
        public sealed class Stream : ITgStream
        {
            readonly TgBufStream inner; readonly List<byte> rbuf = new List<byte>(); int left;
            public Stream(TgBufStream inner) { this.inner = inner; }
            async Task<byte[]> Payload()
            {
                if (left > 0)
                {
                    var b = new byte[Math.Min(left, 65536)];
                    int n = await inner.ReadAsync(b, 0, b.Length).ConfigureAwait(false);
                    if (n <= 0) return null;
                    left -= n; Array.Resize(ref b, n); return b;
                }
                while (true)
                {
                    var hdr = await inner.ReadExactly(5).ConfigureAwait(false);
                    int len = hdr[3] << 8 | hdr[4];
                    if (hdr[0] == RecCcs) { if (len > 0) await inner.ReadExactly(len).ConfigureAwait(false); continue; }
                    if (hdr[0] != RecApp) return null;
                    var b = new byte[Math.Min(len, 65536)];
                    int n = b.Length == 0 ? 0 : await inner.ReadAsync(b, 0, b.Length).ConfigureAwait(false);
                    if (n <= 0 && len > 0) return null;
                    Array.Resize(ref b, n); left = len - n; return b;
                }
            }
            public async Task<byte[]> ReadExactly(int n)
            {
                while (rbuf.Count < n) { var p = await Payload().ConfigureAwait(false); if (p == null) throw new EndOfStreamException(); rbuf.AddRange(p); }
                var r = rbuf.GetRange(0, n).ToArray(); rbuf.RemoveRange(0, n); return r;
            }
            public async Task<int> ReadAsync(byte[] b, int off, int count)
            {
                if (rbuf.Count == 0) { var p = await Payload().ConfigureAwait(false); if (p == null) return 0; rbuf.AddRange(p); }
                int n = Math.Min(count, rbuf.Count); rbuf.CopyTo(0, b, off, n); rbuf.RemoveRange(0, n); return n;
            }
            public Task WriteAsync(byte[] b, int off, int count)
            {
                var o = new List<byte>(count + 5 * (count / 16384 + 1));
                for (int i = 0; i < count; i += 16384)
                {
                    int n = Math.Min(16384, count - i);
                    o.AddRange(new byte[] { 0x17, 0x03, 0x03, (byte)(n >> 8), (byte)n });
                    for (int j = 0; j < n; j++) o.Add(b[off + i + j]);
                }
                var a = o.ToArray(); return inner.WriteAsync(a, 0, a.Length);
            }
            public void Close() { inner.Close(); }
        }

        // не наш клиент — весь поток на настоящий сайт маскировки (для сканеров прокси выглядит как обычный HTTPS)
        public static async Task ProxyToMasking(TgBufStream client, byte[] initial, string domain, string label, int bufSize)
        {
            TcpClient up;
            try { up = await TgNetUtil.Connect(domain, 443, 10, bufSize).ConfigureAwait(false); }
            catch (Exception ex) { TgLog.Wn("[" + label + "] masking: cannot connect to " + domain + ":443: " + TgNetUtil.Err(ex)); return; }
            TgLog.D("[" + label + "] masking -> " + domain + ":443");
            Interlocked.Increment(ref TgStats.Masked);
            var us = up.GetStream();
            try
            {
                if (initial != null && initial.Length > 0) await us.WriteAsync(initial, 0, initial.Length).ConfigureAwait(false);
                var a = Task.Run(async () => { var b = new byte[16384]; try { int n; while ((n = await client.ReadAsync(b, 0, b.Length).ConfigureAwait(false)) > 0) await us.WriteAsync(b, 0, n).ConfigureAwait(false); } catch { } try { up.Client.Shutdown(SocketShutdown.Send); } catch { } });
                var c = Task.Run(async () => { var b = new byte[16384]; try { int n; while ((n = await us.ReadAsync(b, 0, b.Length).ConfigureAwait(false)) > 0) await client.WriteAsync(b, 0, n).ConfigureAwait(false); } catch { } });
                await Task.WhenAll(a, c).ConfigureAwait(false);
            }
            catch { }
            finally { up.Close(); client.Close(); }
        }
    }

    // ---- пул готовых WS-соединений к Telegram (по DC, медиа, тестовые DC) ----
    public sealed class TgWsPool
    {
        const double MaxAge = 120, CheckEvery = 5, BackoffInit = 1, BackoffMax = 3600;
        readonly object gate = new object();
        readonly Dictionary<string, Queue<TgWs>> idle = new Dictionary<string, Queue<TgWs>>();
        readonly HashSet<string> refilling = new HashSet<string>(), rotating = new HashSet<string>();
        readonly Dictionary<string, int> failures = new Dictionary<string, int>();
        readonly Dictionary<string, DateTime> refillAfter = new Dictionary<string, DateTime>();
        volatile bool tryFrontingFirst;
        TgConfig cfg; CancellationTokenSource cts = new CancellationTokenSource();
        public const string FrontSni = "sprinthost.ru";

        static string Key(int dc, bool media, bool test) { return dc + (media ? "m" : "") + (test ? "t" : ""); }

        public void Reset(TgConfig c)
        {
            cfg = c; var old = cts; cts = new CancellationTokenSource(); old.Cancel();
            lock (gate)
            {
                foreach (var q in idle.Values) foreach (var ws in q) ws.Abort();
                idle.Clear(); refilling.Clear(); rotating.Clear(); failures.Clear(); refillAfter.Clear();
            }
            tryFrontingFirst = false;
        }

        public TgWs Get(int dc, bool media, bool test)
        {
            string ip; if (cfg == null || cfg.PoolSize <= 0 || !cfg.DcRedirects.TryGetValue(dc, out ip) || ip == null) return null;
            string key = Key(dc, media, test); var domains = TgConst.WsDomains(dc, media);
            TgWs got = null;
            lock (gate)
            {
                Queue<TgWs> q; if (!idle.TryGetValue(key, out q)) idle[key] = q = new Queue<TgWs>();
                while (q.Count > 0)
                {
                    var ws = q.Dequeue();
                    if (ws.Stale(MaxAge)) { var _ = ws.CloseAsync(); continue; }
                    got = ws; break;
                }
            }
            if (got != null) { Interlocked.Increment(ref TgStats.PoolHit); TgLog.D("WS pool hit DC" + dc + (test ? "t" : "") + (media ? "m" : "")); }
            else Interlocked.Increment(ref TgStats.PoolMiss);
            ScheduleRefill(key, ip, domains, test);
            return got;
        }

        void ScheduleRefill(string key, string ip, string[] domains, bool test)
        {
            if (cfg.PoolSize <= 0) return;
            var tok = cts.Token;
            lock (gate)
            {
                if (rotating.Add(key)) Task.Run(() => Rotate(key, ip, domains, test, tok));
                DateTime after;
                if (refilling.Contains(key) || (refillAfter.TryGetValue(key, out after) && DateTime.UtcNow < after)) return;
                refilling.Add(key);
            }
            Task.Run(() => Refill(key, ip, domains, test, tok));
        }

        async Task Refill(string key, string ip, string[] domains, bool test, CancellationToken tok)
        {
            try
            {
                int needed; lock (gate) { Queue<TgWs> q; idle.TryGetValue(key, out q); needed = cfg.PoolSize - (q == null ? 0 : q.Count); }
                if (needed <= 0) return;
                var tasks = Enumerable.Range(0, needed).Select(_ => ConnectOne(ip, domains, test ? TgConst.WsPathTest : TgConst.WsPath)).ToList();
                int connected = 0;
                foreach (var t in tasks)
                {
                    TgWs ws = null; try { ws = await t.ConfigureAwait(false); } catch (Exception ex) { TgLog.D("WS pool connect failed: " + TgNetUtil.Err(ex)); }
                    if (ws == null) continue;
                    if (tok.IsCancellationRequested) { ws.Abort(); continue; }
                    lock (gate) { Queue<TgWs> q; if (!idle.TryGetValue(key, out q)) idle[key] = q = new Queue<TgWs>(); q.Enqueue(ws); }
                    connected++;
                }
                lock (gate)
                {
                    if (connected > 0) { failures.Remove(key); refillAfter.Remove(key); }
                    else
                    {
                        int f; failures.TryGetValue(key, out f); failures[key] = ++f;
                        double delay = Math.Min(BackoffInit * Math.Pow(2, Math.Min(f - 1, 12)), BackoffMax);
                        refillAfter[key] = DateTime.UtcNow.AddSeconds(delay);
                        TgLog.I("WS pool refill failed for DC" + key + ", retry in " + delay.ToString("0") + "s");
                    }
                }
            }
            finally { lock (gate) refilling.Remove(key); }
        }

        async Task Rotate(string key, string ip, string[] domains, bool test, CancellationToken tok)
        {
            try
            {
                while (!tok.IsCancellationRequested && cfg.PoolSize > 0)
                {
                    DateTime wake = DateTime.UtcNow.AddSeconds(CheckEvery); bool need;
                    lock (gate)
                    {
                        Queue<TgWs> q; if (!idle.TryGetValue(key, out q)) idle[key] = q = new Queue<TgWs>();
                        var keep = new Queue<TgWs>(); int expired = 0;
                        while (q.Count > 0) { var ws = q.Dequeue(); if (ws.Stale(MaxAge)) { expired++; var _ = ws.CloseAsync(); } else keep.Enqueue(ws); }
                        idle[key] = keep;
                        if (expired > 0) TgLog.D("WS pool rotated DC" + key + ": " + expired + " stale, " + keep.Count + " ready");
                        need = keep.Count < cfg.PoolSize;
                        foreach (var ws in keep) { var w = ws.Created.AddSeconds(MaxAge); if (w < wake) wake = w; }
                        DateTime after; if (!refilling.Contains(key) && refillAfter.TryGetValue(key, out after) && after > DateTime.UtcNow && after < wake) wake = after;
                    }
                    if (need) ScheduleRefillNoRotate(key, ip, domains, test, tok);
                    var ms = Math.Max(50, (wake - DateTime.UtcNow).TotalMilliseconds);
                    await Task.Delay(TimeSpan.FromMilliseconds(ms), tok).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { lock (gate) rotating.Remove(key); }
        }
        void ScheduleRefillNoRotate(string key, string ip, string[] domains, bool test, CancellationToken tok)
        {
            lock (gate)
            {
                DateTime after;
                if (refilling.Contains(key) || (refillAfter.TryGetValue(key, out after) && DateTime.UtcNow < after)) return;
                refilling.Add(key);
            }
            Task.Run(() => Refill(key, ip, domains, test, tok));
        }

        async Task<TgWs> ConnectOne(string ip, string[] domains, string path)
        {
            foreach (var domain in domains)
            {
                var modes = tryFrontingFirst ? new[] { true, false } : new[] { false, true };
                foreach (var fronted in modes)
                {
                    try
                    {
                        var ws = await TgWs.Connect(ip, domain, fronted ? 7 : 8, path, fronted ? FrontSni : null, true, cfg.BufferSize).ConfigureAwait(false);
                        tryFrontingFirst = fronted;
                        if (fronted) Interlocked.Increment(ref TgStats.Fronting);
                        TgLog.D("WS pool connected " + domain + path + " via " + ip + " (fronting=" + fronted + ")");
                        return ws;
                    }
                    catch (Exception ex) { Interlocked.Increment(ref TgStats.WsErr); TgLog.D("WS pool connect " + domain + path + " via " + ip + " (fronting=" + fronted + "): " + TgNetUtil.Err(ex)); }
                }
            }
            return null;
        }

        public void Warmup()
        {
            foreach (var kv in cfg.DcRedirects)
            {
                if (kv.Value == null) continue;
                foreach (var media in new[] { false, true }) ScheduleRefill(Key(kv.Key, media, cfg.ForceTestDc), kv.Value, TgConst.WsDomains(kv.Key, media), cfg.ForceTestDc);
            }
            TgLog.I("WS pool warmup started for " + cfg.DcRedirects.Count + " DC(s)");
        }
        public int Ready { get { lock (gate) return idle.Values.Sum(q => q.Count); } }
    }

    // ---- пул соединений через Cloudflare Worker (по одному на DC без своего IP) ----
    public sealed class TgWorkerPool
    {
        const double MaxAge = 100;
        readonly object gate = new object();
        readonly Dictionary<int, Queue<Tuple<TgWs, string>>> idle = new Dictionary<int, Queue<Tuple<TgWs, string>>>();
        readonly HashSet<int> refilling = new HashSet<int>();
        TgConfig cfg; CancellationTokenSource cts = new CancellationTokenSource();

        public void Reset(TgConfig c)
        {
            cfg = c; var old = cts; cts = new CancellationTokenSource(); old.Cancel();
            lock (gate) { foreach (var q in idle.Values) foreach (var t in q) t.Item1.Abort(); idle.Clear(); refilling.Clear(); }
        }
        public static string PathFor(string dst, int dc) { return "/apiws?dst=" + Uri.EscapeDataString(dst) + "&dc=" + dc; }

        public Tuple<TgWs, string> Get(int dc, string dst, List<string> domains)
        {
            Tuple<TgWs, string> got = null;
            lock (gate)
            {
                Queue<Tuple<TgWs, string>> q; if (!idle.TryGetValue(dc, out q)) idle[dc] = q = new Queue<Tuple<TgWs, string>>();
                while (q.Count > 0) { var t = q.Dequeue(); if (t.Item1.Stale(MaxAge)) { var _ = t.Item1.CloseAsync(); continue; } got = t; break; }
            }
            if (got != null) { Interlocked.Increment(ref TgStats.CfPoolHit); Schedule(dc, dst, domains); }
            else Interlocked.Increment(ref TgStats.CfPoolMiss);
            return got;
        }
        void Schedule(int dc, string dst, List<string> domains)
        {
            var tok = cts.Token;
            lock (gate) { if (!refilling.Add(dc)) return; }
            Task.Run(async () =>
            {
                try
                {
                    int needed; lock (gate) { Queue<Tuple<TgWs, string>> q; idle.TryGetValue(dc, out q); needed = Math.Min(cfg.PoolSize, 1) - (q == null ? 0 : q.Count); }
                    for (int i = 0; i < needed; i++)
                    {
                        var c = await ConnectOne(domains, dst, dc).ConfigureAwait(false);
                        if (c == null) break;
                        if (tok.IsCancellationRequested) { c.Item1.Abort(); return; }
                        lock (gate) { Queue<Tuple<TgWs, string>> q; if (!idle.TryGetValue(dc, out q)) idle[dc] = q = new Queue<Tuple<TgWs, string>>(); q.Enqueue(c); }
                    }
                }
                finally { lock (gate) refilling.Remove(dc); }
            });
        }
        async Task<Tuple<TgWs, string>> ConnectOne(List<string> domains, string dst, int dc)
        {
            foreach (var d in Available(domains))
            {
                try { var ws = await TgWs.Connect(d, d, 8, PathFor(dst, dc), null, !cfg.DisableSecure, cfg.BufferSize).ConfigureAwait(false); return Tuple.Create(ws, d); }
                catch { }
            }
            return null;
        }
        public List<string> Available(List<string> domains) { var l = domains.Distinct().ToList(); TgConst.Shuffle(l); return l; }

        public void Warmup()
        {
            if (cfg.CfWorkerDomains.Count == 0) return;
            var fb = TgConst.DcDefaultIps.Where(kv => !cfg.DcRedirects.ContainsKey(kv.Key)).ToList();
            if (fb.Count == 0) return;
            foreach (var kv in fb) Schedule(kv.Key, kv.Value, cfg.CfWorkerDomains);
            TgLog.I("CF worker pool warmup started for " + fb.Count + " DC(s)");
        }
    }
}
