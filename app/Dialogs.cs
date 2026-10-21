// Stand-ins for the dialogs (docs/NATIVE_UI.md "Dialogs"): the dialogs task replaces this file and keeps this API.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Arrow
{
    public static class Dialogs
    {
        static readonly Dictionary<string, Form> infos = new Dictionary<string, Form>(), progress = new Dictionary<string, Form>();
        static readonly Dictionary<string, Action<Download>> refresh = new Dictionary<string, Action<Download>>();

        // Modeless, one per pending download; brought to the front even when the main window is hidden
        public static void FileInfo(Api api, Form owner, string id)
        {
            Form f;
            if (infos.TryGetValue(id, out f)) { Front(f); return; }
            Caption text;
            f = Shell("Download File Info", out text);
            text.Text = "Getting file info…";
            refresh["i" + id] = d => text.Text = Core.Subtitle(d, d.Probe != null && d.Probe.Name != null ? d.Probe.Name : Core.ShortName(d)) + "\n" + d.Url;
            Action<string> answer = action => Run(f, () => api.Post("/api/confirm", new Dictionary<string, object> { { "id", id }, { "action", action } }));
            var later = new FlatButton("Download Later", '\0', ButtonKind.Secondary);
            var cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary);
            var start = new FlatButton("Start Download", '\0', ButtonKind.Primary);
            later.Click += (s, e) => { answer("later"); f.Close(); };
            cancel.Click += (s, e) => { answer("cancel"); f.Close(); };
            start.Click += (s, e) => { answer("start"); f.Close(); Progress(api, owner, id); };
            Footer(f, start, cancel, later);
            f.CancelButton = cancel;
            f.FormClosed += (s, e) => { infos.Remove(id); refresh.Remove("i" + id); };
            infos[id] = f;
            Front(f);
        }

        // Modeless, one per download
        public static void Progress(Api api, Form owner, string id)
        {
            Form f;
            if (progress.TryGetValue(id, out f)) { Front(f); return; }
            Caption text;
            f = Shell("Progress", out text);
            var bar = new SegmentBar { Dock = DockStyle.Bottom };
            text.Parent.Controls.Add(bar);
            refresh["p" + id] = d =>
            {
                text.Text = Core.Heading(d) + "\n" + Core.ShortName(d);
                f.Text = Math.Floor(Core.Fraction(d, true) * 100) + "% · " + Core.ShortName(d);
                bar.Set(Core.Pieces(d), d.Status == "done" ? BarKind.Done : d.Status == "error" ? BarKind.Err : BarKind.Normal);
            };
            var close = new FlatButton("Close", '\0', ButtonKind.Primary);
            close.Click += (s, e) => f.Close();
            Footer(f, close);
            f.CancelButton = close;
            f.FormClosed += (s, e) => { progress.Remove(id); refresh.Remove("p" + id); };
            progress[id] = f;
            Front(f);
        }

        public static void AddLinks(Api api, IWin32Window owner, string prefill)
        {
            Caption text;
            var f = Shell("Add links", out text);
            text.Text = "One link per line. img[001-120].jpg adds 120 links.";
            var links = new TextField('\0', "") { Dock = DockStyle.Fill };
            links.Box.Multiline = true;
            links.Box.ScrollBars = ScrollBars.Vertical;
            links.Box.Text = prefill ?? "";
            var later = new CheckBox { Text = "Start later", Dock = DockStyle.Bottom, Height = Theme.Px(28), ForeColor = Theme.Text };
            text.Parent.Height = Theme.Px(220);
            text.Parent.Controls.Add(links);
            text.Parent.Controls.Add(later);
            links.BringToFront();
            var add = new FlatButton("Add", '\0', ButtonKind.Primary);
            var cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary) { DialogResult = DialogResult.Cancel };
            add.Click += (s, e) =>
            {
                var typed = Regex.Split(links.Box.Text, @"\s+").Where(x => x != "").ToList();
                List<string> urls;
                try { urls = Core.ExpandBatch(typed); }
                catch (ArgumentException ex) { Error(f, ex.Message); return; }
                if (urls.Count == 0) return;
                if (urls.Count > typed.Count && !Confirm(f, "Add links", "Add " + urls.Count + " downloads?\n\n" + urls[0] + "\n…\n" + urls[urls.Count - 1], "Add", false)) return;
                foreach (var u in urls)
                {
                    try { api.Post("/api/add", new Dictionary<string, object> { { "url", u }, { "scheduled", later.Checked } }); }
                    catch (ApiException ex) { Error(f, u + "\n" + ex.Message); return; }
                }
                f.DialogResult = DialogResult.OK;
            };
            Footer(f, add, cancel);
            f.CancelButton = cancel;
            Modal(f, owner);
        }

        public static void Options(Api api, IWin32Window owner, State state, bool focusSchedule)
        {
            Error(owner, "Options are coming in the next step of the native app.");
        }

        public static bool ConfirmRemove(IWin32Window owner, int count, bool anyFinished, out bool deleteFiles)
        {
            Caption text;
            var f = Shell("Remove", out text);
            text.Text = "Remove " + (count == 1 ? "1 download" : count + " downloads") + " from the list?";
            var files = new CheckBox { Text = "Also delete the downloaded files", Dock = DockStyle.Bottom, Height = Theme.Px(28), ForeColor = Theme.Text, Visible = anyFinished };
            text.Parent.Controls.Add(files);
            var remove = new FlatButton("Remove", '\0', ButtonKind.Danger) { DialogResult = DialogResult.OK };
            var cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary) { DialogResult = DialogResult.Cancel };
            Footer(f, remove, cancel);
            f.AcceptButton = remove;
            f.CancelButton = cancel;
            var ok = Modal(f, owner) == DialogResult.OK;
            deleteFiles = ok && anyFinished && files.Checked;
            return ok;
        }

        public static bool Confirm(IWin32Window owner, string title, string text, string okText, bool danger)
        {
            Caption body;
            var f = Shell(title, out body);
            body.Text = text;
            var ok = new FlatButton(okText, '\0', danger ? ButtonKind.Danger : ButtonKind.Primary) { DialogResult = DialogResult.OK };
            var cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary) { DialogResult = DialogResult.Cancel };
            Footer(f, ok, cancel);
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return Modal(f, owner) == DialogResult.OK;
        }

        // null = cancelled
        public static string ChangeLink(IWin32Window owner, string current)
        {
            Caption body;
            var f = Shell("Change link", out body);
            body.Text = "New link for the same file";
            var field = new TextField('\0', "") { Dock = DockStyle.Bottom };
            field.Box.Text = current ?? "";
            body.Parent.Controls.Add(field);
            var save = new FlatButton("Save", '\0', ButtonKind.Primary) { DialogResult = DialogResult.OK };
            var cancel = new FlatButton("Cancel", '\0', ButtonKind.Secondary) { DialogResult = DialogResult.Cancel };
            Footer(f, save, cancel);
            f.AcceptButton = save;
            f.CancelButton = cancel;
            string url = null;
            f.FormClosing += (s, e) => { if (f.DialogResult == DialogResult.OK && field.Box.Text.Trim() != "") url = field.Box.Text.Trim(); };
            Modal(f, owner);
            return url;
        }

        public static void Error(IWin32Window owner, string text)
        {
            Caption body;
            var f = Shell(Program.Name, out body);
            body.Text = text;
            var ok = new FlatButton("OK", '\0', ButtonKind.Primary) { DialogResult = DialogResult.OK };
            Footer(f, ok);
            f.AcceptButton = f.CancelButton = ok;
            Modal(f, owner);
        }

        public static void About(IWin32Window owner)
        {
            Caption body;
            var f = Shell("About " + Program.Name, out body);
            body.Text = Program.Name + " " + Application.ProductVersion + "\nA download manager for Windows.";
            var ok = new FlatButton("Close", '\0', ButtonKind.Primary) { DialogResult = DialogResult.OK };
            Footer(f, ok);
            f.AcceptButton = f.CancelButton = ok;
            Modal(f, owner);
        }

        // Called on every poll: open Download File Info and progress windows show the new state
        public static void Update(State s)
        {
            foreach (var key in refresh.Keys.ToList())
            {
                var id = key.Substring(1);
                var d = s.Downloads.FirstOrDefault(x => x.Id == id);
                Form f;
                if (key[0] == 'i' && (d == null || d.Status != "pending") && infos.TryGetValue(id, out f)) { f.Close(); continue; }
                if (d != null) refresh[key](d);
            }
        }

        // A themed window: body text (Caption) over a footer
        static Form Shell(string title, out Caption text)
        {
            var f = new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false,
                ClientSize = new Size(Theme.Px(420), Theme.Px(160)), BackColor = Theme.Inspector, Font = Theme.Body, KeyPreview = true,
            };
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(20)), BackColor = Theme.Inspector };
            text = new Caption(Theme.Body, () => Theme.Text) { Dock = DockStyle.Top, MaxLines = 8 };
            var t = text;
            body.Controls.Add(text);
            body.Layout += (s, e) => t.Height = t.HeightFor(body.ClientSize.Width - body.Padding.Horizontal);
            f.Controls.Add(body);
            f.HandleCreated += (s, e) => { AppIcon.Apply(f); Theme.TitleBar(f); };
            return f;
        }

        static void Footer(Form f, params FlatButton[] buttons)
        {
            var bar = new Canvas((g, r) => Theme.HLine(g, 0, r.Width, 0)) { Dock = DockStyle.Bottom, Height = Theme.Px(64), BackColor = Theme.Top };
            bar.Painter = (g, r) => { using (var b = new SolidBrush(Theme.Top)) g.FillRectangle(b, r); Theme.HLine(g, 0, r.Width, 0); };
            var x = f.ClientSize.Width - Theme.Px(20);
            foreach (var b in buttons)
            {
                b.Height = Theme.Px(32);
                b.Width = Math.Max(Theme.Px(88), b.PreferredWidth);
                x -= b.Width;
                b.Location = new Point(x, Theme.Px(16));
                b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                x -= Theme.Px(8);
                bar.Controls.Add(b);
            }
            bar.BackColor = Theme.Top;
            f.Controls.Add(bar);
            if (buttons.Length > 0 && f.AcceptButton == null) f.AcceptButton = buttons[0];
        }

        static DialogResult Modal(Form f, IWin32Window owner)
        {
            using (f)
            {
                var c = owner as Control;
                var visible = c != null && c.Visible;
                f.StartPosition = visible ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                return visible ? f.ShowDialog(owner) : f.ShowDialog();
            }
        }

        static void Front(Form f)
        {
            f.StartPosition = FormStartPosition.CenterScreen;
            if (!f.Visible) f.Show();
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.TopMost = true;
            f.TopMost = false;
            f.Activate();
        }

        static void Run(IWin32Window owner, Action call)
        {
            try { call(); }
            catch (ApiException e) { Error(owner, e.Message); }
        }
    }
}
