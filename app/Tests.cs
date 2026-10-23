// The ported helpers against the same cases as ui.test.js, plus the /api/state model. Run by app.test.js.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Arrow
{
    public static class Tests
    {
        static int failures;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string Show(object o) { return o == null ? "null" : "\"" + Convert.ToString(o, Inv) + "\""; }
        static void Eq<T>(T actual, T expected, string what)
        {
            if (Equals(actual, expected)) return;
            failures++;
            Console.WriteLine("FAIL " + what + ": got " + Show(actual) + ", expected " + Show(expected));
        }
        static void Ok(bool ok, string what) { Eq(ok, true, what); }
        static string L(IEnumerable<string> list) { return string.Join(" | ", list); }
        static string P(List<double[]> pieces) { return string.Join(" ", pieces.Select(p => p[0].ToString("R", Inv) + "," + p[1].ToString("R", Inv))); }
        static void Throws(Action a, string message, string what)
        {
            try { a(); }
            catch (Exception e)
            {
                Ok(e.Message.Contains(message), what + " (message: " + e.Message + ")");
                return;
            }
            Ok(false, what + " did not throw");
        }
        static long Ms(int y, int mo, int d, int h, int mi) { return new DateTimeOffset(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local)).ToUnixTimeMilliseconds(); }
        static string Q(string s) { return s.Replace('\'', '"'); }

        // A /api/state body as server.js sends it: a running http download, a pending YouTube one in its Download File Info window, HLS, an error
        static readonly string StateJson = Q(@"{
  'settings': {'dir': 'C:\\Users\\PC\\Downloads', 'connections': 8, 'maxActive': 3, 'speedLimitKB': 0, 'scheduleAt': '', 'categories': true, 'clipboard': false, 'startup': false},
  'downloads': [
    {'id': 'a1', 'url': 'https://releases.ubuntu.com/24.04/u.iso', 'name': 'u.iso', 'file': 'C:\\Users\\PC\\Downloads\\Compressed\\u.iso', 'size': 6291456000,
     'status': 'downloading', 'resumable': true, 'kind': 'http', 'added': 1760860800000, 'got': 2147483648, 'speed': 5242880, 'connections': 8,
     'segments': [[0, 3145728000, 1073741824], [3145728000, 6291456000, 4219469824]]},
    {'id': 'p2', 'url': 'https://www.youtube.com/watch?v=jNQXAC9IVRw', 'name': 'Me at the zoo', 'status': 'pending', 'kind': 'video', 'title': 'Me at the zoo',
     'quality': 'best', 'added': 1760860900000, 'got': 0, 'speed': 0, 'connections': 0, 'segments': null,
     'probe': {'name': 'Me at the zoo.mp4', 'category': 'Video', 'size': 504566, 'type': null, 'error': null, 'format': 'mp4:160+140', 'options': [
       {'key': 'mp4:160+140', 'group': 'MP4', 'label': '144p', 'size': 504566},
       {'key': 'mp4:137+140', 'group': 'MP4', 'label': '1080p', 'tag': 'HD', 'size': 126353408},
       {'key': 'mp3', 'group': 'Audio', 'label': 'MP3', 'size': 456000}]}},
    {'id': 'h3', 'url': 'https://x.com/live/index.m3u8', 'name': 'index.mp4', 'status': 'paused', 'kind': 'hls', 'phase': null,
     'hls': {'done': 3, 'bytes': 300, 'total': 6}, 'added': 1760860000000, 'got': 300, 'speed': 0, 'connections': 0},
    {'id': 'e4', 'url': 'https://x.com/f%C3%A9.zip', 'name': 'Café — ü.zip', 'status': 'error', 'error': 'HTTP 404', 'kind': 'http', 'got': 0, 'speed': 0, 'connections': 0}
  ],
  'whenDone': '', 'powerIn': null, 'open': 2,
  'exts': ['mp4','mkv','avi','mov','wmv','flv','webm','m4v','ts','3gp','mp3','wav','flac','aac','m4a','ogg','wma','opus','zip','rar','7z','tar','gz','bz2','xz','iso',
           'pdf','doc','docx','xls','xlsx','ppt','pptx','txt','epub','csv','exe','msi','apk','dmg','deb','rpm','appimage'],
  'categories': {
    'Video': ['mp4','mkv','avi','mov','wmv','flv','webm','m4v','ts','3gp'],
    'Music': ['mp3','wav','flac','aac','m4a','ogg','wma','opus'],
    'Compressed': ['zip','rar','7z','tar','gz','bz2','xz','iso'],
    'Documents': ['pdf','doc','docx','xls','xlsx','ppt','pptx','txt','epub','csv'],
    'Programs': ['exe','msi','apk','dmg','deb','rpm','appimage']}
}");

        // The same with the IDM-parity fields (engine issues 01–03)
        static readonly string IdmJson = Q(@"{
  'settings': {'dir': 'D:\\Downloads', 'speedLimitKB': 500, 'speedLimitOn': true, 'duplicates': 'replace', 'showComplete': false,
    'types': {'Video': ['mp4'], 'Music': ['mp3'], 'Programs': ['exe'], 'Documents': ['pdf'], 'Compressed': ['zip'], 'Ebooks': ['epub', 'mobi']},
    'queues': [{'id': 'main', 'name': 'Main download queue', 'maxActive': 1, 'startAt': '02:00', 'days': [], 'stopAt': '', 'retries': 0, 'whenDone': ''},
               {'id': 'n1', 'name': 'Night', 'maxActive': 2, 'startAt': '23:30', 'days': [1, 5], 'stopAt': '06:00', 'retries': 3, 'whenDone': 'shutdown'}]},
  'downloads': [
    {'id': 'q1', 'url': 'https://x.com/r.pdf', 'name': 'r.pdf', 'status': 'downloading', 'kind': 'http', 'size': 1000, 'got': 350, 'speed': 10, 'connections': 1,
     'description': 'Annual report', 'limitKB': 250, 'whenDone': 'open', 'lastTry': 1760860800000, 'notify': true, 'queue': 'main',
     'segments': [[0, 500, 250, 1], [500, 1000, 600, 0]]},
    {'id': 'q2', 'url': 'https://x.com/a.zip', 'status': 'paused', 'kind': 'http', 'description': null, 'lastTry': null, 'queue': null, 'segments': null}
  ],
  'queues': [{'id': 'main', 'name': 'Main download queue', 'running': true, 'waiting': 1, 'active': 1, 'total': 3},
             {'id': 'n1', 'name': 'Night', 'running': false, 'waiting': 0, 'active': 0, 'total': 0}],
  'whenDone': '', 'powerOp': 'shutdown', 'powerIn': 42, 'powerAll': false, 'open': 0
}");

        // args: every assets/types/*.svg of the repo, passed by app.test.js since Tests.exe runs from a temp folder
        public static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = Inv;
            var state = State.From(Json.Parse(StateJson));

            // batch links
            Eq(L(Core.ExpandBatch(new[] { "https://s.com/img[001-003].jpg" })), "https://s.com/img001.jpg | https://s.com/img002.jpg | https://s.com/img003.jpg", "batch padded");
            Eq(L(Core.ExpandBatch(new[] { "https://s.com/[8-10].zip" })), "https://s.com/8.zip | https://s.com/9.zip | https://s.com/10.zip", "batch unpadded");
            Eq(L(Core.ExpandBatch(new[] { "https://s.com/p[099-101].png" })), "https://s.com/p099.png | https://s.com/p100.png | https://s.com/p101.png", "batch padding kept past 99");
            Eq(L(Core.ExpandBatch(new[] { "https://s.com/[03-01].jpg" })), "https://s.com/03.jpg | https://s.com/02.jpg | https://s.com/01.jpg", "batch counting down");
            var imgs = Core.ExpandBatch(new[] { "https://s.com/img[001-120].jpg" });
            Eq(imgs.Count, 120, "batch count");
            Eq(imgs.Last(), "https://s.com/img120.jpg", "batch last");
            Eq(L(Core.ExpandBatch(new[] { "https://s.com/a.zip", "https://s.com/[1-2]/[01-02].jpg" })),
                "https://s.com/a.zip | https://s.com/1/01.jpg | https://s.com/1/02.jpg | https://s.com/2/01.jpg | https://s.com/2/02.jpg", "batch ranges multiply");
            var plain = new[] { "http://[::1]:8080/f.zip", "https://s.com/[a-z].zip", "https://s.com/[5].zip" };
            Eq(L(Core.ExpandBatch(plain)), L(plain), "batch leaves other links");
            Eq(Core.ExpandBatch(new[] { "https://s.com/[0001-1000].jpg" }).Count, 1000, "batch of 1000");
            foreach (var links in new[] {
                new[] { "https://s.com/[1-1001].jpg" },
                new[] { "https://s.com/a.zip", "https://s.com/[1-1000].jpg" },
                new[] { "https://s.com/[1-40]/[1-40].jpg" },
                new[] { "https://s.com/[1-99999999999999999999].jpg" } })
                Throws(() => Core.ExpandBatch(links), "A batch can add at most 1000 links.", "batch refused: " + L(links));

            // clipboard
            var exts = state.Exts;
            Ok(exts.Contains("zip") && exts.Contains("mp4") && exts.Contains("exe"), "exts from the state");
            Eq(Core.ClipboardLink("  https://x.com/a/File.ZIP \n", exts), "https://x.com/a/File.ZIP", "clipboard trimmed");
            Eq(Core.ClipboardLink("HTTPS://X.com/v.mp4?token=1#t", exts), "https://x.com/v.mp4?token=1#t", "clipboard normalized");
            foreach (var text in new[] { "https://x.com/page.html", "https://x.com/", "https://x.com/zip", "https://x.com/a.zip/", "ftp://x.com/a.zip",
                "see https://x.com/a.zip", "https://x.com/a.zip https://x.com/b.zip", "C:\\Downloads\\a.zip", "", "https://[bad/a.zip" })
                Eq(Core.ClipboardLink(text, exts), null, "clipboard refuses " + text);

            // category
            var cats = state.Categories;
            Eq(L(cats.Keys), "Video | Music | Compressed | Documents | Programs", "categories in order");
            Func<Download, string> cat = d => { d.Kind = d.Kind ?? "http"; d.Url = d.Url ?? "https://x.com/"; return Core.Category(d, cats); };
            Eq(cat(new Download { Url = "https://x.com/dl/Setup.EXE?v=2#x" }), "Programs", "category from the URL");
            Eq(cat(new Download { Name = "report.pdf" }), "Documents", "category from the name");
            Eq(cat(new Download { Name = "old.bin", File = "C:\\Downloads\\Compressed\\a.tar.gz" }), "Compressed", "the file on disk wins");
            Eq(cat(new Download { Name = "song.flac" }), "Music", "category music");
            Eq(cat(new Download { Url = "https://x.com/download.php?id=4" }), "Other", "category php");
            Eq(cat(new Download { Name = "README" }), "Other", "category none");
            Eq(cat(new Download { Kind = "video", Quality = "best" }), "Video", "youtube best");
            Eq(cat(new Download { Kind = "video", Quality = "mp3" }), "Music", "youtube mp3");
            Eq(cat(new Download { Kind = "video", Quality = "best", Format = "m4a:140" }), "Music", "youtube m4a");
            Eq(cat(new Download { Kind = "video", Quality = "best", Format = "mp3" }), "Music", "youtube mp3 key");
            Eq(cat(new Download { Kind = "video", Quality = "mp3", Format = "webm:248+251" }), "Video", "the format key wins");
            Eq(cat(new Download { Kind = "hls", Url = "https://x.com/live/index.m3u8" }), "Video", "hls");

            // date added
            var now = new DateTime(2026, 10, 19, 10, 0, 0, DateTimeKind.Local);
            Eq(Core.DateLabel(Ms(2026, 10, 19, 0, 0), now), "Today 00:00", "today midnight");
            Eq(Core.DateLabel(Ms(2026, 10, 19, 9, 5), now), "Today 09:05", "today");
            Eq(Core.DateLabel(Ms(2026, 10, 18, 23, 59), now), "Yesterday", "yesterday");
            Eq(Core.DateLabel(Ms(2026, 10, 1, 0, 0), new DateTime(2026, 10, 2, 8, 0, 0)), "Yesterday", "yesterday on the 2nd");
            Eq(Core.DateLabel(Ms(2026, 9, 30, 12, 0), new DateTime(2026, 10, 1, 8, 0, 0)), "Yesterday", "yesterday across a month");
            Eq(Core.DateLabel(Ms(2026, 10, 17, 12, 0), now), "Oct 17", "short date");
            Eq(Core.DateLabel(Ms(2026, 10, 7, 12, 0), now), "Oct 7", "short date, day without a zero");
            Ok(Core.DateLabel(Ms(2025, 10, 17, 0, 0), now).Contains("2025"), "another year shows the year");
            Eq(Core.DateLabel(0, now), "", "downloads from before added existed");

            // Download File Info (confirm.html)
            var on = new Settings { Dir = "C:\\Users\\PC\\Downloads", Categories = true };
            Eq(Core.FolderFor(on, "Video"), "C:\\Users\\PC\\Downloads\\Video", "folder video");
            Eq(Core.FolderFor(on, "Other"), "C:\\Users\\PC\\Downloads", "folder other");
            Eq(Core.FolderFor(new Settings { Dir = on.Dir, Categories = false }, "Music"), "C:\\Users\\PC\\Downloads", "folder categories off");
            Eq(Core.FolderFor(new Settings { Dir = "D:\\", Categories = true }, "Programs"), "D:\\Programs", "folder at a drive root");
            var kept = new Settings { Dir = on.Dir, Categories = true, Folders = new Dictionary<string, string> { { "Video", "E:\\Films" }, { "Other", "E:\\Misc" }, { "Music", "" } } };
            Eq(Core.FolderFor(kept, "Video"), "E:\\Films", "folder remembered");
            Eq(Core.FolderFor(kept, "Other"), "E:\\Misc", "folder remembered other");
            Eq(Core.FolderFor(kept, "Compressed"), "C:\\Users\\PC\\Downloads\\Compressed", "folder not remembered");
            Eq(Core.FolderFor(kept, "Music"), "C:\\Users\\PC\\Downloads\\Music", "folder empty entry");
            Eq(Core.FolderFor(new Settings { Dir = on.Dir, Categories = false, Folders = kept.Folders }, "Video"), "E:\\Films", "folder remembered categories off");
            Eq(Settings.From((IDictionary<string, object>)Json.Parse("{\"folders\":{\"Video\":\"E:\\\\Films\",\"Music\":5}}")).Folders["Video"], "E:\\Films", "settings folders parsed");
            Eq(Settings.From((IDictionary<string, object>)Json.Parse("{\"folders\":{\"Video\":\"E:\\\\Films\",\"Music\":5}}")).Folders.Count, 1, "settings folders bad entry");
            Eq(Settings.From((IDictionary<string, object>)Json.Parse("{}")).Folders.Count, 0, "settings folders missing");
            Eq(Core.JoinPath(Core.FolderFor(on, "Compressed"), "a.iso"), "C:\\Users\\PC\\Downloads\\Compressed\\a.iso", "join");
            Eq(Core.JoinPath("D:\\", "a.iso"), "D:\\a.iso", "join at a drive root");
            Eq(L(Core.SplitPath("C:\\a\\b c\\Me at the zoo.mp4")), "C:\\a\\b c | Me at the zoo.mp4", "split");
            Eq(L(Core.SplitPath("C:/a/f.zip")), "C:/a | f.zip", "split slashes");
            Eq(L(Core.SplitPath("f.zip")), " | f.zip", "split a bare name");
            Eq(Core.WithExt("Me at the zoo.mp4", ".mp3"), "Me at the zoo.mp3", "withExt swap");
            Eq(Core.WithExt("clip", ".mp4"), "clip.mp4", "withExt add");
            Eq(Core.WithExt("v1.2 final.mp4", ""), "v1.2 final", "withExt drop");
            Eq(Core.FormatText(new FormatOption { Key = "mp4:137+140", Group = "MP4", Label = "1080p", Tag = "HD", Size = 126353408 }), "MP4 · 1080p · 120.5 MB", "formatText mp4");
            Eq(Core.FormatText(new FormatOption { Key = "webm:303+251", Group = "WEBM", Label = "1080p60", Size = 102860390 }), "WEBM · 1080p60 · 98.1 MB", "formatText webm");
            Eq(Core.FormatText(new FormatOption { Key = "m4a:140", Group = "Audio", Label = "M4A", Size = 3565158 }), "M4A · audio · 3.4 MB", "formatText m4a");
            Eq(Core.FormatText(new FormatOption { Key = "mp3", Group = "Audio", Label = "MP3", Size = 456000 }), "MP3 · audio · 445.3 KB", "formatText mp3");
            Eq(Core.FormatText(new FormatOption { Key = "mp4:299+140", Group = "MP4", Label = "1080p60", Tag = "HD", Size = 1116691200, Approx = true }), "MP4 · 1080p60 · ~1.04 GB", "formatText approximate");
            Eq(L(new[] { "mp4:137+140", "webm:248+251", "m4a:140-drc", "mp3", "best" }.Select(Core.FormatExt)), ".mp4 | .webm | .m4a | .mp3 | .mp4", "formatExt");
            Eq(L(new[] { "mp4:137+140", "webm:248+251", "m4a:140", "mp3", "best" }.Select(Core.FormatCategory)), "Video | Video | Music | Music | Video", "formatCategory");
            Eq(Core.Subtitle(new Download { Kind = "video", Url = "https://www.youtube.com/watch?v=jNQXAC9IVRw" }, "Me at the zoo.mp4"), "Me at the zoo · youtube.com", "subtitle video");
            Eq(Core.Subtitle(new Download { Kind = "http", Url = "https://releases.ubuntu.com/24.04/u.iso" }, "u.iso"), "u.iso · releases.ubuntu.com", "subtitle http");
            Eq(Core.Subtitle(new Download { Kind = "http", Url = "bad" }, "u.iso"), "u.iso", "subtitle bad url");
            Func<List<FormatOption>, string> choices = opts => string.Join(" | ", Core.FormatChoices(new Probe { Options = opts }).Select(c => c[0] + "=" + c[1]));
            Eq(choices(new List<FormatOption>()), "best=MP4 · best quality | mp3=MP3 · audio", "format choices when the list failed");
            Eq(choices(new List<FormatOption> {
                new FormatOption { Key = "mp3", Group = "Audio", Label = "MP3", Size = 456000 },
                new FormatOption { Key = "webm:248+251", Group = "WEBM", Label = "1080p", Size = 1024 },
                new FormatOption { Key = "mp4:137+140", Group = "MP4", Label = "1080p", Size = 2048 } }),
                "mp4:137+140=MP4 · 1080p · 2.0 KB | webm:248+251=WEBM · 1080p · 1.0 KB | mp3=MP3 · audio · 445.3 KB", "format choices grouped MP4, WEBM, Audio");
            Eq(L(Core.QualityKeys) + "/" + L(Core.QualityNames), "best | 1080 | 720 | 480 | mp3/Best quality | 1080p | 720p | 480p | MP3", "qualities");

            // Options
            Eq(Core.WholeNumber(" 8 ", 1, 32), 8, "number");
            Eq(Core.WholeNumber("32", 1, 32), 32, "number at the top");
            foreach (var bad in new[] { "0", "33", "", "abc", "-1", "+3", "2.5", "1e1", null })
                Eq(Core.WholeNumber(bad, 1, 32), null, "number refused: " + bad);
            Eq(Core.WholeNumber("0", 0, 10000000), 0, "0 = unlimited");
            Eq(Core.ScheduleTime("02:00"), "02:00", "time");
            Eq(Core.ScheduleTime(" 2:05 "), "02:05", "time padded");
            Eq(Core.ScheduleTime("23:59"), "23:59", "time late");
            Eq(Core.ScheduleTime(""), "", "time off");
            foreach (var bad in new[] { "24:00", "12:60", "12", "1200", "ab:cd", "12:5", "012:00" })
                Eq(Core.ScheduleTime(bad), null, "time refused: " + bad);
            Ok(Core.IsFullPath("C:\\Downloads") && Core.IsFullPath("d:/x") && Core.IsFullPath("\\\\server\\share"), "full paths");
            Ok(!Core.IsFullPath("Downloads") && !Core.IsFullPath("C:") && !Core.IsFullPath("\\x") && !Core.IsFullPath("\\\\server") && !Core.IsFullPath(""), "not full paths");

            Eq(Core.Size(6291456000), "5.86 GB", "size GB");
            Eq(Core.Size(10), "10 B", "size B");

            // progress window (progress.html)
            Func<Download> http = () => new Download { Kind = "http", Status = "downloading", Size = 1000, Got = 452, Connections = 8, Segments = new List<long[]> { new long[] { 0, 500, 300 }, new long[] { 500, 1000, 652 } } };
            Func<string, Download> with = s => { var d = http(); d.Status = s; return d; };
            Eq(Core.Heading(http()), "Downloading · 45%", "heading downloading");
            Eq(Core.StatusText(http()), "Receiving data… (8 connections)", "status connections");
            var one = http(); one.Connections = 1;
            Eq(Core.StatusText(one), "Receiving data… (1 connection)", "status one connection");
            Eq(P(Core.Pieces(http())), "0,0.3 0.5,0.152", "pieces http");
            var conns = http();
            conns.Segments = new List<long[]> { new long[] { 0, 1048576, 524288, 1 }, new long[] { 1048576, 2097152, 2097152, 0 }, new long[] { 2097152, 3145728, 2097152, 0 }, new long[] { 0, 0, 2048 } };
            Eq(string.Join(" | ", Core.ConnectionRows(conns).Select(r => r[0] + " · " + r[1])),
                "512.0 KB of 1.0 MB · Receiving data… | 1.0 MB of 1.0 MB · Done | 0 B of 1.0 MB · Waiting | 2.0 KB of ? · Waiting", "connection rows");
            conns.Kind = "hls";
            Eq(Core.ConnectionRows(conns).Count, 0, "no connection rows for hls");
            conns.Kind = "http"; conns.Segments = null;
            Eq(Core.ConnectionRows(conns).Count, 0, "no connection rows without pieces");
            var finished = new Download { Status = "done", Notify = true };
            var showing = new Settings { ShowComplete = true };
            Ok(Core.OpensComplete(finished, "downloading", showing) && Core.OpensComplete(finished, "pending", showing), "complete opens when it finishes");
            Ok(!Core.OpensComplete(finished, null, showing), "complete stays shut for a download done before the app started");
            Ok(!Core.OpensComplete(finished, "done", showing), "complete opens once");
            Ok(!Core.OpensComplete(finished, "downloading", new Settings { ShowComplete = false }), "complete stays shut when turned off");
            Ok(!Core.OpensComplete(new Download { Status = "done" }, "downloading", showing), "complete stays shut without notify");
            Ok(!Core.OpensComplete(new Download { Status = "error", Notify = true }, "downloading", showing), "complete stays shut when it failed");
            Eq(Core.Heading(with("paused")), "Paused · 45%", "heading paused");
            Eq(Core.StatusText(with("paused")), "Paused", "status paused");
            Eq(Core.StatusText(with("queued")), "Queued", "status queued");
            Eq(Core.Heading(with("queued")), "Queued", "heading queued");
            var err = with("error"); err.Error = "HTTP 404";
            Eq(Core.Heading(err), "Error", "heading error");
            Eq(Core.StatusText(err), "Error: HTTP 404", "status error");
            Eq(Core.Heading(with("done")), "Download complete", "heading done");
            Eq(Core.Fraction(with("done"), true), 1.0, "fraction done");
            Func<string, Download> video = phase => new Download { Kind = "video", Status = "downloading", Size = 200, Got = 50, Phase = phase };
            Eq(Core.StatusText(video("audio")), "Downloading audio", "status video audio");
            Eq(Core.StatusText(video("converting")), "Converting to MP3", "status converting");
            Eq(Core.StatusText(video(null)), "Starting…", "status video starting");
            Eq(Core.Heading(video("merging")), "Merging", "heading merging");
            // a progress window in the tray: its tooltip, within Windows' 127 (or 63) characters
            var tray = http(); tray.Name = "file.zip"; tray.Speed = 2516582;
            Eq(Core.TrayTip(tray, 127), "45% · 2.4 MB/s\nfile.zip", "tray tip running");
            var held = with("paused"); held.Name = "file.zip";
            Eq(Core.TrayTip(held, 127), "Paused · 45%\nfile.zip", "tray tip paused");
            Eq(Core.TrayTip(video("merging"), 127), "Merging\nYouTube video", "tray tip merging");
            tray.Name = new string('a', 200);
            Eq(Core.TrayTip(tray, 127).Length, 127, "tray tip fits 127");
            Eq(Core.TrayTip(tray, 63), "45% · 2.4 MB/s\n" + new string('a', 47) + "…", "tray tip cut at 63");
            tray.Name = new string('a', 46) + "\U0001F3BB.mp4";
            Eq(Core.TrayTip(tray, 63), "45% · 2.4 MB/s\n" + new string('a', 46) + "…", "tray tip keeps emoji whole");
            Eq(P(Core.Pieces(video("audio"))), "0,0.25", "one fill for video");
            var hls = new Download { Kind = "hls", Status = "downloading", Got = 300, Hls = new HlsInfo { Done = 3, Total = 6, Bytes = 300 } };
            Eq(Core.StatusText(hls), "Receiving data…", "status hls");
            Eq(P(Core.Pieces(hls)), "0,0.5", "pieces hls");
            Ok(Core.CanPause(http()) && Core.CanPause(with("queued")) && !Core.CanPause(with("paused")) && !Core.CanPause(with("error")), "canPause");
            Eq(Core.Size(null, "Unknown"), "Unknown", "size unknown");
            Eq(Core.Time(190), "3 min 10 s", "time");

            // the list (ui.html)
            Eq(Core.Size(null), "?", "list size unknown");
            Eq(Core.Time(5.48), "6 s", "time rounds seconds up");
            Eq(Core.Time(3700), "1 h 1 min", "time hours");
            Eq(Core.Time(double.PositiveInfinity), "", "time infinite");
            var cell = Core.Status(http());
            Eq(cell.Text + "/" + cell.Kind + "/" + cell.Bar, "45.2%/Normal/0.452", "list status downloading");
            cell = Core.Status(with("paused"));
            Eq(cell.Text + "/" + cell.Kind + "/" + cell.Bar, "Paused · 45.2%/Normal/0.452", "list status paused");
            cell = Core.Status(err);
            Eq(cell.Text + "/" + cell.Kind + "/" + cell.Bar, "Error: HTTP 404/Err/", "list status error");
            cell = Core.Status(with("done"));
            Eq(cell.Text + "/" + cell.Kind + "/" + cell.Bar, "Complete/Done/", "list status done");
            cell = Core.Status(with("scheduled"));
            Eq(cell.Text + "/" + cell.Kind + "/" + cell.Bar, "Scheduled/Muted/", "list status scheduled");
            Eq(Core.Status(video("merging")).Text, "Merging", "list status video phase");
            var over = http(); over.Got = 1500;
            Eq(Core.Fraction(over), 1.5, "the list's fraction is not clamped");
            Eq(Core.Fraction(over, true), 1.0, "the progress window's is");
            Eq(Core.Eta(new Download { Kind = "http", Status = "downloading", Size = 1000, Got = 452, Speed = 100 }), "6 s", "eta");
            Eq(Core.Eta(with("paused")), "", "eta paused");
            Eq(Core.Total(hls), 600.0, "hls total from its segments");
            Eq(Core.Total(new Download { Kind = "hls", Status = "downloading", Hls = new HlsInfo() }), null, "hls total unknown");
            Eq(Core.ShortName(new Download { Kind = "http", Url = "https://x.com/f%C3%A9.zip?x=1" }), "fé.zip", "shortName from the URL");
            Eq(Core.ShortName(new Download { Kind = "http", Url = "https://x.com/" }), "https://x.com/", "shortName falls back to the URL");
            Eq(Core.ShortName(new Download { Kind = "video", Url = "https://www.youtube.com/watch?v=x" }), "YouTube video", "shortName video");
            Ok(Core.IsYouTube("https://music.youtube.com/watch?v=x") && Core.IsYouTube("HTTPS://youtu.be/x") && !Core.IsYouTube("https://notyoutube.com/"), "isYouTube");
            Ok(Core.CanResume(err) && !Core.CanResume(http()) && Core.CanStop(with("scheduled")) && !Core.CanStop(with("done")), "canResume / canStop");

            // emoji: drawn in color by DirectWrite, everything else stays on TextRenderer
            foreach (var s in new[] { "(1) MT NEWS on X \U0001F6A8\U0001F6A8 الخريطة.mp4", "عزف \U0001F3BB.mp4",
                "☀ sun", "end ➿", "❤️", "❤️‍\U0001F525", "\U0001F3F3️‍\U0001F308", "1⃣", "#️⃣",
                "\U0001F000", "\U0001FAFF", "\U0001F44D\U0001F3FD" })
                Ok(Core.HasEmoji(s), "emoji in " + s);
            foreach (var s in new[] { null, "", "Last War Winter Storm.mp4", "Last War Winter Storm الشتوية.mp4", "Café — ü … · →",
                "ع‍", "◿ ⟀", "\U0001EFFF", "\U0001FB00", "\U00020000 \U0001D400", "\uD83D", "x\uDE80" })
                Ok(!Core.HasEmoji(s), "no emoji in " + s);

            // Windows file icons: the file's own extension, else its category's typical one
            Eq(L(new[] { "Video", "Music", "Documents", "Programs", "Compressed", "Other", "?" }.Select(Core.TypicalExt)), ".mp4 | .mp3 | .pdf | .exe | .zip |  | ", "typical extensions");
            Eq(Core.IconExt("C:\\Users\\PC\\Downloads\\Music\\Song.MP3", "Music"), ".mp3", "icon ext from a path, lower case");
            Eq(Core.IconExt("(1) tangerine - Search _ X.ts", "Video"), ".ts", "icon ext ts");
            Eq(Core.IconExt("backup.tar.gz", "Compressed"), ".gz", "icon ext of the last part");
            Eq(Core.IconExt("README", "Other"), "", "no extension: the generic file");
            Eq(Core.IconExt("YouTube video", "Video"), ".mp4", "no extension: the category's");
            Eq(Core.IconExt("v1.2 final", "Video"), ".mp4", "a dot inside the name is no extension");
            Eq(Core.IconExt("https://x.com/", "Other") + "/" + Core.IconExt(null, "Music"), "/.mp3", "icon ext of a bare URL and of nothing");

            // the sidebar's SVG Types icons: any path data as absolute M, L, C, Z steps (rounded to 4 decimals here), else null (the glyph)
            Func<string, string> svg = d =>
            {
                var s = Core.ParseSvg("<svg viewBox=\"0 0 128 96\" fill-rule=\"evenodd\"><path d=\"" + d + "\"/></svg>");
                return s == null ? null : s.Width.ToString(Inv) + "x" + s.Height.ToString(Inv) + ": "
                    + L(s.Steps.Select(p => p.Op + string.Concat(p.Args.Select(a => " " + Math.Round(a, 4).ToString("R", Inv)))));
            };
            Eq(svg("M1 2L3.5 4C5 6 7 8 9 10.25Z"), "128x96: M 1 2 | L 3.5 4 | C 5 6 7 8 9 10.25 | Z", "svg path");
            Eq(svg("M0 0 10 0 10 10Z M20 20L30 30 40 40Z"), "128x96: M 0 0 | L 10 0 | L 10 10 | Z | M 20 20 | L 30 30 | L 40 40 | Z", "svg lines after M and L");
            Eq(svg("m0 0l10 10z"), "128x96: M 0 0 | L 10 10 | Z", "svg relative commands");
            Eq(svg("m1 2 3 4c1 1 2 2 3 3z l1 1m2 2"), "128x96: M 1 2 | L 4 6 | C 5 7 6 8 7 9 | Z | L 2 3 | M 4 5", "svg relative, the pen carried and back at the start after z");
            Eq(svg("M0 0L10 10H5Z"), "128x96: M 0 0 | L 10 10 | L 5 10 | Z", "svg H");
            Eq(svg("M1 1H5V7h-2v-3"), "128x96: M 1 1 | L 5 1 | L 5 7 | L 3 7 | L 3 4", "svg H and V");
            Eq(svg("M0 0C1 2 3 4 5 5S9 8 10 10"), "128x96: M 0 0 | C 1 2 3 4 5 5 | C 7 6 9 8 10 10", "svg S mirrors the last control point");
            Eq(svg("M0 0S1 2 3 4"), "128x96: M 0 0 | C 0 0 1 2 3 4", "svg S without a curve before");
            Eq(svg("M0 0Q3 6 6 0T12 0"), "128x96: M 0 0 | C 2 4 4 4 6 0 | C 8 -4 10 -4 12 0", "svg Q and T as cubics");
            Eq(svg("M1 0A1 1 0 0 1 0 1"), "128x96: M 1 0 | C 1 0.5523 0.5523 1 0 1", "svg quarter-circle arc");
            Eq(svg("M0 0A1 1 0 0 1 2 0"), "128x96: M 0 0 | C 0 -0.5523 0.4477 -1 1 -1 | C 1.5523 -1 2 -0.5523 2 0", "svg half-circle arc");
            Eq(svg("M1 1A0 5 0 0 1 4 5A2 2 0 0 1 4 5Z"), "128x96: M 1 1 | L 4 5 | Z", "svg arc of radius 0 and arc to its own start");
            Ok(svg("M0 0a1 1 0 01.5.5") != null && svg("M0 0a1 1 0 01.5.5") == svg("M0 0a1 1 0 0 1 .5 .5"), "svg packed arc flags");
            Eq(svg("M1.5.5-1-2"), "128x96: M 1.5 0.5 | L -1 -2", "svg numbers split at a second dot and a sign");
            Eq(svg("M0 0L10Z"), null, "svg wrong number count");
            Eq(svg("M0 0C1 2 3 4 5Z"), null, "svg short curve");
            Eq(svg("L1 1"), null, "svg not starting with a move");
            Eq(svg("M0 0Z 1 1"), null, "svg numbers after Z");
            Eq(svg("M0 0a1 1 0 2 1 1 1"), null, "svg arc flag not 0 or 1");
            Eq(Core.ParseSvg("<svg><path d=\"M0 0L1 1Z\"/></svg>"), null, "svg without viewBox");
            Eq(Core.ParseSvg("<svg viewBox=\"10 0 128 128\"><path d=\"M0 0L1 1Z\"/></svg>"), null, "svg viewBox not at 0 0");
            Eq(Core.ParseSvg("<svg viewBox=\"0 0 128 128\"></svg>"), null, "svg without path");
            Func<string, SvgShape> file = rest => Core.ParseSvg("<svg viewBox=\"0 0 48 48\"" + rest + "</svg>");
            Eq(file(" fill-rule=\"evenodd\"><path d=\"M0 0L1 1Z\"/>").EvenOdd, true, "svg fill-rule evenodd");
            Eq(file("><path style=\"fill-rule: evenodd\" d=\"M0 0L1 1Z\"/>").EvenOdd, true, "svg fill-rule evenodd in a style");
            Eq(file("><path d=\"M0 0L1 1Z\"/>").EvenOdd, false, "svg nonzero by default");
            Eq(file("><path transform=\"scale(2)\" d=\"M0 0L1 1Z\"/>"), null, "svg with a transform");
            Eq(file("><circle cx=\"1\" cy=\"1\" r=\"1\"/><path d=\"M0 0L1 1Z\"/>"), null, "svg with a circle");
            Eq(file(" fill=\"none\" stroke=\"#000\"><path d=\"M0 0L1 1Z\"/>"), null, "svg stroke-only");
            foreach (var f in args) Ok(Core.ParseSvg(File.ReadAllText(f)) != null, "svg the real " + Path.GetFileName(f));

            // the user's regional format
            Thread.CurrentThread.CurrentCulture = new CultureInfo("fr-FR");
            Eq(Core.Size(126353408), "120,5 MB", "size with a comma");
            Eq(Core.Status(http()).Text, "45,2%", "percent with a comma");
            Thread.CurrentThread.CurrentCulture = Inv;

            // /api/state
            Eq(state.Settings.Dir, "C:\\Users\\PC\\Downloads", "settings dir");
            Eq(state.Settings.Connections + "/" + state.Settings.MaxActive + "/" + state.Settings.Categories + "/" + state.Settings.Startup, "8/3/True/False", "settings");
            Eq(state.Downloads.Count, 4, "downloads");
            Eq(state.WhenDone + "/" + state.PowerIn + "/" + state.Open + "/" + state.Exts.Count, "//2/43", "whenDone, powerIn, open, exts");
            var a1 = state.Downloads[0];
            Eq(a1.Size, 6291456000L, "a size past 2 GB");
            Eq(a1.Got + "/" + a1.Speed + "/" + a1.Connections + "/" + a1.Resumable + "/" + a1.Added, "2147483648/5242880/8/True/1760860800000", "running download");
            Eq(a1.Segments.Count + "/" + a1.Segments[1][0] + "/" + a1.Segments[1][2], "2/3145728000/4219469824", "segments");
            Eq(Core.Category(a1, cats), "Compressed", "category of a parsed download");
            var p2 = state.Downloads[1];
            Eq(p2.Status + "/" + p2.Segments + "/" + p2.Probe.Name + "/" + p2.Probe.Size + "/" + p2.Probe.Type + "/" + p2.Probe.Listing + "/" + p2.Probe.Format, "pending//Me at the zoo.mp4/504566//False/mp4:160+140", "pending probe");
            Eq(L(p2.Probe.Options.Select(Core.FormatText)), "MP4 · 144p · 492.7 KB | MP4 · 1080p · 120.5 MB | MP3 · audio · 445.3 KB", "probe options");
            Eq(p2.Probe.Options[1].Tag + "/" + p2.Probe.Options[0].Tag, "HD/", "option tag");
            var h3 = state.Downloads[2];
            Eq(h3.Hls.Done + "/" + h3.Hls.Bytes + "/" + h3.Hls.Total + "/" + h3.Resumable + "/" + h3.Phase + "/" + h3.Probe, "3/300/6///", "hls download");
            Eq(Core.Status(h3).Text, "Paused · 50%", "hls status");
            var e4 = state.Downloads[3];
            Eq(e4.Name + "/" + e4.Error + "/" + e4.Added + "/" + e4.Size, "Café — ü.zip/HTTP 404/0/", "error download, UTF-8 name");
            var again = Json.Serialize(Json.Parse(StateJson));
            Eq(Json.Serialize(Json.Parse(again)), again, "serialize, parse, serialize is stable");
            var back = State.From(Json.Parse(again));
            Eq(back.Downloads[0].Segments[1][2] + "/" + back.Downloads[1].Probe.Options[2].Key + "/" + back.Downloads[3].Name + "/" + back.Open, "4219469824/mp3/Café — ü.zip/2", "state after a round trip");
            var empty = State.From(Json.Parse("{}"));
            Eq(empty.Downloads.Count + "/" + empty.Exts.Count + "/" + empty.Categories.Count + "/" + empty.Open + "/" + empty.Settings.Dir + "/" + empty.WhenDone, "0/0/0/0//", "missing fields");
            var body = Json.Parse(Json.Serialize(new Dictionary<string, object> { { "id", "a1" }, { "path", "C:\\Téléchargements\\a \"b\".zip" } })) as IDictionary<string, object>;
            Eq(body.Str("path"), "C:\\Téléchargements\\a \"b\".zip", "a request body round trip");
            Eq(body.Long("missing") + "/" + body.Bool("id") + "/" + body.Num("id") + "/" + (body.List("id") == null), "///True", "accessors tolerate missing keys and other types");

            // /api/state's IDM-parity fields: queues, per-download description, limit, when done, last try, the 4th segment value
            var idm = State.From(Json.Parse(IdmJson));
            var st = idm.Settings;
            Eq(st.SpeedLimitOn + "/" + st.SpeedLimitKB + "/" + st.Duplicates + "/" + st.ShowComplete, "True/500/replace/False", "new settings");
            Eq(L(st.Types.Keys) + "/" + L(st.Types["Ebooks"]), "Video | Music | Programs | Documents | Compressed | Ebooks/epub | mobi", "settings types in order");
            Eq(L(st.Queues.Select(q => q.Id + ":" + q.Name + ":" + q.MaxActive + ":" + q.StartAt + ":" + string.Join(",", q.Days) + ":" + q.StopAt + ":" + q.Retries + ":" + q.WhenDone)),
                "main:Main download queue:1:02:00:::0: | n1:Night:2:23:30:1,5:06:00:3:shutdown", "settings queues");
            Eq(L(idm.Queues.Select(q => q.Id + ":" + q.Running + ":" + q.Waiting + ":" + q.Active + ":" + q.Total)), "main:True:1:1:3 | n1:False:0:0:0", "state queues with counts");
            Eq(idm.PowerAll + "/" + State.From(Json.Parse("{\"powerAll\": true}")).PowerAll + "/" + empty.PowerAll, "False/True/False", "powerAll: a download's or a queue's own countdown, or all finished");
            Eq(idm.PowerOp + "/" + idm.PowerIn + "/" + empty.PowerOp + "/" + empty.Queues.Count + "/" + empty.Settings.Duplicates + "/" + empty.Settings.ShowComplete, "shutdown/42//0/ask/True", "powerOp and defaults");
            var q1 = idm.Downloads[0];
            Eq(q1.Description + "/" + q1.LimitKB + "/" + q1.WhenDone + "/" + q1.LastTry + "/" + q1.Notify + "/" + q1.Queue, "Annual report/250/open/1760860800000/True/main", "download fields");
            Eq(q1.Segments[0].Length + "/" + q1.Segments[0][3] + "/" + q1.Segments[1][3], "4/1/0", "segments' working flag");
            var q2 = idm.Downloads[1];
            Eq(q2.Description + "/" + q2.LimitKB + "/" + q2.WhenDone + "/" + q2.LastTry + "/" + q2.Notify + "/" + q2.Queue, "/0//0/False/", "download fields missing or null");
            Eq(P(Core.Pieces(q1)), "0,0.25 0.5,0.1", "pieces ignore the working flag");

            // the list: cells and status texts
            var queues = idm.Queues;
            Func<string, Download> dl = s => new Download { Kind = "http", Status = s, Size = 1000, Got = 452, Speed = 100, Added = Ms(2026, 10, 19, 9, 5), LastTry = Ms(2026, 10, 17, 12, 0) };
            Eq(Core.ListStatus(dl("downloading"), queues), "45%", "status downloading");
            Eq(Core.ListStatus(video("merging"), queues) + " | " + Core.ListStatus(video("converting"), queues) + " | " + Core.ListStatus(video("audio"), queues), "Merging | Converting to MP3 | 25%", "status video phases");
            Eq(Core.ListStatus(dl("queued"), queues), "Queued", "status queued");
            var inQ = dl("scheduled"); inQ.Queue = "main";
            Eq(Core.ListStatus(inQ, queues), "In Main queue", "status in the main queue");
            inQ.Queue = "n1";
            Eq(Core.ListStatus(inQ, queues), "In Night", "status in a named queue");
            inQ.Queue = "gone";
            Eq(Core.ListStatus(inQ, queues), "Scheduled", "status in an unknown queue");
            Eq(Core.ListStatus(dl("paused"), queues) + " | " + Core.ListStatus(dl("error"), queues) + " | " + Core.ListStatus(dl("done"), queues), "Paused · 45% | Failed | Complete", "status paused, failed, complete");
            Eq(Core.QueueName(queues[0]) + " | " + Core.QueueName(queues[1]), "Main queue | Night", "queue names");
            var running = dl("downloading"); running.Name = "setup.exe"; running.File = "C:\\Users\\PC\\Downloads\\Programs\\setup.exe"; running.Description = "Installer";
            Eq(L(Core.Columns.Select(c => Core.Cell(running, c, queues, now))), "setup.exe | 1000 B | 45% | 6 s | 100 B/s | Oct 17 | Installer | C:\\Users\\PC\\Downloads\\Programs | Today 09:05", "cells while downloading");
            var paused = dl("paused"); paused.Name = "a.zip"; paused.Size = null; paused.LastTry = 0;
            Eq(L(Core.Columns.Select(c => Core.Cell(paused, c, queues, now))), "a.zip |  | Paused · 0% |  |  |  |  |  | Today 09:05", "cells of a paused download without size or last try");

            // which columns fit: Name keeps 220, the first that doesn't fit and all right of it are left out
            var defaults = Core.DefaultColumns;
            Eq(L(Core.FitColumns(defaults, 1000, 1)), "size | status | left | speed | lastTry | description", "all default columns fit 1000");
            Eq(L(Core.FitColumns(defaults, 876, 1)), "size | status | left | speed | lastTry | description", "exactly 876");
            Eq(L(Core.FitColumns(defaults, 875, 1)), "size | status | left | speed | lastTry", "one short of 876");
            Eq(L(Core.FitColumns(defaults, 700, 1)), "size | status | left | speed", "700 (the minimum window without the details panel)");
            Eq(L(Core.FitColumns(defaults, 300, 1)), "", "only Name");
            Eq(L(Core.FitColumns(new[] { "added", "size", "description" }, 2000, 1)), "size | description | added", "the chosen ones in column order");
            Eq(L(Core.FitColumns(new[] { "description", "added" }, 482, 1)), "description | added", "482 fits Name, Description and Added");
            Eq(L(Core.FitColumns(new[] { "description", "added" }, 481, 1)), "description", "a later column is left out too");
            Eq(L(Core.FitColumns(defaults, 1300, 1.5)), "size | status | left | speed | lastTry", "scaled widths");

            // rows that grow with their name: tops from heights, the row at a y
            var tops = Core.RowTops(new[] { 48, 76, 48 });
            Eq(string.Join(" ", tops), "0 48 124 172", "row tops");
            Eq(string.Join(" ", Core.RowTops(new int[0])), "0", "no rows");
            Eq(Core.RowAt(tops, 0), 0, "y 0 is the first row");
            Eq(Core.RowAt(tops, 47), 0, "the first row's last pixel");
            Eq(Core.RowAt(tops, 48), 1, "the second row's top");
            Eq(Core.RowAt(tops, 123), 1, "inside the tall row");
            Eq(Core.RowAt(tops, 171), 2, "the last row's last pixel");
            Eq(Core.RowAt(tops, 172), -1, "under the rows");
            Eq(Core.RowAt(tops, -1), -1, "above the rows");
            Eq(Core.RowAt(Core.RowTops(new int[0]), 0), -1, "no rows: none at 0");

            // sorting: the default order, each column both ways, empty cells last, ties in the given order
            Func<string, string, long, long?, Download> row = (id, status, added, size) => new Download { Id = id, Name = id + ".zip", Kind = "http", Status = status, Added = added, Size = size, Got = 0 };
            var rows = new List<Download> { row("done1", "done", 50, 10), row("err", "error", 40, 30), row("run", "downloading", 10, 20), row("p-old", "paused", 5, null), row("p-new", "paused", 60, 40), row("done2", "done", 70, null), row("sch", "scheduled", 80, 5) };
            rows[2].Speed = 5; rows[2].Got = 10; rows[4].Got = 20;
            rows[0].LastTry = 300; rows[2].LastTry = 100; rows[3].LastTry = 200;
            rows[0].Description = "b"; rows[1].Description = "A"; rows[4].Description = "c";
            Func<IEnumerable<Download>, string> ids = l => string.Join(" ", l.Select(d => d.Id));
            Eq(ids(Core.DefaultOrder(rows)), "run p-new p-old err sch done2 done1", "default order: unfinished first, newest first");
            var byDefault = Core.DefaultOrder(rows);
            Eq(ids(Core.Sort(byDefault, "name", false, queues)), "done1 done2 err p-new p-old run sch", "by name");
            Eq(ids(Core.Sort(byDefault, "name", true, queues)), "sch run p-old p-new err done2 done1", "by name, descending");
            Eq(ids(Core.Sort(byDefault, "size", false, queues)), "sch done1 run err p-new p-old done2", "by size, unknown last");
            Eq(ids(Core.Sort(byDefault, "size", true, queues)), "p-new err run done1 sch p-old done2", "by size descending, unknown still last");
            Eq(ids(Core.Sort(byDefault, "status", false, queues)), "run p-old p-new err sch done2 done1", "by status: the groups, less done first");
            Eq(ids(Core.Sort(byDefault, "left", false, queues)), "run p-new p-old err sch done2 done1", "by time left: only the running one has one");
            Eq(ids(Core.Sort(byDefault, "speed", true, queues)), "run p-new p-old err sch done2 done1", "by speed");
            Eq(ids(Core.Sort(byDefault, "lastTry", false, queues)), "run p-old done1 p-new err sch done2", "by last try");
            Eq(ids(Core.Sort(byDefault, "lastTry", true, queues)), "done1 p-old run p-new err sch done2", "by last try descending");
            Eq(ids(Core.Sort(byDefault, "description", false, queues)), "err done1 p-new run p-old sch done2", "by description, case-insensitive, empty last");
            Eq(ids(Core.Sort(byDefault, "added", true, queues)), "sch done2 p-new done1 err run p-old", "by added descending");
            rows[0].File = "D:\\b\\done1.zip"; rows[5].File = "C:\\a\\done2.zip";
            Eq(ids(Core.Sort(byDefault, "saveTo", false, queues)), "done2 done1 run p-new p-old err sch", "by save to");
            string col = null; var desc = false;
            Core.NextSort("size", ref col, ref desc);
            Eq(col + "/" + desc, "size/False", "first click sorts ascending");
            Core.NextSort("size", ref col, ref desc);
            Eq(col + "/" + desc, "size/True", "second click reverses");
            Core.NextSort("size", ref col, ref desc);
            Eq(col + "/" + desc, "/False", "third click goes back to the default");
            Core.NextSort("size", ref col, ref desc);
            Core.NextSort("name", ref col, ref desc);
            Eq(col + "/" + desc, "name/False", "another column starts ascending");

            // speed limit switch, next queue start
            Eq(Core.SpeedLimitText(st) + " | " + Core.SpeedLimitText(new Settings { SpeedLimitKB = 500 }) + " | " + Core.SpeedLimitText(new Settings { SpeedLimitOn = true }), "Speed limit: 500 KB/s | Speed limit: Off | Speed limit: Off", "speed limit text");
            Func<string, string, int[], bool, Queue> queue = (id, at, days, run) => new Queue { Id = id, Name = id, StartAt = at, Days = days.ToList(), Running = run };
            var mon = new DateTime(2026, 10, 19, 10, 0, 0); // a Monday
            Eq(Core.NextStart(new[] { queue("main", "02:00", new int[0], false), queue("Night", "23:30", new int[0], false) }, mon), "Night at 23:30", "the next start today");
            Eq(Core.NextStart(new[] { queue("main", "02:00", new int[0], false), queue("Night", "23:30", new int[0], true) }, mon), "Main queue at 02:00", "a running queue is skipped; tomorrow's start");
            Eq(Core.NextStart(new[] { queue("main", "02:00", new[] { 5 }, false), queue("Night", "09:00", new[] { 3 }, false) }, mon), "Night at 09:00", "days of the week: Wednesday before Friday");
            Eq(Core.NextStart(new[] { queue("main", "", new int[0], false), queue("Night", "bad", new int[0], false) }, mon), null, "no start time");
            Eq(Core.NextStart(new[] { queue("main", "10:00", new[] { 1 }, false) }, mon), "Main queue at 10:00", "a start at this minute is next week's");

            // duplicates: probe.duplicate, the saved choice, "resume the existing one", the numbered name
            var dupProbe = State.From(Json.Parse(Q("{'downloads': [{'id': 'n', 'status': 'pending', 'probe': {'name': 'a.zip', 'duplicate': {'id': 'q2', 'name': 'a.zip', 'status': 'paused'}}}]}"))).Downloads[0].Probe;
            Eq(dupProbe.Duplicate.Id + "/" + dupProbe.Duplicate.Name + "/" + dupProbe.Duplicate.Status + "/" + (state.Downloads[1].Probe.Duplicate == null), "q2/a.zip/paused/True", "probe.duplicate");
            Eq(L(new[] { "ask", "number", "replace", "resume", "bad", null }.Select(c => Core.DuplicateChoice(dupProbe, new Settings { Duplicates = c }))), "ask | number | replace | resume | ask | ask", "duplicate choice: saved or ask");
            Eq(Core.DuplicateChoice(state.Downloads[1].Probe, st) + "/" + Core.DuplicateChoice(null, st), "/", "no duplicate, no question");
            Eq(L(new[] { "done", "paused", "error", "scheduled", "downloading", "queued" }.Select(Core.DuplicateResume)), "show | resume | resume | resume | progress | progress", "resume the existing one");
            Eq(L(new[] { "photos-ete.zip", "archive.tar.gz", "README", "" }.Select(Core.Numbered)), "photos-ete (1).zip | archive.tar (1).gz | README (1) |  (1)", "numbered names");

            // Properties: which address can change, the header's status line
            Func<string, string, Download> pd = (kind, status) => new Download { Kind = kind, Status = status, Size = 524288000, Got = 157286400 };
            Eq(L(new[] { "paused", "error", "scheduled", "downloading", "queued", "done" }.Select(x => Core.CanChangeUrl(pd("http", x)).ToString())), "True | True | True | False | False | False", "address editable while stopped");
            Eq(Core.CanChangeUrl(pd("video", "paused")) + "/" + Core.CanChangeUrl(pd("hls", "error")), "False/True", "YouTube keeps its link");
            var inNight = pd("http", "scheduled"); inNight.Queue = "n1";
            var noSize = pd("http", "error"); noSize.Size = null;
            Eq(Core.PropertiesStatus(pd("http", "paused"), queues) + " | " + Core.PropertiesStatus(pd("http", "done"), queues) + " | " + Core.PropertiesStatus(inNight, queues) + " | " + Core.PropertiesStatus(noSize, queues),
                "Paused · 30% of 500.0 MB | Complete · 500.0 MB | In Night · 500.0 MB | Failed", "properties status line");

            // the category editor: names and file types as engine.js checks them, and what it sends
            var names = new[] { "Video", "Music", "Programs", "Documents", "Compressed", "Courses" };
            foreach (var good in new[] { "Ebooks", "Cours de français", "a-b_c 2", "كتب", new string('x', 30) }) Eq(Core.CategoryNameError(good, names), null, "category name " + good);
            Eq(Core.CategoryNameError("", names), "Give the category a name.", "empty name");
            foreach (var bad in new[] { "a/b", "C++", "a.b", new string('x', 31) }) Eq(Core.CategoryNameError(bad, names), "A category name has up to 30 letters, digits, spaces, - or _.", "category name " + bad);
            Eq(L(new[] { "video", "OTHER", "all", "courses" }.Select(n => Core.CategoryNameError(n, names))),
                "There is already a category named Video. | There is already a category named Other. | There is already a category named All. | There is already a category named Courses.", "taken names, any case");
            string typeError;
            Eq(L(Core.ParseTypes("  srt VTT\tsrt  ass ", out typeError)) + "/" + typeError, "srt | vtt | ass/", "file types lower-cased, without repeats");
            Eq(Core.ParseTypes("", out typeError).Count + "/" + typeError, "0/", "no file types");
            Ok(Core.ParseTypes("srt .vtt", out typeError) == null && typeError == "Leave out the dot: vtt, not .vtt.", "a dot (" + typeError + ")");
            Ok(Core.ParseTypes("srt sub-title", out typeError) == null && typeError == "\"sub-title\" isn't a file type: use 1–10 letters or digits.", "a bad type (" + typeError + ")");
            Ok(Core.ParseTypes("abcdefghijk", out typeError) == null && Core.ParseTypes("abcdefghij", out typeError).Count == 1, "at most 10 characters");
            var cs = new Settings
            {
                Types = new Dictionary<string, List<string>> { { "Video", new List<string> { "mp4", "srt" } }, { "Music", new List<string> { "mp3" } }, { "Courses", new List<string> { "vtt" } }, { "Ebooks", new List<string> { "epub" } } },
                Folders = new Dictionary<string, string> { { "Courses", "D:\\Courses" }, { "Music", "D:\\Music" } },
            };
            Func<Dictionary<string, object>, string> sent = b => Json.Serialize(b);
            Eq(sent(Core.CategoryBody(cs, "Courses", "Lessons", new List<string> { "vtt", "srt" }, "D:\\Lessons")),
                Q("{'types':{'Video':['mp4'],'Music':['mp3'],'Lessons':['vtt','srt'],'Ebooks':['epub']},'folders':{'Music':'D:\\\\Music','Lessons':'D:\\\\Lessons'}}"), "rename: same place, its types taken from the others, its folder follows");
            Eq(sent(Core.CategoryBody(cs, null, "Subtitles", new List<string> { "ass" }, "")),
                Q("{'types':{'Video':['mp4','srt'],'Music':['mp3'],'Courses':['vtt'],'Ebooks':['epub'],'Subtitles':['ass']},'folders':{'Courses':'D:\\\\Courses','Music':'D:\\\\Music'}}"), "a new category goes last");
            Eq(sent(Core.CategoryBody(cs, "Music", "Music", new List<string> { "mp3", "flac" }, "")),
                Q("{'types':{'Video':['mp4','srt'],'Music':['mp3','flac'],'Courses':['vtt'],'Ebooks':['epub']},'folders':{'Courses':'D:\\\\Courses'}}"), "a built-in's types; an emptied folder is dropped");
            Eq(sent(Core.TypesWithout(cs, "Courses")), Q("{'types':{'Video':['mp4','srt'],'Music':['mp3'],'Ebooks':['epub']}}"), "remove a category: the others in order");

            // Options: the duplicates Picker ↔ settings.duplicates, and Save sending only what changed
            Eq(string.Join(",", new[] { "ask", "number", "replace", "resume", "odd", null }.Select(d => Core.DuplicateIndex(d))), "0,1,2,3,0,0", "duplicates → Picker, unknown = Ask me");
            Eq(L(Core.DuplicateSettings), "ask | number | replace | resume", "Picker → duplicates");
            var was = new Dictionary<string, object> { { "dir", "C:\\D" }, { "connections", 8 }, { "speedLimitOn", false }, { "duplicates", "ask" } };
            Eq(sent(Core.Changed(was, new Dictionary<string, object> { { "dir", "C:\\D" }, { "connections", 16 }, { "speedLimitOn", true }, { "duplicates", "ask" } })),
                Q("{'connections':16,'speedLimitOn':true}"), "only the changed settings");
            Eq(Core.Changed(was, was).Count, 0, "nothing changed");
            Eq(Core.ShortFolder(@"C:\Users\PC\Downloads\", @"C:\Users\PC\Downloads\Video"), @"Downloads\Video", "a category folder from the download folder's name");
            Eq(Core.ShortFolder(@"C:\Users\PC\Downloads", @"c:\users\pc\downloads"), "Downloads", "the download folder itself");
            Eq(Core.ShortFolder(@"C:\Users\PC\Downloads", @"C:\Users\PC\Downloads2\x"), @"C:\Users\PC\Downloads2\x", "another folder as it is");
            Eq(Core.ShortFolder(@"C:\D", @"D:\Courses"), @"D:\Courses", "elsewhere as it is");

            // the Scheduler: day toggles (Monday first) ↔ the engine's days (Sunday = 0), its fields as engine.js takes them
            Func<string, bool[]> toggles = s => s.Select(c => c == '1').ToArray();
            Eq(string.Join(",", Core.QueueDays(toggles("1111100"))), "1,2,3,4,5", "weekdays");
            Eq(string.Join(",", Core.QueueDays(toggles("0000011"))), "0,6", "weekend: Sunday is 0");
            Eq(Core.QueueDays(toggles("0000000")).Count, 0, "none = every day");
            Eq(new string(Core.DayToggles(new List<int> { 0, 1, 6 }).Select(b => b ? '1' : '0').ToArray()), "1000011", "engine days → toggles");
            Eq(new string(Core.DayToggles(new List<int>()).Select(b => b ? '1' : '0').ToArray()), "0000000", "every day → no toggle");
            Eq(string.Join(",", Core.QueueDays(Core.DayToggles(new List<int> { 3, 0 }))), "0,3", "round trip");
            var sq = new List<Queue> { new Queue { Id = Queue.Main, Name = "Main download queue" }, new Queue { Id = "n1", Name = "Night" } };
            Eq(Core.QueueValue("name", " Weekend ", "n1", sq), (object)"Weekend", "queue name trimmed");
            Eq(Core.QueueValue("name", "night", "n1", sq), (object)"night", "its own name in another case");
            Eq(Core.QueueValue("name", "NIGHT", "x", sq), null, "taken, any case");
            Eq(Core.QueueValue("name", "main download queue", "n1", sq), null, "the main queue's name is taken");
            Eq(Core.QueueValue("name", "  ", "n1", sq), null, "empty name");
            Eq(Core.QueueValue("name", new string('x', 40), "n1", sq), (object)new string('x', 40), "40 characters");
            Eq(Core.QueueValue("name", new string('x', 41), "n1", sq), null, "41 characters");
            Eq(Core.QueueValue("maxActive", "2", "n1", sq), (object)2, "files at the same time");
            Eq(Core.QueueValue("maxActive", "0", "n1", sq), null, "at least 1");
            Eq(Core.QueueValue("maxActive", "21", "n1", sq), null, "at most 20");
            Eq(Core.QueueValue("retries", "0", "n1", sq), (object)0, "no retries");
            Eq(Core.QueueValue("retries", "11", "n1", sq), null, "at most 10 retries");
            Eq(Core.QueueValue("retries", "2.5", "n1", sq), null, "whole retries");
            Eq(Core.QueueValue("startAt", "2:00", "n1", sq), (object)"02:00", "start padded");
            Eq(Core.QueueValue("stopAt", "07:00", "n1", sq), (object)"07:00", "stop");
            Eq(Core.QueueValue("startAt", "", "n1", sq), null, "a checked time can't be empty");
            Eq(Core.QueueValue("stopAt", "24:00", "n1", sq), null, "no 24:00");

            Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
