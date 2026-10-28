using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

using static NetProbe.Tr;

namespace NetProbe
{
    public partial class MainForm
    {
        RichTextBox logBox; Slider pollSlider; Label pollValue, pollEff, lLogDir; ThemedCheck chkLogPolls, chkErrOnly, chkRouteOnly; DarkCombo cbLogDay, cbRetention;
        readonly List<Tuple<DateTime, LogLevel, string>> logItems = new List<Tuple<DateTime, LogLevel, string>>();
        readonly System.Windows.Forms.Timer pollSaveTimer = new System.Windows.Forms.Timer { Interval = 800 };
        bool pollSaveHooked, fillingDays;
        DateTime? viewDay; // null = сегодня (вживую)

        void BuildLogPage()
        {
            var p = NewPage(Tx("Лог и опрос"), Tx("События и частота опроса · лог по дням"));
            var pc = new CardPanel { Title = Tx("Частота опроса") };
            pollSlider = new Slider { Min = Settings.MinPollRate, Max = st.MaxPollRate, Step = 0.1, Value = st.PollRate };
            pollValue = new Label { AutoSize = false, Font = Theme.H2, ForeColor = Theme.Text, BackColor = Theme.Card, TextAlign = ContentAlignment.MiddleRight };
            var lim = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card };
            pollEff = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card };
            Action updLim = () =>
            {
                lim.Text = Tx("Предел ") + st.MaxPollRate.ToString("0.0", CultureInfo.InvariantCulture) + Tx(" опр/с на канал, без перекрытия, не чаще ") + Settings.MinIntervalMs + Tx(" мс; после ошибок интервал ×2 до ") + (Settings.MaxBackoffMs / 1000) + Tx(" с.");
            };
            updLim();
            pollValue.Text = PollText();
            pollSlider.ValueChanged += (s, e) =>
            {
                st.PollRate = pollSlider.Value; st.Clamp();
                pollValue.Text = PollText();
                pollSaveTimer.Stop(); pollSaveTimer.Start();
                monVpn.Kick(); monDir.Kick();
                UpdateSide();
            };
            if (!pollSaveHooked) { pollSaveHooked = true; pollSaveTimer.Tick += (s, e) => { pollSaveTimer.Stop(); st.Save(); Logger.Write(LogLevel.Info, Tx("Частота опроса: ") + PollText()); }; }
            // IP опроса VPN-канала и запись смены IP
            var lFam = new Label { Text = Tx("IP опроса VPN:"), AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card, UseMnemonic = false };
            var cbFam = new DarkCombo { Width = Theme.S(110) }; cbFam.Items.AddRange(new object[] { "IPv4", "IPv6", Tx("Авто") });
            cbFam.SelectedIndex = Math.Max(0, Math.Min(2, st.ProbeIpFamily));
            cbFam.SelectedIndexChanged += (s, e) => { if (!cbFam.UserPick) return; st.ProbeIpFamily = cbFam.SelectedIndex; st.Save(); RebuildProbers(); Logger.Write(LogLevel.Info, Tx("IP опроса VPN: ") + cbFam.SelectedItem); };
            var cNewIp = Ui.Check(Tx("В лог — только новый IP"), st.IpNewOnly); cNewIp.BackColor = Theme.Card;
            cNewIp.CheckedChanged += (s, e) => { st.IpNewOnly = cNewIp.Checked; st.Save(); };
            Ui.Tip().SetToolTip(cNewIp, Tx("«Публичный IP сменился» пишется только для адреса, которого ещё не было; если IP чередуется между уже виденными — одна строка «выходит через несколько IP»"));
            pc.Controls.AddRange(new Control[] { pollSlider, pollValue, lim, pollEff, lFam, cbFam, cNewIp });
            pc.Resize += (s, e) =>
            {
                int pad = Theme.S(18), w = pc.Width - pad * 2;
                pollValue.SetBounds(pc.Width - pad - Theme.S(300), Theme.S(8), Theme.S(300), Theme.S(28));
                pollSlider.SetBounds(pad - Theme.S(6), Theme.S(40), w + Theme.S(12), Theme.S(26));
                lim.SetBounds(pad, Theme.S(68), w, Theme.S(32));
                pollEff.SetBounds(pad, Theme.S(100), w, Theme.S(18));
                lFam.Location = new Point(pad, Theme.S(130)); cbFam.Location = new Point(lFam.Right + Theme.S(8), Theme.S(125));
                cNewIp.Location = new Point(cbFam.Right + Theme.S(18), Theme.S(128));
            };
            pollSlider.Tag = updLim;

