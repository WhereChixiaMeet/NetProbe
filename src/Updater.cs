using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

using static NetProbe.Tr;

namespace NetProbe
{
    // Обновления с GitHub: ядро xray (XTLS/Xray-core) и zapret (Flowseal/zapret-discord-youtube)
    public static class Updater
    {
        public const string XrayRepo = "XTLS/Xray-core";
        public const string ZapretRepo = "Flowseal/zapret-discord-youtube";

        public class Release { public string Tag = "", AssetName = "", AssetUrl = ""; public long Size; public DateTime PublishedAt = DateTime.MinValue; /* UTC */ }

        static HttpWebRequest Req(string url, int timeout)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288;
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "NetProbe/" + MainForm.Version; rq.Timeout = timeout; rq.ReadWriteTimeout = timeout;
            rq.Accept = "application/vnd.github+json";
            return rq;
        }

        public static Release Latest(string repo, Func<string, bool> pick)
        {
            using (var r = Req("https://api.github.com/repos/" + repo + "/releases/latest", 20000).GetResponse())
            using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
            {
                var j = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
                var rel = new Release { Tag = Convert.ToString(j["tag_name"]) };
                DateTime pub; object po;
                if (j.TryGetValue("published_at", out po) && DateTime.TryParse(Convert.ToString(po), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out pub)) rel.PublishedAt = pub;
                foreach (var ao in (System.Collections.ArrayList)j["assets"])
                {
                    var a = (Dictionary<string, object>)ao;
                    string n = Convert.ToString(a["name"]);
                    if (!pick(n)) continue;
                    rel.AssetName = n; rel.AssetUrl = Convert.ToString(a["browser_download_url"]); rel.Size = Convert.ToInt64(a["size"]);
                    break;
                }
                return rel;
            }
        }

        static string Download(Release rel, Action<string> status)
        {
            string tmp = Path.Combine(Settings.Sub("runtime"), "download-" + Guid.NewGuid().ToString("N").Substring(0, 8)); // всё внутри папки приложения
            Directory.CreateDirectory(tmp);
            string zip = Path.Combine(tmp, rel.AssetName);
            status(Tx("Загрузка ") + rel.AssetName + " (" + (rel.Size / 1048576.0).ToString("0.0") + Tx(" МБ") + ")…");
            using (var r = Req(rel.AssetUrl, 120000).GetResponse())
            using (var src = r.GetResponseStream())
            using (var dst = File.Create(zip)) src.CopyTo(dst);
            string ex = Path.Combine(tmp, "x");
            ZipFile.ExtractToDirectory(zip, ex);
            return ex;
        }

        public static string XrayVersion(Settings st)
        {
            string exe = XrayProcess.Exe(st);
            if (!File.Exists(exe)) return "";
            var r = Proc.Run(exe, "version", 8000);
            var first = r.Out.Split('\n').FirstOrDefault() ?? "";
            var parts = first.Split(' ');
            return parts.Length > 1 ? parts[1] : first.Trim();
        }

