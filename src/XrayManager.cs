using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

using static NetProbe.Tr;

namespace NetProbe
{
    public enum XState { Off, Connecting, Connected, Switching, AllDown }

    // Подключение через xray + автосвитч серверов.
    //  Каждые AsInterval с активный сервер проверяется запросом TestUrl через локальный socks.
    //  AsFails сбоев подряд (или AsSlowCount проверок медленнее AsMaxLatency) → переключение:
    //  кандидаты из пула (все / группа / избранные), без недавно упавших, по стратегии (самый быстрый /
    //  следующий / случайный из лучших), с паузой AsCooldown и лимитом AsMaxPerHour.
    //  Если ничего не работает — состояние AllDown (опционально включается DPI), повтор раз в минуту.
    //  Можно возвращаться на выбранный вручную сервер, когда он снова отвечает.
    public class XrayManager
    {
        public Func<Settings> GetSettings;
        public Func<string> GetCurl;
        public event Action Changed;
        public Action<bool> DpiFallback;   // true = включить DPI, false = выключить
        public Func<bool> NeedDpi;         // в списке «Серверы — через DPI» есть приложения → winws нужен, пока xray подключён
        public bool DpiWanted { get { return dpiOn || dpiApps; } }
        public long SessionDown, SessionUp; // трафик за текущее подключение, байт

        public volatile XState State = XState.Off;
        public string Detail = "";
        public int LastDelay = -1, Switches;
        public DateTime ConnectedAt, LastOk = DateTime.MinValue;
        readonly XrayProcess x = new XrayProcess();
        readonly SingBoxProcess sb = new SingBoxProcess();
        public Func<TunLists> GetTunLists;
        public Func<string> TunBlocked;                            // причина, по которой TUN нельзя включить (например, работает WireSock)
        DateTime lastTunTry = DateTime.MinValue;
        public bool TunRunning { get { return sb.Running; } }
        readonly Queue<Action> q = new Queue<Action>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Dictionary<string, DateTime> failedAt = new Dictionary<string, DateTime>();
        readonly List<DateTime> switchTimes = new List<DateTime>();
        DateTime lastCheck = DateTime.MinValue, lastSwitch = DateTime.MinValue, lastBgTest = DateTime.Now, lastReturn = DateTime.Now, lastAllDownTry = DateTime.MinValue;
        int fails, slows; bool dpiOn, dpiApps; volatile bool died;
        bool statsOn = true; string statsId; DateTime lastStats = DateTime.MinValue, lastStatsSave = DateTime.Now; bool statsDirty;
        string lastErr = "";

        public bool Active { get { return State != XState.Off; } }
        public bool Testing;

        public XrayManager()
        {
            x.Exited += c => { died = true; Logger.Write(LogLevel.Warn, "xray " + Tx("завершился (код ") + c + ")"); wake.Set(); };
            sb.Exited += c => Logger.Write(LogLevel.Warn, "sing-box (TUN) " + Tx("завершился (код ") + c + ")");
            sb.Output += s => { if (s.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("FATAL", StringComparison.OrdinalIgnoreCase) >= 0) Logger.Write(LogLevel.Warn, "[sing-box] " + s); };
            x.Output += s => { if (s.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) Logger.Write(LogLevel.Poll, "[xray] " + s); };
            new Thread(Loop) { IsBackground = true, Name = "xray" }.Start();
        }

        void Post(Action a) { lock (q) q.Enqueue(a); wake.Set(); }
        void Fire() { var h = Changed; if (h != null) try { h(); } catch { } }
        void Set(XState s, string d) { State = s; Detail = d; Fire(); }

        int pendingConnects;
        public bool ConnectPending { get { return Volatile.Read(ref pendingConnects) > 0; } }
        public void Connect(string id)
        {
            Interlocked.Increment(ref pendingConnects);
            Post(() => { try { DoConnect(id, true); } finally { Interlocked.Decrement(ref pendingConnects); } });
        }
        // Отключение не ждёт фоновых замеров автосвитча: они прерываются сразу (раньше кнопка VPN «не выключала»,
        // пока поток xray проверял до 60 кандидатов — Disconnect стоял в очереди за ними).
        readonly CancelFlag abort = new CancelFlag();
        public void Disconnect() { abort.Cancelled = true; Post(DoDisconnect); }
        public void Reapply() { Post(() => { if (Active) { var st = GetSettings(); StartOn(ServerStore.Find(st.ActiveServer)); } }); }
        public void ShutdownSync() { DoDisconnect(); }

