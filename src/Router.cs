using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using static NetProbe.Tr;

namespace NetProbe
{
    public enum RouteState { Off, Connecting, Vpn, Backup, DpiFailover, VpnDown, DpiOnly }

    // Маршрутизатор: держит WireSock (VPN) и winws (DPI) в нужном состоянии и делает аварийное переключение.
    //
    //  DPI/VPN:  основной VPN ─(молчит > N с)─> резервный VPN (если блок включён) ─(молчит > N с)─> DPI.
    //            В режиме DPI туннель остаётся поднятым только для «строго VPN» приложений и пробника,
    //            по нему же определяется восстановление: VPN стабилен M секунд → обратно на VPN, DPI выключается.
    //  Только VPN: основной → резервный → «VPN недоступен» (kill switch), повторные попытки каждые 30 с.
    //  Только DPI: только winws, туннель не запускается.
    public class Router
    {
        public Func<Settings> GetSettings;
        public readonly TunnelRunner Wg = new TunnelRunner(); // WireSock или xray-туннель — по протоколу конфига
        public readonly ZapretRunner Dpi = new ZapretRunner();
        public event Action Changed;
        public Func<string, bool> AskStopService; // спросить пользователя перед остановкой чужой службы WireSock

        public volatile RouteState State = RouteState.Off;
        public string Detail = "";
        public bool OnBackup;            // туннель сейчас поднят на резервном конфиге
        public bool StrictOnly;          // туннель поднят только для «строго VPN» приложений (режим DPI)
        public int Failovers;
        public DateTime ConnectedAt, StateSince;
        public DateTime LastVpnOk = DateTime.MinValue;

        DateTime okSince = DateTime.MinValue, tunnelAt = DateTime.MinValue, lastCycle = DateTime.MinValue, lastExcl = DateTime.MinValue, lastKill = DateTime.MinValue;
        DateTime backupSince = DateTime.MinValue, lastLearn = DateTime.MinValue, lastDomDpi = DateTime.MinValue;
        // домены приложений через DPI: туннель поднят с DisallowedIPs для них, winws работает параллельно с VPN
        volatile bool domainDpi; int learnedVer; volatile bool learning;
        int dpiRestarts;
        volatile bool wgDied, dpiDied;
        string lastTunnelErr; // одна и та же ошибка запуска туннеля пишется в лог один раз (повторы каждые 30 с молча)

        readonly Queue<Action> q = new Queue<Action>();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        Thread th;

        public static string VpnProbe { get { return Path.Combine(CurlProber.WorkDir, "bin", "netprobe-vpn.exe"); } }
        public const string DirectProbe = "netprobe-direct.exe";

        public Router()
        {
            Wg.Exited += code => { wgDied = true; Logger.Write(LogLevel.Warn, (Wg.IsXray ? "xray TUN: " + Tx("туннель завершился (код ") : Tx("WireSock завершился (код ")) + code + ")"); wake.Set(); };
            Dpi.Exited += code => { dpiDied = true; Logger.Write(LogLevel.Warn, Tx("winws.exe завершился (код ") + code + ")"); wake.Set(); };
            Wg.Output += s => { if (GetSettings != null && GetSettings().LogPolls) Logger.Write(LogLevel.Poll, "[WireSock] " + s); else if (s.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0) Logger.Write(LogLevel.Warn, "[WireSock] " + s); };
            Dpi.Output += s => Logger.Write(LogLevel.Poll, "[winws] " + s);
            th = new Thread(Loop) { IsBackground = true, Name = "router" };
            th.Start();
        }

        public bool Active { get { return State != RouteState.Off; } }
        public bool NeedsVpnProbe { get { return Active && true; } }

