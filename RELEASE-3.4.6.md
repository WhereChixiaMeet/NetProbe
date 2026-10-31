## RouteX 3.4.6

Built on the latest 3.4.5 source, retaining its authenticated Configs SOCKS port, route-inspection improvements and VPN Only kill-switch fixes.

- Removed the separate Rule editor, its table and its enable switch.
- Configs always use the standard **Routing → Include/Exclude** lists. Old table settings are ignored, including previously enabled tables.
- Kept the original **Settings → Engine → RU sites direct** option and its saved on/off setting. Recognized RU domains (category-ru, .ru, .su, .рф) and Russian IPs use Direct when this option is enabled, including for apps in Include.
- Kept DNS and IPv4-only TUN fixes. Direct uses DPI only when DPI is enabled and its rules match.

Fully disconnect the VPN and close RouteX before replacing the executable. Keep the existing config and core folders. RouteX.exe and NetProbe.exe are identical.

Validation: Windows .NET Framework 4.8 build passed; 47 existing regression checks and 27 standard routing/DNS/migration/lifecycle checks passed. Generated configurations passed sing-box check and Xray run -test. Real VPN engines were not started.
