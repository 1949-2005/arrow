// Download File Info (docs/NATIVE_UI.md "Dialogs", the behavior of the old confirm.html): one pending download's name,
// format, category (with "+" for a new one), Save As, description, answered with POST /api/confirm start, later (into a
// queue, from the Add to queue popover) or cancel. A link already in the list asks first (Link already in the list).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Glide
{
    public class FileInfoDialog : Dialog
    {
        public readonly string Id;
        public readonly Api Api; // null in GLIDE_SHOTS
        static readonly string[] DefaultCategories = { "Compressed", "Documents", "Music", "Programs", "Video", "Other" };
        string[] cats = DefaultCategories; // settings.types' names in alphabetical order, then Other

        readonly Tile tile = new Tile();
        readonly Caption title = new Caption(Theme.Bold, () => Theme.Text) { Text = "Download File Info" };
        readonly Caption sub = new Caption(Theme.Body, () => Theme.Muted) { Text = "Getting file info…", MaxLines = Caption.Unlimited }; // the name, whole
        readonly Caption urlLabel = Label("URL"), formatLabel = Label("Format"), catLabel = Label("Category"), fileLabel = Label("Save As"), sizeLabel = Label("Size");
        readonly TextBox url = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, AccessibleName = "URL" };
        readonly Caption note = new Caption(Theme.Body, () => Theme.Warn) { MaxLines = 3, Visible = false };
        readonly Picker format = new Picker { AccessibleName = "Format", Height = Theme.Px(32) };
        readonly Canvas spinner;
        readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer { Interval = 50 };
        readonly Picker category = new Picker { AccessibleName = "Category", Height = Theme.Px(32) };
        readonly FlatButton addCategory = new FlatButton("", Glyph.Add, ButtonKind.Secondary) { AccessibleName = "New category" };
        readonly TextField file = new TextField('\0', "");
        readonly FlatButton browse = new FlatButton("Browse…", '\0', ButtonKind.Secondary);
        readonly FlatCheck remember = new FlatCheck("Remember this folder for other files"); // unchecked each time the window opens
        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 4, Visible = false };
        readonly Caption descLabel = Label("Description");
        readonly TextField description = new TextField('\0', "Optional, shown in the list");
        readonly Caption size; // muted "Getting details…" while an own format waits for the list
        readonly FlatButton later = new FlatButton("Download Later", '\0', ButtonKind.Secondary) { Chevron = true };
        readonly FlatButton cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary);
        readonly FlatButton start = new FlatButton("Start Download", '\0', ButtonKind.Primary);

        Download d;
        Settings settings = new Settings();
        State all; // the whole state, for the duplicate's file and status
        bool duplicateSeen; // probe.duplicate handled (asked, or the saved choice applied)
        string replaceId, replaceFile, rememberDuplicates; // "Replace the existing file": its id and file; "Remember my choice"
        List<string[]> formats; // [key, text]; null while listing
        bool listing = true, shownListing, filled, busy, gone, answered;
        bool chosen; // the extension's format menu already picked this video's format: no Format row
        int angle;

        public FileInfoDialog(Api api, string id) : base("Download File Info", 560)
        {
            Api = api;
            Id = id;
            ShowInTaskbar = !Offscreen;
            spinner = new Canvas(PaintSpinner) { Visible = false, AccessibleName = "Getting the video's details", AccessibleRole = AccessibleRole.Animation }; // next to Format, or in Size when the format is already chosen
            spin.Tick += (s, e) => { angle = (angle + 30) % 360; spinner.Invalidate(); };
            category.Items.AddRange(cats);
            category.SelectedIndex = cats.Length - 1;
            Tips.SetToolTip(addCategory, "New category");
            description.Box.AccessibleName = "Description";
            description.Box.MaxLength = 300;
            file.Box.AccessibleName = "Save As";
            Tips.SetToolTip(file.Box, "Missing folders are created.");
            file.Box.TextChanged += (s, e) => { tile.File = file.Text; tile.Invalidate(); }; // the icon follows the name's extension
            url.Font = Theme.Body;
            size = new Caption(Theme.Body, () => chosen && Spinning ? Theme.Muted : Theme.Text) { AccessibleName = "Size" };
            foreach (var c in new Control[] { tile, title, sub, urlLabel, url, note, formatLabel, format, spinner, catLabel, category, addCategory, fileLabel, file, browse, remember, error, descLabel, description, sizeLabel, size }) Add(c);
            Buttons(new[] { later }, new[] { cancel, start });
            AcceptButton = start;
            CancelButton = cancel;

            format.Picked += () =>
            {
                var key = Key();
                if (key == null) return;
                SetCategory(Core.FormatCategory(key));
                file.Text = Core.JoinPath(Core.FolderFor(settings, Category()), Core.WithExt(Core.SplitPath(file.Text)[1], Core.FormatExt(key)));
                ShowSize();
            };
            category.Picked += () =>
            {
                CategoryChanged();
                file.Text = Core.JoinPath(Core.FolderFor(settings, Category()), Core.SplitPath(file.Text)[1]);
            };
            browse.Click += (s, e) => Browse();
            addCategory.Click += (s, e) => NewCategory();
            later.Click += (s, e) => ShowLater();
            cancel.Click += (s, e) => Answer("cancel");
            start.Click += (s, e) => Answer("start");
            FormClosing += (s, e) =>
            {
                // closed with X or Alt+F4 before answering: the engine forgets it, like Cancel
                if (answered || gone || busy || Api == null) return;
                answered = true;
                var a = Api;
                var body = new Dictionary<string, object> { { "id", Id }, { "action", "cancel" } };
                if (rememberDuplicates != null) body["rememberDuplicates"] = rememberDuplicates;
                ThreadPool.QueueUserWorkItem(_ => { try { a.Post("/api/confirm", body); } catch (ApiException) { } });
            };
            FormClosed += (s, e) => spin.Dispose();
            Enable(false);
            ActiveControl = cancel;
            Relayout();
        }

        string Category() { return category.SelectedIndex >= 0 && category.SelectedIndex < cats.Length ? cats[category.SelectedIndex] : "Other"; }
        void SetCategory(string c)
        {
            var i = Array.IndexOf(cats, c);
            category.SelectedIndex = i >= 0 ? i : cats.Length - 1;
            CategoryChanged();
        }

        // The Category list follows settings.types (a category added here or in Options), keeping the choice
        void Categories()
        {
            var names = settings.Types.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).Concat(new[] { "Other" }).ToArray();
            if (settings.Types.Count == 0 || names.SequenceEqual(cats)) return;
            var current = Category();
            cats = names;
            category.Items.Clear();
            category.Items.AddRange(cats);
            SetCategory(current);
        }

        // "+": the category editor for a new category, then it is this file's category
        void NewCategory()
        {
            string name;
            var saved = Dialogs.EditCategory(Api, this, settings, null, out name);
            if (saved == null || answered || gone) return;
            settings = saved;
            Categories();
            SetCategory(name);
            file.Text = Core.JoinPath(Core.FolderFor(settings, name), Core.SplitPath(file.Text)[1]);
            addCategory.Focus();
        }

        // Download Later: the Add to queue popover under the button
        void ShowLater()
        {
            if (busy || listing || gone) return;
            var pop = new QueuePopover(Api, settings.Queues);
            pop.Chosen += (queue, startNow) => Answer("later", queue, startNow);
            var at = later.PointToScreen(new Point(0, later.Height + Theme.Px(4)));
            if (at.Y + pop.Height > Screen.FromControl(this).WorkingArea.Bottom) at.Y = later.PointToScreen(Point.Empty).Y - pop.Height - Theme.Px(4);
            pop.Location = at;
            pop.Show(this);
        }

        // probe.duplicate (with the probe, which may come after the window opened): ask, or apply the saved choice
        void Duplicate()
        {
            if (answered || busy || gone || IsDisposed || d == null || d.Probe == null || d.Probe.Duplicate == null) return;
            var dup = d.Probe.Duplicate;
            var existing = all == null ? null : all.Downloads.FirstOrDefault(x => x.Id == dup.Id);
            var choice = Core.DuplicateChoice(d.Probe, settings);
            if (choice == "ask")
            {
                bool remember;
                choice = Dialogs.AskDuplicate(this, dup.Name, existing != null ? Core.ListStatus(existing, all.Queues) : null, Core.Numbered(d.Probe.Name ?? dup.Name), out remember);
                if (answered || busy || gone || IsDisposed) return; // gone or answered while it asked
                if (remember) rememberDuplicates = choice;
                if (choice == null) { Answer("cancel"); return; }
            }
            if (choice == "replace")
            {
                replaceId = dup.Id;
                replaceFile = existing != null ? existing.File : null;
                if (!string.IsNullOrEmpty(replaceFile)) file.Text = replaceFile;
            }
            else if (choice == "resume") ResumeExisting(dup.Id, existing != null ? existing.Status : dup.Status);
        }

        // "Resume the existing download": this one is cancelled; the existing one resumes, or its window shows
        void ResumeExisting(string id, string status)
        {
            if (Api == null) return;
            var op = Core.DuplicateResume(status);
            var body = new Dictionary<string, object> { { "id", Id }, { "action", "cancel" } };
            if (rememberDuplicates != null) body["rememberDuplicates"] = rememberDuplicates;
            var api = Api;
            Lock(true, "cancel");
            Dialogs.Call(this, () =>
            {
                api.Post("/api/confirm", body);
                if (op == "resume") api.Action(id, "resume");
            }, err =>
            {
                answered = true;
                if (err != null) Dialogs.Error(this, err);
                else if (op != "resume") Dialogs.Progress(api, this, id); // a finished one opens as its Download complete window
                Close();
            });
        }
        void CategoryChanged()
        {
            var c = Category();
            tile.Category = c;
            tile.Invalidate();
            remember.Text = "Remember this folder for " + (c == "Other" ? "other files" : c);
            remember.Width = remember.PreferredWidth;
        }

        string Key() { return formats != null && format.SelectedIndex >= 0 && format.SelectedIndex < formats.Count ? formats[format.SelectedIndex][0] : null; }
        FormatOption Option()
        {
            var key = Key();
            return d == null || d.Probe == null || d.Probe.Options == null ? null : d.Probe.Options.FirstOrDefault(o => o.Key == key);
        }
        // A video with a Format row: yt-dlp's sites, and an HLS stream whose master playlist lists qualities
        bool YouTube { get { return d != null && (d.Kind == "video" || d.Kind == "hls" && d.Probe != null && d.Probe.Options != null && d.Probe.Options.Count > 0); } }
        bool Spinning { get { return YouTube && listing && !gone; } }

        // Every poll: the download (null when it is gone) and the settings
        public void Fill(Download download, Settings s, State state = null)
        {
            if (answered) return;
            if (download == null || download.Status != "pending")
            {
                if (!busy && !gone) Gone(); // while busy, our own answer explains the change
                return;
            }
            d = download;
            settings = s;
            all = state;
            Categories();
            if (!duplicateSeen && d.Probe != null && d.Probe.Duplicate != null && !busy && IsHandleCreated)
            {
                duplicateSeen = true;
                BeginInvoke((MethodInvoker)Duplicate); // after this poll: the question is modal
            }
            if (url.Text != (d.Url ?? "")) url.Text = d.Url ?? "";
            if (gone || filled || d.Probe == null) return;
            if (d.Probe.Listing)
            {
                if (shownListing) return;
                shownListing = true;
            }
            else filled = true;
            Apply(d.Probe);
        }

        void Apply(Probe p)
        {
            listing = p.Listing;
            var yt = YouTube;
            // the pending download's own format (not the probe's pick), while it is listed or still being listed
            chosen = yt && !string.IsNullOrEmpty(d.Format) && (listing || p.Options.Any(o => o.Key == d.Format));
            formatLabel.Visible = format.Visible = yt && !chosen;
            spinner.Visible = Spinning;
            if (Spinning && !Offscreen) spin.Start(); else spin.Stop();
            if (yt)
            {
                format.Items.Clear();
                if (listing)
                {
                    formats = null;
                    format.Items.Add("Getting formats…");
                    format.SelectedIndex = 0;
                }
                else
                {
                    formats = Core.FormatChoices(p);
                    format.Items.AddRange(formats.Select(c => c[1]));
                    format.SelectedIndex = Math.Max(0, formats.FindIndex(c => c[0] == (chosen ? d.Format : p.Format)));
                }
            }
            SetCategory(p.Category);
            var name = p.Name ?? Core.ShortName(d);
            file.Text = Core.JoinPath(Core.FolderFor(settings, Category()), name);
            if (!string.IsNullOrEmpty(replaceFile)) file.Text = replaceFile; // chosen before the format list came in
            sub.Text = Core.Subtitle(d, name);
            ShowSize();
            note.Text = p.Error != null ? "Couldn't read the file details (" + p.Error + "). You can still try to start it." : "";
            note.Visible = p.Error != null;
            Enable(!listing);
            ActiveControl = listing ? cancel : start;
            Relayout();
        }

        void ShowSize()
        {
            var p = d.Probe;
            var o = YouTube ? Option() : null;
            var n = YouTube ? (o != null ? o.Size : null) : p.Size;
            size.Font = n != null ? Theme.Bold : Theme.Body;
            size.Text = chosen && Spinning ? "Getting details…" : n != null ? (o != null && o.Approx ? "~" : "") + Core.Size(n) : YouTube ? "Known once the download starts" : "Unknown";
            size.Invalidate();
        }

        // While listing (or before the probe) only Cancel works
        void Enable(bool on)
        {
            foreach (var c in new Control[] { format, category, addCategory, file, browse, remember, description, later, start }) c.Enabled = on;
        }

        void Lock(bool on, string action)
        {
            busy = on;
            if (on) { Enable(false); cancel.Enabled = false; }
            else { Enable(!listing && !gone); cancel.Enabled = true; }
            start.Text = on && action == "start" ? "Starting…" : "Start Download";
            LayoutFoot();
        }

        // A server's answer under Save As
        public void ShowError(string text)
        {
            error.Text = text ?? "";
            error.Visible = !string.IsNullOrEmpty(text);
            Relayout();
        }

        void Gone()
        {
            gone = true;
            spin.Stop();
            spinner.Visible = false;
            if (chosen) ShowSize(); // not "Getting details…" any more
            Enable(false);
            cancel.Text = "Close";
            Buttons(new FlatButton[0], new[] { cancel });
            AcceptButton = cancel;
            ActiveControl = cancel;
            ShowError("This download is no longer waiting to start.");
        }

        void Browse()
        {
            var parts = Core.SplitPath(file.Text.Trim());
            using (var dlg = new SaveFileDialog { Title = "Save As", OverwritePrompt = false, AddExtension = false, CheckPathExists = false, FileName = parts[1], InitialDirectory = parts[0], Filter = "All files (*.*)|*.*" })
                if (dlg.ShowDialog(this) == DialogResult.OK) file.Text = dlg.FileName;
            browse.Focus();
        }

        void Answer(string action, string queue = null, bool startQueue = false)
        {
            if (busy) return;
            if (gone) { Close(); return; }
            if (listing && action != "cancel") return;
            if (Api == null) return;
            var body = new Dictionary<string, object> { { "id", Id }, { "action", action } };
            if (action != "cancel")
            {
                body["path"] = file.Text.Trim();
                if (remember.Checked) body["remember"] = Category();
                body["description"] = description.Text.Trim();
                // only while Save As is still the existing file: another path keeps the existing download
                if (replaceId != null && (string.IsNullOrEmpty(replaceFile) || string.Equals(body["path"] as string, replaceFile, StringComparison.OrdinalIgnoreCase))) body["replace"] = replaceId;
                if (action == "later")
                {
                    body["queue"] = queue ?? Queue.Main;
                    body["startQueue"] = startQueue;
                }
                if (YouTube)
                {
                    var o = Option();
                    if (o != null) body["format"] = o.Key;
                    else body["quality"] = Key();
                }
            }
            if (rememberDuplicates != null) body["rememberDuplicates"] = rememberDuplicates;
            Lock(true, action);
            ShowError(null);
            var api = Api;
            Dialogs.Call(this, () => api.Post("/api/confirm", body), err =>
            {
                if (err == null || action == "cancel") // a failed cancel: it is already gone
                {
                    answered = true;
                    if (err == null && action == "start") Dialogs.Started(this);
                    else Close();
                    return;
                }
                Lock(false, action);
                ShowError(err);
                file.Box.Focus();
            });
        }

        void PaintSpinner(Graphics g, Rectangle r)
        {
            var s = Theme.Px(14);
            var box = new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var track = new Pen(Theme.Bar, Theme.Px(2))) g.DrawEllipse(track, box);
            using (var pen = new Pen(Theme.Accent, Theme.Px(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, box, angle, 90);
        }

        void Relayout()
        {
            int p = Pad, w = ClientSize.Width - 2 * p, lw = Theme.Px(86), x = p + lw + Theme.Px(12), vw = ClientSize.Width - p - x;
            int row = Theme.Px(32), gap = Theme.Px(12);
            Action<Caption, int, int> label = (c, y0, h) => c.Bounds = new Rectangle(p, y0, lw, h);
            tile.Location = new Point(p, p);
            var tx = p + tile.Width + Theme.Px(12);
            var sh = sub.HeightFor(w - tx + p);
            var th = title.Height + Theme.Px(2) + sh;
            title.Bounds = new Rectangle(tx, p + Math.Max(0, (tile.Height - th) / 2), w - tx + p, title.Height);
            sub.Bounds = new Rectangle(tx, title.Bottom + Theme.Px(2), w - tx + p, sh);
            var y = Math.Max(p + tile.Height, sub.Bottom) + Theme.Px(20);

            var line = Theme.Px(20);
            label(urlLabel, y, line);
            url.Bounds = new Rectangle(x, y + (line - url.PreferredHeight) / 2, vw, url.PreferredHeight);
            y += line + gap;
            if (note.Text != "")
            {
                var h = note.HeightFor(vw);
                note.Bounds = new Rectangle(x, y - Theme.Px(4), vw, h);
                y += h - Theme.Px(4) + gap;
            }
            if (YouTube && !chosen)
            {
                label(formatLabel, y, row);
                var fw = Math.Min(Theme.Px(320), vw - (Spinning ? Theme.Px(24) : 0));
                format.Bounds = new Rectangle(x, y, fw, row);
                spinner.Bounds = new Rectangle(format.Right + Theme.Px(6), y + (row - Theme.Px(18)) / 2, Theme.Px(18), Theme.Px(18));
                y += row + gap;
            }
            label(catLabel, y, row);
            category.Bounds = new Rectangle(x, y, Theme.Px(180), row);
            addCategory.Bounds = new Rectangle(category.Right + Theme.Px(8), y, Theme.Px(36), row);
            y += row + gap;
            label(fileLabel, y, row);
            browse.Size = new Size(Math.Max(Theme.Px(88), browse.PreferredWidth), row);
            browse.Location = new Point(x + vw - browse.Width, y);
            file.Bounds = new Rectangle(x, y, vw - browse.Width - Theme.Px(8), row);
            y += row;
            if (error.Text != "")
            {
                var h = error.HeightFor(vw);
                error.Bounds = new Rectangle(x, y + Theme.Px(4), vw, h);
                y += h + Theme.Px(4);
            }
            y += Theme.Px(6);
            remember.Location = new Point(x, y);
            y += remember.Height + gap;
            label(descLabel, y, row);
            description.Bounds = new Rectangle(x, y, vw, row);
            y += row + gap;
            label(sizeLabel, y, line);
            // own format while listing: the Format row's spinner waits here, before "Getting details…"
            var sx = chosen && Spinning ? x + Theme.Px(24) : x;
            spinner.Bounds = chosen ? new Rectangle(x, y + (line - Theme.Px(18)) / 2, Theme.Px(18), Theme.Px(18)) : spinner.Bounds;
            size.Bounds = new Rectangle(sx, y, ClientSize.Width - p - sx, line);
            y += line;
            BodyHeight(y + p);
        }
    }

    // Download Later's "Add to queue" popover (spec "5"): a borderless window under the button with the queues ("+" asks
    // for a new one's name), "Start the queue now" and Add to queue. Esc or a click elsewhere closes it.
    public class QueuePopover : Form
    {
        public readonly Panel Root = new Panel { Dock = DockStyle.Fill };
        readonly Caption title = new Caption(Theme.Bold, () => Theme.Text) { Text = "Add to queue" };
        readonly Picker picker = new Picker { AccessibleName = "Queue", Height = Theme.Px(32) };
        readonly FlatButton add = new FlatButton("", Glyph.Add, ButtonKind.Secondary) { AccessibleName = "New queue" };
        readonly FlatCheck startNow = new FlatCheck("Start the queue now");
        readonly FlatButton go = new FlatButton("Add to queue", '\0', ButtonKind.Primary);
        readonly ToolTip tips = new ToolTip();
        readonly List<string> ids = new List<string>();
        readonly Api api; // null in GLIDE_SHOTS
        bool asking; // the New queue prompt is open: the popover stays
        public event Action<string, bool> Chosen; // queue id, start it now

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public QueuePopover(Api api, IList<Queue> queues)
        {
            this.api = api;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Font = Theme.Body;
            BackColor = Theme.Inspector;
            ForeColor = Theme.Text;
            AccessibleName = "Add to queue";
            foreach (var q in queues) { ids.Add(q.Id); picker.Items.Add(Core.QueueName(q)); }
            if (ids.Count == 0) { ids.Add(Queue.Main); picker.Items.Add("Main queue"); } // the engine always has it
            picker.SelectedIndex = 0;
            tips.SetToolTip(add, "New queue");
            Root.Paint += (s, e) => { var r = Root.ClientRectangle; r.Width--; r.Height--; using (var pen = new Pen(Theme.Line)) e.Graphics.DrawRectangle(pen, r); };
            var tab = 0;
            foreach (var c in new Control[] { title, picker, add, startNow, go }) { c.TabIndex = tab++; Root.Controls.Add(c); }
            Controls.Add(Root);
            int p = Theme.Px(16), w = Theme.Px(300), row = Theme.Px(32);
            title.Bounds = new Rectangle(p, p, w - 2 * p, title.Height);
            var y = title.Bottom + Theme.Px(10);
            add.Bounds = new Rectangle(w - p - Theme.Px(36), y, Theme.Px(36), row);
            picker.Bounds = new Rectangle(p, y, add.Left - Theme.Px(8) - p, row);
            y += row + Theme.Px(10);
            startNow.Location = new Point(p, y);
            y = startNow.Bottom + Theme.Px(12);
            go.Size = new Size(Math.Max(Theme.Px(88), go.PreferredWidth), row);
            go.Location = new Point(w - p - go.Width, y);
            ClientSize = new Size(w, go.Bottom + p);
            AcceptButton = go;
            ActiveControl = picker;
            go.Click += (s, e) =>
            {
                var h = Chosen;
                var id = ids[Math.Max(0, picker.SelectedIndex)];
                var start = startNow.Checked;
                Close();
                if (h != null) h(id, start);
            };
            add.Click += (s, e) => NewQueue();
            Deactivate += (s, e) => { if (!asking) BeginInvoke((MethodInvoker)(() => { if (!asking && !IsDisposed) Close(); })); };
        }

        // a shadow, and Windows 11's rounded corners (DWMWA_WINDOW_CORNER_PREFERENCE = round)
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }
        protected override bool ShowWithoutActivation { get { return Dialog.Offscreen; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var round = 2;
            try { DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch (DllNotFoundException) { }
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
        }

        void NewQueue()
        {
            asking = true;
            var name = Dialogs.AskText(this, "New queue", "Name of the new queue", "", 40, "Add");
            if (name == null || api == null) { asking = false; return; }
            object answer = null;
            var a = api;
            Dialogs.Call(this, () => answer = a.Queues("add", null, new Dictionary<string, object> { { "name", name } }), err =>
            {
                if (err != null) Dialogs.Error(this, err);
                asking = false;
                var id = err == null && answer is IDictionary<string, object> ? ((IDictionary<string, object>)answer).Str("id") : null;
                if (id == null) return;
                ids.Add(id);
                picker.Items.Add(name);
                picker.SelectedIndex = ids.Count - 1;
                picker.Focus();
            });
        }
    }
}