        // списки, с которыми запущен VLESS / VMess / Trojan туннель (sing-box TUN) — для окна «Маршрут»
        volatile TunLists lastXrayLists;
        static TunLists XrayTunLists(Settings st, bool strictOnly)
        {
            var strictAll = AppLists.Get(AppLists.VpnOnlyApps);
            return strictOnly
                ? TunLists.FromLists(strictAll.Concat(new[] { VpnProbe }), new string[0])
                : TunLists.FromLists(AppLists.Get(AppLists.HybridApps).Concat(strictAll), AppLists.Get(TunExclKey(st)));
        }
        public TunLists CurrentXrayLists() { return lastXrayLists ?? XrayTunLists(GetSettings(), StrictOnly); } // после самообновления (Adopt) — по текущим спискам

        void Post(Action a) { lock (q) q.Enqueue(a); wake.Set(); }
        public void Connect() { Post(DoConnect); }
        public void Disconnect() { Post(DoDisconnect); }
        // при обычном запуске (не после самообновления) снимаем правила kill switch, оставшиеся от прошлого запуска
        public void ClearStaleKill() { Post(() => { if (KillSwitch.NeedsClear && KillSwitch.Clear()) Logger.Write(LogLevel.Route, Tx("Kill switch: сняты правила брандмауэра от прошлого запуска")); }); }
        public void Reapply() { Post(DoReapply); }
        public void RestartDpi() { Post(() => { if (Dpi.Running) StartDpi(); }); }
        public void ShutdownSync() { DoDisconnect(); }
        // DPI без маршрутизатора (запасной вариант автосвитча серверов)
        // DPI отдельной кнопкой: работает параллельно с VPN и не выключается вместе с ним
        public volatile bool ManualDpi;
        // DPI выключен пользователем кнопкой: winws не поднимают ни домены приложений, ни «Серверы» (DpiHold),
        // пока DPI снова не включат. Аварийный DPI (DpiFailover) и «Только DPI» — это сам маршрут, их не трогаем.
        public volatile bool DpiForcedOff;
        public bool DpiIsRoute { get { return State == RouteState.DpiFailover || State == RouteState.DpiOnly; } }
        // Режим DPI/VPN (ROUTE): туннель и winws работают одновременно — Include/«строго VPN» в туннеле, winws для всего остального.
        // Выключенная кнопка DPI (DpiForcedOff) оставляет только туннель.
        bool HybridDpiWanted(Settings st)
        {
            return st.RunMode == RouteMode.Hybrid && !DpiForcedOff &&
                   (State == RouteState.Vpn || State == RouteState.Backup || State == RouteState.VpnDown || State == RouteState.Connecting || State == RouteState.DpiFailover);
        }
        DateTime lastHybridDpi = DateTime.MinValue;
        void EnsureHybridDpi(Settings st)
        {
            if (!HybridDpiWanted(st) || Dpi.Running || ZapretRunner.Check(st.ZapretPath) != null) return;
            if ((DateTime.Now - lastHybridDpi).TotalSeconds < 15) return; // не долбить запуском, если winws сразу падает
            lastHybridDpi = DateTime.Now;
            Logger.Write(LogLevel.Route, Tx("DPI/VPN: winws параллельно с туннелем"));
            StartDpi();
        }
        public void SetManualDpi(bool on)
        {
            Post(() =>
            {
                ManualDpi = on; DpiForcedOff = !on;
                if (on) { Logger.Write(LogLevel.Route, Tr.Tx("DPI включён вручную")); if (!Dpi.Running) StartDpi(); }
                else
                {
                    Logger.Write(LogLevel.Route, Tr.Tx("DPI выключен вручную"));
                    if (!DpiIsRoute) StopDpi(true);
                }
                SetState(State, Detail);
            });
        }
        // Тумблер «DPI вместе с туннелем» (Конфиги → DPI): выкл = DPI только аварийный / «Только DPI» / ручной
        public void SetManualDpiParallel(bool on)
        {
            Post(() =>
            {
                DpiForcedOff = !on;
                if (!on) { ManualDpi = false; if (!DpiIsRoute) StopDpi(true); Logger.Write(LogLevel.Route, Tr.Tx("DPI вместе с туннелем выключен")); }
                else { Logger.Write(LogLevel.Route, Tr.Tx("DPI вместе с туннелем включён")); lastHybridDpi = DateTime.MinValue; EnsureHybridDpi(GetSettings()); }
                SetState(State, Detail);
            });
        }
        // После самообновления: подхватить уже работающие wiresock-client / winws вместо повторного запуска
        public void Adopt(Dictionary<string, string> h)
        {
            Post(() =>
            {
                var st = GetSettings();
                Func<string, string> g = k => h.ContainsKey(k) ? h[k] : "";
                System.Diagnostics.Process wg = null;
                if (TunnelProto.IsXray(g("tunnel")))
                {
                    var xp = SelfUpdate.FindChild("xray", st.XrayPath); var sp = SelfUpdate.FindChild("sing-box", st.SingBoxPath);
                    if (xp != null && sp != null) { Wg.AttachXray(xp, sp, g("tunnel")); wg = xp; }
                }
                else wg = SelfUpdate.FindChild("wiresock-client", null);
                if (wg != null && !Wg.IsXray) Wg.Attach(wg, g("tunnel"));
                if (wg != null) { tunnelAt = DateTime.Now; OnBackup = g("backup") == "1"; StrictOnly = g("strict") == "1"; domainDpi = !StrictOnly && TunnelDomains(false).Count > 0; learnedVer = DomainRoutes.Version; }
                var dpi = SelfUpdate.FindChild("winws", st.ZapretPath);
                if (dpi != null) Dpi.Attach(dpi, g("strategy"));
                ManualDpi = g("manualdpi") == "1";
                RouteState rs; if (!Enum.TryParse(g("state"), out rs)) rs = RouteState.Off;
                if (rs != RouteState.Off && (wg != null || dpi != null)) { ConnectedAt = DateTime.Now; SetState(rs, g("detail")); Logger.Write(LogLevel.Route, Tr.Tx("Обновление: подключение сохранено")); }
                else SetState(State, Detail);
            });
        }