        // Обновить xray: xray.exe, geoip.dat, geosite.dat. xray должен быть остановлен вызывающим.
        public static string UpdateXray(Settings st, Action<string> status)
        {
            status(Tx("Проверка версии xray…"));
            var rel = Latest(XrayRepo, n => n.Equals(Environment.Is64BitOperatingSystem ? "Xray-windows-64.zip" : "Xray-windows-32.zip", StringComparison.OrdinalIgnoreCase));
            if (rel.AssetUrl == "") return Tx("В релизе нет сборки для Windows");
            string cur = XrayVersion(st);
            if (cur != "" && rel.Tag.TrimStart('v') == cur.TrimStart('v')) return Tx("Уже последняя версия: ") + rel.Tag;
            string ex = Download(rel, status);
            Directory.CreateDirectory(st.XrayPath);
            foreach (var f in Directory.GetFiles(ex))
            {
                string name = Path.GetFileName(f);
                if (!(name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))) continue;
                File.Copy(f, Path.Combine(st.XrayPath, name), true);
            }
            TryDelete(ex);
            return "xray " + rel.Tag + Tx(" установлен");
        }

        public const string SingBoxRepo = "SagerNet/sing-box";

        public static string SingBoxVersion(Settings st)
        {
            string exe = SingBoxProcess.Exe(st);
            if (!File.Exists(exe)) return "";
            var r = Proc.Run(exe, "version", 8000);
            var first = (r.Out.Split('\n').FirstOrDefault() ?? "").Trim();
            return first.StartsWith("sing-box version ") ? first.Substring(17).Trim() : first;
        }

        static bool SingBoxAsset(string n)
        {
            n = n.ToLowerInvariant();
            return n.EndsWith(".zip") && n.Contains("windows-" + (Environment.Is64BitOperatingSystem ? "amd64" : "386")) && !n.Contains("legacy");
        }
        public static Release SingBoxLatest() { return Latest(SingBoxRepo, SingBoxAsset); }

        // Обновить sing-box (ядро TUN). Процесс должен быть остановлен вызывающим.
        public static string UpdateSingBox(Settings st, Action<string> status)
        {
            status(Tx("Проверка версии sing-box…"));
            var rel = SingBoxLatest();
            if (rel.AssetUrl == "") return Tx("В релизе нет сборки для Windows");
            string cur = SingBoxVersion(st);
            if (cur != "" && rel.Tag.TrimStart('v') == cur.TrimStart('v')) return Tx("Уже последняя версия: ") + rel.Tag;
            string ex = Download(rel, status);
            Directory.CreateDirectory(st.SingBoxPath);
            foreach (var f in Directory.GetFiles(ex, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) File.Copy(f, Path.Combine(st.SingBoxPath, name), true);
            }
            TryDelete(ex);
            return "sing-box " + rel.Tag + Tx(" установлен");
        }

        public static string ZapretVersion(Settings st)
        {
            foreach (var f in new[] { Path.Combine(st.ZapretPath, "version.txt"), Path.Combine(st.ZapretPath, ".service", "version.txt") })
                try { if (File.Exists(f)) return File.ReadAllText(f).Trim(); } catch { }
            return "";
        }

        // Обновить zapret: bin\ и lists\ (пользовательские *-user.txt не трогаем), general*.bat; стратегии переимпортируются.
        // winws должен быть остановлен вызывающим.
        public static string UpdateZapret(Settings st, Action<string> status)
        {
            status(Tx("Проверка версии zapret…"));
            var rel = Latest(ZapretRepo, n => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (rel.AssetUrl == "") return Tx("В релизе нет архива");
            string cur = ZapretVersion(st);
            if (cur != "" && rel.Tag.TrimStart('v') == cur.TrimStart('v')) return Tx("Уже последняя версия: ") + rel.Tag;
            string ex = Download(rel, status);
            string root = ex;
            // архив может содержать вложенную папку
            if (!Directory.Exists(Path.Combine(root, "bin"))) { var d = Directory.GetDirectories(ex).FirstOrDefault(x => Directory.Exists(Path.Combine(x, "bin"))); if (d != null) root = d; }
            if (!Directory.Exists(Path.Combine(root, "bin"))) return Tx("Неожиданная структура архива");
            string z = st.ZapretPath;
            Directory.CreateDirectory(z);
            CopyDir(Path.Combine(root, "bin"), Path.Combine(z, "bin"), null);
            CopyDir(Path.Combine(root, "lists"), Path.Combine(z, "lists"), n => !n.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase));
            foreach (var f in Directory.GetFiles(root, "*.bat")) File.Copy(f, Path.Combine(z, Path.GetFileName(f)), true);
            File.WriteAllText(Path.Combine(z, "version.txt"), rel.Tag.TrimStart('v'));
            int n2 = DpiConfigs.ImportAll(z, true);
            TryDelete(ex);
            return "zapret " + rel.Tag + Tx(" установлен") + " · " + Tx("стратегий: ") + n2;
        }

        static void CopyDir(string from, string to, Func<string, bool> filter)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from))
            {
                string n = Path.GetFileName(f);
                if (filter != null && !filter(n) && File.Exists(Path.Combine(to, n))) continue;
                File.Copy(f, Path.Combine(to, n), true);
            }
            foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)), filter);
        }

        static void TryDelete(string dir) { try { Directory.Delete(Path.GetDirectoryName(dir), true); } catch { } }
    }
}
