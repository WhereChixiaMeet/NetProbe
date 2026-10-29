using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    // Personal-темы: до 4 картинок-персонажей у главного окна (за рамкой) и у виджета. Только отрисовка, на работу не влияет.
    // Вне редактора картинки «прозрачны» для мыши — кнопки под ними всегда нажимаются. В редакторе их тянут мышкой,
    // колёсико — размер, ПКМ — меню (другая картинка / сбросить / убрать / готово).
    public partial class MainForm
    {
        readonly Dictionary<string, CharOverlay> chars = new Dictionary<string, CharOverlay>();

        bool charEdit; // режим редактора: персонажей двигают мышкой

        bool SlotOnWidget(string slot)
        {
            foreach (var part in (st.CharHosts ?? "").Split(';'))
            {
                int i = part.IndexOf('='); if (i <= 0) continue;
                if (part.Substring(0, i) == slot) return part.Substring(i + 1) == "d";
            }
            return slot == "widget" || slot == "pic4";
        }
        void SetSlotHost(string slot, bool onWidget)
        {
            var m = CharOverlay.Slots.ToDictionary(x => x, x => SlotOnWidget(x) ? "d" : "w");
            m[slot] = onWidget ? "d" : "w";
            st.CharHosts = string.Join(";", m.Select(kv => kv.Key + "=" + kv.Value)); st.Save();
        }
        static string SlotFile(string slot) { return Path.Combine(CharOverlay.Folder(Theme.Kind), slot + ".png"); }
        string LayoutKey(string slot) { return (Theme.Kurumi ? "kurumi." : "chisa.") + slot; }

        void UpdateChars()
        {
            if (IsDisposed || ShotsDir != null) return;
            if (CharOverlay.GetLayout == null)
            {
                CharOverlay.GetLayout = k => st.GetCharLayout(k);
                CharOverlay.SetLayout = (k, v) => { st.SetCharLayout(k, v); st.Save(); };
            }
            try
            {
                bool winOk = Theme.Personal && st.CharWindow && st.CharFrame && Visible && !hiddenToTray && WindowState == FormWindowState.Normal;
                bool widOk = Theme.Personal && st.CharWidget && widget != null && !widget.IsDisposed && widget.Visible;
                foreach (var slot in CharOverlay.Slots)
                {
                    bool onWid = SlotOnWidget(slot);
                    Form host = onWid ? (Form)widget : this;
                    CharOverlay o;
                    chars.TryGetValue(slot, out o);
                    // хозяин сменился (пересборка виджета, картинку перенесли окно ⇄ виджет) — окно персонажа пересоздаём
                    if (o != null && (o.IsDisposed || o.Host != host)) { try { o.Dispose(); } catch { } chars.Remove(slot); o = null; }
                    bool want = (onWid ? widOk : winOk) && File.Exists(SlotFile(slot));
                    if (!want) { if (o != null && o.Visible) o.Hide(); continue; }
                    if (o == null)
                    {
                        o = new CharOverlay(host, onWid, slot) { Owner = host };
                        o.MenuPicked += OnCharMenu;
                        chars[slot] = o;
                    }
                    o.SetEdit(charEdit);
                    if (o.Place(st.CharOpacity)) { if (!o.Visible) o.Show(); }
                    else if (o.Visible) o.Hide();
                }
            }
            catch (Exception ex) { Logger.Write(LogLevel.Poll, "character: " + ex.Message); }
        }

        void OnCharMenu(CharOverlay o, string what)
        {
            string slot = o.Slot;
            if (what == "done") { SetCharEdit(false); return; }
            if (what == "reset") { st.SetCharLayout(LayoutKey(slot), null); st.Save(); UpdateChars(); return; }
            if (what == "hide") { try { File.Delete(SlotFile(slot)); } catch { } UpdateChars(); var h = charEditChanged; if (h != null) h(); return; }
            if (what == "pick")
                using (var d = new OpenFileDialog { Filter = "PNG (*.png)|*.png", Title = Tx("Картинка персонажа (PNG с прозрачностью)") })
                    if (d.ShowDialog(this) == DialogResult.OK) SetCharPicture(slot, d.FileName);
        }

        // поставить картинку в слот (копия в папку темы; дата файла — сейчас, чтобы картинка точно перечиталась)
        bool SetCharPicture(string slot, string file)
        {
            try
            {
                using (var test = new System.Drawing.Bitmap(file)) { } // битый файл — сразу понятная ошибка
                string dst = SlotFile(slot);
                File.Copy(file, dst, true);
                File.SetLastWriteTimeUtc(dst, DateTime.UtcNow);
                Logger.Write(LogLevel.Info, Tx("Персонаж: ") + Path.GetFileName(file) + " → " + slot);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RouteX"); return false; }
            if (SlotOnWidget(slot)) st.CharWidget = true; else st.CharWindow = true;
            st.Save();
            UpdateChars();
            var h = charEditChanged; if (h != null) h();
            return true;
        }

        // PNG, брошенный на окно или виджет: в первый свободный слот этого хозяина, иначе заменяет первый
        void DropCharPicture(string file, bool onWidget)
        {
            if (!file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;
            if (!Theme.Personal)
            {
                Notice.Show(this, Tx("Персонаж"), Tx("Персонажи работают в темах Chisa и Kurumi — выбери тему в Settings → Оформление."));
                return;
            }
            if (!onWidget && !st.CharFrame) // персонаж «внутри окна» — это фон страниц
            {
                try { File.Copy(file, Path.Combine(CharOverlay.Folder(Theme.Kind), "window.png"), true); File.SetLastWriteTimeUtc(Path.Combine(CharOverlay.Folder(Theme.Kind), "window.png"), DateTime.UtcNow); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "RouteX"); return; }
                ResetPageBackground(); RebuildUi(); return;
            }
            var mine = CharOverlay.Slots.Where(x => SlotOnWidget(x) == onWidget).ToList();
            string slot = mine.FirstOrDefault(x => !File.Exists(SlotFile(x))) ?? mine.FirstOrDefault();
            if (slot == null) { slot = CharOverlay.Slots.FirstOrDefault(x => !File.Exists(SlotFile(x))) ?? CharOverlay.Slots[onWidget ? 1 : 0]; SetSlotHost(slot, onWidget); }
            SetCharPicture(slot, file);
        }

        // режим редактора: все картинки у окна и у виджета можно двигать (виджет на время показывается)
        Action charEditChanged;
        void SetCharEdit(bool on)
        {
            if (charEdit == on) return;
            if (on && st.UiLocked) { Notice.Show(this, Tx("Интерфейс заблокирован"), Tx("Сними «Lock interface» в Настройках → Оформление, чтобы двигать персонажей.")); return; }
            charEdit = on;
            if (on && st.CharWidget && widget != null && !widget.IsDisposed && !widget.Visible) { widget.EnsureOnScreen(st.WidgetX, st.WidgetY); widget.Show(); }
            if (!on) ApplyWidgetVisibility();
            UpdateChars();
            if (miCharEdit != null) miCharEdit.Checked = on;
            var h = charEditChanged; if (h != null) h();
        }
        ToolStripMenuItem miCharEdit;

        // много событий подряд (перетаскивание, пересборка интерфейса) — одно обновление персонажей
        bool charsQueued;
        void QueueChars()
        {
            if (charsQueued || IsDisposed || !IsHandleCreated) return;
            charsQueued = true;
            try { BeginInvoke((Action)(() => { charsQueued = false; UpdateChars(); })); } catch { charsQueued = false; }
        }

        void HookChars()
        {
            EventHandler h = (s, e) => QueueChars();
            LocationChanged += h; SizeChanged += h; VisibleChanged += h;
        }

        void HookWidgetChars(WidgetForm w)
        {
            EventHandler h = (s, e) => QueueChars();
            w.LocationChanged += h; w.VisibleChanged += h; w.Moved += h;
            w.FileDropped += f => DropCharPicture(f, true);
        }
    }

    // Перетаскивание файлов из Проводника в окно, запущенное от администратора: OLE-drag&drop между уровнями прав
    // Windows блокирует, поэтому — старый WM_DROPFILES с разрешённым фильтром сообщений.
    public static class FileDrop
    {
        [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr h, bool accept);
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern uint DragQueryFile(IntPtr hDrop, uint i, System.Text.StringBuilder f, uint size);
        [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern void DragFinish(IntPtr hDrop);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr p);
        public const int WM_DROPFILES = 0x233;
        public static void Enable(IntPtr h)
        {
            try
            {
                ChangeWindowMessageFilterEx(h, WM_DROPFILES, 1, IntPtr.Zero);
                ChangeWindowMessageFilterEx(h, 0x004A, 1, IntPtr.Zero); // WM_COPYDATA
                ChangeWindowMessageFilterEx(h, 0x0049, 1, IntPtr.Zero); // WM_COPYGLOBALDATA
                DragAcceptFiles(h, true);
            }
            catch { }
        }
        public static List<string> Files(IntPtr hDrop)
        {
            var l = new List<string>();
            try
            {
                uint n = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
                for (uint i = 0; i < n; i++) { var sb = new System.Text.StringBuilder(1024); DragQueryFile(hDrop, i, sb, (uint)sb.Capacity); l.Add(sb.ToString()); }
            }
            catch { }
            finally { try { DragFinish(hDrop); } catch { } }
            return l;
        }
    }
}
