using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace NetProbe
{
    public class StatsSnapshot
    {
        public int Count; public double Last = -1, Avg = -1, Min = -1, Max = -1, Jitter = -1, LossPct, Uptime = 100, AvgDns = -1;
        public long Total, Fails, Drops;
    }

    public class ChannelStats
    {
        const int Window = 300;
        readonly Queue<ProbeResult> win = new Queue<ProbeResult>();
        public long Total, Fails, Drops;
        bool lastOk = true;
        readonly object lk = new object();

        public void Add(ProbeResult r)
        {
            lock (lk)
            {
                win.Enqueue(r); if (win.Count > Window) win.Dequeue();
                Total++;
                if (!r.Ok) { Fails++; if (lastOk) Drops++; }
                lastOk = r.Ok;
            }
        }

        public void Reset() { lock (lk) { win.Clear(); Total = Fails = Drops = 0; lastOk = true; } }

        public StatsSnapshot Snap()
        {
            lock (lk)
            {
                var s = new StatsSnapshot { Total = Total, Fails = Fails, Drops = Drops, Count = win.Count };
                var arr = win.ToArray();
                var ok = arr.Where(x => x.Ok && x.Latency >= 0).Select(x => x.Latency).ToList();
                if (arr.Length > 0) s.LossPct = 100.0 * arr.Count(x => !x.Ok) / arr.Length;
                if (Total > 0) s.Uptime = 100.0 * (Total - Fails) / Total;
                if (ok.Count > 0)
                {
                    s.Last = ok[ok.Count - 1]; s.Avg = ok.Average(); s.Min = ok.Min(); s.Max = ok.Max();
                    double j = 0; for (int i = 1; i < ok.Count; i++) j += Math.Abs(ok[i] - ok[i - 1]);
                    s.Jitter = ok.Count > 1 ? j / (ok.Count - 1) : 0;
                }
                var dns = arr.Where(x => x.Ok && x.Dns >= 0).Select(x => x.Dns).ToList();
                if (dns.Count > 0) s.AvgDns = dns.Average();
                return s;
            }
        }
    }

    // Фоновый опрос одного канала. Ограничения частоты:
    //  - не более Settings.MaxPollRate запросов/с и не чаще одного раза в MinIntervalMs;
    //  - следующий опрос стартует только после завершения предыдущего (без перекрытия);
    //  - при ошибках интервал растёт экспоненциально до MaxBackoffMs.
    public class ChannelMonitor
    {
        public readonly string Key;
        public readonly ChannelStats Stats = new ChannelStats();
        public event Action<ChannelMonitor, ProbeResult> Sample;
        public Func<Prober> GetProber;
        public Func<Settings> GetSettings;
        public volatile bool Paused;
        public double EffectiveIntervalMs;
        public int ConsecutiveFails;
        // VPN-канал: пассивная проверка туннеля (null или Unavailable — обычный опрос сайта)
        public Func<TunnelFlowState> Passive;
        // опросы по кругу только когда это разрешено (окно и виджеты скрыты): на виду — один сайт, ровный график
        public Func<bool> Rotate;
        public event Action<ChannelMonitor, TunnelFlowState> PassiveSample; // данные через сервер идут / тишина — опрос сайта пропущен
        DateTime lastActive = DateTime.MinValue; int urlIndex;

        Thread th; volatile bool running; readonly AutoResetEvent wake = new AutoResetEvent(false);
        public bool Running { get { return running; } }

        public ChannelMonitor(string key) { Key = key; }

        public void Start()
        {
            if (running) return;
            running = true;
            th = new Thread(Loop) { IsBackground = true, Name = "monitor-" + Key };
            th.Start();
        }

        public void Stop() { running = false; wake.Set(); }
        public void Kick() { wake.Set(); }

        void Loop()
        {
            while (running)
            {
                var st = GetSettings();
                double rate = Math.Max(Settings.MinPollRate, Math.Min(st.MaxPollRate, Key == "direct" ? st.DirectPollRate : st.PollRate));
                double interval = Math.Max(Settings.MinIntervalMs, 1000.0 / rate);
                if (Paused) { wake.WaitOne(500); continue; }
                var t0 = DateTime.Now;
                if (Passive != null && st.ProbePassive && ConsecutiveFails == 0 && (t0 - lastActive).TotalSeconds < st.ProbeIdleSec)
                {
                    TunnelFlowState fs = TunnelFlowState.Unavailable;
                    try { fs = Passive(); } catch { }
                    if (fs == TunnelFlowState.Flowing || fs == TunnelFlowState.Idle)
                    {
                        EffectiveIntervalMs = interval;
                        var ps = PassiveSample; if (ps != null) try { ps(this, fs); } catch { }
                        wake.WaitOne((int)Math.Max(200, interval - (DateTime.Now - t0).TotalMilliseconds));
                        continue;
                    }
                }
                var urls = st.ProbeUrls();
                bool rotate = Rotate == null || Rotate();
                string url = rotate ? urls[(urlIndex++ & int.MaxValue) % urls.Count] : st.ProbeUrl;
                lastActive = t0;
                ProbeResult r;
                try { r = GetProber().Http(url, st.TimeoutSec); }
                catch (Exception ex) { r = new ProbeResult { Error = ex.Message }; }
                if (!running) break;
                if (Paused) continue; // результат во время теста скорости не учитываем
                Stats.Add(r);
                ConsecutiveFails = r.Ok ? 0 : ConsecutiveFails + 1;
                // одиночный сбой — сразу перепроверяем с обычным интервалом; реже опрашиваем только когда связи нет на самом деле
                int downAfter = Math.Max(1, st.DownAfterFails);
                if (ConsecutiveFails >= downAfter) interval = Math.Min(Settings.MaxBackoffMs, interval * Math.Pow(2, Math.Min(ConsecutiveFails - downAfter + 1, 5)));
                EffectiveIntervalMs = interval;
                var h = Sample; if (h != null) try { h(this, r); } catch { }
                double elapsed = (DateTime.Now - t0).TotalMilliseconds;
                int wait = (int)Math.Max(200, interval - elapsed);
                wake.WaitOne(wait);
            }
        }
    }
}
