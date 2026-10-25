// Pure logic for the native app: JSON, the /api/state model, and the text helpers ported from
// ui.html, confirm.html and progress.html (same behavior; numbers and dates follow the user's regional format).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Glide
{
    public static class Json
    {
        static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        public static object Parse(string s) { return Serializer().DeserializeObject(s); }
        public static string Serialize(object o) { return Serializer().Serialize(o); }

        static object Get(IDictionary<string, object> o, string key)
        {
            object v;
            return o != null && o.TryGetValue(key, out v) ? v : null;
        }
        // The serializer gives int, long, decimal or double, whichever fits
        static bool IsNumber(object v) { return v is int || v is long || v is decimal || v is double; }

        public static string Str(this IDictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            return v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
        }
        public static double? Num(this IDictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            return IsNumber(v) ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : (double?)null;
        }
        public static long? Long(this IDictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            try { return IsNumber(v) ? Convert.ToInt64(v, CultureInfo.InvariantCulture) : (long?)null; }
            catch (OverflowException) { return null; }
        }
        public static int? Int(this IDictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            try { return IsNumber(v) ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : (int?)null; }
            catch (OverflowException) { return null; }
        }
        public static bool? Bool(this IDictionary<string, object> o, string key) { return Get(o, key) as bool?; }
        public static IDictionary<string, object> Obj(this IDictionary<string, object> o, string key) { return Get(o, key) as IDictionary<string, object>; }
        // null when missing or not an array
        public static List<object> List(this IDictionary<string, object> o, string key)
        {
            var v = Get(o, key);
            return v is IEnumerable && !(v is string) && !(v is IDictionary<string, object>) ? ((IEnumerable)v).Cast<object>().ToList() : null;
        }
        public static List<string> Strings(this IDictionary<string, object> o, string key)
        {
            var l = o.List(key);
            return l == null ? new List<string>() : l.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToList();
        }
    }

    public class State
    {
        public Settings Settings;
        public List<Download> Downloads;
        public string WhenDone, PowerOp; // PowerOp: what the running countdown does ('sleep', 'shutdown'), null when none runs
        public int? PowerIn;
        public bool PowerAll; // the running countdown is "When all downloads finish", not a download's or a queue's own
        public List<string> Exts;
        public Dictionary<string, List<string>> Categories; // the sidebar's Types, in order (settings.types)
        public List<Queue> Queues; // with running and the counts
        public int Open;

        public static State From(object json)
        {
            var o = json as IDictionary<string, object>;
            var s = new State
            {
                Settings = Settings.From(o.Obj("settings")),
                Downloads = (o.List("downloads") ?? new List<object>()).Select(x => Download.From(x as IDictionary<string, object>)).ToList(),
                WhenDone = o.Str("whenDone") ?? "",
                PowerOp = o.Str("powerOp"),
                PowerIn = o.Int("powerIn"),
                PowerAll = o.Bool("powerAll") ?? false,
                Queues = Queue.ListFrom(o.List("queues")),
                Exts = o.Strings("exts"),
                Categories = new Dictionary<string, List<string>>(),
                Open = o.Int("open") ?? 0,
            };
            var c = o.Obj("categories");
            if (c != null) foreach (var k in c.Keys) s.Categories[k] = c.Strings(k);
            return s;
        }
    }

    public class Settings
    {
        public string Dir, Duplicates, Proxy;
        public int Connections, MaxActive, SpeedLimitKB;
        public bool Categories, Clipboard, Startup, SpeedLimitOn, ShowComplete;
        public Dictionary<string, string> Folders = new Dictionary<string, string>(); // "Remember this folder for <Category>"
        public Dictionary<string, List<string>> Types = new Dictionary<string, List<string>>(); // category → file types, in sidebar order
        public List<Queue> Queues = new List<Queue>();
        public List<Site> Sites = new List<Site>(); // per-site connections and speed limit (engine.js cleanSites)

        public static Settings From(IDictionary<string, object> o)
        {
            var f = o.Obj("folders");
            var t = o.Obj("types");
            var queues = Queue.ListFrom(o.List("queues"));
            return new Settings
            {
                Dir = o.Str("dir") ?? "",
                Connections = o.Int("connections") ?? 0,
                MaxActive = o.Int("maxActive") ?? 0,
                SpeedLimitKB = o.Int("speedLimitKB") ?? 0,
                SpeedLimitOn = o.Bool("speedLimitOn") ?? false,
                Duplicates = o.Str("duplicates") ?? "ask",
                ShowComplete = o.Bool("showComplete") ?? true,
                Categories = o.Bool("categories") ?? false,
                Clipboard = o.Bool("clipboard") ?? false,
                Proxy = o.Str("proxy") ?? "",
                Startup = o.Bool("startup") ?? false,
                Folders = f == null ? new Dictionary<string, string>() : f.Keys.Where(k => !string.IsNullOrEmpty(f[k] as string)).ToDictionary(k => k, k => (string)f[k]),
                Types = t == null ? new Dictionary<string, List<string>>() : t.Keys.ToDictionary(k => k, k => t.Strings(k)),
                Queues = queues,
                Sites = (o.List("sites") ?? new List<object>()).OfType<IDictionary<string, object>>()
                    .Select(x => new Site { Host = x.Str("host") ?? "", Connections = x.Int("connections") ?? 0, LimitKB = x.Int("limitKB") ?? 0 }).ToList(),
            };
        }
    }

    // Phone access (POST /api/remote): on or off, listening, its port, this PC's local addresses, the paired phones
    public class RemoteDevice
    {
        public string Id, Name;
        public long Added, Seen;
    }

    public class RemoteInfo
    {
        public bool Enabled, Listening;
        public int Port;
        public List<string> Addresses = new List<string>();
        public string PublicNetwork; // the network's name when Windows calls it Public (its firewall then blocks the phone)
        public List<RemoteDevice> Devices = new List<RemoteDevice>();

        public static RemoteInfo From(IDictionary<string, object> o)
        {
            var r = new RemoteInfo();
            if (o == null) return r;
            r.Enabled = o.Bool("enabled") ?? false;
            r.Listening = o.Bool("listening") ?? false;
            r.Port = o.Int("port") ?? 0;
            r.Addresses = o.Strings("addresses") ?? new List<string>();
            r.PublicNetwork = o.Str("publicNetwork");
            r.Devices = (o.List("devices") ?? new List<object>()).OfType<IDictionary<string, object>>()
                .Select(d => new RemoteDevice { Id = d.Str("id") ?? "", Name = d.Str("name") ?? "Phone", Added = d.Long("added") ?? 0, Seen = d.Long("seen") ?? 0 }).ToList();
            return r;
        }

        // POST /api/remote/pair's qr: rows of booleans (dark modules); null when it isn't a square grid
        public static bool[][] Grid(object qr)
        {
            var rows = qr as System.Collections.IEnumerable;
            if (rows == null) return null;
            var grid = rows.Cast<object>().Select(r => (r as System.Collections.IEnumerable ?? new object[0]).Cast<object>().Select(v => v is bool && (bool)v).ToArray()).ToArray();
            return grid.Length >= 21 && grid.All(r => r.Length == grid.Length) ? grid : null;
        }
    }

    // A site's own settings: Connections and LimitKB, 0 for the usual ones
    public class Site
    {
        public string Host;
        public int Connections, LimitKB;
    }

    // A download queue (settings.queues; /api/state's queues add Running and the counts)
    public class Queue
    {
        public const string Main = "main";
        public string Id, Name, StartAt, StopAt, WhenDone;
        public int MaxActive, Retries, Waiting, Active, Total;
        public List<int> Days = new List<int>(); // Sunday = 0; empty = every day
        public bool Running;

        public static Queue From(IDictionary<string, object> o)
        {
            var days = o.List("days");
            return new Queue
            {
                Id = o.Str("id"), Name = o.Str("name") ?? "", StartAt = o.Str("startAt") ?? "", StopAt = o.Str("stopAt") ?? "", WhenDone = o.Str("whenDone") ?? "",
                MaxActive = o.Int("maxActive") ?? 1, Retries = o.Int("retries") ?? 0,
                Waiting = o.Int("waiting") ?? 0, Active = o.Int("active") ?? 0, Total = o.Int("total") ?? 0, Running = o.Bool("running") ?? false,
                Days = days == null ? new List<int>() : days.Where(v => v is int).Cast<int>().ToList(),
            };
        }

        public static List<Queue> ListFrom(List<object> l)
        {
            return (l ?? new List<object>()).OfType<IDictionary<string, object>>().Select(From).Where(q => q.Id != null).ToList();
        }
    }

    public class HlsInfo
    {
        public long Done, Bytes, Total;
    }

    public class Download
    {
        public string Id, Url, Name, File, Status, Error, Kind, Title, Phase, Quality, Format;
        public string Description, Queue, Referer; // Queue: the id of its queue, null when in none
        public string WhenDone = ""; // '' | 'open' | 'sleep' | 'shutdown'
        public long? Size;
        public bool? Resumable;
        public bool Notify; // started from Download File Info: its Download complete window opens once
        public HlsInfo Hls;
        public long Added, Got, Speed, LastTry; // LastTry: time of the last start, 0 when never
        public int Connections, LimitKB; // LimitKB: its own speed limit, 0 = none
        public Probe Probe;
        public List<long[]> Segments; // [start, end, pos, working (1 when a connection works on it now)]; null when the engine sends none

        public static Download From(IDictionary<string, object> o)
        {
            var h = o.Obj("hls");
            var segs = o.List("segments");
            return new Download
            {
                Id = o.Str("id"), Url = o.Str("url"), Name = o.Str("name"), File = o.Str("file"), Status = o.Str("status"), Error = o.Str("error"),
                Kind = o.Str("kind"), Title = o.Str("title"), Phase = o.Str("phase"), Quality = o.Str("quality"), Format = o.Str("format"),
                Description = o.Str("description"), Queue = o.Str("queue"), Referer = o.Str("referer"), WhenDone = o.Str("whenDone") ?? "", Notify = o.Bool("notify") ?? false,
                LastTry = o.Long("lastTry") ?? 0, LimitKB = o.Int("limitKB") ?? 0,
                Size = o.Long("size"), Resumable = o.Bool("resumable"),
                Hls = h == null ? null : new HlsInfo { Done = h.Long("done") ?? 0, Bytes = h.Long("bytes") ?? 0, Total = h.Long("total") ?? 0 },
                Added = o.Long("added") ?? 0, Got = o.Long("got") ?? 0, Speed = o.Long("speed") ?? 0, Connections = o.Int("connections") ?? 0,
                Probe = Probe.From(o.Obj("probe")),
                Segments = segs == null ? null : segs.Select(s => ((IEnumerable)s).Cast<object>().Select(v => Convert.ToInt64(v, CultureInfo.InvariantCulture)).ToArray()).ToList(),
            };
        }
    }

    public class Probe
    {
        public string Name, Category, Type, Error, Format;
        public long? Size;
        public bool Listing;
        public List<FormatOption> Options;
        public Download Duplicate; // probe.duplicate: the download already in the list with this link (Id, Name, Status only), else null

        public static Probe From(IDictionary<string, object> o)
        {
            if (o == null) return null;
            return new Probe
            {
                Name = o.Str("name"), Category = o.Str("category"), Type = o.Str("type"), Error = o.Str("error"), Format = o.Str("format"),
                Size = o.Long("size"), Listing = o.Bool("listing") ?? false,
                Options = (o.List("options") ?? new List<object>()).Select(x => FormatOption.From(x as IDictionary<string, object>)).ToList(),
                Duplicate = o.Obj("duplicate") == null ? null : new Download { Id = o.Obj("duplicate").Str("id"), Name = o.Obj("duplicate").Str("name"), Status = o.Obj("duplicate").Str("status") },
            };
        }
    }

    public class FormatOption
    {
        public string Key, Group, Label, Tag;
        public long? Size;
        public bool Approx; // the size is yt-dlp's estimate (a recording YouTube is still processing)

        public static FormatOption From(IDictionary<string, object> o)
        {
            return new FormatOption { Key = o.Str("key"), Group = o.Str("group"), Label = o.Str("label"), Tag = o.Str("tag"), Size = o.Long("size"), Approx = o.Bool("approx") ?? false };
        }
    }

    public enum CellKind { Normal, Err, Done, Muted }

    // The list's Status column
    public class StatusCell
    {
        public string Text;
        public CellKind Kind;
        public double? Bar;
    }

    // An SVG icon as plain data: its viewBox size, its fill rule and one absolute step per segment (M and L: x y,
    // C: x1 y1 x2 y2 x y, Z: none)
    public class SvgShape
    {
        public double Width, Height;
        public bool EvenOdd;
        public List<SvgStep> Steps = new List<SvgStep>();
    }

    public class SvgStep
    {
        public char Op;
        public double[] Args;
    }

    public static class Core
    {
        static readonly Dictionary<string, string> Label = new Dictionary<string, string> { { "queued", "Queued" }, { "downloading", "Downloading" }, { "paused", "Paused" }, { "done", "Complete" }, { "scheduled", "Scheduled" }, { "removed", "Removing" } };
        static readonly Dictionary<string, string> PhaseText = new Dictionary<string, string> { { "video", "Downloading video" }, { "audio", "Downloading audio" }, { "merging", "Merging" }, { "converting", "Converting to MP3" }, { "updating", "Updating yt-dlp" } };
        static readonly Dictionary<string, string> HeadingText = new Dictionary<string, string> { { "queued", "Queued" }, { "scheduled", "Scheduled" }, { "error", "Error" }, { "done", "Download complete" }, { "pending", "Waiting" }, { "removed", "Removing" } };
        // [A-Za-z0-9_] and [0-9]: .NET's \w and \d also match non-ASCII letters and digits, JavaScript's do not
        static readonly Regex Ext = new Regex(@"\.([A-Za-z0-9_]+)$");
        static readonly Regex AudioKey = new Regex("^(m4a|mp3)");
        static readonly Regex Batch = new Regex(@"\[([0-9]+)-([0-9]+)\]");
        static readonly Regex Link = new Regex(@"^https?://\S+$", RegexOptions.IgnoreCase);
        static readonly Regex YouTube = new Regex(@"^https?://((www|m|music)\.)?youtube\.com/|^https?://youtu\.be/", RegexOptions.IgnoreCase);
        static readonly Regex LastExt = new Regex(@"\.[^.\\/]*$");
        static readonly Regex IconExtension = new Regex(@"\.[A-Za-z0-9_]{1,16}$");
        // only at 0 0, as the user's files are: another origin would draw the shape shifted
        static readonly Regex ViewBox = new Regex(@"viewBox=""\s*0[\s,]+0[\s,]+([-+0-9.eE]+)[\s,]+([-+0-9.eE]+)\s*""");
        static readonly Regex PathData = new Regex(@"\sd=""([^""]*)""");
        // what a fill of the paths alone would draw wrong: transforms, other shapes, stroke-only icons
        static readonly Regex SvgUnsupported = new Regex(@"transform=|<(circle|rect|ellipse|line|polyline|polygon|use|image|text)[\s/>]|\bfill\s*=\s*[""']none[""']");
        static readonly Regex SvgEvenOdd = new Regex(@"fill-rule\s*(=\s*[""']\s*|:\s*)evenodd");
        const string SvgCommands = "MLHVCSQTAZ";
        static readonly int[] SvgArgCount = { 2, 2, 1, 1, 6, 4, 4, 2, 7, 0 };

        static string Num(double n) { return n.ToString(CultureInfo.CurrentCulture); }
        static string Get(Dictionary<string, string> map, string key) { string v; return key != null && map.TryGetValue(key, out v) ? v : null; }

        // ui.html shows "?" for an unknown size, progress.html "Unknown"
        public static string Size(double? n, string unknown = "?")
        {
            if (n == null || double.IsNaN(n.Value) || double.IsInfinity(n.Value)) return unknown;
            var v = n.Value;
            var c = CultureInfo.CurrentCulture;
            return v < 1024 ? Num(v) + " B" : v < 1048576 ? (v / 1024).ToString("F1", c) + " KB" : v < 1073741824 ? (v / 1048576).ToString("F1", c) + " MB" : (v / 1073741824).ToString("F2", c) + " GB";
        }

        public static string Time(double s)
        {
            if (double.IsNaN(s) || double.IsInfinity(s)) return "";
            return s < 60 ? (long)Math.Ceiling(s) + " s" : s < 3600 ? (long)Math.Floor(s / 60) + " min " + (long)Math.Floor(s % 60) + " s" : (long)Math.Floor(s / 3600) + " h " + (long)Math.Floor(s % 3600 / 60) + " min";
        }

        // HLS knows its size only from the segments done so far
        public static double? Total(Download d)
        {
            if (d.Kind == "hls" && d.Status != "done") return d.Hls != null && d.Hls.Done != 0 ? (double)d.Hls.Bytes / d.Hls.Done * d.Hls.Total : (double?)null;
            return d.Size;
        }

        // clamp: progress.html's version, which never passes 1
        public static double Fraction(Download d, bool clamp = false)
        {
            if (d.Status == "done") return 1;
            if (d.Kind == "hls") return d.Hls != null ? (double)d.Hls.Done / d.Hls.Total : 0;
            if (d.Size == null || d.Size == 0) return 0;
            var f = (double)d.Got / d.Size.Value;
            return clamp ? Math.Min(1, f) : f;
        }

        public static StatusCell Status(Download d)
        {
            var pct = Num(Math.Floor(Fraction(d) * 1000) / 10) + "%";
            if (d.Status == "error") return new StatusCell { Text = "Error: " + d.Error, Kind = CellKind.Err };
            if (d.Status == "done") return new StatusCell { Text = "Complete", Kind = CellKind.Done };
            if (d.Status == "downloading") return new StatusCell { Text = Get(PhaseText, d.Phase) ?? pct, Bar = Fraction(d) };
            if (d.Status == "paused") return new StatusCell { Text = "Paused · " + pct, Bar = Fraction(d) };
            return new StatusCell { Text = Get(Label, d.Status) ?? "", Kind = CellKind.Muted };
        }

        // Progress window header: "Downloading · 45%", "Paused · 45%", "Queued", "Merging", "Error", …
        public static string Heading(Download d)
        {
            var pct = Math.Floor(Fraction(d, true) * 100) + "%";
            if (d.Status == "downloading") return d.Phase == "merging" ? "Merging" : d.Phase == "converting" ? "Converting" : "Downloading · " + pct;
            if (d.Status == "paused") return "Paused · " + pct;
            return Get(HeadingText, d.Status) ?? d.Status;
        }

        // A progress window in the tray: "45% · 2.4 MB/s" (else the heading), then the name on its own line, within max
        // characters (Windows' tray tooltip limit). The only place a name is shortened: its end gives way to "…".
        public static string TrayTip(Download d, int max)
        {
            var head = d.Status == "downloading" && d.Phase != "merging" && d.Phase != "converting" ? Math.Floor(Fraction(d, true) * 100) + "% · " + Size(d.Speed) + "/s" : Heading(d);
            var tip = head + "\n" + ShortName(d);
            if (tip.Length <= max) return tip;
            var cut = max - 1;
            if (cut > 0 && char.IsHighSurrogate(tip[cut - 1])) cut--; // never half an emoji
            return tip.Substring(0, Math.Max(0, cut)).TrimEnd() + "…";
        }

        // Progress window status row
        public static string StatusText(Download d)
        {
            if (d.Status == "error") return "Error: " + d.Error;
            if (d.Status == "downloading")
            {
                if (d.Kind == "video" || !string.IsNullOrEmpty(d.Phase)) return Get(PhaseText, d.Phase) ?? "Starting…"; // HLS has a phase only while ffmpeg joins its sound
                return "Receiving data…" + (d.Connections != 0 ? " (" + d.Connections + " connection" + (d.Connections == 1 ? "" : "s") + ")" : "");
            }
            return d.Status == "paused" ? "Paused" : Heading(d);
        }

        // Bar pieces [left, width] as fractions: one per connection for http, one fill for HLS and video
        public static List<double[]> Pieces(Download d)
        {
            if (d.Kind == "http" && d.Segments != null && d.Size != null && d.Size != 0)
            {
                double size = d.Size.Value;
                return d.Segments.Select(s => new[] { s[0] / size, (s[2] - s[0]) / size }).ToList();
            }
            return new List<double[]> { new[] { 0, Fraction(d, true) } };
        }

        public const string Receiving = "Receiving data…";

        // The progress window's Connections table: [downloaded, info] per piece of an http download, none otherwise.
        // A piece's end is 0 (null) while the size is unknown.
        public static List<string[]> ConnectionRows(Download d)
        {
            if (d.Kind != "http" || d.Segments == null) return new List<string[]>();
            return d.Segments.Select(s =>
            {
                var known = s[1] > s[0];
                var info = known && s[2] >= s[1] ? "Done" : s.Length > 3 && s[3] == 1 ? Receiving : "Waiting";
                return new[] { Size(s[2] - s[0]) + " of " + (known ? Size(s[1] - s[0]) : "?"), info };
            }).ToList();
        }

        // Download complete opens by itself for a download started from Download File Info (notify) that this app saw
        // unfinished (before: its last status seen, null when not seen: already done when the app started) and that is done now
        public static bool OpensComplete(Download d, string before, Settings s)
        {
            return s.ShowComplete && d.Notify && d.Status == "done" && before != null && before != "done";
        }

        public static string ShortName(Download d)
        {
            if (!string.IsNullOrEmpty(d.Name)) return d.Name;
            if (d.Kind == "video") return IsYouTube(d.Url) ? "YouTube video" : "Video";
            var url = d.Url ?? "";
            var last = Uri.UnescapeDataString(url.Split('?')[0].Split('/').Last());
            return last != "" ? last : url;
        }

        // Seconds left while downloading at a known speed and size, else null
        public static double? EtaSeconds(Download d)
        {
            var t = Total(d);
            return d.Status == "downloading" && d.Speed != 0 && t != null && t != 0 && !double.IsNaN(t.Value) ? (t.Value - d.Got) / d.Speed : (double?)null;
        }

        public static string Eta(Download d) { var s = EtaSeconds(d); return s == null ? "" : Time(s.Value); }

        // ---- the list's columns, cells and order

        // Key, title and width at 96 DPI; Name takes the rest, at least its width
        public static readonly string[] Columns = { "name", "size", "status", "left", "speed", "lastTry", "description", "saveTo", "added" };
        public static readonly string[] ColumnTitles = { "Name", "Size", "Status", "Time left", "Speed", "Last try", "Description", "Save to", "Added" };
        public static readonly int[] ColumnWidths = { 220, 86, 120, 92, 96, 112, 150, 160, 112 };
        public static readonly string[] DefaultColumns = { "size", "status", "left", "speed", "lastTry", "description" };

        // The chosen columns that fit a list width (pixels at scale), in column order after Name: the first that doesn't fit
        // and every one right of it are left out
        public static List<string> FitColumns(IEnumerable<string> chosen, int width, double scale)
        {
            var set = new HashSet<string>(chosen);
            var used = (int)Math.Round(ColumnWidths[0] * scale);
            var fit = new List<string>();
            for (var i = 1; i < Columns.Length; i++)
            {
                if (!set.Contains(Columns[i])) continue;
                used += (int)Math.Round(ColumnWidths[i] * scale);
                if (used > width) break;
                fit.Add(Columns[i]);
            }
            return fit;
        }

        // Rows of these heights, one under the other: tops[i] is row i's top, tops[count] the total height
        public static int[] RowTops(IList<int> heights)
        {
            var tops = new int[heights.Count + 1];
            for (var i = 0; i < heights.Count; i++) tops[i + 1] = tops[i] + heights[i];
            return tops;
        }

        // The row at y (from RowTops; rows taller than 0), or -1 above or below the rows
        public static int RowAt(int[] tops, int y)
        {
            if (y < 0 || y >= tops[tops.Length - 1]) return -1;
            var i = Array.BinarySearch(tops, y);
            return i >= 0 ? i : ~i - 1;
        }

        public static string QueueName(Queue q) { return q.Id == Queue.Main ? "Main queue" : q.Name; }

        // The Status column: "45%", the video phases, "Queued", "In <queue>", "Paused · 30%", "Failed", "Complete"
        public static string ListStatus(Download d, IList<Queue> queues)
        {
            var pct = Num(Math.Floor(Fraction(d, true) * 100)) + "%";
            switch (d.Status)
            {
                case "downloading": return d.Phase == "merging" ? "Merging" : d.Phase == "converting" ? "Converting to MP3" : pct;
                case "queued": return "Queued";
                case "scheduled":
                    var q = queues == null ? null : queues.FirstOrDefault(x => x.Id == d.Queue);
                    return q != null ? "In " + QueueName(q) : "Scheduled";
                case "paused": return "Paused · " + pct;
                case "error": return "Failed";
                case "done": return "Complete";
                default: return StatusText(d);
            }
        }

        // A list cell's text; Time left and Speed only while downloading, empty when unknown
        public static string Cell(Download d, string column, IList<Queue> queues, DateTime now)
        {
            switch (column)
            {
                case "name": return ShortName(d);
                case "size": var t = Total(d); return t == null ? "" : Size(t);
                case "status": return ListStatus(d, queues);
                case "left": return Eta(d);
                case "speed": return d.Status == "downloading" ? Size(d.Speed) + "/s" : "";
                case "lastTry": return DateLabel(d.LastTry, now);
                case "description": return d.Description ?? "";
                case "saveTo": return string.IsNullOrEmpty(d.File) ? "" : SplitPath(d.File)[0];
                case "added": return DateLabel(d.Added, now);
                default: return "";
            }
        }

        // Unfinished first (downloading, queued, paused, failed, scheduled), then finished
        public static int Rank(Download d)
        {
            switch (d.Status)
            {
                case "downloading": return 0;
                case "queued": return 1;
                case "paused": return 2;
                case "error": return 3;
                case "done": return 5;
                default: return 4;
            }
        }

        // The list's default order: by Rank, newest first within each
        public static List<Download> DefaultOrder(IEnumerable<Download> list)
        {
            return list.OrderBy(Rank).ThenByDescending(d => d.Added).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        }

        // A column's sort key: a string or a double, null for an empty cell
        static object SortKey(Download d, string column, IList<Queue> queues)
        {
            switch (column)
            {
                case "size": return Total(d);
                case "status": return Rank(d) + Math.Min(1, Fraction(d, true)) / 2; // the groups in Rank order, less done first within one
                case "left": return EtaSeconds(d);
                case "speed": return d.Status == "downloading" ? (double?)d.Speed : null;
                case "lastTry": return d.LastTry == 0 ? (double?)null : d.LastTry;
                case "added": return d.Added == 0 ? (double?)null : d.Added;
                default: var s = Cell(d, column, queues, DateTime.Now); return s == "" ? null : s; // name, description, save to
            }
        }

        // The rows sorted by a column; empty cells stay last either way, ties keep the given order
        public static List<Download> Sort(IList<Download> items, string column, bool descending, IList<Queue> queues)
        {
            var keyed = items.Select(d => new { d, k = SortKey(d, column, queues) }).ToList();
            var cmp = Comparer<object>.Create((a, b) => a is string ? StringComparer.CurrentCultureIgnoreCase.Compare((string)a, (string)b) : ((double)a).CompareTo((double)b));
            var full = keyed.Where(x => x.k != null);
            var sorted = descending ? full.OrderByDescending(x => x.k, cmp) : full.OrderBy(x => x.k, cmp);
            return sorted.Concat(keyed.Where(x => x.k == null)).Select(x => x.d).ToList();
        }

        // A header click: a new column sorts ascending, the same one again reverses, a third time goes back to the default (null)
        public static void NextSort(string clicked, ref string column, ref bool descending)
        {
            if (clicked != column) { column = clicked; descending = false; }
            else if (!descending) descending = true;
            else { column = null; descending = false; }
        }

        // The global limit counts only when on and above 0 (the engine's rate is speedLimitOn ? speedLimitKB : 0)
        public static bool LimitOn(Settings s) { return s.SpeedLimitOn && s.SpeedLimitKB > 0; }
        public static string SpeedLimitText(Settings s) { return "Speed limit: " + (LimitOn(s) ? Num(s.SpeedLimitKB) + " KB/s" : "Off"); }

        // The status bar's next queue start, "Main queue at 02:00": the queue not running whose start comes first; null when none
        public static string NextStart(IList<Queue> queues, DateTime now)
        {
            Queue best = null;
            var bestAt = DateTime.MaxValue;
            foreach (var q in queues)
            {
                var hm = ScheduleTime(q.StartAt);
                if (q.Running || string.IsNullOrEmpty(hm)) continue;
                var at = now.Date.AddHours(int.Parse(hm.Substring(0, 2), CultureInfo.InvariantCulture)).AddMinutes(int.Parse(hm.Substring(3), CultureInfo.InvariantCulture));
                if (at <= now) at = at.AddDays(1);
                for (var i = 0; i < 7 && q.Days.Count > 0 && !q.Days.Contains((int)at.DayOfWeek); i++) at = at.AddDays(1);
                if (at < bestAt) { best = q; bestAt = at; }
            }
            return best == null ? null : QueueName(best) + " at " + best.StartAt;
        }

        public static bool CanResume(Download d) { return d.Status == "paused" || d.Status == "error" || d.Status == "scheduled"; }
        public static bool CanStop(Download d) { return d.Status == "downloading" || d.Status == "queued" || d.Status == "scheduled"; }
        public static bool CanPause(Download d) { return CanStop(d); }
        public static bool IsYouTube(string url) { return url != null && YouTube.IsMatch(url); }

        // ytdlp.js SITES: the other video sites yt-dlp downloads from (with www., m. and the like in front)
        static readonly Regex VideoSite = new Regex(@"^https?://([^/?#]+\.)?(vimeo\.com|dailymotion\.com|x\.com|twitter\.com|tiktok\.com|instagram\.com|facebook\.com|fb\.watch|reddit\.com|twitch\.tv|soundcloud\.com|bilibili\.com|streamable\.com)([:/?#]|$)", RegexOptions.IgnoreCase);

        // A link yt-dlp downloads: YouTube or one of the other video sites (Add links offers its Quality)
        public static bool IsVideoLink(string url) { return url != null && (YouTube.IsMatch(url) || VideoSite.IsMatch(url)); }

        // Sidebar category: from the file extension, else Other. YouTube is Video, or Music for M4A/MP3; HLS is always Video.
        public static string Category(Download d, IDictionary<string, List<string>> categories)
        {
            if (d.Kind == "video") return AudioKey.IsMatch(d.Format ?? d.Quality ?? "") ? "Music" : "Video";
            if (d.Kind == "hls") return "Video";
            var src = !string.IsNullOrEmpty(d.File) ? d.File : !string.IsNullOrEmpty(d.Name) ? d.Name : Regex.Split(d.Url ?? "", "[?#]")[0];
            var m = Ext.Match(src);
            if (!m.Success) return "Other";
            var ext = m.Groups[1].Value.ToLowerInvariant();
            return categories.Keys.FirstOrDefault(c => categories[c].Contains(ext)) ?? "Other";
        }

        // "Date added": "Today 14:05", "Yesterday", else day and short month (with the year when it is not this year)
        public static string DateLabel(long addedMs, DateTime now)
        {
            if (addedMs == 0) return "";
            var t = DateTimeOffset.FromUnixTimeMilliseconds(addedMs).LocalDateTime;
            if (t.Date == now.Date) return "Today " + t.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (t.Date == now.Date.AddDays(-1)) return "Yesterday";
            var c = CultureInfo.CurrentCulture;
            var pattern = c.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM").Replace("dd", "d");
            return t.ToString(t.Year != now.Year ? pattern + " yyyy" : pattern, c);
        }

        // Batch links: "img[001-120].jpg" gives img001.jpg … img120.jpg. Zero padding is kept, a range may count down,
        // and several ranges in one link multiply. Never builds more than max links: a range that would pass it throws.
        public static List<string> ExpandBatch(IEnumerable<string> input, int max = 1000)
        {
            var links = new List<string>(input);
            for (var i = 0; i < links.Count; i++)
            {
                var m = Batch.Match(links[i]);
                if (!m.Success) continue;
                string ma = m.Groups[1].Value, mb = m.Groups[2].Value;
                double a = double.Parse(ma, CultureInfo.InvariantCulture), b = double.Parse(mb, CultureInfo.InvariantCulture), count = Math.Abs(b - a) + 1;
                var pad = Regex.IsMatch(ma, "^0[0-9]") || Regex.IsMatch(mb, "^0[0-9]") ? Math.Max(ma.Length, mb.Length) : 0;
                if (links.Count - 1 + count > max) throw new ArgumentException("A batch can add at most " + max + " links.");
                var output = new List<string>();
                for (var k = 0; k < count; k++) output.Add(Batch.Replace(links[i], (a < b ? a + k : a - k).ToString("0", CultureInfo.InvariantCulture).PadLeft(pad, '0'), 1));
                links.RemoveAt(i);
                links.InsertRange(i, output);
                i--; // checks the first new link again, for a second range
            }
            return links;
        }

        // Clipboard monitoring offers only a lone http(s) link to a file type the engine sorts (state.Exts)
        public static string ClipboardLink(string text, IList<string> exts)
        {
            var t = (text ?? "").Trim();
            Uri u;
            if (!Link.IsMatch(t) || !Uri.TryCreate(t, UriKind.Absolute, out u)) return null;
            var m = Ext.Match(u.AbsolutePath);
            return m.Success && exts.Contains(m.Groups[1].Value.ToLowerInvariant()) ? u.AbsoluteUri : null;
        }

        // Category folder, as engine.js assignName(): the remembered folder, else <dir>\<Category>; Other and categories off go straight into <dir>
        public static string FolderFor(Settings settings, string category)
        {
            string kept;
            if (settings.Folders != null && category != null && settings.Folders.TryGetValue(category, out kept) && !string.IsNullOrEmpty(kept)) return kept;
            var dir = (settings.Dir ?? "").TrimEnd('\\', '/');
            return settings.Categories && category != "Other" ? dir + "\\" + category : dir;
        }

        public static string[] SplitPath(string p)
        {
            var i = Math.Max(p.LastIndexOf('\\'), p.LastIndexOf('/'));
            return new[] { p.Substring(0, Math.Max(i, 0)), p.Substring(i + 1) };
        }

        public static string JoinPath(string folder, string name) { return folder.TrimEnd('\\', '/') + "\\" + name; }
        public static string WithExt(string name, string ext) { return LastExt.Replace(name, "", 1) + ext; }

        // YouTube formats: option text, extension and category of a key. 'best' and 'mp3' are the legacy choices.
        public static string FormatText(FormatOption o)
        {
            var audio = o.Group == "Audio";
            return string.Join(" · ", audio ? o.Label : o.Group, audio ? "audio" : o.Label, (o.Approx ? "~" : "") + Size(o.Size));
        }
        public static string FormatExt(string key) { return key == "best" ? ".mp4" : "." + key.Split(':')[0]; }
        public static string FormatCategory(string key) { return AudioKey.IsMatch(key) ? "Music" : "Video"; }

        // Download File Info's Format list: [key, text] grouped MP4, WEBM, Audio; the legacy choices when the list failed
        public static List<string[]> FormatChoices(Probe p)
        {
            if (p.Options == null || p.Options.Count == 0) return new List<string[]> { new[] { "best", "MP4 · best quality" }, new[] { "mp3", "MP3 · audio" } };
            return new[] { "MP4", "WEBM", "Audio" }.SelectMany(g => p.Options.Where(o => o.Group == g)).Select(o => new[] { o.Key, FormatText(o) }).ToList();
        }

        // Add links' Quality for YouTube links (engine.js QUALITIES)
        public static readonly string[] QualityKeys = { "best", "1080", "720", "480", "mp3" };
        public static readonly string[] QualityNames = { "Best quality", "1080p", "720p", "480p", "MP3" };

        // Options: a whole number in [min, max], else null
        public static int? WholeNumber(string text, int min, int max)
        {
            int n;
            return int.TryParse((text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= min && n <= max ? n : (int?)null;
        }

        // Options' schedule: "HH:MM" (an hour without its zero is padded), "" for an empty field, null when invalid
        public static string ScheduleTime(string text)
        {
            var t = (text ?? "").Trim();
            if (t == "") return "";
            var m = Regex.Match(t, "^([01]?[0-9]|2[0-3]):([0-5][0-9])$");
            return m.Success ? m.Groups[1].Value.PadLeft(2, '0') + ":" + m.Groups[2].Value : null;
        }

        // ---- the Scheduler (engine.js queueFields, queueName)

        // Its day toggles, Monday first; the engine numbers the days Sunday = 0
        public static readonly string[] DayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        public static List<int> QueueDays(IList<bool> toggles) { return Enumerable.Range(0, 7).Where(i => toggles[i]).Select(i => (i + 1) % 7).OrderBy(d => d).ToList(); }
        public static bool[] DayToggles(IEnumerable<int> days)
        {
            var set = new HashSet<int>(days ?? new int[0]);
            return Enumerable.Range(0, 7).Select(i => set.Contains((i + 1) % 7)).ToArray();
        }

        // A Scheduler field's value to send ("name", "maxActive", "retries", "startAt", "stopAt" as the engine takes it), null
        // when invalid: names 1–40 characters, unique regardless of case among the other queues; a checked time is HH:MM
        public static object QueueValue(string field, string text, string id, IEnumerable<Queue> queues)
        {
            var t = (text ?? "").Trim();
            switch (field)
            {
                case "name": return t.Length >= 1 && t.Length <= 40 && !queues.Any(q => q.Id != id && string.Equals(q.Name, t, StringComparison.OrdinalIgnoreCase)) ? t : null;
                case "maxActive": return WholeNumber(t, 1, 20);
                case "retries": return WholeNumber(t, 0, 10);
                case "startAt": case "stopAt": var hm = ScheduleTime(t); return hm == "" ? null : hm;
                default: return null;
            }
        }

        // A folder with a drive or a network share, like C:\Downloads or \\server\share
        // engine.js cleanSites' rule: a host name (example.com, dl.example.com); a scheme, path or port is dropped there, refused here
        public static bool IsSiteHost(string s) { return Regex.IsMatch((s ?? "").Trim(), @"^(\*\.)?([A-Za-z0-9-]{1,63}\.)*[A-Za-z0-9-]{1,63}$") && (s ?? "").Trim().Length <= 253; }

        // engine.js cleanProxy's rule: http(s)://[user:pass@]host:port, nothing after the port
        public static bool IsProxy(string s) { return Regex.IsMatch((s ?? "").Trim(), @"^https?://([^@/\s]+@)?[^:/@\s]+:\d{1,5}/?$", RegexOptions.IgnoreCase); }

        public static bool IsFullPath(string path) { return Regex.IsMatch((path ?? "").Trim(), @"^([A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+)"); }

        // ---- duplicates (spec "Duplicates"): what a pending download with probe.duplicate does

        static readonly string[] DuplicateChoices = { "number", "replace", "resume" };

        // null: no duplicate; "ask": the Link already in the list window; else the saved choice
        public static string DuplicateChoice(Probe p, Settings s)
        {
            if (p == null || p.Duplicate == null) return null;
            return s != null && DuplicateChoices.Contains(s.Duplicates) ? s.Duplicates : "ask";
        }

        // "Resume the existing download": a finished one is shown (its Download complete window), a stopped one resumes,
        // one that already runs or waits shows its progress window
        public static string DuplicateResume(string status)
        {
            if (status == "done") return "show";
            return status == "paused" || status == "error" || status == "scheduled" ? "resume" : "progress";
        }

        // The sub-line of "Download it again with a new name", as engine.js uniqueFile() names the first free one
        public static string Numbered(string name)
        {
            var m = LastExt.Match(name ?? "");
            var ext = m.Success ? m.Value : "";
            return (name ?? "").Substring(0, (name ?? "").Length - ext.Length) + " (1)" + ext;
        }

        // ---- Properties

        // The address can change while nothing works on it (engine.js act 'url'); YouTube downloads keep their link
        public static bool CanChangeUrl(Download d)
        {
            return d.Kind != "video" && (d.Status == "paused" || d.Status == "error" || d.Status == "scheduled");
        }

        // Header line: "Paused · 30% of 500 MB", "Complete · 500 MB", "In Night"
        public static string PropertiesStatus(Download d, IList<Queue> queues)
        {
            var s = ListStatus(d, queues);
            var t = Total(d);
            if (t == null || d.Kind == "hls") return s;
            return s + (s.EndsWith("%") ? " of " : " · ") + Size(t);
        }

        // ---- categories (engine.js cleanTypes)

        public static readonly string[] BuiltInCategories = { "Video", "Music", "Programs", "Documents", "Compressed" }; // their icons are keyed by name
        static readonly Regex CategoryRx = new Regex(@"^[\p{L}\p{N} _-]{1,30}$"), TypeRx = new Regex("^[a-z0-9]{1,10}$");

        // A custom category's name, trimmed; others: the other categories' names. null when it is fine.
        public static string CategoryNameError(string name, IEnumerable<string> others)
        {
            if (name == "") return "Give the category a name.";
            if (!CategoryRx.IsMatch(name)) return "A category name has up to 30 letters, digits, spaces, - or _.";
            var taken = BuiltInCategories.Concat(new[] { "Other", "All" }).Concat(others).FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            return taken == null ? null : "There is already a category named " + taken + ".";
        }

        // "srt vtt": file types, lower-cased, without repeats; null with error when one is not 1–10 letters or digits
        public static List<string> ParseTypes(string text, out string error)
        {
            error = null;
            var l = new List<string>();
            foreach (var t in Regex.Split((text ?? "").Trim(), @"\s+").Where(x => x != "").Select(x => x.ToLowerInvariant()))
            {
                if (!TypeRx.IsMatch(t))
                {
                    error = t.StartsWith(".") ? "Leave out the dot: " + t.TrimStart('.') + ", not " + t + "." : "\"" + t + "\" isn't a file type: use 1–10 letters or digits.";
                    return null;
                }
                if (!l.Contains(t)) l.Add(t);
            }
            return l;
        }

        // POST /api/settings {types, folders} after the category editor: types in order (a rename keeps its place, a new one
        // goes last), the edited category's types taken from the others (the engine gives a type to the later one), and
        // every remembered folder, the edited one set or dropped. original: null for a new category.
        public static Dictionary<string, object> CategoryBody(Settings s, string original, string name, List<string> exts, string folder)
        {
            var types = new Dictionary<string, List<string>>();
            foreach (var k in s.Types.Keys)
                if (k == original) types[name] = exts;
                else types[k] = s.Types[k].Where(e => !exts.Contains(e)).ToList();
            if (original == null) types[name] = exts;
            var folders = s.Folders.Where(f => f.Key != original && f.Key != name).ToDictionary(f => f.Key, f => f.Value);
            if (!string.IsNullOrEmpty(folder)) folders[name] = folder;
            return new Dictionary<string, object> { { "types", types }, { "folders", folders } };
        }

        // POST /api/settings {types} without one category (Options › Categories › Remove): its downloads become Other,
        // the engine drops its remembered folder
        public static Dictionary<string, object> TypesWithout(Settings s, string name)
        {
            return new Dictionary<string, object> { { "types", s.Types.Where(t => t.Key != name).ToDictionary(t => t.Key, t => t.Value) } };
        }

        // Options › General's "When a link is already in the list" Picker, in its order ↔ settings.duplicates
        public static readonly string[] DuplicateSettings = { "ask", "number", "replace", "resume" };
        public static int DuplicateIndex(string duplicates) { return Math.Max(0, Array.IndexOf(DuplicateSettings, duplicates)); } // unknown = Ask me, as the engine

        // The main window's keyboard shortcuts (MainForm.ProcessCmdKey), as the Keyboard shortcuts window lists them
        public static readonly string[][] Shortcuts =
        {
            new[] { "Ctrl+V", "Paste a link into the paste bar" },
            new[] { "Ctrl+N", "Add several links" },
            new[] { "Ctrl+F", "Search downloads" },
            new[] { "Ctrl+A", "Select all downloads" },
            new[] { "Ctrl+C", "Copy the selected downloads' links" },
            new[] { "Delete", "Remove the selected downloads" },
            new[] { "Enter", "Open the selected download" },
            new[] { "Ctrl+,", "Options" },
            new[] { "F1 or Ctrl+/", "This list" },
            new[] { "Esc", "Clear the search, close a window" },
        };

        // The app's theme (ui.json "theme"): follow Windows, or always light or dark
        public static readonly string[] ThemeModes = { "system", "light", "dark" };
        public static readonly string[] ThemeNames = { "Use Windows setting", "Light", "Dark" };
        public static int ThemeIndex(string mode) { return Math.Max(0, Array.IndexOf(ThemeModes, mode)); } // unknown = Windows

        // Updates (Updater): "v1.2.3" or "1.2.3" against the running version; anything unreadable is never newer
        public static bool IsNewer(string tag, string current)
        {
            var a = VersionParts(tag);
            var b = VersionParts(current);
            if (a == null || b == null) return false;
            for (var i = 0; i < 3; i++) if (a[i] != b[i]) return a[i] > b[i];
            return false;
        }

        static int[] VersionParts(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var m = Regex.Match(v.Trim(), @"^v?(\d+)\.(\d+)(?:\.(\d+))?");
            if (!m.Success) return null;
            return new[] { int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0 };
        }

        // The latest release (GitHub's releases/latest JSON): its tag and the setup's and checksum's download links; null
        // when either asset is missing
        public static string[] ReleaseSetup(IDictionary<string, object> release, string setupName)
        {
            if (release == null) return null;
            string setup = null, sum = null;
            var assets = release.ContainsKey("assets") ? release["assets"] as System.Collections.IEnumerable : null;
            if (assets != null)
                foreach (var a in assets.OfType<IDictionary<string, object>>())
                {
                    var name = a.Str("name");
                    if (name == setupName) setup = a.Str("browser_download_url");
                    else if (name == setupName + ".sha256") sum = a.Str("browser_download_url");
                }
            var tag = release.Str("tag_name");
            return tag == null || setup == null || sum == null ? null : new[] { tag, setup, sum };
        }

        // "<hex>  GlideSetup.exe" (or just the hex): the hash, lower case; null when it isn't 64 hex digits
        public static string Sha256Of(string sumFile)
        {
            var hex = (sumFile ?? "").Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            return Regex.IsMatch(hex, "^[0-9a-fA-F]{64}$") ? hex.ToLowerInvariant() : null;
        }

        // The setup's quiet update: quit the running app, reinstall, start it again; keep the shortcuts as they are
        public static string SetupArgs(bool desktopLink, bool startupLink)
        {
            return "--quiet --quit-app --start" + (desktopLink ? "" : " --no-desktop") + (startupLink ? "" : " --no-startup");
        }

        // A folder inside the download folder from that folder's own name on (Options › Categories: "Downloads\Video"); others as they are
        public static string ShortFolder(string dir, string folder)
        {
            var d = (dir ?? "").TrimEnd('\\', '/');
            if (d == "" || !(folder ?? "").StartsWith(d, StringComparison.OrdinalIgnoreCase) || folder.Length > d.Length && folder[d.Length] != '\\') return folder;
            var i = d.LastIndexOfAny(new[] { '\\', '/' });
            return d.Substring(i + 1) + folder.Substring(d.Length);
        }

        // The settings whose value differs from before (Options' Save sends only these)
        public static Dictionary<string, object> Changed(IDictionary<string, object> before, IDictionary<string, object> after)
        {
            return after.Where(a => { object b; return !before.TryGetValue(a.Key, out b) || !Equals(a.Value, b); }).ToDictionary(a => a.Key, a => a.Value);
        }

        // A typical extension per Category name, for its Windows icon ("" = the generic file)
        public static string TypicalExt(string category)
        {
            switch (category)
            {
                case "Video": return ".mp4";
                case "Music": return ".mp3";
                case "Documents": return ".pdf";
                case "Programs": return ".exe";
                case "Compressed": return ".zip";
                default: return "";
            }
        }

        // The extension whose Windows icon a file gets: its own (".mp4"), else its category's typical one
        public static string IconExt(string name, string category)
        {
            var m = IconExtension.Match(name ?? "");
            return m.Success ? m.Value.ToLowerInvariant() : TypicalExt(category);
        }

        // Download File Info header: the name (a YouTube title without its extension) · the host without www.
        public static string Subtitle(Download d, string name)
        {
            Uri u;
            var host = d.Url != null && Uri.TryCreate(d.Url, UriKind.Absolute, out u) ? Regex.Replace(u.Host, @"^www\.", "") : "";
            return string.Join(" · ", new[] { d.Kind == "video" ? WithExt(name, "") : name, host }.Where(s => !string.IsNullOrEmpty(s)));
        }

        // Text GDI can't draw in color: emoji planes U+1F000–1FAFF, symbols and dingbats U+2600–27BF, the emoji variation
        // selector U+FE0F and the keycap U+20E3. ZWJ sequences are found through their emoji; a ZWJ alone (Arabic, Indic) is not one.
        public static bool HasEmoji(string s)
        {
            if (s == null) return false;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c >= '☀' && c <= '➿' || c == '️' || c == '⃣') return true;
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    var cp = char.ConvertToUtf32(c, s[++i]);
                    if (cp >= 0x1F000 && cp <= 0x1FAFF) return true;
                }
            }
            return false;
        }

        // The sidebar's Types icons: every d="..." as absolute M, L, C, Z steps. A file this can't draw right (bad path data,
        // transforms, other shapes, fill="none") gives null and the sidebar shows its glyph instead of a wrong shape.
        public static SvgShape ParseSvg(string svg)
        {
            svg = svg ?? "";
            var vb = ViewBox.Match(svg);
            double w, h;
            if (!vb.Success || !double.TryParse(vb.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                || !double.TryParse(vb.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out h) || w <= 0 || h <= 0
                || SvgUnsupported.IsMatch(svg)) return null;
            // SVG's default fill rule is nonzero
            var shape = new SvgShape { Width = w, Height = h, EvenOdd = SvgEvenOdd.IsMatch(svg) };
            foreach (Match d in PathData.Matches(svg))
                if (!AddPath(shape.Steps, d.Groups[1].Value)) return null;
            return shape.Steps.Count == 0 ? null : shape;
        }

        // One path's data (SVG 1.1 §8.3), absolute or relative: H and V become lines, S, Q, T and A become cubics
        static bool AddPath(List<SvgStep> steps, string d)
        {
            char op = '\0', prev = '\0';
            double x = 0, y = 0, sx = 0, sy = 0, cx = 0, cy = 0, x1, y1, ex, ey; // pen, subpath start, last control point
            var a = new double[7];
            for (var i = Skip(d, 0); i < d.Length; i = Skip(d, i))
            {
                var letter = char.IsLetter(d[i]);
                if (letter) op = d[i++];
                // numbers without a letter repeat the command; pairs after a move are lines
                else if (op == 'M' || op == 'm') op = op == 'M' ? 'L' : 'l';
                var up = char.ToUpperInvariant(op);
                var n = SvgCommands.IndexOf(up);
                // a path starts with a move; Z takes no numbers
                if (n < 0 || prev == '\0' && up != 'M' || !letter && up == 'Z') return false;
                if (!Args(d, ref i, a, SvgArgCount[n], up == 'A')) return false;
                double ox = char.IsLower(op) ? x : 0, oy = char.IsLower(op) ? y : 0;
                switch (up)
                {
                    case 'M': sx = x = ox + a[0]; sy = y = oy + a[1]; Step(steps, 'M', x, y); break;
                    case 'L': x = ox + a[0]; y = oy + a[1]; Step(steps, 'L', x, y); break;
                    case 'H': x = ox + a[0]; Step(steps, 'L', x, y); break;
                    case 'V': y = oy + a[0]; Step(steps, 'L', x, y); break;
                    // after Z the pen is back at the subpath's start
                    case 'Z': Step(steps, 'Z'); x = sx; y = sy; break;
                    case 'C':
                    case 'S':
                        // S's first control point mirrors the last C or S one, else it is the pen
                        var j = up == 'C' ? 2 : 0;
                        x1 = j > 0 ? ox + a[0] : prev == 'C' || prev == 'S' ? 2 * x - cx : x;
                        y1 = j > 0 ? oy + a[1] : prev == 'C' || prev == 'S' ? 2 * y - cy : y;
                        cx = ox + a[j]; cy = oy + a[j + 1]; x = ox + a[j + 2]; y = oy + a[j + 3];
                        Step(steps, 'C', x1, y1, cx, cy, x, y);
                        break;
                    case 'Q':
                    case 'T':
                        // T's control point mirrors the last Q or T one, else it is the pen
                        if (up == 'Q') { cx = ox + a[0]; cy = oy + a[1]; }
                        else if (prev == 'Q' || prev == 'T') { cx = 2 * x - cx; cy = 2 * y - cy; }
                        else { cx = x; cy = y; }
                        ex = ox + a[up == 'Q' ? 2 : 0]; ey = oy + a[up == 'Q' ? 3 : 1];
                        // a quadratic is the cubic with control points 2/3 of the way from each end to its control point
                        Step(steps, 'C', x + 2 * (cx - x) / 3, y + 2 * (cy - y) / 3, ex + 2 * (cx - ex) / 3, ey + 2 * (cy - ey) / 3, ex, ey);
                        x = ex; y = ey;
                        break;
                    case 'A':
                        ex = ox + a[5]; ey = oy + a[6];
                        Arc(steps, x, y, a[0], a[1], a[2], a[3] != 0, a[4] != 0, ex, ey);
                        x = ex; y = ey;
                        break;
                }
                prev = up;
            }
            return true;
        }

        // An arc as cubics of at most 90° each, from SVG 1.1's endpoint-to-center conversion (implementation notes F.6.5, F.6.6)
        static void Arc(List<SvgStep> steps, double x0, double y0, double rx, double ry, double degrees, bool large, bool sweep, double x, double y)
        {
            if (x0 == x && y0 == y) return;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx == 0 || ry == 0) { Step(steps, 'L', x, y); return; }
            double cos = Math.Cos(degrees * Math.PI / 180), sin = Math.Sin(degrees * Math.PI / 180);
            double dx = (x0 - x) / 2, dy = (y0 - y) / 2, px = cos * dx + sin * dy, py = cos * dy - sin * dx;
            // radii too small to reach the end point grow until they just do
            var scale = px * px / (rx * rx) + py * py / (ry * ry);
            if (scale > 1) { rx *= Math.Sqrt(scale); ry *= Math.Sqrt(scale); }
            double rx2 = rx * rx, ry2 = ry * ry, sum = rx2 * py * py + ry2 * px * px;
            var k = Math.Sqrt(Math.Max(0, (rx2 * ry2 - sum) / sum)) * (large == sweep ? -1 : 1);
            double pcx = k * rx * py / ry, pcy = -k * ry * px / rx;
            double cx = cos * pcx - sin * pcy + (x0 + x) / 2, cy = sin * pcx + cos * pcy + (y0 + y) / 2;
            var from = Math.Atan2((py - pcy) / ry, (px - pcx) / rx);
            var turn = Math.Atan2((-py - pcy) / ry, (-px - pcx) / rx) - from;
            if (sweep && turn < 0) turn += 2 * Math.PI;
            else if (!sweep && turn > 0) turn -= 2 * Math.PI;
            var n = Math.Max(1, (int)Math.Ceiling(Math.Abs(turn) / (Math.PI / 2) - 1e-9));
            var part = turn / n;
            var t = 4.0 / 3 * Math.Tan(part / 4);
            // the ellipse's point and tangent at angle e
            Func<double, double[]> at = e => new[] {
                cx + rx * Math.Cos(e) * cos - ry * Math.Sin(e) * sin, cy + rx * Math.Cos(e) * sin + ry * Math.Sin(e) * cos,
                -rx * Math.Sin(e) * cos - ry * Math.Cos(e) * sin, -rx * Math.Sin(e) * sin + ry * Math.Cos(e) * cos };
            for (var s = 0; s < n; s++)
            {
                double[] p = at(from + s * part), q = at(from + (s + 1) * part);
                // the last segment ends exactly at the given point
                if (s == n - 1) { q[0] = x; q[1] = y; }
                Step(steps, 'C', p[0] + t * p[2], p[1] + t * p[3], q[0] - t * q[2], q[1] - t * q[3], q[0], q[1]);
            }
        }

        // count numbers after a command; an arc's flags (4th and 5th) are one '0' or '1' each, so "01.5" is 0, 1, .5
        static bool Args(string d, ref int i, double[] a, int count, bool arc)
        {
            for (var k = 0; k < count; k++)
            {
                i = Skip(d, i);
                if (!arc || k < 3 || k > 4) { if (!Number(d, ref i, out a[k])) return false; }
                else if (i < d.Length && (d[i] == '0' || d[i] == '1')) a[k] = d[i++] - '0';
                else return false;
            }
            return true;
        }

        // sign, digits, one dot, exponent: a number ends where it can't go on, so "1.5.5" is 1.5 .5 and "-1-2" is -1 -2
        static bool Number(string d, ref int i, out double n)
        {
            var start = i;
            n = 0;
            if (i < d.Length && (d[i] == '+' || d[i] == '-')) i++;
            var digits = Digits(d, ref i);
            if (i < d.Length && d[i] == '.') { i++; digits += Digits(d, ref i); }
            if (digits == 0) return false;
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E'))
            {
                i++;
                if (i < d.Length && (d[i] == '+' || d[i] == '-')) i++;
                if (Digits(d, ref i) == 0) return false;
            }
            return double.TryParse(d.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out n);
        }

        static int Digits(string d, ref int i) { var start = i; while (i < d.Length && d[i] >= '0' && d[i] <= '9') i++; return i - start; }
        static int Skip(string d, int i) { while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; return i; }
        static void Step(List<SvgStep> steps, char op, params double[] args) { steps.Add(new SvgStep { Op = op, Args = args }); }
    }

    // log.txt (log.js's format, the engine writes the same file): "<ISO time> [source] message", more lines indented;
    // past 1 MB the oldest half goes. Never throws: a log that cannot be written must not stop the app.
    public static class Log
    {
        public const int Max = 1 << 20;
        public static string File; // set by Program: next to state.json

        public static void Write(string source, string message)
        {
            var file = File;
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var line = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + " [" + source + "] "
                    + (message ?? "").Replace("\r\n", "\n").Replace("\n", "\n    ") + "\n";
                lock (typeof(Log))
                {
                    System.IO.File.AppendAllText(file, line, new System.Text.UTF8Encoding(false));
                    if (new System.IO.FileInfo(file).Length <= Max) return;
                    var text = System.IO.File.ReadAllText(file);
                    var cut = text.IndexOf('\n', Math.Max(0, text.Length - Max / 2));
                    System.IO.File.WriteAllText(file, cut < 0 ? "" : text.Substring(cut + 1), new System.Text.UTF8Encoding(false));
                }
            }
            catch (Exception) { }
        }
    }
}