        public void Adopt(bool wasActive)
        {
            Post(() =>
            {
                var st = GetSettings();
                var xp = SelfUpdate.FindChild("xray", st.XrayPath);
                if (!wasActive || xp == null) return;
                x.Attach(xp);
                var sp = SelfUpdate.FindChild("sing-box", st.SingBoxPath);
                if (sp != null) sb.Attach(sp);
                ConnectedAt = DateTime.Now; lastCheck = DateTime.Now;
                var cur = ServerStore.Find(st.ActiveServer);
                statsId = cur != null ? cur.Id : null; lastStats = DateTime.Now;
                dpiApps = NeedDpi != null && NeedDpi(); // winws уже подхвачен маршрутизатором
                if (st.SysProxy && !st.TunMode) SysProxy.Adopt("127.0.0.1:" + st.HttpPort);
                Set(XState.Connected, cur != null ? cur.Name : "");
            });
        }
        public void ReapplyTun() { Post(() => { if (Active) { sb.Stop(); EnsureTun(); } }); }
        public void ReapplyDpi() { Post(SyncDpi); }

        // DPI для приложений «через DPI» вкладки «Серверы»: winws работает, пока подключён xray
        void SyncDpi()
        {
            bool want = Active && NeedDpi != null && NeedDpi();
            if (want == dpiApps) return;
            bool before = DpiWanted;
            dpiApps = want;
            if (want) Logger.Write(LogLevel.Route, Tx("Серверы: DPI для приложений «через DPI»"));
            if (DpiWanted != before && DpiFallback != null) DpiFallback(DpiWanted);
        }

        // Статистика xray: трафик outbound «proxy» с прошлого запроса (-reset) — в счётчики текущего сервера
        void PollStats(bool save)
        {
            var st = GetSettings();
            if (statsOn && statsId != null && x.Running)
            {
                var r = Proc.Run(XrayProcess.Exe(st), "api statsquery --server=127.0.0.1:" + st.ApiPort + " -pattern \"outbound>>>proxy>>>traffic\" -reset", 5000);
                long down = 0, up = 0;
                foreach (Match m in Regex.Matches(r.Out ?? "", "traffic>>>(downlink|uplink)\"\\s*(?:,\\s*\"value\"\\s*:\\s*\"?(\\d+))?"))
                {
                    long v = 0; if (m.Groups[2].Success) long.TryParse(m.Groups[2].Value, out v);
                    if (m.Groups[1].Value == "downlink") down += v; else up += v;
                }
                var srv = ServerStore.Find(statsId);
                if (srv != null && (down > 0 || up > 0)) { srv.TotalDown += down; srv.TotalUp += up; SessionDown += down; SessionUp += up; statsDirty = true; }
            }
            lastStats = DateTime.Now;
            if (statsDirty && (save || (DateTime.Now - lastStatsSave).TotalSeconds >= 60)) { statsDirty = false; lastStatsSave = DateTime.Now; ServerStore.Save(); }
        }

        // TUN поднимается один раз и указывает на локальный socks xray — при смене сервера его не трогаем
        void EnsureTun()
        {
            var st = GetSettings();
            if (!st.TunMode) { if (sb.Running) { sb.Stop(); Logger.Write(LogLevel.Route, "TUN " + Tx("выключен")); } return; }
            if (sb.Running) return;
            string why = TunBlocked != null ? TunBlocked() : null;
            if (why != null) { Detail = why; return; }
            lastTunTry = DateTime.Now;
            var l = GetTunLists();
            string err = sb.Start(st, SingBoxConfig.Build(st, l));
            if (err != null) Logger.Write(LogLevel.Error, err);
            else Logger.Write(LogLevel.Route, "TUN (sing-box) " + Tx("запущен") + " · " + (l.Include.Count == 0 ? Tx("весь трафик через сервер") : Tx("приложений через сервер: ") + l.Include.Count) + (l.Exclude.Count > 0 ? " · " + Tx("исключений: ") + l.Exclude.Count : "") + (l.Domains.Count > 0 ? " · " + Tx("доменов через DPI: ") + l.Domains.Count : ""));
        }

