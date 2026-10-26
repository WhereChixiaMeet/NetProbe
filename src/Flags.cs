using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;

namespace NetProbe
{
    // Флаги стран для колонки GEO. Windows не рисует эмодзи-флаги, поэтому картинки (flagcdn.com, 40 px)
    // скачиваются один раз в config\runtime\flags и дальше берутся с диска. Нет картинки — показывается код страны.
    public static class Flags
    {
        static readonly object lk = new object();
        static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Queue<string> queue = new Queue<string>();
        static bool working;
        public static event Action Loaded;

        static string Dir { get { string d = Path.Combine(Settings.Sub("runtime"), "flags"); try { Directory.CreateDirectory(d); } catch { } return d; } }

        public static Image Get(string cc)
        {
            if (string.IsNullOrEmpty(cc) || cc.Length != 2 || !char.IsLetter(cc[0]) || !char.IsLetter(cc[1])) return null;
            cc = cc.ToLowerInvariant();
            lock (lk)
            {
                Image img;
                if (cache.TryGetValue(cc, out img)) return img;
                if (failed.Contains(cc)) return null;
                string f = Path.Combine(Dir, cc + ".png");
                if (File.Exists(f))
                {
                    try { using (var fs = File.OpenRead(f)) img = new Bitmap(Image.FromStream(fs)); cache[cc] = img; return img; }
                    catch { try { File.Delete(f); } catch { } }
                }
                if (!queue.Contains(cc)) queue.Enqueue(cc);
                if (!working) { working = true; ThreadPool.QueueUserWorkItem(_ => Work()); }
                return null;
            }
        }

        static void Work()
        {
            while (true)
            {
                string cc;
                lock (lk) { if (queue.Count == 0) { working = false; break; } cc = queue.Dequeue(); }
                bool ok = false;
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
                    var rq = (HttpWebRequest)WebRequest.Create("https://flagcdn.com/w40/" + cc + ".png");
                    rq.UserAgent = "NetProbe/2.0"; rq.Timeout = 8000; rq.ReadWriteTimeout = 8000;
                    string tmp = Path.Combine(Dir, cc + ".tmp"), f = Path.Combine(Dir, cc + ".png");
                    using (var r = rq.GetResponse()) using (var src = r.GetResponseStream()) using (var dst = File.Create(tmp)) src.CopyTo(dst);
                    using (var fs = File.OpenRead(tmp)) { var img = new Bitmap(Image.FromStream(fs)); lock (lk) cache[cc] = img; }
                    if (File.Exists(f)) File.Delete(f);
                    File.Move(tmp, f);
                    ok = true;
                }
                catch { }
                if (!ok) lock (lk) failed.Add(cc);
                var h = Loaded; if (ok && h != null) try { h(); } catch { }
            }
        }
    }
}
