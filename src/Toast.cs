using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace NetProbe
{
    // Системное уведомление Windows (toast) без внешних dll и ссылок на winmd: WinRT-типы берутся через
    // Type.GetType(..., ContentType=WindowsRuntime), это работает в .NET Framework 4.5+ на Windows 10/11.
    // AUMID регистрируется в HKCU без ярлыка. Если что-то не так — Show() возвращает false, и вызывающий
    // показывает обычный balloon из трея.
    public static class Toast
    {
        public const string Aumid = "WhereChixiaMeet.NetProbe";
        public static event Action<string> Activated; // аргумент кнопки / тела уведомления ("open", "update", "later")

        [DllImport("shell32.dll", SetLastError = true)] static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string id);

        static bool registered;
        static object notifier;

        public static void Init(Bitmap icon)
        {
            try
            {
                SetCurrentProcessExplicitAppUserModelID(Aumid);
                string png = Path.Combine(Settings.Sub("runtime"), "toast-icon.png");
                try { if (icon != null) using (var b = new Bitmap(icon, 64, 64)) b.Save(png, ImageFormat.Png); } catch { }
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Aumid))
                {
                    k.SetValue("DisplayName", "RouteX");
                    if (File.Exists(png)) k.SetValue("IconUri", png);
                }
                registered = true;
            }
            catch { registered = false; }
        }

        static Type WinRt(string name, string asm) { return Type.GetType(name + ", " + asm + ", ContentType=WindowsRuntime"); }

        static string X(string s) { return SecurityElement.Escape(s ?? ""); }

        // buttons: пары (текст, аргумент). Возвращает false, если toast недоступен (тогда — balloon).
        public static bool Show(string title, string text, params Tuple<string, string>[] buttons)
        {
            if (!registered || Environment.OSVersion.Version.Major < 10) return false;
            try
            {
                var tMgr = WinRt("Windows.UI.Notifications.ToastNotificationManager", "Windows.UI");
                var tToast = WinRt("Windows.UI.Notifications.ToastNotification", "Windows.UI");
                var tXml = WinRt("Windows.Data.Xml.Dom.XmlDocument", "Windows.Data.Xml.Dom");
                if (tMgr == null || tToast == null || tXml == null) return false;
                string acts = "";
                if (buttons != null && buttons.Length > 0)
                {
                    acts = "<actions>";
                    foreach (var b in buttons) acts += "<action content=\"" + X(b.Item1) + "\" arguments=\"" + X(b.Item2) + "\" activationType=\"foreground\"/>";
                    acts += "</actions>";
                }
                string xml = "<toast launch=\"open\"><visual><binding template=\"ToastGeneric\"><text>" + X(title) + "</text><text>" + X(text) + "</text></binding></visual>" + acts + "</toast>";
                var doc = Activator.CreateInstance(tXml);
                tXml.GetMethod("LoadXml").Invoke(doc, new object[] { xml });
                var toast = Activator.CreateInstance(tToast, doc);
                // клик по уведомлению / кнопке — пока NetProbe запущен (он и так живёт в трее)
                try
                {
                    var ev = tToast.GetEvent("Activated");
                    var mi = typeof(Toast).GetMethod("OnActivated", BindingFlags.NonPublic | BindingFlags.Static);
                    var d = Delegate.CreateDelegate(ev.EventHandlerType, mi, false);
                    if (d != null) ev.AddEventHandler(toast, d);
                }
                catch { }
                if (notifier == null) notifier = tMgr.GetMethod("CreateToastNotifier", new[] { typeof(string) }).Invoke(null, new object[] { Aumid });
                notifier.GetType().GetMethod("Show").Invoke(notifier, new[] { toast });
                return true;
            }
            catch (Exception ex)
            {
                Logger.Write(LogLevel.Poll, "toast: " + (ex.InnerException ?? ex).Message);
                return false;
            }
        }

        static void OnActivated(object sender, object args)
        {
            string a = "open";
            try { var p = args != null ? args.GetType().GetProperty("Arguments") : null; if (p != null) a = Convert.ToString(p.GetValue(args, null)); } catch { }
            var h = Activated; if (h != null) try { h(string.IsNullOrEmpty(a) ? "open" : a); } catch { }
        }
    }
}