        public Dictionary<string, string> Handoff()
        {
            return new Dictionary<string, string> { { "state", State.ToString() }, { "detail", Detail }, { "tunnel", Wg.TunnelName }, { "backup", OnBackup ? "1" : "0" }, { "strict", StrictOnly ? "1" : "0" }, { "strategy", Dpi.Strategy }, { "manualdpi", ManualDpi ? "1" : "0" } };
        }

        // DPI для вкладки «Серверы» (запасной вариант автосвитча, приложения «через DPI»): не зависит от кнопки VPN
        public Func<bool> DpiHold;                 // winws нужен «Серверам» — не останавливать вместе с VPN
        public Func<List<string>> ExtraDpiExcl;    // исключения DPI вкладки «Серверы»
        public void StandaloneDpi(bool on)
        {
            Post(() =>
            {
                if (on) { if (!Dpi.Running && !DpiForcedOff) { Logger.Write(LogLevel.Route, Tr.Tx("DPI для серверов включён")); StartDpi(); } }
                else if (State != RouteState.DpiFailover && State != RouteState.DpiOnly) StopDpi();
                SetState(State, Detail);
            });
        }
        public void RefreshDpiExclusions() { Post(() => { if (Dpi.Running) RefreshExclusions(GetSettings(), true); }); }

        // результат каждого опроса VPN-канала (пробник netprobe-vpn.exe идёт через туннель)
        public void VpnSample(bool ok, DateTime t)
        {
            if (t < tunnelAt.AddMilliseconds(500)) return; // опрос начался до перезапуска туннеля
            if (ok) { LastVpnOk = t; if (okSince == DateTime.MinValue) okSince = t; }
            else okSince = DateTime.MinValue;
        }

        void SetState(RouteState s, string detail)
        {
            if (State != s) StateSince = DateTime.Now;
            State = s; Detail = detail;
            var h = Changed; if (h != null) try { h(); } catch { }
        }

