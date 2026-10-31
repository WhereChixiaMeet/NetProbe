## RouteX 3.4.3

Fully disconnect the VPN and close RouteX before replacing the executable. Keep your existing `config`, `xray`, `singbox` and `zapret` folders. `RouteX.exe` and `NetProbe.exe` are identical; use the filename required by your installation.

### Ordered routing rules

- Open **Settings → Routing · Configs → Rule editor**.
- Add, edit, enable/disable, delete and reorder rules. The first matching rule wins.
- Match domains/GeoSite, IP/CIDR/GeoIP, executable names/full paths, TCP/UDP and destination ports. Choose Direct, VPN (`proxy`) or Block, plus an explicit fallback for unmatched traffic.
- **RU + current lists** creates a draft with RU → Direct, Exclude → Direct and Include → VPN. The lists are copied, not linked.
- The editor is optional and applies to VLESS/VMess/Trojan in Configs. It replaces legacy Include/Exclude routing for those connections; Xray RU/ad overrides are disabled in this mode. Local-network and service rules remain fixed. WireGuard and subscriptions keep their existing routing paths.
- Changes require the VPN to be off and take effect on the next connection. Direct uses DPI only when DPI is enabled and its own rules match.

### Fixes

- Added IPv4-only TUN for Configs: no IPv6 address in the TUN inbound, empty AAAA answers, and IPv6 rejection for packets reaching TUN; strict routing stays enabled.
- The Configs **DNS through VPN** switch now controls DNS fallback. Explicit Direct domain rules use local DNS before Include rules. VPN-server DNS uses local bootstrap resolution.
- Requests made through the Windows DNS service may lack the original application's identity. With VPN DNS enabled, those requests use VPN DNS by default; process-only exclusions cannot reliably identify such queries. Browser Secure DNS/DoH remains browser-controlled.
- Route inspection reads the generated sing-box and Xray runtime rules, including legacy RU bypass. It reports Unknown when required connection details are missing.
- Running sing-box is rebuilt when its configuration changes, including a changed SOCKS port. Added configuration checks and immediate-startup-exit detection.
- Removed the tg-ws-proxy upstream-version banner, TG Proxy update badge and related tray notification.
- Existing TLS/VLESS compatibility and `allowInsecure` behavior are unchanged.

### Validation

- Windows .NET Framework 4.8 build passed.
- 47 existing regression checks and 39 Configs routing/DNS/editor/lifecycle checks passed. Lifecycle checks used an offline fake core; no real VPN was started.
- Generated configurations passed sing-box 1.14.4 `check` and Xray 26.10.29 `run -test`.
- Editor rendering checked in light and dark themes.
- These checks do not certify leak-free operation or confirm the remote computer's Yandex issue is resolved; that requires testing on the affected computer and network.

Detailed changes: [CHANGES-3.4.3.md](https://github.com/WhereChixiaMeet/RouteX/blob/v3.4.3/CHANGES-3.4.3.md).
