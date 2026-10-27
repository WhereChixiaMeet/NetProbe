using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    static class Program
    {
        public static int WM_SHOWME, WM_QUITME;

        static void Crash(Exception ex)
        {
            string text = ex == null ? "Unknown error" : ex.ToString();
            string file = "";
            try { file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NetProbe-crash.txt"); System.IO.File.WriteAllText(file, DateTime.Now + "\r\n" + text); } catch { }
            try { Logger.Write(LogLevel.Error, "Fatal: " + text); } catch { }
            try { MessageBox.Show(Tx("NetProbe не смог запуститься:\r\n\r\n") + text + (file != "" ? Tx("\r\n\r\nСохранено в ") + file : ""), "NetProbe", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            try { WM_SHOWME = Native.RegisterWindowMessage("NetProbe_ShowMe_7f3a"); WM_QUITME = Native.RegisterWindowMessage("NetProbe_QuitMe_7f3a"); } catch { }
            bool created;
            using (var mutex = new Mutex(true, "NetProbe2_SingleInstance_7f3a", out created))
            {
                // --replace: попросить запущенную копию закрыться и занять её место (обновление без ручного выхода)
                if (!created && Array.IndexOf(args, "--replace") >= 0)
                {
                    try { Native.PostMessage((IntPtr)0xffff, WM_QUITME, IntPtr.Zero, IntPtr.Zero); } catch { }
                    try { created = mutex.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
                }
                if (!created && Array.IndexOf(args, "--shots") < 0) { try { Native.PostMessage((IntPtr)0xffff, WM_SHOWME, IntPtr.Zero, IntPtr.Zero); } catch { } return; }
                try { Native.SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f; } catch { }
                Application.ThreadException += (s, e) => Logger.Write(LogLevel.Error, "Unhandled: " + e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
                bool min = Array.Exists(args, a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
                MainForm.Updated = Array.Exists(args, a => a == "--updated");
                MainForm.Reapply3 = Array.Exists(args, a => a == "--reapply");
                int ti = Array.FindIndex(args, a => a == "--selftest");
                if (ti >= 0 && ti + 1 < args.Length) { SelfTest.Run(args[ti + 1]); return; }
                int sp = Array.FindIndex(args, a => a == "--speedtest");
                if (sp >= 0 && sp + 1 < args.Length) { SelfTest.Speed(args[sp + 1], sp + 2 < args.Length && !args[sp + 2].StartsWith("--") ? args[sp + 2] : null); return; }
                int si = Array.FindIndex(args, a => a == "--shots");
                if (si >= 0 && si + 1 < args.Length) MainForm.ShotsDir = args[si + 1];
                try { Application.Run(new MainForm(min)); }
                catch (Exception ex) { Crash(ex); }
                GC.KeepAlive(mutex);
            }
        }
    }
}
