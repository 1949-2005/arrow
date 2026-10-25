// The Scheduler (spec "4 Scheduler", mockups/m3-scheduler.png): the queues at the left, the selected queue's settings
// and its unfinished files at the right. Every change is sent at once (POST /api/queues update), validated inline;
// polls (Fill) refresh it without overwriting a field being edited. Opened through Dialogs.Scheduler, one at a time.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Glide
{
    public class SchedulerDialog : Dialog
    {
        readonly Api api; // null in GLIDE_SHOTS
        readonly Column left = new Column();
        readonly Sidebar queueList = new Sidebar { Counts = false, AccessibleName = "Queues" };
        readonly FlatButton newQueue = new FlatButton("New queue", Glyph.Add, ButtonKind.Secondary), delete = new FlatButton("Delete", '\0', ButtonKind.Secondary) { Destructive = true };
        readonly Caption nameLabel = Label("Name"), activeLabel = Label("Download at the same time"), startLabel = Label("Start"), daysLabel = Label("On these days"),
            stopLabel = Label("Stop"), failsLabel = Label("If a file fails"), doneLabel = Label("When the queue is done");
        readonly TextField name = new TextField('\0', ""), active = new TextField('\0', ""), startAt = new TextField('\0', "HH:MM"), stopAt = new TextField('\0', "HH:MM"), retries = new TextField('\0', "");
        readonly Caption files = Hint("files"), times = Hint("times"), tryAgain = new Caption(Theme.Body, () => Theme.Text) { Text = "Try again" },
            daysHint = new Caption(Theme.Small, () => Theme.Faint) { Text = "None = every day" };
        readonly FlatRadio manual = new FlatRadio("By hand only"), at = new FlatRadio("At");
        readonly FlatCheck stopOn = new FlatCheck("At");
        readonly DayChip[] days = Core.DayNames.Select((n, i) => new DayChip(n, CultureInfo.InvariantCulture.DateTimeFormat.DayNames[(i + 1) % 7])).ToArray();
        readonly Picker whenDone = new Picker();
        static readonly string[] WhenDoneKeys = { "", "sleep", "shutdown" };
        readonly QueueFiles list = new QueueFiles();
        readonly FlatButton up = new FlatButton("Move up", Glyph.Up, ButtonKind.Secondary), down = new FlatButton("Move down", Glyph.Down, ButtonKind.Secondary),
            unqueue = new FlatButton("Remove from queue", '\0', ButtonKind.Secondary);
        readonly FlatButton startNow = new FlatButton("Start now", Glyph.Play, ButtonKind.Secondary), stop = new FlatButton("Stop", Glyph.Stop, ButtonKind.Secondary),
            close = new FlatButton("Close", '\0', ButtonKind.Primary);
        List<Queue> queues = new List<Queue>();
        List<Download> downloads = new List<Download>();
        string selected = Queue.Main, want, built; // want: a queue just added, selected once a poll brings it
        bool filling; // the window shows the engine's values: no change is sent

        // changes not sent yet, merged per queue; Drain sends them one at a time, so an older value never lands last
        readonly object gate = new object();
        readonly Dictionary<string, Dictionary<string, object>> pending = new Dictionary<string, Dictionary<string, object>>();
        bool sending;
        int sentAt = Environment.TickCount - 1001; // a difference of TickCounts survives its wrap-around

        static Caption Hint(string text) { return new Caption(Theme.Body, () => Theme.Muted) { Text = text }; }

        public SchedulerDialog(Api api) : base("Scheduler", 880)
        {
            this.api = api;
            queueList.TabIndex = 0;
            newQueue.TabIndex = 1;
            delete.TabIndex = 2;
            left.Controls.AddRange(new Control[] { queueList, newQueue, delete });
            Add(left);
            name.Box.MaxLength = 40;
            name.Box.AccessibleName = "Name";
            active.Box.AccessibleName = "Download at the same time, 1 to 20 files";
            startAt.Box.AccessibleName = "Start at, HH:MM";
            stopAt.Box.AccessibleName = "Stop at, HH:MM";
            retries.Box.AccessibleName = "If a file fails, try again 0 to 10 times";
            manual.AccessibleName = "Start by hand only";
            at.AccessibleName = "Start at a time";
            stopOn.AccessibleName = "Stop at a time";
            whenDone.AccessibleName = "When the queue is done";
            whenDone.Height = Theme.Px(32);
            whenDone.Items.AddRange(new[] { "Nothing", "Sleep", "Shut down" });
            foreach (var c in new Control[] { nameLabel, name, activeLabel, active, files, startLabel, manual, at, startAt, daysLabel }) Add(c);
            foreach (var d in days) Add(d);
            foreach (var c in new Control[] { daysHint, stopLabel, stopOn, stopAt, failsLabel, tryAgain, retries, times, doneLabel, whenDone, list, up, down, unqueue }) Add(c);
            Buttons(new[] { startNow }, new[] { stop, close });
            AcceptButton = close;
            CancelButton = close;
            ActiveControl = queueList;

            queueList.SelectedChanged += () => { selected = queueList.Selected; Form(true); };
            newQueue.Click += (o, e) => NewQueue();
            delete.Click += (o, e) => DeleteQueue();
            Typed(name, () => Edited(name, "name"));
            Typed(active, () => Edited(active, "maxActive"));
            Typed(retries, () => Edited(retries, "retries"));
            // typing a time picks its "At"
            Typed(startAt, () => { if (!at.Checked) at.Checked = true; else StartChanged(); });
            at.CheckedChanged += (o, e) => StartChanged();
            Typed(stopAt, () => { if (!stopOn.Checked) stopOn.Checked = true; else StopChanged(); });
            stopOn.CheckedChanged += (o, e) => StopChanged();
            foreach (var d in days) d.CheckedChanged += (o, e) => { if (!filling) Send("days", Core.QueueDays(days.Select(x => x.Checked).ToList())); };
            whenDone.Picked += () => Send("whenDone", WhenDoneKeys[whenDone.SelectedIndex]);
            list.SelectionChanged += Enable;
            up.Click += (o, e) => MoveFile("up");
            down.Click += (o, e) => MoveFile("down");
            unqueue.Click += (o, e) => MoveFile("queue");
            startNow.Click += (o, e) => { var id = selected; Call(a => a.Queues("start", id)); };
            stop.Click += (o, e) => { var id = selected; Call(a => a.Queues("stop", id)); };
            close.Click += (o, e) => Close();
            Relayout();
        }

        Queue Current { get { return queues.FirstOrDefault(q => q.Id == selected); } }

        // ---- the engine's state

        public void Fill(State s)
        {
            queues = s.Queues ?? new List<Queue>();
            downloads = s.Downloads ?? new List<Download>();
            var key = string.Join("|", queues.Select(q => q.Id + ":" + q.Name));
            if (key != built)
            {
                built = key;
                queueList.Clear();
                queueList.Heading("Queues");
                foreach (var q in queues) queueList.Add(q.Id, Core.QueueName(q), Glyph.List, null);
            }
            foreach (var it in queueList.Items) it.Dot = queues.Any(q => q.Running && q.Id == it.Key);
            queueList.Invalidate();
            // a queue just added, else the shown one, else (removed meanwhile, or the first fill) the main queue
            var target = want != null && queues.Any(q => q.Id == want) ? want : Current != null ? selected : Queue.Main;
            if (target == want) want = null;
            if (queueList.Selected != target) Select(target);
            else Form(false);
        }

        // Shows a queue (the sidebar's queue menu opens the Scheduler with it); Sidebar's SelectedChanged fills the form
        public void Select(string id) { queueList.Selected = id; }

        public void SelectFile(string id) { list.Select(id); }

        // all: another queue is shown, every field takes its values; else fields being edited, or not valid yet, stay
        void Form(bool all)
        {
            var q = Current;
            if (q == null) return;
            bool busy;
            lock (gate) busy = sending || pending.Count > 0;
            // a poll answered just before a change went through still carries the old values
            if (all || !busy && unchecked(Environment.TickCount - sentAt) > 1000)
            {
                filling = true;
                try
                {
                    name.Box.ReadOnly = q.Id == Queue.Main; // the engine keeps the main queue's name
                    Set(name, Core.QueueName(q), all);
                    Set(active, q.MaxActive.ToString(CultureInfo.InvariantCulture), all);
                    Set(retries, q.Retries.ToString(CultureInfo.InvariantCulture), all);
                    // "By hand only" keeps the time typed last, for going back to "At"
                    if (all || !string.IsNullOrEmpty(q.StartAt)) Set(startAt, q.StartAt ?? "", all);
                    if (!startAt.Invalid) { if (string.IsNullOrEmpty(q.StartAt)) manual.Checked = true; else at.Checked = true; } // an "At" waiting for a valid time stays
                    var on = Core.DayToggles(q.Days);
                    for (var i = 0; i < 7; i++) days[i].Checked = on[i];
                    if (all || !string.IsNullOrEmpty(q.StopAt)) Set(stopAt, q.StopAt ?? "", all);
                    if (!stopAt.Invalid) stopOn.Checked = !string.IsNullOrEmpty(q.StopAt);
                    whenDone.SelectedIndex = Math.Max(0, Array.IndexOf(WhenDoneKeys, q.WhenDone));
                }
                finally { filling = false; }
            }
            list.SetItems(downloads.Where(d => d.Queue == q.Id && d.Status != "done").ToList(), all);
            Enable();
        }

        void Set(TextField f, string value, bool all)
        {
            if (all) f.Invalid = false;
            else if (f.Box.Focused || f.Invalid) return;
            if (f.Text != value) f.Text = value;
        }

        void Enable()
        {
            var q = Current;
            var i = list.Index;
            delete.Enabled = q != null && q.Id != Queue.Main;
            startNow.Enabled = q != null && !q.Running;
            stop.Enabled = q != null && q.Running;
            up.Enabled = i > 0;
            down.Enabled = i >= 0 && i < list.Items.Count - 1;
            unqueue.Enabled = i >= 0;
        }

        // ---- changes

        // The user's edits only: the box has the focus, and the window is not showing the engine's values
        void Typed(TextField f, Action edited) { f.Box.TextChanged += (o, e) => { if (!filling && f.Box.Focused) edited(); }; }

        void Edited(TextField f, string field)
        {
            if (filling) return;
            var v = Core.QueueValue(field, f.Text, selected, queues);
            f.Invalid = v == null;
            if (v != null) Send(field, v);
        }

        void StartChanged()
        {
            if (filling) return;
            if (at.Checked) Edited(startAt, "startAt");
            else { startAt.Invalid = false; Send("startAt", ""); }
        }

        void StopChanged()
        {
            if (filling) return;
            if (stopOn.Checked) Edited(stopAt, "stopAt");
            else { stopAt.Invalid = false; Send("stopAt", ""); }
        }

        void Send(string field, object value)
        {
            if (api == null) return;
            bool start;
            lock (gate)
            {
                Dictionary<string, object> f;
                if (!pending.TryGetValue(selected, out f)) pending[selected] = f = new Dictionary<string, object>();
                f[field] = value;
                start = !sending;
                sending = true;
            }
            if (start) Dialogs.Call(this, Drain, err => { sentAt = Environment.TickCount; if (err != null) Dialogs.Error(this, err); });
        }

        // On a pool thread; it goes on after the window closes, so nothing typed is lost
        void Drain()
        {
            while (true)
            {
                string id;
                Dictionary<string, object> f;
                lock (gate)
                {
                    if (pending.Count == 0) { sending = false; return; }
                    var first = pending.First();
                    id = first.Key;
                    f = first.Value;
                    pending.Remove(id);
                }
                try { api.Queues("update", id, f); }
                catch { lock (gate) sending = false; throw; } // what is left goes with the next change
            }
        }

        void Call(Action<Api> work)
        {
            if (api == null) return;
            var a = api;
            Dialogs.Call(this, () => work(a), err => { if (err != null) Dialogs.Error(this, err); });
        }

        // up / down in the queue's order (the engine skips finished files), or out of every queue
        void MoveFile(string op)
        {
            var id = list.Selected;
            if (id == null) return;
            if (op == "queue") Call(a => a.Action(id, "queue", "queue", null));
            else Call(a => a.Action(id, op));
        }

        void NewQueue()
        {
            var n = Dialogs.AskText(this, "New queue", "Name of the new queue", "", 40, "Add");
            if (n == null || api == null) return;
            var a = api;
            object answer = null;
            Dialogs.Call(this, () => answer = a.Queues("add", null, new Dictionary<string, object> { { "name", n } }), err =>
            {
                if (err != null) { Dialogs.Error(this, err); return; }
                var o = answer as IDictionary<string, object>;
                want = o == null ? null : o.Str("id");
            });
        }

        void DeleteQueue()
        {
            var q = Current;
            if (q == null || q.Id == Queue.Main) return;
            if (!Dialogs.Confirm(this, "Delete queue", "Delete the queue \"" + q.Name + "\"? Its files move to the main queue.", "Delete", true)) return;
            var id = q.Id;
            lock (gate) pending.Remove(id);
            Call(a => a.Queues("remove", id));
        }

        // ---- layout (label column 196, wider when its longest label needs it; rows 32 every 44)

        void Relayout()
        {
            int p = Pad, lx = Theme.Px(230) + p, right = ClientSize.Width - p, row = Theme.Px(32), gap = Theme.Px(8);
            var y = p;
            // c at x, centered on the row; returns where the next one starts
            Func<Control, int, int, int> place = (c, cx, w) =>
            {
                var h = c is TextField || c is Picker ? row : c.Height;
                c.Bounds = new Rectangle(cx, y + (row - h) / 2, w, h);
                return cx + w + gap;
            };
            Func<Caption, int> width = c => Theme.Measure(c.Text, c.Font).Width + 1;
            var labels = new[] { nameLabel, activeLabel, startLabel, daysLabel, stopLabel, failsLabel, doneLabel };
            var vx = lx + Math.Max(Theme.Px(196), labels.Max(c => Theme.Measure(c.Text, c.Font).Width) + Theme.Px(12));
            Action<Caption> label = c => { c.Bounds = new Rectangle(lx, y, vx - lx - gap, row); };

            label(nameLabel);
            place(name, vx, Theme.Px(260));
            y += Theme.Px(44);
            label(activeLabel);
            place(files, place(active, vx, Theme.Px(64)), width(files));
            y += Theme.Px(44);
            label(startLabel);
            var x = place(manual, vx, manual.PreferredWidth) + Theme.Px(8);
            place(startAt, place(at, x, at.PreferredWidth), Theme.Px(76));
            y += Theme.Px(44);
            label(daysLabel);
            x = vx;
            foreach (var d in days) x = place(d, x, d.Width) - gap + Theme.Px(6);
            place(daysHint, x + Theme.Px(4), Math.Max(0, right - x - Theme.Px(4)));
            y += Theme.Px(44);
            label(stopLabel);
            place(stopAt, place(stopOn, vx, stopOn.PreferredWidth), Theme.Px(76));
            y += Theme.Px(44);
            label(failsLabel);
            place(times, place(retries, place(tryAgain, vx, width(tryAgain)), Theme.Px(56)), width(times));
            y += Theme.Px(44);
            label(doneLabel);
            place(whenDone, vx, Theme.Px(200));
            y += row + Theme.Px(12);
            list.Bounds = new Rectangle(lx, y, right - lx, Theme.Px(32 + 3 * 36));
            y = list.Bottom + Theme.Px(12);
            x = lx;
            foreach (var b in new[] { up, down, unqueue }) { b.Bounds = new Rectangle(x, y, b.PreferredWidth, row); x = b.Right + gap; }
            BodyHeight(y + row + p);
            left.Bounds = new Rectangle(0, 0, Theme.Px(230), Body.Height);
            queueList.Bounds = new Rectangle(0, 0, left.Width, y - Theme.Px(8));
            newQueue.Bounds = new Rectangle(Theme.Px(12), y, newQueue.PreferredWidth, row);
            delete.Bounds = new Rectangle(newQueue.Right + gap, y, Math.Max(Theme.Px(66), delete.PreferredWidth), row);
        }

        // The left column: the sidebar token (again after a theme switch), a hairline at its right
        class Column : Panel, IThemed
        {
            public Column() { DoubleBuffered = true; ResizeRedraw = true; Restyle(); }
            public void Restyle() { BackColor = Theme.Sidebar; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); Theme.VLine(e.Graphics, Width - 1, 0, Height); }
        }

        // A day toggle: the native CheckBox (Tab, Space, its checked state for screen readers) drawn as a chip, soft when on
        class DayChip : CheckBox
        {
            public DayChip(string text, string fullName)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Text = text;
                AccessibleName = fullName;
                Font = Theme.SmallBold;
                AutoSize = false;
                Size = new Size(Math.Max(Theme.Px(34), Theme.Measure(text, Font).Width + Theme.Px(12)), Theme.Px(28));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                var r = ClientRectangle;
                using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Inspector)) g.FillRectangle(b, r);
                // a chosen day is filled like a pressed toggle: a soft fill was too faint to tell apart in light mode
                if (Checked) Theme.Fill(g, r, Theme.Px(6), Theme.Accent);
                else Theme.Border(g, r, Theme.Px(6), Theme.Line, 1);
                Theme.DrawText(g, Text, Font, r, !Enabled ? Theme.Faint : Checked ? Theme.OnAccent : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                // the accent ring would vanish on the accent fill: an inner ring in the text color instead
                if (Focused && ShowFocusCues)
                {
                    if (Checked) Theme.Border(g, Rectangle.Inflate(r, -Theme.Px(3), -Theme.Px(3)), Theme.Px(4), Theme.OnAccent, Theme.Px(2));
                    else Theme.FocusRing(g, r, Theme.Px(6));
                }
            }
        }

        // The queue's unfinished files in its order: a bordered table (header 32, rows 36 or as tall as the wrapped name,
        // 1 px dividers), one selected row, Up/Down/Home/End and the wheel
        class QueueFiles : Painted
        {
            public List<Download> Items = new List<Download>();
            public string Selected;
            public event Action SelectionChanged;
            int scroll;
            int[] tops = { 0 }, blocks = new int[0]; // each row's top under the header (Core.RowTops), its name's height

            public QueueFiles()
            {
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
                AccessibleName = "Files in this queue";
            }

            static int RowH { get { return Theme.Px(36); } }
            static int HeadH { get { return Theme.Px(32); } }
            static int SizeW { get { return Theme.Px(80); } }
            static int StatusW { get { return Theme.Px(132); } }
            int NameWidth { get { return Width - StatusW - SizeW - 2 * Theme.Px(12); } }

            // the Size and Status cells stay on the name's first line
            void Measure()
            {
                blocks = Items.Select(d => Theme.NameHeight(Core.ShortName(d), Theme.Body, NameWidth, RowH)).ToArray();
                tops = Core.RowTops(blocks.Select(b => Math.Max(RowH, b)).ToList());
            }
            public int Index { get { return Items.FindIndex(d => d.Id == Selected); } }

            // another queue: nothing selected
            public void SetItems(List<Download> l, bool reset)
            {
                Items = l;
                Measure();
                if (reset) scroll = 0;
                if (reset || Selected != null && Index < 0) Select(null);
                Scroll(scroll);
                Invalidate();
            }

            public void Select(string id)
            {
                if (Selected == id) return;
                Selected = id;
                var i = Index;
                if (i >= 0)
                {
                    var r = Row(i);
                    if (r.Top < HeadH) Scroll(scroll - (HeadH - r.Top));
                    else if (r.Bottom > Height) Scroll(scroll + r.Bottom - Height + 1);
                }
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, Math.Max(0, i));
                var h = SelectionChanged;
                if (h != null) h();
            }

            void Scroll(int y)
            {
                var max = Math.Max(0, tops[tops.Length - 1] - (Height - HeadH));
                scroll = Math.Max(0, Math.Min(max, y));
                Invalidate();
            }

            Rectangle Row(int i) { return new Rectangle(1, HeadH + tops[i] - scroll, Width - 2, tops[i + 1] - tops[i]); }

            static string StatusText(Download d) { return d.Status == "scheduled" ? "Waiting" : Core.ListStatus(d, null); }
            static string SizeText(Download d) { var t = Core.Total(d); return t == null ? "" : Core.Size(t); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                var i = Core.RowAt(tops, e.Y - HeadH + scroll);
                if (e.Y >= HeadH && i >= 0) Select(Items[i].Id);
                base.OnMouseDown(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e) { Scroll(scroll - e.Delta * RowH / 120); base.OnMouseWheel(e); }
            protected override void OnResize(EventArgs e) { base.OnResize(e); Measure(); Scroll(scroll); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End || base.IsInputKey(k); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                var i = Index;
                var j = e.KeyCode == Keys.Up ? i - 1 : e.KeyCode == Keys.Down ? i + 1 : e.KeyCode == Keys.Home ? 0 : e.KeyCode == Keys.End ? Items.Count - 1 : -2;
                if (j != -2 && Items.Count > 0)
                {
                    Select(Items[Math.Max(0, Math.Min(Items.Count - 1, j))].Id);
                    e.Handled = true;
                }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                int pad = Theme.Px(12), sizeW = SizeW, statusW = StatusW, w = Width;
                var outline = new Rectangle(0, 0, w, Height);
                int sizeX = w - statusW - sizeW, statusX = w - statusW;
                var head = new Rectangle(0, 0, w, HeadH - 1);
                Theme.DrawText(g, "Files in this queue (in order)", Theme.Small, new Rectangle(pad, 0, sizeX - 2 * pad, head.Height), Theme.Muted);
                Theme.DrawText(g, "Size", Theme.Small, new Rectangle(sizeX, 0, sizeW - pad, head.Height), Theme.Muted);
                Theme.DrawText(g, "Status", Theme.Small, new Rectangle(statusX, 0, statusW - pad, head.Height), Theme.Muted);
                Theme.HLine(g, 1, w - 1, HeadH - 1);
                using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, w - 1, Height - 1), Theme.Px(8)))
                {
                    g.SetClip(path);
                    g.IntersectClip(new Rectangle(0, HeadH, w, Height - HeadH));
                    if (Items.Count == 0)
                        Theme.DrawText(g, "No files wait in this queue.", Theme.Body, new Rectangle(0, HeadH, w, Height - HeadH), Theme.Faint,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                    for (var i = 0; i < Items.Count; i++)
                    {
                        var r = Row(i);
                        if (r.Bottom < HeadH || r.Top > Height) continue;
                        var d = Items[i];
                        var sel = d.Id == Selected;
                        if (sel) using (var b = new SolidBrush(Theme.Sel)) g.FillRectangle(b, r);
                        var fore = sel ? Theme.OnSel : Theme.Text;
                        var line = RowH;
                        Theme.DrawName(g, Core.ShortName(d), Theme.Body, new Rectangle(pad, r.Y, sizeX - 2 * pad, line), blocks[i], fore);
                        Theme.DrawText(g, SizeText(d), Theme.Body, new Rectangle(sizeX, r.Y, sizeW - pad, line), fore);
                        Theme.DrawText(g, StatusText(d), Theme.Body, new Rectangle(statusX, r.Y, statusW - pad, line), sel ? fore : d.Status == "error" ? Theme.Err : fore);
                        if (i < Items.Count - 1) Theme.HLine(g, 1, w - 1, r.Bottom - 1);
                        if (sel && Focused && ShowFocusCues)
                            using (var p = new Pen(Theme.OnSel, Theme.Px(2))) g.DrawRectangle(p, r.X + Theme.Px(2), r.Y + Theme.Px(2), r.Width - Theme.Px(4), r.Height - Theme.Px(4));
                    }
                    g.ResetClip();
                }
                Theme.Border(g, outline, Theme.Px(8), Theme.Line, 1);
                if (Focused && ShowFocusCues && Index < 0) Theme.FocusRing(g, outline, Theme.Px(8));
            }

            protected override AccessibleObject CreateAccessibilityInstance()
            {
                var me = this;
                ListAccessible l = null;
                l = new ListAccessible(this, () => Items.Count, i =>
                {
                    var d = Items[i];
                    return new ItemAccessible(l, () => string.Join(", ", new[] { Core.ShortName(d), SizeText(d), StatusText(d) }.Where(s => s != "")), () => d.Id == Selected,
                        () => Focused && d.Id == Selected, () => me.RectangleToScreen(Items.IndexOf(d) >= 0 ? Row(Items.IndexOf(d)) : Rectangle.Empty), () => Select(d.Id));
                });
                return l;
            }
        }
    }
}
