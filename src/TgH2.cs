using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetProbe
{
    // HTTP/2 к CF-прокси для медиа (как cf_h2): каждый MTProto-пакет — POST https://kwsN.<домен>/api, ответ — пакеты сервера.
    // HTTP/2 в .NET Framework нет, поэтому — системный WinHTTP (Windows 10 1607+): он сам держит одно HTTP/2-соединение
    // на хост и мультиплексирует запросы. Системный прокси не используется (как trust_env=False).
    static class WinHttp
    {
        const string Dll = "winhttp.dll";
        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr WinHttpOpen(string agent, uint access, string proxy, string bypass, uint flags);
        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr WinHttpConnect(IntPtr session, string server, ushort port, uint reserved);
        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr WinHttpOpenRequest(IntPtr connect, string verb, string obj, string version, string referrer, IntPtr accept, uint flags);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpSetOption(IntPtr h, uint option, ref uint value, uint len);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpSetTimeouts(IntPtr h, int resolve, int connect, int send, int receive);
        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool WinHttpAddRequestHeaders(IntPtr req, string headers, uint len, uint modifiers);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpSendRequest(IntPtr req, IntPtr headers, uint headersLen, byte[] optional, uint optionalLen, uint totalLen, IntPtr ctx);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpReceiveResponse(IntPtr req, IntPtr reserved);
        [DllImport(Dll, SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool WinHttpQueryHeaders(IntPtr req, uint level, string name, StringBuilder buf, ref uint len, IntPtr index);
        [DllImport(Dll, SetLastError = true, EntryPoint = "WinHttpQueryHeaders")] public static extern bool WinHttpQueryHeadersNum(IntPtr req, uint level, IntPtr name, ref uint value, ref uint len, IntPtr index);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpQueryOption(IntPtr h, uint option, ref uint value, ref uint len);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpReadData(IntPtr req, byte[] buf, uint toRead, out uint read);
        [DllImport(Dll, SetLastError = true)] public static extern bool WinHttpCloseHandle(IntPtr h);
        public const uint AccessNoProxy = 1, FlagSecure = 0x00800000, OptEnableProto = 133, OptProtoUsed = 134, ProtoHttp2 = 1, AddHeader = 0x20000000;
        public const uint QStatus = 19 | 0x20000000, QContentType = 1, QContentLength = 5, QContentEncoding = 29, QCustom = 65535;

        public static string Header(IntPtr req, uint level, string name = null)
        {
            uint len = 0; WinHttpQueryHeaders(req, level, name, null, ref len, IntPtr.Zero);
            if (len == 0) return null;
            var sb = new StringBuilder((int)len / 2 + 1);
            return WinHttpQueryHeaders(req, level, name, sb, ref len, IntPtr.Zero) ? sb.ToString() : null;
        }
        public static Exception Err(string what) { int e = Marshal.GetLastWin32Error(); return new IOException(what + " failed (WinHTTP " + e + ")"); }
    }

    sealed class TgTransportError : Exception { public readonly int Code; public TgTransportError(int code, string src) : base(code + " (" + src + ")") { Code = code; } }

    // «полоса» — один хост CF (kwsN.домен) со своим WinHTTP-сеансом (= одно HTTP/2-соединение, до 64 потоков)
    sealed class TgH2Lane
    {
        public const int MaxRequests = 64; public const long MaxBytes = 32L << 20;
        public readonly string Host; public readonly int Id;
        IntPtr session, connect; readonly object gate = new object();
        public DateTime FailedUntil = DateTime.MinValue; public volatile bool Closed;
        public int Inflight; long queued; public long Requests, Responses, Errors;
        readonly SemaphoreSlim changed = new SemaphoreSlim(0);
        public readonly HashSet<TgH2Channel> Channels = new HashSet<TgH2Channel>();

        public TgH2Lane(string host, int id)
        {
            Host = host; Id = id;
            session = WinHttp.WinHttpOpen("tg-ws-proxy", WinHttp.AccessNoProxy, null, null, 0);
            if (session == IntPtr.Zero) throw WinHttp.Err("WinHttpOpen");
            uint h2 = WinHttp.ProtoHttp2;
            if (!WinHttp.WinHttpSetOption(session, WinHttp.OptEnableProto, ref h2, 4)) { WinHttp.WinHttpCloseHandle(session); throw WinHttp.Err("HTTP/2 enable"); }
            WinHttp.WinHttpSetTimeouts(session, 0, 8000, 65000, 65000);
            connect = WinHttp.WinHttpConnect(session, host, 443, 0);
            if (connect == IntPtr.Zero) { WinHttp.WinHttpCloseHandle(session); throw WinHttp.Err("WinHttpConnect"); }
            Interlocked.Increment(ref TgStats.H2Tcp);
        }

        public sealed class Req { public IntPtr H; public volatile bool Cancelled, Receiving; public Action OnHeaders; }

        // синхронный запрос (на своём потоке); Cancel закрывает дескриптор — блокирующий вызов сразу возвращается
        byte[] Run(string verb, byte[] body, Req r, bool preflight)
        {
            IntPtr req;
            lock (gate)
            {
                if (Closed) throw new IOException("H2 lane closed");
                req = WinHttp.WinHttpOpenRequest(connect, verb, "/api", null, null, IntPtr.Zero, WinHttp.FlagSecure);
            }
            if (req == IntPtr.Zero) throw WinHttp.Err("WinHttpOpenRequest");
            r.H = req;
            try
            {
                if (r.Cancelled) throw new OperationCanceledException();
                string hdr = body != null ? "Content-Type: application/octet-stream\r\nAccept-Encoding: identity\r\n" : "Accept-Encoding: identity\r\n";
                WinHttp.WinHttpAddRequestHeaders(req, hdr, 0xFFFFFFFF, WinHttp.AddHeader);
                uint blen = body == null ? 0u : (uint)body.Length;
                if (!WinHttp.WinHttpSendRequest(req, IntPtr.Zero, 0, body, blen, blen, IntPtr.Zero)) throw r.Cancelled ? new OperationCanceledException() : WinHttp.Err("send");
                if (!WinHttp.WinHttpReceiveResponse(req, IntPtr.Zero)) throw r.Cancelled ? new OperationCanceledException() : WinHttp.Err("receive");
                uint proto = 0, plen = 4; WinHttp.WinHttpQueryOption(req, WinHttp.OptProtoUsed, ref proto, ref plen);
                if ((proto & WinHttp.ProtoHttp2) == 0) throw new InvalidDataException("CF /api negotiated HTTP/1.1, not HTTP/2");
                uint status = 0, slen = 4; WinHttp.WinHttpQueryHeadersNum(req, WinHttp.QStatus, IntPtr.Zero, ref status, ref slen, IntPtr.Zero);
                string ray = WinHttp.Header(req, WinHttp.QCustom, "cf-ray") ?? "-";
                if (preflight)
                {
                    if (!(status >= 200 && status < 300) && status != 405 && status != 501) throw new IOException("CF /api preflight HTTP " + status + " cf_ray=" + ray);
                    return null;
                }
                if (status == 403 || status == 404 || status == 429 || status == 444) throw new TgTransportError(-(int)status, "HTTP " + status + " cf_ray=" + ray);
                if (status != 200) throw new IOException("CF /api HTTP " + status + " cf_ray=" + ray);
                var ct = (WinHttp.Header(req, WinHttp.QContentType) ?? "").ToLowerInvariant();
                if (ct.Contains("text/html")) throw new InvalidDataException("CF /api returned HTML instead of MTProto");
                long declared = -1; long dl; var cls = WinHttp.Header(req, WinHttp.QContentLength); if (cls != null && long.TryParse(cls, out dl)) declared = dl;
                if (declared > TgH2.MaxPacket) throw new InvalidDataException("H2 response Content-Length exceeds native packet bound");
                r.Receiving = true; if (r.OnHeaders != null) r.OnHeaders();
                var ms = new MemoryStream(); var buf = new byte[65536];
                while (true)
                {
                    uint n;
                    if (!WinHttp.WinHttpReadData(req, buf, (uint)buf.Length, out n)) throw r.Cancelled ? new OperationCanceledException() : WinHttp.Err("read");
                    if (n == 0) break;
                    if (ms.Length + n > TgH2.MaxPacket) throw new InvalidDataException("H2 response exceeds native packet bound");
                    ms.Write(buf, 0, (int)n);
                }
                var content = ms.ToArray();
                var enc = WinHttp.Header(req, WinHttp.QContentEncoding) ?? "identity";
                if (declared >= 0 && enc == "identity" && content.Length != declared) throw new InvalidDataException("incomplete H2 response body");
                if (content.Length % 4 != 0) throw new InvalidDataException("CF /api response is not an aligned MTProto packet");
                if (content.Length == 4) { int code = BitConverter.ToInt32(content, 0); if (code < 0) throw new TgTransportError(code, "HTTP 200 payload"); }
                return content;
            }
            finally { lock (gate) { if (r.H != IntPtr.Zero) { WinHttp.WinHttpCloseHandle(r.H); r.H = IntPtr.Zero; } } }
        }
        public void Cancel(Req r) { r.Cancelled = true; lock (gate) { if (r.H != IntPtr.Zero) { WinHttp.WinHttpCloseHandle(r.H); r.H = IntPtr.Zero; } } }

        public Task Preflight()
        {
            var r = new Req();
            return Task.Factory.StartNew(() => { Run("HEAD", null, r, true); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public bool HasCapacity(int len) { return Inflight < MaxRequests && Interlocked.Read(ref queued) + len <= MaxBytes; }
        public async Task<bool> Reserve(int len, bool wait)
        {
            var until = DateTime.UtcNow.AddSeconds(TgH2.RequestTimeout);
            while (true)
            {
                lock (gate)
                {
                    if (Closed) throw new IOException("H2 lane closed");
                    if (HasCapacity(len)) { Inflight++; queued += len; return true; }
                }
                if (!wait) return false;
                if (DateTime.UtcNow > until) throw new TimeoutException("H2 lane capacity");
                await changed.WaitAsync(250).ConfigureAwait(false);
            }
        }
        public void Release(int len) { lock (gate) { Inflight--; queued -= len; } changed.Release(); }

        public async Task<byte[]> Post(byte[] body, Req r)
        {
            Interlocked.Increment(ref Requests); Interlocked.Increment(ref TgStats.H2Req);
            try
            {
                var res = await Task.Factory.StartNew(() => Run("POST", body, r, false), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
                Interlocked.Increment(ref Responses); return res;
            }
            catch (TgTransportError) { Interlocked.Increment(ref Errors); throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { Interlocked.Increment(ref Errors); FailedUntil = DateTime.UtcNow.AddSeconds(TgH2.DomainCooldown); throw; }
        }

        public void Close()
        {
            List<TgH2Channel> ch; lock (gate) { if (Closed) return; Closed = true; } lock (Channels) ch = Channels.ToList();
            foreach (var c in ch) c.Fail(new IOException("H2 lane closed"));
            lock (gate)
            {
                if (connect != IntPtr.Zero) { WinHttp.WinHttpCloseHandle(connect); connect = IntPtr.Zero; }
                if (session != IntPtr.Zero) { WinHttp.WinHttpCloseHandle(session); session = IntPtr.Zero; }
            }
            changed.Release();
            TgLog.D("H2 CLOSED lane=" + Id + " host=" + Host + " requests=" + Requests + " replies=" + Responses + " errors=" + Errors);
        }
    }

    // канал = одно соединение Telegram (медиа) поверх полосы: запросы параллельно, ответы — по мере готовности,
    // «опрос» (повтор последнего пакета), когда всё отвечено — так сервер может прислать то, что пришло позже
    sealed class TgH2Channel
    {
        sealed class Packet { public byte[] Body; public DateTime SentAt; public long Seq; public int Attempts; public DateTime LastAttempt; public Pending Replay; public readonly HashSet<Pending> Originals = new HashSet<Pending>(); public bool Retired; public byte[] ReplyId; public bool Replayed { get { return Attempts > 0 || LastAttempt > DateTime.MinValue; } } }
        sealed class Pending { public TgH2Lane.Req R = new TgH2Lane.Req(); public int Len; public bool IsReplay; public DateTime Since, SentAt; public Packet Pkt; public Task T; }

        public readonly TgH2Lane Lane; public readonly int Id; public readonly string Label;
        readonly object gate = new object();
        readonly List<Pending> pending = new List<Pending>();
        readonly List<Packet> history = new List<Packet>(); byte[] historyKey;
        readonly ConcurrentQueue<object> replies = new ConcurrentQueue<object>(); readonly SemaphoreSlim replySignal = new SemaphoreSlim(0);
        readonly SemaphoreSlim capChanged = new SemaphoreSlim(0), sendLock = new SemaphoreSlim(1, 1);
        long pendingBytes, replyBytes, seq; int replyCount;
        public volatile bool Closed; public int? TransportError; public DateTime? DeliveringSince;
        public DateTime LastProgress = DateTime.UtcNow;
        public long Requests, ReplayRequests, Up, Down;

        public TgH2Channel(TgH2Lane lane, int id, string label) { Lane = lane; Id = id; Label = label; lock (lane.Channels) lane.Channels.Add(this); }
        bool Backpressure { get { return Interlocked.Read(ref replyBytes) > 0 || DeliveringSince != null; } }
        bool HasCapacity(int len) { lock (gate) return pending.Count < TgH2.MaxChannelRequests && pendingBytes + len <= TgH2.MaxChannelBytes; }

        public void Fail(Exception e)
        {
            if (Closed) return;
            var te = e as TgTransportError; if (te != null) TransportError = te.Code;
            Closed = true; object x; while (replies.TryDequeue(out x)) { } Interlocked.Exchange(ref replyBytes, 0); replyCount = 0;
            replies.Enqueue(e); replySignal.Release(); capChanged.Release();
        }

        public async Task Send(byte[] body)
        {
            await sendLock.WaitAsync().ConfigureAwait(false);
            try { await SendCore(body, false).ConfigureAwait(false); }
            finally { sendLock.Release(); }
        }

        async Task<bool> SendCore(byte[] body, bool replay)
        {
            var until = DateTime.UtcNow.AddSeconds(TgH2.RequestTimeout);
            while (!HasCapacity(body.Length))
            {
                if (replay) return false;
                if (Closed) throw new IOException("H2 channel closed");
                MakeRoom();
                if (DateTime.UtcNow > until) throw new TimeoutException("H2 channel capacity");
                await capChanged.WaitAsync(250).ConfigureAwait(false);
            }
            if (Closed) throw new IOException("H2 channel closed");
            if (!await Lane.Reserve(body.Length, !replay).ConfigureAwait(false)) return false;
            if (Closed) { Lane.Release(body.Length); throw new IOException("H2 channel closed"); }
            var now = DateTime.UtcNow; Packet pkt;
            lock (gate)
            {
                pkt = replay ? history.FirstOrDefault(h => h.Body.SequenceEqual(body)) : Remember(body, now);
                if (replay && (pkt == null || pkt.Replay != null || !Due(pkt, now))) { Lane.Release(body.Length); return false; }
                var p = new Pending { Len = body.Length, IsReplay = replay, Since = now, Pkt = pkt };
                pendingBytes += body.Length; Requests++; if (replay) ReplayRequests++; Up += body.Length;
                Interlocked.Add(ref TgStats.Up, body.Length);
                pending.Add(p);
                if (pkt != null)
                {
                    if (replay) { pkt.Attempts++; pkt.LastAttempt = now; pkt.Replay = p; }
                    else { pkt.Originals.Add(p); Trim(now); }
                }
                p.T = Run(body, p);
            }
            return true;
        }

        Packet Remember(byte[] body, DateTime now)
        {
            if (body.Length < 8 || body.Take(8).All(b => b == 0)) return null; // незашифрованные служебные — не повторяем
            var key = body.Take(8).ToArray();
            if (historyKey == null || !historyKey.SequenceEqual(key)) { history.Clear(); historyKey = key; }
            if (body.Length > TgH2.ReplayHistoryBytes) return null;
            var p = history.FirstOrDefault(h => h.Body.SequenceEqual(body));
            if (p == null) { p = new Packet { Body = body, SentAt = now, Seq = ++seq }; history.Add(p); }
            return p;
        }
        void Trim(DateTime now)
        {
            long bytes = history.Sum(h => (long)h.Body.Length);
            while (history.Count > TgH2.ReplayHistoryPackets || bytes > TgH2.ReplayHistoryBytes)
            {
                var v = history.FirstOrDefault(h => h.Originals.Count == 0 && !(h.Retired && h.Attempts < TgH2.ReplayMaxAttempts && (now - h.SentAt).TotalSeconds <= TgH2.ReplayMaxAge)) ?? history[0];
                history.Remove(v); bytes -= v.Body.Length;
            }
        }

        async Task Run(byte[] body, Pending p)
        {
            await Task.Yield();
            p.R.OnHeaders = () => { LastProgress = DateTime.UtcNow; };
            p.SentAt = DateTime.UtcNow;
            try
            {
                var reply = await Lane.Post(body, p.R).ConfigureAwait(false);
                LastProgress = DateTime.UtcNow;
                if (reply != null && reply.Length > 0 && !Closed)
                {
                    lock (gate)
                    {
                        if (p.Pkt != null && reply.Length >= 24)
                        {
                            var rid = reply.Take(24).ToArray();
                            if (p.Pkt.ReplyId == null || !p.Pkt.ReplyId.SequenceEqual(rid)) { p.Pkt.ReplyId = rid; p.Pkt.Attempts = 0; }
                        }
                    }
                    if (replyCount >= 16 || Interlocked.Read(ref replyBytes) + reply.Length > TgH2.MaxChannelBytes) throw new InsufficientMemoryException("H2 client response queue exceeded bound");
                    Interlocked.Add(ref replyBytes, reply.Length); Interlocked.Increment(ref replyCount);
                    replies.Enqueue(reply); replySignal.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (TgTransportError ex)
            {
                Interlocked.Increment(ref TgStats.H2Err);
                if (!Closed) TgLog.Wn("[" + Label + "] H2 lane=" + Lane.Id + " channel=" + Id + " host=" + Lane.Host + " MTProto transport error=" + ex.Message + " bytes=" + body.Length + " replay=" + (p.IsReplay ? 1 : 0) + "; reporting to client");
                Fail(ex);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref TgStats.H2Err);
                if (!Closed) TgLog.Wn("[" + Label + "] H2 lane=" + Lane.Id + " channel=" + Id + " request failed: " + TgNetUtil.Err(ex) + "; closing native channel, reconnect may select another domain");
                Fail(ex);
            }
            finally { Finish(p); }
        }

        void Finish(Pending p)
        {
            lock (gate)
            {
                if (!pending.Remove(p)) return;
                pendingBytes -= p.Len;
                if (p.Pkt != null) { p.Pkt.Originals.Remove(p); if (p.Pkt.Replay == p) p.Pkt.Replay = null; }
            }
            Lane.Release(p.Len);
            capChanged.Release();
        }

        int Limit(Packet e) { return e.Originals.Count > 0 || e.Retired || (history.Count > 0 && e == history[history.Count - 1]) ? TgH2.ReplayMaxAttempts : 0; }
        bool Overdue(Packet e, DateTime now) { return e.Retired || e.Originals.Any(o => !o.R.Receiving && (now - o.SentAt).TotalSeconds >= TgH2.ReplayRequestSeconds); }
        bool Due(Packet e, DateTime now)
        {
            double age = (now - e.SentAt).TotalSeconds;
            if (age < 0 || age > TgH2.ReplayMaxAge) return false;
            if (e.Attempts >= Limit(e)) return false;
            if (Backpressure) return false;
            if (e.Originals.Any(o => o.R.Receiving)) return false;
            if (e.Replay != null && e.Replay.R.Receiving) return false;
            if (e.Replayed && (now - e.LastAttempt).TotalSeconds < TgH2.ReplayRetrySeconds) return false;
            if (Overdue(e, now)) return true;
            if (e != history[history.Count - 1] || pending.Any(x => x != e.Replay)) return false;
            if (e.Replayed && (now - LastProgress).TotalSeconds < TgH2.ReplayIdleSeconds) return false;
            return true;
        }

        // канал упёрся в лимит — снимаем самый старый «опрос», который дольше 3 с ждёт заголовков
        void MakeRoom()
        {
            Pending victim;
            lock (gate)
            {
                var now = DateTime.UtcNow;
                victim = pending.Where(x => !x.R.Receiving && (now - x.SentAt).TotalSeconds >= TgH2.ReplayRequestSeconds && x.SentAt > DateTime.MinValue)
                                .OrderBy(x => x.IsReplay ? 0 : 1).ThenBy(x => x.SentAt).FirstOrDefault();
                if (victim == null) return;
                if (!victim.IsReplay && victim.Pkt != null && (now - victim.Pkt.SentAt).TotalSeconds <= TgH2.ReplayMaxAge) victim.Pkt.Retired = true;
                else if (!victim.IsReplay) return;
            }
            Lane.Cancel(victim.R);
        }

        public async Task Recover()
        {
            while (!Closed)
            {
                await Task.Delay(TimeSpan.FromSeconds(TgH2.ReplayCheckSeconds)).ConfigureAwait(false);
                if (Closed || Backpressure) continue;
                byte[] body = null; Pending replace = null; var now = DateTime.UtcNow;
                lock (gate)
                {
                    if (history.Count == 0) continue;
                    var cands = history.OrderBy(e => Overdue(e, now) ? 0 : 1).ThenBy(e => Overdue(e, now) ? e.Seq : -e.Seq).ToList();
                    foreach (var e in cands)
                    {
                        if (!Due(e, now)) continue;
                        replace = e.Replay;
                        if (replace == null && pending.Count(x => x.IsReplay) >= TgH2.MaxChannelReplays)
                        {
                            replace = pending.FirstOrDefault(x => x.IsReplay && !x.R.Receiving);
                            if (replace == null) continue;
                        }
                        if (replace != null && (replace.R.Receiving || replace.SentAt == DateTime.MinValue || (now - replace.SentAt).TotalSeconds < TgH2.ReplaySlotSeconds)) { replace = null; continue; }
                        body = e.Body; break;
                    }
                }
                if (body == null) continue;
                if (replace != null) { Lane.Cancel(replace.R); try { await replace.T.ConfigureAwait(false); } catch { } }
                if (sendLock.Wait(0))
                {
                    try { await SendCore(body, true).ConfigureAwait(false); } catch { }
                    finally { sendLock.Release(); }
                }
            }
        }

        public async Task<byte[]> Receive()
        {
            while (true)
            {
                object v;
                if (replies.TryDequeue(out v))
                {
                    var ex = v as Exception; if (ex != null) throw ex;
                    var b = (byte[])v; Interlocked.Add(ref replyBytes, -b.Length); Interlocked.Decrement(ref replyCount);
                    return b;
                }
                await replySignal.WaitAsync().ConfigureAwait(false);
            }
        }

        public async Task Close()
        {
            Fail(new IOException("H2 channel closed"));
            List<Pending> ps; lock (gate) ps = pending.ToList();
            foreach (var p in ps) Lane.Cancel(p.R);
            foreach (var p in ps) { try { if (p.T != null) await p.T.ConfigureAwait(false); } catch { } }
            lock (gate) { history.Clear(); }
            lock (Lane.Channels) Lane.Channels.Remove(this);
            TgLog.D("[" + Label + "] H2 channel=" + Id + " closed lane=" + Lane.Id + " requests=" + Requests + " replays=" + ReplayRequests + " up=" + Up + " down=" + Down);
        }
    }

    public sealed class TgH2Pool
    {
        readonly Dictionary<string, TgH2Lane> lanes = new Dictionary<string, TgH2Lane>();
        readonly Dictionary<string, DateTime> failed = new Dictionary<string, DateTime>();
        readonly Dictionary<int, DateTime> setupRetry = new Dictionary<int, DateTime>();
        readonly SemaphoreSlim openLock = new SemaphoreSlim(1, 1);
        int nextLane, nextChannel; volatile bool closed;
        public TgH2Pool(TgConfig c) { }

        internal async Task<TgH2Channel> Open(int dc, string label)
        {
            if (closed) return null;
            DateTime ra; lock (setupRetry) if (setupRetry.TryGetValue(dc, out ra) && DateTime.UtcNow < ra) return null;
            try { return await TgNetUtil.WithTimeout(OpenCore(dc, label), TgH2.SetupTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                lock (setupRetry) setupRetry[dc] = DateTime.UtcNow.AddSeconds(TgH2.DomainCooldown);
                Interlocked.Increment(ref TgStats.H2Err);
                TgLog.Wn("[" + label + "] H2 DC" + dc + " setup timed out; using WS for " + TgH2.DomainCooldown + "s");
                return null;
            }
        }

        async Task<TgH2Channel> OpenCore(int dc, string label)
        {
            if (!TgConst.DcDefaultIps.ContainsKey(dc)) { TgLog.Wn("[" + label + "] H2 unsupported DC" + dc + " -> existing WS route"); return null; }
            foreach (var bd in TgBalancer.For(dc))
            {
                var host = "kws" + dc + "." + bd;
                await openLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (closed) return null;
                    TgH2Lane lane; lanes.TryGetValue(host, out lane);
                    DateTime f; failed.TryGetValue(host, out f);
                    if (DateTime.UtcNow < f || (lane != null && DateTime.UtcNow < lane.FailedUntil)) continue;
                    if (lane != null && lane.Closed) { lanes.Remove(host); lane = null; }
                    if (lane == null)
                    {
                        try
                        {
                            lane = new TgH2Lane(host, ++nextLane);
                            await lane.Preflight().ConfigureAwait(false);
                            TgLog.D("H2 lane=" + lane.Id + " ready host=" + host);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref TgStats.H2Err);
                            failed[host] = DateTime.UtcNow.AddSeconds(TgH2.DomainCooldown);
                            TgLog.Wn("[" + label + "] H2 lane preflight failed host=" + host + ": " + TgNetUtil.Err(ex));
                            if (lane != null) lane.Close();
                            continue;
                        }
                        if (closed) { lane.Close(); return null; }
                        lanes[host] = lane;
                    }
                    var ch = new TgH2Channel(lane, ++nextChannel, label);
                    TgBalancer.SetFor(dc, bd);
                    TgLog.I("[" + label + "] ROUTE DC" + dc + " media -> H2 lane=" + lane.Id + " channel=" + ch.Id + " host=" + host);
                    return ch;
                }
                finally { openLock.Release(); }
            }
            TgLog.D("[" + label + "] H2 DC" + dc + " unavailable; using WS");
            return null;
        }

        public void Close() { closed = true; List<TgH2Lane> l; lock (lanes) { l = lanes.Values.ToList(); lanes.Clear(); } foreach (var x in l) x.Close(); }
    }

    public static class TgH2
    {
        public const int MaxPacket = 4 * 1024 * 1024, MaxPlainPadding = 256, MaxChannelRequests = 8, MaxChannelReplays = 2, ReplayHistoryPackets = 16, ReplayMaxAttempts = 3;
        public const long MaxChannelBytes = 8L << 20, ReplayHistoryBytes = 64 * 1024;
        public const double DomainCooldown = 30, RequestTimeout = 65, SetupTimeout = 8, ReplayIdleSeconds = 1, ReplayRequestSeconds = 3, ReplayCheckSeconds = 0.25,
                            ReplayMaxAge = 30, ReplaySlotSeconds = 30, ReplayRetrySeconds = 2;
    }

    // мост: нативный MTProto клиента ⇄ HTTP/2 запросы (без обфускации к Telegram — её снимаем/накладываем сами)
    static class TgH2Bridge
    {
        static async Task<byte[]> Plain(ITgStream s, AesCtr dec, int n)
        {
            var b = new byte[n]; int got = 0;
            while (got < n) { int m = await s.ReadAsync(b, got, n - got).ConfigureAwait(false); if (m <= 0) throw new EndOfStreamException(); got += m; }
            dec.Transform(b, 0, n); return b;
        }

        public static async Task<byte[]> ReadPacket(ITgStream s, AesCtr dec, uint tag)
        {
            int length;
            if (tag == TgConst.ProtoAbridged)
            {
                int first = (await Plain(s, dec, 1).ConfigureAwait(false))[0], words = first & 0x7f;
                if (words == 0x7f) { var w = await Plain(s, dec, 3).ConfigureAwait(false); words = w[0] | w[1] << 8 | w[2] << 16; }
                length = words * 4;
            }
            else { var v = await Plain(s, dec, 4).ConfigureAwait(false); length = (int)(BitConverter.ToUInt32(v, 0) & 0x7fffffff); }
            int max = TgH2.MaxPacket + (tag == TgConst.ProtoPadded ? TgH2.MaxPlainPadding : 0);
            if (length < 24 || length > max) throw new InvalidDataException("native packet length outside supported range: " + length);
            var body = await Plain(s, dec, length).ConfigureAwait(false);
            if (tag == TgConst.ProtoPadded)
            {
                int pl, maxPad;
                if (body.Take(8).All(x => x == 0)) { pl = 20 + BitConverter.ToInt32(body, 16); maxPad = TgH2.MaxPlainPadding; }
                else { pl = 24 + ((length - 24) / 16) * 16; maxPad = 15; }
                int pad = length - pl;
                if (pl < 24 || pl > length || pad > maxPad) throw new InvalidDataException("invalid padded native packet: frame=" + length + " packet=" + pl);
                Array.Resize(ref body, pl);
            }
            if (body.Length % 4 != 0 || body.Length > TgH2.MaxPacket) throw new InvalidDataException("invalid native packet alignment or size");
            return body;
        }

        public static byte[] Encode(byte[] body, uint tag)
        {
            if (body.Length == 0 || body.Length % 4 != 0 || body.Length > TgH2.MaxPacket) throw new InvalidDataException("invalid HTTP MTProto response length: " + body.Length);
            var o = new List<byte>(body.Length + 8);
            if (tag == TgConst.ProtoAbridged)
            {
                int w = body.Length / 4;
                if (w < 0x7f) o.Add((byte)w); else { o.Add(0x7f); o.Add((byte)w); o.Add((byte)(w >> 8)); o.Add((byte)(w >> 16)); }
                o.AddRange(body);
            }
            else
            {
                var pad = tag == TgConst.ProtoPadded ? TgConst.Rand(TgConst.RandInt(4)) : new byte[0];
                o.AddRange(BitConverter.GetBytes(body.Length + pad.Length)); o.AddRange(body); o.AddRange(pad);
            }
            return o.ToArray();
        }

        public static async Task Run(ITgStream cs, TgH2Channel ch, TgCrypto ctx, uint tag)
        {
            Func<byte[], Task> writeNative = async data => { var d = (byte[])data.Clone(); ctx.CltEnc.Transform(d, 0, d.Length); await cs.WriteAsync(d, 0, d.Length).ConfigureAwait(false); };
            var upload = Task.Run(async () => { while (true) { var body = await ReadPacket(cs, ctx.CltDec, tag).ConfigureAwait(false); await ch.Send(body).ConfigureAwait(false); } });
            var download = Task.Run(async () =>
            {
                while (true)
                {
                    var body = await ch.Receive().ConfigureAwait(false);
                    if (ch.Closed) throw new IOException("H2 channel closed before native response");
                    ch.DeliveringSince = DateTime.UtcNow;
                    try { await writeNative(Encode(body, tag)).ConfigureAwait(false); } finally { ch.DeliveringSince = null; }
                    ch.Down += body.Length; Interlocked.Add(ref TgStats.Down, body.Length); ch.LastProgress = DateTime.UtcNow;
                }
            });
            var recover = ch.Recover();
            var done = await Task.WhenAny(upload, download).ConfigureAwait(false);
            try
            {
                if (ch.TransportError != null) { await writeNative(Encode(BitConverter.GetBytes(ch.TransportError.Value), tag)).ConfigureAwait(false); Interlocked.Add(ref TgStats.Down, 4); }
                else await done.ConfigureAwait(false);
            }
            catch (EndOfStreamException) { TgLog.D("[" + ch.Label + "] H2 channel=" + ch.Id + " native EOF"); }
            catch (Exception ex) { if (!ch.Closed) TgLog.Wn("[" + ch.Label + "] H2 channel=" + ch.Id + " bridge ended: " + TgNetUtil.Err(ex)); }
            finally
            {
                await ch.Close().ConfigureAwait(false);
                cs.Close();
                try { await Task.WhenAll(upload, download, recover).ConfigureAwait(false); } catch { }
            }
        }
    }
}
