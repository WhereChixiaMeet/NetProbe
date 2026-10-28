using System;
using System.Windows.Forms;

namespace NetProbe
{
    // Personal-темы: персонаж у главного окна (компаньон за рамкой) и у виджета. Только отрисовка, на работу не влияет.
    public partial class MainForm
    {
        CharOverlay charWin, charWid; Form charWidHost;

        bool charEdit; // режим «Разместить»: персонажей двигают мышкой

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
                bool wantWin = Theme.Personal && st.CharWindow && st.CharFrame && Visible && !hiddenToTray && WindowState == FormWindowState.Normal;
                if (wantWin)
                {
                    if (charWin == null || charWin.IsDisposed) { charWin = new CharOverlay(this, false) { Owner = this }; charWin.EditEnded += () => SetCharEdit(false); }
                    charWin.SetEdit(charEdit);
                    if (charWin.Place(st.CharOpacity)) { if (!charWin.Visible) charWin.Show(); }
                    else if (charWin.Visible) charWin.Hide();
                }
                else if (charWin != null && !charWin.IsDisposed && charWin.Visible) charWin.Hide();

                bool wantWid = Theme.Personal && st.CharWidget && widget != null && !widget.IsDisposed && widget.Visible;
                if (charWid != null && (charWid.IsDisposed || charWidHost != widget)) { try { charWid.Dispose(); } catch { } charWid = null; }
                if (wantWid)
                {
                    if (charWid == null) { charWid = new CharOverlay(widget, true) { Owner = widget }; charWidHost = widget; charWid.EditEnded += () => SetCharEdit(false); }
                    charWid.SetEdit(charEdit);
                    if (charWid.Place(st.CharOpacity)) { if (!charWid.Visible) charWid.Show(); }
                    else if (charWid.Visible) charWid.Hide();
                }
                else if (charWid != null && charWid.Visible) charWid.Hide();
            }
            catch (Exception ex) { Logger.Write(LogLevel.Poll, "character: " + ex.Message); }
        }

        // «Разместить»: персонаж у окна и у виджета становятся перетаскиваемыми (виджет на время показывается)
        Action charEditChanged;
        void SetCharEdit(bool on)
        {
            if (charEdit == on) return;
            charEdit = on;
            if (on && st.CharWidget && widget != null && !widget.IsDisposed && !widget.Visible) { widget.EnsureOnScreen(st.WidgetX, st.WidgetY); widget.Show(); }
            if (!on) ApplyWidgetVisibility();
            UpdateChars();
            var h = charEditChanged; if (h != null) h();
        }

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
        }
    }
}
