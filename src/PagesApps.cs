using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        void RefreshListsUi() { if (listCards != null) foreach (var c in listCards) if (!c.IsDisposed) c.Fill(); if (curPage == PgConnect) UpdateConnectUi(); }

        void AddToList(string key, string item)
        {
            if (string.IsNullOrWhiteSpace(item)) return;
            // одно и то же приложение не может быть и в «через VPN», и в исключениях этого же блока
            if (key == AppLists.HybridExcl) { AppLists.Remove(AppLists.HybridApps, new[] { item }); AppLists.Remove(AppLists.VpnOnlyApps, new[] { item }); }
            if (key == AppLists.HybridApps || key == AppLists.VpnOnlyApps) AppLists.Remove(AppLists.HybridExcl, new[] { item });
            if (key == AppLists.HybridApps) AppLists.Remove(AppLists.VpnOnlyApps, new[] { item });
            if (key == AppLists.VpnOnlyApps) AppLists.Remove(AppLists.HybridApps, new[] { item });
            if (key == AppLists.DpiApps) { AppLists.Remove(AppLists.HybridApps, new[] { item }); AppLists.Remove(AppLists.VpnOnlyApps, new[] { item }); }
            if (key == AppLists.HybridApps || key == AppLists.VpnOnlyApps) AppLists.Remove(AppLists.DpiApps, new[] { item });
            AppLists.PushRecent(item);
            if (AppLists.Add(key, item)) Logger.Write(LogLevel.Info, AppLists.Title(key) + Tx(": добавлено ") + item + (router.Active ? Tx(" — применяется без отключения") : ""));
        }

        string Prompt(string title, string label, string val)
        {
            using (var f = new Form { Text = title, StartPosition = FormStartPosition.CenterParent, Size = new Size(Theme.S(560), Theme.S(190)), BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.None })
            {
                var l = Ui.Hint(label, Theme.S(510)); l.Location = new Point(Theme.S(16), Theme.S(14));
                var tb = Ui.TextBox(val); tb.SetBounds(Theme.S(16), l.Bottom + Theme.S(8), Theme.S(510), tb.Height);
                var ok = new FlatButton("OK", true).AutoW(); ok.Location = new Point(Theme.S(526) - ok.Width, tb.Bottom + Theme.S(14));
                ok.Click += (s, e) => { f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.AddRange(new Control[] { l, tb, ok });
                f.AcceptButton = null;
                tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { f.DialogResult = DialogResult.OK; f.Close(); } };
                f.HandleCreated += (s, e) => Native.TitleTheme(f.Handle, Theme.IsDark);
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        // ================= Диспетчер задач =================
        DataGridView taskGrid; TextBox tbTaskSearch; ThemedCheck chkTasksAuto, chkTasksGroup, chkTasksNet; DarkCombo cbAddTarget; Label lTasks;
        volatile bool tasksBusy; DateTime lastTasks = DateTime.MinValue, tasksMsgUntil = DateTime.MinValue;

        class TaskItem { public string Key, Name, Path; public int Pid, Count; public double Cpu; public long Mem; public int Conns; }

        void BuildTasksPage()
        {
            var p = NewPage(Tx("Диспетчер задач"), Tx("Добавляйте процессы в списки без отключения VPN"));
            tbTaskSearch = Ui.TextBox(); tbTaskSearch.Width = Theme.S(240);
            var lSearch = Ui.Label(Tx("Поиск:"), Theme.Body, Theme.Muted);
            tbTaskSearch.TextChanged += (s, e) => RefreshTasks(true);
            chkTasksAuto = Ui.Check(Tx("Автообновление"), true); chkTasksAuto.BackColor = Theme.Bg;
            chkTasksGroup = Ui.Check(Tx("Группировать по имени"), true); chkTasksGroup.BackColor = Theme.Bg; chkTasksGroup.CheckedChanged += (s, e) => RefreshTasks(true);
            chkTasksNet = Ui.Check(Tx("Только с соединениями"), false); chkTasksNet.BackColor = Theme.Bg; chkTasksNet.CheckedChanged += (s, e) => RefreshTasks(true);
            cbAddTarget = new DarkCombo { Width = Theme.S(250) };
            foreach (var k in AppLists.All) cbAddTarget.Items.Add(AppLists.Title(k));
            cbAddTarget.SelectedIndex = 0;
            var bAdd = new FlatButton(Tx("Добавить выбранные"), true) { Glyph = "" }.AutoW();
            bAdd.Click += (s, e) => AddSelectedTasks(AppLists.All[Math.Max(0, cbAddTarget.SelectedIndex)], false);
            var bAddPath = new FlatButton(Tx("По полному пути")) { Glyph = "" }.AutoW();
            bAddPath.Click += (s, e) => AddSelectedTasks(AppLists.All[Math.Max(0, cbAddTarget.SelectedIndex)], true);
            var bKill = new FlatButton(Tx("Завершить процесс")) { Glyph = "", Danger = true }.AutoW();
            bKill.Click += (s, e) => KillSelectedTasks();
            var bRef = new FlatButton(Tx("Обновить")) { Glyph = "" }.AutoW(); bRef.Click += (s, e) => RefreshTasks(true);
            lTasks = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Bg, UseMnemonic = false };

            var card = new CardPanel();
            taskGrid = new DataGridView(); Ui.StyleGrid(taskGrid);
            taskGrid.Columns.Add("n", Tx("ИМЯ")); taskGrid.Columns.Add("pid", "PID"); taskGrid.Columns.Add("cpu", Tx("ЦП %")); taskGrid.Columns.Add("mem", Tx("ПАМЯТЬ"));
            taskGrid.Columns.Add("c", Tx("СОЕД.")); taskGrid.Columns.Add("r", Tx("МАРШРУТ")); taskGrid.Columns.Add("p", Tx("ПУТЬ"));
            float[] fw = { 18, 7, 7, 9, 6, 15, 38 };
            for (int i = 0; i < fw.Length; i++) taskGrid.Columns[i].FillWeight = fw[i];
            taskGrid.Columns[1].DefaultCellStyle.Alignment = taskGrid.Columns[2].DefaultCellStyle.Alignment = taskGrid.Columns[3].DefaultCellStyle.Alignment = taskGrid.Columns[4].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            taskGrid.Columns[6].DefaultCellStyle.ForeColor = Theme.Muted;
            taskGrid.SortCompare += (s, e) =>
            {
                if (e.Column.Index >= 1 && e.Column.Index <= 4)
                {
                    double a, b;
                    double.TryParse(Convert.ToString(e.CellValue1).Replace(Tx(" МБ"), "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out a);
                    double.TryParse(Convert.ToString(e.CellValue2).Replace(Tx(" МБ"), "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out b);
                    e.SortResult = a.CompareTo(b); e.Handled = true;
                }
            };
            var cm = new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body };
            foreach (var k in AppLists.All) { string key = k; cm.Items.Add(Tx("Добавить в «") + AppLists.Title(k) + "»", null, (s, e) => AddSelectedTasks(key, false)); }
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(Tx("Открыть расположение файла"), null, (s, e) => { var t = SelTasks().FirstOrDefault(); if (t != null && t.Path != "") Process.Start("explorer.exe", "/select,\"" + t.Path + "\""); });
            cm.Items.Add(Tx("Копировать путь"), null, (s, e) => { var t = SelTasks().FirstOrDefault(); if (t != null && t.Path != "") Clipboard.SetText(t.Path); });
            taskGrid.ContextMenuStrip = cm;
            taskGrid.CellMouseDown += (s, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !taskGrid.Rows[e.RowIndex].Selected) { taskGrid.ClearSelection(); taskGrid.Rows[e.RowIndex].Selected = true; } };
            card.Controls.Add(taskGrid);
            card.Resize += (s, e) => taskGrid.SetBounds(Theme.S(8), Theme.S(8), card.Width - Theme.S(16), card.Height - Theme.S(16));

            p.Controls.AddRange(new Control[] { lSearch, tbTaskSearch, chkTasksAuto, chkTasksGroup, chkTasksNet, bRef, cbAddTarget, bAdd, bAddPath, bKill, lTasks, card });
            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, y = Theme.S(86);
                lSearch.Location = new Point(L, y + Theme.S(4)); tbTaskSearch.Location = new Point(lSearch.Right + Theme.S(6), y + Theme.S(2));
                chkTasksAuto.Location = new Point(tbTaskSearch.Right + Theme.S(18), y + Theme.S(4));
                chkTasksGroup.Location = new Point(chkTasksAuto.Right + Theme.S(14), y + Theme.S(4));
                chkTasksNet.Location = new Point(chkTasksGroup.Right + Theme.S(14), y + Theme.S(4));
                bRef.Location = new Point(L + W - bRef.Width, y);
                y += Theme.S(42);
                int by = p.ClientSize.Height - Theme.S(20) - bAdd.Height;
                var lTo = cbAddTarget.Tag as Label;
                if (lTo == null) { lTo = Ui.Label(Tx("Добавить в:"), Theme.Body, Theme.Muted); p.Controls.Add(lTo); cbAddTarget.Tag = lTo; }
                lTo.Location = new Point(L, by + Theme.S(6));
                cbAddTarget.Location = new Point(lTo.Right + Theme.S(6), by + Theme.S(3));
                bAdd.Location = new Point(cbAddTarget.Right + Theme.S(8), by); bAddPath.Location = new Point(bAdd.Right + Theme.S(8), by);
                bKill.Location = new Point(L + W - bKill.Width, by);
                lTasks.SetBounds(bAddPath.Right + Theme.S(12), by + Theme.S(8), Math.Max(10, bKill.Left - bAddPath.Right - Theme.S(24)), Theme.S(18));
                card.SetBounds(L, y, W, by - Theme.S(12) - y);
            };
            p.Resize += (s, e) => lay(); lay();
        }

        List<TaskItem> SelTasks()
        {
            var l = new List<TaskItem>();
            foreach (DataGridViewRow r in taskGrid.SelectedRows) { var t = r.Tag as TaskItem; if (t != null) l.Add(t); }
            return l;
        }

        void AddSelectedTasks(string key, bool fullPath)
        {
            var sel = SelTasks();
            if (sel.Count == 0) { lTasks.Text = Tx("Выберите процессы в таблице."); return; }
            foreach (var t in sel)
            {
                if (t.Name.Equals("netprobe.exe", StringComparison.OrdinalIgnoreCase) || t.Name.StartsWith("netprobe-", StringComparison.OrdinalIgnoreCase)) continue;
                AddToList(key, fullPath && t.Path != "" ? t.Path : t.Name);
            }
            tasksMsgUntil = DateTime.Now.AddSeconds(6);
            lTasks.Text = Tx("Добавлено в «") + AppLists.Title(key) + "»: " + string.Join(", ", sel.Select(x => x.Name)) + (router.Active ? Tx(" · туннель перезапущен с новыми правилами") : "");
            RefreshTasks(true);
        }

        void KillSelectedTasks()
        {
            var sel = SelTasks(); if (sel.Count == 0) return;
            if (MessageBox.Show(this, Tx("Завершить: ") + string.Join(", ", sel.Select(x => x.Name + (x.Count > 1 ? " ×" + x.Count : ""))) + Tx("?\nНесохранённые данные в этих программах будут потеряны."), "NetProbe", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            foreach (var t in sel)
            {
                try
                {
                    if (t.Count > 1 || chkTasksGroup.Checked) foreach (var pr in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(t.Name))) { try { pr.Kill(); } catch { } pr.Dispose(); }
                    else using (var pr = Process.GetProcessById(t.Pid)) pr.Kill();
                    Logger.Write(LogLevel.Info, Tx("Процесс завершён: ") + t.Name);
                }
                catch (Exception ex) { Logger.Write(LogLevel.Warn, Tx("Не удалось завершить ") + t.Name + ": " + ex.Message); }
            }
            RefreshTasks(true);
        }

        string RouteOf(string name, string path, List<string> hyb, List<string> strict, List<string> ex)
        {
            Func<List<string>, bool> m = l => l.Any(r => ProcInfo.Matches(r, name, path));
            var mode = st.RMode; var s = router.State;
            if (name.StartsWith("netprobe-vpn", StringComparison.OrdinalIgnoreCase)) return "probe-vpn";
            if (name.StartsWith("netprobe-direct", StringComparison.OrdinalIgnoreCase)) return "probe-direct";
            if (m(strict)) return KillSwitch.Active ? "strict-block" : "strict";
            if (m(ex)) return "excl";
            if (mode == RouteMode.DpiOnly) return "dpi";
            bool inVpn = hyb.Count == 0 && strict.Count == 0 || m(hyb);
            if (!inVpn) return "direct";
            if (mode == RouteMode.Hybrid && s == RouteState.DpiFailover) return "dpi-fail";
            return "vpn";
        }

        void RefreshTasks(bool force = false)
        {
            if (taskGrid == null || taskGrid.IsDisposed || tasksBusy) return;
            if (!force && (DateTime.Now - lastTasks).TotalSeconds < 2) return;
            tasksBusy = true; lastTasks = DateTime.Now;
            bool group = chkTasksGroup.Checked, netOnly = chkTasksNet.Checked; string q = tbTaskSearch.Text.Trim();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<TaskItem> items = null;
                try
                {
                    var rows = ProcInfo.Snapshot();
                    var hyb = AppLists.Get(AppLists.HybridApps); var strict = AppLists.Get(AppLists.VpnOnlyApps);
                    if (group)
                        items = rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).Select(g => new TaskItem
                        {
                            Key = g.Key.ToLowerInvariant(), Name = g.First().Name, Pid = g.Min(x => x.Pid), Count = g.Count(), Cpu = g.Sum(x => x.Cpu), Mem = g.Sum(x => x.MemBytes), Conns = g.Sum(x => x.Conns),
                            Path = g.Select(x => x.Path).FirstOrDefault(x => x != "") ?? ""
                        }).ToList();
                    else items = rows.Select(r => new TaskItem { Key = r.Pid.ToString(), Name = r.Name, Pid = r.Pid, Count = 1, Cpu = r.Cpu, Mem = r.MemBytes, Conns = r.Conns, Path = r.Path }).ToList();
                    if (q != "") items = items.Where(t => t.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || t.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                    if (netOnly) items = items.Where(t => t.Conns > 0).ToList();
                    items = items.OrderByDescending(t => t.Conns > 0).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                }
                catch { }
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        tasksBusy = false;
                        if (items == null || taskGrid.IsDisposed) return;
                        var ex = st.RMode == RouteMode.VpnOnly ? AppLists.Get(AppLists.VpnOnlyExcl) : st.RMode == RouteMode.DpiOnly ? AppLists.Get(AppLists.DpiOnlyExcl) : AppLists.Get(AppLists.HybridExcl);
                        var hyb2 = AppLists.Get(AppLists.HybridApps); var strict2 = AppLists.Get(AppLists.VpnOnlyApps);
                        FillTasks(items, hyb2, strict2, ex);
                    }));
                }
                catch { tasksBusy = false; }
            });
        }

        void FillTasks(List<TaskItem> items, List<string> hyb, List<string> strict, List<string> ex)
        {
            var selKeys = new HashSet<string>(SelTasks().Select(t => t.Key));
            int first = taskGrid.FirstDisplayedScrollingRowIndex;
            var sortCol = taskGrid.SortedColumn; var sortDir = taskGrid.SortOrder;
            taskGrid.SuspendLayout();
            taskGrid.Rows.Clear();
            foreach (var t in items)
            {
                string route = RouteOf(t.Name, t.Path, hyb, strict, ex);
                string rtext = route == "probe-vpn" ? Tx("пробник VPN") : route == "probe-direct" ? Tx("пробник напрямую") : route == "strict-block" ? Tx("строго VPN · блок") : route == "strict" ? Tx("строго VPN") :
                               route == "excl" ? Tx("исключение") : route == "dpi" ? "DPI*" : route == "direct" ? Tx("напрямую") : route == "dpi-fail" ? Tx("DPI (авария)") : "VPN";
                int i = taskGrid.Rows.Add(t.Name + (t.Count > 1 ? "  ×" + t.Count : ""), t.Count > 1 ? "—" : t.Pid.ToString(), t.Cpu.ToString("0.0", CultureInfo.InvariantCulture), (t.Mem / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + Tx(" МБ"), t.Conns.ToString(), rtext, t.Path);
                var row = taskGrid.Rows[i]; row.Tag = t;
                row.Cells[5].Style.ForeColor = route == "vpn" || route == "strict" ? Theme.Accent : route.StartsWith("dpi") ? Theme.Yellow : route == "excl" ? Theme.Orange : route == "strict-block" ? Theme.Red : Theme.Muted;
                if (selKeys.Contains(t.Key)) row.Selected = true;
            }
            if (sortCol != null && sortDir != SortOrder.None) taskGrid.Sort(sortCol, sortDir == SortOrder.Ascending ? System.ComponentModel.ListSortDirection.Ascending : System.ComponentModel.ListSortDirection.Descending);
            if (first >= 0 && first < taskGrid.Rows.Count) try { taskGrid.FirstDisplayedScrollingRowIndex = first; } catch { }
            if (selKeys.Count == 0) taskGrid.ClearSelection();
            taskGrid.ResumeLayout();
            int conns = items.Sum(x => x.Conns);
            if (DateTime.Now > tasksMsgUntil) lTasks.Text = items.Count + " " + (chkTasksGroup.Checked ? Tx("приложений") : Tx("процессов")) + Tx(" · TCP-соединений: ") + conns;
        }
    }

    static class ControlExt
    {
        public static void PerformClick2(this Control c) { var m = typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); m.Invoke(c, new object[] { EventArgs.Empty }); }
    }
}
