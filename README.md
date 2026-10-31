# RouteX
*(formerly NetProbe)*

Version 3.4.9: steady ping on one site while the window is open; site rotation and passive VPN checks only in the background. See [release notes](docs/releases/v3.4.9.md).

### MANUAL RESTART REQUIRED AFTER UPDATE ###

Windows protocol router: WireGuard · VLESS · VMess · Trojan, DPI bypass, MTProxy (TG) per-app split routing with automatic failover and a built-in network monitor.

## Requirements
- Windows 10/11, .NET Framework 4.8 (built in).
- Runs as administrator (WinDivert, TUN and the firewall kill switch need it).
- WireGuard tunnels use the WireSock VPN Client 1.4.x engine. VLESS / VMess / Trojan tunnels and subscriptions use xray-core + sing-box (TUN), both installable from inside the app.

## Folder layout
```
RouteX.exe       (NetProbe.exe in installs updated from NetProbe — same app)
zapret\          winws.exe, WinDivert, lists (copied from zapret-discord-youtube)
xray\, singbox\  cores, installed/updated from the app
config\          settings.ini, tunnels\*.conf|*.json, dpi\*.txt, lists\*.txt, themes\, runtime\
log\             one file per day: YYYY-MM-DD.log (folder can be changed in Settings)
src\             sources, build.cmd
```

## Routing modes (segment ROUTE | VPN | DPI)
- **ROUTE** (DPI/VPN) — Include apps go through the tunnel, DPI handles everything outside it at the same time. If the tunnel probe gets no answer for N seconds: primary → backup → DPI failover; back to the tunnel once it is stable again.
- **VPN** — tunnel only (primary → backup). On outage the kill switch blocks Strict VPN apps.
- **DPI** — winws only, no tunnel.

Clicking the active chip disconnects; clicking another chip switches mode and connects.

## Protocols
The protocol is a property of each tunnel config (Configs tab): **WireGuard** (via WireSock), **VLESS**, **VMess**, **Trojan** (via xray + sing-box TUN). Every protocol works the same way on top: same router states, lists, kill switch, DPI failover and recovery. Links (`vless://`, `vmess://`, `trojan://`) fill the fields automatically.

## Sources
- At least one tunnel → Primary VPN is the source, subscriptions are off.
- No tunnels → subscriptions (xray) are the source.
- **Hybrid mode** (Settings) → both, with a manual "Primary VPN ⇄ Subscriptions" switch. Primary VPN always has priority.

## Lists (Routing tab — apps only)
- **Include** — apps in the tunnel (empty = all traffic).
- **Strict VPN** — always in the tunnel, never DPI; blocked by the kill switch when the tunnel is down.
- **Exclude** — bypass the tunnel.
Disconnect the VPN before adding, deleting, pasting, renaming or moving entries in TUN routing lists. Changes take effect on the next connection.
DPI is global: domains for DPI and "don't touch these sites" live on Configs → DPI → Sites. Priority: Strict VPN > domain > app.

## Build
```
src\build.cmd
```
Uses Roslyn from the installed .NET SDK, targets .NET Framework 4.8.

## Languages
English · Español · 中文 (Settings → Language, applied without restart).

## Known limits
- winws cannot filter by process; legacy process exclusions from 2.x work through current TCP connections (QUIC/UDP not covered).
- WireSock cannot run twice: stop `wiresock-client-service` (TunnlTo) first — RouteX offers to do it.

## Known Issues
- the application breaks the routing configuration during an update (SAME ROUTE issue)
- minor UI issues using custom themes
