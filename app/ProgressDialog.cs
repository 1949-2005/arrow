// The progress window (docs/NATIVE_UI.md "Dialogs", the behavior of the old progress.html), styled like the inspector:
// running (percent, segments, speed graph, facts, Pause/Resume and Cancel), done (Open folder, Close, Open) or removed.
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

        readonly Tile tile = new Tile();
        readonly Caption title = new Caption(Theme.Bold, () => Theme.Text);
        readonly Caption name = new Caption(Theme.Body, () => Theme.Muted);
        readonly Caption big = new Caption(Theme.Big, () => Theme.Text);
        readonly Caption sub = new Caption(Theme.Body, () => Theme.Muted);
        readonly SegmentBar bar = new SegmentBar();
        readonly Sparkline graph = new Sparkline { AccessibleName = "Speed, last 60 seconds" };
        readonly List<FactRow> facts = new List<FactRow>();
        readonly FlatButton toggle = new FlatButton("Pause", '\0', ButtonKind.Secondary);
        readonly FlatButton cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary);
        readonly FlatButton folder = new FlatButton("Open folder", '\0', ButtonKind.Secondary);
        readonly FlatButton close = new FlatButton("Close", '\0', ButtonKind.Secondary);
        readonly FlatButton open = new FlatButton("Open", '\0', ButtonKind.Primary);
        Download d;
        string mode;
        bool statusErr;

        public ProgressDialog(Api api, string id) : base("Progress", 460)
        {
            this.api = api;
            Id = id;
            ShowInTaskbar = !Offscreen;
            for (var i = 0; i < 7; i++) facts.Add(new FactRow());
            facts[1].ValueColor = () => statusErr ? Theme.Err : Theme.Text;
            foreach (var c in new Control[] { tile, title, name, big, sub, bar, graph }) Add(c);
            foreach (var f in facts) Add(f);
            toggle.Click += (s, e) => { if (d != null) Act(Core.CanPause(d) ? "pause" : "resume", null); };
            cancel.Click += (s, e) =>
            {
                if (d != null && Dialogs.Confirm(this, "Cancel download", "Cancel this download? The partial file is deleted.", "Cancel download", true))
                    Act("remove", Close);
            };
            folder.Click += (s, e) => Act("folder", null);
            open.Click += (s, e) => Act("open", null);
            close.Click += (s, e) => Close();
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
            Text = Math.Floor(fraction * 100) + "% · " + short_;
            tile.Category = Core.Category(d, categories);
            tile.Complete = done;
            tile.Invalidate();
            title.Text = Core.Heading(d);
            name.Text = short_;
            Tips.SetToolTip(name, string.IsNullOrEmpty(d.File) ? short_ : d.File);
            bar.Set(Core.Pieces(d), done ? BarKind.Done : d.Status == "error" ? BarKind.Err : BarKind.Normal);
            if (done)
            {
                Facts("Saved to", d.File ?? "", "File size", Core.Size(d.Size, "Unknown"), "URL", d.Url ?? "");
                facts[0].PathValue = true;
                Tips.SetToolTip(facts[0], d.File);
                Tips.SetToolTip(facts[2], d.Url);
                return;
            }
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
            Invalidate(true);
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
            tile.Visible = name.Visible = bar.Visible = !gone;
            big.Visible = sub.Visible = graph.Visible = run;
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
                Buttons(new[] { folder }, new[] { close, open });
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
            int p = Pad, w = ClientSize.Width - 2 * p, y = p;
            if (mode == "gone")
            {
                title.Bounds = new Rectangle(p, y, w, title.Height);
                BodyHeight(y + title.Height + p);
                return;
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
                sub.Bounds = new Rectangle(p + bw + Theme.Px(8), y + big.Height - sub.Height - Theme.Px(3), w - bw - Theme.Px(8), sub.Height);
                y += big.Height + Theme.Px(8);
            }
            bar.Bounds = new Rectangle(p, y, w, Theme.Px(8));
            y += Theme.Px(8);
            if (mode == "run")
            {
                y += Theme.Px(12);
                graph.Bounds = new Rectangle(p, y, w, Theme.Px(56));
                y += Theme.Px(56);
            }
            y += Theme.Px(12);
            var count = mode == "run" ? 7 : 3;
            for (var i = 0; i < count; i++)
            {
                facts[i].Bounds = new Rectangle(p, y, w, facts[i].Height);
                y += facts[i].Height;
            }
            BodyHeight(y + p);
        }
    }
}
