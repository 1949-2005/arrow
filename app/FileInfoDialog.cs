// Download File Info (docs/NATIVE_UI.md "Dialogs", the behavior of the old confirm.html): one pending download's name,
// format, category and Save As, answered with POST /api/confirm start, later or cancel.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Arrow
{
    public class FileInfoDialog : Dialog
    {
        public readonly string Id;
        public readonly Api Api; // null in ARROW_SHOTS
        static readonly string[] Categories = { "Compressed", "Documents", "Music", "Programs", "Video", "Other" };

        readonly Tile tile = new Tile();
        readonly Caption title = new Caption(Theme.Bold, () => Theme.Text) { Text = "Download File Info" };
        readonly Caption sub = new Caption(Theme.Body, () => Theme.Muted) { Text = "Getting file info…" };
        readonly Caption urlLabel = Label("URL"), formatLabel = Label("Format"), catLabel = Label("Category"), fileLabel = Label("Save As"), sizeLabel = Label("Size");
        readonly TextBox url = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, AccessibleName = "URL" };
        readonly Caption note = new Caption(Theme.Body, () => Theme.Warn) { MaxLines = 3, Visible = false };
        readonly Picker format = new Picker { AccessibleName = "Format", Height = Theme.Px(32) };
        readonly Canvas spinner;
        readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer { Interval = 50 };
        readonly Picker category = new Picker { AccessibleName = "Category", Height = Theme.Px(32) };
        readonly TextField file = new TextField('\0', "");
        readonly FlatButton browse = new FlatButton("Browse…", '\0', ButtonKind.Secondary);
        readonly Caption hint = new Caption(Theme.Small, () => Theme.Muted) { Text = "Missing folders are created." };
        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 4, Visible = false };
        readonly Caption size = new Caption(Theme.Body, () => Theme.Text);
        readonly FlatButton later = new FlatButton("Download Later", '\0', ButtonKind.Secondary);
        readonly FlatButton cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary);
        readonly FlatButton start = new FlatButton("Start Download", '\0', ButtonKind.Primary);

        Download d;
        Settings settings = new Settings();
        List<string[]> formats; // [key, text]; null while listing
        bool listing = true, shownListing, filled, busy, gone, answered;
        int angle;

        public FileInfoDialog(Api api, string id) : base("Download File Info", 560)
        {
            Api = api;
            Id = id;
            ShowInTaskbar = !Offscreen;
            spinner = new Canvas(PaintSpinner) { Visible = false, AccessibleName = "Getting formats", AccessibleRole = AccessibleRole.Animation };
            spin.Tick += (s, e) => { angle = (angle + 30) % 360; spinner.Invalidate(); };
            category.Items.AddRange(Categories);
            category.SelectedIndex = Categories.Length - 1;
            file.Box.AccessibleName = "Save As";
            url.Font = Theme.Body;
            size.AccessibleName = "Size";
            foreach (var c in new Control[] { tile, title, sub, urlLabel, url, note, formatLabel, format, spinner, catLabel, category, fileLabel, file, browse, hint, error, sizeLabel, size }) Add(c);
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
                tile.Category = Category();
                tile.Invalidate();
                file.Text = Core.JoinPath(Core.FolderFor(settings, Category()), Core.SplitPath(file.Text)[1]);
            };
            browse.Click += (s, e) => Browse();
            later.Click += (s, e) => Answer("later");
            cancel.Click += (s, e) => Answer("cancel");
            start.Click += (s, e) => Answer("start");
            FormClosing += (s, e) =>
            {
                // closed with X or Alt+F4 before answering: the engine forgets it, like Cancel
                if (answered || gone || busy || Api == null) return;
                answered = true;
                var a = Api;
                var body = new Dictionary<string, object> { { "id", Id }, { "action", "cancel" } };
                ThreadPool.QueueUserWorkItem(_ => { try { a.Post("/api/confirm", body); } catch (ApiException) { } });
            };
            FormClosed += (s, e) => spin.Dispose();
            Enable(false);
            ActiveControl = cancel;
            Relayout();
        }

        string Category() { return category.SelectedIndex >= 0 ? Categories[category.SelectedIndex] : "Other"; }
        void SetCategory(string c)
        {
            var i = Array.IndexOf(Categories, c);
            category.SelectedIndex = i >= 0 ? i : Categories.Length - 1;
            tile.Category = Category();
            tile.Invalidate();
        }

        string Key() { return formats != null && format.SelectedIndex >= 0 && format.SelectedIndex < formats.Count ? formats[format.SelectedIndex][0] : null; }
        FormatOption Option()
        {
            var key = Key();
            return d == null || d.Probe == null || d.Probe.Options == null ? null : d.Probe.Options.FirstOrDefault(o => o.Key == key);
        }
        bool YouTube { get { return d != null && d.Kind == "video"; } }
        bool Spinning { get { return YouTube && listing && !gone; } }

        // Every poll: the download (null when it is gone) and the settings
        public void Fill(Download download, Settings s)
        {
            if (answered) return;
            if (download == null || download.Status != "pending")
            {
                if (!busy && !gone) Gone(); // while busy, our own answer explains the change
                return;
            }
            d = download;
            settings = s;
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
            formatLabel.Visible = format.Visible = yt;
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
                    format.SelectedIndex = Math.Max(0, formats.FindIndex(c => c[0] == p.Format));
                }
            }
            SetCategory(p.Category);
            var name = p.Name ?? Core.ShortName(d);
            file.Text = Core.JoinPath(Core.FolderFor(settings, Category()), name);
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
            size.Text = n != null ? Core.Size(n) : YouTube ? "Known once the download starts" : "Unknown";
        }

        // While listing (or before the probe) only Cancel works
        void Enable(bool on)
        {
            foreach (var c in new Control[] { format, category, file, browse, later, start }) c.Enabled = on;
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

        void Answer(string action)
        {
            if (busy) return;
            if (gone) { Close(); return; }
            if (listing && action != "cancel") return;
            if (Api == null) return;
            var body = new Dictionary<string, object> { { "id", Id }, { "action", action } };
            if (action != "cancel")
            {
                body["path"] = file.Text.Trim();
                if (YouTube)
                {
                    var o = Option();
                    if (o != null) body["format"] = o.Key;
                    else body["quality"] = Key();
                }
            }
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
            var th = title.Height + Theme.Px(2) + sub.Height;
            title.Bounds = new Rectangle(tx, p + (tile.Height - th) / 2, w - tx + p, title.Height);
            sub.Bounds = new Rectangle(tx, title.Bottom + Theme.Px(2), w - tx + p, sub.Height);
            var y = p + tile.Height + Theme.Px(20);

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
            if (YouTube)
            {
                label(formatLabel, y, row);
                var fw = Math.Min(Theme.Px(320), vw - (Spinning ? Theme.Px(24) : 0));
                format.Bounds = new Rectangle(x, y, fw, row);
                spinner.Bounds = new Rectangle(format.Right + Theme.Px(6), y + (row - Theme.Px(18)) / 2, Theme.Px(18), Theme.Px(18));
                y += row + gap;
            }
            label(catLabel, y, row);
            category.Bounds = new Rectangle(x, y, Theme.Px(180), row);
            y += row + gap;
            label(fileLabel, y, row);
            browse.Size = new Size(Math.Max(Theme.Px(88), browse.PreferredWidth), row);
            browse.Location = new Point(x + vw - browse.Width, y);
            file.Bounds = new Rectangle(x, y, vw - browse.Width - Theme.Px(8), row);
            y += row + Theme.Px(4);
            hint.Bounds = new Rectangle(x, y, vw, hint.Height);
            y += hint.Height;
            if (error.Text != "")
            {
                var h = error.HeightFor(vw);
                error.Bounds = new Rectangle(x, y + Theme.Px(2), vw, h);
                y += h + Theme.Px(2);
            }
            y += gap;
            label(sizeLabel, y, line);
            size.Bounds = new Rectangle(x, y, vw, line);
            y += line;
            BodyHeight(y + p);
        }
    }
}
