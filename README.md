# NetProbe 2.0

VPN (WireSock) + DPI (zapret) router with automatic failover, per-app split tunneling and the NetProbe network monitor.
Interface language: English by default, Russian in Settings.

## Requirements
- Windows 10/11, .NET Framework 4.8 (built in).
- WireSock VPN Client 1.4.x — installer: `TunnlTo\wiresock_installers\wiresock-vpn-client-x64-1.4.7.1.msi`.
- Runs as administrator (WireSock, WinDivert and the firewall kill switch need it).

## Folder layout
```
NetProbe.exe
zapret\          winws.exe, WinDivert, lists (copied from zapret-discord-youtube)
config\          settings.ini, tunnels\*.conf, dpi\*.txt, lists\*.txt, runtime\
log\             one file per day: YYYY-MM-DD.log (folder can be changed in Settings)
src\             sources, build.cmd
```

## Modes
- **DPI / VPN** — traffic of the Include apps goes through the VPN. If the VPN probe gets no answer for N seconds (Switch after, default 7), NetProbe switches: primary VPN → backup VPN (if the block is on) → DPI. While on DPI the tunnel stays up for Strict VPN apps; once the VPN is stable for M seconds (Return after) it switches back and stops DPI.
- **VPN Only** — tunnel only (primary → backup). On outage the kill switch blocks Strict VPN apps.
- **DPI Only** — winws only, the tunnel is never started.

## Lists (Connection tab)
- **Include** — apps in the VPN (empty = all traffic).
- **Strict VPN** — always in the tunnel, never DPI/direct; blocked by the kill switch when no VPN is reachable.
- **Exclude** — separate list per mode. VPN modes: `DisallowedApps`. DPI: domains/IPs plus remote IPs of excluded processes (refreshed every 10 s) go to `--ipset-exclude`.
Add items by hand or from the built-in Task manager; changes apply without disconnecting (the tunnel restarts with the new rules for ~1–2 s).

## Build
```
src\build.cmd
```
Uses Roslyn from the installed .NET SDK, targets .NET Framework 4.8.

## Known limits
- WireSock cannot run twice: stop `wiresock-client-service` (TunnlTo) first — NetProbe offers to do it.
- winws cannot filter by process; DPI exclusions by process work through their current TCP connections (QUIC/UDP of excluded apps is not covered).
- On first start NetProbe imports the zapret strategies from the bundled zapret folder. Tunnels, servers and lists start empty.