        void Loop()
        {
            while (true)
            {
                wake.WaitOne(1000);
                while (true) { Action a; lock (q) { if (q.Count == 0) break; a = q.Dequeue(); } try { a(); } catch (Exception ex) { Logger.Write(LogLevel.Error, "xray: " + ex.Message); } }
                try { if (State != XState.Off) Tick(); } catch (Exception ex) { Logger.Write(LogLevel.Error, "xray: " + ex.Message); }
            }
        }

        // Чужие xray / v2rayN / sing-box (не из папки NetProbe): предупреждаем, порты при занятости подбираем свободные
        public static string ForeignCores(Settings st)
        {
            var l = new List<string>();
            foreach (var name in new[] { "xray", "v2rayN", "v2ray", "sing-box", "nekoray", "nekobox", "hiddify", "clash", "mihomo" })
                try
                {
                    foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                    {
                        string path = ProcInfo.ImagePath(p.Id);
                        if (path == "" || (!path.StartsWith(st.XrayPath, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(st.SingBoxPath, StringComparison.OrdinalIgnoreCase)))
                            if (!l.Contains(name)) l.Add(name);
                        p.Dispose();
                    }
                }
                catch { }
            return l.Count == 0 ? null : string.Join(", ", l);
        }

        static bool PortBusy(int port)
        {
            try { return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port); } catch { return false; }
        }

        void EnsurePorts(Settings st)
        {
            if (x.Running) return; // наш xray сам держит эти порты
            int s0 = st.SocksPort, h0 = st.HttpPort, a0 = st.ApiPort;
            Func<int, bool> tun = p => p == st.TunnelSocksPort || p == st.TunnelSocksPort + 1; // порты xray-туннеля «Конфигов»
            while (PortBusy(st.SocksPort) || PortBusy(st.HttpPort) || st.SocksPort == st.HttpPort || tun(st.SocksPort) || tun(st.HttpPort)) { st.SocksPort += 10; st.HttpPort += 10; if (st.SocksPort > 64000) { st.SocksPort = 20808; st.HttpPort = 20809; } }
            while (PortBusy(st.ApiPort) || st.ApiPort == st.SocksPort || st.ApiPort == st.HttpPort || tun(st.ApiPort)) { st.ApiPort += 10; if (st.ApiPort > 64000) st.ApiPort = 20813; }
            if (s0 != st.SocksPort || h0 != st.HttpPort) { st.Save(); Logger.Write(LogLevel.Warn, Tx("Порты ") + s0 + "/" + h0 + Tx(" заняты другой программой — RouteX использует ") + st.SocksPort + "/" + st.HttpPort); }
            else if (a0 != st.ApiPort) st.Save();
        }

