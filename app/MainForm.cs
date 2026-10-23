// The main window and the tray icon (docs/NATIVE_UI.md "Main window", "Tray").
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Arrow
{
    public class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetFocus();
        const int WM_CLIPBOARDUPDATE = 0x031D;
        const int Samples = 86; // 60 s of 700 ms polls
        static readonly Regex LinkRx = new Regex(@"^https?://\S+$", RegexOptions.IgnoreCase);
        static readonly Regex BatchRx = new Regex(@"\[[0-9]+-[0-9]+\]");
        static readonly string[] WhenDoneValues = { "", "sleep", "shutdown" };
        static readonly string[] Library = { "all", "active", "finished", "failed", "scheduled" };
        static readonly string[] BuiltinTypes = { "Video", "Music", "Programs", "Documents", "Compressed" }; // their icons are keyed by name

        // A dock area with a token background and hairlines
        class Area : Panel
        {
            public Func<Color> Back;
            public bool LineTop, LineBottom;
            public Area(Func<Color> back) { Back = back; DoubleBuffered = true; ResizeRedraw = true; BackColor = back(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (LineTop) Theme.HLine(e.Graphics, 0, Width, 0);
                if (LineBottom) Theme.HLine(e.Graphics, 0, Width, Height - 1);
            }
        }

        readonly Api api;
        readonly Poller poller;
        readonly string shots;
        bool visible, quitting, firstPoll = true, offline, counting, clipListening, trayHint;
        int openSeen, polls;
        State state;
        List<Download> all = new List<Download>();
        string section = "all", offered, lastClip, shownWhenDone, stripKind, balloonId, freeText;
        string wantSection, sidebarKey, sortColumn; // wantSection: ui.json's section until the first state builds its queue or type
        bool sortDesc, details; // details: the details panel (the inspector), hidden by default
        FormWindowState shownState = FormWindowState.Normal;
        Dictionary<string, string> statuses = new Dictionary<string, string>();
        readonly HashSet<string> pendingSeen = new HashSet<string>();
        readonly Dictionary<string, List<double>> speeds = new Dictionary<string, List<double>>();
        readonly List<double> totals = new List<double>();

        readonly ToolTip tips = new ToolTip();
        readonly Area root, top, commands, listPanel, statusBar;
        readonly Canvas logo, overlay;
        readonly TextField paste = new TextField(Glyph.Link, "Paste a link or drop files");
        readonly TextField search = new TextField(Glyph.Search, "Search downloads");
        readonly FlatButton download = new FlatButton("Download", Glyph.Download, ButtonKind.Primary);
        readonly FlatButton addLinks, options, more;
        // the command bar; labels keeps each one's text for when the bar shows glyphs only
        readonly FlatButton resume, pause, delete, pauseAll, deleteDone, startQueue, stopQueue, scheduler, speedLimit, sortButton, viewButton;
        readonly Dictionary<FlatButton, string> labels = new Dictionary<FlatButton, string>();
        readonly List<int> separators = new List<int>();
        readonly FlatButton chip = new FlatButton("", '\0', ButtonKind.Secondary) { Font = Theme.Small, Radius = Theme.Px(10), Visible = false }; // until the first state
        readonly Caption nextStart = new Caption(Theme.Small, () => Theme.Muted);
        readonly Strip strip = new Strip { Dock = DockStyle.Top, Visible = false };
        readonly Sidebar sidebar = new Sidebar { Dock = DockStyle.Left };
        readonly DownloadList list = new DownloadList();
        readonly Inspector inspector;
        readonly Caption statusText = new Caption(Theme.Small, () => Theme.Muted);
        readonly Caption whenDoneLabel = new Caption(Theme.Small, () => Theme.Muted) { Text = "When done:" };
        readonly Picker whenDone = new Picker { AccessibleName = "When done" };
        readonly NotifyIcon tray;
        readonly Image logoSmall, logoBig;
        Bitmap snapshot;
        readonly Action onTheme, onShow;

        public MainForm(Api api, bool show, string shots)
        {
            this.api = api;
            this.shots = shots;
            if (AppIcon.Window != null) Icon = AppIcon.Window;
            visible = show;
            Text = Program.Name;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(Theme.Px(900), Theme.Px(560));
            Size = new Size(Theme.Px(1180), Theme.Px(700));
            StartPosition = FormStartPosition.CenterScreen;
            logoSmall = Logo(24);
            logoBig = Logo(64);

            addLinks = IconButton(Glyph.List, "Add several links");
            options = IconButton(Glyph.Options, "Options");
            more = IconButton(Glyph.More, "More");
            download.Height = Theme.Px(34);
            download.Width = download.PreferredWidth;
            logo = new Canvas((g, r) => { if (logoSmall != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(logoSmall, r); } }) { AccessibleName = Program.Name };
            top = new Area(() => Theme.Top) { Dock = DockStyle.Top, Height = Theme.Px(56), LineBottom = true };
            top.Controls.AddRange(new Control[] { logo, paste, download, addLinks, options, more });
            top.Layout += (s, e) => LayoutTop();
            paste.AccessibleName = "Link";
            paste.Box.AccessibleName = "Paste a link";
            paste.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(paste.Text); } };
            paste.Box.TextChanged += (s, e) => { if (paste.Invalid) { paste.Invalid = false; tips.Hide(paste); } };
            download.Click += (s, e) => Submit(paste.Text);
            addLinks.Click += (s, e) => Dialogs.AddLinks(api, this, "");
            options.Click += (s, e) => OpenOptions(false);
            more.Click += (s, e) => MoreMenu().Show(more, new Point(0, more.Height + Theme.Px(2)));

            resume = Command("Resume", Glyph.Play, () => Resume(list.Selected));
            pause = Command("Pause", Glyph.Pause, () => Pause(list.Selected));
            delete = Command("Delete", Glyph.Delete, () => Remove(list.Selected));
            pauseAll = Command("Pause all", Glyph.Pause, () => Run(() => api.Post("/api/pause-all", null)));
            deleteDone = Command("Delete completed", Glyph.Delete, DeleteCompleted);
            startQueue = Command("Start queue", Glyph.Play, () => QueueCommand(true));
            stopQueue = Command("Stop queue", Glyph.Stop, () => QueueCommand(false));
            scheduler = Command("Scheduler", Glyph.Clock, () => OpenOptions(true)); // the Options' schedule until the Scheduler window exists
            speedLimit = Command("Speed limit: Off", Glyph.SpeedLimit, SpeedMenu);
            speedLimit.Chevron = true;
            commands = new Area(() => Theme.Top) { Dock = DockStyle.Top, Height = Theme.Px(44), LineBottom = true };
            commands.Controls.AddRange(new Control[] { resume, pause, delete, pauseAll, deleteDone, startQueue, stopQueue, scheduler, speedLimit });
            commands.Layout += (s, e) => LayoutCommands();
            commands.Paint += (s, e) => { foreach (var x in separators) Theme.VLine(e.Graphics, x, (commands.Height - 1 - Theme.Px(20)) / 2, (commands.Height - 1 + Theme.Px(20)) / 2); };

            sidebar.Width = Theme.Px(200);
            BuildSidebar();

            search.Height = Theme.Px(32);
            search.Box.AccessibleName = "Search downloads";
            search.Box.TextChanged += (s, e) => RefreshList();
            search.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; search.Text = ""; } };
            sortButton = Command("Sort", Glyph.Sort, () => SortMenu().Show(sortButton, new Point(0, sortButton.Height + Theme.Px(2))));
            viewButton = Command("View", Glyph.View, () => ViewMenu().Show(viewButton, new Point(0, viewButton.Height + Theme.Px(2))));
            foreach (var b in new[] { sortButton, viewButton }) { b.Chevron = true; b.Width = b.PreferredWidth; }
            list.CategoryOf = Cat;
            list.Logo = logoBig;
            list.SelectionChanged += () => { UpdateInspector(); UpdateCommands(); };
            list.Activated += Open;
            list.ToggleRequested += () => Ops(list.Selected.Select(d => Core.CanResume(d) ? new[] { d.Id, "resume" } : Core.CanPause(d) ? new[] { d.Id, "pause" } : null));
            list.MenuRequested += RowMenu;
            list.HeaderClicked += c => { Core.NextSort(c, ref sortColumn, ref sortDesc); RefreshList(); };
            list.HeaderMenuRequested += p => ViewMenu().Show(list.Header, p);
            list.EmptyButton.Click += (s, e) => Dialogs.AddLinks(api, this, "");
            listPanel = new Area(() => Theme.List) { Dock = DockStyle.Fill };
            listPanel.Controls.AddRange(new Control[] { search, sortButton, viewButton, list.Header, list });
            listPanel.Layout += (s, e) =>
            {
                var p = Theme.Px(12);
                viewButton.Location = new Point(listPanel.Width - p - viewButton.Width, p);
                sortButton.Location = new Point(viewButton.Left - Theme.Px(4) - sortButton.Width, p);
                search.Bounds = new Rectangle(p, p, Math.Max(Theme.Px(80), sortButton.Left - Theme.Px(8) - p), Theme.Px(32));
                list.Header.Bounds = new Rectangle(0, Theme.Px(56), listPanel.Width, list.Header.Height);
                list.Bounds = new Rectangle(0, list.Header.Bottom, listPanel.Width, Math.Max(0, listPanel.Height - list.Header.Bottom));
            };

            inspector = new Inspector(tips) { Dock = DockStyle.Right, Width = Theme.Px(320), Visible = false };
            inspector.Do = InspectorAction;

            whenDone.Items.AddRange(new[] { "Do nothing", "Sleep", "Shut down" });
            whenDone.SelectedIndex = 0;
            whenDone.Font = Theme.Small;
            whenDone.Size = new Size(Theme.Px(110), Theme.Px(22));
            whenDone.Radius = Theme.Px(6);
            whenDone.Picked += () => SetWhenDone(WhenDoneValues[whenDone.SelectedIndex]);
            statusBar = new Area(() => Theme.Top) { Dock = DockStyle.Bottom, Height = Theme.Px(30), LineTop = true };
            chip.Click += (s, e) => ToggleLimit();
            tips.SetToolTip(chip, "Turn the speed limit on or off");
            statusBar.Controls.AddRange(new Control[] { statusText, chip, nextStart, whenDoneLabel, whenDone });
            statusBar.Layout += (s, e) =>
            {
                var p = Theme.Px(12);
                whenDone.Location = new Point(statusBar.Width - p - whenDone.Width, (statusBar.Height - whenDone.Height) / 2 + 1);
                var lw = Theme.Measure(whenDoneLabel.Text, Theme.Small).Width + Theme.Px(2);
                whenDoneLabel.Bounds = new Rectangle(whenDone.Left - Theme.Px(6) - lw, 1, lw, statusBar.Height - 1);
                // "1 active · 2.4 MB/s" [Speed limit: Off] " · Main queue at 02:00"
                var tw = Theme.Measure(statusText.Text, Theme.Small).Width + Theme.Px(2);
                statusText.Bounds = new Rectangle(p, 1, tw, statusBar.Height - 1);
                chip.Size = new Size(Theme.Measure(chip.Text, Theme.Small).Width + Theme.Px(16), Theme.Px(20));
                chip.Location = new Point(statusText.Bounds.Right + Theme.Px(6), (statusBar.Height - chip.Height) / 2 + 1);
                nextStart.Bounds = new Rectangle(chip.Right + Theme.Px(2), 1, Math.Max(0, whenDoneLabel.Left - p - chip.Right - Theme.Px(2)), statusBar.Height - 1);
            };

            overlay = new Canvas(PaintOverlay) { Visible = false, AllowDrop = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            overlay.DragEnter += (s, e) => e.Effect = DropText(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            overlay.DragOver += (s, e) => e.Effect = DropText(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            overlay.DragLeave += (s, e) => HideOverlay();
            overlay.DragDrop += (s, e) => { var text = DropText(e.Data); HideOverlay(); if (text != null) Drop(text); };

            root = new Area(() => Theme.List) { Dock = DockStyle.Fill };
            // last added docks first: the top bar and the strip span the width, then the status bar, then the columns
            root.Controls.AddRange(new Control[] { listPanel, inspector, sidebar, statusBar, strip, commands, top });
            root.Controls.Add(overlay);
            overlay.BringToFront();
            Controls.Add(root);
            HookDrop(root);

            if (shots == null) LoadUi();
            wantSection = section;
            sidebar.Selected = sidebar.Items.Any(i => i.Key == section) ? section : "all";
            section = sidebar.Selected;
            sidebar.SelectedChanged += () => { section = sidebar.Selected; RefreshList(); };
            inspector.Visible = details;
            UpdateInspector();
            UpdateCommands();
            ApplyColors();

            if (shots != null)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(SystemInformation.VirtualScreen.Right + 400, SystemInformation.VirtualScreen.Top);
                ShowInTaskbar = false;
            }
            else
            {
                tray = new NotifyIcon { Icon = AppIcon.Load(SystemInformation.SmallIconSize.Width) ?? SystemIcons.Application, Text = Program.Name, ContextMenuStrip = TrayMenu(), Visible = true };
                tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
                tray.BalloonTipClicked += (s, e) => { ShowMain(); if (balloonId != null) Reveal(balloonId); };
            }

            poller = new Poller(api);
            poller.Updated += s => { var free = FreeSpace(s.Settings.Dir); Ui(() => OnState(s, free)); };
            poller.Failed += e => Ui(() => OnFailed(e));
            onTheme = () => Ui(ApplyColors);
            onShow = () => Ui(ShowMain);
            Theme.Changed += onTheme;
            Program.ShowRequested += onShow;
        }

        static Image Logo(int px)
        {
            var icon = AppIcon.Load(Theme.Px(px));
            return icon == null ? null : icon.ToBitmap();
        }

        FlatButton IconButton(char glyph, string tip)
        {
            var b = new FlatButton("", glyph, ButtonKind.Ghost) { AccessibleName = tip, Size = new Size(Theme.Px(34), Theme.Px(34)) };
            tips.SetToolTip(b, tip);
            return b;
        }

        void LayoutTop()
        {
            int p = Theme.Px(12), h = Theme.Px(34), y = (top.Height - 1 - h) / 2;
            logo.Bounds = new Rectangle(p, (top.Height - 1 - Theme.Px(24)) / 2, Theme.Px(24), Theme.Px(24));
            var x = top.Width - p;
            foreach (var b in new[] { more, options, addLinks })
            {
                x -= b.Width;
                b.Location = new Point(x, y);
                x -= Theme.Px(4);
            }
            x -= Theme.Px(4);
            download.Location = new Point(x - download.Width, y);
            x -= download.Width + Theme.Px(8);
            var left = logo.Right + Theme.Px(12);
            paste.Bounds = new Rectangle(left, y, Math.Max(Theme.Px(80), x - left), h);
        }

        // A command bar ghost button: muted glyph + text, 32 high, radius 6, its name as tooltip
        FlatButton Command(string text, char glyph, Action click)
        {
            // padding 8: the labelled bar fits the default window (1180, client 1164) with "Speed limit: 500 KB/s"
            var b = new FlatButton(text, glyph, ButtonKind.Ghost) { MutedGlyph = true, Radius = Theme.Px(6), AccessibleName = text, Height = Theme.Px(32), Pad = Theme.Px(8) };
            b.Width = b.PreferredWidth;
            tips.SetToolTip(b, text);
            b.Click += (s, e) => click();
            labels[b] = text;
            return b;
        }

        // Padding 0 8, groups split by 1 x 20 lines with 6 px margins, Speed limit at the right; when the labels don't
        // fit, the whole bar shows glyphs only (the tooltips keep the names)
        void LayoutCommands()
        {
            var left = new[] { resume, pause, delete, pauseAll, deleteDone, startQueue, stopQueue, scheduler };
            var all = left.Concat(new[] { speedLimit }).ToList();
            int p = Theme.Px(8), sep = Theme.Px(6 + 1 + 6), y = (commands.Height - 1 - Theme.Px(32)) / 2;
            foreach (var b in all) { b.Text = labels[b]; b.Width = b.PreferredWidth; }
            if (all.Sum(b => b.Width) + 3 * sep + Theme.Px(8) + 2 * p > commands.Width)
                foreach (var b in all) { b.Text = ""; b.Width = b.PreferredWidth; }
            separators.Clear();
            var x = p;
            for (var i = 0; i < left.Length; i++)
            {
                left[i].Location = new Point(x, y);
                x += left[i].Width;
                if (i == 2 || i == 4 || i == 6) { separators.Add(x + Theme.Px(6)); x += sep; }
            }
            speedLimit.Location = new Point(commands.Width - p - speedLimit.Width, y);
            commands.Invalidate();
        }

        // --tray: stay hidden until something asks to show
        protected override void SetVisibleCore(bool value)
        {
            if (!visible && !IsHandleCreated) CreateHandle();
            base.SetVisibleCore(visible && value);
        }

        protected override bool ShowWithoutActivation { get { return shots != null; } }

        // after WinForms' own UpdateWindowIcon, so the crisp small icon wins
        protected override void CreateHandle()
        {
            base.CreateHandle();
            AppIcon.Apply(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Themed.Apply(this);
            poller.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState != FormWindowState.Minimized) shownState = WindowState;
        }

        void Ui(Action a)
        {
            if (IsDisposed) return;
            try { if (IsHandleCreated) BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) a(); })); }
            catch (InvalidOperationException) { } // closing
        }

        void ShowMain()
        {
            visible = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = shownState;
            Activate();
        }

        // ---- engine state

        static string FreeSpace(string dir)
        {
            try
            {
                var drive = Path.GetPathRoot(dir);
                if (string.IsNullOrEmpty(drive)) return null;
                return Core.Size(new DriveInfo(drive).AvailableFreeSpace) + " free on " + drive.TrimEnd('\\');
            }
            catch (Exception) { return null; } // a network path or a missing drive
        }

        string Cat(Download d) { return state == null ? "Other" : Core.Category(d, state.Categories); }
        List<Queue> Queues { get { return state == null ? new List<Queue>() : state.Queues; } }

        // Section keys: the Library's, "type:<category>", "queue:<id>" (its unfinished downloads)
        bool Matches(Download d, string key)
        {
            switch (key)
            {
                case "all": return true;
                case "active": return d.Status == "downloading" || d.Status == "queued" || d.Status == "paused";
                case "finished": return d.Status == "done";
                case "failed": return d.Status == "error";
                case "scheduled": return d.Status == "scheduled";
                default:
                    if (key.StartsWith("queue:")) return d.Queue == key.Substring(6) && d.Status != "done";
                    return key.StartsWith("type:") && Cat(d) == key.Substring(5);
            }
        }

        // Library, Types (state.categories in order, then Other; custom ones get Other's icon), Queues. Built again only
        // when a category or a queue comes, goes or is renamed; the selection stays when its key is still there.
        void BuildSidebar()
        {
            var types = state != null && state.Categories.Count > 0 ? state.Categories.Keys.ToList() : BuiltinTypes.ToList();
            var key = string.Join("|", types) + "#" + string.Join("|", Queues.Select(q => q.Id + ":" + q.Name));
            if (key == sidebarKey) return;
            sidebarKey = key;
            sidebar.Clear();
            sidebar.Heading("Library");
            sidebar.Add("all", "All", Glyph.All, null);
            sidebar.Add("active", "Active", Glyph.Active, null);
            sidebar.Add("finished", "Finished", Glyph.Finished, null);
            sidebar.Add("failed", "Failed", Glyph.Failed, null);
            sidebar.Add("scheduled", "Scheduled", Glyph.Scheduled, null);
            sidebar.Heading("Types");
            foreach (var c in types.Where(c => c != "Other").Concat(new[] { "Other" }))
            {
                var icon = BuiltinTypes.Contains(c) ? c : "Other";
                sidebar.Add("type:" + c, c, Glyph.Of(icon), icon);
            }
            if (Queues.Count > 0) sidebar.Heading("Queues");
            foreach (var q in Queues) sidebar.Add("queue:" + q.Id, Core.QueueName(q), Glyph.List, null);
            var want = wantSection ?? section;
            if (state != null) wantSection = null;
            sidebar.Selected = sidebar.Items.Any(i => i.Key == want) ? want : "all";
            sidebar.Invalidate();
        }

        void OnFailed(ApiException e)
        {
            if (!e.Offline) return;
            offline = true;
            UpdateStrip();
        }

        void OnState(State s, string free)
        {
            state = s;
            offline = false;
            freeText = free;
            foreach (var d in s.Downloads.Where(d => d.Status == "pending"))
                if (pendingSeen.Add(d.Id) && shots == null) Dialogs.FileInfo(api, this, d.Id);
            pendingSeen.IntersectWith(s.Downloads.Select(d => d.Id));
            if (shots == null) Dialogs.Update(s);

            all = Core.DefaultOrder(s.Downloads.Where(d => d.Status != "pending"));
            var next = new Dictionary<string, string>();
            foreach (var d in all)
            {
                string prev;
                if (statuses.TryGetValue(d.Id, out prev) && prev == "downloading" && d.Status == "done") Balloon("Download complete", Core.ShortName(d), d.Id);
                next[d.Id] = d.Status;
            }
            statuses = next;

            var running = all.Where(d => d.Status == "downloading").ToList();
            foreach (var d in running)
            {
                List<double> l;
                if (!speeds.TryGetValue(d.Id, out l)) speeds[d.Id] = l = new List<double>();
                Push(l, d.Speed);
            }
            foreach (var id in speeds.Keys.Except(running.Select(d => d.Id)).ToList()) speeds.Remove(id);
            Push(totals, running.Sum(d => (double)d.Speed));

            if (firstPoll) openSeen = s.Open;
            else if (s.Open > openSeen) { openSeen = s.Open; if (shots == null) ShowMain(); }
            firstPoll = false;

            if (s.PowerIn != null && !counting)
                Balloon(Countdown(s), (s.PowerAll ? "All downloads finished. " : "") + "Open " + Program.Name + " to cancel.", null);
            counting = s.PowerIn != null;
            Listen(s.Settings.Clipboard && shots == null);
            if (offered != null && all.Any(d => d.Url == offered)) offered = null;
            if (s.WhenDone != shownWhenDone)
            {
                shownWhenDone = s.WhenDone;
                whenDone.SelectedIndex = Math.Max(0, Array.IndexOf(WhenDoneValues, s.WhenDone));
            }

            BuildSidebar();
            RefreshList();
            UpdateStrip();
            UpdateCommands();
            var n = running.Count;
            var nextQueue = Core.NextStart(s.Queues, DateTime.Now);
            var text = n + " active · " + Core.Size(running.Sum(d => (double)d.Speed)) + "/s";
            var after = nextQueue == null ? "" : " · " + nextQueue;
            if (text != statusText.Text || after != nextStart.Text || Core.SpeedLimitText(s.Settings) != chip.Text)
            {
                statusText.Text = text;
                nextStart.Text = after;
                chip.Text = chip.AccessibleName = Core.SpeedLimitText(s.Settings);
                chip.Visible = true;
                statusBar.PerformLayout();
            }
            if (tray != null)
            {
                var tip = Program.Name + (n > 0 ? " · " + n + " active" : "");
                tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
            if (shots != null && ++polls == 6) Shots();
        }

        static void Push(List<double> l, double v)
        {
            l.Add(v);
            if (l.Count > Samples) l.RemoveRange(0, l.Count - Samples);
        }

        void RefreshList()
        {
            // a queue's count is what waits or runs in it; its dot says it runs
            sidebar.SetCounts(key =>
            {
                var queue = key.StartsWith("queue:") ? Queues.FirstOrDefault(x => "queue:" + x.Id == key) : null;
                return queue != null ? queue.Waiting + queue.Active : all.Count(d => Matches(d, key));
            });
            foreach (var it in sidebar.Items) it.Dot = Queues.Any(x => x.Running && "queue:" + x.Id == it.Key);
            var q = search.Text.Trim();
            Func<string, bool> has = t => t != null && t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            // a queue lists its downloads in its own order: the engine's list order
            var source = section.StartsWith("queue:") && state != null ? state.Downloads.Where(d => d.Status != "pending") : all;
            var shown = source.Where(d => Matches(d, section) && (q == "" || has(Core.ShortName(d)) || has(d.Url))).ToList();
            if (sortColumn != null) shown = Core.Sort(shown, sortColumn, sortDesc, Queues);
            list.Queues = Queues;
            list.SortColumn = sortColumn;
            list.SortDesc = sortDesc;
            list.Header.Invalidate();
            list.FirstRun = all.Count == 0;
            list.EmptyTitle = all.Count == 0 ? "No downloads yet" : "Nothing here";
            list.EmptyHint = all.Count == 0 ? "Paste a link above, drop links here, or let the browser extension catch them."
                : q != "" ? "No download matches your search." : "No downloads in this section.";
            list.SetItems(shown);
            UpdateInspector();
        }

        void UpdateInspector()
        {
            if (state == null) { inspector.ShowOverview(all, totals, freeText); return; }
            var sel = list.Selected;
            if (sel.Count == 1)
            {
                List<double> l;
                inspector.ShowOne(sel[0], state, Cat(sel[0]), speeds.TryGetValue(sel[0].Id, out l) ? l : new List<double>());
            }
            else if (sel.Count > 1) inspector.ShowSeveral(sel);
            else inspector.ShowOverview(all, totals, freeText);
        }

        void UpdateStrip()
        {
            var kind = offline ? "offline" : state != null && state.PowerIn != null ? "countdown" : offered != null && state != null && state.Settings.Clipboard ? "clipboard" : null;
            if (kind != stripKind)
            {
                stripKind = kind;
                if (kind == "offline")
                {
                    strip.Icon = Glyph.Error;
                    strip.IconColor = () => Theme.Err;
                    strip.SetButtons(TextButton("Start it", StartEngine));
                }
                else if (kind == "countdown")
                {
                    strip.Icon = Glyph.Warning;
                    strip.IconColor = () => Theme.Warn;
                    strip.SetButtons(TextButton("Cancel", () => SetWhenDone("")));
                }
                else if (kind == "clipboard")
                {
                    strip.Icon = Glyph.Clipboard;
                    strip.IconColor = () => Theme.Text;
                    var close = IconButton(Glyph.Close, "Dismiss");
                    close.Click += (s, e) => { offered = null; UpdateStrip(); };
                    strip.SetButtons(TextButton("Download", () =>
                    {
                        var url = offered;
                        offered = null;
                        UpdateStrip();
                        Run(() => api.Post("/api/add", new Dictionary<string, object> { { "url", url }, { "confirm", true } }));
                    }), close);
                }
                strip.Visible = kind != null;
            }
            if (kind == "offline") strip.Text = Program.Name + "'s engine isn't running.";
            else if (kind == "countdown") strip.Text = (state.PowerAll ? "All downloads finished. " : "") + Countdown(state);
            else if (kind == "clipboard") strip.Text = "From clipboard: " + offered;
        }

        // The countdown names what it does (powerOp): the status bar's When done, a download's or a queue's own
        static string Countdown(State s) { return ((s.PowerOp ?? s.WhenDone) == "shutdown" ? "Shutting down" : "Going to sleep") + " in " + s.PowerIn + " s"; }

        void UpdateCommands()
        {
            var sel = list.Selected;
            resume.Enabled = sel.Any(Core.CanResume);
            pause.Enabled = sel.Any(Core.CanPause);
            delete.Enabled = sel.Count > 0;
            pauseAll.Enabled = all.Any(d => d.Status == "downloading" || d.Status == "queued");
            deleteDone.Enabled = all.Any(d => d.Status == "done");
            startQueue.Enabled = Queues.Any(q => !q.Running);
            stopQueue.Enabled = Queues.Any(q => q.Running);
            var limit = state == null ? "Speed limit: Off" : Core.SpeedLimitText(state.Settings);
            // one queue: the button starts it; more: a menu of the ones not running (Stop: of the running ones)
            var startMenu = Queues.Count > 1;
            var stopMenu = Queues.Count(q => q.Running) > 1;
            if (startQueue.Chevron == startMenu && stopQueue.Chevron == stopMenu && labels[speedLimit] == limit) return;
            startQueue.Chevron = startMenu;
            stopQueue.Chevron = stopMenu;
            labels[speedLimit] = speedLimit.AccessibleName = limit;
            tips.SetToolTip(speedLimit, limit);
            commands.PerformLayout();
        }

        void QueueCommand(bool start)
        {
            var qs = Queues.Where(q => q.Running != start).ToList();
            if (qs.Count == 0) return;
            var button = start ? startQueue : stopQueue;
            if (!button.Chevron) { SetQueue(qs[0].Id, start); return; }
            var m = Menu();
            foreach (var q in qs) { var id = q.Id; Item(m, Core.QueueName(q), () => SetQueue(id, start), true); }
            m.Show(button, new Point(0, button.Height + Theme.Px(2)));
        }

        void SetQueue(string id, bool start) { Run(() => api.Queues(start ? "start" : "stop", id)); }

        // Speed limit ▾: Turn on (needs a limit above 0, else Set a limit… opens Options), Turn off, Settings…
        void SpeedMenu()
        {
            if (state == null) return;
            var s = state.Settings;
            var m = Menu();
            if (s.SpeedLimitKB > 0) Item(m, "Turn on (" + s.SpeedLimitKB + " KB/s)", () => SetLimit(true), !s.SpeedLimitOn);
            else Item(m, "Set a limit…", () => OpenOptions(false), true);
            Item(m, "Turn off", () => SetLimit(false), s.SpeedLimitOn);
            Item(m, "Settings…", () => OpenOptions(false), true);
            m.Show(speedLimit, new Point(0, speedLimit.Height + Theme.Px(2)));
        }

        void SetLimit(bool on) { Run(() => api.Post("/api/settings", new Dictionary<string, object> { { "speedLimitOn", on } })); }

        // The status bar's chip and the tray's item
        void ToggleLimit()
        {
            if (state == null) return;
            if (Core.LimitOn(state.Settings)) SetLimit(false);
            else if (state.Settings.SpeedLimitKB > 0) SetLimit(true);
            else OpenOptions(false);
        }

        void SetSort(string column, bool descending)
        {
            sortColumn = column;
            sortDesc = column != null && descending;
            RefreshList();
        }

        // Sort ▾: Default, each column (the current one checked), then ascending / descending
        ContextMenuStrip SortMenu()
        {
            var m = Menu();
            m.ShowCheckMargin = true;
            Item(m, "Default", () => SetSort(null, false), true).Checked = sortColumn == null;
            for (var i = 0; i < Core.Columns.Length; i++)
            {
                var c = Core.Columns[i];
                Item(m, Core.ColumnTitles[i], () => SetSort(c, false), true).Checked = sortColumn == c;
            }
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Ascending", () => SetSort(sortColumn, false), sortColumn != null).Checked = sortColumn != null && !sortDesc;
            Item(m, "Descending", () => SetSort(sortColumn, true), sortColumn != null).Checked = sortColumn != null && sortDesc;
            return m;
        }

        // View ▾ (and a right-click on the list header): each column, then the details panel
        ContextMenuStrip ViewMenu()
        {
            var m = Menu();
            m.ShowCheckMargin = true;
            for (var i = 1; i < Core.Columns.Length; i++)
            {
                var c = Core.Columns[i];
                var on = list.Chosen.Contains(c);
                Item(m, Core.ColumnTitles[i], () => list.SetColumns(on ? list.Chosen.Where(x => x != c) : list.Chosen.Concat(new[] { c })), true).Checked = on;
            }
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Details panel", () => { details = !details; inspector.Visible = details; UpdateInspector(); }, true).Checked = details;
            return m;
        }

        FlatButton TextButton(string text, Action click)
        {
            var b = new FlatButton(text, '\0', ButtonKind.Ghost);
            b.Click += (s, e) => click();
            return b;
        }

        void ApplyColors()
        {
            foreach (var a in new[] { root, top, commands, listPanel, statusBar }) a.BackColor = a.Back();
            strip.BackColor = Theme.Inspector;
            sidebar.BackColor = Theme.Sidebar;
            list.BackColor = Theme.List;
            Themed.Apply(this);
        }

        // ---- engine calls: off the UI thread, errors in a dialog, then a fresh poll

        void Run(Action work)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { work(); }
                catch (ApiException e) { var m = e.Message; Ui(() => Dialogs.Error(this, m)); }
                catch (Exception) { Ui(() => Dialogs.Error(this, "Something went wrong. Try again.")); }
                poller.PollNow();
            });
        }

        void Act(string id, string op, string url) { api.Action(id, op, url != null ? "url" : null, url); }

        // [id, op] or [id, op, url], one after the other
        void Ops(IEnumerable<string[]> ops)
        {
            var l = ops.Where(o => o != null).ToList();
            if (l.Count > 0) Run(() => { foreach (var o in l) Act(o[0], o[1], o.Length > 2 ? o[2] : null); });
        }

        void Pause(List<Download> sel) { Ops(sel.Where(d => Core.CanPause(d)).Select(d => new[] { d.Id, "pause" })); }
        void Resume(List<Download> sel) { Ops(sel.Where(d => Core.CanResume(d)).Select(d => new[] { d.Id, "resume" })); }

        // Enter or double-click: a finished file opens, anything else shows its progress window
        void Open(Download d)
        {
            if (d.Status == "done") Ops(new[] { new[] { d.Id, "open" } });
            else Dialogs.Progress(api, this, d.Id);
        }

        void CopyLinks()
        {
            var urls = list.Selected.Select(d => d.Url).Where(u => !string.IsNullOrEmpty(u)).ToList();
            if (urls.Count == 0) return;
            try { Clipboard.SetText(string.Join("\r\n", urls)); }
            catch (ExternalException) { Dialogs.Error(this, "The clipboard is busy. Try again."); }
        }

        void ChangeLink(Download d)
        {
            var url = Dialogs.ChangeLink(this, d.Url);
            if (url != null) Ops(new[] { new[] { d.Id, "url", url } });
        }

        void Remove(List<Download> sel)
        {
            if (sel.Count == 0) return;
            bool files;
            if (!Dialogs.ConfirmRemove(this, sel.Count, sel.Any(d => d.Status == "done"), out files)) return;
            Ops(sel.Select(d => new[] { d.Id, files && d.Status == "done" ? "removeFile" : "remove" }));
        }

        void DeleteCompleted()
        {
            var done = all.Where(d => d.Status == "done").ToList();
            if (done.Count == 0) return;
            if (Dialogs.Confirm(this, "Delete completed", "Remove " + done.Count + " finished download" + (done.Count == 1 ? "" : "s") + " from the list? The files stay on disk.", "Remove", true))
                Ops(done.Select(d => new[] { d.Id, "remove" }));
        }

        void SetWhenDone(string value) { Run(() => api.Post("/api/settings", new Dictionary<string, object> { { "whenDone", value } })); }

        void OpenOptions(bool schedule) { if (state != null) Dialogs.Options(api, this, state, schedule); }

        void StartEngine()
        {
            foreach (Control c in strip.Controls) c.Enabled = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var error = Program.StartEngine(api);
                Ui(() =>
                {
                    foreach (Control c in strip.Controls) c.Enabled = true;
                    if (error != null) Dialogs.Error(this, error);
                });
                poller.PollNow();
            });
        }

        void Quit()
        {
            if (all.Any(d => d.Status == "downloading") && !Dialogs.Confirm(this, "Quit " + Program.Name,
                "Quit " + Program.Name + "? Running downloads stop now and continue when you start it again.", "Quit", false)) return;
            quitting = true;
            poller.Stop();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { api.Post("/api/quit", null); }
                catch (ApiException) { } // already stopped
                Ui(() => { if (tray != null) tray.Visible = false; Application.Exit(); });
            });
        }

        void InspectorAction(string op)
        {
            var sel = list.Selected;
            if (sel.Count == 0) return;
            switch (op)
            {
                case "pause": Pause(sel); break;
                case "resume": Resume(sel); break;
                case "open": Open(sel[0]); break;
                case "folder": Ops(sel.Where(d => !string.IsNullOrEmpty(d.File)).Take(1).Select(d => new[] { d.Id, "folder" })); break;
                case "copy": CopyLinks(); break;
                case "link": ChangeLink(sel[0]); break;
                case "progress": Dialogs.Progress(api, this, sel[0].Id); break;
                case "remove": Remove(sel); break;
            }
        }

        // ---- paste bar, drag and drop, clipboard

        void Submit(string text)
        {
            var tokens = Regex.Split((text ?? "").Trim(), @"\s+").Where(t => t != "").ToList();
            if (tokens.Count == 0) { paste.Box.Focus(); return; }
            if (!tokens.All(t => LinkRx.IsMatch(t)))
            {
                paste.Invalid = true;
                tips.Show("Paste an http or https link", paste, 0, paste.Height + Theme.Px(4), 4000);
                return;
            }
            paste.Text = "";
            if (tokens.Count == 1 && !BatchRx.IsMatch(tokens[0]))
            {
                var url = tokens[0];
                Run(() => api.Post("/api/add", new Dictionary<string, object> { { "url", url }, { "confirm", true } }));
            }
            else Dialogs.AddLinks(api, this, string.Join("\r\n", tokens));
        }

        void Drop(string text)
        {
            var links = Regex.Split(text.Trim(), @"\s+").Where(t => LinkRx.IsMatch(t)).ToList();
            if (links.Count == 1) Submit(links[0]);
            else if (links.Count > 1) Dialogs.AddLinks(api, this, string.Join("\r\n", links));
        }

        static string DropText(IDataObject data)
        {
            var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
            return text != null && Regex.IsMatch(text, @"https?://", RegexOptions.IgnoreCase) ? text : null;
        }

        void HookDrop(Control c)
        {
            if (c == overlay) return;
            c.AllowDrop = true;
            c.DragEnter += (s, e) =>
            {
                if (DropText(e.Data) == null) return;
                e.Effect = DragDropEffects.Copy;
                ShowOverlay();
            };
            foreach (Control child in c.Controls) HookDrop(child);
            c.ControlAdded += (s, e) => HookDrop(e.Control); // inspector and strip buttons come and go
        }

        void ShowOverlay()
        {
            if (overlay.Visible) return;
            if (snapshot != null) snapshot.Dispose();
            snapshot = new Bitmap(Math.Max(1, root.Width), Math.Max(1, root.Height));
            root.DrawToBitmap(snapshot, new Rectangle(Point.Empty, root.Size));
            overlay.Bounds = root.ClientRectangle;
            overlay.Visible = true;
            overlay.BringToFront();
        }

        void HideOverlay() { overlay.Visible = false; }

        void PaintOverlay(Graphics g, Rectangle r)
        {
            if (snapshot != null) g.DrawImageUnscaled(snapshot, 0, 0);
            using (var b = new SolidBrush(Color.FromArgb(235, Theme.Inspector))) g.FillRectangle(b, r);
            var inset = Theme.Px(16);
            var box = new Rectangle(inset, inset, r.Width - 2 * inset, r.Height - 2 * inset);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(box, Theme.Px(12)))
            using (var pen = new Pen(Theme.Accent, Theme.Px(2)) { DashStyle = DashStyle.Dash }) g.DrawPath(pen, path);
            var cy = r.Height / 2;
            Theme.DrawGlyph(g, Glyph.Download, new Rectangle(0, cy - Theme.Px(44), r.Width, Theme.Px(40)), Theme.Accent, 32);
            Theme.DrawText(g, "Drop to download", Theme.Bold, new Rectangle(0, cy + Theme.Px(4), r.Width, Theme.Bold.Height), Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }

        void Listen(bool on)
        {
            if (on == clipListening || !IsHandleCreated) return;
            clipListening = on;
            if (on) { lastClip = null; AddClipboardFormatListener(Handle); }
            else RemoveClipboardFormatListener(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE && clipListening && state != null) OnClipboard();
            base.WndProc(ref m);
        }

        // Offer a copied link once, only one Core.ClipboardLink accepts and no download has
        void OnClipboard()
        {
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (ExternalException) { return; }
            if (text == null || text == lastClip) return;
            lastClip = text;
            var url = Core.ClipboardLink(text, state.Exts);
            if (url == null || all.Any(d => d.Url == url)) return;
            offered = url;
            UpdateStrip();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var focus = FromHandle(GetFocus());
            var inText = focus is TextBoxBase || focus is ComboBox;
            switch (keyData)
            {
                case Keys.Control | Keys.V:
                    if (inText) break;
                    try { paste.Text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : paste.Text; }
                    catch (ExternalException) { }
                    paste.Box.Focus();
                    paste.Box.SelectionStart = paste.Text.Length;
                    return true;
                case Keys.Control | Keys.N: Dialogs.AddLinks(api, this, ""); return true;
                case Keys.Control | Keys.F: search.Box.Focus(); search.Box.SelectAll(); return true;
                case Keys.Control | Keys.Oemcomma: OpenOptions(false); return true;
                case Keys.Delete: if (inText) break; Remove(list.Selected); return true;
                case Keys.Control | Keys.A: if (inText) break; list.SelectAll(); return true;
                case Keys.Control | Keys.C: if (inText) break; CopyLinks(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- menus, tray

        static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { Renderer = Theme.Menus, ShowImageMargin = false, Font = Theme.Body };
            m.Closed += (s, e) => m.BeginInvoke((MethodInvoker)m.Dispose); // after the clicked item's handler
            return m;
        }

        static ToolStripMenuItem Item(ContextMenuStrip m, string text, Action click, bool enabled)
        {
            var i = new ToolStripMenuItem(text) { Enabled = enabled, Padding = new Padding(Theme.Px(4), Theme.Px(3), Theme.Px(4), Theme.Px(3)) };
            i.Click += (s, e) => click();
            m.Items.Add(i);
            return i;
        }

        ContextMenuStrip MoreMenu()
        {
            var m = Menu();
            Item(m, "About " + Program.Name, () => Dialogs.About(this), true);
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Quit " + Program.Name, Quit, true);
            return m;
        }

        ContextMenuStrip TrayMenu()
        {
            var m = new ContextMenuStrip { Renderer = Theme.Menus, ShowImageMargin = false, Font = Theme.Body };
            var open = Item(m, "Open " + Program.Name, ShowMain, true);
            open.Font = Theme.Bold;
            Item(m, "Pause all", () => Run(() => api.Post("/api/pause-all", null)), true);
            m.ShowCheckMargin = true;
            var limit = Item(m, "Speed limit", ToggleLimit, true);
            m.Opening += (s, e) => limit.Checked = state != null && Core.LimitOn(state.Settings);
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Quit " + Program.Name, Quit, true);
            return m;
        }

        void RowMenu(Point p)
        {
            var sel = list.Selected;
            if (sel.Count == 0) return;
            var one = sel.Count == 1 ? sel[0] : null;
            var unfinished = sel.Where(d => d.Status != "done").ToList();
            var m = Menu();
            Item(m, "Open", () => Open(one), one != null && one.Status == "done");
            Item(m, "Open with…", () => Dialogs.OpenWith(this, one.File), one != null && one.Status == "done" && !string.IsNullOrEmpty(one.File));
            Item(m, "Show in folder", () => InspectorAction("folder"), sel.Any(d => !string.IsNullOrEmpty(d.File)));
            Item(m, "Copy link", CopyLinks, true);
            m.Items.Add(new ToolStripSeparator());
            if (sel.Any(d => d.Status == "downloading" || d.Status == "queued")) Item(m, "Pause", () => Pause(sel), true);
            else Item(m, "Resume", () => Resume(sel), sel.Any(d => Core.CanResume(d)));
            var add = Item(m, "Add to queue", () => { }, unfinished.Count > 0 && Queues.Count > 0);
            var sub = (ToolStripDropDownMenu)add.DropDown;
            sub.Renderer = Theme.Menus;
            sub.ShowImageMargin = false;
            sub.ShowCheckMargin = true;
            sub.Font = Theme.Body;
            foreach (var q in Queues)
            {
                var id = q.Id;
                var i = new ToolStripMenuItem(Core.QueueName(q)) { Checked = unfinished.Count > 0 && unfinished.All(d => d.Queue == id), Padding = add.Padding };
                i.Click += (s, e) => ToQueue(unfinished, id);
                sub.Items.Add(i);
            }
            var inQueue = sel.Where(d => d.Queue != null && d.Status != "done").ToList();
            if (inQueue.Count > 0) Item(m, "Remove from queue", () => ToQueue(inQueue, null), true);
            if (section.StartsWith("queue:") && state != null && sel.All(d => d.Status != "done"))
            {
                // a queue's order is the engine's list order among its unfinished downloads; a finished one doesn't move
                var order = one == null || one.Status == "done" ? null : state.Downloads.Where(d => d.Queue == one.Queue && d.Status != "done").ToList();
                var at = order == null ? -1 : order.FindIndex(d => d.Id == one.Id);
                Item(m, "Move up", () => Ops(new[] { new[] { one.Id, "up" } }), at > 0);
                Item(m, "Move down", () => Ops(new[] { new[] { one.Id, "down" } }), at >= 0 && at < order.Count - 1);
            }
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Progress window", () => Dialogs.Progress(api, this, one.Id), one != null && one.Status != "done");
            Item(m, "Properties…", () => { }, false); // issue 06 builds the window
            Item(m, "Change link…", () => ChangeLink(one), one != null && (one.Status == "paused" || one.Status == "error") && one.Kind != "video"); // until Properties has it
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Remove…", () => Remove(sel), true);
            m.Show(list, p);
        }

        // Into a queue as 'scheduled', or out of every queue (null)
        void ToQueue(List<Download> l, string queue)
        {
            if (l.Count > 0) Run(() => { foreach (var d in l) api.Action(d.Id, "queue", "queue", queue); });
        }

        void Balloon(string title, string text, string id)
        {
            if (tray == null) return;
            balloonId = id;
            tray.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
        }

        // Selects a download, leaving the section or search that hides it
        void Reveal(string id)
        {
            if (!list.Items.Any(d => d.Id == id))
            {
                sidebar.Selected = "all";
                search.Text = "";
                RefreshList();
            }
            list.Select(id);
            list.Focus();
        }

        // ---- closing, window state

        string UiFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.Name, "ui.json"); } }

        void LoadUi()
        {
            IDictionary<string, object> o;
            try { o = Json.Parse(File.ReadAllText(UiFile)) as IDictionary<string, object>; }
            catch (Exception) { return; } // first start, or unreadable: defaults
            if (o == null) return;
            section = o.Str("section") ?? section;
            if (!Library.Contains(section) && !section.StartsWith("type:") && !section.StartsWith("queue:")) section = "type:" + section; // from before Types had a prefix
            trayHint = o.Bool("trayHint") ?? false;
            var sort = o.Str("sort");
            sortColumn = Core.Columns.Contains(sort) ? sort : null;
            sortDesc = sortColumn != null && o.Bool("desc") == true;
            if (o.List("columns") != null) list.SetColumns(o.Strings("columns"));
            details = o.Bool("details") ?? false;
            int? x = o.Int("x"), y = o.Int("y"), w = o.Int("w"), h = o.Int("h");
            if (x != null && y != null && w != null && h != null)
            {
                var r = new Rectangle(x.Value, y.Value, Math.Max(w.Value, MinimumSize.Width), Math.Max(h.Value, MinimumSize.Height));
                if (Screen.AllScreens.Any(s => { var i = Rectangle.Intersect(s.WorkingArea, r); return i.Width >= Theme.Px(200) && i.Height >= Theme.Px(100); }))
                {
                    StartPosition = FormStartPosition.Manual;
                    Bounds = r;
                }
            }
            if (o.Bool("max") == true) WindowState = FormWindowState.Maximized;
        }

        void SaveUi()
        {
            if (shots != null) return;
            var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            var o = new Dictionary<string, object> {
                { "x", b.X }, { "y", b.Y }, { "w", b.Width }, { "h", b.Height },
                { "max", WindowState == FormWindowState.Maximized || WindowState == FormWindowState.Minimized && shownState == FormWindowState.Maximized },
                { "section", section }, { "trayHint", trayHint },
                { "sort", sortColumn }, { "desc", sortDesc }, { "columns", list.Chosen }, { "details", details },
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UiFile));
                File.WriteAllText(UiFile, Json.Serialize(o));
            }
            catch (Exception) { } // window placement only: never worth an error
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!quitting && shots == null && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SaveUi();
                Hide();
                if (!trayHint)
                {
                    trayHint = true;
                    SaveUi();
                    Balloon(Program.Name, Program.Name + " keeps running in the tray. Right-click its icon to quit.", null);
                }
                return;
            }
            SaveUi();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            poller.Stop();
            Theme.Changed -= onTheme;
            Program.ShowRequested -= onShow;
            Listen(false);
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            base.OnFormClosed(e);
        }

        // ---- ARROW_SHOTS: PNGs of the window for visual review, then exit

        void Shots()
        {
            Directory.CreateDirectory(shots);
            list.ClearSelection();
            Shot("main-overview");
            list.SelectRange(0, 0);
            Shot("main-one");
            list.SelectRange(0, 2);
            Shot("main-several");
            details = inspector.Visible = true;
            list.SelectRange(1, 1);
            Shot("main-details");
            details = inspector.Visible = false;
            list.ClearSelection();
            sidebar.Selected = "queue:" + Queue.Main;
            Shot("main-queue");
            sidebar.Selected = "all";
            SetSort("size", true);
            Shot("main-sorted");
            SetSort(null, false);
            // a running download and queue, staged in the local state only (the engine's downloads stay off the network)
            var run = all.FirstOrDefault(d => d.Status == "paused");
            if (run != null) { run.Status = "downloading"; run.Speed = 2516582; run.Connections = 4; }
            foreach (var q in Queues.Take(1)) q.Running = true;
            state.Settings.SpeedLimitOn = true;
            all = Core.DefaultOrder(all);
            RefreshList();
            UpdateCommands();
            chip.Text = Core.SpeedLimitText(state.Settings);
            nextStart.Text = Core.NextStart(Queues, DateTime.Now) == null ? "" : " · " + Core.NextStart(Queues, DateTime.Now);
            statusText.Text = "1 active · " + Core.Size(2516582) + "/s";
            statusBar.PerformLayout();
            Shot("main-running");
            var size = Size;
            Size = MinimumSize;
            Shot("main-narrow");
            Size = new Size(Theme.Px(1280), size.Height); // wide enough for the command bar's labels
            Shot("main-wide");
            Size = size;
            list.ClearSelection();
            // the strips and the overlay, staged in the local state only: nothing here reaches the engine
            state.Settings.Clipboard = true;
            offered = "https://downloads.example.com/files/setup-2.4.1.zip";
            UpdateStrip();
            Shot("main-strip-clipboard");
            state.PowerIn = 42;
            state.PowerAll = true;
            state.WhenDone = "shutdown";
            UpdateStrip();
            Shot("main-strip-countdown");
            offline = true;
            UpdateStrip();
            Shot("main-strip-offline");
            offline = false;
            state.PowerIn = null;
            offered = null;
            UpdateStrip();
            ShowOverlay();
            Shot("main-overlay", overlay); // DrawToBitmap paints children bottom-up, so root would hide it
            HideOverlay();
            search.Text = "zzzz";
            Shot("main-empty-search");
            Dialogs.Shots(this, shots);
            Close();
        }

        void Shot(string name) { Shot(name, root); }

        void Shot(string name, Control c)
        {
            root.PerformLayout();
            using (var bmp = new Bitmap(root.Width, root.Height))
            {
                c.DrawToBitmap(bmp, new Rectangle(Point.Empty, root.Size));
                bmp.Save(Path.Combine(shots, name + (Theme.Dark ? "-dark" : "-light") + ".png"), ImageFormat.Png);
            }
        }
    }
}
