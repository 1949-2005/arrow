// The dialogs (docs/NATIVE_UI.md "Dialogs"): the static API the main window calls, the themed Dialog base, the small
// modal dialogs (Confirm, Remove, Change link, Error, About), and ARROW_SHOTS pictures of every dialog.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Arrow
{
    // A themed dialog: Body (inspector token) over Foot (top token, top hairline, buttons at the left and the right).
    // Esc closes it when it has no CancelButton.
    public class Dialog : Form
    {
        public static bool Offscreen; // ARROW_SHOTS: shown beyond the screens, never activated
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

        // Centered on the owner window, or on the screen when it is hidden or minimized
        public void Center(IWin32Window owner)
        {
            var c = owner as Control;
            var f = c as Form;
            var shown = c != null && c.Visible && (f == null || f.WindowState != FormWindowState.Minimized);
            var area = shown ? (f != null ? f.Bounds : c.RectangleToScreen(c.ClientRectangle)) : Screen.PrimaryScreen.WorkingArea;
            var wa = Screen.FromRectangle(area).WorkingArea;
            var x = area.X + (area.Width - Width) / 2;
            var y = area.Y + (area.Height - Height) / 2;
            Location = new Point(Math.Max(wa.Left, Math.Min(x, wa.Right - Width)), Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height)));
        }
    }

    public static class Dialogs
    {
        static readonly Dictionary<string, FileInfoDialog> infos = new Dictionary<string, FileInfoDialog>();
        static readonly Dictionary<string, ProgressDialog> progress = new Dictionary<string, ProgressDialog>();
        static readonly Dictionary<string, List<double>> samples = new Dictionary<string, List<double>>();
        static State last;
        const int Samples = 86; // 60 s of 700 ms polls

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
                f.Location = info.Location;
            }
            info.Close();
            Front(f);
        }

        // Called on every poll: open Download File Info and progress windows show the new state
        public static void Update(State s)
        {
            last = s;
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
            foreach (var f in infos.Values.ToList()) f.Fill(Find(s, f.Id), s.Settings);
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

        static void Front(Form f)
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

        public static void Options(Api api, IWin32Window owner, State state, bool focusSchedule)
        {
            Modal(new OptionsDialog(api, state.Settings, focusSchedule), owner);
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

        static Dialog ChangeLinkDialog(string current, out TextField field)
        {
            var f = new Dialog("Change link", 460);
            var y = f.Paragraph(new Caption(Theme.Body, () => Theme.Text) { Text = "New link for the same file" }, Dialog.Pad);
            var box = field = new TextField(Glyph.Link, "https://");
            field.Box.AccessibleName = "New link";
            field.Box.Text = current ?? "";
            field.Bounds = new Rectangle(Dialog.Pad, y + Theme.Px(8), f.ClientSize.Width - 2 * Dialog.Pad, Theme.Px(32));
            f.Add(field);
            f.BodyHeight(field.Bottom + Dialog.Pad);
            var save = Dialog.Button("Save", ButtonKind.Primary, DialogResult.None);
            var cancel = Dialog.Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
            save.Click += (s, e) =>
            {
                if (box.Text.Trim() == "") { box.Invalid = true; box.Box.Focus(); return; }
                f.DialogResult = DialogResult.OK;
            };
            box.Box.TextChanged += (s, e) => box.Invalid = false;
            f.Buttons(new FlatButton[0], new[] { cancel, save });
            f.AcceptButton = save;
            f.CancelButton = cancel;
            f.ActiveControl = field.Box;
            return f;
        }

        // null = cancelled
        public static string ChangeLink(IWin32Window owner, string current)
        {
            TextField field;
            var f = ChangeLinkDialog(current, out field);
            return Modal(f, owner) == DialogResult.OK ? field.Text.Trim() : null;
        }

        public static void Error(IWin32Window owner, string text)
        {
            var f = new Dialog(Program.Name, 420);
            f.BodyHeight(f.Paragraph(new Caption(Theme.Body, () => Theme.Text) { Text = text }, Dialog.Pad) + Dialog.Pad);
            var ok = Dialog.Button("OK", ButtonKind.Primary, DialogResult.OK);
            f.Buttons(new FlatButton[0], new[] { ok });
            f.AcceptButton = f.CancelButton = ok;
            Modal(f, owner);
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

        // ---- ARROW_SHOTS: every dialog from made-up downloads (no engine calls, no Save dialogs), as PNGs

        public static void Shots(Form owner, string folder)
        {
            Dialog.Offscreen = true;
            try
            {
                var settings = new Settings { Dir = "C:\\Users\\PC\\Downloads", Connections = 8, MaxActive = 3, SpeedLimitKB = 500, ScheduleAt = "02:00", Categories = true, Clipboard = true };
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
                    Id = "shot-run", Kind = "http", Status = "downloading", Url = "https://cdn.example.com/games/Last War Winter Storm.mp4", Name = "(1) MT NEWS on X 🚨🚨 الخريطة الجديدة.mp4",
                    Size = 2265595904, Got = 1021313024, Speed = 12976128, Connections = 8, Resumable = true,
                    Segments = Enumerable.Range(0, 8).Select(i => new[] { i * 283199488L, (i + 1) * 283199488L, i * 283199488L + new[] { 270000000L, 180000000, 90000000, 150000000, 60000000, 120000000, 40000000, 111313024 }[i] }).ToList(),
                };
                var speeds = Enumerable.Range(0, 60).Select(i => 9e6 + 4e6 * Math.Sin(i / 5.0) + (i % 7) * 3e5).ToList();
                var prog = new ProgressDialog(null, running.Id);
                prog.Fill(running, cats, speeds);
                Shot(folder, "progress-running", prog);

                var done = new Download
                {
                    Id = "shot-done", Kind = "http", Status = "done", Url = running.Url, Name = running.Name, Size = running.Size, Got = running.Size.Value,
                    File = "C:\\Users\\PC\\Downloads\\Video\\" + running.Name,
                };
                prog = new ProgressDialog(null, done.Id);
                prog.Fill(done, cats, new List<double>());
                Shot(folder, "progress-done", prog);

                Shot(folder, "addlinks", new AddLinksDialog(null, "https://www.youtube.com/watch?v=jNQXAC9IVRw\r\nhttps://cdn.example.com/photos/img[001-120].jpg"));
                Shot(folder, "options", new OptionsDialog(null, settings, false));
                FlatCheck files;
                Shot(folder, "remove", RemoveDialog(3, true, out files));
                TextField field;
                Shot(folder, "changelink", ChangeLinkDialog("https://cdn.example.com/games/Last War Winter Storm.mp4?token=expired", out field));
                Shot(folder, "about", AboutDialog());
            }
            finally { Dialog.Offscreen = false; }
        }

        static void Shot(string folder, string name, Dialog f)
        {
            using (f)
            {
                f.ShowInTaskbar = false;
                f.Location = new Point(SystemInformation.VirtualScreen.Right + 400, SystemInformation.VirtualScreen.Top);
                f.Show();
                f.Root.PerformLayout();
                using (var bmp = new Bitmap(f.Root.Width, f.Root.Height))
                {
                    f.Root.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Root.Size));
                    bmp.Save(Path.Combine(folder, name + (Theme.Dark ? "-dark" : "-light") + ".png"), ImageFormat.Png);
                }
            }
        }
    }
}