        bool StartOn(ServerEntry s)
        {
            var st = GetSettings();
            if (abort.Cancelled) { lastErr = Tx("Отключено"); return false; }
            if (s == null) { lastErr = Tx("Сервер не выбран"); Logger.Write(LogLevel.Error, lastErr); return false; }
            PollStats(true); // трафик до переключения — предыдущему серверу
            statsId = null;
            x.Stop();
            EnsurePorts(st);
            fails = 0; slows = 0;
            string cfg = Path.Combine(Settings.Sub("runtime"), "xray.json");
            string err = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool withStats = statsOn;
                died = false;
                err = x.Start(st, XrayConfig.Build(st, s, withStats), cfg);
                if (err != null) break;
                // xray сразу завершился — неверный конфиг сервера (или ядро без api.listen)
                for (int t = 0; t < 12 && x.Running; t++) { if (PortBusy(st.SocksPort)) break; Thread.Sleep(100); }
                if (x.Running) break;
                if (withStats) { statsOn = false; Logger.Write(LogLevel.Warn, Tx("xray не запустился со статистикой трафика — пробую без неё")); continue; }
                var test = Proc.Run(XrayProcess.Exe(st), "run -test -c \"" + cfg + "\"", 8000);
                string msg = (test.Out + "\n" + test.Err).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0 || l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) ?? "";
                err = Tx("xray не запустился с сервером «") + s.Name + "»" + (msg != "" ? ": " + msg : "");
                break;
            }
            died = false;
            if (err != null) { lastErr = err; Logger.Write(LogLevel.Error, err); x.Stop(); return false; }
            lastErr = "";
            statsId = s.Id; lastStats = DateTime.Now;
            st.ActiveServer = s.Id; st.Save();
            if (st.SysProxy && !st.TunMode) SysProxy.Set("127.0.0.1:" + st.HttpPort);
            EnsureTun();
            lastCheck = DateTime.Now.AddSeconds(-st.AsInterval + 2); // первая проверка через ~2 с
            Logger.Write(LogLevel.Route, "xray → «" + s.Name + "» (" + s.Transport + ", " + s.Endpoint + ")");
            return true;
        }

        void DoConnect(string id, bool manual)
        {
            var st = GetSettings();
            var s = ServerStore.Find(id) ?? ServerStore.Find(st.ActiveServer);
            if (manual && s != null) { st.PreferredServer = s.Id; st.Save(); }
            Switches = 0; ConnectedAt = DateTime.Now; switchTimes.Clear(); failedAt.Clear(); SessionDown = SessionUp = 0;
            string foreign = ForeignCores(st);
            if (foreign != null) Logger.Write(LogLevel.Warn, Tx("Запущен другой клиент: ") + foreign + Tx(" — системный прокси и TUN могут конфликтовать, лучше закрыть его"));
            Set(XState.Connecting, s != null ? s.Name : "");
            if (StartOn(s)) { Set(XState.Connected, s.Name); SyncDpi(); }
            else { x.Stop(); Set(XState.Off, lastErr != "" ? lastErr : Tx("Не удалось запустить")); }
        }

        void DoDisconnect()
        {
            bool was = State != XState.Off;
            PollStats(true);
            statsId = null;
            sb.Stop();
            x.Stop();
            SysProxy.Clear(); // снимает только прокси, поставленный NetProbe
            if (dpiOn || dpiApps) { dpiOn = false; dpiApps = false; if (DpiFallback != null) DpiFallback(false); }
            if (was) Logger.Write(LogLevel.Route, "xray " + Tx("отключён"));
            abort.Cancelled = false;
            Set(XState.Off, "");
        }

        int Check(Settings st) { return DelayTester.ViaProxy(GetCurl(), st.SocksPort, st.TestUrl, st.AsTimeout); }

        List<ServerEntry> Pool(Settings st, ServerEntry cur)
        {
            var all = ServerStore.Snapshot();
            var now = DateTime.Now;
            IEnumerable<ServerEntry> p = all;
            if (st.AsPool == 1 && cur != null) p = p.Where(s => s.Group == cur.Group);
            else if (st.AsPool == 2) p = p.Where(s => s.Favorite);
            return p.Where(s => cur == null || s.Id != cur.Id)
                    .Where(s => { DateTime f; return !failedAt.TryGetValue(s.Id, out f) || (now - f).TotalMinutes >= st.AsSkipFailedMin; })
                    .ToList();
        }

        ServerEntry Choose(Settings st, ServerEntry cur)
        {
            var pool = Pool(st, cur);
            if (pool.Count == 0) return null;
            if (st.AsStrategy == 1)
            {
                var all = ServerStore.Snapshot();
                int ci = cur != null ? all.FindIndex(s => s.Id == cur.Id) : -1;
                for (int k = 1; k <= all.Count; k++) { var c = all[(ci + k + all.Count) % all.Count]; if (pool.Any(p => p.Id == c.Id)) return c; }
                return pool[0];
            }
            if (st.AsPreTest)
            {
                Set(XState.Switching, Tx("замер кандидатов: ") + pool.Count);
                Testing = true;
                try { DelayTester.TestMany(st, GetCurl(), pool.Take(60).ToList(), s => { }, abort); } finally { Testing = false; }
                ServerStore.Save();
            }
            var alive = pool.Where(s => s.Delay > 0 && (st.AsMaxLatency == 0 || s.Delay <= st.AsMaxLatency)).OrderBy(s => s.Delay).ToList();
            if (alive.Count == 0) alive = pool.Where(s => s.Delay > 0).OrderBy(s => s.Delay).ToList();
            if (alive.Count == 0) return st.AsPreTest ? null : pool[0];
            if (st.AsStrategy == 2) return alive[new Random().Next(Math.Min(3, alive.Count))];
            return alive[0];
        }

        bool CanSwitch(Settings st)
        {
            var now = DateTime.Now;
            switchTimes.RemoveAll(t => (now - t).TotalHours >= 1);
            if (switchTimes.Count >= st.AsMaxPerHour) { Detail = Tx("лимит переключений в час"); return false; }
            return (now - lastSwitch).TotalSeconds >= st.AsCooldown;
        }

        void SwitchAway(Settings st, ServerEntry cur, string why)
        {
            if (cur != null) failedAt[cur.Id] = DateTime.Now;
            if (!st.AsEnabled) { Set(XState.AllDown, why); return; }
            if (!CanSwitch(st)) { Set(XState.AllDown, why + " · " + Detail); return; }
            var next = Choose(st, cur);
            if (abort.Cancelled) return;
            if (next == null)
            {
                Logger.Write(LogLevel.Route, Tx("Автосвитч: рабочих серверов нет") + " (" + why + ")");
                Set(XState.AllDown, why);
                if (st.AsDpiFallback && !dpiOn && DpiFallback != null) { dpiOn = true; DpiFallback(true); }
                lastAllDownTry = DateTime.Now;
                return;
            }
            lastSwitch = DateTime.Now; switchTimes.Add(lastSwitch); Switches++;
            Logger.Write(LogLevel.Route, Tx("Автосвитч: ") + (cur != null ? "«" + cur.Name + "» " : "") + "(" + why + ") → «" + next.Name + "»" + (next.Delay > 0 ? " · " + next.Delay + Tx(" мс") : ""));
            if (StartOn(next)) Set(XState.Connected, next.Name);
        }

        void Tick()
        {
            if (abort.Cancelled) return;
            var st = GetSettings();
            var now = DateTime.Now;
            var cur = ServerStore.Find(st.ActiveServer);
            if (State == XState.Connecting || Testing) return;
            if (st.TunMode && !sb.Running && (now - lastTunTry).TotalSeconds >= 10) EnsureTun();

            if (died) { died = false; SwitchAway(st, cur, Tx("процесс xray остановился")); return; }
            if ((now - lastStats).TotalSeconds >= 10) PollStats(false);

            if (State == XState.AllDown)
            {
                if ((now - lastAllDownTry).TotalSeconds < 60) return;
                lastAllDownTry = now;
                failedAt.Clear();
                if (cur != null && x.Running && Check(st) > 0) { Recovered(st, cur); return; }
                // автосвитч выключен: на другой сервер не уходим — только перезапуск текущего, если xray упал
                if (!st.AsEnabled)
                {
                    if (cur != null && !x.Running && StartOn(cur)) { Thread.Sleep(1500); if (Check(st) > 0) Recovered(st, cur); }
                    return;
                }
                var next = Choose(st, null);
                if (next != null && StartOn(next)) { Thread.Sleep(1500); if (Check(st) > 0) Recovered(st, next); }
                return;
            }

            if ((now - lastCheck).TotalSeconds >= st.AsInterval)
            {
                lastCheck = now;
                int d = Check(st);
                LastDelay = d;
                if (d > 0)
                {
                    LastOk = now; fails = 0;
                    if (cur != null) { cur.Delay = d; cur.Tested = now; }
                    slows = st.AsMaxLatency > 0 && d > st.AsMaxLatency ? slows + 1 : 0;
                    if (slows >= st.AsSlowCount) { SwitchAway(st, cur, Tx("медленно: ") + d + Tx(" мс")); return; }
                    if (State != XState.Connected) Set(XState.Connected, cur != null ? cur.Name : "");
                    else Fire();
                }
                else
                {
                    fails++;
                    Fire();
                    if (fails >= st.AsFails) { SwitchAway(st, cur, Tx("нет ответа ") + fails + Tx(" раз")); return; }
                }
            }

            // возврат на выбранный вручную сервер
            if (st.AsEnabled && st.AsReturnPreferred && st.PreferredServer != "" && st.PreferredServer != st.ActiveServer && (now - lastReturn).TotalMinutes >= st.AsReturnMin && CanSwitch(st))
            {
                lastReturn = now;
                var pref = ServerStore.Find(st.PreferredServer);
                if (pref != null)
                {
                    DelayTester.TestMany(st, GetCurl(), new List<ServerEntry> { pref }, s => { }, abort);
                    if (pref.Delay > 0 && (st.AsMaxLatency == 0 || pref.Delay <= st.AsMaxLatency))
                    {
                        Logger.Write(LogLevel.Route, Tx("Возврат на выбранный сервер «") + pref.Name + "» · " + pref.Delay + Tx(" мс"));
                        lastSwitch = now; switchTimes.Add(now);
                        if (StartOn(pref)) Set(XState.Connected, pref.Name);
                    }
                }
            }

            // фоновые замеры кандидатов, чтобы при сбое сразу знать лучший
            if (st.AsEnabled && st.AsBgTestMin > 0 && (now - lastBgTest).TotalMinutes >= st.AsBgTestMin)
            {
                lastBgTest = now;
                var pool = Pool(st, cur).Take(60).ToList();
                if (pool.Count > 0) { Testing = true; try { DelayTester.TestMany(st, GetCurl(), pool, s => { }, abort); } finally { Testing = false; } ServerStore.Save(); }
            }
        }

        void Recovered(Settings st, ServerEntry s)
        {
            Logger.Write(LogLevel.Route, Tx("Сервер снова работает: «") + s.Name + "»");
            if (dpiOn) { dpiOn = false; if (DpiFallback != null) DpiFallback(DpiWanted); }
            fails = 0; Set(XState.Connected, s.Name);
        }

        public static string StateName(XState s)
        {
            switch (s) { case XState.Connecting: return Tx("Подключение…"); case XState.Connected: return Tx("Подключено"); case XState.Switching: return Tx("Переключение…"); case XState.AllDown: return Tx("Нет рабочих серверов"); default: return Tx("Отключено"); }
        }
    }

    // Системный прокси Windows (WinINET) на время подключения
    public static class SysProxy
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        [DllImport("wininet.dll")] static extern bool InternetSetOption(IntPtr h, int opt, IntPtr buf, int len);
        static string ours;
        static void Refresh() { InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0); InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0); }

        // Чужой прокси (Happ, v2rayN, Clash…) не трогаем никогда: снимаем только то, что поставили сами.
        // Что мы поставили, помним и после перезапуска — config\runtime\sysproxy.txt.
        static string Marker { get { return System.IO.Path.Combine(Settings.Sub("runtime"), "sysproxy.txt"); } }
        static string Mine()
        {
            if (ours != null) return ours;
            try { return System.IO.File.Exists(Marker) ? System.IO.File.ReadAllLines(Marker)[0].Trim() : null; } catch { return null; }
        }
        // какой прокси был до нас (например, Happ) — вернём его при отключении
        static string[] Before() { try { var l = System.IO.File.ReadAllLines(Marker); return l.Length >= 3 ? new[] { l[1], l[2], l.Length >= 4 ? l[3] : "" } : null; } catch { return null; } }
        static void Forget() { ours = null; try { System.IO.File.Delete(Marker); } catch { } }
        static string Current(RegistryKey k) { return Convert.ToInt32(k.GetValue("ProxyEnable", 0)) == 1 ? Convert.ToString(k.GetValue("ProxyServer")) : null; }

        // старт NetProbe: прокси остался от нас (NetProbe упал / выключили ПК) — убрать. Если порт кем-то слушается
        // (наш xray ещё не запущен — значит, это другой клиент с тем же портом), прокси его и не трогаем.
        public static void ClearIfStale(int httpPort, string ourXrayDir)
        {
            try
            {
                string mine = Mine();
                string cur; using (var k = Registry.CurrentUser.OpenSubKey(Key)) cur = Current(k);
                // 3.0.0 и раньше метку не писали: «наш» = 127.0.0.1:<наш HTTP-порт>, но только если порт никто не слушает
                if (string.IsNullOrEmpty(mine) && cur == "127.0.0.1:" + httpPort && !PortListened(httpPort) && !OurXrayRunning(ourXrayDir)) { ours = cur; mine = cur; }
                if (string.IsNullOrEmpty(mine)) return;
                if (cur == null || cur != mine) { Forget(); return; } // прокси уже не наш
                int port; int c = mine.LastIndexOf(':');
                if (c > 0 && int.TryParse(mine.Substring(c + 1), out port) && PortListened(port))
                {
                    if (OurXrayRunning(ourXrayDir)) return; // наш xray пережил обновление NetProbe — прокси остаётся нашим
                    { Logger.Write(LogLevel.Info, Tx("Системный прокси ") + mine + Tx(" используется другой программой — не трогаю")); Forget(); return; }
                }
                Clear(); // наш, остался после сбоя: снять (и вернуть прокси, который был до нас)
            }
            catch { }
        }

        static bool OurXrayRunning(string dir)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("xray"))
                    using (p) { string path = ProcInfo.ImagePath(p.Id); if (path != "" && !string.IsNullOrEmpty(dir) && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return true; }
            }
            catch { }
            return false;
        }

        static bool PortListened(int port)
        {
            try { foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()) if (ep.Port == port) return true; } catch { }
            return false;
        }

        public static void Set(string hostPort)
        {
            try
            {
                string prev = null;
                using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                {
                    string had = Current(k);
                    if (had != null && had != hostPort && had != Mine()) prev = "1\n" + had + "\n" + Convert.ToString(k.GetValue("ProxyOverride"));
                    else if (Mine() == hostPort) { var b = Before(); if (b != null) prev = b[0] + "\n" + b[1] + "\n" + b[2]; } // повторный Set: «до нас» не теряем
                    k.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                    k.SetValue("ProxyServer", hostPort);
                    k.SetValue("ProxyOverride", "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*;172.30.*;172.31.*;192.168.*;<local>");
                }
                ours = hostPort; Refresh();
                try { System.IO.File.WriteAllText(Marker, hostPort + (prev != null ? "\n" + prev : "")); } catch { }
            }
            catch (Exception ex) { Logger.Write(LogLevel.Error, "System proxy: " + ex.Message); }
        }

        // после обновления со старой версии: xray подхвачен, прокси на его порту — считаем своим
        public static void Adopt(string hostPort)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) if (string.IsNullOrEmpty(Mine()) && Current(k) == hostPort) { ours = hostPort; System.IO.File.WriteAllText(Marker, hostPort); } } catch { }
        }

        public static bool IsOurs()
        {
            try { string mine = Mine(); using (var k = Registry.CurrentUser.OpenSubKey(Key)) return !string.IsNullOrEmpty(mine) && Current(k) == mine; } catch { return false; }
        }

        public static void Clear()
        {
            try
            {
                string mine = Mine();
                using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                    if (!string.IsNullOrEmpty(mine) && Current(k) == mine)
                    {
                        var b = Before();
                        if (b != null && b[0] == "1" && b[1] != "") { k.SetValue("ProxyServer", b[1]); k.SetValue("ProxyOverride", b[2]); Logger.Write(LogLevel.Info, Tx("Системный прокси возвращён: ") + b[1]); }
                        else k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        Refresh();
                    } // чужой (или никакой) — не трогаем
                Forget();
            }
            catch { }
        }
    }
}
