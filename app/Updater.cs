// Updates from GitHub Releases, as install.ps1 does them: the latest release of brand.json's "repo", its <name>Setup.exe
// checked against its .sha256, then run quietly (--quit-arrow ends this app; --start opens the new one). Network calls
// run off the UI thread and report back through the callbacks, on that thread. ARROW_UPDATES_URL points the check
// elsewhere (tests).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace Arrow
{
    public class Release
    {
        public string Tag, Setup, Sum;
        public string Version { get { return Tag.TrimStart('v', 'V'); } }
    }

    public static class Updater
    {
        // The release's setup: <name>Setup.exe, else one under the app's former name (a release keeps both for a while)
        static string[] SetupNames { get { return new[] { Program.Name + "Setup.exe" }.Concat(new[] { "Arrow" }.Where(n => n != Program.Name).Select(n => n + "Setup.exe")).ToArray(); } }

        static string LatestUrl
        {
            get
            {
                var test = Environment.GetEnvironmentVariable("ARROW_UPDATES_URL");
                if (!string.IsNullOrEmpty(test)) return test;
                return string.IsNullOrEmpty(Program.Repo) ? null : "https://api.github.com/repos/" + Program.Repo + "/releases/latest";
            }
        }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = Program.Name + "/" + System.Windows.Forms.Application.ProductVersion; // GitHub refuses requests without one
            return c;
        }

        // done(release newer than this app, or null; error text or null), on the calling thread's context
        public static void Check(Action<Release, string> done)
        {
            var ui = SynchronizationContext.Current;
            new Thread(() =>
            {
                Release found = null;
                string error = null;
                try
                {
                    var url = LatestUrl;
                    if (url == null) throw new InvalidOperationException("brand.json names no repository to update from.");
                    string json;
                    using (var c = Client()) json = c.DownloadString(url);
                    var release = Json.Parse(json) as IDictionary<string, object>;
                    var r = SetupNames.Select(n => Core.ReleaseSetup(release, n)).FirstOrDefault(x => x != null);
                    if (r == null) throw new InvalidOperationException("The latest release has no " + SetupNames[0] + " or no checksum.");
                    if (Core.IsNewer(r[0], System.Windows.Forms.Application.ProductVersion)) found = new Release { Tag = r[0], Setup = r[1], Sum = r[2] };
                }
                catch (Exception e) { error = "Couldn't check for updates: " + e.Message; Log.Write("app", error); }
                Post(ui, () => done(found, error));
            }) { IsBackground = true, Name = "update check" }.Start();
        }

        // Downloads and checks the setup, then runs it: progress(0-100), then failed(error) or nothing (the setup quits us)
        public static void Install(Release r, Action<int> progress, Action<string> failed)
        {
            var ui = SynchronizationContext.Current;
            new Thread(() =>
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), Program.Name + "-update");
                    Directory.CreateDirectory(dir);
                    var setup = Path.Combine(dir, SetupNames[0]);
                    string sum;
                    using (var c = Client())
                    {
                        sum = c.DownloadString(r.Sum);
                        var expected = Core.Sha256Of(sum);
                        if (expected == null) throw new InvalidOperationException("The release's checksum file is unreadable.");
                        c.Headers[HttpRequestHeader.UserAgent] = Program.Name + "/" + System.Windows.Forms.Application.ProductVersion; // WebClient clears its headers after each request
                        c.DownloadProgressChanged += (s, e) => Post(ui, () => progress(e.ProgressPercentage));
                        var wait = new ManualResetEvent(false);
                        Exception err = null;
                        c.DownloadFileCompleted += (s, e) => { err = e.Error; wait.Set(); };
                        c.DownloadFileAsync(new Uri(r.Setup), setup);
                        wait.WaitOne();
                        if (err != null) throw err;
                        string actual;
                        using (var f = File.OpenRead(setup))
                        using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                        if (actual != expected)
                        {
                            File.Delete(setup);
                            throw new InvalidOperationException("The download is damaged or was changed (checksum mismatch). Nothing was installed.");
                        }
                    }
                    var desktop = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Program.Name + ".lnk"));
                    var startup = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Program.Name + ".lnk"));
                    Log.Write("app", "updating to " + r.Tag);
                    Process.Start(new ProcessStartInfo(setup, Core.SetupArgs(desktop, startup)) { UseShellExecute = false, CreateNoWindow = true });
                }
                catch (Exception e)
                {
                    Log.Write("app", "update failed: " + e.Message);
                    Post(ui, () => failed("The update didn't install: " + e.Message));
                }
            }) { IsBackground = true, Name = "update" }.Start();
        }

        static void Post(SynchronizationContext ui, Action a)
        {
            if (ui != null) ui.Post(_ => a(), null);
            else a();
        }
    }
}
