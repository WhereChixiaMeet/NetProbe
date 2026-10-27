using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace NetProbe
{
    // Графики Day / Week (ТЗ, раздел 5) — из тех же дневных логов log\YYYY-MM-DD.log, без параллельного хранилища:
    // раз в минуту монитор пишет строку «STAT vpn <ср.мс|-> <сбоев>/<опросов> | direct …». Чтение — построчно, в фоне.
    // Пропуск данных = NaN (на графике — разрыв, ничего не «дорисовывается»).
    public static class History
    {
        public class Acc { public double Sum; public int N, Fail; }
        static readonly object lk = new object();
        static readonly Acc vpn = new Acc(), dir = new Acc();
        static DateTime lastFlush = DateTime.Now;

        public static void Add(string key, bool ok, double ms)
        {
            lock (lk)
            {
                var a = key == "vpn" ? vpn : dir;
                a.N++; if (ok && ms >= 0) a.Sum += ms; else a.Fail++;
            }
        }

        static string Part(Acc a)
        {
            int good = a.N - a.Fail;
            return (good > 0 ? (a.Sum / good).ToString("0.0", CultureInfo.InvariantCulture) : "-") + " " + a.Fail + "/" + a.N;
        }

        // вызывается из UI-тика: раз в минуту — одна строка в лог (если были опросы)
        public static void Tick()
        {
            string line = null;
            lock (lk)
            {
                if ((DateTime.Now - lastFlush).TotalSeconds < 60) return;
                lastFlush = DateTime.Now;
                if (vpn.N + dir.N == 0) return;
                line = "STAT vpn " + Part(vpn) + " | direct " + Part(dir);
                vpn.Sum = dir.Sum = 0; vpn.N = vpn.Fail = dir.N = dir.Fail = 0;
            }
            Logger.Write(LogLevel.Poll, line);
        }

        static readonly Regex Rx = new Regex(@"^(\d\d):(\d\d):\d\d\.\d+ \[POLL\] STAT vpn (\S+) (\d+)/(\d+) \| direct (\S+) (\d+)/(\d+)", RegexOptions.Compiled);

        // days = 1 (сегодня, 1 точка / мин) или 7 (неделя, 1 точка / 30 мин). Возвращает null, если логов нет.
        public static Tuple<List<double>, List<double>> Load(string dir, int days)
        {
            int bucketMin = days == 1 ? 1 : 30;
            int perDay = 1440 / bucketMin, total = perDay * days;
            var sa = new double[total]; var na = new int[total]; var sb = new double[total]; var nb = new int[total];
            var today = DateTime.Today; bool any = false;
            for (int d = 0; d < days; d++)
            {
                var date = today.AddDays(-(days - 1 - d));
                string f = Path.Combine(dir, date.ToString("yyyy-MM-dd") + ".log");
                if (!File.Exists(f)) continue;
                try
                {
                    using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string l;
                        while ((l = sr.ReadLine()) != null)
                        {
                            if (l.IndexOf("STAT vpn", StringComparison.Ordinal) < 0) continue;
                            var m = Rx.Match(l); if (!m.Success) continue;
                            int min = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                            int idx = d * perDay + min / bucketMin; if (idx < 0 || idx >= total) continue;
                            double v; any = true;
                            if (double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { sa[idx] += v; na[idx]++; }
                            if (double.TryParse(m.Groups[6].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) { sb[idx] += v; nb[idx]++; }
                        }
                    }
                }
                catch { }
            }
            if (!any) return null;
            // сегодня — только до текущей минуты (будущее не рисуем)
            int nowIdx = (days - 1) * perDay + (int)(DateTime.Now - today).TotalMinutes / bucketMin;
            var a = new List<double>(); var b = new List<double>();
            for (int i = 0; i <= Math.Min(nowIdx, total - 1); i++) { a.Add(na[i] > 0 ? sa[i] / na[i] : double.NaN); b.Add(nb[i] > 0 ? sb[i] / nb[i] : double.NaN); }
            return Tuple.Create(a, b);
        }
    }
}