        void Loop()
        {
            while (true)
            {
                wake.WaitOne(1000);
                while (true)
                {
                    Action a; lock (q) { if (q.Count == 0) break; a = q.Dequeue(); }
                    try { a(); } catch (Exception ex) { Logger.Write(LogLevel.Error, Tx("Маршрутизатор: ") + ex.Message); }
                }
                try
                {
                    if (State != RouteState.Off) Tick();
                    else if (Dpi.Running) RefreshExclusions(GetSettings(), false); // DPI кнопкой или для «Серверов» без VPN
                }
                catch (Exception ex) { Logger.Write(LogLevel.Error, Tx("Маршрутизатор: ") + ex.Message); }
            }
        }

        // ---------- списки ----------
        // исключения winws: «Не трогать эти сайты» (dpionly-exclude: домены/IP/CIDR + легаси-процессы через снапшот TCP).
        // DPI глобален: исключение приложений из VPN (Exclude) на DPI не влияет — домен важнее приложения.
        List<string> Excl(Settings st) { return AppLists.Get(AppLists.DpiOnlyExcl); }
        // исключения туннеля: только приложения (WireSock DisallowedApps)
        public static string TunExclKey(Settings st) { return st.RunMode == RouteMode.VpnOnly ? AppLists.VpnOnlyExcl : AppLists.HybridExcl; }

