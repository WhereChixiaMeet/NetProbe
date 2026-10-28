using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetProbe
{
    // --selftest: проверки TG Proxy. Офлайн — шифрование, рукопожатие, Fake TLS, разбиение пакетов, кадры HTTP-моста.
    // Онлайн — настоящий запрос req_pq_multi к Telegram через прокси каждым путём (WS / TCP / CF WS / CF HTTP/2):
    // ответ resPQ с нашим nonce = путь реально работает. Это только серверы Telegram и CF-домены прокси.
    static class TgSelfTest
    {
        static int fails;
        static void Ok(StringBuilder sb, bool ok, string name, string detail = "") { if (!ok) fails++; sb.AppendLine("TG " + (ok ? "ok   " : "FAIL ") + name + (detail != "" ? "  " + detail : "")); }
        static byte[] Sha(byte[] a, byte[] b) { using (var h = SHA256.Create()) return h.ComputeHash(a.Concat(b).ToArray()); }

        public static void Run(StringBuilder sb)
        {
            fails = 0;
            sb.AppendLine("--- TG Proxy ---");
            try { Offline(sb); } catch (Exception ex) { Ok(sb, false, "offline exception", ex.ToString()); }
            int offlineFails = fails;
            try { Online(sb); } catch (Exception ex) { sb.AppendLine("TG online exception: " + ex); }
            sb.AppendLine(offlineFails == 0 ? "TG OFFLINE OK" : "TG OFFLINE FAILED: " + offlineFails);
        }

        // клиентская сторона obfuscated2 (как Telegram Desktop): 64 байта рукопожатия + шифраторы
        sealed class Client { public byte[] Hs; public AesCtr Enc, Dec; }
        static Client MakeClient(byte[] secret, uint proto, short dcIdx)
        {
            var rnd = TgConst.Rand(64); rnd[0] = 0x55;
            var key = Sha(rnd.Skip(8).Take(32).ToArray(), secret);
            var enc = new AesCtr(key, 0, rnd, 40);
            var encd = enc.Update(rnd);
            var hs = (byte[])rnd.Clone();
            var tail = BitConverter.GetBytes(proto).Concat(BitConverter.GetBytes(dcIdx)).Concat(new byte[] { 1, 2 }).ToArray();
            for (int i = 0; i < 8; i++) hs[56 + i] = (byte)(tail[i] ^ encd[56 + i] ^ rnd[56 + i]);
            var rev = hs.Skip(8).Take(48).Reverse().ToArray();
            var dec = new AesCtr(Sha(rev.Take(32).ToArray(), secret), 0, rev, 32);
            return new Client { Hs = hs, Enc = enc, Dec = dec };
        }

        static void Offline(StringBuilder sb)
        {
            // AES-256-CTR: NIST SP 800-38A F.5.5
            var k = TgConst.Hex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
            var iv = TgConst.Hex("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            var c = new AesCtr(k, 0, iv, 0);
            var ct = c.Update(TgConst.Hex("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51"));
            Ok(sb, TgConst.ToHex(ct) == "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5", "aes-ctr NIST vector");
            // потоково по кускам = целиком
            var c1 = new AesCtr(k, 0, iv, 0); var c2 = new AesCtr(k, 0, iv, 0); var data = TgConst.Rand(5000);
            var whole = c1.Update(data); var parts = new List<byte>(); int o = 0; foreach (var n in new[] { 1, 15, 16, 17, 1000, 3951 }) { parts.AddRange(c2.Update(data.Skip(o).Take(n).ToArray())); o += n; }
            Ok(sb, whole.SequenceEqual(parts), "aes-ctr streaming chunks");

            // рукопожатие: DC, медиа, протокол; шифрование клиент → прокси → «Telegram» и обратно
            var secret = TgConst.Rand(16);
            var cl = MakeClient(secret, TgConst.ProtoIntermediate, -4);
            var r = TgHandshake.Try(cl.Hs, secret);
            Ok(sb, r != null && r.Value.Dc == 4 && r.Value.Media && r.Value.Proto == TgConst.ProtoIntermediate, "handshake parse", r == null ? "null" : "dc=" + r.Value.Dc + " media=" + r.Value.Media);
            Ok(sb, TgHandshake.Try(cl.Hs, TgConst.Rand(16)) == null, "handshake wrong secret rejected");
            var relay = TgHandshake.RelayInit(TgConst.ProtoIntermediate, -4);
            var ctx = TgHandshake.Build(r.Value.PrekeyIv, secret, relay);
            // Telegram-сторона: обычный obfuscated2 без секрета
            var tgDec = new AesCtr(relay, 8, relay, 40); var relayPlain = tgDec.Update(relay);
            Ok(sb, BitConverter.ToUInt32(relayPlain, 56) == TgConst.ProtoIntermediate && BitConverter.ToInt16(relayPlain, 60) == -4, "relay init tag/dc");
            var msg = Encoding.ASCII.GetBytes("hello telegram 0123456789");
            var wire = cl.Enc.Update(msg); ctx.CltDec.Transform(wire, 0, wire.Length); ctx.TgEnc.Transform(wire, 0, wire.Length);
            Ok(sb, tgDec.Update(wire).SequenceEqual(msg), "client -> telegram re-encryption");
            var rrev = relay.Skip(8).Take(48).Reverse().ToArray(); var tgEnc = new AesCtr(rrev, 0, rrev, 32);
            var back = Encoding.ASCII.GetBytes("reply from dc");
            var w2 = tgEnc.Update(back); ctx.TgDec.Transform(w2, 0, w2.Length); ctx.CltEnc.Transform(w2, 0, w2.Length);
            Ok(sb, cl.Dec.Update(w2).SequenceEqual(back), "telegram -> client re-encryption");
            // зарезервированные начала не выдаются
            bool okInit = true; for (int i = 0; i < 300; i++) { var x = TgHandshake.RelayInit(TgConst.ProtoAbridged, 2); if (x[0] == 0xEF || BitConverter.ToUInt32(x, 4) == 0) okInit = false; }
            Ok(sb, okInit, "relay init avoids reserved bytes");

            // Fake TLS: ClientHello с правильным HMAC проходит, с чужим секретом — нет; ServerHello подписан
            var hello = new byte[200]; hello[0] = 0x16; hello[1] = 3; hello[2] = 1; hello[3] = 0; hello[4] = 195; hello[5] = 1; hello[43] = 0x20;
            Buffer.BlockCopy(TgConst.Rand(32), 0, hello, 44, 32);
            byte[] mac; using (var h = new HMACSHA256(secret)) mac = h.ComputeHash(hello);
            uint now = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds; var tsb = BitConverter.GetBytes(now);
            for (int i = 0; i < 28; i++) hello[11 + i] = mac[i]; for (int i = 0; i < 4; i++) hello[39 + i] = (byte)(mac[28 + i] ^ tsb[i]);
            byte[] crand, sid; uint ts;
            Ok(sb, TgFakeTls.Verify(hello, secret, out crand, out sid, out ts) && ts == now && sid.SequenceEqual(hello.Skip(44).Take(32)), "fake tls verify");
            Ok(sb, !TgFakeTls.Verify(hello, TgConst.Rand(16), out crand, out sid, out ts), "fake tls wrong secret");
            TgFakeTls.Verify(hello, secret, out crand, out sid, out ts);
            var sh = TgFakeTls.ServerHello(secret, crand, sid);
            var zero = (byte[])sh.Clone(); for (int i = 0; i < 32; i++) zero[11 + i] = 0;
            byte[] smac; using (var h = new HMACSHA256(secret)) smac = h.ComputeHash(crand.Concat(zero).ToArray());
            Ok(sb, sh.Skip(11).Take(32).SequenceEqual(smac) && sh[0] == 0x16 && sh.Length > 2000, "fake tls server hello");

            // разбиение пакетов по длине (abridged / intermediate)
            var sp = new TgSplitter(TgConst.ProtoAbridged);
            var pa = new List<byte> { 2 }; pa.AddRange(new byte[8]); pa.Add(1); pa.AddRange(new byte[4]); var pl = pa.ToArray();
            var outp = sp.Split(pl.Take(5).ToArray(), pl.Take(5).ToArray()); var outp2 = sp.Split(pl.Skip(5).ToArray(), pl.Skip(5).ToArray());
            Ok(sb, outp.Count == 0 && outp2.Count == 2 && outp2[0].Length == 9 && outp2[1].Length == 5, "splitter abridged");
            var si = new TgSplitter(TgConst.ProtoIntermediate);
            var pi = BitConverter.GetBytes(8).Concat(new byte[8]).Concat(BitConverter.GetBytes(4)).Concat(new byte[4]).ToArray();
            var oi = si.Split(pi, pi);
            Ok(sb, oi.Count == 2 && oi[0].Length == 12 && oi[1].Length == 8, "splitter intermediate");

            // кадры HTTP/2-моста: Encode → ReadPacket возвращает тот же пакет
            foreach (var tag in new[] { TgConst.ProtoAbridged, TgConst.ProtoIntermediate, TgConst.ProtoPadded })
            {
                var body = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }.Concat(TgConst.Rand(8)).Concat(BitConverter.GetBytes(20)).Concat(TgConst.Rand(20)).ToArray();
                var frame = TgH2Bridge.Encode(body, tag);
                var kk = TgConst.Rand(32); var ivv = TgConst.Rand(16);
                var e1 = new AesCtr(kk, 0, ivv, 0); var d1 = new AesCtr(kk, 0, ivv, 0);
                var ms = new MemStream(e1.Update(frame));
                var got = TgH2Bridge.ReadPacket(ms, d1, tag).Result;
                Ok(sb, got.SequenceEqual(body), "h2 frame roundtrip 0x" + tag.ToString("X8"));
            }

            // домены CF-прокси расшифровываются как в оригинале
            var dd = TgCfDomains.Defaults();
            Ok(sb, dd.Count == 20 && dd[0] == "pclead.co.uk" && dd[19] == "nothingthere.co.uk", "cf domain decode", dd.Count > 0 ? dd[0] : "");
            // ссылка и правила DC
            var cfg = new TgConfig { Secret = "00112233445566778899aabbccddeeff" };
            Ok(sb, TgProxy.Link(cfg) == "tg://proxy?server=127.0.0.1&port=1443&secret=dd00112233445566778899aabbccddeeff", "link dd");
            cfg.FakeTlsDomain = "ya.ru";
            Ok(sb, TgProxy.Link(cfg).EndsWith("secret=ee00112233445566778899aabbccddeeff79612e7275"), "link ee");
            var rules = TgConst.ParseDcIp(TgConst.DefaultDcIp);
            Ok(sb, rules.Count == 2 && rules[2] == "149.154.167.220", "dc rules parse");
            bool threw = false; try { TgConst.ParseDcIp("2:999.1.1.1"); } catch (FormatException) { threw = true; }
            Ok(sb, threw, "dc rules reject bad ip");
            Ok(sb, TgConst.Censor("kws2.pclead.co.uk via kws2.web.telegram.org") == "kw**.pcl***.c*.uk via kws2.web.telegram.org", "log domain censor", TgConst.Censor("kws2.pclead.co.uk via kws2.web.telegram.org"));
        }

        sealed class MemStream : ITgStream
        {
            readonly byte[] d; int p; public MemStream(byte[] d) { this.d = d; }
            public Task<int> ReadAsync(byte[] b, int off, int count) { int n = Math.Min(count, d.Length - p); Buffer.BlockCopy(d, p, b, off, n); p += n; return Task.FromResult(n); }
            public Task WriteAsync(byte[] b, int off, int count) { return Task.FromResult(0); }
            public void Close() { }
        }

        // ---- онлайн: req_pq_multi через прокси, ответ resPQ ----
        static void Online(StringBuilder sb)
        {
            int port = 18843;
            var cases = new List<Tuple<string, Action<TgConfig>, short>>
            {
                Tuple.Create<string, Action<TgConfig>, short>("ws (DC2 via 149.154.167.220)", c => { c.FallbackCf = false; }, (short)2),
                Tuple.Create<string, Action<TgConfig>, short>("ws media (DC4)", c => { c.FallbackCf = false; }, (short)-4),
                Tuple.Create<string, Action<TgConfig>, short>("tcp fallback (DC2)", c => { c.PoolSize = 0; c.FallbackCf = false; }, (short)2),
                Tuple.Create<string, Action<TgConfig>, short>("cf ws (DC1)", c => { c.DcRedirects.Clear(); c.H2Media = false; }, (short)1),
                Tuple.Create<string, Action<TgConfig>, short>("cf h2 media (DC2)", c => { c.DcRedirects.Clear(); c.PoolSize = 0; }, (short)-2),
            };
            foreach (var cs in cases)
            {
                var cfg = new TgConfig { Port = port, Secret = TgConst.NewSecret(), Verbose = false };
                cs.Item2(cfg);
                var log = new List<string>();
                Action<TgLv, string> h = (lv, m) => { lock (log) log.Add(lv + " " + m); };
                TgLog.Line += h;
                var sw = Stopwatch.StartNew(); string res;
                try
                {
                    if (!TgProxy.Start(cfg)) { res = "start failed: " + TgProxy.LastError; }
                    else
                    {
                        if (cfg.PoolSize > 0 && cfg.DcRedirects.Count > 0) { for (int i = 0; i < 40 && TgProxy.WsPool.Ready == 0; i++) Thread.Sleep(200); }
                        res = ReqPq(port, TgConst.Hex(cfg.Secret), cs.Item3);
                    }
                }
                catch (Exception ex) { res = TgNetUtil.Err(ex); }
                finally { TgProxy.Stop(); TgLog.Line -= h; }
                string route; lock (log) route = string.Join(" | ", log.Where(l => l.Contains("->") || l.Contains("fallback") || l.Contains("failed")).Take(4));
                sb.AppendLine("TG online " + (res == null ? "ok   " : "FAIL ") + cs.Item1 + "  " + sw.ElapsedMilliseconds + "ms" + (res != null ? "  " + res : "") + (route != "" ? "  [" + route + "]" : ""));
                Thread.Sleep(300);
            }
        }

        static string ReqPq(int port, byte[] secret, short dcIdx)
        {
            var cl = MakeClient(secret, TgConst.ProtoIntermediate, dcIdx);
            using (var tcp = new TcpClient())
            {
                tcp.Connect("127.0.0.1", port); tcp.ReceiveTimeout = 20000; tcp.SendTimeout = 10000;
                var s = tcp.GetStream();
                s.Write(cl.Hs, 0, 64);
                var nonce = TgConst.Rand(16);
                ulong unix = (ulong)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
                ulong msgId = (unix << 32) | ((ulong)BitConverter.ToUInt32(TgConst.Rand(4), 0) & 0xFFFFFFFC);
                var payload = new byte[8].Concat(BitConverter.GetBytes(msgId)).Concat(BitConverter.GetBytes(20)).Concat(BitConverter.GetBytes(0xbe7e8ef1)).Concat(nonce).ToArray();
                var frame = BitConverter.GetBytes(payload.Length).Concat(payload).ToArray();
                var w = cl.Enc.Update(frame); s.Write(w, 0, w.Length);
                Func<int, byte[]> read = n => { var b = new byte[n]; int g = 0; while (g < n) { int m = s.Read(b, g, n - g); if (m <= 0) throw new EndOfStreamException("closed after " + g + " bytes"); g += m; } return cl.Dec.Update(b); };
                int len = (int)(BitConverter.ToUInt32(read(4), 0) & 0x7fffffff);
                if (len < 40 || len > 4096) return len == 4 ? "transport error " + BitConverter.ToInt32(read(4), 0) : "bad length " + len;
                var resp = read(len);
                uint ctor = BitConverter.ToUInt32(resp, 20);
                if (ctor != 0x05162463) return "unexpected ctor 0x" + ctor.ToString("x8");
                if (!resp.Skip(24).Take(16).SequenceEqual(nonce)) return "nonce mismatch";
                return null;
            }
        }
    }
}
