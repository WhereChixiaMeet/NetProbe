## RouteX 3.4.4

The Configs routing settings are now correctly located under **Settings → Engine**:

- Ordered routing rule editor.
- IPv4-only TUN setting.
- RU sites direct setting.

The section now has a permanent module registration, so it no longer falls back to Appearance. No separate Routing settings tab is added. The hint on the Routing page also points to Settings → Engine.

This is a settings-placement fix on top of 3.4.3. VPN routing behavior is unchanged. The Windows .NET Framework 4.8 build passed; the existing two PagesServers.cs compiler warnings remain.

Fully disconnect the VPN and close RouteX before replacing the executable. Keep the existing config and core folders. RouteX.exe and NetProbe.exe are identical; use your installation's filename.
