# RouteX (formerly NetProbe) — context for Claude

The app was renamed from NetProbe to RouteX. User-visible names are RouteX (window, tray, texts, `RouteX.exe`, `RouteX.ico`, GitHub repo `WhereChixiaMeet/RouteX`). Internal names deliberately stay `NetProbe` for compatibility with existing installs: C# namespace, `C:\ProgramData\NetProbe`, `netprobe-vpn.exe` / `netprobe-direct.exe`, firewall rule `NetProbe KillSwitch`, TUN adapter `NetProbe TUN`, mutex / window messages, autostart task name, toast AUMID, backup marker `app=NetProbe`, User-Agent. Releases ship `RouteX.exe` plus a copy named `NetProbe.exe` so pre-rename versions can still self-update.

Windows desktop app (C# WinForms, .NET Framework 4.8, single exe, runs as admin) that combines:
- a **VPN router** on top of WireSock (WireGuard with per-app split tunneling),
- **DPI bypass** via zapret (`winws.exe` + WinDivert),
- **Servers** — a v2rayN-like client on xray-core with subscriptions and auto-switch, optional **TUN** via sing-box,
- the original **NetProbe network monitor** (two channels: VPN / Direct; latency, loss, tests, sites).

UI languages: English (default), Español, 中文 (`Lang.En.cs` / `Lang.Es.cs` / `Lang.Zh.cs`; Russian literals in code are only keys, Russian is not selectable). All user data lives next to the exe.

## Build
```
src\build.cmd
```
Roslyn `csc.dll` from the installed .NET SDK (`C:\Program Files\dotnet\sdk\<latest>\Roslyn\bincore\csc.dll`), referencing the .NET Framework 4.8 assemblies in `C:\Windows\Microsoft.NET\Framework64\v4.0.30319`. Output: `..\RouteX.exe`. `app.manifest` sets `requireAdministrator`.
- If NetProbe is running, Roslyn renames the locked exe to `<guid>_NetProbe.exe` and fails; run build again. Start the new exe with `--replace` to make the running copy quit (WM "NetProbe_QuitMe_7f3a").
- For UI testing without UAC, build a copy with a manifest using `asInvoker` into a temp folder.
- Hidden dev flags: `--shots <dir>` (PNG of every page in all 5 themes for the current language + Configs→DPI→Sites + widget, then exit; ignores the single-instance mutex), `--selftest <file>` (config generation, i18n coverage, protocol link/config check), `--xraytest <file>`.
- Test copy without UAC and without touching the user's profile: `_tools\testshots.cmd` pattern — asInvoker build into `%TEMP%\NetProbeTest`, settings.ini with AutoConnect=0, Autorun=0, CheckUpdates=0 and **different proxy ports** (otherwise `SysProxy.ClearIfStale` turns off the live copy's system proxy).

