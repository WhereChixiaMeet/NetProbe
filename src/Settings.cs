using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public enum RouteMode { Hybrid = 0, VpnOnly = 1, DpiOnly = 2 }

    public class Settings
    {
        public int[] GetCharLayout(string key)
        {
            foreach (var part in (CharLayout ?? "").Split(';'))
            {
                int e = part.IndexOf('='); if (e <= 0 || part.Substring(0, e) != key) continue;
                var v = part.Substring(e + 1).Split(','); if (v.Length < 3) return null;
                int a, b, c; if (int.TryParse(v[0], out a) && int.TryParse(v[1], out b) && int.TryParse(v[2], out c)) return new[] { a, b, c };
            }
            return null;
        }
        public void SetCharLayout(string key, int[] v)
        {
            var parts = (CharLayout ?? "").Split(';').Where(x => x.Length > 0 && !x.StartsWith(key + "=")).ToList();
            if (v != null) parts.Add(key + "=" + v[0] + "," + v[1] + "," + v[2]);
            CharLayout = string.Join(";", parts);
        }

        // готовые URL для замеров (как список в v2rayN); в поле можно вписать свой
        public static readonly string[] TestUrls = {
            "https://www.google.com/generate_204", "https://www.youtube.com/generate_204", "https://www.googlevideo.com/generate_204",
            "https://www.gstatic.com/generate_204", "https://www.apple.com/library/test/success.html", "http://www.msftconnecttest.com/connecttest.txt" };
        public static readonly string[] SpeedUrls = {
            "https://cachefly.cachefly.net/1mb.test", "https://cachefly.cachefly.net/10mb.test", "https://cachefly.cachefly.net/50mb.test", "https://cachefly.cachefly.net/100mb.test",
            "https://speed.cloudflare.com/__down?bytes=10000000", "https://speed.cloudflare.com/__down?bytes=50000000", "https://speed.cloudflare.com/__down?bytes=99999999" };

        // Безопасные пределы частоты опроса (запросов в секунду на один канал)
        public const double MinPollRate = 0.1;
        public const double MaxPollRateCurl = 2.0;
        public const double MaxPollRatePs = 1.0;
        public const int MinIntervalMs = 500;
        public const int MaxBackoffMs = 15000;
        public static string[] RetentionNames { get { return new[] { Tx("1 час"), Tx("1 день"), Tx("1 неделя"), Tx("1 месяц") }; } }

        // --- маршрутизация ---
        public int Mode = 0;                 // RouteMode — режим маршрутизации (сегмент ROUTE | VPN | DPI): 0 DPI/VPN, 1 только VPN, 2 только DPI
        public int VpnMode = 0;              // как работает кнопка VPN: 0 DPI/VPN (с переключением на DPI), 1 Только VPN
        public string PrimaryTunnel = "";    // имя .conf в config\tunnels
        public string BackupTunnel = "";
        public bool BackupEnabled = false;
        public int FailoverSec = 7;          // сколько секунд VPN может молчать до переключения
        public int RecoverSec = 15;          // сколько секунд VPN должен стабильно работать для возврата
        public int RetryPrimaryMin = 5;      // на резервном VPN: как часто пробовать вернуться на основной (0 = никогда)
        public bool KillSwitch = true;       // блокировать «только VPN» приложения, пока VPN недоступен
        public bool AutoConnect = true;      // по умолчанию VPN включается сам при запуске (в т.ч. автозапуске)
        public string WiresockPath = @"C:\Program Files\WireSock VPN Client\bin\wiresock-client.exe";
        public string WiresockLogLevel = "info";
        public bool WiresockAdapter = false; // -lac: виртуальный адаптер
        public bool DnsViaTunnel = false;
        public bool TunBlockIpv6 = true; // IPv4-only TUN; strict_route blocks the unsupported family on Windows.
        internal bool PrimaryXrayTunnel;
        internal string TunServerDomain;

        // --- DPI (zapret / winws) ---
        public string ZapretDir = "";        // пусто = <папка приложения>\zapret
        public string DpiStrategy = "general";
        public int GameFilter = 0;           // 0 выкл, 1 TCP+UDP, 2 TCP, 3 UDP
        public string GameTcp = "1024-65535";
        public string GameUdp = "1024-65535";
        public int DpiButton = 0;            // кнопка DPI: 0 не трогали, 1 включена пользователем, 2 выключена пользователем (переживает перезапуск)
        public int ExclRefreshSec = 10;      // обновление IP исключённых процессов для DPI
        public int DomainReapplyMin = 10;    // домены приложений через DPI: новые IP из кэша DNS → перезапуск туннеля не чаще, мин (0 = только при подключении)

        // --- серверы (xray, аналог v2rayN) ---
        public string XrayDir = "";          // пусто = <папка приложения>\xray
        public int SocksPort = 10808;
        public int TunnelSocksPort = 10828; // socks xray-туннеля (VLESS/VMess/Trojan в «Конфигах»), отдельно от подписок
        public int HttpPort = 10809;
        public bool SysProxy = true;         // системный прокси Windows на время подключения
        public bool AllowLan = false;
        public bool BypassRu = true;         // RU-домены и IP напрямую
        public bool BlockAds = false;
        public bool XrayAutoConnect = false;
        public bool TunMode = false;         // TUN через sing-box: приложения из Include → сервер, Exclude → напрямую
        public string SingBoxDir = "";       // пусто = <папка приложения>\singbox
        public bool CheckUpdates = true;     // проверять обновления автоматически
        public bool UpdNotify = true;        // уведомления о новых версиях
        public bool UpdZapret = true;        // следить за zapret
        public bool UpdXray = true;          // следить за xray
        public int UpdEvery = 3;             // 0 только при запуске, 1 каждые 6 ч, 2 каждые 12 ч, 3 раз в день, 4 раз в неделю
        public double LastUpdCheck = 0;      // OADate последней проверки
        public string ActiveServer = "";
        public string PreferredServer = "";  // выбранный вручную; к нему можно возвращаться
        public string TestUrl = "https://www.gstatic.com/generate_204";
        public string SpeedUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
        public int SpeedSec = 10;            // длительность замера скорости одного сервера, с
        public int ApiPort = 10813;          // API статистики xray (трафик по серверам)
        public string SrvTab = "all";        // открытая вкладка серверов: all / active / fav / manual / id подписки
        public int SrvListTab = 0;
        public string SrvCols = "";          // колонки таблицы серверов: имя:ширина/вес:порядок;… (пусто = по умолчанию)           // открытый список приложений на вкладке «Серверы»
        // автосвитч серверов
        public bool AsEnabled = true;
        public int AsInterval = 10;          // проверка активного сервера, с
        public int AsTimeout = 5;            // таймаут проверки, с
        public int AsFails = 3;              // сбоев подряд до переключения
        public int AsMaxLatency = 0;         // мс; медленнее этого считается «плохо» (0 = не учитывать)
        public int AsSlowCount = 3;          // сколько медленных проверок подряд до переключения
        public int AsPool = 0;               // 0 все, 1 та же группа, 2 только избранные
        public int AsStrategy = 0;           // 0 самый быстрый, 1 следующий по списку, 2 случайный из 3 лучших
        public bool AsPreTest = true;        // перед переключением замерить кандидатов
        public int AsBgTestMin = 15;         // фоновый замер всех кандидатов, мин (0 = выкл)
        public bool AsReturnPreferred = true;
        public int AsReturnMin = 5;          // как часто проверять выбранный вручную сервер, мин
        public int AsCooldown = 30;          // пауза после переключения, с
        public int AsMaxPerHour = 10;        // не больше переключений в час (защита от «дребезга»)
        public int AsSkipFailedMin = 10;     // не пробовать упавший сервер N минут
        public bool AsDpiFallback = false;   // ни один сервер не работает — включить DPI (zapret)

        // --- мониторинг ---
        public double PollRate = 0.5;        // опросов/с канала VPN (пробник внутри туннеля)
        public double DirectPollRate = 0.1;  // опросов/с канала «Напрямую» — раз в 10 с: он идёт с твоего обычного IP
        public bool PollDefaults34 = false;  // 3.4.1: один раз перевести старое значение по умолчанию 1.0 на новое
        public int TimeoutSec = 5;
        public string ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";
        // 3.4.7: опросы по кругу — ProbeUrl (даёт IP и страну), затем эти адреса; один сбойный сайт не роняет VPN
        public bool ProbeRotate = true;
        public string ProbeSpare = "https://www.gstatic.com/generate_204 http://www.msftconnecttest.com/connecttest.txt https://www.apple.com/library/test/success.html";
        // 3.4.7: VLESS/VMess/Trojan-туннель — пока данные идут через сервер (Clash API sing-box), сайт не опрашивается;
        // активный опрос не реже раза в ProbeIdleSec и сразу, если трафик через сервер встал
        public bool ProbePassive = true;
        public int ProbeIdleSec = 30;
        public List<string> ProbeUrls()
        {
            var l = new List<string> { ProbeUrl };
            if (ProbeRotate) l.AddRange((ProbeSpare ?? "").Split(new[] { ' ', ',', ';', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)));
            return l.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        public string CurlPath = "curl.exe";
        public string VpnProxy = "";
        public int DirectMode = 0;
        public string DirectInterface = "auto";
        public string PingHost = "1.1.1.1";
        public int PingCount = 20;
        public int DownloadMB = 25;
        public int UploadMB = 10;
        public int Streams = 4;
        public int GoodMs = 120;
        public int WarnMs = 300;
        public string DnsDomains = "google.com youtube.com cloudflare.com github.com wikipedia.org yandex.ru telegram.org microsoft.com";
        public string Sites = "https://www.google.com\nhttps://www.youtube.com\nhttps://www.instagram.com\nhttps://x.com\nhttps://discord.com\nhttps://web.telegram.org\nhttps://chatgpt.com\nhttps://github.com\nhttps://www.netflix.com\nhttps://open.spotify.com\nhttps://yandex.ru\nhttps://vk.com";

        // --- лог ---
        public string LogDir = "";           // пусто = <папка приложения>\log
        public int LogRetention = 2;         // 0=1 час, 1=1 день, 2=1 неделя, 3=1 месяц
        public bool LogPolls = false;
        public bool LogToFile = true;

        // --- окно ---
        public int Theme = 0;                // ThemeKind
        public string Accent = "79,140,255"; // цвет акцента R,G,B
        public bool ShowAppIcons = true;
        public string BgImage = "";          // своя картинка фона
        public int BgDim = 70;
        public int CardOpacity = 70;         // непрозрачность карточек поверх своего фона, %               // насколько затемнить/осветлить фон под тему, %
        public string AppIconPath = "";      // своя иконка приложения (.ico/.png/.exe)     // иконки приложений в списках
        public string Lang = "en";           // язык интерфейса: en / ru
        public bool MinimizeToTray = true;
        public bool CloseToTray = true;
        public bool ShowWidget = true;
        public bool WidgetTopMost = true;
        public int WidgetOpacity = 92;
        public int WidgetX = -1;
        public int WidgetY = -1;
        public bool WidgetPing = true;       // виджет: строки пинга (VPN / Напрямую)
        public bool WidgetSpeed = true;     // виджет: строки скорости (↓ / ↑)
        public bool WidgetSplit = false;     // пинг и скорость — два отдельных виджета
        public int Widget2X = -1;            // позиция второго виджета (скорость)
        public int Widget2Y = -1;
        public bool SidePing = true;         // мини-монитор в боковой панели: пинг
        public bool SideSpeed = true;       // мини-монитор в боковой панели: скорость
        public bool SpeedChart = true;       // график скорости на вкладке Monitor
        public bool SpeedOn = true;  // монитор скорости целиком (выкл — не считается и нигде не показывается)
        public bool LogProbes = true;  // опросы пишутся в лог и в статистику Day/Week, даже когда мониторинг выключен
        public int ProbeIpFamily = 0;        // опрос VPN-канала: 0 — IPv4, 1 — IPv6, 2 — авто (раньше авто: IP «прыгал» v4 ⇄ v6)
        public bool IpNewOnly = true;  // «публичный IP сменился» — только для действительно нового адреса, не для чередования
        public int DownAfterFails = 3;       // «связь потеряна» — только после стольких сбоев подряд (одиночный сбой — потеря опроса)
        public bool WidgetSpeedOn { get { return SpeedOn && WidgetSpeed; } }
        public bool SideSpeedOn { get { return SpeedOn && SideSpeed; } }
        public bool SpeedChartOn { get { return SpeedOn && SpeedChart; } }
        public bool SpeedDefaults = false;
        public bool MonDefaults312 = false;  // 3.1.2: один раз у всех — пинг и скорость включены (дальше можно выключить)
        // ---- 3.1: окно, шрифты ----
        public int WinX = -32000, WinY = -32000, WinW = 0, WinH = 0; // последнее положение/размер окна (0 — по умолчанию)
        public bool WinMax = false;
        public int LastPage = 0;             // вкладка, открытая при выходе
        public int SideWidth = 214;
        public string SetTab = "look";       // открытая вкладка настроек
        // 3.3: TG Proxy — локальный MTProto-прокси для Telegram Desktop (перенос tg-ws-proxy)
        public bool TgOn = false;                 // включён (запускается вместе с NetProbe)
        public string TgHost = "127.0.0.1"; public int TgPort = 1443; public string TgSecret = "";
        public string TgDcIp = "2:149.154.167.220, 4:149.154.167.220";
        public bool TgVerbose = false; public int TgBufKb = 256, TgPool = 4;
        public bool TgCf = true, TgH2 = true, TgCfUserOn = false, TgWorkerOn = false, TgTestDc = false, TgNoSecure = false, TgProxyProto = false;
        public string TgCfUser = "", TgWorker = "", TgFakeTls = "";
        public string TgUpstreamNotified = "";   // о какой версии tg-ws-proxy уже сообщили (один раз)
        public string ModuleOrder = "";      // 3.2.1: порядок карточек (ключи через запятую) — перетаскивание внутри страницы
        public string ModuleTabs = "";       // 3.2.1: вкладка Настроек, куда положили карточку: ключ=вкладка;…
        public string ModuleLoc = "";        // 3.2: где лежат карточки-модули: ключ=home|settings|c<N>;…
        public string CustomPages = "";      // свои вкладки: c1:Название;…
        public string NavNames = "";         // свои названия пунктов меню: индекс:Название;…
        public int SrvRightW = 360;
        public int DockW = 380;              // ширина правой колонки модулей на остальных страницах
        public bool LockDefault321 = false;  // 3.2.1: один раз у всех — интерфейс заблокирован (двигать — снять Lock в Настройках)
        public bool UiLocked = true;        // Lock interface: карточки, разделители и пункты меню не двигаются и не меняются          // ширина правой колонки Subscriptions (тянется мышкой)
        public string SrvSorts = "";         // сортировка таблицы серверов по вкладкам: id=колонка:a|d;… («*» — общая)          // ширина левой панели (перетаскивается за край)
        public string FontBtnFamily = "Segoe UI", FontLargeFamily = "Segoe UI", FontMedFamily = "Segoe UI", FontSmallFamily = "Segoe UI";
        public double FontBtnSize = 9, FontLargeSize = 11, FontMedSize = 9, FontSmallSize = 8;   // 3.0.9: один раз включить скорость везде (в 3.0.8 по умолчанию была выключена)
        public bool AutoStartMonitor = true;
        public bool StartMinimized = false;
        public bool Autorun = true;          // по умолчанию запуск вместе с Windows
        public bool Notify = true;

        // ---- 3.0 ----
        public bool V3Migrated = false;      // одноразовая миграция настроек 2.x → 3.0 (Mode = режим работы, а не «вид»)
        public bool HybridMode = false;      // Режим гибрида: Primary VPN и подписки разрешены одновременно, ручной тумблер источника
        public bool HybridWarned = false;    // предупреждение о гибриде показано (галочка «больше не показывать»)
        public int Source = 0;               // активный источник в гибриде: 0 Primary VPN, 1 Subscriptions
        public bool VpnNoTunnelWarned = false; // «Туннель не назначен» показан — один раз за жизнь установки, обновлением не сбрасывается
        public bool DomainsMigrated = false; // домены из карточек приложений перенесены в общий список DPI
        public string UpdatedFrom = "";      // версия, с которой обновились (баннер «перезапусти вручную»)
        public int AppNotifyEvery = 0;       // уведомления о новой версии NetProbe: 0 сразу, 1 день, 2 неделя, 3 месяц
        public bool AppNotifyDelayOn = true; // уведомлять не раньше чем через AppNotifyDelayMin после публикации релиза
        public int AppNotifyDelayMin = 15;
        public string AppNotifiedTag = "";   // о какой версии уже уведомили (одно уведомление на версию)
        public double AppNotifiedAt = 0;     // OADate последнего уведомления
        public bool CharWidget = true;       // Personal-темы: персонаж у виджета
        public bool CharWindow = true;       // Personal-темы: персонаж у окна
        public string CharHosts = "frame=w;widget=d;pic3=w;pic4=d"; // 3.1: у кого картинка: w — окно, d — виджет
        public bool CharFrame = true;        // Personal-темы: персонаж за рамкой окна (окно-компаньон)
        public int CharOpacity = 85;         // Personal-темы: непрозрачность персонажа, %
        public string CharLayout = "";       // 3.0.6: своё положение персонажей: "chisa.frame=dx,dy,h;kurumi.widget=…" (пусто — по умолчанию)
        public int CharInsideSize = 0;       // персонаж внутри окна: высота в % от окна (0 = по умолчанию)
        public int CharInsideAnchor = -1;    // 0 лево-низ, 1 право-низ, 2 право-верх, 3 лево-верх, 4 центр (-1 = по умолчанию темы)

        public double MaxPollRate { get { return DirectMode == 1 ? MaxPollRatePs : MaxPollRateCurl; } }
        public RouteMode RMode { get { return (RouteMode)Mode; } }
        public RouteMode RunMode { get { return (RouteMode)Mode; } }
        public void SetRunMode(RouteMode m) { Mode = (int)m; VpnMode = m == RouteMode.VpnOnly ? 1 : 0; } // VpnMode — для старых версий

        public static string Root { get { return Path.GetDirectoryName(Application.ExecutablePath); } }

        public static string Dir
        {
            get
            {
                string d = Path.Combine(Root, "config");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string Sub(string name)
        {
            string d = Path.Combine(Dir, name);
            try { Directory.CreateDirectory(d); } catch { }
            return d;
        }

        public string LogPath
        {
            get
            {
                string d = string.IsNullOrWhiteSpace(LogDir) ? Path.Combine(Root, "log") : LogDir.Trim();
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public string SingBoxPath { get { return string.IsNullOrWhiteSpace(SingBoxDir) ? Path.Combine(Root, "singbox") : SingBoxDir.Trim(); } }
        public string XrayPath { get { return string.IsNullOrWhiteSpace(XrayDir) ? Path.Combine(Root, "xray") : XrayDir.Trim(); } }
        public string ZapretPath { get { return string.IsNullOrWhiteSpace(ZapretDir) ? Path.Combine(Root, "zapret") : ZapretDir.Trim(); } }

        public TimeSpan Retention
        {
            get
            {
                switch (LogRetention) { case 0: return TimeSpan.FromHours(1); case 1: return TimeSpan.FromDays(1); case 3: return TimeSpan.FromDays(31); default: return TimeSpan.FromDays(7); }
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public void Clamp()
        {
            SideWidth = Math.Max(150, Math.Min(380, SideWidth));
            TgPort = Math.Max(1, Math.Min(65535, TgPort)); TgBufKb = Math.Max(4, Math.Min(4096, TgBufKb)); TgPool = Math.Max(0, Math.Min(32, TgPool));
            if (!TgConst.ValidSecret(TgSecret)) TgSecret = TgConst.NewSecret(); // секрет создаётся один раз и хранится
            if (string.IsNullOrWhiteSpace(TgHost)) TgHost = "127.0.0.1";
            FontBtnSize = Math.Max(7, Math.Min(14, FontBtnSize)); FontMedSize = Math.Max(7, Math.Min(14, FontMedSize));
            FontSmallSize = Math.Max(6, Math.Min(12, FontSmallSize)); FontLargeSize = Math.Max(8, Math.Min(20, FontLargeSize));
            if (!LockDefault321) { LockDefault321 = true; UiLocked = true; }
            if (!MonDefaults312) { MonDefaults312 = true; AutoStartMonitor = true; SpeedOn = true; LogProbes = true; WidgetPing = SidePing = true; WidgetSpeed = SideSpeed = SpeedChart = true; }
            if (!SpeedDefaults) { SpeedDefaults = true; WidgetPing = SidePing = true; WidgetSpeed = SideSpeed = SpeedChart = true; }
            if (!PollDefaults34) { PollDefaults34 = true; if (Math.Abs(PollRate - 1.0) < 0.01) PollRate = 0.5; } // сам не менял — новое значение
            PollRate = Math.Max(MinPollRate, Math.Min(MaxPollRate, PollRate));
            DirectPollRate = Math.Max(MinPollRate, Math.Min(MaxPollRate, DirectPollRate));
            TimeoutSec = Math.Max(2, Math.Min(30, TimeoutSec));
            DownAfterFails = Math.Max(1, Math.Min(10, DownAfterFails));
            PingCount = Math.Max(5, Math.Min(200, PingCount));
            DownloadMB = Math.Max(1, Math.Min(500, DownloadMB));
            UploadMB = Math.Max(1, Math.Min(200, UploadMB));
            Streams = Math.Max(1, Math.Min(8, Streams));
            GoodMs = Math.Max(10, Math.Min(2000, GoodMs));
            WarnMs = Math.Max(GoodMs + 10, Math.Min(5000, WarnMs));
            WidgetOpacity = Math.Max(30, Math.Min(100, WidgetOpacity));
            FailoverSec = Math.Max(3, Math.Min(120, FailoverSec));
            RecoverSec = Math.Max(3, Math.Min(600, RecoverSec));
            RetryPrimaryMin = Math.Max(0, Math.Min(240, RetryPrimaryMin));
            ExclRefreshSec = Math.Max(3, Math.Min(300, ExclRefreshSec));
            if (Mode < 0 || Mode > 2) Mode = 0;
            SocksPort = Math.Max(1024, Math.Min(65000, SocksPort)); HttpPort = Math.Max(1024, Math.Min(65000, HttpPort));
            AsInterval = Math.Max(3, Math.Min(600, AsInterval)); AsTimeout = Math.Max(2, Math.Min(30, AsTimeout)); AsFails = Math.Max(1, Math.Min(20, AsFails));
            AsMaxLatency = Math.Max(0, Math.Min(10000, AsMaxLatency)); AsSlowCount = Math.Max(1, Math.Min(50, AsSlowCount));
            if (AsPool < 0 || AsPool > 2) AsPool = 0; if (AsStrategy < 0 || AsStrategy > 2) AsStrategy = 0;
            AsBgTestMin = Math.Max(0, Math.Min(1440, AsBgTestMin)); AsReturnMin = Math.Max(1, Math.Min(1440, AsReturnMin));
            AsCooldown = Math.Max(0, Math.Min(3600, AsCooldown)); AsMaxPerHour = Math.Max(1, Math.Min(120, AsMaxPerHour)); AsSkipFailedMin = Math.Max(0, Math.Min(1440, AsSkipFailedMin));
            if (string.IsNullOrWhiteSpace(TestUrl)) TestUrl = "https://www.gstatic.com/generate_204";
            if (string.IsNullOrWhiteSpace(SpeedUrl)) SpeedUrl = "https://speed.cloudflare.com/__down?bytes=50000000";
            SpeedSec = Math.Max(3, Math.Min(60, SpeedSec)); if (ApiPort < 1024 || ApiPort > 65000) ApiPort = 10813;
            if (Theme < 0 || Theme > 4) Theme = 0;
            if (!Tr.IsLang(Lang)) Lang = "en"; // русский в 3.0 не выбирается (en / es / zh)
            if (AppNotifyEvery < 0 || AppNotifyEvery > 3) AppNotifyEvery = 0;
            AppNotifyDelayMin = Math.Max(0, Math.Min(1440, AppNotifyDelayMin));
            CharOpacity = Math.Max(20, Math.Min(100, CharOpacity));
            if (Source < 0 || Source > 1) Source = 0;
            if (string.IsNullOrWhiteSpace(Accent)) Accent = "79,140,255";
            if (LogRetention < 0 || LogRetention > 3) LogRetention = 2;
            if (GameFilter < 0 || GameFilter > 3) GameFilter = 0;
            if (DirectMode < 0 || DirectMode > 1) DirectMode = 0;
            if (string.IsNullOrWhiteSpace(CurlPath)) CurlPath = "curl.exe";
            if (string.IsNullOrWhiteSpace(ProbeUrl)) ProbeUrl = "https://www.cloudflare.com/cdn-cgi/trace";
            ProbeIdleSec = Math.Max(10, Math.Min(600, ProbeIdleSec));
            if (string.IsNullOrWhiteSpace(PingHost)) PingHost = "1.1.1.1";
            if (string.IsNullOrWhiteSpace(DirectInterface)) DirectInterface = "auto";
            if (string.IsNullOrWhiteSpace(WiresockLogLevel)) WiresockLogLevel = "info";
            if (string.IsNullOrWhiteSpace(GameTcp)) GameTcp = "1024-65535";
            if (string.IsNullOrWhiteSpace(GameUdp)) GameUdp = "1024-65535";
        }

        public Settings Clone() { return (Settings)MemberwiseClone(); }
        // перенести все значения в ЭТОТ объект: объект настроек один на всё приложение — фоновые потоки, взявшие его раньше,
        // не сохранят потом устаревшую копию поверх новых значений (так «откатывались» шрифты и др. после перезапуска)
        public void CopyFrom(Settings n) { foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance)) f.SetValue(this, f.GetValue(n)); }

        static string Esc(string s) { return s.Replace("\\", "\\\\").Replace("\r", "").Replace("\n", "\\n"); }
        static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] == 'n' ? '\n' : s[i]); }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static bool MigratedFrom2;
        public static Settings Load()
        {
            var st = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        var f = typeof(Settings).GetField(line.Substring(0, eq).Trim(), BindingFlags.Public | BindingFlags.Instance);
                        if (f == null) continue;
                        string v = line.Substring(eq + 1);
                        try
                        {
                            if (f.FieldType == typeof(string)) f.SetValue(st, Unesc(v));
                            else if (f.FieldType == typeof(int)) f.SetValue(st, int.Parse(v, CultureInfo.InvariantCulture));
                            else if (f.FieldType == typeof(double)) f.SetValue(st, double.Parse(v, CultureInfo.InvariantCulture));
                            else if (f.FieldType == typeof(bool)) f.SetValue(st, v.Trim() == "1" || v.Trim().ToLower() == "true");
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (!st.V3Migrated)
            {
                // 2.x: Mode был только «какие списки редактируем», запускался VpnMode (0 DPI/VPN, 1 только VPN)
                st.Mode = st.VpnMode == 1 ? 1 : 0;
                st.V3Migrated = true;
                MigratedFrom2 = File.Exists(FilePath); // был settings.ini от 2.x (а не чистая установка)
            }
            st.Clamp();
            return st;
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var f in typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    object v = f.GetValue(this);
                    string s;
                    if (v is double) s = ((double)v).ToString("0.###", CultureInfo.InvariantCulture);
                    else if (v is bool) s = (bool)v ? "1" : "0";
                    else if (v is int) s = ((int)v).ToString(CultureInfo.InvariantCulture);
                    else s = Esc(v == null ? "" : v.ToString());
                    sb.Append(f.Name).Append('=').Append(s).Append("\r\n");
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public List<string> SiteList()
        {
            var l = new List<string>();
            foreach (var s in Sites.Split(new[] { '\n', '\r', ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string u = s.Trim();
                if (u.Length == 0) continue;
                if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) u = "https://" + u;
                l.Add(u);
            }
            return l;
        }

        public List<string> DnsList()
        {
            var l = new List<string>();
            foreach (var s in DnsDomains.Split(new[] { '\n', '\r', ' ', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)) l.Add(s.Trim());
            return l;
        }
    }

    // Списки приложений и исключений для каждого блока. Хранятся как текстовые файлы в config\lists.
    public static class AppLists
    {
        public const string HybridApps = "hybrid-apps";        // DPI/VPN: через VPN, при сбое — через DPI
        public const string VpnOnlyApps = "vpnonly-apps";      // всегда только через VPN (даже при аварийном DPI)
        public const string HybridExcl = "hybrid-exclude";     // исключения блока DPI/VPN
        public const string VpnOnlyExcl = "vpnonly-exclude";   // исключения режима «Только VPN»
        public const string DpiOnlyExcl = "dpionly-exclude";
        public const string DpiDomains = "zapret-general-user";
        public const string DpiApps = "dpi-apps";
        // списки вкладки «Серверы» (xray / TUN), независимые от VPN на «Подключении»
        public const string SrvApps = "servers-apps";           // через сервер xray (TUN)
        public const string SrvExcl = "servers-exclude";        // мимо сервера, напрямую
        public const string SrvDpiApps = "servers-dpi-apps";    // мимо сервера, под DPI (winws включается сам)
        public const string SrvDpiExcl = "servers-dpi-exclude"; // мимо DPI               // приложения, которые идут мимо VPN под DPI   // домены через DPI = lists\list-general-user.txt zapret
        public static string ZapretLists = "";
        const string Placeholder = "domain.example.abc";   // исключения режима «Только DPI»

        public static readonly string[] All = { HybridApps, VpnOnlyApps, HybridExcl, VpnOnlyExcl, DpiOnlyExcl, SrvApps, SrvExcl, SrvDpiApps, SrvDpiExcl };
        public static readonly string[] Servers = { SrvApps, SrvExcl, SrvDpiApps, SrvDpiExcl };
        public static bool IsServers(string key) { return Array.IndexOf(Servers, key) >= 0; }

        // Список приложений из буфера обмена: строки, запятые, точки с запятой, табы; кавычки снимаются.
        // Строка без пути с несколькими словами («a.exe b.exe») делится по пробелам.
        public static List<string> ParseClipboard(string text)
        {
            var res = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return res;
            foreach (var raw in text.Split(new[] { '\r', '\n', ',', ';', '\t', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim().Trim('"', '\'', '`').Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.IndexOf('\\') < 0 && line.IndexOf('/') < 0 && line.Contains(" ") ? line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new[] { line };
                foreach (var x in parts) { var v = x.Trim().Trim('"', '\''); if (v.Length > 0 && v.Length < 400 && !res.Contains(v, StringComparer.OrdinalIgnoreCase)) res.Add(v); }
            }
            return res;
        }

        // Первый запуск новой версии: списки «Серверов» берутся из «Подключения», как раньше работал TUN
        public static void SeedServers()
        {
            if (Servers.Any(k => File.Exists(FileOf(k)))) return;
            Set(SrvApps, Get(HybridApps).Concat(Get(VpnOnlyApps)));
            Set(SrvExcl, Get(HybridExcl));
            Set(SrvDpiApps, new string[0]);
            Set(SrvDpiExcl, new string[0]);
        }

        public static string Title(string key)
        {
            switch (key)
            {
                case HybridApps: return Tx("DPI/VPN — приложения");
                case VpnOnlyApps: return Tx("Только через VPN — приложения");
                case HybridExcl: return Tx("DPI/VPN — исключения");
                case VpnOnlyExcl: return Tx("Только VPN — исключения");
                case DpiOnlyExcl: return Tx("Только DPI — исключения");
                case DpiApps: return Tx("DPI — приложения");
                case SrvApps: return Tx("Серверы — через сервер");
                case SrvExcl: return Tx("Серверы — исключения");
                case SrvDpiApps: return Tx("Серверы — через DPI");
                case SrvDpiExcl: return Tx("Серверы — мимо DPI");
            }
            return key;
        }

        static readonly object lk = new object();
        public static event Action<string> Changed;

        static string FileOf(string key) { return key == DpiDomains ? Path.Combine(ZapretLists, "list-general-user.txt") : Path.Combine(Settings.Sub("lists"), key + ".txt"); }

        public static List<string> Get(string key)
        {
            lock (lk)
            {
                try
                {
                    string f = FileOf(key);
                    if (!File.Exists(f)) return new List<string>();
                    return File.ReadAllLines(f, Encoding.UTF8).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith("#") && x != Placeholder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }
                catch { return new List<string>(); }
            }
        }

        public static void Set(string key, IEnumerable<string> items)
        {
            lock (lk)
            {
                try
                {
                    var arr = items.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (key == DpiDomains) { Directory.CreateDirectory(ZapretLists); if (arr.Count == 0) arr.Add(Placeholder); } // пустой список zapret трактует иначе
                    File.WriteAllLines(FileOf(key), arr.ToArray(), new UTF8Encoding(false));
                }
                catch { }
            }
            var h = Changed; if (h != null) try { h(key); } catch { }
        }

        // 5 последних добавленных приложений — показываются первыми в «+»
        static string RecentFile { get { return Path.Combine(Settings.Dir, "recent.txt"); } }
        public static List<string> Recent() { try { return File.Exists(RecentFile) ? File.ReadAllLines(RecentFile).Where(x => x.Trim() != "").Take(5).ToList() : new List<string>(); } catch { return new List<string>(); } }
        public static void PushRecent(string item)
        {
            if (item.Contains(".") && !item.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !item.Contains("\\")) return; // домены не нужны
            var l = Recent(); l.RemoveAll(x => x.Equals(item, StringComparison.OrdinalIgnoreCase)); l.Insert(0, item);
            try { File.WriteAllLines(RecentFile, l.Take(5).ToArray()); } catch { }
        }

        public static bool Add(string key, string item)
        {
            var l = Get(key);
            if (l.Any(x => x.Equals(item, StringComparison.OrdinalIgnoreCase))) return false;
            l.Add(item); Set(key, l); return true;
        }

        public static void Remove(string key, IEnumerable<string> items)
        {
            var rm = new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
            // вместе с приложением удаляются и его домены
            Set(key, Get(key).Where(x => !rm.Contains(x) && !(IsChild(x) && rm.Contains(ParentOf(x)))));
        }

        // ---- домены под приложением: строка «приложение|домен» в списке Include DPI ----
        // brave.exe в Include VPN + youtube.com под brave.exe в Include DPI → весь brave через VPN, кроме youtube.com — он через DPI
        public const char Sep = '|';
        public static readonly string[] DomainKeys = { DpiApps, SrvDpiApps };
        public static bool HasDomains(string key) { return Array.IndexOf(DomainKeys, key) >= 0; }
        // где хранятся домены для приложений этого списка: Include VPN / Strict → Include DPI, Servers Include → Servers Include DPI
        public static string DomKey(string key)
        {
            if (HasDomains(key)) return key;
            if (key == HybridApps || key == VpnOnlyApps) return DpiApps;
            if (key == SrvApps) return SrvDpiApps;
            return null;
        }
        public static bool IsChild(string s) { return s != null && s.IndexOf(Sep) > 0; }
        public static string ParentOf(string s) { int i = s.IndexOf(Sep); return i > 0 ? s.Substring(0, i) : s; }
        public static string ChildOf(string s) { int i = s.IndexOf(Sep); return i > 0 ? s.Substring(i + 1) : s; }

        // приложение попадает под список так же, как у WireSock: запись — подстрока имени или пути процесса
        public static bool InList(List<string> list, string app)
        {
            string path = null; try { path = AppIcons.KnownPath(app); } catch { }
            foreach (var x in list)
            {
                if (IsChild(x)) continue;
                if (x.Equals(app, StringComparison.OrdinalIgnoreCase) || app.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (path != null && path.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        // только приложения, без доменов
        public static List<string> Apps(string key) { return Get(key).Where(x => !IsChild(x)).ToList(); }

        // ---- 3.0: списки VPN принимают только приложения/процессы ----
        // запись — приложение: путь, *.exe или имя без точки; домен/IP в списке приложений остаётся, но серый и в движок не идёт
        public static bool IsAppEntry(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || IsChild(s)) return false;
            s = s.Trim();
            if (s.Contains("\\") || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return true;
            return !s.Contains(".") && !s.Contains(":") && !s.Contains("/");
        }
        public static List<string> AppsOnly(string key) { return Get(key).Where(IsAppEntry).ToList(); }
        public static string NotAppReason(string s) { return IsAppEntry(s) || s == null ? null : Tx("WireGuard (WireSock) понимает только приложения — домен заработает через подписку или туннель VLESS / VMess / Trojan"); }
        // исключение сайтов из DPI («Не трогать эти сайты»): ключ dpionly-exclude, в UI только домены/IP; процессы — легаси, работают фоном
        public static List<string> DpiSitesExcl() { return Get(DpiOnlyExcl).Where(x => !IsAppEntry(x) && !IsChild(x)).ToList(); }
        public static void SetDpiSitesExcl(IEnumerable<string> sites)
        {
            var legacy = Get(DpiOnlyExcl).Where(IsAppEntry);
            Set(DpiOnlyExcl, legacy.Concat(sites));
        }

        // 3.0: домены из карточек приложений («приложение|домен» в dpi-apps / servers-dpi-apps) → общий список DPI
        public static List<string> MigrateAppDomains()
        {
            var moved = new List<string>();
            foreach (var key in DomainKeys)
            {
                var all = Get(key);
                var kids = all.Where(IsChild).ToList();
                if (kids.Count == 0) continue;
                moved.AddRange(kids.Select(ChildOf).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
                Set(key, all.Where(x => !IsChild(x)));
            }
            moved = moved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (moved.Count > 0)
            {
                var sites = Get(DpiDomains);
                Set(DpiDomains, sites.Concat(moved.Where(m => !ExclusionSet.IsIp(m))).Distinct(StringComparer.OrdinalIgnoreCase));
            }
            return moved;
        }

        // для показа: каждое приложение, сразу под ним его домены (в Include VPN — домены из Include DPI, чтобы было видно)
        public static List<string> Display(string key)
        {
            var all = Get(key); var res = new List<string>();
            string dk = DomKey(key);
            if (dk != null && dk != key)
            {
                var doms = Domains(dk);
                foreach (var a in all) { res.Add(a); res.AddRange(doms.Where(d => d.Key.Equals(a, StringComparison.OrdinalIgnoreCase)).Select(d => d.Key + Sep + d.Value)); }
                return res;
            }
            foreach (var a in all.Where(x => !IsChild(x)))
            {
                res.Add(a);
                res.AddRange(all.Where(x => IsChild(x) && ParentOf(x).Equals(a, StringComparison.OrdinalIgnoreCase)));
            }
            return res;
        }

        // пары (приложение, домен), у которых приложение есть в списке
        public static List<KeyValuePair<string, string>> Domains(string key)
        {
            var all = Get(key);
            var apps = new HashSet<string>(all.Where(x => !IsChild(x)), StringComparer.OrdinalIgnoreCase);
            return all.Where(x => IsChild(x) && apps.Contains(ParentOf(x))).Select(x => new KeyValuePair<string, string>(ParentOf(x), ChildOf(x))).ToList();
        }

        public static bool AddDomain(string key, string app, string domain)
        {
            domain = (domain ?? "").Trim().Trim('"', '\'').TrimStart('*', '.').ToLowerInvariant();
            if (domain.StartsWith("http://")) domain = domain.Substring(7);
            if (domain.StartsWith("https://")) domain = domain.Substring(8);
            int sl = domain.IndexOf('/'); if (sl > 0 && !ExclusionSet.IsIp(domain)) domain = domain.Substring(0, sl);
            if (domain.StartsWith("www.")) domain = domain.Substring(4);
            if (domain.Length == 0 || domain.IndexOf(Sep) >= 0 || (!domain.Contains(".") && !domain.Contains(":"))) return false;
            string line = app + Sep + domain;
            var l = Get(key);
            if (l.Any(x => x.Equals(line, StringComparison.OrdinalIgnoreCase))) return false;
            if (!l.Any(x => x.Equals(app, StringComparison.OrdinalIgnoreCase))) l.Add(app);
            l.Add(line); Set(key, l); return true;
        }

        // переименование приложения тянет за собой его домены
        public static void Rename(string key, string old, string now)
        {
            now = now.Trim();
            Set(key, Get(key).Select(x =>
                x.Equals(old, StringComparison.OrdinalIgnoreCase) ? now :
                IsChild(old) ? x :
                IsChild(x) && ParentOf(x).Equals(old, StringComparison.OrdinalIgnoreCase) ? now + Sep + ChildOf(x) : x));
        }
    }
}
