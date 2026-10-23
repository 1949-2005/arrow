// Options (spec "8 Options", mockups/m5-options.png right): pages General, Downloads, Categories, Queues in a left
// navigation. General and Downloads are validated inline and saved together by Save (POST /api/settings with only
// what changed); Categories saves through the category editor and Remove at once; Queues opens the Scheduler.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Arrow
{
    public class OptionsDialog : Dialog
    {
        public const string General = "general", Downloads = "downloads", Categories = "categories", Queues = "queues";
        readonly Api api; // null in ARROW_SHOTS
        Settings settings; // the engine's, again after each category change
        readonly Dictionary<string, object> before; // General and Downloads as the dialog opened
        readonly int limitKB; // kept while the limit is off
        readonly Sidebar nav = new Sidebar { Counts = false, ItemH = 32, AccessibleName = "Options pages" };
        readonly Dictionary<Control, string> pageOf = new Dictionary<Control, string>();

        // General
        readonly FlatCheck startup = new FlatCheck("Start with Windows"), clipboard = new FlatCheck("Offer links I copy"), showComplete = new FlatCheck("Show the Download complete window");
        readonly Caption dupLabel = Label("When a link is already in the list");
        readonly Picker duplicates = new Picker();
        // Downloads
        readonly Caption dirLabel = Label("Save to folder"), connLabel = Label("Connections per download"), activeLabel = Label("Downloads at the same time");
        readonly TextField dir = new TextField('\0', ""), conn = new TextField('\0', ""), active = new TextField('\0', ""), limit = new TextField('\0', "");
        readonly FlatButton browse = new FlatButton("Browse…", '\0', ButtonKind.Secondary);
        readonly FlatCheck sort = new FlatCheck("Sort into category folders"), limitOn = new FlatCheck("Limit all downloads to");
        readonly Caption connHint = Hint("1–32"), activeHint = Hint("1–20"), kbs = Hint("KB/s");
        // Categories
        readonly CategoryTable table = new CategoryTable();
        readonly FlatButton add = new FlatButton("Add", Glyph.Add, ButtonKind.Secondary), edit = new FlatButton("Edit…", '\0', ButtonKind.Secondary),
            remove = new FlatButton("Remove", '\0', ButtonKind.Secondary) { Destructive = true };
        readonly Caption note = new Caption(Theme.Small, () => Theme.Muted)
        {
            MaxLines = 4,
            Text = "The five built-in categories keep their names and icons; you can change their file types and folders. Files that match no category go to Other.",
        };
        // Queues
        readonly Caption queuesText = new Caption(Theme.Body, () => Theme.Text) { MaxLines = 3, Text = "Queues and their schedules are in the Scheduler." };
        readonly FlatButton openScheduler = new FlatButton("Open Scheduler", Glyph.Clock, ButtonKind.Secondary);

        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 6, Visible = false };
        readonly FlatButton save = new FlatButton("Save", '\0', ButtonKind.Primary);
        readonly FlatButton cancel = Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        bool busy;

        // Open Scheduler saved and closed Options; Dialogs.Options opens the Scheduler then
        public bool SchedulerWanted;

        static Caption Hint(string text) { return new Caption(Theme.Body, () => Theme.Muted) { Text = text }; }

        public OptionsDialog(Api api, Settings s, string page) : base("Options", 700)
        {
            this.api = api;
            settings = s;
            limitKB = s.SpeedLimitKB;
            startup.Checked = s.Startup;
            clipboard.Checked = s.Clipboard;
            showComplete.Checked = s.ShowComplete;
            duplicates.Items.AddRange(new[] { "Ask me", "Download it again with a new name", "Replace the existing file", "Resume the existing download" });
            duplicates.SelectedIndex = Core.DuplicateIndex(s.Duplicates);
            duplicates.Height = Theme.Px(32);
            duplicates.AccessibleName = "When a link is already in the list";
            dir.Text = s.Dir;
            sort.Checked = s.Categories;
            conn.Text = s.Connections.ToString();
            active.Text = s.MaxActive.ToString();
            limitOn.Checked = s.SpeedLimitOn && s.SpeedLimitKB > 0;
            limit.Text = (s.SpeedLimitKB > 0 ? s.SpeedLimitKB : 500).ToString(); // what checking the box limits to when none was set
            before = Values();
            dir.Box.AccessibleName = "Save to folder";
            conn.Box.AccessibleName = "Connections per download, 1 to 32";
            active.Box.AccessibleName = "Downloads at the same time, 1 to 20";
            limit.Box.AccessibleName = "Limit all downloads to, in KB/s, 1 to 10 000 000";
            foreach (var f in new[] { dir, conn, active }) f.Box.TextChanged += (o, e) => Check(false);
            // typing a limit turns it on
            limit.Box.TextChanged += (o, e) => { if (limit.Box.Focused && !limitOn.Checked) limitOn.Checked = true; else Check(false); };
            limitOn.CheckedChanged += (o, e) => Check(false);

            nav.TabIndex = 0;
            Add(nav);
            nav.Add(General, "General", '\0', null);
            nav.Add(Downloads, "Downloads", '\0', null);
            nav.Add(Categories, "Categories", '\0', null);
            nav.Add(Queues, "Queues", '\0', null);
            Page(General, startup, clipboard, showComplete, dupLabel, duplicates);
            Page(Downloads, dirLabel, dir, browse, sort, connLabel, conn, connHint, activeLabel, active, activeHint, limitOn, limit, kbs);
            Page(Categories, table, add, edit, remove, note);
            Page(Queues, queuesText, openScheduler);
            Add(error);
            Buttons(new FlatButton[0], new[] { cancel, save });
            AcceptButton = save;
            CancelButton = cancel;
            ActiveControl = nav;

            browse.Click += (o, e) => Browse();
            table.Fill(s);
            table.SelectionChanged += Enable;
            table.DoubleClick += (o, e) => { if (table.Selected != null) EditCategory(table.Selected); };
            add.Click += (o, e) => EditCategory(null);
            edit.Click += (o, e) => { if (table.Selected != null) EditCategory(table.Selected); };
            remove.Click += (o, e) => RemoveCategory();
            openScheduler.Click += (o, e) => { SchedulerWanted = true; Save(); };
            save.Click += (o, e) => Save();
            nav.SelectedChanged += () => ShowPage(nav.Selected);
            nav.Selected = nav.Items.Any(i => i.Key == page) ? page : General;
            Enable();
        }

        void Page(string page, params Control[] cs) { foreach (var c in cs) { pageOf[c] = page; Add(c); } }

        void ShowPage(string page)
        {
            foreach (var kv in pageOf) kv.Key.Visible = kv.Value == page;
            Relayout();
        }

        // General and Downloads as POST /api/settings takes them; the limit's number only counts while it is on
        Dictionary<string, object> Values()
        {
            var kb = Core.WholeNumber(limit.Text, 1, 10000000);
            return new Dictionary<string, object>
            {
                { "startup", startup.Checked },
                { "clipboard", clipboard.Checked },
                { "showComplete", showComplete.Checked },
                { "duplicates", Core.DuplicateSettings[Math.Max(0, duplicates.SelectedIndex)] },
                { "dir", dir.Text.Trim() },
                { "categories", sort.Checked },
                { "connections", Core.WholeNumber(conn.Text, 1, 32) ?? 0 },
                { "maxActive", Core.WholeNumber(active.Text, 1, 20) ?? 0 },
                { "speedLimitOn", limitOn.Checked },
                { "speedLimitKB", limitOn.Checked && kb != null ? kb.Value : limitKB },
            };
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
            check(limit, !limitOn.Checked || Core.WholeNumber(limit.Text, 1, 10000000) != null, "The speed limit must be a whole number of KB/s from 1 to 10 000 000.");
            if (all || problems.Count == 0 || error.Text != "") ShowError(problems.FirstOrDefault());
            return problems.Count == 0;
        }

        void ShowError(string text)
        {
            if (error.Text == (text ?? "")) return;
            error.Text = text ?? "";
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
            if (busy) return;
            if (!Check(true))
            {
                SchedulerWanted = false;
                nav.Selected = Downloads; // every field that can be wrong is there
                return;
            }
            var body = Core.Changed(before, Values());
            if (api == null) return;
            if (body.Count == 0) { DialogResult = DialogResult.OK; return; }
            var a = api;
            Lock(true);
            Dialogs.Call(this, () => a.Post("/api/settings", body), err =>
            {
                Lock(false);
                if (err == null) DialogResult = DialogResult.OK;
                else { SchedulerWanted = false; ShowError(err); }
            });
        }

        void Lock(bool on)
        {
            busy = on;
            foreach (var c in new Control[] { startup, clipboard, showComplete, duplicates, dir, browse, sort, conn, active, limitOn, limit, add, edit, remove, openScheduler, save }) c.Enabled = !on;
            if (!on) Enable();
        }

        // ---- Categories

        void Enable()
        {
            if (busy) return;
            edit.Enabled = table.Selected != null;
            remove.Enabled = table.Selected != null && !Core.BuiltInCategories.Contains(table.Selected);
        }

        // name null: a new one. The editor saves at once; the table shows the engine's settings it answered with.
        void EditCategory(string name)
        {
            string saved;
            var s = Dialogs.EditCategory(api, this, settings, name, out saved);
            if (s == null) return;
            Refill(s);
            table.Select(saved);
            table.Focus();
        }

        void RemoveCategory()
        {
            var name = table.Selected;
            if (busy || name == null || Core.BuiltInCategories.Contains(name) || api == null) return;
            if (!Dialogs.Confirm(this, "Remove category", "Remove the category " + name + "? Its downloads go to Other; no file is moved.", "Remove", true)) return;
            var a = api;
            var body = Core.TypesWithout(settings, name);
            object answer = null;
            Lock(true);
            Dialogs.Call(this, () => answer = a.Post("/api/settings", body), err =>
            {
                Lock(false);
                if (err != null) { ShowError(err); return; }
                Refill(Settings.From(answer as IDictionary<string, object>));
                table.Focus();
            });
        }

        public void SelectCategory(string name) { table.Select(name); } // ARROW_SHOTS

        void Refill(Settings s)
        {
            settings = s;
            table.Fill(s);
            Enable();
        }

        // ---- layout: the navigation (170) at the left, the page at its right; every page as high as the tallest (Categories)

        void Relayout()
        {
            int p = Pad, nw = Theme.Px(170), x = nw + p, w = ClientSize.Width - p - x, row = Theme.Px(32), gap = Theme.Px(8);
            var y = p;
            var page = nav.Selected;
            if (page == General)
            {
                foreach (var c in new[] { startup, clipboard, showComplete })
                {
                    c.Location = new Point(x, y);
                    y += c.Height + Theme.Px(8);
                }
                y += Theme.Px(8);
                dupLabel.Bounds = new Rectangle(x, y, w, dupLabel.Height);
                y += dupLabel.Height + Theme.Px(6);
                duplicates.Bounds = new Rectangle(x, y, Math.Min(w, Theme.Px(320)), row);
                y += row;
            }
            else if (page == Downloads)
            {
                dirLabel.Bounds = new Rectangle(x, y, w, dirLabel.Height);
                y += dirLabel.Height + Theme.Px(6);
                browse.Size = new Size(Math.Max(Theme.Px(88), browse.PreferredWidth), row);
                browse.Location = new Point(x + w - browse.Width, y);
                dir.Bounds = new Rectangle(x, y, w - browse.Width - gap, row);
                y += row + gap;
                sort.Location = new Point(x, y);
                y += sort.Height + Theme.Px(16);
                var lw = new[] { connLabel, activeLabel }.Max(c => Theme.Measure(c.Text, c.Font).Width) + Theme.Px(16);
                Action<Caption, TextField, Caption> number = (label, field, hint) =>
                {
                    label.Bounds = new Rectangle(x, y, lw, row);
                    field.Bounds = new Rectangle(x + lw, y, Theme.Px(72), row);
                    hint.Bounds = new Rectangle(field.Right + gap, y, w - lw - Theme.Px(72) - gap, row);
                    y += row + gap;
                };
                number(connLabel, conn, connHint);
                number(activeLabel, active, activeHint);
                y += gap;
                limitOn.Width = limitOn.PreferredWidth;
                limitOn.Location = new Point(x, y + (row - limitOn.Height) / 2);
                limit.Bounds = new Rectangle(limitOn.Right + gap, y, Theme.Px(88), row);
                kbs.Bounds = new Rectangle(limit.Right + gap, y, Theme.Measure(kbs.Text, kbs.Font).Width + 1, row);
                y += row;
            }
            else if (page == Categories) y = CategoriesLayout(x, w);
            else
            {
                queuesText.Bounds = new Rectangle(x, y, w, queuesText.HeightFor(w));
                y = queuesText.Bottom + Theme.Px(12);
                openScheduler.Bounds = new Rectangle(x, y, openScheduler.PreferredWidth, row);
                y += row;
            }
            error.Visible = error.Text != "";
            if (error.Visible)
            {
                var h = error.HeightFor(w);
                error.Bounds = new Rectangle(x, y + Theme.Px(12), w, h);
                y = error.Bottom;
            }
            // the tallest page sets the height, so switching pages does not resize the window
            BodyHeight(Math.Max(y, page == Categories ? y : CategoriesHeight(w)) + p);
            nav.Bounds = new Rectangle(0, 0, nw, Body.Height);
        }

        int CategoriesHeight(int w) { return Pad + CategoryTable.HeightFor(6) + Theme.Px(12 + 32 + 12) + note.HeightFor(w); }

        int CategoriesLayout(int x, int w)
        {
            int y = Pad, row = Theme.Px(32), gap = Theme.Px(8);
            table.Bounds = new Rectangle(x, y, w, CategoryTable.HeightFor(6)); // six rows, then it scrolls
            y = table.Bottom + Theme.Px(12);
            add.Bounds = new Rectangle(x, y, Math.Max(Theme.Px(80), add.PreferredWidth), row);
            edit.Bounds = new Rectangle(add.Right + gap, y, Math.Max(Theme.Px(80), edit.PreferredWidth), row);
            remove.Bounds = new Rectangle(edit.Right + gap, y, Math.Max(Theme.Px(84), remove.PreferredWidth), row);
            y += row + Theme.Px(12);
            note.Bounds = new Rectangle(x, y, w, note.HeightFor(w));
            return note.Bottom;
        }

        // The categories in sidebar order: header 32, then a row each, 40 or taller: its name, file types and folder wrap
        // whole, never cut. Custom ones carry "yours" (mockup m5). Hairlines under each, one selected row,
        // Up/Down/Home/End and the wheel.
        class CategoryTable : Painted
        {
            const string Yours = "yours";
            List<string> names = new List<string>();
            List<int> tops = new List<int> { 0 }; // each row's top under the header, then the last one's bottom
            Settings s = new Settings();
            public string Selected;
            public event Action SelectionChanged;
            int scroll;

            public CategoryTable()
            {
                SetStyle(ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
                TabStop = true;
                AccessibleName = "Categories";
            }

            static int RowH { get { return Theme.Px(40); } }
            static int HeadH { get { return Theme.Px(32); } }
            static int Gap { get { return Theme.Px(8); } }
            public static int HeightFor(int rows) { return HeadH + rows * RowH; }
            int Index { get { return names.IndexOf(Selected); } }
            // the columns after the 24 px icon: name, file types (52% of the rest), folder
            int NameX { get { return Gap + Theme.Px(24 + 10); } }
            int TypesX { get { return NameX + Theme.Px(124); } }
            int FolderX { get { return TypesX + (Width - TypesX) * 52 / 100; } }
            static bool Custom(string name) { return !Core.BuiltInCategories.Contains(name); }
            static int BadgeW { get { return Theme.Measure(Yours, Theme.Small).Width + Theme.Px(12); } }
            int NameW(string name) { return TypesX - NameX - Gap - (Custom(name) ? BadgeW + Theme.Px(6) : 0); }

            public void Fill(Settings settings)
            {
                s = settings;
                names = settings.Types.Keys.ToList();
                if (Selected != null && Index < 0) Selected = null;
                Measure();
                AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
            }

            // each row as high as its tallest column; again on resize, as the columns follow the width
            void Measure()
            {
                tops = new List<int> { 0 };
                int tw = FolderX - TypesX - Gap, fw = Width - FolderX - Gap;
                foreach (var n in names)
                {
                    var h = tw <= 0 || fw <= 0 ? 0 : Math.Max(Theme.MeasureHeight(n, Theme.Body, NameW(n)),
                        Math.Max(Theme.MeasureHeight(Types(n), Theme.Small, tw), Theme.MeasureHeight(FolderText(n), Theme.Small, fw)));
                    tops.Add(tops[tops.Count - 1] + Math.Max(RowH, h + Theme.Px(8)));
                }
                Scroll(scroll);
            }

            public void Select(string name)
            {
                if (Selected == name || !names.Contains(name)) return;
                Selected = name;
                var r = Row(Index);
                if (r.Top < HeadH) Scroll(scroll - (HeadH - r.Top));
                else if (r.Bottom > Height) Scroll(scroll + r.Bottom - Height);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, Index);
                var h = SelectionChanged;
                if (h != null) h();
            }

            void Scroll(int y)
            {
                var max = Math.Max(0, tops[names.Count] - (Height - HeadH));
                scroll = Math.Max(0, Math.Min(max, y));
                Invalidate();
            }

            Rectangle Row(int i) { return new Rectangle(0, HeadH + tops[i] - scroll, Width, tops[i + 1] - tops[i]); }

            // Custom categories have no icon of their own: they show Other's
            static string Icon(string name) { return Custom(name) ? "Other" : name; }
            string Types(string name) { List<string> l; return s.Types.TryGetValue(name, out l) ? string.Join(" ", l) : ""; }
            string Folder(string name) { return Core.FolderFor(s, name); }
            string FolderText(string name) { return Core.ShortFolder(s.Dir, Folder(name)); }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                var y = e.Y - HeadH + scroll;
                var i = tops.FindLastIndex(t => t <= y);
                if (e.Y >= HeadH && i < names.Count) Select(names[i]);
                base.OnMouseDown(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e) { Scroll(scroll - e.Delta * RowH / 120); base.OnMouseWheel(e); }
            protected override void OnResize(EventArgs e) { base.OnResize(e); Measure(); }
            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
            protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End || base.IsInputKey(k); }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                var i = Index;
                var j = e.KeyCode == Keys.Up ? i - 1 : e.KeyCode == Keys.Down ? i + 1 : e.KeyCode == Keys.Home ? 0 : e.KeyCode == Keys.End ? names.Count - 1 : -2;
                if (j != -2 && names.Count > 0)
                {
                    Select(names[Math.Max(0, Math.Min(names.Count - 1, j))]);
                    e.Handled = true;
                }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                int pad = Gap, w = Width, nameX = NameX, typesX = TypesX, folderX = FolderX;
                Theme.DrawText(g, "Category", Theme.Small, new Rectangle(nameX, 0, typesX - nameX - pad, HeadH - 1), Theme.Muted);
                Theme.DrawText(g, "File types", Theme.Small, new Rectangle(typesX, 0, folderX - typesX - pad, HeadH - 1), Theme.Muted);
                Theme.DrawText(g, "Folder", Theme.Small, new Rectangle(folderX, 0, w - folderX - pad, HeadH - 1), Theme.Muted);
                Theme.HLine(g, 0, w, HeadH - 1);
                g.SetClip(new Rectangle(0, HeadH, w, Height - HeadH));
                for (var i = 0; i < names.Count; i++)
                {
                    var r = Row(i);
                    if (r.Bottom < HeadH || r.Top > Height) continue;
                    var n = names[i];
                    var sel = n == Selected;
                    var line = new Rectangle(r.X, r.Y, r.Width, r.Height - 1);
                    if (sel) Theme.Fill(g, line, Theme.Px(6), Theme.Sel);
                    var fore = sel ? Theme.OnSel : Theme.Text;
                    var slot = new Rectangle(pad, r.Y, Theme.Px(24), r.Height - 1);
                    if (!FileIcons.DrawType(g, Icon(n), slot, 24, fore)) Theme.DrawGlyph(g, Glyph.Of(Icon(n)), slot, sel ? fore : Theme.Category(Icon(n)), 16);
                    // each column whole and centered in the row; the file types as strong as the rest (format labels are never faint)
                    Action<string, Font, int, int> column = (text, font, x, cw) =>
                    {
                        var h = Theme.MeasureHeight(text, font, cw);
                        Theme.DrawText(g, text, font, new Rectangle(x, line.Y + (line.Height - h) / 2, cw, h), fore, Theme.Wrap);
                    };
                    var nw = NameW(n);
                    column(n, Theme.Body, nameX, nw);
                    if (Custom(n))
                    {
                        var badge = new Rectangle(nameX + Math.Min(nw, Theme.Measure(n, Theme.Body).Width) + Theme.Px(6), line.Y + (line.Height - Theme.Px(18)) / 2, BadgeW, Theme.Px(18));
                        Theme.Fill(g, badge, Theme.Px(9), Theme.Soft); // neutral: readable on a selected row in both themes
                        Theme.DrawText(g, Yours, Theme.Small, badge, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    }
                    column(Types(n), Theme.Small, typesX, folderX - typesX - pad);
                    column(FolderText(n), Theme.Small, folderX, w - folderX - pad);
                    Theme.HLine(g, 0, w, r.Bottom - 1);
                    if (sel && Focused && ShowFocusCues)
                        using (var pen = new Pen(Theme.OnSel, Theme.Px(2))) g.DrawRectangle(pen, line.X + Theme.Px(2), line.Y + Theme.Px(2), line.Width - Theme.Px(5), line.Height - Theme.Px(5));
                }
                g.ResetClip();
                if (Focused && ShowFocusCues && Index < 0) Theme.FocusRing(g, ClientRectangle, Theme.Px(8));
            }

            protected override AccessibleObject CreateAccessibilityInstance()
            {
                var me = this;
                ListAccessible l = null;
                l = new ListAccessible(this, () => names.Count, i =>
                {
                    var n = names[i];
                    return new ItemAccessible(l, () => n + (Custom(n) ? ", yours" : "") + ", file types " + Types(n) + ", folder " + Folder(n), () => n == Selected,
                        () => Focused && n == Selected, () => me.RectangleToScreen(Row(Math.Max(0, names.IndexOf(n)))), () => Select(n));
                });
                return l;
            }
        }
    }
}
