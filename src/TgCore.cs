using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace NetProbe
{
    // TG Proxy — локальный MTProto-прокси для Telegram Desktop: трафик к Telegram идёт WebSocket-соединениями
    // (kwsN.web.telegram.org), при недоступности — через Cloudflare Worker / CF-прокси (WS или HTTP/2) / прямой TCP.
    // Перенос tg-ws-proxy 1.11.2 (Flowseal, MIT License, https://github.com/Flowseal/tg-ws-proxy) на C#.
    public static class TgConst
    {
        public const int HandshakeLen = 64, SkipLen = 8, PrekeyLen = 32, KeyLen = 32, IvLen = 16, ProtoTagPos = 56, DcIdxPos = 60;
        public const uint ProtoAbridged = 0xEFEFEFEF, ProtoIntermediate = 0xEEEEEEEE, ProtoPadded = 0xDDDDDDDD;
        public static readonly Dictionary<int, string> DcDefaultIps = new Dictionary<int, string>
        { { 1, "149.154.175.50" }, { 2, "149.154.167.51" }, { 3, "149.154.175.100" }, { 4, "149.154.167.91" }, { 5, "149.154.171.5" }, { 203, "91.105.192.100" } };
        public static readonly Dictionary<int, string> DcTestIps = new Dictionary<int, string>
        { { 1, "149.154.175.10" }, { 2, "149.154.167.40" }, { 3, "149.154.175.117" } };
        public const string WsPath = "/apiws", WsPathTest = "/apiws_test";
        public const string DefaultDcIp = "2:149.154.167.220, 4:149.154.167.220";

        public static string[] WsDomains(int dc, bool media)
        {
            if (dc == 203) dc = 2;
            return media ? new[] { "kws" + dc + "-1.web.telegram.org", "kws" + dc + ".web.telegram.org" } : new[] { "kws" + dc + ".web.telegram.org" };
        }

        public static string Human(long n)
        {
            double v = n; foreach (var u in new[] { "B", "KB", "MB", "GB" }) { if (Math.Abs(v) < 1024) return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + u; v /= 1024; }
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "TB";
        }

        public static byte[] Hex(string s)
        {
            s = (s ?? "").Trim(); if (s.Length % 2 != 0) throw new FormatException("hex");
            var b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return b;
        }
        public static string ToHex(byte[] b, int off = 0, int len = -1)
        {
            if (len < 0) len = b.Length - off;
            var sb = new StringBuilder(len * 2); for (int i = 0; i < len; i++) sb.Append(b[off + i].ToString("x2")); return sb.ToString();
        }
        static readonly RandomNumberGenerator rng = RandomNumberGenerator.Create();
        public static byte[] Rand(int n) { var b = new byte[n]; lock (rng) rng.GetBytes(b); return b; }
        public static int RandInt(int maxExclusive) { var b = Rand(4); return (int)(BitConverter.ToUInt32(b, 0) % (uint)Math.Max(1, maxExclusive)); }
        public static void Shuffle<T>(IList<T> l) { for (int i = l.Count - 1; i > 0; i--) { int j = RandInt(i + 1); var t = l[i]; l[i] = l[j]; l[j] = t; } }
        public static string NewSecret() { return ToHex(Rand(16)); }

        public static bool ValidSecret(string s)
        {
            s = (s ?? "").Trim(); if (s.Length != 32) return false;
            foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        // «2:149.154.167.220, 4:…» → словарь; ошибка — исключение с текстом записи
        public static Dictionary<int, string> ParseDcIp(string text)
        {
            var d = new Dictionary<int, string>();
            foreach (var raw in (text ?? "").Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int i = raw.IndexOf(':');
                if (i <= 0) throw new FormatException(raw);
                int dc; IPAddress ip;
                if (!int.TryParse(raw.Substring(0, i), out dc) || !IPAddress.TryParse(raw.Substring(i + 1), out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    throw new FormatException(raw);
                d[dc] = raw.Substring(i + 1);
            }
            return d;
        }

        public static List<string> DomainList(string s)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var l = new List<string>();
            foreach (var x in (s ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (seen.Add(x.Trim())) l.Add(x.Trim());
            return l;
        }

        // подписи доменов в логе: кроме *.telegram.org — половина каждой части звёздочками (как DomainCensorFilter)
        static readonly Regex domRx = new Regex(@"(?<![\w-])(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,}(?![\w-])", RegexOptions.Compiled);
        public static string Censor(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return domRx.Replace(s, m =>
            {
                var d = m.Value; var n = d.ToLowerInvariant().TrimEnd('.');
                if (n == "telegram.org" || n.EndsWith(".telegram.org") || n.EndsWith(".log")) return d;
                var parts = d.Split('.'); if (parts.Length < 2) return d;
                return string.Join(".", parts.Select((p, i) => i == parts.Length - 1 ? p : p.Substring(0, p.Length / 2) + new string('*', p.Length - p.Length / 2)));
            });
        }
    }

    // AES-256-CTR (счётчик 128 бит big-endian), потоковый: Transform шифрует/расшифровывает «на месте»
    public sealed class AesCtr : IDisposable
    {
        readonly ICryptoTransform ecb;
        readonly byte[] ctr = new byte[16];
        byte[] ks = new byte[16 * 64]; int ksPos, ksLen;
        public AesCtr(byte[] key, int keyOff, byte[] iv, int ivOff)
        {
            var aes = Aes.Create(); aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
            var k = new byte[32]; Buffer.BlockCopy(key, keyOff, k, 0, 32);
            aes.Key = k; ecb = aes.CreateEncryptor();
            Buffer.BlockCopy(iv, ivOff, ctr, 0, 16);
        }
        void Refill(int blocks)
        {
            if (ks.Length < blocks * 16) ks = new byte[blocks * 16];
            var input = new byte[blocks * 16];
            for (int b = 0; b < blocks; b++)
            {
                Buffer.BlockCopy(ctr, 0, input, b * 16, 16);
                for (int i = 15; i >= 0; i--) { if (++ctr[i] != 0) break; }
            }
            ecb.TransformBlock(input, 0, input.Length, ks, 0);
            ksPos = 0; ksLen = blocks * 16;
        }
        public void Transform(byte[] data, int off, int len)
        {
            int i = 0;
            while (i < len)
            {
                if (ksPos >= ksLen) Refill(Math.Max(1, Math.Min(1024, (len - i + 15) / 16)));
                int n = Math.Min(len - i, ksLen - ksPos);
                for (int j = 0; j < n; j++) data[off + i + j] ^= ks[ksPos + j];
                ksPos += n; i += n;
            }
        }
        public byte[] Update(byte[] data) { var r = (byte[])data.Clone(); Transform(r, 0, r.Length); return r; }
        public void Skip(int n) { Transform(new byte[n], 0, n); }
        public void Dispose() { ecb.Dispose(); }
    }

    public sealed class TgCrypto
    {
        public AesCtr CltDec, CltEnc, TgEnc, TgDec;
    }

    public static class TgHandshake
    {
        public struct Result { public int Dc; public bool Media; public uint Proto; public byte[] PrekeyIv; }

        static byte[] Sha256(byte[] a, byte[] b) { using (var h = SHA256.Create()) { var x = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, x, 0, a.Length); Buffer.BlockCopy(b, 0, x, a.Length, b.Length); return h.ComputeHash(x); } }

        public static Result? Try(byte[] hs, byte[] secret)
        {
            var prekeyIv = new byte[TgConst.PrekeyLen + TgConst.IvLen];
            Buffer.BlockCopy(hs, TgConst.SkipLen, prekeyIv, 0, prekeyIv.Length);
            var prekey = prekeyIv.Take(TgConst.PrekeyLen).ToArray();
            var key = Sha256(prekey, secret);
            var dec = new AesCtr(key, 0, prekeyIv, TgConst.PrekeyLen);
            var d = dec.Update(hs); dec.Dispose();
            uint tag = BitConverter.ToUInt32(d, TgConst.ProtoTagPos);
            if (tag != TgConst.ProtoAbridged && tag != TgConst.ProtoIntermediate && tag != TgConst.ProtoPadded) return null;
            short dcIdx = BitConverter.ToInt16(d, TgConst.DcIdxPos);
            return new Result { Dc = Math.Abs((int)dcIdx), Media = dcIdx < 0, Proto = tag, PrekeyIv = prekeyIv };
        }

        static readonly uint[] reservedStarts = { 0x44414548, 0x54534F50, 0x20544547, 0xEEEEEEEE, 0xDDDDDDDD, 0x02010316, 0x4954504F };
        public static byte[] RelayInit(uint proto, int dcIdx)
        {
            byte[] rnd;
            while (true)
            {
                rnd = TgConst.Rand(TgConst.HandshakeLen);
                if (rnd[0] == 0xEF) continue;
                if (reservedStarts.Contains(BitConverter.ToUInt32(rnd, 0))) continue;
                if (BitConverter.ToUInt32(rnd, 4) == 0) continue;
                break;
            }
            var enc = new AesCtr(rnd, TgConst.SkipLen, rnd, TgConst.SkipLen + TgConst.PrekeyLen);
            var full = enc.Update(rnd); enc.Dispose();
            var tail = new byte[8];
            BitConverter.GetBytes(proto).CopyTo(tail, 0);
            BitConverter.GetBytes((short)dcIdx).CopyTo(tail, 4);
            var r2 = TgConst.Rand(2); tail[6] = r2[0]; tail[7] = r2[1];
            var result = (byte[])rnd.Clone();
            for (int i = 0; i < 8; i++) result[56 + i] = (byte)(tail[i] ^ (full[56 + i] ^ rnd[56 + i]));
            return result;
        }

        public static TgCrypto Build(byte[] cltPrekeyIv, byte[] secret, byte[] relayInit)
        {
            var c = new TgCrypto();
            var decKey = Sha256(cltPrekeyIv.Take(TgConst.PrekeyLen).ToArray(), secret);
            c.CltDec = new AesCtr(decKey, 0, cltPrekeyIv, TgConst.PrekeyLen);
            var rev = cltPrekeyIv.Reverse().ToArray();
            var encKey = Sha256(rev.Take(TgConst.PrekeyLen).ToArray(), secret);
            c.CltEnc = new AesCtr(encKey, 0, rev, TgConst.PrekeyLen);
            c.CltDec.Skip(64);
            var relayKeyIv = new byte[TgConst.PrekeyLen + TgConst.IvLen];
            Buffer.BlockCopy(relayInit, TgConst.SkipLen, relayKeyIv, 0, relayKeyIv.Length);
            c.TgEnc = new AesCtr(relayKeyIv, 0, relayKeyIv, TgConst.PrekeyLen);
            var relayRev = relayKeyIv.Reverse().ToArray();
            c.TgDec = new AesCtr(relayRev, 0, relayRev, TgConst.KeyLen);
            c.TgEnc.Skip(64);
            return c;
        }
    }

    // Делит поток MTProto-пакетов (по расшифрованной длине) так, чтобы каждый пакет шёл отдельным WS-кадром
    public sealed class TgSplitter
    {
        readonly uint proto; readonly List<byte> cipher = new List<byte>(), plain = new List<byte>(); bool disabled;
        public TgSplitter(uint proto) { this.proto = proto; }
        public List<byte[]> Split(byte[] chunk, byte[] plainChunk)
        {
            var parts = new List<byte[]>();
            if (chunk.Length == 0) return parts;
            if (disabled) { parts.Add(chunk); return parts; }
            cipher.AddRange(chunk); plain.AddRange(plainChunk);
            int offset = 0, len = cipher.Count;
            while (offset < len)
            {
                int? pl = Next(offset, len - offset);
                if (pl == null) break;
                if (pl <= 0) { parts.Add(cipher.GetRange(offset, len - offset).ToArray()); offset = len; disabled = true; break; }
                parts.Add(cipher.GetRange(offset, pl.Value).ToArray()); offset += pl.Value;
            }
            if (offset > 0) { cipher.RemoveRange(0, offset); plain.RemoveRange(0, offset); }
            return parts;
        }
        public byte[] Flush() { if (cipher.Count == 0) return null; var t = cipher.ToArray(); cipher.Clear(); plain.Clear(); return t; }
        int? Next(int off, int avail)
        {
            if (avail <= 0) return null;
            if (proto == TgConst.ProtoAbridged)
            {
                int first = plain[off], payload, hdr;
                if (first == 0x7F || first == 0xFF) { if (avail < 4) return null; payload = (plain[off + 1] | plain[off + 2] << 8 | plain[off + 3] << 16) * 4; hdr = 4; }
                else { payload = (first & 0x7F) * 4; hdr = 1; }
                if (payload <= 0) return 0;
                return avail < hdr + payload ? (int?)null : hdr + payload;
            }
            if (proto == TgConst.ProtoIntermediate || proto == TgConst.ProtoPadded)
            {
                if (avail < 4) return null;
                int payload = (int)((uint)(plain[off] | plain[off + 1] << 8 | plain[off + 2] << 16 | plain[off + 3] << 24) & 0x7FFFFFFF);
                if (payload <= 0) return 0;
                return avail < 4 + payload ? (int?)null : 4 + payload;
            }
            return 0;
        }
    }

    public static class TgStats
    {
        public static long Total, Active, Ws, H2, H2Tcp, H2Req, H2Err, TcpFb, Cf, Fronting, Bad, Masked, WsErr, Up, Down, PoolHit, PoolMiss, CfPoolHit, CfPoolMiss;
        public static void Reset() { Total = Active = Ws = H2 = H2Tcp = H2Req = H2Err = TcpFb = Cf = Fronting = Bad = Masked = WsErr = Up = Down = PoolHit = PoolMiss = CfPoolHit = CfPoolMiss = 0; }
        static string Ratio(long h, long m) { return h + m > 0 ? h + "/" + (h + m) : "n/a"; }
        public static string Summary()
        {
            return "total=" + Total + " active=" + Interlocked.Read(ref Active) + " ws=" + Ws + " h2=" + H2 + " h2_tcp=" + H2Tcp + " h2_req=" + H2Req + " h2_err=" + H2Err +
                   " tcp_fb=" + TcpFb + " cf=" + Cf + " front=" + Fronting + " bad=" + Bad + " masked=" + Masked + " err=" + WsErr +
                   " pool=" + Ratio(PoolHit, PoolMiss) + " cf_pool=" + Ratio(CfPoolHit, CfPoolMiss) + " up=" + TgConst.Human(Up) + " down=" + TgConst.Human(Down);
        }
    }

    public sealed class TgConfig
    {
        public string Host = "127.0.0.1"; public int Port = 1443; public string Secret = "";
        public Dictionary<int, string> DcRedirects = new Dictionary<int, string> { { 2, "149.154.167.220" }, { 4, "149.154.167.220" } };
        public int BufferSize = 256 * 1024, PoolSize = 4;
        public bool FallbackCf = true, H2Media = true, DisableSecure, ProxyProtocol, ForceTestDc, Verbose;
        public List<string> CfUserDomains = new List<string>(), CfWorkerDomains = new List<string>();
        public string FakeTlsDomain = "";
        public bool H2Enabled { get { return H2Media && FallbackCf && !DisableSecure && !ForceTestDc; } }
    }

    public enum TgLv { Debug, Info, Warn, Error }
    public static class TgLog
    {
        public static event Action<TgLv, string> Line;
        public static volatile bool Verbose;
        public static bool DebugOn { get { return Verbose; } }
        public static void W(TgLv lv, string msg) { if (lv == TgLv.Debug && !Verbose) return; var h = Line; if (h != null) try { h(lv, TgConst.Censor(msg)); } catch { } }
        public static void D(string m) { W(TgLv.Debug, m); }
        public static void I(string m) { W(TgLv.Info, m); }
        public static void Wn(string m) { W(TgLv.Warn, m); }
        public static void E(string m) { W(TgLv.Error, m); }
    }

    // Балансировщик доменов CF-прокси: у каждого DC — текущий домен, остальные — в случайном порядке
    public static class TgBalancer
    {
        static readonly object gate = new object();
        static List<string> domains = new List<string>();
        static readonly Dictionary<int, string> dcDomain = new Dictionary<int, string>();
        public static List<string> Domains { get { lock (gate) return domains.ToList(); } }
        public static void Update(List<string> list)
        {
            lock (gate)
            {
                if (domains.OrderBy(x => x).SequenceEqual(list.OrderBy(x => x))) return;
                domains = list.ToList(); dcDomain.Clear();
                if (domains.Count > 0) foreach (var dc in new[] { 1, 2, 3, 4, 5, 203 }) dcDomain[dc] = domains[TgConst.RandInt(domains.Count)];
            }
        }
        public static bool SetFor(int dc, string d) { lock (gate) { string cur; if (dcDomain.TryGetValue(dc, out cur) && cur == d) return false; dcDomain[dc] = d; return true; } }
        public static List<string> For(int dc)
        {
            lock (gate)
            {
                var r = new List<string>(); string cur;
                if (dcDomain.TryGetValue(dc, out cur)) r.Add(cur);
                var sh = domains.ToList(); TgConst.Shuffle(sh);
                foreach (var d in sh) if (d != cur) r.Add(d);
                return r;
            }
        }
    }

    // Домены CF-прокси проекта: зашифрованный список (сдвиг букв) + обновление с GitHub раз в час
    public static class TgCfDomains
    {
        public const string Url = "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";
        static readonly string[] enc = { "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com", "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
            "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com", "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com" };
        public static string Decode(string s)
        {
            if (!s.EndsWith(".com")) return s;
            var p = s.Substring(0, s.Length - 4); int n = p.Count(char.IsLetter);
            var sb = new StringBuilder();
            foreach (char c in p)
            {
                if (char.IsLetter(c)) { int b = c > '`' ? 97 : 65; sb.Append((char)(((c - b - n) % 26 + 26) % 26 + b)); }
                else sb.Append(c);
            }
            return sb + ".co.uk";
        }
        public static List<string> Defaults() { return enc.Select(Decode).ToList(); }

        static bool ValidDomain(string d)
        {
            if (string.IsNullOrEmpty(d) || d.Length > 253 || d.StartsWith(".") || d.EndsWith(".")) return false;
            var labels = d.Split('.'); if (labels.Length < 2) return false;
            foreach (var l in labels) { if (l.Length == 0 || l.Length > 63 || l[0] == '-' || l[l.Length - 1] == '-' || !l.All(ch => char.IsLetterOrDigit(ch) || ch == '-')) return false; }
            var tld = labels[labels.Length - 1]; return tld.Length >= 2 && tld.Any(char.IsLetter);
        }

        static Timer timer; static int gen;
        public static void Start(TgConfig cfg)
        {
            Stop();
            if (cfg.CfUserDomains.Count > 0) { TgBalancer.Update(cfg.CfUserDomains); return; }
            TgBalancer.Update(Defaults());
            int my = Interlocked.Increment(ref gen);
            timer = new Timer(_ => { if (my == gen) Refresh(); }, null, 0, 3600 * 1000);
        }
        public static void Stop() { Interlocked.Increment(ref gen); var t = timer; timer = null; if (t != null) t.Dispose(); }

        static void Refresh()
        {
            try
            {
                string text = TgHttp.GetText(Url + "?" + new string(Enumerable.Range(0, 7).Select(_ => (char)('a' + TgConst.RandInt(26))).ToArray()));
                var fetched = text.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith("#")).Select(Decode).ToList();
                var pool = new List<string>(); var seen = new HashSet<string>();
                foreach (var d in fetched) { var x = d.Trim().ToLowerInvariant(); if (ValidDomain(x) && seen.Add(x)) pool.Add(x); }
                if (pool.Count >= 3) { TgBalancer.Update(pool); TgLog.I("CF proxy domain pool updated from GitHub (" + pool.Count + " domains)"); }
                else TgLog.Wn("Ignoring fetched CF proxy domains (total=" + fetched.Count + ", valid=" + pool.Count + "); keeping current pool");
            }
            catch (Exception ex) { TgLog.Wn("Failed to fetch CF proxy domain list: " + ex.Message); }
        }
    }
}
