using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using static NetProbe.Tr;

namespace NetProbe
{
    // Самообновление NetProbe из GitHub Releases (WhereChixiaMeet/NetProbe).
    // Ожидаемый релиз: тег vX.Y.Z (больше MainForm.Version) и ассет NetProbe.exe — или .zip, внутри которого есть NetProbe.exe.
    // VPN не падает: wiresock-client / winws / xray / sing-box — отдельные процессы без перенаправления вывода, при выходе на
    // обновление NetProbe их не останавливает, а новая версия запускается с --updated и подхватывает их (handoff.ini).
    public static class SelfUpdate
    {
        public const string Repo = "WhereChixiaMeet/NetProbe";
        static string HandoffFile { get { return Path.Combine(Settings.Sub("runtime"), "handoff.ini"); } }

        public static bool Newer(string tag)
        {
            Version a, b;
            return Version.TryParse((tag ?? "").TrimStart('v', 'V').Split('-')[0], out a) && Version.TryParse(MainForm.Version, out b) && a > b;
        }

        public static Updater.Release Latest()
        {
            return Updater.Latest(Repo, n => n.Equals("NetProbe.exe", StringComparison.OrdinalIgnoreCase) || (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && n.IndexOf("source", StringComparison.OrdinalIgnoreCase) < 0 && n.IndexOf("src", StringComparison.OrdinalIgnoreCase) < 0));
        }

        // Скачивает новую версию, возвращает путь к новому NetProbe.exe
        public static string Download(Updater.Release rel, Action<string> status)
        {
            string dir = Path.Combine(Settings.Sub("runtime"), "app-update");
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, rel.AssetName);
            status(Tx("Загрузка ") + rel.AssetName + "…");
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | (System.Net.SecurityProtocolType)12288;
            var rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(rel.AssetUrl);
            rq.UserAgent = "NetProbe/" + MainForm.Version; rq.Timeout = 120000; rq.ReadWriteTimeout = 120000;
            using (var r = rq.GetResponse()) using (var src = r.GetResponseStream()) using (var dst = File.Create(file)) src.CopyTo(dst);
            if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string ex = Path.Combine(dir, "x");
                ZipFile.ExtractToDirectory(file, ex);
                var exe = Directory.GetFiles(ex, "NetProbe.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (exe == null) throw new Exception(Tx("В архиве релиза нет NetProbe.exe"));
                return exe;
            }
            return file;
        }

        public static void SaveHandoff(Dictionary<string, string> d)
        {
            File.WriteAllLines(HandoffFile, d.Select(kv => kv.Key + "=" + kv.Value).ToArray(), new UTF8Encoding(false));
        }

        public static Dictionary<string, string> TakeHandoff()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (!File.Exists(HandoffFile)) return null;
                foreach (var l in File.ReadAllLines(HandoffFile)) { int i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1); }
                File.Delete(HandoffFile);
            }
            catch { }
            return d;
        }

        // Наш дочерний процесс по имени и папке (после перезапуска NetProbe)
        public static Process FindChild(string name, string folder)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                string path = ProcInfo.ImagePath(p.Id);
                if (folder == null || path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return p;
                p.Dispose();
            }
            return null;
        }

        // Заменить exe после выхода текущего процесса и запустить новую версию с --updated (права админа наследуются)
        public static void LaunchSwap(string newExe)
        {
            string exe = System.Windows.Forms.Application.ExecutablePath;
            int pid = Process.GetCurrentProcess().Id;
            Func<string, string> q = s => "'" + s.Replace("'", "''") + "'";
            string ps = "Wait-Process -Id " + pid + " -Timeout 30 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500; " +
                        "for ($i = 0; $i -lt 20; $i++) { try { Copy-Item -LiteralPath " + q(newExe) + " -Destination " + q(exe) + " -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 } }; " +
                        "Start-Process -FilePath " + q(exe) + " -ArgumentList '--updated'";
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(ps));
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -EncodedCommand " + enc) { UseShellExecute = false, CreateNoWindow = true });
        }
    }
}
