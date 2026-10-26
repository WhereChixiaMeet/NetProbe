# Разбор TunnlTo, zapret-discord-youtube и NetProbe 1.0.3

## TunnlTo (TunnlTo.exe, 24 МБ)
- Tauri-приложение (Rust + React/WebView2). Сам туннель не делает — это GUI над **WireSock VPN Client 1.4.7** (`wiresock-client.exe`, драйвер `ndiswgc`, NDIS-фильтр).
- Данные: `%APPDATA%\com.tunnl.to\` — `tunnels_store.json` (WireGuard-ключи, адреса, endpoint), `rule_items_store.json` / `rule_groups_store.json` (правила «Process Name»), `app_data_store.json` (выбранный туннель, allowed/disallowed, автозапуск), `settings_store.json`.
- При включении собирает `%LOCALAPPDATA%\com.tunnl.to\tunnel.conf` — обычный WireGuard-конфиг + ключи WireSock: `AllowedApps`, `DisallowedApps`, `AllowedIPs`, `DisallowedIPs`, `Socks5Proxy…`. Совпадение приложений — подстрока имени/пути процесса.
- Запуск: ставит службу `wiresock-client-service` (`install -start-type 3 -config … -log-level …`) и задачи планировщика `StartWiresock` / `StopWiresock` (`sc start/stop`), чтобы GUI работал без прав администратора.
- Статус: читает лог `%ProgramData%\NT KERNEL\WireSock VPN Client\wiresock-vpn-client.log` (строки «Handshake response received…»). Своего мониторинга канала и переключения при сбое нет.
- У WireSock есть встроенный `-fallback-config`, но это просто запасной конфиг, без проверки реальной связи.

## zapret-discord-youtube (Flowseal, 1.10.4)
- Ядро — `bin\winws.exe` (zapret для Windows) + WinDivert (`WinDivert.dll`, `WinDivert64.sys`). Работает на уровне пакетов для всей системы, по приложениям не делит.
- Стратегия = командная строка winws в `general*.bat` (22 варианта): `--wf-tcp/--wf-udp` (что перехватывать) и профили через `--new` (`--filter-tcp/udp`, `--hostlist`, `--ipset`, `--dpi-desync=fake|multisplit…`, файлы-подделки `*.bin`).
- Списки в `lists\`: `list-general.txt`, `list-google.txt`, `list-exclude.txt`, `ipset-all.txt`, `ipset-exclude.txt` + пользовательские `*-user.txt` (пустыми оставлять нельзя).
- `service.bat`: ставит winws службой `zapret`, игровой фильтр (порты 1024-65535 вместо заглушки `12`), режимы ipset, обновления, диагностика конфликтов (GoodbyeDPI, чужой WinDivert).

## NetProbe 1.0.3
- C# WinForms, .NET Framework 4.8, исходники в `NetProbe_old\src`.
- Два канала: копии curl `netprobe-vpn.exe` (кладётся в туннель) и `netprobe-direct.exe` (привязка к IP физического адаптера). Опрос Cloudflare `/cdn-cgi/trace`: задержка, IP, страна, DNS/TCP/TLS, потери, джиттер; ограничение частоты и откат при ошибках.
- Вкладки: монитор, тесты, сайты, лог, настройки; трей и виджет.

## Что из этого взято в NetProbe 2.0
- VPN: NetProbe сам запускает `wiresock-client.exe run -config …` и подставляет `AllowedApps`/`DisallowedApps` из своих списков. Пробник `netprobe-vpn.exe` всегда в туннеле — по нему видно, жив ли VPN.
- DPI: стратегии из `general*.bat` импортируются в `config\dpi\*.txt`, редактируются в GUI, запускаются как `winws.exe` с подставленными путями. Исключения NetProbe добавляются в каждый профиль через `--ipset-exclude` / `--hostlist-exclude`.
- Мониторинг, тесты, сайты, трей и виджет — из NetProbe 1.0.3.
