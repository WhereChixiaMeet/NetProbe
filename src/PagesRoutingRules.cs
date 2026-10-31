using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NetProbe
{
    public partial class MainForm
    {
        bool CanEditTunSettings()
        {
            if (!TunSettingsLocked) return true;
            Notice.Show(this, "TUN settings", "Turn off the VPN before changing TUN settings."); return false;
        }
        void ShowRoutingRules()
        {
            using (var f = new RoutingSettingsDialog(st, CanEditTunSettings))
                if (f.ShowDialog(this) == DialogResult.OK) LoadConnectUi();
        }
    }

    // Shared look of the three routing windows (v2rayN: Routing settings → Rule set → Rule details).
    public class RoutingDialogBase : Form
    {
        protected RoutingDialogBase(string title, int w, int h)
        {
            Text = title; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body; KeyPreview = true;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None; MinimizeBox = false;
            ClientSize = new Size(Theme.S(w), Theme.S(h));
            HandleCreated += (s, e) => Native.TitleTheme(Handle, Theme.IsDark);
        }
        protected static FlowLayoutPanel Bar(DockStyle dock, int h = 46)
        {
            return new FlowLayoutPanel { Dock = dock, Height = Theme.S(h), Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), 0), WrapContents = false, BackColor = Color.Transparent };
        }
        protected static FlatButton Btn(Control parent, string text, Action click, string tip = null, bool primary = false)
        {
            var b = new FlatButton(text, primary).AutoW(); b.Click += (s, e) => click(); parent.Controls.Add(b);
            if (tip != null) Ui.Tip().SetToolTip(b, tip);
            return b;
        }
        protected static DataGridView Grid(params Tuple<string, float>[] cols)
        {
            var g = new DataGridView(); Ui.StyleGrid(g); g.Dock = DockStyle.Fill; g.MultiSelect = true;
            foreach (var c in cols) { int i = g.Columns.Add("c" + g.Columns.Count, c.Item1); g.Columns[i].FillWeight = c.Item2; g.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable; }
            g.CellMouseDown += (s, e) => { if (e.Button == MouseButtons.Right && e.RowIndex >= 0 && !g.Rows[e.RowIndex].Selected) { g.ClearSelection(); g.Rows[e.RowIndex].Selected = true; g.CurrentCell = g.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)]; } };
            return g;
        }
        protected static Tuple<string, float> Col(string t, float w) { return Tuple.Create(t, w); }
        protected static List<int> SelectedRows(DataGridView g) { return g.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).OrderBy(i => i).ToList(); }
        protected static void Select(DataGridView g, IEnumerable<int> rows)
        {
            g.ClearSelection(); bool first = true;
            foreach (int i in rows) if (i >= 0 && i < g.Rows.Count) { if (first) { g.CurrentCell = g.Rows[i].Cells[1]; first = false; } g.Rows[i].Selected = true; }
        }
        protected static ContextMenuStrip CtxMenu() { return new ContextMenuStrip { Renderer = new ThemedMenuRenderer(), Font = Theme.Body }; }
        protected static void Item(ContextMenuStrip m, string text, Action a, Keys keys = Keys.None)
        {
            var it = new ToolStripMenuItem(text, null, (s, e) => a()); if (keys != Keys.None) { it.ShortcutKeys = keys; it.ShowShortcutKeys = true; }
            m.Items.Add(it);
        }
        protected static string AskText(IWin32Window owner, string title, string label, string value)
        {
            using (var f = new RoutingDialogBase(title, 560, 130))
            {
                f.MaximizeBox = false; f.FormBorderStyle = FormBorderStyle.FixedDialog;
                var l = Ui.Label(label); l.Location = new Point(Theme.S(14), Theme.S(16)); f.Controls.Add(l);
                var tb = Ui.TextBox(value); tb.SetBounds(Theme.S(14), Theme.S(42), Theme.S(532), Theme.S(28)); f.Controls.Add(tb);
                string res = null;
                var ok = new FlatButton("OK", true).AutoW(); ok.Location = new Point(Theme.S(14), Theme.S(84)); ok.Click += (s, e) => { res = tb.Text.Trim(); f.Close(); }; f.Controls.Add(ok);
                var cancel = new FlatButton("Cancel").AutoW(); cancel.Location = new Point(ok.Right + Theme.S(8), Theme.S(84)); cancel.Click += (s, e) => f.Close(); f.Controls.Add(cancel);
                f.ShowDialog(owner); return res;
            }
        }
    }

    // v2rayN "Routing settings": domain strategy, list of rule sets, the active one is used.
    public sealed class RoutingSettingsDialog : RoutingDialogBase
    {
        readonly Settings settings;
        readonly Func<bool> canEdit;
        readonly DataGridView grid;
        readonly ThemedCheck enabled;
        readonly DarkCombo strategy = new DarkCombo();
        readonly TextBox probe;
        readonly Label probeResult, geoState;
        RoutingConfig cfg;
        bool loading = true;

        public RoutingSettingsDialog(Settings st, Func<bool> canEdit) : base("Routing settings · Configs", 1060, 640)
        {
            settings = st; this.canEdit = canEdit;
            MinimumSize = new Size(Theme.S(820), Theme.S(520));
            try { cfg = RoutingStore.Load(st); }
            catch (Exception ex) { cfg = new RoutingConfig { Sets = RoutingPresets.Builtin(st) }; cfg.ActiveId = cfg.Sets[0].Id; Notice.Show(this, "Routing", ex.Message + "\nBuilt-in rule sets are shown. The saved file will not change until you click Save."); }

            var top = Bar(DockStyle.Top, 44);
            enabled = Ui.Check("Use these rules for Configs (VLESS / VMess / Trojan)", st.ConfigRulesEnabled);
            enabled.CheckedChanged += (s, e) => { if (loading || canEdit()) return; loading = true; enabled.Checked = !enabled.Checked; loading = false; };
            top.Controls.Add(enabled);
            var lStr = Ui.Label("     Domain strategy"); lStr.Margin = new Padding(0, Theme.S(4), Theme.S(6), 0); top.Controls.Add(lStr);
            strategy.Width = Theme.S(150); foreach (var d in ConfigRouting.DomainStrategies) strategy.Items.Add(d); strategy.SelectedItem = cfg.DomainStrategy;
            strategy.SelectedIndexChanged += (s, e) => { if (strategy.UserPick && !canEdit()) { strategy.SelectedItem = cfg.DomainStrategy; return; } cfg.DomainStrategy = strategy.SelectedItem as string ?? "AsIs"; };
            Ui.Tip().SetToolTip(strategy, "AsIs: rules see the domain and the IP of the connection.\nIPIfNonMatch: no rule matched → resolve the domain, try the IP rules again.\nIPOnDemand: resolve the domain before the rules (a set can override this).");
            top.Controls.Add(strategy);

            var tools = Bar(DockStyle.Top, 44);
            Btn(tools, "+ Add", AddSet, "New rule set");
            Btn(tools, "Edit", EditSet);
            Btn(tools, "Set active", SetActive, "Use the selected set (Enter)");
            Btn(tools, "Remove", RemoveSets);
            Btn(tools, "Import built-in rules", ImportBuiltin, "Restore the built-in sets (RU direct, blocked in RU, all proxy, all direct). Your own sets stay.");
            Btn(tools, "RU geodata…", InstallRuGeo, "Download the RU lists of runetfreedom (geosite ru-blocked, ru-available-only-inside; geoip ru-blocked), as the v2rayN Russia preset");

            grid = Grid(Col("", 4), Col("REMARKS", 34), Col("COUNT", 7), Col("SORT", 6), Col("STRATEGY", 11), Col("URL", 38));
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) EditSet(); };
            var cm = CtxMenu();
            Item(cm, "Add", AddSet); Item(cm, "Edit", EditSet); Item(cm, "Set as active", SetActive);
            Item(cm, "Remove", RemoveSets, Keys.Delete); Item(cm, "Select all", () => grid.SelectAll(), Keys.Control | Keys.A);
            cm.Items.Add(new ToolStripSeparator()); Item(cm, "Import built-in rules", ImportBuiltin);
            grid.ContextMenuStrip = cm;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter && grid.Focused) { SetActive(); e.Handled = e.SuppressKeyPress = true; } };

            // RU sites detect: which rule takes a domain / IP and which RU lists contain it
            var detect = new Panel { Dock = DockStyle.Bottom, Height = Theme.S(96), Padding = new Padding(Theme.S(12), Theme.S(6), Theme.S(12), 0) };
            var lp = Ui.Label("RU sites detect"); lp.Location = new Point(Theme.S(12), Theme.S(12)); detect.Controls.Add(lp);
            probe = Ui.TextBox("yandex.ru"); probe.SetBounds(Theme.S(150), Theme.S(8), Theme.S(300), Theme.S(28)); detect.Controls.Add(probe);
            var bProbe = new FlatButton("Check", true).AutoW(); bProbe.Location = new Point(probe.Right + Theme.S(8), Theme.S(8)); bProbe.Click += (s, e) => Detect(); detect.Controls.Add(bProbe);
            Ui.Tip().SetToolTip(bProbe, "Domain or IP. Shows which rule of the active set takes it (assuming a TCP 443 TLS connection) and which RU lists contain it.");
            probe.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { Detect(); e.Handled = e.SuppressKeyPress = true; } };
            geoState = Ui.Label("", Theme.Small, Theme.Muted); geoState.Location = new Point(bProbe.Right + Theme.S(14), Theme.S(14)); detect.Controls.Add(geoState);
            probeResult = new Label { AutoSize = false, UseMnemonic = false, Font = Theme.Small, ForeColor = Theme.Text, BackColor = Color.Transparent };
            probeResult.SetBounds(Theme.S(12), Theme.S(42), Theme.S(1000), Theme.S(50)); probeResult.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top; detect.Controls.Add(probeResult);

            var bottom = Bar(DockStyle.Bottom, 50);
            Btn(bottom, "Save", Save, null, true); Btn(bottom, "Cancel", Close);
            var hint = Ui.Hint("Like v2rayN: only the active set (●) is used, first match wins, unmatched traffic goes to the proxy. Local networks, RouteX core processes and the VPN health probe keep fixed service rules above the set. Applies on the next connection; WireGuard and subscriptions do not use it.", Theme.S(1020));
            hint.Dock = DockStyle.Bottom; hint.Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0); hint.Height += Theme.S(6);

            Controls.Add(grid); Controls.Add(detect); Controls.Add(hint); Controls.Add(bottom); Controls.Add(tools); Controls.Add(top);
            Fill(cfg.Sets.IndexOf(cfg.Active));
            UpdateGeoState();
            loading = false;
        }

        List<RouteSet> Ordered { get { return cfg.Sets.OrderBy(s => s.Sort).ToList(); } }
        void Fill(int index)
        {
            cfg.Sets = Ordered; grid.Rows.Clear();
            var active = cfg.Active;
            foreach (var s in cfg.Sets)
            {
                int i = grid.Rows.Add(s == active ? "●" : "", s.Remarks, s.Rules.Count, s.Sort, s.DomainStrategy == "" ? "(global)" : s.DomainStrategy, s.Url);
                if (s == active) { grid.Rows[i].DefaultCellStyle.ForeColor = Theme.Accent; grid.Rows[i].DefaultCellStyle.Font = new Font(Theme.Body, FontStyle.Bold); }
            }
            Select(grid, new[] { Math.Max(0, Math.Min(index, cfg.Sets.Count - 1)) });
        }
        RouteSet Current { get { var r = SelectedRows(grid); return r.Count == 0 ? null : cfg.Sets[r[0]]; } }
        void AddSet()
        {
            if (!canEdit()) return;
            var s = new RouteSet { Remarks = "New rule set", Sort = cfg.Sets.Count == 0 ? 1 : cfg.Sets.Max(x => x.Sort) + 1 };
            using (var f = new RuleSetDialog(settings, cfg, s, canEdit)) if (f.ShowDialog(this) != DialogResult.OK) return;
            cfg.Sets.Add(s); Fill(Ordered.IndexOf(s));
        }
        void EditSet()
        {
            var cur = Current; if (cur == null) return;
            var copy = cur.Clone();
            using (var f = new RuleSetDialog(settings, cfg, copy, canEdit)) if (f.ShowDialog(this) != DialogResult.OK) return;
            cfg.Sets[cfg.Sets.IndexOf(cur)] = copy; Fill(Ordered.IndexOf(copy));
        }
        void SetActive() { var cur = Current; if (cur == null || !canEdit()) return; cfg.ActiveId = cur.Id; Fill(cfg.Sets.IndexOf(cur)); }
        void RemoveSets()
        {
            var rows = SelectedRows(grid); if (rows.Count == 0 || !canEdit()) return;
            if (rows.Count >= cfg.Sets.Count) { Notice.Show(this, "Routing", "At least one rule set must stay."); return; }
            bool d;
            if (Notice.Show(this, "Remove rule sets?", "Remove " + rows.Count + " rule set(s)?", new[] { "Remove", "Cancel" }, null, out d) != 0) return;
            var del = rows.Select(i => cfg.Sets[i]).ToList(); cfg.Sets.RemoveAll(del.Contains);
            if (cfg.Sets.All(s => s.Id != cfg.ActiveId)) cfg.ActiveId = cfg.Sets[0].Id;
            Fill(rows[0]);
        }
        void ImportBuiltin()
        {
            if (!canEdit()) return;
            bool d;
            if (Notice.Show(this, "Import built-in rules?", "Built-in sets are restored to their defaults (with the RU lists installed now). Your own sets are kept.", new[] { "Import", "Cancel" }, null, out d) != 0) return;
            var b = RoutingPresets.Builtin(settings);
            int sort = cfg.Sets.Where(s => b.All(x => x.Id != s.Id)).Select(s => s.Sort).DefaultIfEmpty(0).Max();
            cfg.Sets.RemoveAll(s => b.Any(x => x.Id == s.Id));
            foreach (var s in b) { s.Sort = ++sort; cfg.Sets.Add(s); }
            if (cfg.Sets.All(s => s.Id != cfg.ActiveId)) cfg.ActiveId = b[0].Id;
            Fill(cfg.Sets.IndexOf(cfg.Active));
        }
        void UpdateGeoState()
        {
            var have = RuDetect.SiteCategories.Where(c => GeoData.Has(settings, true, c)).Select(c => "geosite:" + c)
                .Concat(RuDetect.IpCategories.Where(c => GeoData.Has(settings, false, c)).Select(c => "geoip:" + c)).ToList();
            geoState.Text = have.Count == 0 ? "No RU lists installed (install Xray geodata / RU geodata)" : "Installed: " + string.Join(", ", have) + (RuDetect.RuGeoInstalled(settings) ? "" : "   ·   RU geodata: not installed");
        }
        void InstallRuGeo()
        {
            geoState.Text = "Downloading…";
            Task.Run(() => { try { return RuDetect.Install(settings, s => BeginInvoke((Action)(() => geoState.Text = s))); } catch (Exception ex) { return "RU geodata: " + ex.Message; } })
                .ContinueWith(t => { if (IsDisposed) return; BeginInvoke((Action)(() => { UpdateGeoState(); probeResult.Text = t.Result + ". Use “Import built-in rules” to rebuild the RU sets with these lists."; })); });
        }
        void Detect()
        {
            string q = probe.Text.Trim(); var set = cfg.Active; if (q == "" || set == null) return;
            probeResult.Text = "Checking " + q + "…";
            var c = cfg.Clone(); var st = settings.Clone();
            Task.Run(() => Describe(st, c, c.Active, q)).ContinueWith(t => { if (!IsDisposed) BeginInvoke((Action)(() => probeResult.Text = t.Exception != null ? t.Exception.GetBaseException().Message : t.Result)); });
        }
        // Text for the detect line: rule of the active set + RU lists.
        public static string Describe(Settings st, RoutingConfig c, RouteSet set, string q)
        {
            IPAddress ip; string host = null; IPAddress[] ips;
            q = q.Trim(); if (q.Contains("://")) { try { q = new Uri(q).Host; } catch { } }
            if (IPAddress.TryParse(q, out ip)) ips = new[] { ip };
            else
            {
                host = q.TrimEnd('/', '.');
                try { var t = Dns.GetHostAddressesAsync(host); ips = t.Wait(4000) ? t.Result : new IPAddress[0]; } catch { ips = new IPAddress[0]; }
                if (st.TunBlockIpv6) ips = ips.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
            }
            string route;
            try { route = Decide(st, c, set, host, ips.FirstOrDefault()); }
            catch (Exception ex) { route = "rules: " + ex.Message; }
            return (host ?? q) + (host != null ? " (" + (ips.Length == 0 ? "not resolved" : string.Join(", ", ips.Take(2).Select(a => a.ToString()))) + ")" : "") + "  →  " + route
                + "\r\n" + RuDetect.Report(st, host, ips);
        }
        static string Decide(Settings st, RoutingConfig c, RouteSet set, string host, IPAddress ip)
        {
            for (int i = 0; i < set.Rules.Count; i++)
            {
                var r = set.Rules[i]; if (!r.Enabled || r.RuleType == 2) continue;
                var routes = new List<object>(); ConfigRouting.AddRule(routes, null, r, st);
                foreach (Dictionary<string, object> m in routes)
                {
                    var hit = RoutingSnapshot.Matches(m, null, null, ip, host, 443, "tcp", host != null ? "tls" : null);
                    if (hit == true) return r.OutboundTag.ToUpperInvariant() + "  (rule " + (i + 1) + (r.Remarks != "" ? " «" + r.Remarks + "»" : "") + ")";
                }
            }
            return "PROXY  (no rule matched)";
        }
        void Save()
        {
            if (!canEdit()) return;
            try
            {
                var active = cfg.Active; if (active == null) throw new FormatException("Add a rule set.");
                cfg.ActiveId = active.Id;
                ConfigRouting.Validate(cfg, active, settings);
                RoutingStore.Save(cfg);
                settings.ConfigRulesEnabled = enabled.Checked; settings.Save();
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { Notice.Show(this, "Routing · " + (cfg.Active != null ? cfg.Active.Remarks : ""), ex.Message); }
        }
    }

    // v2rayN "Rule settings": one set, its rules in order, import / export.
    public sealed class RuleSetDialog : RoutingDialogBase
    {
        readonly Settings settings;
        readonly RoutingConfig cfg;
        readonly RouteSet set;
        readonly Func<bool> canEdit;
        readonly DataGridView grid;
        readonly TextBox remarks, url;
        readonly Spin sort;
        readonly DarkCombo strategy = new DarkCombo();
        List<RouteRule> rules;

        public RuleSetDialog(Settings st, RoutingConfig cfg, RouteSet set, Func<bool> canEdit) : base("Rule set settings", 1100, 660)
        {
            settings = st; this.cfg = cfg; this.set = set; this.canEdit = canEdit;
            MinimumSize = new Size(Theme.S(860), Theme.S(520));
            rules = set.Rules.Select(r => r.Clone()).ToList();

            var head = new Panel { Dock = DockStyle.Top, Height = Theme.S(84) };
            Func<string, int, int, Label> lab = (t, x, y) => { var l = Ui.Label(t); l.Location = new Point(Theme.S(x), Theme.S(y)); head.Controls.Add(l); return l; };
            lab("Remarks", 14, 16); remarks = Ui.TextBox(set.Remarks); remarks.SetBounds(Theme.S(110), Theme.S(12), Theme.S(330), Theme.S(28)); head.Controls.Add(remarks);
            lab("Sort", 460, 16); sort = Ui.Num(0, 9999, set.Sort); sort.Location = new Point(Theme.S(500), Theme.S(12)); head.Controls.Add(sort);
            lab("Domain strategy", 610, 16); strategy.SetBounds(Theme.S(740), Theme.S(12), Theme.S(160), Theme.S(28)); head.Controls.Add(strategy);
            strategy.Items.Add("(global)"); foreach (var d in ConfigRouting.DomainStrategies) strategy.Items.Add(d);
            strategy.SelectedItem = set.DomainStrategy == "" ? "(global)" : set.DomainStrategy;
            lab("Url", 14, 52); url = Ui.TextBox(set.Url); url.SetBounds(Theme.S(110), Theme.S(48), Theme.S(790), Theme.S(28)); head.Controls.Add(url);
            Ui.Tip().SetToolTip(url, "Source of “Import from URL”: a v2rayN / Xray JSON rule list");

            var tools = Bar(DockStyle.Top, 44);
            Btn(tools, "+ Add rule", AddRule);
            Btn(tools, "Import from file", () => { using (var d = new OpenFileDialog { Filter = "JSON|*.json;*.txt|All files|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) Import(() => File.ReadAllText(d.FileName)); });
            Btn(tools, "Import from clipboard", () => Import(() => Clipboard.GetText()));
            Btn(tools, "Import from URL", ImportUrl, "Download the rules from the Url above");
            Btn(tools, "Export selected", Export, "Copy the selected rules as JSON (v2rayN format)");

            grid = Grid(Col("ON", 4), Col("REMARKS", 16), Col("TYPE", 7), Col("OUTBOUND", 8), Col("PORT", 8), Col("PROTOCOL", 9), Col("INBOUND", 7), Col("NETWORK", 7), Col("DOMAIN / IP / PROCESS", 34));
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex > 0) EditRule(); };
            grid.CellClick += (s, e) => { if (e.RowIndex >= 0 && e.ColumnIndex == 0 && canEdit()) { rules[e.RowIndex].Enabled = !rules[e.RowIndex].Enabled; Fill(new[] { e.RowIndex }); } };
            var cm = CtxMenu();
            Item(cm, "Add rule", AddRule); Item(cm, "Edit", EditRule);
            Item(cm, "Remove", Remove, Keys.Delete); Item(cm, "Select all", () => grid.SelectAll(), Keys.Control | Keys.A);
            Item(cm, "Enable / disable", ToggleEnabled);
            Item(cm, "Export selected", Export);
            cm.Items.Add(new ToolStripSeparator());
            Item(cm, "Move to top", () => MoveRows(int.MinValue), Keys.Control | Keys.T); Item(cm, "Move up", () => MoveRows(-1), Keys.Control | Keys.U);
            Item(cm, "Move down", () => MoveRows(1), Keys.Control | Keys.D); Item(cm, "Move to bottom", () => MoveRows(int.MaxValue), Keys.Control | Keys.B);
            grid.ContextMenuStrip = cm;
            grid.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { EditRule(); e.Handled = e.SuppressKeyPress = true; } };

            var bottom = Bar(DockStyle.Bottom, 50);
            Btn(bottom, "Confirm", Confirm, null, true); Btn(bottom, "Cancel", Close);
            var hint = Ui.Hint("First match wins. Domain, IP and Process of one rule are alternatives; port / protocol / network must all match. A rule with only a port (0-65535) catches everything. Right-click: move, enable, export.", Theme.S(1060));
            hint.Dock = DockStyle.Bottom; hint.Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0);
            Controls.Add(grid); Controls.Add(hint); Controls.Add(bottom); Controls.Add(tools); Controls.Add(head);
            Fill(new[] { 0 });
        }

        static string Join(List<string> l) { return string.Join(",", l); }
        void Fill(IEnumerable<int> select)
        {
            grid.Rows.Clear();
            foreach (var r in rules)
            {
                var what = new List<string>();
                if (r.Domain.Count > 0) what.Add(Join(r.Domain)); if (r.Ip.Count > 0) what.Add(Join(r.Ip)); if (r.Process.Count > 0) what.Add(Join(r.Process));
                string text = string.Join("  |  ", what); if (text.Length > 300) text = text.Substring(0, 300) + "…";
                int i = grid.Rows.Add(r.Enabled ? "✓" : "", r.Remarks, ConfigRouting.RuleTypes[Math.Max(0, Math.Min(2, r.RuleType))], r.OutboundTag, r.Port, Join(r.Protocol), Join(r.InboundTag), r.Network, text);
                if (!r.Enabled) grid.Rows[i].DefaultCellStyle.ForeColor = Theme.Muted;
                else if (r.OutboundTag == "block") grid.Rows[i].Cells[3].Style.ForeColor = Theme.Red;
                else if (r.OutboundTag == "direct") grid.Rows[i].Cells[3].Style.ForeColor = Theme.Green;
                else grid.Rows[i].Cells[3].Style.ForeColor = Theme.Accent;
            }
            Select(grid, select);
        }
        void AddRule()
        {
            if (!canEdit()) return;
            var r = RuleDetailsDialog.Edit(this, settings, new RouteRule()); if (r == null) return;
            var sel = SelectedRows(grid); int at = sel.Count > 0 ? sel.Last() + 1 : rules.Count;
            rules.Insert(at, r); Fill(new[] { at });
        }
        void EditRule()
        {
            var sel = SelectedRows(grid); if (sel.Count == 0) return;
            if (!canEdit()) return;
            var r = RuleDetailsDialog.Edit(this, settings, rules[sel[0]].Clone()); if (r == null) return;
            rules[sel[0]] = r; Fill(new[] { sel[0] });
        }
        void Remove()
        {
            var sel = SelectedRows(grid); if (sel.Count == 0 || !canEdit()) return;
            bool d;
            if (Notice.Show(this, "Remove rules?", "Remove " + sel.Count + " rule(s)?", new[] { "Remove", "Cancel" }, null, out d) != 0) return;
            foreach (int i in sel.OrderByDescending(i => i)) rules.RemoveAt(i);
            Fill(new[] { Math.Min(sel[0], rules.Count - 1) });
        }
        void ToggleEnabled()
        {
            var sel = SelectedRows(grid); if (sel.Count == 0 || !canEdit()) return;
            bool on = !rules[sel[0]].Enabled; foreach (int i in sel) rules[i].Enabled = on; Fill(sel);
        }
        void MoveRows(int delta)
        {
            var sel = SelectedRows(grid); if (sel.Count == 0 || !canEdit()) return;
            var moving = sel.Select(i => rules[i]).ToList();
            int target = delta == int.MinValue ? 0 : delta == int.MaxValue ? rules.Count - moving.Count : Math.Max(0, Math.Min(rules.Count - moving.Count, sel[0] + delta));
            foreach (var r in moving) rules.Remove(r);
            rules.InsertRange(target, moving);
            Fill(Enumerable.Range(target, moving.Count));
        }
        void Import(Func<string> read)
        {
            if (!canEdit()) return;
            List<RouteRule> got;
            try { got = RoutingStore.ParseRules(read()); }
            catch (Exception ex) { Notice.Show(this, "Import rules", ex.Message); return; }
            Merge(got);
        }
        void Merge(List<RouteRule> got)
        {
            bool d; int replace = 1;
            if (rules.Count > 0) { replace = Notice.Show(this, "Import " + got.Count + " rule(s)", "Replace the current rules, or add the new ones at the end?", new[] { "Replace", "Add", "Cancel" }, null, out d); if (replace < 0 || replace == 2) return; }
            if (replace == 0 || rules.Count == 0) rules = got; else rules.AddRange(got);
            if (rules.Count > RoutingStore.MaxRules) { rules = rules.Take(RoutingStore.MaxRules).ToList(); Notice.Show(this, "Import rules", "Only the first " + RoutingStore.MaxRules + " rules are kept."); }
            Fill(new[] { 0 });
        }
        void ImportUrl()
        {
            if (!canEdit()) return;
            string u = url.Text.Trim();
            if (u == "") { u = AskText(this, "Import from URL", "Rule list URL (v2rayN / Xray JSON)", ""); if (string.IsNullOrEmpty(u)) return; url.Text = u; }
            Uri uri; if (!Uri.TryCreate(u, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) { Notice.Show(this, "Import from URL", "Enter an http(s) URL."); return; }
            Cursor = Cursors.WaitCursor; Enabled = false;
            Task.Run(() => ServerStore.Download(u, 30000)).ContinueWith(t => BeginInvoke((Action)(() =>
            {
                Cursor = Cursors.Default; Enabled = true;
                if (t.Exception != null) { Notice.Show(this, "Import from URL", t.Exception.GetBaseException().Message); return; }
                Import(() => t.Result);
            })));
        }
        void Export()
        {
            var sel = SelectedRows(grid); var list = (sel.Count > 0 ? sel.Select(i => rules[i]) : rules).ToList();
            if (list.Count == 0) return;
            Clipboard.SetText(RoutingStore.ExportRules(list));
            Notice.Show(this, "Export", list.Count + " rule(s) copied to the clipboard (v2rayN JSON).");
        }
        void Confirm()
        {
            if (!canEdit()) return;
            var draft = new RouteSet { Id = set.Id, Remarks = remarks.Text.Trim(), Url = url.Text.Trim(), Sort = (int)sort.Value, DomainStrategy = (strategy.SelectedItem as string) == "(global)" ? "" : strategy.SelectedItem as string ?? "", Rules = rules };
            if (draft.Remarks == "") { Notice.Show(this, "Rule set", "Enter remarks."); return; }
            try { ConfigRouting.Validate(cfg, draft, settings); }
            catch (Exception ex)
            {
                bool d;
                // like v2rayN, a set may be saved with rules that need geodata not installed yet; it only cannot be used then
                if (Notice.Show(this, "Rule set", ex.Message + "\n\nThis set cannot be used until this is fixed.", new[] { "Keep editing", "Save anyway" }, null, out d) != 1) return;
            }
            set.Remarks = draft.Remarks; set.Url = draft.Url; set.Sort = draft.Sort; set.DomainStrategy = draft.DomainStrategy; set.Rules = rules;
            DialogResult = DialogResult.OK; Close();
        }
    }

    // v2rayN "Rule details": remarks, type, outbound, port, protocol, inbound, network, Domain | IP | Process.
    public sealed class RuleDetailsDialog : RoutingDialogBase
    {
        RouteRule result;

        RuleDetailsDialog(Settings st, RouteRule r) : base("Rule details", 1000, 600)
        {
            MinimumSize = new Size(Theme.S(800), Theme.S(520));
            var head = new Panel { Dock = DockStyle.Top, Height = Theme.S(176) };
            Func<string, int, int, Label> lab = (t, x, y) => { var l = Ui.Label(t); l.Location = new Point(Theme.S(x), Theme.S(y)); head.Controls.Add(l); return l; };
            lab("Remarks", 14, 16); var remarks = Ui.TextBox(r.Remarks); remarks.SetBounds(Theme.S(120), Theme.S(12), Theme.S(330), Theme.S(28)); head.Controls.Add(remarks);
            var enabled = Ui.Check("Enabled", r.Enabled); enabled.Location = new Point(Theme.S(470), Theme.S(15)); head.Controls.Add(enabled);
            lab("Rule type", 600, 16); var type = new DarkCombo(); type.SetBounds(Theme.S(690), Theme.S(12), Theme.S(140), Theme.S(28)); head.Controls.Add(type);
            foreach (var t in ConfigRouting.RuleTypes) type.Items.Add(t); type.SelectedIndex = Math.Max(0, Math.Min(2, r.RuleType));
            Ui.Tip().SetToolTip(type, "ALL: connections and DNS · Routing: connections only · DNS: only which DNS server answers the domain");

            lab("outboundTag", 14, 52); var outbound = new DarkCombo(); outbound.SetBounds(Theme.S(120), Theme.S(48), Theme.S(160), Theme.S(28)); head.Controls.Add(outbound);
            foreach (var o in ConfigRouting.Outbounds) outbound.Items.Add(o); outbound.SelectedItem = r.OutboundTag; if (outbound.SelectedIndex < 0) outbound.SelectedIndex = 0;
            Ui.Tip().SetToolTip(outbound, "proxy = the VLESS / VMess / Trojan server · direct = without the server (DPI if enabled) · block = reject");
            lab("port", 300, 52); var port = Ui.TextBox(r.Port); port.SetBounds(Theme.S(350), Theme.S(48), Theme.S(200), Theme.S(28)); head.Controls.Add(port);
            Ui.Tip().SetToolTip(port, "53,443,1000-2000 · 0-65535 = all");
            lab("network", 600, 52); var network = new DarkCombo(); network.SetBounds(Theme.S(690), Theme.S(48), Theme.S(140), Theme.S(28)); head.Controls.Add(network);
            foreach (var n in ConfigRouting.Networks) network.Items.Add(n == "" ? "(any)" : n); network.SelectedItem = r.Network == "" ? "(any)" : r.Network; if (network.SelectedIndex < 0) network.SelectedIndex = 0;

            lab("protocol", 14, 88);
            var protos = new List<ThemedCheck>(); int px = 120;
            foreach (var p in ConfigRouting.Protocols) { var c = Ui.Check(p, r.Protocol.Contains(p)); c.Location = new Point(Theme.S(px), Theme.S(90)); head.Controls.Add(c); protos.Add(c); px += 110; }
            var extraProto = r.Protocol.Where(p => !ConfigRouting.Protocols.Contains(p)).ToList();
            lab("inboundTag", 600, 88); var inbound = Ui.TextBox(string.Join(",", r.InboundTag)); inbound.SetBounds(Theme.S(690), Theme.S(84), Theme.S(140), Theme.S(28)); head.Controls.Add(inbound);
            Ui.Tip().SetToolTip(inbound, "Empty = any. RouteX has one inbound, “tun”: a rule for another inbound (socks, http…) never matches.");
            var autoSort = Ui.Check("Auto sort", false); autoSort.Location = new Point(Theme.S(120), Theme.S(126)); head.Controls.Add(autoSort);
            Ui.Tip().SetToolTip(autoSort, "Sort and de-duplicate the Domain / IP / Process lists on Confirm");
            var tip = Ui.Hint("*Domain: domain:example.com (with subdomains), full:, keyword:, regexp:, geosite:category-ru; plain text = keyword.  IP: 1.2.3.0/24, geoip:ru, geoip:private, !geoip:ru = everything else.  Process: name.exe or full path.  One per line or comma-separated.", Theme.S(700));
            tip.Location = new Point(Theme.S(250), Theme.S(122)); head.Controls.Add(tip);

            var boxes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Padding = new Padding(Theme.S(10), 0, Theme.S(10), 0), BackColor = Color.Transparent };
            for (int i = 0; i < 3; i++) boxes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
            boxes.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.S(34))); boxes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Func<List<string>, TextBox> box = vals => { var tb = Ui.TextBox(string.Join("\r\n", vals), true); tb.Dock = DockStyle.Fill; return tb; };
            var domain = box(r.Domain); var ip = box(r.Ip);
            var procHead = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            var lProc = Ui.Label("PROCESS", Theme.Small, Theme.Muted); lProc.Margin = new Padding(0, Theme.S(12), Theme.S(8), 0); procHead.Controls.Add(lProc);
            var proc = box(r.Process);
            var pick = new FlatButton("+ App…").AutoW(); pick.Height = Theme.S(26); procHead.Controls.Add(pick);
            pick.Click += (s, e) =>
            {
                using (var d = new ProcPicker("Process", AppLists.Recent()))
                    if (d.ShowDialog(this) == DialogResult.OK && d.Result.Count > 0)
                        proc.Text = string.Join("\r\n", Lines(proc.Text).Concat(d.Result).Distinct(StringComparer.OrdinalIgnoreCase));
            };
            var lDom = Ui.Label("DOMAIN", Theme.Small, Theme.Muted); lDom.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            var lIp = Ui.Label("IP", Theme.Small, Theme.Muted); lIp.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
            boxes.Controls.Add(lDom, 0, 0); boxes.Controls.Add(lIp, 1, 0); boxes.Controls.Add(procHead, 2, 0);
            boxes.Controls.Add(domain, 0, 1); boxes.Controls.Add(ip, 1, 1); boxes.Controls.Add(proc, 2, 1);

            var bottom = Bar(DockStyle.Bottom, 50);
            Btn(bottom, "Confirm", () =>
            {
                var res = new RouteRule
                {
                    Remarks = remarks.Text.Trim(), Enabled = enabled.Checked, RuleType = Math.Max(0, type.SelectedIndex), OutboundTag = outbound.SelectedItem as string ?? "proxy",
                    Port = port.Text.Replace(" ", "").Trim(), Network = (network.SelectedItem as string) == "(any)" ? "" : network.SelectedItem as string ?? "",
                    Protocol = protos.Where(c => c.Checked).Select(c => c.Text).Concat(extraProto).ToList(), InboundTag = Lines(inbound.Text),
                    Domain = Lines(domain.Text), Ip = Lines(ip.Text), Process = Lines(proc.Text)
                };
                if (autoSort.Checked) { res.Domain = Sorted(res.Domain); res.Ip = Sorted(res.Ip); res.Process = Sorted(res.Process); }
                if (!res.HasMatch) { Notice.Show(this, "Rule details", "Fill in at least one of Domain, IP, Process, port, protocol or network (port 0-65535 = everything)."); return; }
                try { ConfigRouting.AddRule(new List<object>(), new List<object>(), new RouteRule { Remarks = res.Remarks, OutboundTag = res.OutboundTag, Port = res.Port, Network = res.Network, Protocol = res.Protocol, Domain = res.Domain, Ip = res.Ip, Process = res.Process }, st); }
                catch (Exception ex) { Notice.Show(this, "Rule details", ex.Message); return; }
                result = res; Close();
            }, null, true);
            Btn(bottom, "Cancel", Close);
            Controls.Add(boxes); Controls.Add(bottom); Controls.Add(head);
        }
        static List<string> Lines(string s) { return ConfigRouting.Tokens(s).Distinct().ToList(); }
        static List<string> Sorted(List<string> l) { return l.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
        public static RouteRule Edit(IWin32Window owner, Settings st, RouteRule r)
        {
            using (var f = new RuleDetailsDialog(st, r)) { f.ShowDialog(owner); return f.result; }
        }
    }
}