## Folder layout (runtime)
```
RouteX.exe, RouteX.ico   (NetProbe.exe in installs updated from a pre-rename version)
zapret\      winws.exe, WinDivert, lists\ (bundled from Flowseal/zapret-discord-youtube), version.txt
xray\        xray.exe + geo*.dat (installed/updated from inside the app)
singbox\     sing-box.exe (TUN core, installed from inside the app)
config\      settings.ini, lists\*.txt, tunnels\*.conf, dpi\*.txt (+ .stock hashes), servers.json,
             subscriptions.json, routing.json (rule sets of the Configs rule editor), icons.txt, iconpaths.txt, recent.txt, runtime\ (generated configs, downloads)
log\         YYYY-MM-DD.log (folder configurable)
```
Exception: curl copies used as probes live in `C:\ProgramData\NetProbe\bin\` (`netprobe-vpn.exe`, `netprobe-direct.exe`) — ASCII path, and that path is referenced by WireSock rules.

## Source map (`src\`)
| File | What |
|---|---|
| `Program.cs` | entry, single-instance mutex, `--replace/--shots/--selftest` |
| `MainForm.cs` | shell (sidebar, pages), tray + widget, monitoring glue, app icon, page background, theme rebuild (`RebuildUi`, WM_SETREDRAW) |
| `Router.cs` | **state machine** for VPN/DPI: Off, Connecting, Vpn, Backup, DpiFailover, VpnDown, DpiOnly; manual DPI button (`ManualDpi`) |
| `Tunnel.cs` | WireGuard `.conf` store, runtime config with `AllowedApps/DisallowedApps`, `WiresockRunner` (`wiresock-client.exe run -config`, Ctrl+C stop) |
| `Zapret.cs` | DPI strategies (`config\dpi\*.txt`, import from `general*.bat`), `ZapretRunner` (winws args, placeholders `%BIN% %LISTS% %GameFilterTCP% %GameFilterUDP%`), exclusions |
| `Procs.cs` | process snapshot, TCP table (remote IPs per pid), kill switch (Windows Firewall rules "NetProbe KillSwitch") |
| `Xray.cs` | share-link parsing (vless/vmess/trojan/ss/socks; reality/tls/ws/grpc/xhttp/h2), xray config builder, `ServerStore`, `DelayTester` |
| `XrayManager.cs` | xray connection + **auto-switch** + system proxy + TUN start; port conflict handling |
| `SingBox.cs` | TUN config: `process_path_regex` rules, Include → xray socks, Exclude → direct, app+domain overrides → direct |
| `RouteCheck.cs` | «Маршрут» window (Connection page button, right-click → Check route… in lists): app + domain → VPN / DPI / direct / server / blocked with the reason, plus live TCP connections with route; decided from what actually runs (`config\runtime\tunnel.conf` AllowedApps/DisallowedApps/DisallowedIPs, winws hostlists/ipsets of the strategy, xray proxy / TUN lists, kill switch), not from local addresses (WireSock transparent mode keeps the LAN address) |
| `DomainRoutes.cs` | domains under apps in Include DPI: CDN families, DNS / DNS-cache IP learning for WireSock `DisallowedIPs` |
| `Updater.cs` | GitHub latest-release download/extract for xray, zapret, sing-box (into `config\runtime`) |
| `Backup.cs` | export/import all settings as one JSON |
| `Settings.cs` | `settings.ini` (reflection over public fields), `AppLists` (per-mode lists, recent apps) |
| `Pages*.cs` | Connection, Servers, Configs (VPN tunnels + DPI strategies/lists), Monitor/Tests/Sites, Log, Settings |
| `Ui.cs`, `Controls2.cs`, `Controls3.cs`, `ThemedList.cs`, `ScrollHost.cs`, `TabStrip.cs` | custom-drawn controls (no native look): buttons, toggle, combo, checkbox, spin, text box frame, list with icons + thin scrollbar, scroll host |
| `Theme.cs` | palettes Dark (NetProbe 1.x navy), Light, Classic (light, thin borders); accent colour |
| `Lang.cs`, `Lang.En.cs` | i18n: code strings are Russian, wrapped in `Tx("...")`; English in `Lang.En.cs` keyed by the Russian literal |
| `Net.cs`, `Monitor.cs`, `Tests.cs` | monitoring probes (curl), channel stats, tests, Logger (per-day files, retention) |

## 3.0 model (overrides the 2.x notes below where they differ)
- Tabs: Routing (`BuildConnectPage`), Subscriptions (`BuildServersPage`), Monitor, Configs, Tests, Sites, Log, Settings. `PgMonitor=2`, `PgConfigs=3`.
- Top segment **ROUTE | VPN | DPI** (`ModeSegment`, `Sources.cs`) on Routing and Subscriptions = `Settings.Mode` = `RunMode` (0 DPI/VPN, 1 VPN only, 2 DPI only; `VpnMode` kept in sync for old versions). Click another chip = switch + connect, active chip = disconnect.
- Sources (`Sources.cs`): tunnels ≥ 1 → Primary VPN, subscriptions blocked; no tunnels → subscriptions; `HybridMode` → manual `SourceSwitch` (Primary VPN ⇄ Subscriptions). "No tunnel assigned" notice once per install (`VpnNoTunnelWarned`).
- ROUTE: winws runs **together** with the tunnel (`Router.HybridDpiWanted/EnsureHybridDpi`); DPI button/toggle off = `DpiForcedOff` (persisted as `DpiButton`).
- Lists: VPN lists accept apps only (`AppLists.IsAppEntry/AppsOnly`); domains there are gray (`ThemedList.DimReason`) and never reach the engine. DPI is global; winws exclusions = `dpionly-exclude` ("Don't touch these sites", Configs → DPI → Sites; legacy process entries still applied via TCP snapshot, hidden in UI). Per-app domains removed; migrated once into `list-general-user.txt` (`DomainsMigrated`). TUN of subscriptions uses the Routing lists.
- Protocols (`Proto.cs`, `PagesProto.cs`): tunnel = `tunnels\<name>.conf` (WireGuard → WireSock) or `tunnels\<name>.json` (VLESS/VMess/Trojan → xray on `TunnelSocksPort` + sing-box TUN). `Router.Wg` is a `TunnelRunner` wrapping both; router states, failover, kill switch are identical.
- Updates: one notification per version (`AppNotifiedTag`), frequency `AppNotifyEvery` (now/day/week/month), delay after `published_at` (`AppNotifyDelayOn/Min`, default 15 min), Windows toast via WinRT reflection (`Toast.cs`, AUMID `WhereChixiaMeet.NetProbe`), balloon fallback. After an update: "restart manually" notice + sidebar button (`UpdatedFrom`, `--reapply`).
- Monitor: Session / Day / Week; a `STAT` line per minute goes to the daily log (`History.cs`), Day/Week are read from the logs.
- Themes: Dark, Light, Classic, **Chisa (Personal)**, **Kurumi Tokisaki (Personal)** (`Theme.cs`, `Skin.cs`, `CharOverlay.cs`, `Chars.cs`). Character art is local only: `config\themes\<chisa|kurumi>\widget.png|frame.png|window.png` — never commit it or embed it in the exe.

## Routing model
- **DPI / VPN** (default): Include apps → WireSock tunnel. Probe `netprobe-vpn.exe` is always in the tunnel; if it gets no answer for N s (FailoverSec, default 7): primary → backup VPN (if the block is on) → DPI. In DPI state the tunnel stays up only for **Strict VPN** apps + probe; when VPN is stable RecoverSec s → back to VPN, DPI stops. Kill switch blocks Strict VPN apps when no VPN is reachable.
- **VPN Only**: primary → backup → VpnDown (kill switch, retry every 30 s).
- **DPI Only**: winws only. Lists: Include (DPI apps; may overlap VPN Include — see domains under an app below), Domains (`zapret\lists\list-general-user.txt`), Exclude.
- Mode cards only choose which mode's lists are being edited; they never reconnect. `Settings.Mode` = edited view, `Settings.VpnMode`/`RunMode` = how the VPN button runs (DPI/VPN or VPN Only). Router uses `RunMode`.
- Custom background: cards become semi-transparent (`Theme.Translucent`, `CardOpacity`); custom controls paint `Gfx.Backdrop` (page image + translucent card ancestors) instead of clearing with the parent colour.
- **DPI button** next to VPN on the Connection page: winws in parallel with VPN; not stopped by disconnect.
- Lists per mode: `hybrid-apps` (Include), `vpnonly-apps` (Strict VPN), `hybrid-exclude`, `vpnonly-exclude`, `dpionly-exclude`, `dpi-apps`. Matching = substring of process name/path (WireSock semantics). `netprobe-vpn.exe` is shown as a built-in Include row but not stored.
- **Domains under an app** (right-click in Include DPI → "Domains via DPI…"): stored as `app|domain` lines in `dpi-apps` / `servers-dpi-apps`. UI: domain rows are also shown (read-only mirror, deletable) under the app in Include VPN / Strict / Servers Include, with a "→ DPI" tag; the right-click item works there too and writes to the Include DPI list. Include VPN and Include DPI no longer auto-remove each other: app in both = everything via VPN/server except its domains, which go direct under winws. WireSock has no per-app domain rules, so the domain (expanded to its CDN family in `DomainRoutes.Expand`, e.g. youtube → googlevideo/ytimg) is turned into IPs (YouTube family → Google's own subnets `DomainRoutes.GoogleNets`, because googlevideo cache hosts are unknown in advance and DoH in browsers hides them from the Windows DNS cache; plus DNS at tunnel start + Windows DNS cache every 60 s, kept in `config\runtime\domain-ips.txt`) and put into `DisallowedIPs` — this applies to all tunnelled apps; new IPs restart the tunnel at most every `DomainReapplyMin` min. winws runs alongside VPN while such domains exist; they are added to the hostlist via `config\runtime\list-general-netprobe.txt` (replaces `list-general-user.txt` in strategy args). In TUN (sing-box) the rule is exact (process + domain_suffix → direct); in proxy mode the domains go direct in xray for all apps.
- DPI exclusions by process work through current TCP connections → `--ipset-exclude` file (winws cannot filter by process).
- **Servers are independent of the VPN button**: own lists `servers-apps` (Include → xray), `servers-exclude` (direct), `servers-dpi-apps` (direct + winws started while xray is connected), `servers-dpi-exclude` (DPI exclusions). Seeded once from the Connection lists. Lists apply in TUN mode (sing-box); TUN is blocked while the WireSock tunnel runs. Router keeps winws running while `Router.DpiHold` (= `xm.DpiWanted`) is true.
- Servers page: tabs All / Active (answered last test) / Favorite / Manual / one per subscription (⟳ updates only the open subscription). Columns GEO (flag in name, else exit country via cloudflare trace during the test), PORT, SPEED (curl download through the server, `SpeedUrl`), TOTAL ↓ (xray stats via `api.listen` on `ApiPort`, polled every 10 s with `xray api statsquery -reset`).
- Delay/speed tests: servers run in chunks in one test xray; if xray refuses the config, the chunk is split in halves until the bad server is isolated (before, one bad server made the whole chunk "none").
- "+" lists have a clipboard button (Ctrl+V): one app per line / comma / semicolon.

## Speed of the xray path (3.4.11)
- xray sniffing has `routeOnly: false` (as v2rayN): the server gets the sniffed domain and resolves it near itself (right CDN node). DoH hosts are in `domainsExcluded`.
- xray `domainStrategy`: AsIs in proxy mode; IPIfNonMatch only in TUN with BypassRu (the override hides the IP from geoip:ru; the lookup hits the Windows / sing-box DNS cache).
- sing-box remote DNS is DoH (`type: https`, 1.1.1.1 via proxy), not TCP per query.
- `TunRejectQuic` (default on): every sing-box rule to `proxy` gets a twin before it rejecting UDP 443 (`no_drop`), so browsers fall back to TCP at once; `RoutingSnapshot` skips these twins (`SingBoxConfig.IsQuicGuard`).

## Rule editor (Configs, v2rayN-style; off by default)
- 3.4.6 removed the 3.4.3 table editor; it came back in v2rayN form. Switch = `Settings.RuleSetsEnabled` (new name, so an old `ConfigRulesEnabled=1` stays ignored). Off → standard Include/Exclude + RU sites direct, exactly as 3.4.6+.
- `ConfigRouting.cs` / `PagesRoutingRules.cs`: rule sets in `config\routing.json` (v2rayN JSON keys: remarks, outboundTag, port, network, protocol, inboundTag, domain, ip, process, ruleType, enabled). Only the active set is used; first match wins; unmatched → proxy (as v2rayN). Domain / IP / Process of a rule are separate sing-box rules sharing port/network/protocol; plain domain = keyword. Domain strategy AsIs / IPIfNonMatch / IPOnDemand via sing-box `resolve`.
- Windows: Routing settings (sets, strategy, built-in sets, RU geodata, RU sites detect) → Rule set (rules, import file/clipboard/URL, export, move) → Rule details.
- RU geodata (runetfreedom) is stored as `xray\geosite-ru.dat` / `geoip-ru.dat`; `GeoData` looks in the Xray file first, then in these. The 3.4.3 `ConfigRulesJson` table is migrated once into an inactive "My rules (3.4)" set.

## Behaviour defaults
Auto-connect on start and Windows autostart (Task Scheduler, highest privileges, `--minimized`; task name per copy) are ON by default. Everything autosaves (no Save buttons). Update checks for zapret/xray/sing-box are configurable (interval, per component) with tray notification and a sidebar badge.

## Self-update (SelfUpdate.cs)
- Source: GitHub Releases of `WhereChixiaMeet/RouteX` (renamed from `NetProbe`; GitHub redirects the old name), `/releases/latest`.
- **Release format the updater expects:** tag `vX.Y.Z` greater than `MainForm.Version` (bump `Version` in `MainForm.cs` and `AssemblyInfo.cs` before building a release), with an asset named exactly `RouteX.exe` (plus `NetProbe.exe`, the same file, for pre-rename versions), or a `.zip` (name without "source"/"src") that contains `RouteX.exe`. The release workflow (`.github/workflows/release.yml`) builds and attaches both; if a draft release for the tag exists it fills and publishes that draft.
- When a newer release exists, an "Update to vX" button appears at the top of the sidebar; Settings → Updates has "Check NetProbe updates". NetProbe releases are checked on their own timer (`appUpdTimer`): 20 s after start, then every hour, retry in 2 min after a network error (only the "Check automatically" toggle applies, not the zapret/xray interval). A failed check never hides an already shown button. New-version notifications: see the 3.0 model above (`UpdNotify` default on).
- **VPN does not drop during an update:** wiresock-client / winws / xray / sing-box are started without stdout redirection, so they survive NetProbe exiting. The app writes `config\runtime\handoff.ini` (router state, tunnel, strategy, manual DPI, xray on/off), exits without stopping them, a hidden PowerShell waits for the PID, copies the new exe over the old one and starts it with `--updated`; the new instance calls `Router.Adopt` / `XrayManager.Adopt` to attach to the running processes instead of reconnecting.

## UI preferences of the owner (keep these)
- Look must match NetProbe 1.x: dark navy palette, blue accent (accent is user-selectable), purple VPN / cyan Direct series.
- Short labels, no long descriptions; tooltips instead of subtitles.
- All controls custom-drawn and consistent (no native white combos, spinners, checkboxes, scrollbars).
- Themes: Dark, Light, Classic (light/contrast, not Win95-heavy). Theme switch must not flicker.
- Include/Exclude live on the Connection tab; configs on a separate Configs page; "+" opens the built-in process picker (recent apps first, icons, manual entry).
- App icon = tray icon = sidebar logo; custom icon and background image in Settings.
- Keep everything inside the app folder.
- When adding UI strings: write Russian `Tx("...")` and add the English pair to `Lang.En.cs` (duplicate keys crash the app at start).

## Known limitations / not verified
- Live testing of xray/sing-box/TUN on real servers was not done by the original author's session.
- WireSock is single-instance: TunnlTo's `wiresock-client-service` must be stopped (the app offers to).
- winws: QUIC/UDP of excluded processes is not covered.
- System proxy only affects apps that honour it; use TUN for the rest.
