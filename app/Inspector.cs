// The main window's right column: one download, several, or the overview (docs/NATIVE_UI.md "Inspector").
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Glide
{
    // Error text in an err-tinted rounded box, selectable
    public class ErrorBox : Painted, IThemed
    {
        public readonly TextBox Box = new TextBox { ReadOnly = true, Multiline = true, BorderStyle = BorderStyle.None, WordWrap = true, TabStop = true };

        public ErrorBox()
        {
            Box.Font = Theme.Body;
            Box.AccessibleName = "Error";
            Controls.Add(Box);
        }

        Color Tint { get { return Theme.Mix(Surface, Theme.Err, Theme.Dark ? 0.20 : 0.12); } }
        int TextLeft { get { return Theme.Px(12 + 16 + 8); } }

        public void Restyle() { Box.BackColor = Tint; Box.ForeColor = Theme.Text; }
        public int HeightFor(int width) { return Theme.MeasureHeight(Box.Text, Theme.Body, width - TextLeft - Theme.Px(12)) + Theme.Px(24) + Theme.Px(2); }

        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); Restyle(); }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Box.Bounds = new Rectangle(TextLeft, Theme.Px(12), Width - TextLeft - Theme.Px(12), Height - Theme.Px(24));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Fill(e.Graphics, ClientRectangle, Theme.Px(8), Tint);
            Theme.DrawGlyph(e.Graphics, Glyph.Error, new Rectangle(Theme.Px(12), Theme.Px(12), Theme.Px(16), Theme.Body.Height), Theme.Err, 16);
        }
    }

    public class Inspector : Panel, IThemed
    {
        // pause, resume, open, folder, copy, link, progress, remove: on the shown selection
        public Action<string> Do = op => { };

        readonly ToolTip tips;
        readonly Tile tile = new Tile();
        readonly Caption name = new Caption(Theme.Bold, () => Theme.Text) { MaxLines = Caption.Unlimited };
        readonly Caption host = new Caption(Theme.Body, () => Theme.Muted);
        readonly FlatButton copy = new FlatButton("", Glyph.Copy, ButtonKind.Ghost) { AccessibleName = "Copy link", Radius = Theme.Px(6) };
        readonly Caption heading = new Caption(Theme.Bold, () => Theme.Text) { Text = "Overview" };
        readonly Caption big;
        readonly Caption sub = new Caption(Theme.Body, () => Theme.Muted);
        readonly SegmentBar bar = new SegmentBar();
        readonly ErrorBox error = new ErrorBox();
        readonly Sparkline graph = new Sparkline();
        readonly List<FactRow> facts = new List<FactRow>();
        readonly Caption names = new Caption(Theme.Body, () => Theme.Text) { MaxLines = Caption.Unlimited }; // up to 5 names, each whole
        readonly Caption free = new Caption(Theme.Body, () => Theme.Muted);
        readonly List<FlatButton> buttons = new List<FlatButton>();
        string buttonsKey, mode;
        bool bigErr, laying;
        FactRow link; // the row that opens the folder

        public Inspector(ToolTip tips)
        {
            this.tips = tips;
            big = new Caption(Theme.Big, () => bigErr ? Theme.Err : Theme.Text);
            AutoScroll = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            Size = new Size(Theme.Px(320), Theme.Px(400));
            copy.Size = new Size(Theme.Px(24), Theme.Px(24));
            tips.SetToolTip(copy, "Copy link");
            copy.Click += (s, e) => Do("copy");
            for (var i = 0; i < 12; i++)
            {
                var f = new FactRow();
                f.Click += (s, e) => { if (s == link) Do("folder"); };
                facts.Add(f);
            }
            Controls.AddRange(new Control[] { tile, name, host, copy, heading, big, sub, bar, error, graph, names, free });
            Controls.AddRange(facts.ToArray());
            Restyle();
        }

        public void Restyle() { BackColor = Theme.Inspector; error.Restyle(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Theme.VLine(e.Graphics, 0, 0, Height);
        }

        void Facts(params string[] pairs)
        {
            for (var i = 0; i < facts.Count; i++)
            {
                var on = i * 2 < pairs.Length;
                facts[i].Visible = on;
                if (on) facts[i].Set(pairs[i * 2], pairs[i * 2 + 1]);
                facts[i].Cursor = Cursors.Default;
                facts[i].PathValue = false;
                tips.SetToolTip(facts[i], null);
            }
            link = null;
        }

        // spec: text, op, kind, enabled, destructive
        void Buttons(params object[][] spec)
        {
            var key = string.Join("|", spec.Select(b => b[0] + ":" + b[3]));
            if (key == buttonsKey) return;
            buttonsKey = key;
            var focused = buttons.Any(b => b.Focused);
            foreach (var b in buttons) b.Dispose();
            buttons.Clear();
            foreach (var b in spec)
            {
                var op = (string)b[1];
                var btn = new FlatButton((string)b[0], '\0', (ButtonKind)b[2]) { Enabled = (bool)b[3], Destructive = (bool)b[4] };
                btn.Click += (s, e) => Do(op);
                buttons.Add(btn);
                Controls.Add(btn);
            }
            if (focused && buttons.Count > 0) buttons[0].Focus();
        }

        void Mode(string m, params Control[] shown)
        {
            mode = m;
            var set = new HashSet<Control>(shown);
            foreach (var c in new Control[] { tile, name, host, copy, heading, big, sub, bar, error, graph, names, free }) c.Visible = set.Contains(c);
        }

        public void ShowOne(Download d, State s, string category, IList<double> samples)
        {
            var running = d.Status == "downloading";
            var failed = d.Status == "error";
            Mode("one", tile, name, host, copy, big, sub, bar, graph, error);
            graph.Visible = running;
            error.Visible = failed;
            tile.Category = category;
            tile.File = string.IsNullOrEmpty(d.File) ? Core.ShortName(d) : d.File; // Windows' icon for its type, as the list
            tile.Invalidate();
            name.Text = Core.ShortName(d);
            Uri u;
            host.Text = d.Url != null && Uri.TryCreate(d.Url, UriKind.Absolute, out u) ? u.Host : d.Url;
            bigErr = failed;
            big.Text = d.Status == "done" ? "Done" : failed ? "Failed" : Math.Floor(Core.Fraction(d, true) * 100) + "%";
            var t = Core.Total(d);
            sub.Text = d.Status == "done" ? Core.Size(t) : Core.Size(d.Got) + " of " + Core.Size(t == null ? (double?)null : Math.Round(t.Value));
            bar.Set(Core.Pieces(d), d.Status == "done" ? BarKind.Done : failed ? BarKind.Err : BarKind.Normal);
            if (failed && error.Box.Text != (d.Error ?? "")) error.Box.Text = d.Error ?? "";
            if (running) graph.Set(samples, Core.Size(d.Speed) + "/s");
            var file = d.File;
            var folder = !string.IsNullOrEmpty(file) ? Core.SplitPath(file)[0] : Core.FolderFor(s.Settings, category);
            var youtube = d.Kind == "video";
            var queue = s.Queues.FirstOrDefault(q => q.Id == d.Queue);
            var pairs = new List<string> { "Status", Core.StatusText(d),
                "Speed", running ? Core.Size(d.Speed) + "/s" : "—",
                "Time left", Core.Eta(d) == "" ? "—" : Core.Eta(d),
                "Connections", running ? d.Connections.ToString() : "—",
                "Resume", d.Resumable == true ? "Yes" : d.Resumable == false ? "No" : "—",
                "Added", Core.DateLabel(d.Added, DateTime.Now),
                "Last try", d.LastTry == 0 ? "—" : Core.DateLabel(d.LastTry, DateTime.Now) };
            if (queue != null) pairs.AddRange(new[] { "Queue", Core.QueueName(queue) });
            pairs.AddRange(new[] { youtube ? "Format" : "Type", youtube ? (d.Format ?? d.Quality ?? "—") : category });
            if (!string.IsNullOrEmpty(d.Description)) pairs.AddRange(new[] { "Description", d.Description });
            pairs.AddRange(new[] { "Saved to", folder });
            Facts(pairs.ToArray());
            if (!string.IsNullOrEmpty(d.Description)) tips.SetToolTip(facts[pairs.Count / 2 - 2], d.Description); // the row ends in an ellipsis
            var saved = facts[pairs.Count / 2 - 1];
            saved.PathValue = true;
            tips.SetToolTip(saved, string.IsNullOrEmpty(file) ? folder : file);
            if (!string.IsNullOrEmpty(file)) { link = saved; saved.Cursor = Cursors.Hand; }
            var primary = d.Status == "downloading" || d.Status == "queued" ? new object[] { "Pause", "pause" }
                : d.Status == "paused" || d.Status == "scheduled" ? new object[] { "Resume", "resume" }
                : failed ? new object[] { "Retry", "resume" }
                : d.Status == "done" ? new object[] { "Open", "open" } : null;
            var spec = new List<object[]>();
            if (primary != null) spec.Add(new object[] { primary[0], primary[1], ButtonKind.Primary, true, false });
            if (!string.IsNullOrEmpty(file)) spec.Add(new object[] { "Show in folder", "folder", ButtonKind.Secondary, true, false });
            spec.Add(new object[] { "Copy link", "copy", ButtonKind.Secondary, true, false });
            if (d.Status != "done") spec.Add(new object[] { "Progress window", "progress", ButtonKind.Secondary, true, false });
            spec.Add(new object[] { "Properties…", "properties", ButtonKind.Secondary, true, false }); // the address can change there
            spec.Add(new object[] { "Remove", "remove", ButtonKind.Secondary, true, true });
            Buttons(spec.ToArray());
            Relayout();
        }

        public void ShowSeveral(List<Download> sel)
        {
            Mode("several", big, sub, names);
            bigErr = false;
            big.Text = sel.Count + " downloads";
            sub.Text = Core.Size(sel.Sum(x => Core.Total(x) ?? 0));
            names.Text = string.Join("\n", sel.Take(5).Select(x => Core.ShortName(x)));
            Facts();
            Buttons(new object[] { "Resume", "resume", ButtonKind.Secondary, sel.Any(x => Core.CanResume(x)), false },
                new object[] { "Pause", "pause", ButtonKind.Secondary, sel.Any(x => Core.CanPause(x)), false },
                new object[] { "Remove", "remove", ButtonKind.Secondary, true, true });
            Relayout();
        }

        public void ShowOverview(List<Download> all, IList<double> totals, string freeText)
        {
            Mode("overview", heading, big, graph, free);
            bigErr = false;
            big.Text = Core.Size(totals.Count > 0 ? totals[totals.Count - 1] : 0) + "/s";
            graph.Set(totals, "");
            Facts("Active", all.Count(d => d.Status == "downloading" || d.Status == "queued" || d.Status == "paused").ToString(),
                "Finished", all.Count(d => d.Status == "done").ToString(),
                "Failed", all.Count(d => d.Status == "error").ToString());
            free.Text = freeText ?? "";
            Buttons();
            Relayout();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            Relayout();
        }

        void Place(Control c, int x, int y, int w, int h) { c.Bounds = new Rectangle(x, y + AutoScrollPosition.Y, w, h); }

        void Relayout()
        {
            if (laying || mode == null) return;
            laying = true;
            int p = Theme.Px(16), w = ClientSize.Width - 2 * p, y = p;
            if (mode == "overview")
            {
                Place(heading, p, y, w, heading.Height);
                y += heading.Height + Theme.Px(8);
            }
            if (mode == "one")
            {
                var nx = p + tile.Width + Theme.Px(12);
                var nw = w - tile.Width - Theme.Px(12);
                Place(tile, p, y, tile.Width, tile.Height);
                var nh = name.HeightFor(nw);
                Place(name, nx, y + Theme.Px(2), nw, nh);
                var hy = y + Theme.Px(2) + nh + Theme.Px(2);
                var hw = Math.Min(Theme.Measure(host.Text, host.Font).Width + Theme.Px(2), nw - copy.Width - Theme.Px(4));
                Place(host, nx, hy + (copy.Height - host.Height) / 2, hw, host.Height);
                Place(copy, nx + hw + Theme.Px(2), hy, copy.Width, copy.Height);
                y += Math.Max(tile.Height, hy + copy.Height - y) + Theme.Px(16);
            }
            var bw = Theme.Measure(big.Text, big.Font).Width + Theme.Px(2);
            Place(big, p, y, Math.Min(bw, w), big.Height);
            if (sub.Visible) Place(sub, p + bw + Theme.Px(16), y + big.Height - sub.Height - Theme.Px(3), Math.Max(0, w - bw - Theme.Px(16)), sub.Height);
            y += big.Height;
            if (mode == "one")
            {
                y += Theme.Px(8);
                Place(bar, p, y, w, Theme.Px(8));
                y += Theme.Px(8);
            }
            if (error.Visible)
            {
                y += Theme.Px(12);
                var h = error.HeightFor(w);
                Place(error, p, y, w, h);
                y += h;
            }
            if (graph.Visible)
            {
                y += Theme.Px(12);
                Place(graph, p, y, w, Theme.Px(56));
                y += Theme.Px(56);
            }
            if (names.Visible)
            {
                y += Theme.Px(12);
                var h = names.HeightFor(w);
                Place(names, p, y, w, h);
                y += h;
            }
            var anyFact = facts.Any(f => f.Visible);
            if (anyFact) y += Theme.Px(12);
            foreach (var f in facts.Where(f => f.Visible))
            {
                Place(f, p, y, w, f.Height);
                y += f.Height;
            }
            if (free.Visible)
            {
                y += Theme.Px(8);
                Place(free, p, y, w, free.Height);
                y += free.Height;
            }
            if (buttons.Count > 0)
            {
                y += Theme.Px(16);
                var gap = Theme.Px(8);
                var cw = (w - gap) / 2;
                for (var i = 0; i < buttons.Count; i++)
                {
                    Place(buttons[i], p + (i % 2) * (cw + gap), y + (i / 2) * (Theme.Px(32) + gap), cw, Theme.Px(32));
                }
                y += ((buttons.Count + 1) / 2) * (Theme.Px(32) + gap) - gap;
            }
            AutoScrollMinSize = new Size(0, y + p);
            laying = false;
        }
    }
}
