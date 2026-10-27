using System;
using System.Windows.Forms;

namespace NetProbe
{
    // Personal-темы: персонаж у главного окна (компаньон за рамкой) и у виджета. Только отрисовка, на работу не влияет.
    public partial class MainForm
    {
        CharOverlay charWin, charWid; Form charWidHost;

        void UpdateChars()
        {
            if (IsDisposed || ShotsDir != null) return;
            try
            {
                bool wantWin = Theme.Personal && st.CharWindow && st.CharFrame && Visible && !hiddenToTray && WindowState == FormWindowState.Normal;
                if (wantWin)
                {
                    if (charWin == null || charWin.IsDisposed) charWin = new CharOverlay(this, false) { Owner = this };
                    if (charWin.Place(st.CharOpacity)) { if (!charWin.Visible) charWin.Show(); }
                    else if (charWin.Visible) charWin.Hide();
                }
                else if (charWin != null && !charWin.IsDisposed && charWin.Visible) charWin.Hide();

                bool wantWid = Theme.Personal && st.CharWidget && widget != null && !widget.IsDisposed && widget.Visible;
                if (charWid != null && (charWid.IsDisposed || charWidHost != widget)) { try { charWid.Dispose(); } catch { } charWid = null; }
                if (wantWid)
                {
                    if (charWid == null) { charWid = new CharOverlay(widget, true) { Owner = widget }; charWidHost = widget; }
                    if (charWid.Place(st.CharOpacity)) { if (!charWid.Visible) charWid.Show(); }
                    else if (charWid.Visible) charWid.Hide();
                }
                else if (charWid != null && charWid.Visible) charWid.Hide();
            }
            catch (Exception ex) { Logger.Write(LogLevel.Poll, "character: " + ex.Message); }
        }

        void HookChars()
        {
            EventHandler h = (s, e) => UpdateChars();
            LocationChanged += h; SizeChanged += h; VisibleChanged += h;
        }

        void HookWidgetChars(WidgetForm w)
        {
            EventHandler h = (s, e) => UpdateChars();
            w.LocationChanged += h; w.VisibleChanged += h; w.Moved += h;
        }
    }
}
