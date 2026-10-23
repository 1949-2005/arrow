// The progress window (docs/NATIVE_UI.md "Dialogs", the behavior of the old progress.html), styled like the inspector:
// running (percent, segments, speed graph, facts, the Connections and Options disclosures, Pause/Resume and Cancel),
// Download complete (Saved to, File size, Address, "Don't show this window again", Open with…, Open folder, Close, Open)
// or removed.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Arrow
{
    public class ProgressDialog : Dialog
    {
        public readonly string Id;
        readonly Api api; // null in ARROW_SHOTS
        static readonly string[] WhenDoneValues = { "", "open", "sleep", "shutdown" };

        readonly Tile tile = new Tile();
        readonly Caption title = new Caption(Theme.Bold, () => Theme.Text);
        readonly Caption name = new Caption(Theme.Body, () => Theme.Muted);
        readonly Caption big = new Caption(Theme.Big, () => Theme.Text);
        readonly Caption sub = new Caption(Theme.Body, () => Theme.Muted);
        readonly SegmentBar bar = new SegmentBar();
        readonly Sparkline graph = new Sparkline { AccessibleName = "Speed, last 60 seconds" };
        readonly List<FactRow> facts = new List<FactRow>();
        readonly Disclosure connHead = new Disclosure("Connections"), optHead = new Disclosure("Options");
        readonly ConnectionTable table = new ConnectionTable();
        readonly FlatCheck limitCheck = new FlatCheck("Limit this download to");
        readonly TextField limitField = new TextField('\0', "");
        readonly Caption kbs = Label("KB/s");
        readonly Caption doneLabel = new Caption(Theme.Body, () => Theme.Text) { Text = "When this download is done" };
        readonly Picker donePicker = new Picker { AccessibleName = "When this download is done" };
        readonly FlatCheck hideComplete = new FlatCheck("Don't show this window again");
        readonly Timer apply = new Timer { Interval = 600 }; // a typed limit goes out once the typing pauses
        readonly FlatButton toggle = new FlatButton("Pause", '\0', ButtonKind.Secondary);
        readonly FlatButton cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary);
        readonly FlatButton openWith = new FlatButton("Open with…", '\0', ButtonKind.Secondary);
        readonly FlatButton folder = new FlatButton("Open folder", '\0', ButtonKind.Secondary);
        readonly FlatButton close = new FlatButton("Close", '\0', ButtonKind.Secondary);
        readonly FlatButton open = new FlatButton("Open", '\0', ButtonKind.Primary);
        Download d;
        string mode;
        bool statusErr, syncing, showConns;
        int inflight; // option changes on their way: polls don't overwrite them meanwhile

        public ProgressDialog(Api api, string id) : base("Progress", 460)
        {
            this.api = api;
            Id = id;
            ShowInTaskbar = !Offscreen;
            for (var i = 0; i < 7; i++) facts.Add(new FactRow());
            facts[1].ValueColor = () => statusErr ? Theme.Err : Theme.Text;
            foreach (var c in new Control[] { tile, title, name, big, sub, bar, graph }) Add(c);
            foreach (var f in facts) Add(f);
            foreach (var c in new Control[] { connHead, table, optHead, limitCheck, limitField, kbs, doneLabel, donePicker, hideComplete }) Add(c);
            donePicker.Items.AddRange(new[] { "Nothing", "Open the file", "Sleep", "Shut down" });
            donePicker.SelectedIndex = 0;
            limitField.Text = "500"; // what checking the box limits to while the download has no limit
            limitField.Box.AccessibleName = "Limit this download to, in KB/s, 1 to 10 000 000";
            connHead.Open = Dialogs.ConnectionsOpen;
            optHead.Open = Dialogs.OptionsOpen;
            connHead.Click += (s, e) => { Dialogs.ConnectionsOpen = connHead.Open = !connHead.Open; Remember(); };
            optHead.Click += (s, e) => { Dialogs.OptionsOpen = optHead.Open = !optHead.Open; Remember(); };

            limitCheck.CheckedChanged += (s, e) =>
            {
                if (syncing) return;
                apply.Stop();
                if (!limitCheck.Checked) { limitField.Invalid = false; Set("limit", "kb", 0); return; }
                var kb = Core.WholeNumber(limitField.Text, 1, 10000000);
                limitField.Invalid = kb == null;
                if (kb == null) limitField.Box.Focus();
                else Set("limit", "kb", kb.Value);
            };
            limitField.Box.TextChanged += (s, e) =>
            {
                if (syncing) return;
                apply.Stop();
                limitField.Invalid = Core.WholeNumber(limitField.Text, 1, 10000000) == null;
                if (!limitField.Invalid) apply.Start();
            };
            limitField.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (apply.Enabled) ApplyLimit(); } };
            apply.Tick += (s, e) => ApplyLimit();
            donePicker.Picked += () => Set("whenDone", "value", WhenDoneValues[donePicker.SelectedIndex]);

            toggle.Click += (s, e) => { if (d != null) Act(Core.CanPause(d) ? "pause" : "resume", null); };
            cancel.Click += (s, e) =>
            {
                if (d != null && Dialogs.Confirm(this, "Cancel download", "Cancel this download? The partial file is deleted.", "Cancel download", true))
                    Act("remove", Close);
            };
            openWith.Click += (s, e) => { if (d != null && !string.IsNullOrEmpty(d.File)) Dialogs.OpenWith(this, d.File); };
            folder.Click += (s, e) => Act("folder", null);
            open.Click += (s, e) => Act("open", null);
            close.Click += (s, e) => Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) apply.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (mode == "done" && hideComplete.Checked && api != null)
            {
                var a = api;
                Dialogs.Call(this, () => a.Post("/api/settings", new Dictionary<string, object> { { "showComplete", false } }), err => { });
            }
            base.OnFormClosing(e);
        }

        void Remember()
        {
            if (Dialogs.UiChanged != null) Dialogs.UiChanged();
            Relayout();
        }

        // A typed, valid limit: the box turns on and the engine gets it
        void ApplyLimit()
        {
            apply.Stop();
            var kb = Core.WholeNumber(limitField.Text, 1, 10000000);
            if (kb == null) return;
            syncing = true;
            limitCheck.Checked = true;
            syncing = false;
            Set("limit", "kb", kb.Value);
        }

        void Set(string op, string field, object value)
        {
            if (api == null) return;
            var a = api;
            inflight++;
            Dialogs.Call(this, () => a.Action(Id, op, field, value), err =>
            {
                inflight--;
                if (err != null) Dialogs.Error(this, err);
            });
        }

        void Act(string op, Action then)
        {
            if (api == null) return;
            var a = api;
            var body = new Dictionary<string, object> { { "id", Id }, { "op", op } };
            Dialogs.Call(this, () => a.Post("/api/action", body), err =>
            {
                if (err != null) Dialogs.Error(this, err);
                else if (then != null) then();
            });
        }

        // Every poll: the download (null once it was removed), the engine's categories, this window's speed samples
        public void Fill(Download download, IDictionary<string, List<string>> categories, IList<double> samples)
        {
            d = download;
            if (d == null)
            {
                Mode("gone");
                title.Text = "This download was removed.";
                return;
            }
            var done = d.Status == "done";
            Mode(done ? "done" : "run");
            var short_ = Core.ShortName(d);
            var fraction = Core.Fraction(d, true);
            Text = done ? "Download complete" : Math.Floor(fraction * 100) + "% · " + short_;
            tile.Category = Core.Category(d, categories);
            tile.File = string.IsNullOrEmpty(d.File) ? short_ : d.File;
            tile.Complete = done;
            tile.Invalidate();
            title.Text = Core.Heading(d);
            name.Text = short_;
            Tips.SetToolTip(name, string.IsNullOrEmpty(d.File) ? short_ : d.File);
            if (done)
            {
                Facts("Saved to", string.IsNullOrEmpty(d.File) ? "" : Core.SplitPath(d.File)[0], "File size", Core.Size(d.Size, "Unknown"), "Address", d.Url ?? "");
                facts[0].PathValue = true;
                Tips.SetToolTip(facts[0], d.File);
                Tips.SetToolTip(facts[2], d.Url);
                openWith.Enabled = open.Enabled = !string.IsNullOrEmpty(d.File);
                return;
            }
            bar.Set(Core.Pieces(d), d.Status == "error" ? BarKind.Err : BarKind.Normal);
            var running = d.Status == "downloading";
            var t = Core.Total(d);
            var total = t == null ? (double?)null : Math.Round(t.Value);
            big.Text = Math.Floor(fraction * 100) + "%";
            sub.Text = Core.Size(d.Got) + " of " + Core.Size(total);
            graph.Set(samples, running ? Core.Size(d.Speed) + "/s" : "");
            statusErr = d.Status == "error";
            var eta = Core.Eta(d);
            Facts("URL", d.Url ?? "",
                "Status", Core.StatusText(d),
                "File size", Core.Size(total, "Unknown"),
                "Downloaded", Core.Size(d.Got, "Unknown") + " (" + (Math.Floor(fraction * 1000) / 10).ToString(CultureInfo.CurrentCulture) + "%)",
                "Transfer rate", running ? Core.Size(d.Speed) + "/s" : "—",
                "Time left", eta == "" ? "—" : eta,
                "Resume capability", d.Resumable == true ? "Yes" : d.Resumable == false ? "No" : "—");
            Tips.SetToolTip(facts[0], d.Url);
            Tips.SetToolTip(facts[1], statusErr ? Core.StatusText(d) : null);
            var label = Core.CanPause(d) ? "Pause" : "Resume";
            if (toggle.Text != label) { toggle.Text = label; LayoutFoot(); }
            toggle.Enabled = Core.CanPause(d) || Core.CanResume(d);
            cancel.Enabled = d.Status != "removed";

            var rows = Core.ConnectionRows(d);
            var count = table.Rows.Count;
            table.Rows = rows;
            connHead.Text = "Connections (" + rows.Count + ")";
            table.Invalidate();
            if (rows.Count != count) Relayout();
            Sync();
            Invalidate(true);
        }

        // The engine's limit and when-done choice, unless the user is changing them
        void Sync()
        {
            if (inflight > 0) return;
            syncing = true;
            if (!limitField.Box.Focused && !apply.Enabled && !limitField.Invalid)
            {
                limitCheck.Checked = d.LimitKB > 0;
                if (d.LimitKB > 0) limitField.Text = d.LimitKB.ToString(CultureInfo.InvariantCulture);
            }
            var i = Math.Max(0, Array.IndexOf(WhenDoneValues, d.WhenDone));
            if (donePicker.SelectedIndex != i) donePicker.SelectedIndex = i;
            syncing = false;
        }

        void Facts(params string[] pairs)
        {
            for (var i = 0; i < facts.Count; i++)
            {
                var on = i * 2 < pairs.Length;
                facts[i].Visible = on;
                facts[i].PathValue = false;
                if (on) facts[i].Set(pairs[i * 2], pairs[i * 2 + 1]);
            }
        }

        void Mode(string m)
        {
            if (mode == m) return;
            mode = m;
            var run = m == "run";
            var gone = m == "gone";
            tile.Visible = name.Visible = !gone;
            bar.Visible = big.Visible = sub.Visible = graph.Visible = run;
            title.Visible = true;
            if (gone) foreach (var f in facts) f.Visible = false;
            if (run)
            {
                Buttons(new[] { toggle }, new[] { cancel });
                AcceptButton = null;
                CancelButton = null; // Esc closes the window; Cancel deletes the download
                ActiveControl = toggle;
            }
            else if (m == "done")
            {
                Buttons(new[] { openWith }, new[] { folder, close, open });
                AcceptButton = open;
                CancelButton = close;
                ActiveControl = open;
            }
            else
            {
                Text = Program.Name;
                Buttons(new FlatButton[0], new[] { close });
                AcceptButton = CancelButton = close;
                ActiveControl = close;
            }
            Relayout();
        }

        void Relayout()
        {
            var run = mode == "run";
            // own flags: Visible reads false until the window is shown
            showConns = run && table.Rows.Count > 0;
            connHead.Visible = showConns;
            table.Visible = showConns && connHead.Open;
            optHead.Visible = run;
            limitCheck.Visible = limitField.Visible = kbs.Visible = doneLabel.Visible = donePicker.Visible = run && optHead.Open;
            hideComplete.Visible = mode == "done";
            var h = Place(int.MaxValue);
            if (showConns && connHead.Open && !Offscreen)
            {
                // a long table gives up rows (it scrolls) so the window stays within the screen
                var wa = Screen.FromRectangle(Bounds).WorkingArea;
                var room = wa.Height - (Height - ClientSize.Height) - Foot.Height;
                if (h > room) h = Place(Math.Max(2, table.Rows.Count - (h - room + ConnectionTable.Row - 1) / ConnectionTable.Row));
            }
            BodyHeight(h);
            if (IsHandleCreated && !Offscreen)
            {
                var wa = Screen.FromRectangle(Bounds).WorkingArea;
                if (Bottom > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
            }
        }

        // Lays out the body with at most maxRows rows of the Connections table; returns the body's height
        int Place(int maxRows)
        {
            int p = Pad, w = ClientSize.Width - 2 * p, y = p;
            if (mode == "gone")
            {
                title.Bounds = new Rectangle(p, y, w, title.Height);
                return y + title.Height + p;
            }
            tile.Location = new Point(p, y);
            var tx = p + tile.Width + Theme.Px(12);
            var th = title.Height + Theme.Px(2) + name.Height;
            title.Bounds = new Rectangle(tx, y + (tile.Height - th) / 2, w + p - tx, title.Height);
            name.Bounds = new Rectangle(tx, title.Bottom + Theme.Px(2), w + p - tx, name.Height);
            y += tile.Height + Theme.Px(16);
            if (mode == "run")
            {
                var bw = Theme.Measure("100%", big.Font).Width + Theme.Px(2);
                big.Bounds = new Rectangle(p, y, bw, big.Height);
                sub.Bounds = new Rectangle(p + bw + Theme.Px(16), y + big.Height - sub.Height - Theme.Px(3), w - bw - Theme.Px(16), sub.Height);
                y += big.Height + Theme.Px(8);
                bar.Bounds = new Rectangle(p, y, w, Theme.Px(8));
                y += Theme.Px(8 + 12);
                graph.Bounds = new Rectangle(p, y, w, Theme.Px(56));
                y += Theme.Px(56 + 12);
            }
            var count = mode == "run" ? 7 : 3;
            for (var i = 0; i < count; i++)
            {
                facts[i].Bounds = new Rectangle(p, y, w, facts[i].Height);
                y += facts[i].Height;
            }
            if (mode == "done")
            {
                y += Theme.Px(12);
                hideComplete.Location = new Point(p, y);
                hideComplete.Width = hideComplete.PreferredWidth;
                return hideComplete.Bottom + p;
            }
            int head = Theme.Px(28), row = Theme.Px(32), gap = Theme.Px(8);
            y += Theme.Px(12);
            if (showConns)
            {
                connHead.Bounds = new Rectangle(p - Theme.Px(6), y, w + Theme.Px(12), head);
                y += head + Theme.Px(4);
                if (connHead.Open)
                {
                    var shown = Math.Min(maxRows, table.Rows.Count);
                    table.Bounds = new Rectangle(p, y, w, ConnectionTable.Row * (shown + 1));
                    y += table.Height + Theme.Px(12);
                }
            }
            optHead.Bounds = new Rectangle(p - Theme.Px(6), y, w + Theme.Px(12), head);
            y += head + Theme.Px(4);
            if (optHead.Open)
            {
                limitCheck.Width = limitCheck.PreferredWidth;
                limitCheck.Location = new Point(p, y + (row - limitCheck.Height) / 2);
                limitField.Bounds = new Rectangle(limitCheck.Right + gap, y, Theme.Px(88), row);
                kbs.Bounds = new Rectangle(limitField.Right + gap, y, Theme.Px(60), row);
                y += row + gap;
                var lw = Theme.Measure(doneLabel.Text, doneLabel.Font).Width + Theme.Px(2);
                doneLabel.Bounds = new Rectangle(p, y, lw, row);
                donePicker.Bounds = new Rectangle(p + lw + Theme.Px(12), y, Theme.Px(180), row);
                y += row;
            }
            else y -= Theme.Px(4);
            return y + p;
        }

        // A disclosure row: a 10 px chevron (down when open, up when closed) and its title; Click toggles
        class Disclosure : FlatButton
        {
            bool isOpen;
            public Disclosure(string text) : base(text, '\0', ButtonKind.Ghost) { Font = Theme.Bold; Radius = Theme.Px(6); AccessibleRole = AccessibleRole.OutlineButton; }

            public bool Open
            {
                get { return isOpen; }
                set { isOpen = value; Invalidate(); AccessibilityNotifyClients(AccessibleEvents.StateChange, -1); }
            }

            protected override AccessibleObject CreateAccessibilityInstance() { return new DisclosureAccessible(this); }
            class DisclosureAccessible : ControlAccessibleObject
            {
                public DisclosureAccessible(Disclosure d) : base(d) { }
                public override AccessibleStates State { get { return base.State | (((Disclosure)Owner).Open ? AccessibleStates.Expanded : AccessibleStates.Collapsed); } }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var r = ClientRectangle;
                if (pressed || hover) Theme.Fill(g, r, Radius, pressed ? Theme.Soft : Theme.Hover);
                var x = Theme.Px(6);
                Theme.DrawGlyph(g, isOpen ? Glyph.ChevronDown : Glyph.ChevronUp, new Rectangle(x, 0, Theme.Px(10), r.Height), Theme.Muted, 10);
                x += Theme.Px(10 + 8);
                Theme.DrawText(g, Text, Font, new Rectangle(x, 0, r.Width - x, r.Height), Theme.Text);
                if (Focused && ShowFocusCues) Theme.FocusRing(g, r, Radius);
            }
        }

        // The Connections table: # · Downloaded · Info under a muted header, rows 26 with 1 px dividers. When it is
        // shorter than its rows (a small screen), the wheel scrolls them.
        class ConnectionTable : Painted
        {
            public static int Row { get { return Theme.Px(26); } }
            public List<string[]> Rows = new List<string[]>();
            int top;

            public ConnectionTable() { AccessibleName = "Connections"; }

            int Fit { get { return Math.Max(0, Height / Row - 1); } }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                top = Math.Max(0, Math.Min(Rows.Count - Fit, top - Math.Sign(e.Delta)));
                Invalidate();
                base.OnMouseWheel(e);
            }

            protected override AccessibleObject CreateAccessibilityInstance()
            {
                ListAccessible list = null;
                list = new ListAccessible(this, () => Rows.Count, i => new ItemAccessible(list,
                    () => i < Rows.Count ? "Connection " + (i + 1) + ": " + Rows[i][0] + ", " + Rows[i][1] : "",
                    () => false, () => false, () => RectangleToScreen(new Rectangle(0, (i - top + 1) * Row, Width, Row)), () => { }));
                return list;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                int nx = Theme.Px(30), ix = Width / 2;
                Action<int, string, string, string, Color> line = (y, n, got, info, c) =>
                {
                    Theme.DrawText(g, n, Theme.Body, new Rectangle(0, y, nx, Row), y == 0 ? Theme.Muted : Theme.Text);
                    Theme.DrawText(g, got, Theme.Body, new Rectangle(nx, y, ix - nx - Theme.Px(8), Row), y == 0 ? Theme.Muted : Theme.Text);
                    Theme.DrawText(g, info, Theme.Body, new Rectangle(ix, y, Width - ix, Row), c);
                    Theme.HLine(g, 0, Width, y + Row - 1);
                };
                line(0, "#", "Downloaded", "Info", Theme.Muted);
                top = Math.Max(0, Math.Min(top, Rows.Count - Fit));
                for (var i = top; i < Math.Min(Rows.Count, top + Fit); i++)
                    line((i - top + 1) * Row, (i + 1).ToString(CultureInfo.CurrentCulture), Rows[i][0], Rows[i][1], Rows[i][1] == Core.Receiving ? Theme.Text : Theme.Muted);
            }
        }
    }
}
