// The dialogs (docs/NATIVE_UI.md "Dialogs"): the static API the main window calls, the themed Dialog base, the small
// modal dialogs (Confirm, Remove, a text prompt, Link already in the list, Error, About), and GLIDE_SHOTS pictures of every dialog.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Glide
{
    // A themed dialog: Body (inspector token) over Foot (top token, top hairline, buttons at the left and the right).
    // Esc closes it when it has no CancelButton.
    public class Dialog : Form
    {
        public static bool Offscreen; // GLIDE_SHOTS: shown beyond the screens, never activated
        public readonly Panel Root = new Panel { Dock = DockStyle.Fill };
        public readonly Panel Body = new Panel { Dock = DockStyle.Fill, TabIndex = 0 };
        public readonly Canvas Foot;
        protected readonly ToolTip Tips = new ToolTip();
        List<FlatButton> left = new List<FlatButton>(), right = new List<FlatButton>();
        readonly Action onTheme;
        int tab;

        public static int Pad { get { return Theme.Px(20); } }

        public Dialog(string title, int width)
        {
            Text = title;
            if (AppIcon.Window != null) Icon = AppIcon.Window;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.Body;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            Foot = new Canvas((g, r) => { using (var b = new SolidBrush(Theme.Top)) g.FillRectangle(b, r); Theme.HLine(g, 0, r.Width, 0); })
                { Dock = DockStyle.Bottom, Height = Theme.Px(64), TabIndex = 1 };
            Foot.Layout += (s, e) => LayoutFoot();
            Root.Controls.Add(Body);
            Root.Controls.Add(Foot);
            Controls.Add(Root);
            ClientSize = new Size(Theme.Px(width), Theme.Px(160));
            Colors();
            onTheme = () =>
            {
                try { if (IsHandleCreated) BeginInvoke((MethodInvoker)(() => { if (!IsDisposed) { Colors(); Themed.Apply(this); } })); }
                catch (InvalidOperationException) { }
            };
            Theme.Changed += onTheme;
        }

        protected override bool ShowWithoutActivation { get { return Offscreen; } }

        // after WinForms' own UpdateWindowIcon, so the crisp small icon wins
        protected override void CreateHandle()
        {
            base.CreateHandle();
            AppIcon.Apply(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Colors();
            Themed.Apply(this);
        }

        protected override void Dispose(bool disposing)
        {
            Theme.Changed -= onTheme;
            if (disposing) Tips.Dispose();
            base.Dispose(disposing);
        }

        // Child controls inherit BackColor and ForeColor; only the footer and read-only text boxes need their own
        void Colors()
        {
            BackColor = Theme.Inspector;
            ForeColor = Theme.Text;
            Foot.BackColor = Theme.Top;
            foreach (var t in Body.Controls.OfType<TextBox>()) { t.BackColor = Theme.Inspector; t.ForeColor = Theme.Text; }
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape && CancelButton == null) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        // Adds to the body in tab order
        public T Add<T>(T c) where T : Control
        {
            c.TabIndex = tab++;
            Body.Controls.Add(c);
            return c;
        }

        public static Caption Label(string text) { return new Caption(Theme.Body, () => Theme.Muted) { Text = text }; }

        public static FlatButton Button(string text, ButtonKind kind, DialogResult result)
        {
            return new FlatButton(text, '\0', kind) { DialogResult = result };
        }

        // The footer's buttons, left to right; buttons left out are hidden
        public void Buttons(FlatButton[] atLeft, FlatButton[] atRight)
        {
            left = atLeft.ToList();
            right = atRight.ToList();
            var shown = left.Concat(right).ToList();
            for (var i = 0; i < shown.Count; i++)
            {
                if (shown[i].Parent != Foot) Foot.Controls.Add(shown[i]);
                shown[i].TabIndex = i;
            }
            foreach (Control c in Foot.Controls) c.Visible = shown.Contains(c);
            LayoutFoot();
        }

        public void LayoutFoot()
        {
            int y = Theme.Px(16), h = Theme.Px(32), gap = Theme.Px(8), x = Pad;
            Func<FlatButton, Size> size = b => new Size(Math.Max(Theme.Px(88), b.PreferredWidth), h);
            foreach (var b in left)
            {
                b.Bounds = new Rectangle(new Point(x, y), size(b));
                x += b.Width + gap;
            }
            x = Foot.Width - Pad;
            for (var i = right.Count - 1; i >= 0; i--)
            {
                var b = right[i];
                b.Size = size(b);
                x -= b.Width;
                b.Location = new Point(x, y);
                x -= gap;
            }
            Foot.Invalidate();
        }

        public void BodyHeight(int h) { ClientSize = new Size(ClientSize.Width, h + Foot.Height); }

        // A wrapped paragraph across the body at y; returns the y under it
        public int Paragraph(Caption c, int y)
        {
            var w = ClientSize.Width - 2 * Pad;
            c.MaxLines = 20;
            c.Bounds = new Rectangle(Pad, y, w, c.HeightFor(w));
            if (c.Parent == null) Add(c);
            return c.Bottom;
        }

        bool centered; // placed by Center() and not moved by the user since: a change of height keeps it centered
        int placedHeight;

        // Centered on the screen, without the taskbar (its working area): the owner's screen, or the mouse's when the
        // owner is hidden or minimized. Any resolution, scale or monitor.
        public void Center(IWin32Window owner)
        {
            var c = owner as Control;
            var f = c as Form;
            var shown = c != null && c.Visible && (f == null || f.WindowState != FormWindowState.Minimized);
            var wa = (shown ? Screen.FromControl(c) : Screen.FromPoint(Cursor.Position)).WorkingArea;
            Location = Clamp(new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2), wa);
            centered = true;
            placedHeight = Height;
        }

        Point Clamp(Point p, Rectangle wa)
        {
            return new Point(Math.Max(wa.Left, Math.Min(p.X, wa.Right - Width)), Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - Height)));
        }

        // Connections, Options and the like change the height: the middle stays where it was
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!centered || Height == placedHeight || WindowState != FormWindowState.Normal) return;
            var p = new Point(Left, Top - (Height - placedHeight) / 2);
            placedHeight = Height;
            Location = Clamp(p, Screen.FromControl(this).WorkingArea);
        }

        // Moved (or sized) by the user: it stays where they put it
        protected override void OnResizeEnd(EventArgs e)
        {
            base.OnResizeEnd(e);
            centered = false;
        }
    }

    public static class Dialogs
    {
        static readonly Dictionary<string, FileInfoDialog> infos = new Dictionary<string, FileInfoDialog>();
        static readonly Dictionary<string, ProgressDialog> progress = new Dictionary<string, ProgressDialog>();
        static readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
        static State last;
        static readonly Dictionary<string, string> seen = new Dictionary<string, string>(); // each download's status at the last poll
        static readonly HashSet<string> completeShown = new HashSet<string>(); // Download complete opened by itself once per download
        const int Samples = 86; // 60 s of 700 ms polls

        // The progress window's disclosures, open by default; MainForm keeps them in ui.json and saves on UiChanged
        public static bool ConnectionsOpen = true, OptionsOpen = true;
        public static Action UiChanged;

        // Modeless, one per pending download; comes to the front even when the main window is hidden
        public static void FileInfo(Api api, Form owner, string id)
        {
            FileInfoDialog f;
            if (infos.TryGetValue(id, out f)) { Front(f); return; }
            f = new FileInfoDialog(api, id);
            infos[id] = f;
            f.FormClosed += (s, e) => { FileInfoDialog cur; if (infos.TryGetValue(id, out cur) && cur == f) infos.Remove(id); };
            f.Center(owner);
            Front(f);
        }

        // Modeless, one per download
        public static void Progress(Api api, Form owner, string id)
        {
            ProgressDialog f;
            if (progress.TryGetValue(id, out f)) { Front(f); return; }
            f = NewProgress(api, id);
            f.Center(owner);
            Front(f);
        }

        static SchedulerDialog scheduler;

        // Modeless, one at a time: opening it again brings it to the front, showing queueId when given
        public static void Scheduler(Api api, Form owner, State s, string queueId)
        {
            if (scheduler == null)
            {
                var f = scheduler = new SchedulerDialog(api);
                f.FormClosed += (o, e) => { if (scheduler == f) scheduler = null; };
                f.Fill(s);
                f.Center(owner);
            }
            if (queueId != null) scheduler.Select(queueId);
            Front(scheduler);
        }

        static ProgressDialog NewProgress(Api api, string id)
        {
            var f = new ProgressDialog(api, id);
            progress[id] = f;
            f.FormClosed += (s, e) => { ProgressDialog cur; if (progress.TryGetValue(id, out cur) && cur == f) { progress.Remove(id); samples.Remove(id); } };
            if (last != null) f.Fill(Find(last, id), last.Categories, Speeds(id));
            return f;
        }

        // Download File Info's Start went through: the progress window takes its place
        internal static void Started(FileInfoDialog info)
        {
            ProgressDialog f;
            if (!progress.TryGetValue(info.Id, out f))
            {
                f = NewProgress(info.Api, info.Id);
                f.Center(info); // taller than Download File Info: centered on its own
            }
            info.Close();
            Front(f);
        }

        // Called on every poll: open Download File Info and progress windows show the new state, and a download that just
        // finished opens its Download complete window (an open progress window turns into it by itself)
        public static void Update(Api api, Form owner, State s)
        {
            last = s;
            foreach (var d in s.Downloads)
            {
                string before;
                seen.TryGetValue(d.Id, out before);
                if (Core.OpensComplete(d, before, s.Settings) && completeShown.Add(d.Id) && !progress.ContainsKey(d.Id)) Progress(api, owner, d.Id);
            }
            seen.Clear();
            foreach (var d in s.Downloads) seen[d.Id] = d.Status;
            foreach (var f in progress.Values.ToList())
            {
                var d = Find(s, f.Id);
                var l = Speeds(f.Id);
                if (d != null && d.Status == "downloading")
                {
                    l.Add(d.Speed);
                    if (l.Count > Samples) l.RemoveRange(0, l.Count - Samples);
                }
                f.Fill(d, s.Categories, l);
            }
            foreach (var f in infos.Values.ToList()) f.Fill(Find(s, f.Id), s.Settings, s);
            if (scheduler != null) scheduler.Fill(s);
        }

        static Download Find(State s, string id) { return s.Downloads.FirstOrDefault(d => d.Id == id); }
        static List<double> Speeds(string id)
        {
            List<double> l;
            if (!samples.TryGetValue(id, out l)) samples[id] = l = new List<double>();
            return l;
        }

        // ---- engine calls: work off the UI thread, done(error or null) back on the form's thread, dropped once it is gone

        internal static void Call(Control form, Action work, Action<string> done)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string error = null;
                try { work(); }
                catch (ApiException e) { error = e.Message; }
                catch (Exception) { error = "Something went wrong. Try again."; }
                try { if (!form.IsDisposed && form.IsHandleCreated) form.BeginInvoke((MethodInvoker)(() => { if (!form.IsDisposed) done(error); })); }
                catch (InvalidOperationException) { } // closed meanwhile
            });
        }

        // ---- bringing a window to the front while another program (the browser) has the focus

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        const byte VK_MENU = 0x12;
        const uint KEYEVENTF_KEYUP = 2;

        internal static void Front(Form f)
        {
            if (!f.Visible) f.Show();
            if (Dialog.Offscreen) return;
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.TopMost = true;
            if (GetForegroundWindow() != f.Handle)
            {
                // Windows lets a process take the foreground right after input: a synthetic Alt press counts
                keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                SetForegroundWindow(f.Handle);
            }
            f.Activate();
            f.BeginInvoke((MethodInvoker)(() => { if (!f.IsDisposed) f.TopMost = false; }));
        }

        // ---- modal dialogs

        static DialogResult Modal(Dialog f, IWin32Window owner)
        {
            using (f)
            {
                f.Center(owner);
                var c = owner as Control;
                return c != null && c.Visible ? f.ShowDialog(owner) : f.ShowDialog();
            }
        }

        public static void AddLinks(Api api, IWin32Window owner, string prefill)
        {
            Modal(new AddLinksDialog(api, prefill), owner);
        }

        // page: one of OptionsDialog's pages (General, Downloads, Categories, Queues); its Open Scheduler saves, closes it, then opens the Scheduler
        public static void Options(Api api, IWin32Window owner, State state, string page)
        {
            var f = new OptionsDialog(api, state.Settings, page);
            Modal(f, owner);
            if (f.SchedulerWanted) Scheduler(api, owner as Form, last ?? state, null);
        }

        static Dialog RemoveDialog(int count, bool anyFinished, out FlatCheck files)
        {
            var f = new Dialog("Remove", 420);
            var y = f.Paragraph(new Caption(Theme.Body, () => Theme.Text) { Text = "Remove " + (count == 1 ? "1 download" : count + " downloads") + " from the list?" }, Dialog.Pad);
            files = new FlatCheck("Also delete the downloaded files") { Visible = anyFinished };
            if (anyFinished)
            {
                files.Location = new Point(Dialog.Pad, y + Theme.Px(12));
                f.Add(files);
                y = files.Bottom;
            }
            f.BodyHeight(y + Dialog.Pad);
            var remove = Dialog.Button("Remove", ButtonKind.Danger, DialogResult.OK);
            var cancel = Dialog.Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
            f.Buttons(new FlatButton[0], new[] { cancel, remove });
            f.AcceptButton = remove;
            f.CancelButton = cancel;
            return f;
        }

        public static bool ConfirmRemove(IWin32Window owner, int count, bool anyFinished, out bool deleteFiles)
        {
            FlatCheck files;
            var f = RemoveDialog(count, anyFinished, out files);
            var ok = Modal(f, owner) == DialogResult.OK;
            deleteFiles = ok && anyFinished && files.Checked;
            return ok;
        }

        public static bool Confirm(IWin32Window owner, string title, string text, string okText, bool danger)
        {
            var f = new Dialog(title, 420);
            f.BodyHeight(f.Paragraph(new Caption(Theme.Body, () => Theme.Text) { Text = text }, Dialog.Pad) + Dialog.Pad);
            var ok = Dialog.Button(okText, danger ? ButtonKind.Danger : ButtonKind.Primary, DialogResult.OK);
            var cancel = Dialog.Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
            f.Buttons(new FlatButton[0], new[] { cancel, ok });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return Modal(f, owner) == DialogResult.OK;
        }

        // One line of text: a new queue's name (Download Later's "+")
        static Dialog TextDialog(string title, string prompt, string value, int max, string okText, out TextField field)
        {
            var f = new Dialog(title, 420);
            var y = f.Paragraph(new Caption(Theme.Body, () => Theme.Text) { Text = prompt }, Dialog.Pad);
            var box = field = new TextField('\0', "");
            field.Box.AccessibleName = prompt;
            field.Box.MaxLength = max;
            field.Box.Text = value ?? "";
            field.Bounds = new Rectangle(Dialog.Pad, y + Theme.Px(8), f.ClientSize.Width - 2 * Dialog.Pad, Theme.Px(32));
            f.Add(field);
            f.BodyHeight(field.Bottom + Dialog.Pad);
            var ok = Dialog.Button(okText, ButtonKind.Primary, DialogResult.None);
            var cancel = Dialog.Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
            ok.Click += (s, e) =>
            {
                if (box.Text.Trim() == "") { box.Invalid = true; box.Box.Focus(); return; }
                f.DialogResult = DialogResult.OK;
            };
            box.Box.TextChanged += (s, e) => box.Invalid = false;
            f.Buttons(new FlatButton[0], new[] { cancel, ok });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.ActiveControl = field.Box;
            return f;
        }

        // The trimmed text, null = cancelled
        public static string AskText(IWin32Window owner, string title, string prompt, string value, int max, string okText)
        {
            TextField field;
            var f = TextDialog(title, prompt, value, max, okText, out field);
            return Modal(f, owner) == DialogResult.OK ? field.Text.Trim() : null;
        }

        // Link already in the list (spec "6"): the existing download, three choices with their sub-lines, remember
        static readonly string[] DuplicateKeys = { "number", "replace", "resume" };

        static Dialog DuplicateDialog(string existing, string status, string numbered, out FlatRadio[] choices, out FlatCheck remember)
        {
            var f = new Dialog("Link already in the list", 460);
            int p = Dialog.Pad, w = f.ClientSize.Width - 2 * p, indent = Theme.Px(26);
            var title = f.Add(new Caption(Theme.Bold, () => Theme.Text) { Text = "This link is already in your list" });
            title.Bounds = new Rectangle(p, p, w, title.Height);
            var sub = f.Add(new Caption(Theme.Small, () => Theme.Muted) { Text = string.Join(" · ", new[] { existing, status }.Where(t => !string.IsNullOrEmpty(t))), MaxLines = Caption.Unlimited });
            sub.Bounds = new Rectangle(p, title.Bottom + Theme.Px(2), w, sub.HeightFor(w));
            var y = sub.Bottom + Theme.Px(20);
            var texts = new[] { "Download it again with a new name", "Replace the existing file", "Resume the existing download" };
            var lines = new[] { numbered, null, "If it's finished, show it instead" };
            choices = new FlatRadio[3];
            for (var i = 0; i < 3; i++)
            {
                var r = choices[i] = f.Add(new FlatRadio(texts[i]) { Checked = i == 0, AccessibleDescription = lines[i] });
                r.Bounds = new Rectangle(p, y, w, r.Height);
                y = r.Bottom;
                if (lines[i] != null)
                {
                    var line = f.Add(new Caption(Theme.Small, () => Theme.Muted) { Text = lines[i], MaxLines = Caption.Unlimited }); // the numbered name, whole
                    line.Bounds = new Rectangle(p + indent, y - Theme.Px(2), w - indent, line.HeightFor(w - indent));
                    y = line.Bottom;
                }
                y += Theme.Px(8);
            }
            remember = f.Add(new FlatCheck("Remember my choice (change it in Options)"));
            remember.Location = new Point(p, y);
            f.BodyHeight(remember.Bottom + p);
            var ok = Dialog.Button("OK", ButtonKind.Primary, DialogResult.OK);
            var cancel = Dialog.Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
            f.Buttons(new FlatButton[0], new[] { cancel, ok });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            f.ActiveControl = choices[0];
            return f;
        }

        // "number", "replace" or "resume"; null = Cancel
        public static string AskDuplicate(IWin32Window owner, string existing, string status, string numbered, out bool remember)
        {
            FlatRadio[] choices;
            FlatCheck check;
            var f = DuplicateDialog(existing, status, numbered, out choices, out check);
            var ok = Modal(f, owner) == DialogResult.OK;
            remember = ok && check.Checked;
            return ok ? DuplicateKeys[Array.FindIndex(choices, c => c.Checked)] : null;
        }

        public static void Properties(Api api, IWin32Window owner, Download d, State s) { Modal(new PropertiesDialog(api, d, s), owner); }

        // The category editor, for Download File Info's "+" and Options › Categories: name null = a new category.
        // Saves at once (POST /api/settings {types, folders}); returns the engine's settings, or null when cancelled.
        public static Settings EditCategory(Api api, IWin32Window owner, Settings s, string name, out string saved)
        {
            var f = new CategoryDialog(api, s, name);
            var ok = Modal(f, owner) == DialogResult.OK;
            saved = ok ? f.SavedName : null;
            return ok ? f.Saved : null;
        }

        public static void Error(IWin32Window owner, string text) { Modal(ErrorDialog(text), owner); }

        static Dialog ErrorDialog(string text)
        {
            var f = new Dialog(Program.Name, 420);
            f.BodyHeight(f.Paragraph(new Caption(Theme.Body, () => Theme.Text) { Text = text }, Dialog.Pad) + Dialog.Pad);
            var ok = Dialog.Button("OK", ButtonKind.Primary, DialogResult.OK);
            f.Buttons(new FlatButton[0], new[] { ok });
            f.AcceptButton = f.CancelButton = ok;
            return f;
        }

        static Dialog AboutDialog()
        {
            var f = new Dialog("About " + Program.Name, 400);
            var icon = AppIcon.Load(Theme.Px(64));
            var image = icon == null ? null : icon.ToBitmap();
            var s = Theme.Px(64);
            var logo = new Canvas((g, r) => { if (image != null) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(image, r); } })
                { Bounds = new Rectangle(Dialog.Pad, Dialog.Pad, s, s), AccessibleName = Program.Name, AccessibleRole = AccessibleRole.Graphic };
            f.Disposed += (o, e) => { if (image != null) image.Dispose(); };
            f.Add(logo);
            var x = logo.Right + Theme.Px(16);
            var w = f.ClientSize.Width - x - Dialog.Pad;
            var name = new Caption(Theme.Bold, () => Theme.Text) { Text = Program.Name };
            var version = new Caption(Theme.Body, () => Theme.Muted) { Text = "Version " + Application.ProductVersion };
            var line = new Caption(Theme.Body, () => Theme.Text) { Text = "A download manager for Windows." };
            var h = name.Height + version.Height + line.Height + Theme.Px(8);
            var y = logo.Top + (s - h) / 2;
            foreach (var c in new[] { name, version, line })
            {
                c.Bounds = new Rectangle(x, y, w, c.Height);
                f.Add(c);
                y += c.Height + Theme.Px(4);
            }
            f.BodyHeight(logo.Bottom + Dialog.Pad);
            var close = Dialog.Button("Close", ButtonKind.Primary, DialogResult.OK);
            f.Buttons(new FlatButton[0], new[] { close });
            f.AcceptButton = f.CancelButton = close;
            return f;
        }

        public static void About(IWin32Window owner) { Modal(AboutDialog(), owner); }

        // Keyboard shortcuts (F1, Ctrl+/, More > Keyboard shortcuts): Core.Shortcuts, keys at the left
        static Dialog ShortcutsDialog()
        {
            var f = new Dialog("Keyboard shortcuts", 460);
            var y = Dialog.Pad;
            foreach (var k in Core.Shortcuts)
            {
                var row = new FactRow();
                row.Set(k[0], k[1]);
                row.Bounds = new Rectangle(Dialog.Pad, y, f.ClientSize.Width - 2 * Dialog.Pad, row.Height);
                f.Add(row);
                y += row.Height + Theme.Px(4);
            }
            f.BodyHeight(y + Dialog.Pad - Theme.Px(4));
            var close = Dialog.Button("Close", ButtonKind.Primary, DialogResult.OK);
            f.Buttons(new FlatButton[0], new[] { close });
            f.AcceptButton = f.CancelButton = close;
            return f;
        }

        public static void Shortcuts(IWin32Window owner) { Modal(ShortcutsDialog(), owner); }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OpenAsInfo { public string File, Class; public int Flags; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHOpenWithDialog(IntPtr parent, ref OpenAsInfo info);

        // Windows' own "Open with" dialog for a file, which then opens it (OAIF_ALLOW_REGISTRATION | OAIF_EXEC)
        public static void OpenWith(IWin32Window owner, string file)
        {
            if (!File.Exists(file)) { Error(owner, "The file isn't there anymore. It may have been moved or deleted."); return; }
            var info = new OpenAsInfo { File = file, Flags = 0x1 | 0x4 };
            SHOpenWithDialog(owner == null ? IntPtr.Zero : owner.Handle, ref info);
        }

        // ---- GLIDE_SHOTS: every dialog from made-up downloads (no engine calls, no Save dialogs), as PNGs

        public static void Shots(Form owner, string folder)
        {
            Dialog.Offscreen = true;
            const string longName = "رحلة إلى جبال الأطلس الكبير في فصل الشتاء — الحلقة الأولى.mp4"; // a name shows whole, on every screen
            try
            {
                var settings = new Settings { Dir = "C:\\Users\\PC\\Downloads", Connections = 8, MaxActive = 3, SpeedLimitKB = 500, Categories = true, Clipboard = true };
                var cats = new Dictionary<string, List<string>> { { "Video", new List<string> { "mp4" } }, { "Compressed", new List<string> { "iso", "zip" } } };
                Func<Download> http = () => new Download
                {
                    Id = "shot-http", Kind = "http", Status = "pending", Url = "https://releases.ubuntu.com/24.04/ubuntu-24.04.1-desktop-amd64.iso",
                    Probe = new Probe { Name = "ubuntu-24.04.1-desktop-amd64.iso", Category = "Compressed", Size = 6203355136 },
                };
                Func<Probe, Download> video = p => new Download { Id = "shot-yt", Kind = "video", Status = "pending", Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw", Probe = p };
                Func<string, string, string, long, FormatOption> opt = (key, group, label, size) => new FormatOption { Key = key, Group = group, Label = label, Size = size };

                var info = new FileInfoDialog(null, "shot-http");
                info.Fill(http(), settings);
                Shot(folder, "fileinfo-http", info);

                Func<string, Probe> listed = key => new Probe
                {
                    Name = "عزف سويمي 🎻 في بث.mp4", Category = "Video", Format = key, Options = new List<FormatOption>
                    {
                        opt("m4a:140", "Audio", "M4A", 3565158), opt("mp3", "Audio", "MP3", 456000),
                        opt("webm:248+251", "WEBM", "1080p", 98860390), opt("mp4:137+140", "MP4", "1080p", 126353408), opt("mp4:136+140", "MP4", "720p", 60353408),
                    },
                };
                info = new FileInfoDialog(null, "shot-yt");
                info.Fill(video(listed("mp4:137+140")), settings);
                Shot(folder, "fileinfo-youtube", info);

                info = new FileInfoDialog(null, "shot-yt"); // picked in the extension's format menu: no Format row
                var chosen = video(listed("mp4:136+140"));
                chosen.Format = "mp4:136+140";
                info.Fill(chosen, settings);
                Shot(folder, "fileinfo-chosen", info);

                info = new FileInfoDialog(null, "shot-yt"); // the same while the list is read: spinner in the Size row
                chosen = video(new Probe { Name = "Me at the zoo.mp4", Category = "Video", Listing = true, Options = new List<FormatOption>() });
                chosen.Format = "mp4:136+140";
                info.Fill(chosen, settings);
                Shot(folder, "fileinfo-chosen-listing", info);

                info = new FileInfoDialog(null, "shot-yt");
                info.Fill(video(new Probe { Name = "Me at the zoo.mp4", Category = "Video", Listing = true, Options = new List<FormatOption>() }), settings);
                Shot(folder, "fileinfo-listing", info);

                info = new FileInfoDialog(null, "shot-http");
                var failed = http();
                failed.Probe.Size = null;
                failed.Probe.Error = "HTTP 403";
                info.Fill(failed, settings);
                info.ShowError("Save As needs a full path with a file name, like C:\\Downloads\\file.zip");
                Shot(folder, "fileinfo-error", info);

                var running = new Download
                {
                    Id = "shot-run", Kind = "http", Status = "downloading", Url = "https://cdn.example.com/games/Last War Winter Storm.mp4", Name = longName,
                    Size = 734003200, Got = 330301440, Speed = 2516582, Connections = 3, Resumable = true, LimitKB = 500, WhenDone = "open",
                    // [start, end, pos, working] in MB: receiving, done, receiving, receiving, waiting
                    Segments = new[] { new[] { 0, 140, 126, 1 }, new[] { 140, 210, 210, 0 }, new[] { 210, 385, 294, 1 }, new[] { 385, 560, 420, 1 }, new[] { 560, 700, 560, 0 } }
                        .Select(m => new[] { m[0] * 1048576L, m[1] * 1048576L, m[2] * 1048576L, m[3] }).ToList(),
                };
                var speeds = Enumerable.Range(0, 60).Select(i => 9e6 + 4e6 * Math.Sin(i / 5.0) + (i % 7) * 3e5).ToList();
                var prog = new ProgressDialog(null, running.Id);
                prog.Fill(running, cats, speeds);
                Shot(folder, "progress-running", prog);
                foreach (var px in new[] { 16, 32 }) // the icon a progress window leaves in the tray
                    for (var f = -1; f < ProgressDialog.TrayFrames; f++)
                        using (var bmp = ProgressDialog.TrayBitmap(px, f)) bmp.Save(Path.Combine(folder, "tray-download-" + px + "-" + (f < 0 ? "paused" : "frame" + (f + 1)) + ".png"), ImageFormat.Png);

                var done = new Download
                {
                    Id = "shot-done", Kind = "http", Status = "done", Url = running.Url, Name = running.Name, Size = running.Size, Got = running.Size.Value,
                    File = "C:\\Users\\PC\\Downloads\\Video\\" + running.Name,
                };
                prog = new ProgressDialog(null, done.Id);
                prog.Fill(done, cats, new List<double>());
                Shot(folder, "progress-done", prog);

                var report = new Download
                {
                    Id = "shot-complete", Kind = "http", Status = "done", Notify = true, Url = "https://downloads.example.com/rapport-annuel-2025.pdf", Name = "Rapport annuel 2025.pdf",
                    Size = 2516582, Got = 2516582, File = "C:\\Users\\PC\\Downloads\\Documents\\Rapport annuel 2025.pdf",
                };
                prog = new ProgressDialog(null, report.Id);
                prog.Fill(report, cats, new List<double>());
                Shot(folder, "complete", prog);

                Shot(folder, "addlinks", new AddLinksDialog(null, "https://www.youtube.com/watch?v=jNQXAC9IVRw\r\nhttps://cdn.example.com/photos/img[001-120].jpg"));
                FlatCheck files;
                Shot(folder, "remove", RemoveDialog(3, true, out files));
                TextField field;
                Shot(folder, "newqueue", TextDialog("New queue", "Name of the new queue", "Weekend", 40, "Add", out field));
                Shot(folder, "about", AboutDialog());
                Shot(folder, "shortcuts", ShortcutsDialog());
                // Pair an iPhone, with a stand-in grid (the engine draws the real code)
                var fake = Enumerable.Range(0, 29).Select(y => Enumerable.Range(0, 29).Select(x => (x < 7 && y < 7) || (x > 21 && y < 7) || (x < 7 && y > 21) ? !(x % 6 == 1 || y % 6 == 1) || (x % 6 >= 2 && x % 6 <= 4 && y % 6 >= 2 && y % 6 <= 4) : (x * 7 + y * 13 + x * y) % 3 == 0).ToArray()).ToArray();
                Shot(folder, "phone-pair", new PhoneDialog(null, 0, fake, "http://192.168.1.23:9615/#pair=Ab3dEf6hIj9kLm2nOp5qRs"));
                Shot(folder, "error", ErrorDialog("Couldn't reach the server: the connection was reset. Check the link, then try again."));

                // issue 06: Download Later's popover, Link already in the list, Properties, the category editor
                var queues = new List<Queue> { new Queue { Id = Queue.Main, Name = "Main download queue" }, new Queue { Id = "night", Name = "Night" } };
                using (var pop = new QueuePopover(null, queues)) Shot(folder, "fileinfo-later", pop, pop.Root);
                FlatRadio[] radios;
                Shot(folder, "duplicate", DuplicateDialog(longName, "Paused · 30%", Core.Numbered(longName), out radios, out files));
                // the engine's default file types (engine.js CATEGORIES) and one custom category
                Func<string, List<string>> exts = t => t.Split(' ').ToList();
                var types = new Dictionary<string, List<string>> { { "Video", exts("mp4 mkv avi mov wmv flv webm m4v ts 3gp") }, { "Music", exts("mp3 wav flac aac m4a ogg wma opus") },
                    { "Programs", exts("exe msi apk dmg deb rpm appimage") }, { "Documents", exts("pdf doc docx xls xlsx ppt pptx txt epub csv") },
                    { "Compressed", exts("zip rar 7z tar gz bz2 xz iso") }, { "Courses", exts("srt vtt") } };
                var state = new State { Settings = settings, Categories = types, Queues = queues, Downloads = new List<Download>() };
                var photos = new Download
                {
                    Id = "shot-props", Kind = "http", Status = "paused", Url = "https://downloads.example.com/2026/winter-ep1.mp4", Name = longName, Size = 524288000, Got = 157286400,
                    File = "C:\\Users\\PC\\Downloads\\Video\\" + longName, Description = "Episode 1", Referer = "https://example.com/gallery",
                    Added = DateTimeOffset.Now.AddDays(-1).ToUnixTimeMilliseconds(), LastTry = DateTimeOffset.Now.AddHours(-2).ToUnixTimeMilliseconds(),
                };
                Shot(folder, "properties", new PropertiesDialog(null, photos, state));
                photos.Status = "downloading";
                photos.Queue = "night";
                Shot(folder, "properties-running", new PropertiesDialog(null, photos, state));
                var withTypes = new Settings { Dir = settings.Dir, Categories = true, Types = types, Folders = new Dictionary<string, string> { { "Courses", "D:\\Courses" } } };
                Shot(folder, "category-new", new CategoryDialog(null, withTypes, null));
                Shot(folder, "category-courses", new CategoryDialog(null, withTypes, "Courses"));
                Shot(folder, "category-builtin", new CategoryDialog(null, withTypes, "Documents"));

                // issue 08: Options, each page; Categories with a custom one selected
                var opts = new Settings { Dir = settings.Dir, Connections = 8, MaxActive = 3, SpeedLimitKB = 500, SpeedLimitOn = true, Categories = true, Clipboard = true, Startup = true,
                    ShowComplete = true, Duplicates = "ask", Types = types, Folders = new Dictionary<string, string> { { "Documents", "D:\\Work\\Docs" }, { "Courses", "D:\\Courses" } },
                    Sites = new List<Site> { new Site { Host = "files.example.com", Connections = 2 }, new Site { Host = "cdn.example.net", LimitKB = 800 } } };
                foreach (var page in new[] { OptionsDialog.General, OptionsDialog.Downloads, OptionsDialog.Sites, OptionsDialog.Phone, OptionsDialog.Categories, OptionsDialog.Queues })
                {
                    var o = new OptionsDialog(null, opts, page);
                    if (page == OptionsDialog.Categories) o.SelectCategory("Courses");
                    Shot(folder, "options-" + page, o);
                }
                var pub = new OptionsDialog(null, opts, OptionsDialog.Phone); // the network is Public: Windows' firewall blocks the phone
                pub.FillPhone(new RemoteInfo { Enabled = true, Listening = true, Port = 9615, Addresses = new List<string> { "192.168.1.23" }, PublicNetwork = "Ethernet" });
                Shot(folder, "options-phone-public", pub);

                // issue 07: the Scheduler, on a queue with every field set and three files waiting (a finished one is left out)
                var nightQueue = new Queue { Id = "night", Name = "Night", MaxActive = 2, StartAt = "02:00", Days = new List<int> { 1, 2, 3, 4, 5 }, StopAt = "07:00", Retries = 2, WhenDone = "shutdown" };
                Func<string, string, long, string, Download> waiting = (id, file, size, status) => new Download { Id = id, Kind = "http", Status = status, Queue = "night", Name = file, Size = size, Url = "https://downloads.example.com/" + file };
                var plan = new State
                {
                    Queues = new List<Queue> { new Queue { Id = Queue.Main, Name = "Main download queue", MaxActive = 1, Running = true }, nightQueue },
                    Downloads = new List<Download> { waiting("s1", longName, 1288490189, "scheduled"), waiting("s2", "done.zip", 1048576, "done"),
                        waiting("s3", "Ubuntu-24.04-desktop.iso", 6120328397, "scheduled"), waiting("s4", "setup-2.4.1.exe", 104857600, "scheduled") },
                };
                var sch = new SchedulerDialog(null);
                sch.Fill(plan);
                sch.Select("night");
                sch.SelectFile("s1");
                Shot(folder, "scheduler", sch);
                sch = new SchedulerDialog(null);
                sch.Fill(plan);
                Shot(folder, "scheduler-main", sch);
            }
            finally { Dialog.Offscreen = false; }
        }

        static void Shot(string folder, string name, Dialog f) { using (f) Shot(folder, name, f, f.Root); }

        // root: the part to draw (a dialog's Root has no title bar)
        static void Shot(string folder, string name, Form f, Control root)
        {
            f.ShowInTaskbar = false;
            f.Location = new Point(SystemInformation.VirtualScreen.Right + 400, SystemInformation.VirtualScreen.Top);
            f.Show();
            root.PerformLayout();
            using (var bmp = new Bitmap(root.Width, root.Height))
            {
                root.DrawToBitmap(bmp, new Rectangle(Point.Empty, root.Size));
                bmp.Save(Path.Combine(folder, name + (Theme.Dark ? "-dark" : "-light") + ".png"), ImageFormat.Png);
            }
        }
    }
}
