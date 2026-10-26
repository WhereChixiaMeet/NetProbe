using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using static NetProbe.Tr;

namespace NetProbe
{
    // Встроенный диспетчер задач для «+»: запущенные приложения с иконками, поиск, мультивыбор, ручной ввод
    public class ProcPicker : Form
    {
        readonly ThemedList list;
        readonly TextBox search, manual;
        List<ProcRow> rows = new List<ProcRow>();
        Dictionary<string, Tuple<int, int>> stats = new Dictionary<string, Tuple<int, int>>(StringComparer.OrdinalIgnoreCase);
        public List<string> Result = new List<string>();

        readonly List<string> recent;
        public ProcPicker(string title, List<string> recent = null)
        {
            this.recent = recent ?? new List<string>();
            Text = title; StartPosition = FormStartPosition.CenterParent; Size = new Size(Theme.S(520), Theme.S(600)); MinimumSize = new Size(Theme.S(420), Theme.S(400));
            BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body; MinimizeBox = false; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None; KeyPreview = true;
            search = Ui.TextBox();
            var lSearch = Ui.Label("", Theme.Icons, Theme.Muted);
            var cNet = Ui.Check(Tx("Только с соединениями"), false);
            list = new ThemedList { MultiSelect = true, ShowIcons = true, RightText = n => { if (recent.Contains(n, StringComparer.OrdinalIgnoreCase)) return Tx("недавно"); Tuple<int, int> t; return stats.TryGetValue(n, out t) ? (t.Item1 > 1 ? "×" + t.Item1 + "  " : "") + (t.Item2 > 0 ? t.Item2 + " TCP" : "") : null; } };
            var card = new CardPanel();
            card.Controls.Add(list);
            card.Resize += (s, e) => list.SetBounds(Theme.S(6), Theme.S(6), card.Width - Theme.S(12), card.Height - Theme.S(12));
            var lManual = Ui.Label(Tx("Вручную: имя процесса (Discord.exe), путь, домен или IP"), Theme.Small, Theme.Muted);
            manual = Ui.TextBox();
            var ok = new FlatButton(Tx("Добавить"), true) { Glyph = "" }.AutoW();
            var cancel = new FlatButton(Tx("Отмена")).AutoW();
            ok.Click += (s, e) => Accept();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            list.DoubleClick += (s, e) => Accept();
            MainForm.AttachPathMenu(list, this);
            search.TextChanged += (s, e) => Filter(cNet.Checked);
            cNet.CheckedChanged += (s, e) => Filter(cNet.Checked);
            Controls.AddRange(new Control[] { lSearch, search, cNet, card, lManual, manual, ok, cancel });
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); if (e.KeyCode == Keys.Enter && !manual.Focused) Accept(); };
            manual.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) Accept(); };
            Action lay = () =>
            {
                int p = Theme.S(14), W = ClientSize.Width - p * 2;
                lSearch.Location = new Point(p, p + Theme.S(4)); search.SetBounds(p + Theme.S(24), p, W - Theme.S(24), search.Height);
                cNet.Location = new Point(p, search.Bottom + Theme.S(8));
                int by = ClientSize.Height - p - ok.Height;
                ok.Location = new Point(ClientSize.Width - p - ok.Width, by); cancel.Location = new Point(ok.Left - Theme.S(8) - cancel.Width, by);
                manual.SetBounds(p, by - Theme.S(12) - manual.Height, W, manual.Height);
                lManual.Location = new Point(p, manual.Top - Theme.S(20));
                card.SetBounds(p, cNet.Bottom + Theme.S(8), W, lManual.Top - Theme.S(8) - cNet.Bottom - Theme.S(8));
            };
            Resize += (s, e) => lay(); lay();
            Shown += (s, e) => { search.Focus(); LoadProcs(cNet.Checked); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleTheme(Handle, Theme.IsDark); }

        void LoadProcs(bool net)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                List<ProcRow> r = null;
                try { r = ProcInfo.Snapshot(); } catch { }
                if (r == null) return;
                foreach (var g in r.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)) { var path = g.Select(x => x.Path).FirstOrDefault(x => x != ""); if (path != null) AppIcons.Seed(g.Key, path); }
                try { BeginInvoke((Action)(() => { rows = r; Filter(net); })); } catch { }
            });
        }

        static readonly string[] Hidden = { "svchost.exe", "System.exe", "Registry.exe", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe", "winlogon.exe", "fontdrvhost.exe", "dwm.exe", "conhost.exe", "Memory Compression.exe", "netprobe.exe", "netprobe-vpn.exe", "netprobe-direct.exe" };

        void Filter(bool net)
        {
            string q = search.Text.Trim();
            var groups = rows.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => !Hidden.Contains(g.Key, StringComparer.OrdinalIgnoreCase))
                .Select(g => new { Name = g.First().Name, Count = g.Count(), Conns = g.Sum(x => x.Conns), Path = g.Select(x => x.Path).FirstOrDefault(x => x != "") ?? "" })
                .Where(g => q == "" || g.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || g.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(g => !net || g.Conns > 0)
                .OrderByDescending(g => g.Conns > 0).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
            stats = groups.ToDictionary(g => g.Name, g => Tuple.Create(g.Count, g.Conns), StringComparer.OrdinalIgnoreCase);
            var keep = list.SelectedItems;
            list.Items.Clear();
            var rec = recent.Where(r => q == "" || r.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            list.Items.AddRange(rec);
            list.Items.AddRange(groups.Select(g => g.Name).Where(n => !rec.Contains(n, StringComparer.OrdinalIgnoreCase)));
            foreach (var r in rec) if (!stats.ContainsKey(r)) stats[r] = Tuple.Create(0, -1);
            list.EndUpdate();
            list.ClearSelected();
        }

        void Accept()
        {
            Result = list.SelectedItems;
            foreach (var x in manual.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) if (x.Trim() != "") Result.Add(x.Trim());
            if (Result.Count == 0) return;
            DialogResult = DialogResult.OK; Close();
        }
    }
}