        // Домены под приложениями в Include DPI, чьи приложения идут в туннель (или Include пуст = весь трафик в туннеле)
        public static List<string> TunnelDomains(bool strictOnly)
        {
            // приложение в Include может быть записано иначе (подстрока пути: «browser», полный путь), поэтому
            // при полном туннеле берутся домены всех приложений; при «строго VPN» — только тех, кто совпал с этим списком
            if (GetDomainsRemoved()) return new List<string>(); // 3.0: per-app домены упразднены (домены — только глобально на вкладке DPI)
            var doms = AppLists.Domains(AppLists.DpiApps);
            if (strictOnly) { var strict = AppLists.Get(AppLists.VpnOnlyApps); doms = doms.Where(d => AppLists.InList(strict, d.Key)).ToList(); }
            return doms.Select(d => d.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        static bool GetDomainsRemoved() { return true; }

        string BuildConfig(Settings st, string tunnel, bool strictOnly)
        {
            var strict = AppLists.AppsOnly(AppLists.VpnOnlyApps);
            var allow = new List<string>(); var deny = new List<string>();
            if (strictOnly)
            {
                allow.AddRange(strict); allow.Add(VpnProbe);
            }
            else
            {
                allow.AddRange(AppLists.AppsOnly(AppLists.HybridApps)); allow.AddRange(strict);
                deny.AddRange(AppLists.AppsOnly(TunExclKey(st)));
                if (allow.Count > 0) allow.Add(VpnProbe); else deny.Add(DirectProbe); // пусто = весь трафик через VPN, кроме пробника «напрямую»
            }
            allow = allow.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            deny = deny.Where(a => a.Length > 0 && !allow.Contains(a, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var doms = TunnelDomains(strictOnly);
            var ips = doms.Count > 0 ? DomainRoutes.IpsFor(doms) : new List<string>();
            domainDpi = !strictOnly && doms.Count > 0;
            learnedVer = DomainRoutes.Version;
            if (doms.Count > 0) Logger.Write(LogLevel.Route, Tx("Домены через DPI мимо туннеля: ") + string.Join(", ", doms) + " · IP: " + ips.Count);
            return Tunnels.Runtime(tunnel, allow, deny, st.DnsViaTunnel, ips);
        }

        bool HasBackup(Settings st) { return st.BackupEnabled && Tunnels.Exists(st.BackupTunnel) && st.BackupTunnel != st.PrimaryTunnel; }

        bool StartTunnel(Settings st, bool backup, bool strictOnly)
        {
            string name = backup ? st.BackupTunnel : st.PrimaryTunnel;
            if (!Tunnels.Exists(name)) { TunnelErr((backup ? Tx("Резервный") : Tx("Основной")) + Tx(" туннель не выбран или не найден")); return false; }
            if (TunnelProto.IsXray(name))
            {
                // VLESS / VMess / Trojan: тот же сценарий, другой транспорт (xray + sing-box TUN, списки — process_path_regex)
                wgDied = false;
                okSince = DateTime.MinValue; tunnelAt = DateTime.Now; LastVpnOk = DateTime.MinValue;
                var lists = XrayTunLists(st, strictOnly);
                lastXrayLists = lists;
                string xerr = Wg.StartXray(st, name, lists);
                if (xerr != null) { TunnelErr(xerr); wgDied = true; return false; }
                lastTunnelErr = null; OnBackup = backup; StrictOnly = strictOnly;
                Logger.Write(LogLevel.Route, Tx("Туннель «") + name + "» (" + Tunnels.Endpoint(name) + Tx(") запущен") + (backup ? Tx(" · резервный") : "") + (strictOnly ? Tx(" · только для приложений «строго VPN»") : ""));
                return true;
            }
            if (!File.Exists(st.WiresockPath)) { wgDied = true; TunnelErr(Tx("Не найден wiresock-client.exe: ") + st.WiresockPath + Tx(". Установите WireSock VPN Client (есть в папке TunnlTo\\wiresock_installers) или укажите путь в Настройках.")); return false; }
            if (WiresockRunner.ServiceRunning())
            {
                bool ok = AskStopService != null && AskStopService(Tx("Служба wiresock-client-service (TunnlTo) уже запущена и займёт драйвер WireSock.\nОстановить её, чтобы NetProbe управлял туннелем?"));
                if (!ok) { Logger.Write(LogLevel.Error, Tx("Служба WireSock запущена — туннель NetProbe не стартовал")); return false; }
                Logger.Write(LogLevel.Info, Tx("Останавливаю службу wiresock-client-service…"));
                WiresockRunner.StopService();
            }
            wgDied = false;
            okSince = DateTime.MinValue; tunnelAt = DateTime.Now; LastVpnOk = DateTime.MinValue;
            string err = Wg.Start(st, name, BuildConfig(st, name, strictOnly));
            if (err != null) { TunnelErr(err); wgDied = true; return false; }
            lastTunnelErr = null;
            OnBackup = backup; StrictOnly = strictOnly;
            Logger.Write(LogLevel.Route, Tx("Туннель «") + name + "» (" + Tunnels.Endpoint(name) + Tx(") запущен") + (backup ? Tx(" · резервный") : "") + (strictOnly ? Tx(" · только для приложений «строго VPN»") : ""));
            return true;
        }

        void TunnelErr(string msg) { if (msg == lastTunnelErr) return; lastTunnelErr = msg; Logger.Write(LogLevel.Error, msg); }

        void StopTunnel() { if (Wg.Running) { Wg.Stop(); Logger.Write(LogLevel.Route, Tx("Туннель остановлен")); } wgDied = false; }

        bool StartDpi()
        {
            var st = GetSettings();
            int own = Dpi.Pid; // свой winws — не конфликт (его перезапустит Dpi.Start → Stop)
            string c = ZapretRunner.Conflicts(own);
            if (c != null) { Logger.Write(LogLevel.Warn, Tx("Конфликт DPI: ") + c + Tx(" — останавливаю")); ZapretRunner.StopConflicts(own); }
            RefreshExclusions(st, true);
            dpiDied = false;
            string err = Dpi.Start(st, st.DpiStrategy);
            if (err != null) { Logger.Write(LogLevel.Error, err); dpiDied = true; return false; }
            Logger.Write(LogLevel.Route, Tx("DPI (winws) запущен · стратегия «") + st.DpiStrategy + "»");
            return true;
        }

        void StopDpi(bool force = false) { if (!force && !DpiForcedOff && (ManualDpi || domainDpi || HybridDpiWanted(GetSettings()) || (DpiHold != null && DpiHold()))) return; if (Dpi.Running) { Dpi.Stop(); Logger.Write(LogLevel.Route, Tx("DPI (winws) остановлен")); } dpiDied = false; }

        void RefreshExclusions(Settings st, bool force)
        {
            if (!force && (DateTime.Now - lastExcl).TotalSeconds < st.ExclRefreshSec) return;
            lastExcl = DateTime.Now;
            var list = Excl(st);
            if (ExtraDpiExcl != null) list = list.Concat(ExtraDpiExcl()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var ex = ExclusionSet.From(list);
            ZapretRunner.WriteExclusions(ex, ProcInfo.RemoteIpsOf(ex.Processes));
            ZapretRunner.WriteUserHosts(st.ZapretPath);
        }

        // VPN поднят, а домены приложений должны идти под DPI: держим winws, учим новые IP из кэша DNS
        void DomainTick(Settings st)
        {
            var now = DateTime.Now;
            if (!domainDpi) return;
            if (!Dpi.Running && !DpiForcedOff && (now - lastDomDpi).TotalSeconds >= 30)
            {
                lastDomDpi = now;
                Logger.Write(LogLevel.Route, Tx("DPI для доменов приложений (параллельно с VPN)"));
                StartDpi();
            }
            if (Dpi.Running) RefreshExclusions(st, false);
            if (!learning && (now - lastLearn).TotalSeconds >= 60)
            {
                lastLearn = now; learning = true;
                var doms = TunnelDomains(false);
                ThreadPool.QueueUserWorkItem(_ => { try { int n = DomainRoutes.Learn(doms); if (n > 0) Logger.Write(LogLevel.Poll, Tx("Домены через DPI: новых IP из кэша DNS: ") + n); } catch { } finally { learning = false; } });
            }
            // новые IP попадут в DisallowedIPs только с перезапуском туннеля — не чаще DomainReapplyMin
            if (st.DomainReapplyMin > 0 && DomainRoutes.Version != learnedVer && (now - tunnelAt).TotalMinutes >= st.DomainReapplyMin && Wg.Running && !StrictOnly)
            {
                Logger.Write(LogLevel.Route, Tx("Домены через DPI: новые IP — перезапуск туннеля"));
                StartTunnel(st, OnBackup, false);
            }
        }

        void Kill(Settings st, bool on)
        {
            if (!on || !st.KillSwitch) { if (KillSwitch.NeedsClear) { KillSwitch.Clear(); Logger.Write(LogLevel.Route, Tx("Kill switch снят")); } return; }
            if ((DateTime.Now - lastKill).TotalSeconds < 5) return;
            lastKill = DateTime.Now;
            var strict = AppLists.AppsOnly(AppLists.VpnOnlyApps);
            if (strict.Count == 0) return;
            int n = KillSwitch.Block(ProcInfo.PathsOf(strict));
            if (n > 0) Logger.Write(LogLevel.Route, Tx("Kill switch: заблокировано приложений «строго VPN»: ") + KillSwitch.Count);
        }

        // ---------- команды ----------
        void DoConnect()
        {
            var st = GetSettings();
            DoDisconnect();
            Failovers = 0; dpiRestarts = 0; ConnectedAt = DateTime.Now; lastTunnelErr = null;
            SetState(RouteState.Connecting, "");
            Logger.Write(LogLevel.Route, Tx("Подключение · режим: ") + ModeName(st.RunMode));
            if (st.RunMode == RouteMode.DpiOnly)
            {
                if (StartDpi()) { SetState(RouteState.DpiOnly, st.DpiStrategy); Kill(st, true); }
                else SetState(RouteState.Off, Tx("Не удалось запустить DPI"));
                return;
            }
            if (StartTunnel(st, false, false)) { SetState(RouteState.Vpn, st.PrimaryTunnel); lastHybridDpi = DateTime.MinValue; EnsureHybridDpi(st); return; }
            if (HasBackup(st) && StartTunnel(st, true, false)) { backupSince = DateTime.Now; SetState(RouteState.Backup, st.BackupTunnel); lastHybridDpi = DateTime.MinValue; EnsureHybridDpi(st); return; }
            string why = !TunnelProto.NeedsWiresock(st) || File.Exists(st.WiresockPath) ? Tx("VPN не запустился") : Tx("WireSock не установлен");
            if (st.RunMode == RouteMode.Hybrid) { EnterDpi(st, why); return; }
            SetState(RouteState.VpnDown, why); Kill(st, true);
        }

        void DoDisconnect()
        {
            domainDpi = false;
            bool was = State != RouteState.Off || Wg.Running || Dpi.Running;
            StopTunnel(); StopDpi();
            if (KillSwitch.NeedsClear) KillSwitch.Clear();
            if (was) Logger.Write(LogLevel.Route, Tx("Отключено"));
            SetState(RouteState.Off, "");
        }

        void DoReapply()
        {
            var st = GetSettings();
            if (State == RouteState.Off) return;
            if (Wg.Running) { Logger.Write(LogLevel.Route, Tx("Списки изменены — перезапуск туннеля с новыми правилами")); StartTunnel(st, OnBackup, StrictOnly); }
            if ((State == RouteState.Vpn || State == RouteState.Backup || State == RouteState.VpnDown) && (!domainDpi || DpiForcedOff) && Dpi.Running) StopDpi(); // доменов больше нет / DPI выключен кнопкой (в DPI/VPN winws держит HybridDpiWanted)
            EnsureHybridDpi(st);
            if (domainDpi && !Dpi.Running && !DpiForcedOff) { lastDomDpi = DateTime.Now; StartDpi(); }
            if (Dpi.Running) RefreshExclusions(st, true);
            if (KillSwitch.NeedsClear) { KillSwitch.Clear(); lastKill = DateTime.MinValue; }
            SetState(State, Detail);
        }

        void EnterDpi(Settings st, string why)
        {
            Failovers++;
            Logger.Write(LogLevel.Route, Tx("АВАРИЙНОЕ ПЕРЕКЛЮЧЕНИЕ НА DPI: ") + why);
            StartDpi();
            // туннель оставляем только для «строго VPN» приложений и пробника — по нему ловим восстановление
            if (!StartTunnel(st, OnBackup && HasBackup(st), true)) Kill(st, true);
            lastCycle = DateTime.Now;
            SetState(RouteState.DpiFailover, why);
        }

        double Grace(Settings st) { return st.FailoverSec + st.TimeoutSec + 3; }

        // VPN «молчит» дольше порога?
        bool VpnSilent(Settings st)
        {
            if (wgDied || !Wg.Running) return true;
            var now = DateTime.Now;
            if ((now - tunnelAt).TotalSeconds < Grace(st)) return false;
            var last = LastVpnOk > tunnelAt ? LastVpnOk : tunnelAt.AddSeconds(Grace(st) - st.FailoverSec);
            return (now - last).TotalSeconds > st.FailoverSec;
        }

        bool VpnStable(Settings st) { return Wg.Running && okSince != DateTime.MinValue && (DateTime.Now - okSince).TotalSeconds >= st.RecoverSec; }

        void Tick()
        {
            var st = GetSettings();
            var now = DateTime.Now;
            switch (State)
            {
                case RouteState.Vpn:
                case RouteState.Backup:
                    DomainTick(st);
                    EnsureHybridDpi(st);
                    if (Dpi.Running && st.RunMode == RouteMode.Hybrid) RefreshExclusions(st, false);
                    if (VpnSilent(st))
                    {
                        string why = wgDied ? Tx("процесс WireSock остановился") : Tx("VPN не отвечает ") + st.FailoverSec + Tx(" с");
                        if (!OnBackup && HasBackup(st))
                        {
                            Failovers++;
                            Logger.Write(LogLevel.Route, Tx("Основной VPN: ") + why + Tx(" → резервный VPN «") + st.BackupTunnel + "»");
                            if (StartTunnel(st, true, false)) { backupSince = now; SetState(RouteState.Backup, st.BackupTunnel); return; }
                        }
                        if (st.RunMode == RouteMode.Hybrid) { EnterDpi(st, why); return; }
                        Failovers++;
                        Logger.Write(LogLevel.Route, Tx("VPN недоступен: ") + why + (st.KillSwitch ? Tx(" · kill switch для «строго VPN»") : ""));
                        lastCycle = now;
                        SetState(RouteState.VpnDown, why);
                        Kill(st, true);
                        return;
                    }
                    if (State == RouteState.Backup && st.RetryPrimaryMin > 0 && (now - backupSince).TotalMinutes >= st.RetryPrimaryMin && Tunnels.Exists(st.PrimaryTunnel))
                    {
                        Logger.Write(LogLevel.Route, Tx("Пробую вернуться на основной VPN «") + st.PrimaryTunnel + "»");
                        if (StartTunnel(st, false, false)) SetState(RouteState.Vpn, st.PrimaryTunnel);
                        else { backupSince = now; StartTunnel(st, true, false); }
                    }
                    break;

                case RouteState.DpiFailover:
                    if (dpiDied && dpiRestarts < 5) { dpiRestarts++; Logger.Write(LogLevel.Warn, Tx("Перезапуск winws (") + dpiRestarts + ")"); StartDpi(); }
                    RefreshExclusions(st, false);
                    if (VpnStable(st))
                    {
                        Logger.Write(LogLevel.Route, Tx("VPN снова стабилен ") + st.RecoverSec + Tx(" с → возврат с DPI на VPN"));
                        bool b = OnBackup;
                        if (StartTunnel(st, b, false)) { StopDpi(); Kill(st, false); if (b) backupSince = now; SetState(b ? RouteState.Backup : RouteState.Vpn, b ? st.BackupTunnel : st.PrimaryTunnel); }
                        return;
                    }
                    if (VpnSilent(st))
                    {
                        Kill(st, true);
                        if ((now - lastCycle).TotalSeconds >= 30)
                        {
                            lastCycle = now;
                            bool nb = HasBackup(st) && !OnBackup;
                            StartTunnel(st, nb, true);
                        }
                    }
                    else if (LastVpnOk > tunnelAt) Kill(st, false);
                    break;

                case RouteState.VpnDown:
                    DomainTick(st);
                    EnsureHybridDpi(st);
                    if (VpnStable(st) || (Wg.Running && LastVpnOk > tunnelAt && (now - LastVpnOk).TotalSeconds < 3))
                    {
                        Logger.Write(LogLevel.Route, Tx("VPN восстановлен"));
                        Kill(st, false);
                        if (OnBackup) backupSince = now;
                        SetState(OnBackup ? RouteState.Backup : RouteState.Vpn, OnBackup ? st.BackupTunnel : st.PrimaryTunnel);
                        return;
                    }
                    Kill(st, true);
                    if ((now - lastCycle).TotalSeconds >= 30)
                    {
                        lastCycle = now;
                        bool nb = HasBackup(st) && !OnBackup;
                        if (!StartTunnel(st, nb, false) && nb) StartTunnel(st, false, false);
                    }
                    break;

                case RouteState.DpiOnly:
                    RefreshExclusions(st, false);
                    Kill(st, true);
                    if (dpiDied && dpiRestarts < 5) { dpiRestarts++; Logger.Write(LogLevel.Warn, Tx("Перезапуск winws (") + dpiRestarts + ")"); StartDpi(); }
                    break;
            }
        }

        public static string ModeName(RouteMode m)
        {
            switch (m) { case RouteMode.VpnOnly: return Tx("Только VPN"); case RouteMode.DpiOnly: return Tx("Только DPI"); default: return Tx("DPI/VPN (авто)"); }
        }

        public static string StateName(RouteState s)
        {
            switch (s)
            {
                case RouteState.Connecting: return Tx("Подключение…");
                case RouteState.Vpn: return Tx("VPN · основной");
                case RouteState.Backup: return Tx("VPN · резервный");
                case RouteState.DpiFailover: return Tx("DPI · аварийный режим");
                case RouteState.VpnDown: return Tx("VPN недоступен");
                case RouteState.DpiOnly: return "DPI";
                default: return Tx("Отключено");
            }
        }
    }
}
