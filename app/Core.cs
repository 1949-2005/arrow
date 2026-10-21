// Pure logic for the native app: JSON, the /api/state model, and the text helpers ported from
// ui.html, confirm.html and progress.html (same behavior; numbers and dates follow the user's regional format).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Arrow
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
        public string WhenDone;
        public int? PowerIn;
        public List<string> Exts;
        public Dictionary<string, List<string>> Categories;
        public int Open;

        public static State From(object json)
        {
            var o = json as IDictionary<string, object>;
            var s = new State
            {
                Settings = Settings.From(o.Obj("settings")),
                Downloads = (o.List("downloads") ?? new List<object>()).Select(x => Download.From(x as IDictionary<string, object>)).ToList(),
                WhenDone = o.Str("whenDone") ?? "",
                PowerIn = o.Int("powerIn"),
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
        public string Dir, ScheduleAt;
        public int Connections, MaxActive, SpeedLimitKB;
        public bool Categories, Clipboard, Startup;

        public static Settings From(IDictionary<string, object> o)
        {
            return new Settings
            {
                Dir = o.Str("dir") ?? "",
                Connections = o.Int("connections") ?? 0,
                MaxActive = o.Int("maxActive") ?? 0,
                SpeedLimitKB = o.Int("speedLimitKB") ?? 0,
                ScheduleAt = o.Str("scheduleAt") ?? "",
                Categories = o.Bool("categories") ?? false,
                Clipboard = o.Bool("clipboard") ?? false,
                Startup = o.Bool("startup") ?? false,
            };
        }
    }

    public class HlsInfo
    {
        public long Done, Bytes, Total;
    }

    public class Download
    {
        public string Id, Url, Name, File, Status, Error, Kind, Title, Phase, Quality, Format;
        public long? Size;
        public bool? Resumable;
        public HlsInfo Hls;
        public long Added, Got, Speed;
        public int Connections;
        public Probe Probe;
        public List<long[]> Segments; // [start, end, pos]; null when the engine sends none

        public static Download From(IDictionary<string, object> o)
        {
            var h = o.Obj("hls");
            var segs = o.List("segments");
            return new Download
            {
                Id = o.Str("id"), Url = o.Str("url"), Name = o.Str("name"), File = o.Str("file"), Status = o.Str("status"), Error = o.Str("error"),
                Kind = o.Str("kind"), Title = o.Str("title"), Phase = o.Str("phase"), Quality = o.Str("quality"), Format = o.Str("format"),
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

        public static Probe From(IDictionary<string, object> o)
        {
            if (o == null) return null;
            return new Probe
            {
                Name = o.Str("name"), Category = o.Str("category"), Type = o.Str("type"), Error = o.Str("error"), Format = o.Str("format"),
                Size = o.Long("size"), Listing = o.Bool("listing") ?? false,
                Options = (o.List("options") ?? new List<object>()).Select(x => FormatOption.From(x as IDictionary<string, object>)).ToList(),
            };
        }
    }

    public class FormatOption
    {
        public string Key, Group, Label, Tag;
        public long? Size;

        public static FormatOption From(IDictionary<string, object> o)
        {
            return new FormatOption { Key = o.Str("key"), Group = o.Str("group"), Label = o.Str("label"), Tag = o.Str("tag"), Size = o.Long("size") };
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

    public static class Core
    {
        static readonly Dictionary<string, string> Label = new Dictionary<string, string> { { "queued", "Queued" }, { "downloading", "Downloading" }, { "paused", "Paused" }, { "done", "Complete" }, { "scheduled", "Scheduled" }, { "removed", "Removing" } };
        static readonly Dictionary<string, string> PhaseText = new Dictionary<string, string> { { "video", "Downloading video" }, { "audio", "Downloading audio" }, { "merging", "Merging" }, { "converting", "Converting to MP3" } };
        static readonly Dictionary<string, string> HeadingText = new Dictionary<string, string> { { "queued", "Queued" }, { "scheduled", "Scheduled" }, { "error", "Error" }, { "done", "Download complete" }, { "pending", "Waiting" }, { "removed", "Removing" } };
        // [A-Za-z0-9_] and [0-9]: .NET's \w and \d also match non-ASCII letters and digits, JavaScript's do not
        static readonly Regex Ext = new Regex(@"\.([A-Za-z0-9_]+)$");
        static readonly Regex AudioKey = new Regex("^(m4a|mp3)");
        static readonly Regex Batch = new Regex(@"\[([0-9]+)-([0-9]+)\]");
        static readonly Regex Link = new Regex(@"^https?://\S+$", RegexOptions.IgnoreCase);
        static readonly Regex YouTube = new Regex(@"^https?://((www|m|music)\.)?youtube\.com/|^https?://youtu\.be/", RegexOptions.IgnoreCase);
        static readonly Regex LastExt = new Regex(@"\.[^.\\/]*$");

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

        public static string ShortName(Download d)
        {
            if (!string.IsNullOrEmpty(d.Name)) return d.Name;
            if (d.Kind == "video") return "YouTube video";
            var url = d.Url ?? "";
            var last = Uri.UnescapeDataString(url.Split('?')[0].Split('/').Last());
            return last != "" ? last : url;
        }

        public static string Eta(Download d)
        {
            var t = Total(d);
            return d.Status == "downloading" && d.Speed != 0 && t != null && t != 0 && !double.IsNaN(t.Value) ? Time((t.Value - d.Got) / d.Speed) : "";
        }

        public static bool CanResume(Download d) { return d.Status == "paused" || d.Status == "error" || d.Status == "scheduled"; }
        public static bool CanStop(Download d) { return d.Status == "downloading" || d.Status == "queued" || d.Status == "scheduled"; }
        public static bool CanPause(Download d) { return CanStop(d); }
        public static bool IsYouTube(string url) { return url != null && YouTube.IsMatch(url); }

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

        // Category folder, as engine.js assignName(): <dir>\<Category>, Other and categories off go straight into <dir>
        public static string FolderFor(Settings settings, string category)
        {
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
            return string.Join(" · ", audio ? o.Label : o.Group, audio ? "audio" : o.Label, Size(o.Size));
        }
        public static string FormatExt(string key) { return key == "best" ? ".mp4" : "." + key.Split(':')[0]; }
        public static string FormatCategory(string key) { return AudioKey.IsMatch(key) ? "Music" : "Video"; }

        // Download File Info header: the name (a YouTube title without its extension) · the host without www.
        public static string Subtitle(Download d, string name)
        {
            Uri u;
            var host = d.Url != null && Uri.TryCreate(d.Url, UriKind.Absolute, out u) ? Regex.Replace(u.Host, @"^www\.", "") : "";
            return string.Join(" · ", new[] { d.Kind == "video" ? WithExt(name, "") : name, host }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }
}
