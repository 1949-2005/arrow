// The ported helpers against the same cases as ui.test.js, plus the /api/state model. Run by app.test.js.
using System;
using System.Collections.Generic;
using System.Globalization;
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

        public static int Main()
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

            Console.WriteLine(failures == 0 ? "all passed" : failures + " failed");
            return failures == 0 ? 0 : 1;
        }
    }
}
