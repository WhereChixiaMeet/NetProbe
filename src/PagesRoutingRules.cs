using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
            using (var f = new RoutingRulesDialog(st, CanEditTunSettings, () => TunLists.FromLists(
                AppLists.Get(AppLists.HybridApps).Concat(AppLists.Get(AppLists.VpnOnlyApps)), AppLists.Get(Router.TunExclKey(st)))))
                if (f.ShowDialog(this) == DialogResult.OK) LoadConnectUi();
        }
    }

    public sealed class RoutingRulesDialog : Form
    {
        readonly Settings settings;
        readonly Func<bool> canEdit;
        readonly Func<TunLists> lists;
        readonly DataGridView grid = new DataGridView();
        readonly ThemedCheck enabled;
        readonly DarkCombo fallback = new DarkCombo();
        List<ConfigRouteRule> rules;
        bool loading;

        public RoutingRulesDialog(Settings st, Func<bool> canEdit, Func<TunLists> lists)
        {
            settings = st; this.canEdit = canEdit; this.lists = lists;
            Text = "Routing rules · Configs"; BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body;
            StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(Theme.S(800), Theme.S(500)); ClientSize = new Size(Theme.S(1080), Theme.S(620));
            try { rules = ConfigRouting.Read(st.ConfigRulesJson); }
            catch (Exception ex) { rules = new List<ConfigRouteRule>(); Notice.Show(this, "Routing rules", ex.Message + "\nThe saved rules will not change until you click Save."); }
            enabled = Ui.Check("Use ordered rules for Configs (VLESS / VMess / Trojan)", st.ConfigRulesEnabled);
            enabled.CheckedChanged += (s, e) => { if (loading || canEdit()) return; loading = true; enabled.Checked = !enabled.Checked; loading = false; };
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = Theme.S(45), Padding = new Padding(Theme.S(12)), WrapContents = false };
            top.Controls.Add(enabled);
            var hint = Ui.Hint("First match wins. This table replaces Include/Exclude for Configs only. Local networks, RouteX core processes and the VPN health probe use fixed service rules. Direct uses DPI only when DPI is enabled and its rules match.", Theme.S(1000));
            hint.Dock = DockStyle.Top; hint.Height = Theme.S(60); hint.Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0);
            Ui.StyleGrid(grid); grid.MultiSelect = false; grid.Dock = DockStyle.Fill;
            grid.Columns.Add("enabled", "ON"); grid.Columns.Add("name", "NAME"); grid.Columns.Add("kind", "TYPE"); grid.Columns.Add("outbound", "OUTBOUND"); grid.Columns.Add("network", "NETWORK"); grid.Columns.Add("ports", "PORTS"); grid.Columns.Add("values", "DOMAIN / IP / PROCESS");
            float[] weights = { 5, 20, 9, 9, 8, 11, 38 }; for (int i = 0; i < weights.Length; i++) grid.Columns[i].FillWeight = weights[i];
            foreach (DataGridViewColumn c in grid.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) Edit(); };
            var tools = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(48), Padding = new Padding(Theme.S(10), Theme.S(7), 0, 0), WrapContents = false };
            Action<string, Action> button = (text, action) => { var b = new FlatButton(text).AutoW(); b.Click += (s, e) => action(); tools.Controls.Add(b); };
            button("Add", () => { if (!canEdit()) return; var r = EditRule(new ConfigRouteRule()); if (r != null) { rules.Add(r); Fill(rules.Count - 1); } });
            button("Edit", Edit);
            button("Enable / disable", () => { int i = Selected; if (i < 0 || !canEdit()) return; rules[i].Enabled = !rules[i].Enabled; Fill(i); });
            button("Delete", () => { int i = Selected; if (i < 0 || !canEdit()) return; rules.RemoveAt(i); Fill(Math.Min(i, rules.Count - 1)); });
            button("↑ Up", () => MoveRule(-1)); button("↓ Down", () => MoveRule(1));
            button("RU + current lists", () =>
            {
                if (!canEdit()) return;
                bool d;
                if (rules.Count > 0 && Notice.Show(this, "Replace draft rules?", "Replace this draft with RU → Direct and a snapshot of the current Include/Exclude lists?", new[] { "Replace", "Cancel" }, null, out d) != 0) return;
                rules = ConfigRouting.Preset(lists()); fallback.SelectedItem = "direct"; Fill(0);
            });
            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = Theme.S(50), Padding = new Padding(Theme.S(12), Theme.S(8), 0, 0), WrapContents = false };
            bottom.Controls.Add(Ui.Label("Unmatched traffic:", Theme.Body, Theme.Text));
            fallback.Width = Theme.S(140); foreach (var o in ConfigRouting.Outbounds) fallback.Items.Add(o); fallback.SelectedItem = st.ConfigRulesDefault; if (fallback.SelectedIndex < 0) fallback.SelectedIndex = 0;
            fallback.SelectedIndexChanged += (s, e) => { if (!fallback.UserPick || canEdit()) return; fallback.SelectedItem = st.ConfigRulesDefault; };
            bottom.Controls.Add(fallback);
            var save = new FlatButton("Save", true).AutoW(); save.Click += (s, e) => Save(); bottom.Controls.Add(save);
            var cancel = new FlatButton("Cancel").AutoW(); cancel.Click += (s, e) => Close(); bottom.Controls.Add(cancel);
            var dnsHint = Ui.Hint("DNS: domain rules follow this order. Windows DNS service requests may not carry the original app identity; the DNS through VPN setting controls their fallback. GeoIP / destination port rules apply to traffic after name resolution. Preset lists are copied, not linked.", Theme.S(1000));
            dnsHint.Dock = DockStyle.Bottom; dnsHint.Height = Theme.S(60); dnsHint.Padding = new Padding(Theme.S(12), 0, Theme.S(12), 0);
            Controls.Add(grid); Controls.Add(dnsHint); Controls.Add(tools); Controls.Add(bottom); Controls.Add(hint); Controls.Add(top);
            HandleCreated += (s, e) => Native.TitleTheme(Handle, Theme.IsDark);
            Fill(0);
        }
        int Selected { get { return grid.CurrentRow == null ? -1 : grid.CurrentRow.Index; } }
        void Fill(int index)
        {
            grid.Rows.Clear();
            foreach (var r in rules) grid.Rows.Add(r.Enabled ? "✓" : "", r.Name, r.Kind, r.Outbound, r.Network == "" ? "ALL" : r.Network, r.Ports == "" ? "ALL" : r.Ports, r.Kind == "all" ? "*" : r.Values);
            if (index >= 0 && index < grid.Rows.Count) { grid.CurrentCell = grid.Rows[index].Cells[1]; grid.Rows[index].Selected = true; }
        }
        void Edit() { int i = Selected; if (i < 0 || !canEdit()) return; var r = EditRule(rules[i]); if (r != null) { rules[i] = r; Fill(i); } }
        void MoveRule(int delta)
        {
            int i = Selected, j = i + delta; if (i < 0 || j < 0 || j >= rules.Count || !canEdit()) return;
            var r = rules[i]; rules.RemoveAt(i); rules.Insert(j, r); Fill(j);
        }
        void Save()
        {
            if (!canEdit()) return;
            try
            {
                if (rules.Count > 256) throw new FormatException("Maximum 256 rules.");
                foreach (var r in rules.Where(x => x.Enabled)) ConfigRouting.Matcher(r, settings);
                string json = ConfigRouting.Json().Serialize(rules);
                settings.ConfigRulesJson = json; settings.ConfigRulesEnabled = enabled.Checked;
                settings.ConfigRulesDefault = fallback.SelectedItem as string ?? "direct"; settings.Save();
                DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { Notice.Show(this, "Routing rules", ex.Message); }
        }
        ConfigRouteRule EditRule(ConfigRouteRule original)
        {
            using (var f = new Form { Text = "Routing rule", BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false, FormBorderStyle = FormBorderStyle.FixedDialog, AutoScaleMode = AutoScaleMode.None, ClientSize = new Size(Theme.S(660), Theme.S(450)) })
            {
                var name = Ui.TextBox(original.Name); var values = Ui.TextBox(original.Values); values.Multiline = true; values.ScrollBars = ScrollBars.Vertical;
                var ports = Ui.TextBox(original.Ports); var kind = new DarkCombo(); var outbound = new DarkCombo(); var network = new DarkCombo();
                foreach (var k in ConfigRouting.Kinds) kind.Items.Add(k); kind.SelectedItem = original.Kind;
                foreach (var o in ConfigRouting.Outbounds) outbound.Items.Add(o); outbound.SelectedItem = original.Outbound;
                foreach (var n in new[] { "ALL", "tcp", "udp" }) network.Items.Add(n); network.SelectedItem = original.Network == "" ? "ALL" : original.Network;
                int y = Theme.S(16), lw = Theme.S(130), width = Theme.S(490);
                Action<string, Control, int> row = (label, control, height) => { var l = Ui.Label(label, Theme.Body, Theme.Text); l.Location = new Point(Theme.S(16), y + Theme.S(4)); control.SetBounds(lw, y, width, Theme.S(height)); f.Controls.Add(l); f.Controls.Add(control); y += Theme.S(height + 10); };
                row("Name", name, 28); row("Type", kind, 28); row("Outbound", outbound, 28); row("Network", network, 28); row("Ports", ports, 28); row("Values", values, 100);
                var hint = Ui.Hint("Values: comma-separated or one per line. Domains: domain:ru, full:ya.ru, geosite:category-ru, keyword:, regexp:. IP: CIDR or geoip:ru. Processes: exact executable name or full path. Ports: 80,443,1000-2000; empty = all.", Theme.S(620)); hint.Location = new Point(Theme.S(16), y); f.Controls.Add(hint); y += hint.Height + Theme.S(10);
                ConfigRouteRule result = null;
                var ok = new FlatButton("Apply", true).AutoW(); ok.Location = new Point(lw, y); f.Controls.Add(ok);
                ok.Click += (s, e) =>
                {
                    if (!canEdit()) return;
                    try
                    {
                        var r = new ConfigRouteRule { Name = name.Text.Trim(), Values = values.Text.Trim(), Kind = kind.SelectedItem as string, Outbound = outbound.SelectedItem as string, Network = (network.SelectedItem as string) == "ALL" ? "" : network.SelectedItem as string, Ports = ports.Text.Trim(), Enabled = original.Enabled };
                        if (r.Name.Length == 0) throw new FormatException("Enter a rule name.");
                        ConfigRouting.Matcher(r, settings); result = r; f.Close();
                    }
                    catch (Exception ex) { Notice.Show(f, "Routing rule", ex.Message); }
                };
                var cancel = new FlatButton("Cancel").AutoW(); cancel.Location = new Point(ok.Right + Theme.S(10), y); cancel.Click += (s, e) => f.Close(); f.Controls.Add(cancel);
                f.ClientSize = new Size(f.ClientSize.Width, y + ok.Height + Theme.S(16));
                f.HandleCreated += (s, e) => Native.TitleTheme(f.Handle, Theme.IsDark);
                f.ShowDialog(this); return result;
            }
        }
    }
}
