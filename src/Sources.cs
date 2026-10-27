using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public enum Source { None, Vpn, Subs }

    // 3.0: логика источников (ТЗ, раздел 4) и единый сегмент ROUTE | VPN | DPI.
    //  • туннелей ≥ 1 и гибрид выключен → работает только Primary VPN, подписки заблокированы;
    //  • туннелей нет → источник — подписки (xray); VPN/ROUTE подключают подписку, «туннель не назначен» — один раз за установку;
    //  • ни туннелей, ни подписок → чистый DPI-модуль, VPN/ROUTE серые;
    //  • Режим гибрида (Настройки) → оба источника, ручной тумблер «Primary VPN ⇄ Subscriptions». Приоритет — Primary VPN.
    public partial class MainForm
    {
        int TunnelCount { get { try { return Tunnels.Names().Count; } catch { return 0; } } }
        bool HasSubs { get { try { return ServerStore.Snapshot().Count > 0; } catch { return false; } } }

        Source ActiveSource()
        {
            int n = TunnelCount; bool subs = HasSubs;
            if (st.HybridMode)
            {
                if (st.Source == 1 && subs) return Source.Subs;
                if (n > 0) return Source.Vpn;
                return subs ? Source.Subs : Source.None;
            }
            if (n > 0) return Source.Vpn;
            return subs ? Source.Subs : Source.None;
        }

        // почему подписки сейчас нельзя подключать (null — можно)
        string SubsBlockedReason()
        {
            if (TunnelCount > 0 && !st.HybridMode) return Tx("Есть Primary VPN — подписки отключены; включи Режим гибрида в Настройках");
            if (st.HybridMode && ActiveSource() == Source.Vpn) return Tx("Активный источник — Primary VPN; переключи тумблер на Subscriptions");
            return null;
        }

        bool AnyActive { get { return router.Active || xm.Active; } }

        // причины недоступности чипов (null — доступен)
        string[] ChipReasons()
        {
            var r = new string[3];
            var src = ActiveSource();
            if (src == Source.None) r[0] = r[1] = Tx("Нет ни туннелей, ни подписок — работает только DPI. Добавь туннель в «Конфиги» или подписку на Subscriptions");
            else if (src == Source.Vpn && !System.IO.File.Exists(st.WiresockPath)) r[0] = r[1] = Tx("WireSock не установлен — VPN-режимы недоступны");
            if (ZapretRunner.Check(st.ZapretPath) != null) { r[2] = Tx("zapret не найден — DPI недоступен"); if (r[0] == null) r[0] = null; }
            return r;
        }

        // клик по чипу сегмента: другой режим — выбрать и подключить; текущий при активном подключении — отключить
        void ChipClick(int m)
        {
            var mode = (RouteMode)m;
            if (mode == st.RunMode && AnyActive) { DisconnectAll(); return; }
            if (st.RunMode != mode) { st.SetRunMode(mode); st.Save(); Logger.Write(LogLevel.Info, Tx("Режим: ") + Router.ModeName(mode)); }
            ConnectCurrent(true);
            UpdateModeUi();
        }

        void SetMode(RouteMode m)
        {
            if (st.RunMode == m) return;
            st.SetRunMode(m); st.Save();
            if (AnyActive) ConnectCurrent(true);
            UpdateModeUi();
        }

        // подключить текущий режим из текущего источника (reconnect = уже что-то работает)
        void ConnectCurrent(bool fromUser)
        {
            var mode = st.RunMode;
            if (mode == RouteMode.DpiOnly)
            {
                if (xm.Active) xm.Disconnect();
                StartMonitoring(); router.Connect();
                return;
            }
            var src = ActiveSource();
            if (src == Source.None)
            {
                if (fromUser) Notice.Show(this, Tx("Нет источника"), Tx("Нет ни туннелей, ни подписок — NetProbe работает как чистый DPI-модуль. Добавь туннель в «Конфиги» или подписку на вкладке Subscriptions."));
                return;
            }
            if (src == Source.Vpn)
            {
                if (!Tunnels.Exists(st.PrimaryTunnel))
                {
                    // туннели есть, но основной не выбран — берём первый
                    var first = Tunnels.Names().FirstOrDefault();
                    if (first == null) return;
                    st.PrimaryTunnel = first; st.Save(); LoadConnectUi();
                }
                if (xm.Active) xm.Disconnect(); // приоритет Primary VPN: один активный источник
                StartMonitoring(); router.Connect();
                return;
            }
            // источник — подписки
            if (fromUser && TunnelCount == 0 && !st.VpnNoTunnelWarned)
            {
                st.VpnNoTunnelWarned = true; st.Save(); // один раз за жизнь установки; обновлением не сбрасывается
                Notice.Show(this, Tx("Туннель не назначен"), Tx("Туннель не назначен — VPN будет работать через используемую подписку."));
            }
            if (router.Active) router.Disconnect();
            var pick = ServerStore.Find(st.ActiveServer);
            if (pick == null)
            {
                var all = ServerStore.Snapshot();
                pick = all.Where(x => x.Delay > 0).OrderBy(x => x.Delay).FirstOrDefault() ?? all.FirstOrDefault();
            }
            if (pick == null) return;
            if (xm.Active && st.ActiveServer == pick.Id) { xm.ReapplyDpi(); return; } // уже подключено: режим ROUTE/VPN меняет только DPI
            XConnect(pick.Id, true);
        }

        void DisconnectAll()
        {
            if (router.Active) router.Disconnect();
            if (xm.Active) xm.Disconnect();
        }

        // тумблер источника (только в гибриде)
        void PickSource(int v)
        {
            if (!st.HybridMode) return;
            st.Source = v; st.Save();
            Logger.Write(LogLevel.Info, Tx("Источник: ") + (v == 0 ? "Primary VPN" : "Subscriptions"));
            if (AnyActive && st.RunMode != RouteMode.DpiOnly) ConnectCurrent(true);
            UpdateModeUi();
        }

        // Включение Режима гибрида: предупреждение один раз (с галочкой «больше не показывать»)
        bool ConfirmHybrid()
        {
            if (st.HybridWarned) return true;
            bool dont;
            int r = Notice.Show(this, Tx("Режим гибрида"), Tx("Подписки не проверены: возможны непроверенные каналы. Приоритет всегда у Primary VPN: пока туннель поднят, основной трафик идёт через него."),
                new[] { Tx("Включить"), Tx("Отмена") }, Tx("Больше не показывать"), out dont);
            if (r != 0) return false;
            if (dont) { st.HybridWarned = true; st.Save(); }
            return true;
        }

        readonly List<ModeSegment> segments = new List<ModeSegment>();
        ModeSegment NewSegment()
        {
            var s = new ModeSegment();
            s.ChipClicked += ChipClick;
            s.Disposed += (o, e) => segments.Remove(s);
            segments.Add(s);
            return s;
        }

        // состояние всех сегментов и тумблера источника — из реального состояния router / xray
        void UpdateModeUi()
        {
            var why = ChipReasons();
            foreach (var s in segments.ToList())
            {
                if (s.IsDisposed) continue;
                s.Selected = (int)st.RunMode;
                s.Active = AnyActive;
                for (int i = 0; i < 3; i++) s.Disabled[i] = why[i];
                s.Invalidate();
            }
            if (srcSwitch != null && !srcSwitch.IsDisposed)
            {
                srcSwitch.Value = ActiveSource() == Source.Subs ? 1 : 0;
                srcSwitch.SetReason(!st.HybridMode ? Tx("Доступно только в Режиме гибрида (Настройки)") : TunnelCount == 0 ? Tx("Нет туннелей — работает только Subscriptions") : !HasSubs ? Tx("Нет подписок — работает только Primary VPN") : null);
            }
            if (miModes != null) for (int i = 0; i < 3; i++) miModes[i].Checked = i == st.Mode;
            UpdateSide();
        }
    }
}