            // --- хранение и папка лога ---
            var lcfg = new CardPanel { Title = Tx("Хранение лога") };
            cbRetention = new DarkCombo { Width = Theme.S(150) }; cbRetention.Items.AddRange(Settings.RetentionNames);
            cbRetention.SelectedIndex = st.LogRetention;
            cbRetention.SelectedIndexChanged += (s, e) =>
            {
                st.LogRetention = Math.Max(0, cbRetention.SelectedIndex); st.Save(); Logger.Retention = st.Retention;
                Logger.Write(LogLevel.Info, Tx("Срок хранения лога: ") + Settings.RetentionNames[st.LogRetention]);
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { Logger.Prune(); try { BeginInvoke((Action)(() => { FillLogDays(); RedrawLog(); })); } catch { } });
            };
            var lRet = new Label { Text = Tx("Хранить и показывать:"), AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card };
            lLogDir = new Label { AutoSize = false, Font = Theme.Small, ForeColor = Theme.Muted, BackColor = Theme.Card, AutoEllipsis = true, UseMnemonic = false };
            var bDir = new FlatButton(Tx("Изменить папку…")) { Glyph = "" }.AutoW();
            bDir.Click += (s, e) => ChangeLogDir();
            var bDirDef = new FlatButton(Tx("По умолчанию")) { Glyph = "" }.AutoW();
            bDirDef.Click += (s, e) => SetLogDir("");
            lcfg.Controls.AddRange(new Control[] { lRet, cbRetention, lLogDir, bDir, bDirDef });

            cbLogDay = new DarkCombo { Width = Theme.S(220) };
            cbLogDay.SelectedIndexChanged += (s, e) =>
            {
                if (fillingDays) return;
                var d = cbLogDay.SelectedItem as DayItem;
                viewDay = d == null || d.Day.Date == DateTime.Today ? (DateTime?)null : d.Day;
                RedrawLog();
            };
            var lDay = Ui.Label(Tx("День:"), Theme.Body, Theme.Muted);
            chkLogPolls = Ui.Check(Tx("Каждый опрос"), st.LogPolls); chkLogPolls.BackColor = Theme.Bg;
            chkLogPolls.CheckedChanged += (s, e) => { st.LogPolls = chkLogPolls.Checked; st.Save(); };
            chkErrOnly = Ui.Check(Tx("Ошибки"), false); chkErrOnly.BackColor = Theme.Bg;
            chkErrOnly.CheckedChanged += (s, e) => RedrawLog();
            chkRouteOnly = Ui.Check(Tx("Маршруты"), false); chkRouteOnly.BackColor = Theme.Bg;
            chkRouteOnly.CheckedChanged += (s, e) => RedrawLog();
            var bClear = new FlatButton(Tx("Очистить")) { Glyph = "" }.AutoW();
            bClear.Click += (s, e) => { if (viewDay == null) logItems.Clear(); logBox.Clear(); };
            var bSave = new FlatButton(Tx("Сохранить…")) { Glyph = "" }.AutoW();
            bSave.Click += (s, e) =>
            {
                using (var d = new SaveFileDialog { Filter = Tx("Текст (*.txt)|*.txt"), FileName = "netprobe-log-" + (viewDay ?? DateTime.Now).ToString("yyyyMMdd") + ".txt" })
                    if (d.ShowDialog(this) == DialogResult.OK) File.WriteAllText(d.FileName, string.Join("\r\n", ViewItems().Select(Fmt)), Encoding.UTF8);
            };
            var bFolder = new FlatButton(Tx("Открыть папку")) { Glyph = "" }.AutoW();
            bFolder.Click += (s, e) => { Process.Start("explorer.exe", "\"" + st.LogPath + "\""); };

            var lc = new CardPanel();
            logBox = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Theme.Classic ? Theme.Input : Theme.Card, ForeColor = Theme.Text, Font = Theme.Mono, WordWrap = false, DetectUrls = false, HideSelection = false };
            DarkScroll.Apply(logBox);
            lc.Controls.Add(logBox);
            lc.Resize += (s, e) => logBox.SetBounds(Theme.S(12), Theme.S(10), lc.Width - Theme.S(22), lc.Height - Theme.S(20));

            p.Controls.AddRange(new Control[] { pc, lcfg, lDay, cbLogDay, chkLogPolls, chkErrOnly, chkRouteOnly, bClear, bSave, bFolder, lc });
            Action lay = () =>
            {
                int L = Theme.S(26), W = p.ClientSize.Width - L * 2, y = Theme.S(86), gap = Theme.S(14);
                int hw = (W - gap) / 2;
                pc.SetBounds(L, y, hw, Theme.S(160));
                lcfg.SetBounds(L + hw + gap, y, W - hw - gap, Theme.S(160));
                int ip = Theme.S(16);
                lRet.Location = new Point(ip, Theme.S(48)); cbRetention.Location = new Point(lRet.Right + Theme.S(8), Theme.S(44));
                lLogDir.SetBounds(ip, Theme.S(76), lcfg.Width - ip * 2, Theme.S(16));
                bDir.Location = new Point(ip, Theme.S(92) - Theme.S(2)); bDir.Height = Theme.S(28); bDirDef.Location = new Point(bDir.Right + Theme.S(8), bDir.Top); bDirDef.Height = Theme.S(28);
                y = pc.Bottom + gap;
                lDay.Location = new Point(L, y + Theme.S(7)); cbLogDay.Location = new Point(lDay.Right + Theme.S(6), y + Theme.S(3));
                chkLogPolls.Location = new Point(cbLogDay.Right + Theme.S(18), y + Theme.S(7));
                chkErrOnly.Location = new Point(chkLogPolls.Right + Theme.S(14), y + Theme.S(7));
                chkRouteOnly.Location = new Point(chkErrOnly.Right + Theme.S(14), y + Theme.S(7));
                bFolder.Location = new Point(L + W - bFolder.Width, y);
                bSave.Location = new Point(bFolder.Left - Theme.S(8) - bSave.Width, y);
                bClear.Location = new Point(bSave.Left - Theme.S(8) - bClear.Width, y);
                y += Theme.S(44);
                lc.SetBounds(L, y, W, p.ClientSize.Height - Theme.S(20) - y);
            };
            p.Resize += (s, e) => lay();
            lay();
            UpdateLogDirLabel();
            if (logItems.Count == 0) logItems.AddRange(Logger.ReadDay(DateTime.Today));
            FillLogDays();
        }

        class DayItem { public DateTime Day; public override string ToString() { return Day.Date == DateTime.Today ? Tx("Сегодня (вживую)") : Day.ToString("dd.MM.yyyy, dddd", new CultureInfo("ru-RU")); } }

        void FillLogDays()
        {
            if (cbLogDay == null || cbLogDay.IsDisposed) return;
            fillingDays = true;
            var cut = DateTime.Now - st.Retention;
            var days = Logger.Days().Where(d => d.Date.AddDays(1) > cut).ToList();
            if (!days.Any(d => d.Date == DateTime.Today)) days.Insert(0, DateTime.Today);
            cbLogDay.Items.Clear();
            foreach (var d in days) cbLogDay.Items.Add(new DayItem { Day = d });
            int sel = 0;
            if (viewDay != null) for (int i = 0; i < days.Count; i++) if (days[i].Date == viewDay.Value.Date) sel = i;
            cbLogDay.SelectedIndex = sel;
            if (sel == 0) viewDay = null;
            fillingDays = false;
        }

        void UpdateLogDirLabel() { if (lLogDir != null && !lLogDir.IsDisposed) lLogDir.Text = Tx("Папка: ") + st.LogPath + (string.IsNullOrWhiteSpace(st.LogDir) ? Tx("  (по умолчанию)") : "") + Tx("  ·  файлы ГГГГ-ММ-ДД.log"); }

        void ChangeLogDir()
        {
            using (var d = new FolderBrowserDialog { Description = Tx("Папка для логов NetProbe"), SelectedPath = st.LogPath })
                if (d.ShowDialog(this) == DialogResult.OK) SetLogDir(d.SelectedPath);
        }

        void SetLogDir(string dir)
        {
            st.LogDir = dir; st.Save();
            Logger.Dir = st.LogPath;
            Logger.Write(LogLevel.Info, Tx("Папка лога: ") + st.LogPath);
            UpdateLogDirLabel(); FillLogDays();
            if (tbLogDirSet != null && !tbLogDirSet.IsDisposed) tbLogDirSet.Text = st.LogDir;
        }

        void UpdateEffective()
        {
            if (pollEff == null || pollEff.IsDisposed) return;
            Func<ChannelMonitor, string> f = m => m.EffectiveIntervalMs <= 0 ? "—" : ((int)m.EffectiveIntervalMs) + Tx(" мс") + (m.ConsecutiveFails > 0 ? Tx(" (откат)") : "");
            pollEff.Text = Tx("Текущий интервал:  VPN ") + f(monVpn) + Tx("   ·   напрямую ") + f(monDir);
        }

        static string Fmt(Tuple<DateTime, LogLevel, string> t) { return t.Item1.ToString("HH:mm:ss") + "  " + Logger.Tag(t.Item2) + "  " + t.Item3; }

        static Color LogColor(LogLevel lv)
        {
            switch (lv) { case LogLevel.Ok: return Theme.Green; case LogLevel.Warn: return Theme.Yellow; case LogLevel.Error: return Theme.Red; case LogLevel.Poll: return Theme.Muted; case LogLevel.Test: return Theme.Muted; case LogLevel.Route: return Theme.Text; default: return Theme.Text; }
        }

        bool LogVisible(Tuple<DateTime, LogLevel, string> it)
        {
            if (chkErrOnly != null && chkErrOnly.Checked && it.Item2 != LogLevel.Warn && it.Item2 != LogLevel.Error) return false;
            if (chkRouteOnly != null && chkRouteOnly.Checked && it.Item2 != LogLevel.Route) return false;
            return it.Item1 >= DateTime.Now - st.Retention;
        }

        IEnumerable<Tuple<DateTime, LogLevel, string>> ViewItems()
        {
            var src = viewDay == null ? (IEnumerable<Tuple<DateTime, LogLevel, string>>)logItems : Logger.ReadDay(viewDay.Value);
            return src.Where(LogVisible);
        }

        void AppendLog(DateTime t, LogLevel lv, string s)
        {
            if (logItems.Count > 0 && logItems[logItems.Count - 1].Item1.Date != t.Date) { logItems.Clear(); if (viewDay == null) { logBox.Clear(); FillLogDays(); } }
            var it = Tuple.Create(t, lv, s);
            logItems.Add(it);
            if (logItems.Count > 8000) { logItems.RemoveRange(0, 1000); RedrawLog(); return; }
            if (viewDay == null && logBox != null && !logBox.IsDisposed) AppendLine(it);
        }

        void AppendLine(Tuple<DateTime, LogLevel, string> it)
        {
            if (!LogVisible(it)) return;
            if (++shownLines > 4000) { RedrawLog(); return; } // окно не растёт бесконечно (быстрая перерисовка последних 3000)
            logBox.SelectionStart = logBox.TextLength; logBox.SelectionLength = 0;
            logBox.SelectionColor = Theme.Dim; logBox.AppendText(it.Item1.ToString("HH:mm:ss") + "  ");
            logBox.SelectionColor = LogColor(it.Item2); logBox.AppendText(Logger.Tag(it.Item2) + "  ");
            logBox.SelectionColor = it.Item2 == LogLevel.Poll ? Theme.Muted : Theme.Text; logBox.AppendText(it.Item3 + "\n");
            logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret();
        }

        // 3.2: перерисовка одним RTF-блоком (раньше — построчно с цветом и прокруткой: на тысячах строк окно висло)
        void RedrawLog()
        {
            if (logBox == null || logBox.IsDisposed) return;
            var items = ViewItems().ToList();
            var tail = items.Skip(Math.Max(0, items.Count - 3000)).ToList();
            var colors = new[] { Theme.Dim, Theme.Text, Theme.Muted, Theme.Green, Theme.Yellow, Theme.Red };
            Func<Color, int> ci = c => { for (int i = 0; i < colors.Length; i++) if (colors[i].ToArgb() == c.ToArgb()) return i + 1; return 2; };
            var sb = new StringBuilder(tail.Count * 90 + 512);
            sb.Append(@"{\rtf1\ansi\deff0{\fonttbl{\f0 " + logBox.Font.Name + @";}}{\colortbl ;");
            foreach (var c in colors) sb.Append(@"\red" + c.R + @"\green" + c.G + @"\blue" + c.B + ";");
            sb.Append(@"}\f0\fs" + (int)Math.Round(logBox.Font.SizeInPoints * 2) + " ");
            foreach (var it in tail)
            {
                sb.Append(@"\cf" + ci(Theme.Dim) + " ").Append(it.Item1.ToString("HH:mm:ss")).Append("  ");
                sb.Append(@"\cf" + ci(LogColor(it.Item2)) + " ").Append(Logger.Tag(it.Item2)).Append("  ");
                sb.Append(@"\cf" + (it.Item2 == LogLevel.Poll ? ci(Theme.Muted) : ci(Theme.Text)) + " ").Append(RtfEsc(it.Item3)).Append(@"\par ");
            }
            sb.Append("}");
            Native.SendMessage(logBox.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); // WM_SETREDRAW off
            try { logBox.Rtf = sb.ToString(); logBox.SelectionStart = logBox.TextLength; logBox.ScrollToCaret(); }
            finally { Native.SendMessage(logBox.Handle, 0x000B, (IntPtr)1, IntPtr.Zero); logBox.Invalidate(); }
            shownLines = tail.Count;
        }
        int shownLines;

        static string RtfEsc(string s)
        {
            var b = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                if (ch == '\\' || ch == '{' || ch == '}') b.Append('\\').Append(ch);
                else if (ch == '\n') b.Append(@"\line ");
                else if (ch == '\r') { }
                else if (ch > 127) b.Append(@"\u" + (int)(short)ch + "?");
                else b.Append(ch);
            }
            return b.ToString();
        }
    }
}
