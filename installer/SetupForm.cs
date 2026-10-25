// The setup window, in Glide's own look (its Dialog, controls and theme, light or dark like Windows):
// install: Options → Progress → Done; uninstall: Confirm → Progress → Done. "Glide is running" and a winget failure
// stop on the way with Try again.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Glide.Setup
{
    public enum StepState { Waiting, Running, Done, Failed, Skipped }

    // One line of the progress page: a state icon, the step, and under it a detail (winget's last line, an error)
    public class Step : Painted
    {
        public readonly string Title;
        public StepState State;
        public string Detail = "";
        public float Angle;

        public Step(string title) { Title = title; Height = Theme.Px(46); }

        public void Set(StepState state, string detail) { State = state; Detail = detail ?? ""; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var line = Theme.Px(22);
            var icon = new Rectangle(0, 0, Theme.Px(20), line);
            switch (State)
            {
                case StepState.Done: Theme.DrawGlyph(g, Glyph.CheckCircle, icon, Theme.Done, 16); break;
                case StepState.Failed: Theme.DrawGlyph(g, Glyph.Error, icon, Theme.Err, 16); break;
                default: // a ring: waiting or skipped; with a turning arc while it runs
                    var s = Theme.Px(14);
                    var box = new Rectangle(icon.X + (icon.Width - s) / 2, (line - s) / 2, s, s);
                    var mode = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var track = new Pen(State == StepState.Running ? Theme.Bar : Theme.Faint, Theme.Px(State == StepState.Running ? 2 : 1.5))) g.DrawEllipse(track, box);
                    if (State == StepState.Running)
                        using (var pen = new Pen(Theme.Accent, Theme.Px(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, box, Angle, 90);
                    g.SmoothingMode = mode;
                    break;
            }
            var x = Theme.Px(20 + 10);
            Theme.DrawText(g, Title, State == StepState.Running ? Theme.Bold : Theme.Body, new Rectangle(x, 0, Width - x, line), State == StepState.Waiting ? Theme.Muted : Theme.Text);
            if (Detail != "")
                Theme.DrawText(g, Detail, Theme.Small, new Rectangle(x, line, Width - x, Height - line), State == StepState.Failed ? Theme.Err : Theme.Muted);
        }
    }

    public class SetupForm : Dialog
    {
        readonly bool uninstall;
        string dir;
        bool busy, toolsOk = true;
        readonly System.Windows.Forms.Timer spin = new System.Windows.Forms.Timer { Interval = 50 };
        static Bitmap logo;

        readonly TextField location = new TextField('\0', "");
        readonly FlatCheck desktop = new FlatCheck("Desktop shortcut") { Checked = true };
        readonly FlatCheck startup = new FlatCheck("Start " + Installer.Name + " with Windows") { Checked = true };
        readonly FlatCheck tools = new FlatCheck("Get yt-dlp, FFmpeg and Deno (needed for YouTube and video downloads)") { Checked = true };
        readonly FlatCheck removeData = new FlatCheck("Also remove the download list and settings");
        readonly SegmentBar bar = new SegmentBar();
        Step[] steps = new Step[0];
        bool toolsFound;

        public SetupForm(bool uninstall) : base(uninstall ? "Uninstall " + Installer.Name : Installer.Name + " Setup", 600)
        {
            this.uninstall = uninstall;
            ShowInTaskbar = true;
            ClientSize = new Size(Theme.Px(600), Theme.Px(440));
            StartPosition = FormStartPosition.CenterScreen;
            dir = uninstall ? Installer.InstalledDir : Installer.DefaultDir;
            spin.Tick += (s, e) => { foreach (var st in steps.Where(x => x.State == StepState.Running)) { st.Angle = (st.Angle + 12) % 360; st.Invalidate(); } };
            FormClosing += (s, e) => { if (busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };
            FormClosed += (s, e) => spin.Dispose();
            if (uninstall) ShowConfirm(); else ShowOptions(Installer.MissingTools().Count == 0);
        }

        // ---- the window's icon: the exe's own (csc's /win32icon), crisp at both sizes

        [DllImport("user32.dll")]
        static extern IntPtr LoadImage(IntPtr hInst, IntPtr name, uint type, int cx, int cy, uint load);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(IntPtr name);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var me = GetModuleHandle(IntPtr.Zero);
            foreach (var big in new[] { false, true })
            {
                var size = big ? SystemInformation.IconSize.Width : SystemInformation.SmallIconSize.Width;
                var h = LoadImage(me, (IntPtr)32512, 1, size, size, 0); // IMAGE_ICON; csc stores the icon under this id
                if (h != IntPtr.Zero) SendMessage(Handle, 0x80, (IntPtr)(big ? 1 : 0), h); // WM_SETICON
            }
        }

        static Bitmap Logo
        {
            get
            {
                if (logo == null)
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")) logo = s == null ? null : new Bitmap(s);
                return logo;
            }
        }

        // ---- page building

        int W { get { return ClientSize.Width - 2 * Pad; } }

        void Page()
        {
            Body.Controls.Clear();
            CancelButton = null;
            AcceptButton = null;
        }

        Caption Line(string text, Font font, Func<Color> color, int x, int y, int w, int lines)
        {
            var c = new Caption(font, color) { Text = text, MaxLines = lines };
            c.Bounds = new Rectangle(x, y, w, c.HeightFor(w));
            return Add(c);
        }

        // The logo tile, a big title and a muted line next to it; returns the y under them
        int Header(string title, string sub)
        {
            int s = Theme.Px(56), x = Pad + s + Theme.Px(16);
            Add(new Canvas((g, r) =>
            {
                if (Logo == null) return;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(Logo, r);
            }) { Bounds = new Rectangle(Pad, Pad, s, s), AccessibleName = Installer.Name, AccessibleRole = AccessibleRole.Graphic });
            var t = new Caption(Theme.Big, () => Theme.Text) { Text = title };
            var h = t.Height + (sub != null ? Theme.Px(2) + Theme.Body.Height : 0);
            t.Bounds = new Rectangle(x, Pad + (s - h) / 2, W + Pad - x, t.Height);
            Add(t);
            if (sub != null) Line(sub, Theme.Body, () => Theme.Muted, x, t.Bottom + Theme.Px(2), W + Pad - x, 1);
            return Pad + s + Theme.Px(24);
        }

        FlatButton Btn(string text, ButtonKind kind, Action click)
        {
            var b = new FlatButton(text, '\0', kind);
            if (click != null) b.Click += (s, e) => click();
            return b;
        }

        void Ui(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        // job off the UI thread, then on it: done(null) or done(the error)
        void Work(Action job, Action<Exception> done)
        {
            busy = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Exception error = null;
                try { job(); }
                catch (Exception e) { error = e; }
                Ui(() => { busy = false; done(error); });
            });
        }

        // ---- install: options

        void ShowOptions(bool found)
        {
            Page();
            toolsFound = found;
            var y = Header("Install " + Installer.Name, "A fast download manager for Windows");
            Line("Location", Theme.Body, () => Theme.Muted, Pad, y, W, 1);
            y += Theme.Px(26);
            var change = Btn("Change…", ButtonKind.Secondary, ChooseFolder);
            change.Size = new Size(Math.Max(Theme.Px(88), change.PreferredWidth), Theme.Px(34));
            change.Location = new Point(Pad + W - change.Width, y);
            location.Bounds = new Rectangle(Pad, y, W - change.Width - Theme.Px(8), Theme.Px(34));
            location.Text = dir;
            location.Box.AccessibleName = "Location";
            Add(location);
            Add(change);
            y += Theme.Px(34 + 20);
            foreach (var c in new[] { desktop, startup, tools })
            {
                c.Location = new Point(Pad, y);
                c.Width = Math.Min(c.PreferredWidth, W);
                Add(c);
                y += c.Height + Theme.Px(10);
            }
            tools.Enabled = !found;
            if (found)
            {
                tools.Checked = true;
                Line("Already installed", Theme.Body, () => Theme.Done, Pad + Theme.Px(28), y - Theme.Px(6), W, 1);
            }
            var install = Btn("Install", ButtonKind.Primary, Install);
            var cancel = Btn("Cancel", ButtonKind.Secondary, Close);
            Buttons(new FlatButton[0], new[] { cancel, install });
            AcceptButton = install;
            CancelButton = cancel;
            ActiveControl = install;
        }

        void ChooseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = "Install " + Installer.Name + " in:", SelectedPath = location.Text, ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                // a folder of its own, as Program Files apps have
                var picked = d.SelectedPath;
                location.Text = string.Equals(Path.GetFileName(picked.TrimEnd('\\')), Installer.Name, StringComparison.OrdinalIgnoreCase) ? picked : Path.Combine(picked, Installer.Name);
            }
        }

        void Install()
        {
            var at = location.Text.Trim();
            if (!Core.IsFullPath(at)) { location.Invalid = true; location.Box.Focus(); return; }
            dir = Path.GetFullPath(at);
            IfNotRunning(() =>
            {
                steps = new[] { new Step("Copying " + Installer.Name), new Step("Getting yt-dlp, FFmpeg and Deno"), new Step("Adding shortcuts") };
                ShowProgress("Installing " + Installer.Name);
                RunCopy();
            });
        }

        // ---- progress

        void ShowProgress(string title)
        {
            Page();
            var y = Header(title, dir);
            foreach (var s in steps)
            {
                s.Bounds = new Rectangle(Pad, y, W, s.Height);
                Add(s);
                y += s.Height + Theme.Px(4);
            }
            bar.Bounds = new Rectangle(Pad, y + Theme.Px(8), W, Theme.Px(8));
            bar.Set(new List<double[]> { new[] { 0.0, 0.0 } }, BarKind.Normal);
            Add(bar);
            Buttons(new FlatButton[0], new FlatButton[0]);
            spin.Start();
        }

        void Progress(double f) { bar.Set(new List<double[]> { new[] { 0.0, Math.Max(0, Math.Min(1, f)) } }, f >= 1 ? BarKind.Done : BarKind.Normal); }

        // A failed step: what happened (wrapped, under the bar), the store link when asked, and the footer's choices
        void Problem(string text, bool storeLink, params FlatButton[] right)
        {
            var y = bar.Bottom + Theme.Px(16);
            var c = Line(text, Theme.Body, () => Theme.Text, Pad, y, W, 6);
            if (storeLink)
            {
                var link = new FlatButton("App Installer in the Microsoft Store", Glyph.Link, ButtonKind.Ghost) { Pad = Theme.Px(4) };
                link.Size = new Size(link.PreferredWidth, Theme.Px(28));
                link.Location = new Point(Pad - Theme.Px(4), c.Bottom + Theme.Px(6));
                link.Click += (s, e) => OpenStore();
                Add(link);
            }
            Buttons(new FlatButton[0], right);
            AcceptButton = right.Last();
        }

        static void OpenStore()
        {
            try { Process.Start("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1").Dispose(); }
            catch (Exception) { Process.Start("https://apps.microsoft.com/detail/9nblggh4nns1").Dispose(); }
        }

        void RunCopy()
        {
            steps[0].Set(StepState.Running, null);
            Work(() => Installer.Copy(dir, f => Ui(() => Progress(f * 0.7))), e =>
            {
                if (e == null) { steps[0].Set(StepState.Done, null); RunTools(); return; }
                steps[0].Set(StepState.Failed, e.Message);
                Problem(Installer.Name + " couldn't be copied: " + e.Message, false, Btn("Close", ButtonKind.Secondary, Close), Btn("Try again", ButtonKind.Primary, Retry(RunCopy)));
            });
        }

        // Clears the problem's text and buttons, then runs again
        Action Retry(Action again)
        {
            return () =>
            {
                foreach (var c in Body.Controls.OfType<Control>().Where(c => c.Top > bar.Bottom).ToList()) Body.Controls.Remove(c);
                Buttons(new FlatButton[0], new FlatButton[0]);
                again();
            };
        }

        void RunTools()
        {
            if (!tools.Checked) { steps[1].Set(StepState.Skipped, "Skipped"); toolsOk = toolsFound; RunShortcuts(); return; }
            if (Installer.MissingTools().Count == 0) { steps[1].Set(StepState.Done, "Already installed"); RunShortcuts(); return; }
            steps[1].Set(StepState.Running, "Starting winget…");
            Progress(0.75);
            string result = null;
            Work(() => result = Installer.GetTools(l => Ui(() => { if (steps[1].State == StepState.Running) steps[1].Set(StepState.Running, l); })), e =>
            {
                if (e != null) result = e.Message;
                if (result == null) { steps[1].Set(StepState.Done, null); RunShortcuts(); return; }
                var missing = result == Installer.WingetMissing;
                steps[1].Set(StepState.Failed, missing ? "winget isn't on this PC" : result);
                Problem((missing ? "Windows' app installer (winget) isn't on this PC. Get App Installer from the Microsoft Store, then click Try again."
                        : "winget couldn't get the tools. Check your internet connection, then click Try again.") +
                    " Or click Skip: " + Installer.Name + " still installs, but YouTube and video downloads need yt-dlp, FFmpeg and Deno.", true,
                    Btn("Skip", ButtonKind.Secondary, Retry(() => { steps[1].Set(StepState.Skipped, "Skipped"); toolsOk = false; RunShortcuts(); })),
                    Btn("Try again", ButtonKind.Primary, Retry(RunTools)));
            });
        }

        void RunShortcuts()
        {
            steps[2].Set(StepState.Running, null);
            Progress(0.9);
            bool d = desktop.Checked, s = startup.Checked;
            Work(() => { Installer.Shortcuts(dir, d, s); Installer.Register(dir); }, e =>
            {
                if (e != null)
                {
                    steps[2].Set(StepState.Failed, e.Message);
                    Problem("The shortcuts couldn't be made: " + e.Message, false, Btn("Close", ButtonKind.Secondary, Close), Btn("Try again", ButtonKind.Primary, Retry(RunShortcuts)));
                    return;
                }
                steps[2].Set(StepState.Done, null);
                Progress(1);
                spin.Stop();
                ShowDone();
            });
        }

        // ---- install: done, and the browser extension

        void ShowDone()
        {
            Page();
            var y = Header(Installer.Name + " is installed", "Version " + Installer.Version);
            if (!toolsOk)
                y = Line("YouTube and video downloads need yt-dlp, FFmpeg and Deno. Run this setup again to get them.", Theme.Body, () => Theme.Warn, Pad, y - Theme.Px(8), W, 3).Bottom + Theme.Px(16);
            y = Line("Add " + Installer.Name + " to your browser", Theme.Bold, () => Theme.Text, Pad, y, W, 1).Bottom + Theme.Px(10);
            int n = Theme.Px(24), x = Pad + n;
            Func<string, string, int, int> numbered = (num, text, at) =>
            {
                Line(num, Theme.Bold, () => Theme.Muted, Pad, at, n, 1);
                return Line(text, Theme.Body, () => Theme.Text, x, at, W - n, 2).Bottom;
            };
            y = numbered("1", "Open your browser's extensions page", y) + Theme.Px(8);
            var browsers = Installer.Browsers();
            if (browsers.Count == 0) y = Line("brave://extensions, chrome://extensions or edge://extensions", Theme.Body, () => Theme.Muted, x, y, W - n, 1).Bottom;
            else
            {
                // Browsers drop chrome:// addresses given on their command line (only web pages open that way):
                // the button opens the browser and puts the address on the clipboard, to paste
                var hint = new Caption(Theme.Small, () => Theme.Muted) { Text = "Opens it and copies the address: paste it in its address bar, then press Enter." };
                var bx = x;
                foreach (var b in browsers)
                {
                    string name = b[0], exe = b[1], url = b[2];
                    var button = Btn(name, ButtonKind.Secondary, () =>
                    {
                        try
                        {
                            Clipboard.SetText(url);
                            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false }).Dispose();
                            hint.Text = "Copied " + url + ": paste it in " + name + "'s address bar, then press Enter.";
                        }
                        catch (Exception) { hint.Text = name + " didn't open. Open it yourself, then type " + url + " in its address bar."; }
                    });
                    button.Size = new Size(Math.Max(Theme.Px(88), button.PreferredWidth), Theme.Px(32));
                    button.Location = new Point(bx, y);
                    Tips.SetToolTip(button, "Opens " + name + " and copies " + url);
                    Add(button);
                    bx += button.Width + Theme.Px(8);
                }
                y += Theme.Px(32 + 6);
                hint.Bounds = new Rectangle(x, y, W - n, hint.Height);
                y = Add(hint).Bottom;
            }
            y = numbered("2", "Turn on Developer mode", y + Theme.Px(12)) + Theme.Px(12);
            y = numbered("3", "Click Load unpacked and choose this folder:", y) + Theme.Px(8);
            var folder = Path.Combine(dir, "extension");
            var copy = new FlatButton("Copy", Glyph.Copy, ButtonKind.Secondary);
            copy.Size = new Size(Math.Max(Theme.Px(88), copy.PreferredWidth), Theme.Px(34));
            copy.Location = new Point(Pad + W - copy.Width, y);
            copy.Click += (s, e) => { try { Clipboard.SetText(folder); copy.Text = "Copied"; } catch (ExternalException) { } };
            var path = new TextField('\0', "") { Bounds = new Rectangle(x, y, W - n - copy.Width - Theme.Px(8), Theme.Px(34)) };
            path.Box.ReadOnly = true;
            path.Box.Text = folder;
            path.Box.AccessibleName = "Extension folder";
            Add(path);
            Add(copy);
            var open = Btn("Open " + Installer.Name, ButtonKind.Primary, () => { try { Installer.StartApp(dir); } catch (Exception) { } Close(); });
            var close = Btn("Close", ButtonKind.Secondary, Close);
            Buttons(new FlatButton[0], new[] { close, open });
            AcceptButton = open;
            CancelButton = close;
            ActiveControl = open;
        }

        // ---- Glide is running

        void IfNotRunning(Action go)
        {
            if (Installer.Running(dir).Count == 0) go();
            else RunningPage(go);
        }

        void RunningPage(Action go)
        {
            Page();
            var y = Header((uninstall ? "Uninstall " : "Install ") + Installer.Name, dir);
            var note = Line(Installer.Name + " is running. Quit it from its icon next to the clock, then click Try again.", Theme.Body, () => Theme.Text, Pad, y, W, 4);
            var back = Btn("Back", ButtonKind.Secondary, () => { if (uninstall) ShowConfirm(); else ShowOptions(toolsFound); });
            var quit = Btn("Quit " + Installer.Name, ButtonKind.Secondary, null);
            var retry = Btn("Try again", ButtonKind.Primary, () => IfNotRunning(go));
            quit.Click += (s, e) =>
            {
                quit.Enabled = retry.Enabled = false;
                var ok = false;
                Work(() => ok = Installer.QuitApp(dir), err =>
                {
                    quit.Enabled = retry.Enabled = true;
                    if (ok) go();
                    else note.Text = Installer.Name + " didn't quit. Quit it from its icon next to the clock, then click Try again.";
                });
            };
            Buttons(new[] { back }, new[] { quit, retry });
            AcceptButton = retry;
        }

        // ---- uninstall

        void ShowConfirm()
        {
            Page();
            var y = Header("Uninstall " + Installer.Name, dir);
            y = Line("This removes " + Installer.Name + ", its shortcuts and its entry in Settings > Apps. Your downloaded files stay where they are.",
                Theme.Body, () => Theme.Text, Pad, y, W, 4).Bottom + Theme.Px(16);
            removeData.Location = new Point(Pad, y);
            removeData.Width = Math.Min(removeData.PreferredWidth, W);
            Add(removeData);
            var go = Btn("Uninstall", ButtonKind.Danger, Uninstall);
            var cancel = Btn("Cancel", ButtonKind.Secondary, Close);
            Buttons(new FlatButton[0], new[] { cancel, go });
            CancelButton = cancel;
            ActiveControl = cancel;
        }

        void Uninstall()
        {
            var data = removeData.Checked;
            IfNotRunning(() =>
            {
                steps = new[] { new Step("Removing shortcuts"), new Step("Removing " + Installer.Name) };
                ShowProgress("Uninstalling " + Installer.Name);
                steps[0].Set(StepState.Running, null);
                Work(() => Installer.RemoveShortcuts(dir), e =>
                {
                    steps[0].Set(e == null ? StepState.Done : StepState.Failed, e == null ? null : e.Message);
                    steps[1].Set(StepState.Running, null);
                    Progress(0.3);
                    Work(() => { Installer.RemoveFiles(dir, data, f => Ui(() => Progress(0.3 + 0.7 * f))); Installer.Unregister(); }, e2 =>
                    {
                        spin.Stop();
                        if (e2 != null)
                        {
                            steps[1].Set(StepState.Failed, e2.Message);
                            Problem(Installer.Name + " couldn't be removed completely: " + e2.Message, false, Btn("Close", ButtonKind.Secondary, Close));
                            return;
                        }
                        steps[1].Set(StepState.Done, null);
                        Progress(1);
                        ShowUninstalled(data);
                    });
                });
            });
        }

        void ShowUninstalled(bool data)
        {
            Page();
            var y = Header(Installer.Name + " is uninstalled", null);
            var text = "Your downloaded files are where they were.";
            if (!data && File.Exists(Path.Combine(dir, "state.json"))) text += " The download list and settings stay in " + Path.Combine(dir, "state.json") + ", for a later install.";
            Line(text, Theme.Body, () => Theme.Text, Pad, y, W, 6);
            var close = Btn("Close", ButtonKind.Primary, Close);
            Buttons(new FlatButton[0], new[] { close });
            AcceptButton = CancelButton = close;
        }

        // ---- GLIDE_SETUP_SHOTS: every page from made-up states (nothing installed, nothing run), as PNGs

        public static void Shots(string folder)
        {
            Directory.CreateDirectory(folder);
            Dialog.Offscreen = true;
            var at = @"C:\Users\You\AppData\Local\Programs\" + Installer.Name;
            Action<string, bool, Action<SetupForm>> shot = (name, un, stage) =>
            {
                using (var f = new SetupForm(un))
                {
                    f.dir = at;
                    stage(f);
                    f.ShowInTaskbar = false;
                    f.StartPosition = FormStartPosition.Manual;
                    f.Location = new Point(SystemInformation.VirtualScreen.Right + 400, SystemInformation.VirtualScreen.Top);
                    f.Show();
                    f.spin.Stop();
                    f.Root.PerformLayout();
                    using (var bmp = new Bitmap(f.Root.Width, f.Root.Height))
                    {
                        f.Root.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Root.Size));
                        bmp.Save(Path.Combine(folder, name + (Theme.Dark ? "-dark" : "-light") + ".png"), ImageFormat.Png);
                    }
                    f.busy = false;
                }
            };
            Func<SetupForm, Step[]> installSteps = f => f.steps = new[] { new Step("Copying " + Installer.Name), new Step("Getting yt-dlp, FFmpeg and Deno"), new Step("Adding shortcuts") };
            shot("setup-options", false, f => f.ShowOptions(false));
            shot("setup-options-tools-found", false, f => f.ShowOptions(true));
            shot("setup-progress", false, f =>
            {
                installSteps(f);
                f.ShowProgress("Installing " + Installer.Name);
                f.steps[0].Set(StepState.Done, null);
                f.steps[1].Set(StepState.Running, "Downloading https://github.com/yt-dlp/yt-dlp/releases/download/2026.10.21/yt-dlp.exe  ██████▒▒▒▒  12.0 MB / 18.4 MB");
                f.steps[1].Angle = 200;
                f.Progress(0.78);
            });
            shot("setup-winget-missing", false, f =>
            {
                installSteps(f);
                f.ShowProgress("Installing " + Installer.Name);
                f.steps[0].Set(StepState.Done, null);
                f.Progress(0.75);
                f.steps[1].Set(StepState.Failed, "winget isn't on this PC");
                f.Problem("Windows' app installer (winget) isn't on this PC. Get App Installer from the Microsoft Store, then click Try again. Or click Skip: " +
                    Installer.Name + " still installs, but YouTube and video downloads need yt-dlp, FFmpeg and Deno.", true,
                    new FlatButton("Skip", '\0', ButtonKind.Secondary), new FlatButton("Try again", '\0', ButtonKind.Primary));
            });
            shot("setup-winget-failed", false, f =>
            {
                installSteps(f);
                f.ShowProgress("Installing " + Installer.Name);
                f.steps[0].Set(StepState.Done, null);
                f.Progress(0.75);
                f.steps[1].Set(StepState.Failed, "winget couldn't get yt-dlp: An unexpected error occurred while executing the command: 0x80072ee7");
                f.Problem("winget couldn't get the tools. Check your internet connection, then click Try again. Or click Skip: " +
                    Installer.Name + " still installs, but YouTube and video downloads need yt-dlp, FFmpeg and Deno.", true,
                    new FlatButton("Skip", '\0', ButtonKind.Secondary), new FlatButton("Try again", '\0', ButtonKind.Primary));
            });
            shot("setup-running", false, f => f.RunningPage(() => { }));
            shot("setup-done", false, f => f.ShowDone());
            shot("setup-done-no-tools", false, f => { f.toolsOk = false; f.ShowDone(); });
            shot("uninstall-confirm", true, f => f.ShowConfirm());
            shot("uninstall-running", true, f => f.RunningPage(() => { }));
            shot("uninstall-progress", true, f =>
            {
                f.steps = new[] { new Step("Removing shortcuts"), new Step("Removing " + Installer.Name) };
                f.ShowProgress("Uninstalling " + Installer.Name);
                f.steps[0].Set(StepState.Done, null);
                f.steps[1].Set(StepState.Running, null);
                f.steps[1].Angle = 120;
                f.Progress(0.55);
            });
            shot("uninstall-done", true, f => f.ShowUninstalled(true));
            Dialog.Offscreen = false;
        }
    }
}
