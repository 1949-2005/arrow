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
        FormWindowState shownState = FormWindowState.Normal;
        Dictionary<string, string> statuses = new Dictionary<string, string>();
        readonly HashSet<string> pendingSeen = new HashSet<string>();
        readonly Dictionary<string, List<double>> speeds = new Dictionary<string, List<double>>();
        readonly List<double> totals = new List<double>();

        readonly ToolTip tips = new ToolTip();
        readonly Area root, top, listPanel, statusBar;
        readonly Canvas logo, overlay;
        readonly TextField paste = new TextField(Glyph.Link, "Paste a link or drop files");
        readonly TextField search = new TextField(Glyph.Search, "Search downloads");
        readonly FlatButton download = new FlatButton("Download", Glyph.Download, ButtonKind.Primary);
        readonly FlatButton pauseAll, addLinks, scheduler, options, more;
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
            visible = show;
            Text = Program.Name;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.None;
            MinimumSize = new Size(Theme.Px(900), Theme.Px(560));
            Size = new Size(Theme.Px(1180), Theme.Px(700));
            StartPosition = FormStartPosition.CenterScreen;
            logoSmall = Logo(24);
            logoBig = Logo(64);

            pauseAll = IconButton(Glyph.Pause, "Pause all");
            addLinks = IconButton(Glyph.List, "Add several links");
            scheduler = IconButton(Glyph.Clock, "Scheduler");
            options = IconButton(Glyph.Options, "Options");
            more = IconButton(Glyph.More, "More");
            download.Height = Theme.Px(34);
            download.Width = download.PreferredWidth;
            logo = new Canvas((g, r) => { if (logoSmall != null) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(logoSmall, r); } }) { AccessibleName = Program.Name };
            top = new Area(() => Theme.Top) { Dock = DockStyle.Top, Height = Theme.Px(56), LineBottom = true };
            top.Controls.AddRange(new Control[] { logo, paste, download, pauseAll, addLinks, scheduler, options, more });
            top.Layout += (s, e) => LayoutTop();
            paste.AccessibleName = "Link";
            paste.Box.AccessibleName = "Paste a link";
            paste.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(paste.Text); } };
            paste.Box.TextChanged += (s, e) => { if (paste.Invalid) { paste.Invalid = false; tips.Hide(paste); } };
            download.Click += (s, e) => Submit(paste.Text);
            pauseAll.Click += (s, e) => Run(() => api.Post("/api/pause-all", null));
            addLinks.Click += (s, e) => Dialogs.AddLinks(api, this, "");
            scheduler.Click += (s, e) => OpenOptions(true);
            options.Click += (s, e) => OpenOptions(false);
            more.Click += (s, e) => MoreMenu().Show(more, new Point(0, more.Height + Theme.Px(2)));

            sidebar.Width = Theme.Px(200);
            sidebar.Heading("Library");
            sidebar.Add("all", "All", Glyph.All, false);
            sidebar.Add("active", "Active", Glyph.Active, false);
            sidebar.Add("finished", "Finished", Glyph.Finished, false);
            sidebar.Add("failed", "Failed", Glyph.Failed, false);
            sidebar.Add("scheduled", "Scheduled", Glyph.Scheduled, false);
            sidebar.Heading("Types");
            foreach (var c in new[] { "Video", "Music", "Documents", "Programs", "Compressed", "Other" }) sidebar.Add(c, c, Glyph.Of(c), true);

            search.Height = Theme.Px(32);
            search.Box.AccessibleName = "Search downloads";
            search.Box.TextChanged += (s, e) => RefreshList();
            search.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; search.Text = ""; } };
            list.CategoryOf = Cat;
            list.Logo = logoBig;
            list.SelectionChanged += UpdateInspector;
            list.Activated += Open;
            list.ToggleRequested += () => Ops(list.Selected.Select(d => Core.CanResume(d) ? new[] { d.Id, "resume" } : Core.CanPause(d) ? new[] { d.Id, "pause" } : null));
            list.MenuRequested += RowMenu;
            list.EmptyButton.Click += (s, e) => Dialogs.AddLinks(api, this, "");
            listPanel = new Area(() => Theme.List) { Dock = DockStyle.Fill };
            listPanel.Controls.Add(search);
            listPanel.Controls.Add(list);
            listPanel.Paint += (s, e) => Theme.HLine(e.Graphics, 0, listPanel.Width, Theme.Px(56) - 1);
            listPanel.Layout += (s, e) =>
            {
                var p = Theme.Px(12);
                search.Bounds = new Rectangle(p, p, listPanel.Width - 2 * p, Theme.Px(32));
                list.Bounds = new Rectangle(0, Theme.Px(56), listPanel.Width, Math.Max(0, listPanel.Height - Theme.Px(56)));
            };

            inspector = new Inspector(tips) { Dock = DockStyle.Right, Width = Theme.Px(320) };
            inspector.Do = InspectorAction;

            whenDone.Items.AddRange(new[] { "Do nothing", "Sleep", "Shut down" });
            whenDone.SelectedIndex = 0;
            whenDone.Font = Theme.Small;
            whenDone.Size = new Size(Theme.Px(110), Theme.Px(22));
            whenDone.Radius = Theme.Px(6);
            whenDone.Picked += () => SetWhenDone(WhenDoneValues[whenDone.SelectedIndex]);
            statusBar = new Area(() => Theme.Top) { Dock = DockStyle.Bottom, Height = Theme.Px(30), LineTop = true };
            statusBar.Controls.AddRange(new Control[] { statusText, whenDoneLabel, whenDone });
            statusBar.Layout += (s, e) =>
            {
                var p = Theme.Px(12);
                whenDone.Location = new Point(statusBar.Width - p - whenDone.Width, (statusBar.Height - whenDone.Height) / 2 + 1);
                var lw = Theme.Measure(whenDoneLabel.Text, Theme.Small).Width + Theme.Px(2);
                whenDoneLabel.Bounds = new Rectangle(whenDone.Left - Theme.Px(6) - lw, 1, lw, statusBar.Height - 1);
                statusText.Bounds = new Rectangle(p, 1, Math.Max(0, whenDoneLabel.Left - 2 * p), statusBar.Height - 1);
            };

            overlay = new Canvas(PaintOverlay) { Visible = false, AllowDrop = true, Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right };
            overlay.DragEnter += (s, e) => e.Effect = DropText(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            overlay.DragOver += (s, e) => e.Effect = DropText(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
            overlay.DragLeave += (s, e) => HideOverlay();
            overlay.DragDrop += (s, e) => { var text = DropText(e.Data); HideOverlay(); if (text != null) Drop(text); };

            root = new Area(() => Theme.List) { Dock = DockStyle.Fill };
            // last added docks first: the top bar and the strip span the width, then the status bar, then the columns
            root.Controls.AddRange(new Control[] { listPanel, inspector, sidebar, statusBar, strip, top });
            root.Controls.Add(overlay);
            overlay.BringToFront();
            Controls.Add(root);
            HookDrop(root);

            if (shots == null) LoadUi();
            sidebar.Selected = sidebar.Items.Any(i => i.Key == section) ? section : "all";
            section = sidebar.Selected;
            sidebar.SelectedChanged += () => { section = sidebar.Selected; RefreshList(); };
            UpdateInspector();
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
            foreach (var b in new[] { more, options, scheduler, addLinks, pauseAll })
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

        // --tray: stay hidden until something asks to show
        protected override void SetVisibleCore(bool value)
        {
            if (!visible && !IsHandleCreated) CreateHandle();
            base.SetVisibleCore(visible && value);
        }

        protected override bool ShowWithoutActivation { get { return shots != null; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AppIcon.Apply(this);
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

        static int Rank(Download d)
        {
            switch (d.Status)
            {
                case "downloading": return 0;
                case "queued": return 1;
                case "paused": return 2;
                case "error": return 3;
                case "done": return 5;
                default: return 4;
            }
        }

        string Cat(Download d) { return state == null ? "Other" : Core.Category(d, state.Categories); }

        bool Matches(Download d, string key)
        {
            switch (key)
            {
                case "all": return true;
                case "active": return d.Status == "downloading" || d.Status == "queued" || d.Status == "paused";
                case "finished": return d.Status == "done";
                case "failed": return d.Status == "error";
                case "scheduled": return d.Status == "scheduled";
                default: return Cat(d) == key;
            }
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

            all = s.Downloads.Where(d => d.Status != "pending").OrderBy(Rank).ThenByDescending(d => d.Added).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
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
                Balloon(s.WhenDone == "shutdown" ? "Shutting down in " + s.PowerIn + " s" : "Going to sleep in " + s.PowerIn + " s", "All downloads finished. Open " + Program.Name + " to cancel.", null);
            counting = s.PowerIn != null;
            Listen(s.Settings.Clipboard && shots == null);
            if (offered != null && all.Any(d => d.Url == offered)) offered = null;
            if (s.WhenDone != shownWhenDone)
            {
                shownWhenDone = s.WhenDone;
                whenDone.SelectedIndex = Math.Max(0, Array.IndexOf(WhenDoneValues, s.WhenDone));
            }

            RefreshList();
            UpdateStrip();
            var n = running.Count;
            var text = n + " active · " + Core.Size(running.Sum(d => (double)d.Speed)) + "/s";
            if (s.Settings.SpeedLimitKB > 0) text += " · limit " + s.Settings.SpeedLimitKB + " KB/s";
            if (s.Settings.ScheduleAt != "") text += " · scheduled start " + s.Settings.ScheduleAt;
            statusText.Text = text;
            pauseAll.Enabled = all.Any(d => d.Status == "downloading" || d.Status == "queued");
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
            sidebar.SetCounts(key => all.Count(d => Matches(d, key)));
            var q = search.Text.Trim();
            Func<string, bool> has = t => t != null && t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
            var shown = all.Where(d => Matches(d, section) && (q == "" || has(Core.ShortName(d)) || has(d.Url))).ToList();
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
            else if (kind == "countdown") strip.Text = "All downloads finished. " + (state.WhenDone == "shutdown" ? "Shutting down" : "Going to sleep") + " in " + state.PowerIn + " s";
            else if (kind == "clipboard") strip.Text = "From clipboard: " + offered;
        }

        FlatButton TextButton(string text, Action click)
        {
            var b = new FlatButton(text, '\0', ButtonKind.Ghost);
            b.Click += (s, e) => click();
            return b;
        }

        void ApplyColors()
        {
            foreach (var a in new[] { root, top, listPanel, statusBar }) a.BackColor = a.Back();
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

        void Act(string id, string op, string url)
        {
            var body = new Dictionary<string, object> { { "id", id }, { "op", op } };
            if (url != null) body["url"] = url;
            api.Post("/api/action", body);
        }

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
            Item(m, "Delete completed…", DeleteCompleted, all.Any(d => d.Status == "done"));
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
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Quit " + Program.Name, Quit, true);
            return m;
        }

        void RowMenu(Point p)
        {
            var sel = list.Selected;
            if (sel.Count == 0) return;
            var one = sel.Count == 1 ? sel[0] : null;
            var m = Menu();
            Item(m, "Open", () => Open(one), one != null && one.Status == "done");
            Item(m, "Show in folder", () => InspectorAction("folder"), sel.Any(d => !string.IsNullOrEmpty(d.File)));
            Item(m, "Copy link", CopyLinks, true);
            m.Items.Add(new ToolStripSeparator());
            if (sel.Any(d => d.Status == "downloading" || d.Status == "queued")) Item(m, "Pause", () => Pause(sel), true);
            else Item(m, "Resume", () => Resume(sel), sel.Any(d => Core.CanResume(d)));
            Item(m, "Change link…", () => ChangeLink(one), one != null && (one.Status == "paused" || one.Status == "error") && one.Kind != "video");
            Item(m, "Progress window", () => Dialogs.Progress(api, this, one.Id), one != null && one.Status != "done");
            m.Items.Add(new ToolStripSeparator());
            Item(m, "Remove…", () => Remove(sel), true);
            m.Show(list, p);
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
            trayHint = o.Bool("trayHint") ?? false;
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
            list.ClearSelection();
            // the strips and the overlay, staged in the local state only: nothing here reaches the engine
            state.Settings.Clipboard = true;
            offered = "https://downloads.example.com/files/setup-2.4.1.zip";
            UpdateStrip();
            Shot("main-strip-clipboard");
            state.PowerIn = 42;
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
