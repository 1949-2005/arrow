// Options (docs/NATIVE_UI.md "Dialogs", the old ui.html Settings dialog): Downloads, Schedule and Behavior, validated
// inline, saved with POST /api/settings.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Arrow
{
    public class OptionsDialog : Dialog
    {
        readonly Api api; // null in ARROW_SHOTS
        readonly Caption downloads = Heading("Downloads"), schedule = Heading("Schedule"), behavior = Heading("Behavior");
        readonly Caption dirLabel = Label("Save to folder"), connLabel = Label("Connections per download"), activeLabel = Label("Downloads at the same time"),
            limitLabel = Label("Speed limit (KB/s)"), timeLabel = Label("Start scheduled downloads at");
        readonly TextField dir = new TextField('\0', "");
        readonly FlatButton browse = new FlatButton("Browse…", '\0', ButtonKind.Secondary);
        readonly TextField conn = new TextField('\0', ""), active = new TextField('\0', ""), limit = new TextField('\0', ""), time = new TextField('\0', "HH:MM");
        readonly Caption connHint = Hint("1–32"), activeHint = Hint("1–20"), limitHint = Hint("0 = unlimited"), timeHint = Hint("Empty = off");
        readonly FlatCheck categories = new FlatCheck("Sort into folders"), clipboard = new FlatCheck("Offer links I copy"), startup = new FlatCheck("Start with Windows");
        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 6, Visible = false };
        readonly FlatButton save = new FlatButton("Save", '\0', ButtonKind.Primary);
        readonly FlatButton cancel = Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        bool busy;

        static Caption Heading(string text) { return new Caption(Theme.Bold, () => Theme.Text) { Text = text }; }
        static Caption Hint(string text) { return new Caption(Theme.Body, () => Theme.Muted) { Text = text }; }

        public OptionsDialog(Api api, Settings s, bool focusSchedule) : base("Options", 580)
        {
            this.api = api;
            dir.Text = s.Dir;
            conn.Text = s.Connections.ToString();
            active.Text = s.MaxActive.ToString();
            limit.Text = s.SpeedLimitKB.ToString();
            time.Text = s.ScheduleAt;
            categories.Checked = s.Categories;
            clipboard.Checked = s.Clipboard;
            startup.Checked = s.Startup;
            foreach (var f in new[] { dir, conn, active, limit, time })
            {
                f.Box.TextChanged += (o, e) => Check(false);
            }
            dir.Box.AccessibleName = "Save to folder";
            conn.Box.AccessibleName = "Connections per download, 1 to 32";
            active.Box.AccessibleName = "Downloads at the same time, 1 to 20";
            limit.Box.AccessibleName = "Speed limit in KB/s, 0 = unlimited";
            time.Box.AccessibleName = "Start scheduled downloads at, HH:MM, empty = off";
            foreach (var c in new Control[] { downloads, dirLabel, dir, browse, connLabel, conn, connHint, activeLabel, active, activeHint, limitLabel, limit, limitHint,
                schedule, timeLabel, time, timeHint, behavior, categories, clipboard, startup, error }) Add(c);
            Buttons(new FlatButton[0], new[] { cancel, save });
            AcceptButton = save;
            CancelButton = cancel;
            browse.Click += (o, e) => Browse();
            save.Click += (o, e) => Save();
            if (focusSchedule) Shown += (o, e) => time.Box.Focus();
            Relayout();
        }

        // Marks the bad fields; with all, also shows the first problem (Save); else only clears what is fixed
        bool Check(bool all)
        {
            var problems = new List<string>();
            Func<TextField, bool, string, bool> check = (f, ok, problem) =>
            {
                f.Invalid = !ok;
                if (!ok) problems.Add(problem);
                return ok;
            };
            check(dir, Core.IsFullPath(dir.Text), "Save to folder needs a full path, like C:\\Downloads.");
            check(conn, Core.WholeNumber(conn.Text, 1, 32) != null, "Connections per download must be a whole number from 1 to 32.");
            check(active, Core.WholeNumber(active.Text, 1, 20) != null, "Downloads at the same time must be a whole number from 1 to 20.");
            check(limit, Core.WholeNumber(limit.Text, 0, 10000000) != null, "Speed limit must be a whole number of KB/s, 0 for unlimited.");
            check(time, Core.ScheduleTime(time.Text) != null, "Start time must look like 02:00, or stay empty.");
            if (all || problems.Count == 0 || error.Text != "") ShowError(problems.FirstOrDefault());
            return problems.Count == 0;
        }

        void ShowError(string text)
        {
            var on = !string.IsNullOrEmpty(text);
            if (error.Text == (text ?? "")) return;
            error.Text = text ?? "";
            error.Visible = on;
            Relayout();
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Save to folder", SelectedPath = dir.Text.Trim(), ShowNewFolderButton = true })
                if (dlg.ShowDialog(this) == DialogResult.OK) dir.Text = dlg.SelectedPath;
            browse.Focus();
        }

        void Save()
        {
            if (busy || !Check(true) || api == null) return;
            var body = new Dictionary<string, object>
            {
                { "dir", dir.Text.Trim() },
                { "connections", Core.WholeNumber(conn.Text, 1, 32).Value },
                { "maxActive", Core.WholeNumber(active.Text, 1, 20).Value },
                { "speedLimitKB", Core.WholeNumber(limit.Text, 0, 10000000).Value },
                { "scheduleAt", Core.ScheduleTime(time.Text) },
                { "categories", categories.Checked },
                { "clipboard", clipboard.Checked },
                { "startup", startup.Checked },
            };
            var a = api;
            Lock(true);
            Dialogs.Call(this, () => a.Post("/api/settings", body), err =>
            {
                Lock(false);
                if (err == null) DialogResult = DialogResult.OK;
                else ShowError(err);
            });
        }

        void Lock(bool on)
        {
            busy = on;
            foreach (var c in new Control[] { dir, browse, conn, active, limit, time, categories, clipboard, startup, save }) c.Enabled = !on;
        }

        void Relayout()
        {
            // the label column fits its longest label ("Start scheduled downloads at")
            var lw = Math.Max(Theme.Px(200), new[] { dirLabel, connLabel, activeLabel, limitLabel, timeLabel }.Max(c => Theme.Measure(c.Text, c.Font).Width));
            int p = Pad, x = p + lw + Theme.Px(12), vw = ClientSize.Width - p - x, row = Theme.Px(32), gap = Theme.Px(8), nw = Theme.Px(72);
            var y = p;
            Func<Caption, int> head = h => { h.Bounds = new Rectangle(p, y, ClientSize.Width - 2 * p, h.Height); return h.Height + gap; };
            Action<Caption, TextField, Caption> number = (label, field, hint) =>
            {
                label.Bounds = new Rectangle(p, y, lw, row);
                field.Bounds = new Rectangle(x, y, nw, row);
                hint.Bounds = new Rectangle(x + nw + Theme.Px(8), y, vw - nw - Theme.Px(8), row);
                y += row + gap;
            };
            y += head(downloads);
            dirLabel.Bounds = new Rectangle(p, y, lw, row);
            browse.Size = new Size(Math.Max(Theme.Px(88), browse.PreferredWidth), row);
            browse.Location = new Point(x + vw - browse.Width, y);
            dir.Bounds = new Rectangle(x, y, vw - browse.Width - Theme.Px(8), row);
            y += row + gap;
            number(connLabel, conn, connHint);
            number(activeLabel, active, activeHint);
            number(limitLabel, limit, limitHint);
            y += Theme.Px(12);
            y += head(schedule);
            number(timeLabel, time, timeHint);
            y += Theme.Px(12);
            y += head(behavior);
            foreach (var c in new[] { categories, clipboard, startup })
            {
                c.Location = new Point(p, y);
                y += c.Height + Theme.Px(4);
            }
            if (error.Text != "")
            {
                var h = error.HeightFor(ClientSize.Width - 2 * p);
                error.Bounds = new Rectangle(p, y + Theme.Px(4), ClientSize.Width - 2 * p, h);
                y += h + Theme.Px(4);
            }
            BodyHeight(y - Theme.Px(4) + p);
        }
    }
}
