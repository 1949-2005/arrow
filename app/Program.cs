// The native app: one instance, the engine started when it is not running, then the main window.
//   --tray       start without showing the main window (Start with Windows)
//   --no-engine  never start the engine (tests)
//   --check      no UI: ping the engine, read /api/state, print the result, exit 0 or 1
// ARROW_SHOTS=<folder>: no mutex, tray or engine start; saves PNGs of the main window there and exits (visual review)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Arrow
{
    public static class Program
    {
        // Raised on a background thread when a second start asks this one to show itself
        public static event Action ShowRequested;
        public static string Name { get { return Application.ProductName; } } // brand.json's name, set at build time
        public static string Folder { get { return Path.GetDirectoryName(Application.ExecutablePath); } }

        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Api.AppName = Name;
            var api = new Api();
            if (args.Contains("--check")) return Check(api, args.Contains("--no-engine"));
            Theme.Init(); // SystemEvents raises the theme switch on the thread that first touched it: this one
            var shots = Environment.GetEnvironmentVariable("ARROW_SHOTS");
            if (!string.IsNullOrEmpty(shots))
            {
                Application.Run(new MainForm(api, true, shots));
                return 0;
            }

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + Name + "-show");
            bool first;
            using (new Mutex(true, @"Local\" + Name + "-ui", out first))
            {
                if (!first)
                {
                    AllowSetForegroundWindow(-1); // ASFW_ANY: lets the running instance come to the front
                    show.Set();
                    return 0;
                }
                var error = args.Contains("--no-engine") ? null : StartEngine(api);
                if (error != null)
                {
                    MessageBox.Show(error, Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                new Thread(() => { while (show.WaitOne()) { var h = ShowRequested; if (h != null) h(); } }) { IsBackground = true, Name = "show" }.Start();
                Application.Run(new MainForm(api, !args.Contains("--tray"), null));
                return 0;
            }
        }

        static int Check(Api api, bool noEngine)
        {
            var error = noEngine ? null : StartEngine(api);
            if (error == null)
            {
                try
                {
                    var s = State.From(api.Get("/api/state"));
                    Console.WriteLine("engine ok, " + s.Downloads.Count + " downloads");
                    return 0;
                }
                catch (ApiException e) { error = e.Message; }
            }
            Console.WriteLine(error);
            return 1;
        }

        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);

        // null when the engine answers, else the error to show. Blocks up to 15 s: call it off the UI thread.
        public static string StartEngine(Api api)
        {
            if (api.Ping()) return null;
            var engine = Path.Combine(Folder, Name + " Engine.exe");
            // Development: the engine from this folder's desktop.js, with node from PATH
            var psi = File.Exists(engine) ? new ProcessStartInfo(engine) : new ProcessStartInfo("node", "\"" + Path.Combine(Folder, "desktop.js") + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Folder;
            // A plain start, no job object: the engine keeps downloading after the window closes
            try { using (Process.Start(psi)) { } }
            catch (Exception e) { return Name + " could not start its engine: " + e.Message; }
            for (var t = Stopwatch.StartNew(); t.ElapsedMilliseconds < 15000; Thread.Sleep(200)) if (api.Ping()) return null;
            return Name + "'s engine did not start. Try again, or restart Windows if it keeps failing.";
        }
    }

    // The brand icon. .NET's Icon class draws the PNG images inside this .ico as noise; Windows' own loader reads them.
    public static class AppIcon
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);
        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
        const int WM_SETICON = 0x80, ICON_SMALL = 0, ICON_BIG = 1;

        // null when brand.json or the icon is missing
        public static Icon Load(int size)
        {
            try
            {
                var brand = Json.Parse(File.ReadAllText(Path.Combine(Program.Folder, "brand.json"))) as IDictionary<string, object>;
                var h = LoadImage(IntPtr.Zero, Path.GetFullPath(Path.Combine(Program.Folder, brand.Str("icon"))), IMAGE_ICON, size, size, LR_LOADFROMFILE);
                return h == IntPtr.Zero ? null : Icon.FromHandle(h);
            }
            catch (Exception) { return null; }
        }

        public static void Apply(Form form)
        {
            var small = Load(SystemInformation.SmallIconSize.Width);
            var big = Load(SystemInformation.IconSize.Width);
            if (small != null) SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_SMALL, small.Handle);
            if (big != null) SendMessage(form.Handle, WM_SETICON, (IntPtr)ICON_BIG, big.Handle);
        }
    }
}
