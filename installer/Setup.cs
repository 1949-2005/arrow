// GlideSetup.exe (built by build-installer.js): installs Glide for the user's account from the zip it carries, and
// uninstalls it again as "Uninstall Glide.exe" (a copy of itself in the install folder). No admin rights.
//   (no arguments)      the setup window (SetupForm)
//   --uninstall         uninstall mode (also when the exe's name starts with "Uninstall")
//   --quiet             no window: install (--no-desktop, --no-startup, --no-tools) or uninstall (--remove-data);
//                       --quit-app quits a running Glide first; --start opens it after installing (its own update).
//                       Exit 0 ok, 1 error, 2 Glide is running, 3 tools missing.
// Test hooks, so nothing touches the real system: GLIDE_SETUP_DIR (install folder), GLIDE_SETUP_PROGRAMS / _DESKTOP /
// _STARTUP (shortcut folders), GLIDE_SETUP_REGKEY (HKCU key holding the Uninstall entry), GLIDE_SETUP_WINGET (the winget
// command), FASTDL_PORT (the engine to quit); GLIDE_SETUP_SHOTS=<folder> saves PNGs of every page and exits.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Glide.Setup
{
    public static class SetupProgram
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var uninstall = args.Contains("--uninstall") || Path.GetFileName(Application.ExecutablePath).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase);
            if (args.Contains("--quiet")) return Quiet(uninstall, args);
            Theme.Init();
            var shots = Environment.GetEnvironmentVariable("GLIDE_SETUP_SHOTS");
            if (!string.IsNullOrEmpty(shots)) { SetupForm.Shots(shots); return 0; }
            Application.Run(new SetupForm(uninstall));
            return 0;
        }

        static int Quiet(bool uninstall, string[] args)
        {
            try
            {
                var dir = uninstall ? Installer.InstalledDir : Installer.DefaultDir;
                if (Installer.Running(dir).Count > 0 && !(args.Contains("--quit-app") && Installer.QuitApp(dir))) { Console.Error.WriteLine(Installer.Name + " is running."); return 2; }
                if (uninstall)
                {
                    Installer.RemoveShortcuts(dir);
                    Installer.RemoveFiles(dir, args.Contains("--remove-data"), null);
                    Installer.Unregister();
                    return 0;
                }
                Installer.Copy(dir, null);
                var tools = args.Contains("--no-tools") || Installer.GetTools(null) == null;
                Installer.Shortcuts(dir, !args.Contains("--no-desktop"), !args.Contains("--no-startup"));
                Installer.Register(dir);
                // the app's own update; GLIDE_SETUP_START_ARGS: tests start it with --check-update instead of its window
                if (args.Contains("--start"))
                    Process.Start(new ProcessStartInfo(Path.Combine(dir, Installer.Name + ".exe"), Environment.GetEnvironmentVariable("GLIDE_SETUP_START_ARGS") ?? "") { WorkingDirectory = dir, UseShellExecute = true }); // shell: inherits none of our handles
                return tools ? 0 : 3;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }
    }

    // The install and uninstall steps. They run off the UI thread and throw with messages meant for the user.
    public static class Installer
    {
        public const string Publisher = "1949-2005";
        public static string Name { get { return Application.ProductName; } } // brand.json's name, set at build time
        public static string Version { get { return Application.ProductVersion; } }
        public static string AppExe(string dir) { return Path.Combine(dir, Name + ".exe"); }
        public static string EngineExe(string dir) { return Path.Combine(dir, Name + " Engine.exe"); }
        public static string Uninstaller(string dir) { return Path.Combine(dir, "Uninstall " + Name + ".exe"); }

        static string Env(string key) { var v = Environment.GetEnvironmentVariable(key); return string.IsNullOrEmpty(v) ? null : v; }
        static string LocalAppData { get { return Env("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData); } }
        static string Programs { get { return Env("GLIDE_SETUP_PROGRAMS") ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs); } }
        static string Desktop { get { return Env("GLIDE_SETUP_DESKTOP") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory); } }
        static string Startup { get { return Env("GLIDE_SETUP_STARTUP") ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup); } }
        static string UninstallRoot { get { return Env("GLIDE_SETUP_REGKEY") ?? @"Software\Microsoft\Windows\CurrentVersion\Uninstall"; } }
        static string UninstallKey { get { return UninstallRoot + "\\" + Name; } }

        static string[] Links(string dir) { return new[] { Path.Combine(Programs, Name + ".lnk"), Path.Combine(Desktop, Name + ".lnk"), Path.Combine(Startup, Name + ".lnk") }; }

        // An earlier install's folder (upgrades go there), else %LOCALAPPDATA%\Programs\<name>
        public static string DefaultDir
        {
            get
            {
                if (Env("GLIDE_SETUP_DIR") != null) return Env("GLIDE_SETUP_DIR");
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                {
                    var at = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(at)) return at;
                }
                return Path.Combine(LocalAppData, "Programs", Name);
            }
        }

        // Uninstall: the folder the uninstaller runs from
        public static string InstalledDir
        {
            get
            {
                if (Env("GLIDE_SETUP_DIR") != null) return Env("GLIDE_SETUP_DIR");
                var self = Application.ExecutablePath;
                return Path.GetFileName(self).StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(self) : DefaultDir;
            }
        }

        static ZipArchive Payload() { return new ZipArchive(Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"), ZipArchiveMode.Read); }

        // ---- Glide running from dir: Windows can't replace or delete a running exe

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);

        static string ImagePath(Process p)
        {
            var h = OpenProcess(0x1000, false, p.Id); // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var b = new StringBuilder(1024);
                var n = b.Capacity;
                return QueryFullProcessImageName(h, 0, b, ref n) ? b.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        static bool Same(string a, string b) { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        // Glide's own processes started from dir (another copy elsewhere doesn't lock these files)
        public static List<Process> Running(string dir)
        {
            var found = new List<Process>();
            foreach (var n in new[] { Name, Name + " Engine" })
                foreach (var p in Process.GetProcessesByName(n))
                {
                    var image = ImagePath(p);
                    if (image != null && Same(Path.GetDirectoryName(image), dir)) found.Add(p); else p.Dispose();
                }
            return found;
        }

        // The engine's clean quit (POST /api/quit saves the download list), then the window, which keeps nothing unsaved
        public static bool QuitApp(string dir)
        {
            int port;
            if (!int.TryParse(Env("FASTDL_PORT"), out port)) port = 9614;
            try
            {
                var r = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/quit");
                r.Method = "POST";
                r.Proxy = null;
                r.Timeout = 5000;
                r.ContentType = "application/json";
                using (var s = r.GetRequestStream()) s.Write(new byte[] { 0x7b, 0x7d }, 0, 2); // {}
                r.GetResponse().Dispose();
            }
            catch (WebException) { } // no engine on that port: only the window runs
            for (var t = Stopwatch.StartNew(); t.ElapsedMilliseconds < 10000; Thread.Sleep(200))
            {
                var left = Running(dir);
                if (left.Count == 0) return true;
                if (left.All(p => !p.ProcessName.EndsWith(" Engine"))) // the engine is gone: the window waits for it in vain
                    foreach (var p in left) { try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { } }
            }
            return Running(dir).Count == 0;
        }

        // ---- install

        // The payload into dir: files are replaced, state.json (the download list and settings) is not in it, so it stays
        public static void Copy(string dir, Action<double> progress)
        {
            Directory.CreateDirectory(dir);
            using (var zip = Payload())
            {
                double total = Math.Max(1, zip.Entries.Sum(e => e.Length)), done = 0;
                var buffer = new byte[1 << 20];
                foreach (var e in zip.Entries)
                {
                    var file = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!file.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("bad entry " + e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var from = e.Open())
                    using (var to = new FileStream(file, FileMode.Create, FileAccess.Write))
                        for (int n; (n = from.Read(buffer, 0, buffer.Length)) > 0; )
                        {
                            to.Write(buffer, 0, n);
                            done += n;
                            if (progress != null) progress(done / total);
                        }
                    File.SetLastWriteTimeUtc(file, e.LastWriteTime.UtcDateTime);
                }
            }
            var me = Application.ExecutablePath;
            if (!Same(me, Uninstaller(dir))) File.Copy(me, Uninstaller(dir), true);
        }

        public static void Shortcuts(string dir, bool desktop, bool startup)
        {
            var links = Links(dir);
            Shortcut(links[0], AppExe(dir), "");
            if (desktop) Shortcut(links[1], AppExe(dir), ""); else RemoveLink(links[1], dir);
            if (startup) Shortcut(links[2], AppExe(dir), "--tray"); else RemoveLink(links[2], dir); // the app's own "Start with Windows" shortcut
        }

        // Settings > Apps
        public static void Register(string dir)
        {
            var size = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", Name);
                k.SetValue("DisplayIcon", AppExe(dir));
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", Publisher);
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + Uninstaller(dir) + "\"");
                k.SetValue("QuietUninstallString", "\"" + Uninstaller(dir) + "\" --quiet");
                k.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        // ---- yt-dlp, FFmpeg and Deno: found as ytdlp.js finds them (WinGet's links and packages, PATH). A yt-dlp only on
        // PATH doesn't count: other apps (Parabolic…) put an old one there, YouTube refuses it, and winget's copy comes first.

        static IEnumerable<string> OnPath(string exe)
        {
            return (Env("PATH") ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => { try { return Path.Combine(p.Trim('"'), exe); } catch (ArgumentException) { return null; } }).Where(p => p != null);
        }
        static string WinGet(params string[] p) { return Path.Combine(new[] { LocalAppData, "Microsoft", "WinGet" }.Concat(p).ToArray()); }
        static IEnumerable<string> Subdirs(string dir, string prefix)
        {
            try { return Directory.GetDirectories(dir, prefix + "*"); }
            catch (Exception) { return new string[0]; }
        }

        public static readonly string[] Tools = { "yt-dlp", "FFmpeg", "Deno" };
        static readonly string[] Ids = { "yt-dlp.yt-dlp", "yt-dlp.FFmpeg", "DenoLand.Deno" }; // yt-dlp.yt-dlp brings the other two

        public static bool Found(string tool)
        {
            IEnumerable<string> places;
            switch (tool)
            {
                case "yt-dlp":
                    places = new[] { Env("FASTDL_YTDLP"), WinGet("Links", "yt-dlp.exe") }
                        .Concat(Subdirs(WinGet("Packages"), "yt-dlp.yt-dlp_").Select(p => Path.Combine(p, "yt-dlp.exe")));
                    break;
                case "FFmpeg":
                    if (Env("FASTDL_FFMPEG") != null && (File.Exists(Env("FASTDL_FFMPEG")) || Directory.Exists(Env("FASTDL_FFMPEG")))) return true;
                    places = OnPath("ffmpeg.exe").Concat(Subdirs(WinGet("Packages"), "yt-dlp.FFmpeg_").SelectMany(p => Subdirs(p, "ffmpeg-")).Select(p => Path.Combine(p, "bin", "ffmpeg.exe")));
                    break;
                default:
                    places = OnPath("deno.exe").Concat(new[] { WinGet("Links", "deno.exe") }).Concat(Subdirs(WinGet("Packages"), "DenoLand.Deno_").Select(p => Path.Combine(p, "deno.exe")));
                    break;
            }
            return places.Any(f => f != null && File.Exists(f));
        }

        public static List<string> MissingTools() { return Tools.Where(t => !Found(t)).ToList(); }

        // winget install for each missing tool; null when all are there, else what went wrong (WingetMissing: no winget)
        public const string WingetMissing = "winget";
        public static string GetTools(Action<string> line)
        {
            for (var i = 0; i < Tools.Length; i++)
            {
                if (Found(Tools[i])) continue;
                int code;
                var last = "";
                try { code = Winget(Ids[i], l => { last = l; if (line != null) line(l); }); }
                catch (Win32Exception) { return WingetMissing; }
                if (!Found(Tools[i])) return "winget couldn't get " + Tools[i] + (last != "" ? ": " + last : " (exit code " + code + ")");
            }
            return null;
        }

        static int Winget(string id, Action<string> line)
        {
            var psi = new ProcessStartInfo(Env("GLIDE_SETUP_WINGET") ?? "winget",
                "install -e --id " + id + " --accept-package-agreements --accept-source-agreements --disable-interactivity")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            using (var p = Process.Start(psi))
            {
                p.ErrorDataReceived += (s, e) => Report(e.Data, line);
                p.BeginErrorReadLine();
                // winget redraws its progress on one line with \r: each piece is a line to show
                var b = new StringBuilder();
                for (int c; (c = p.StandardOutput.Read()) >= 0; )
                {
                    if (c == '\r' || c == '\n') { Report(b.ToString(), line); b.Clear(); }
                    else b.Append((char)c);
                }
                Report(b.ToString(), line);
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        static void Report(string s, Action<string> line)
        {
            s = (s ?? "").Trim();
            if (s.Trim('-', '\\', '|', '/', ' ') == "") return; // the spinner
            line(s);
        }

        // ---- uninstall

        // Only shortcuts to this install's exe: another copy's shortcuts with the same name stay
        public static void RemoveShortcuts(string dir) { foreach (var l in Links(dir)) RemoveLink(l, dir); }

        static void RemoveLink(string link, string dir)
        {
            if (!File.Exists(link)) return;
            var target = ShortcutTarget(link);
            if (target != null && Same(target, AppExe(dir))) File.Delete(link);
        }

        // The files the setup put there (its zip's list) and the uninstaller; state.json only when asked. Never
        // anything else: a download folder inside the install folder keeps its files.
        public static void RemoveFiles(string dir, bool data, Action<double> progress)
        {
            if (!Directory.Exists(dir)) return;
            List<string> files;
            using (var zip = Payload()) files = zip.Entries.Select(e => e.FullName).ToList();
            files.Add("log.txt"); // what the app and engine wrote: not the user's data
            if (data) files.AddRange(new[] { "state.json", "state.json.tmp" });
            for (var i = 0; i < files.Count; i++)
            {
                var f = Path.Combine(dir, files[i]);
                if (File.Exists(f)) File.Delete(f);
                if (progress != null) progress((i + 1.0) / files.Count);
            }
            // the folders the zip made, deepest first, when empty
            var root = Path.GetFullPath(dir).TrimEnd('\\');
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                for (var d = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, f))); d.Length > root.Length; d = Path.GetDirectoryName(d)) folders.Add(d);
            foreach (var d in folders.OrderByDescending(d => d.Length)) if (Empty(d)) Directory.Delete(d);
            var me = Application.ExecutablePath;
            if (Same(me, Uninstaller(dir))) DeleteLater(me, dir);
            else if (File.Exists(Uninstaller(dir))) File.Delete(Uninstaller(dir));
            if (Empty(dir)) Directory.Delete(dir);
        }

        static bool Empty(string d) { return Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any(); }

        // A running exe can't delete itself: cmd does it two seconds after this process ends, then the folder if empty
        static void DeleteLater(string exe, string dir)
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + exe + "\" & rmdir \"" + dir + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() }).Dispose();
        }

        public static void Unregister() { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); }

        // ---- shortcuts through WScript.Shell, as install.js makes them: the exe, its arguments, its folder

        static object Call(object o, string member, BindingFlags how, params object[] args) { return o.GetType().InvokeMember(member, how, null, o, args); }

        static void Shortcut(string link, string target, string args)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                var s = Call(shell, "CreateShortcut", BindingFlags.InvokeMethod, link);
                Call(s, "TargetPath", BindingFlags.SetProperty, target);
                Call(s, "Arguments", BindingFlags.SetProperty, args);
                Call(s, "WorkingDirectory", BindingFlags.SetProperty, Path.GetDirectoryName(target));
                Call(s, "Save", BindingFlags.InvokeMethod);
                Marshal.ReleaseComObject(s);
            }
            finally { Marshal.ReleaseComObject(shell); }
        }

        static string ShortcutTarget(string link)
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            try
            {
                var s = Call(shell, "CreateShortcut", BindingFlags.InvokeMethod, link);
                var t = Call(s, "TargetPath", BindingFlags.GetProperty) as string;
                Marshal.ReleaseComObject(s);
                return string.IsNullOrEmpty(t) ? null : t;
            }
            catch (Exception) { return null; }
            finally { Marshal.ReleaseComObject(shell); }
        }

        // ---- after the install

        // Glide started with today's PATH, so yt-dlp finds the Deno winget just put there
        public static void StartApp(string dir)
        {
            var psi = new ProcessStartInfo(AppExe(dir)) { UseShellExecute = false, WorkingDirectory = dir };
            var user = Registry.GetValue(@"HKEY_CURRENT_USER\Environment", "Path", "") as string;
            var machine = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", "Path", "") as string;
            var path = Environment.ExpandEnvironmentVariables((machine ?? "") + ";" + (user ?? ""));
            if (path.Trim(';') != "") psi.EnvironmentVariables["PATH"] = path;
            Process.Start(psi).Dispose();
        }

        // The installed browsers that can load the extension: name, exe, extensions page. Installed = registered in App
        // Paths, as their installers do (a removed Edge can leave a msedge.exe that no longer starts).
        public static List<string[]> Browsers()
        {
            var list = new List<string[]>();
            foreach (var b in new[] { new[] { "Brave", "brave.exe", "brave://extensions" }, new[] { "Chrome", "chrome.exe", "chrome://extensions" }, new[] { "Edge", "msedge.exe", "edge://extensions" } })
            {
                var exe = new[] { "HKEY_CURRENT_USER", "HKEY_LOCAL_MACHINE" }
                    .Select(hive => Registry.GetValue(hive + @"\Software\Microsoft\Windows\CurrentVersion\App Paths\" + b[1], "", null) as string)
                    .Where(p => !string.IsNullOrEmpty(p)).Select(p => p.Trim('"')).FirstOrDefault(File.Exists);
                if (exe != null) list.Add(new[] { b[0], exe, b[2] });
            }
            return list;
        }
    }
}
