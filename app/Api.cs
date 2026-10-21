// The engine's local HTTP API (server.js) and the /api/state poller.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Arrow
{
    public class ApiException : Exception
    {
        public bool Offline;
        public ApiException(string message, bool offline) : base(message) { Offline = offline; }
    }

    public class Api
    {
        public static string AppName = "Arrow"; // Program sets the brand name
        public readonly string Base;

        public Api()
        {
            int port;
            Base = "http://127.0.0.1:" + (int.TryParse(Environment.GetEnvironmentVariable("FASTDL_PORT"), out port) && port > 0 ? port : 9614);
        }

        public object Get(string path) { return Send("GET", path, null); }
        public object Post(string path, object body) { return Send("POST", path, Json.Serialize(body ?? new Dictionary<string, object>())); }

        public bool Ping()
        {
            try
            {
                var o = Get("/api/ping") as IDictionary<string, object>;
                return o.Str("app") == "fastdl";
            }
            catch (ApiException) { return false; }
        }

        object Send(string method, string path, string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(Base + path);
            req.Method = method;
            req.Proxy = null; // a system proxy would slow every call
            req.Timeout = req.ReadWriteTimeout = path == "/api/formats" ? 130000 : method == "GET" ? 3000 : 15000;
            req.ServicePoint.ConnectionLimit = 16; // the default 2 would queue the poller behind a slow /api/formats
            req.ServicePoint.Expect100Continue = false;
            try
            {
                if (body != null)
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    req.ContentType = "application/json";
                    req.ContentLength = bytes.Length;
                    using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                }
                using (var res = (HttpWebResponse)req.GetResponse()) return Read(res);
            }
            catch (WebException e)
            {
                var res = e.Response as HttpWebResponse;
                if (res == null) throw new ApiException(AppName + "'s engine is not running.", true);
                using (res)
                {
                    var o = Read(res) as IDictionary<string, object>;
                    throw new ApiException(o.Str("error") ?? "HTTP " + (int)res.StatusCode, false);
                }
            }
        }

        static object Read(HttpWebResponse res)
        {
            using (var r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
            {
                try { return Json.Parse(r.ReadToEnd()); }
                catch (ArgumentException) { throw new ApiException("Unexpected answer from the engine (HTTP " + (int)res.StatusCode + ").", false); }
            }
        }
    }

    // GET /api/state every 700 ms on a background thread, never overlapping. Events fire on that thread: marshalling to the UI is the caller's job.
    public class Poller
    {
        readonly Api api;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile object run; // the running loop's token: Stop() clears it, so a Start() right after never leaves two loops

        public event Action<State> Updated;
        public event Action<ApiException> Failed;

        public Poller(Api api) { this.api = api; }

        public void Start()
        {
            if (run != null) return;
            var token = run = new object();
            new Thread(() => Loop(token)) { IsBackground = true, Name = "poller" }.Start();
        }

        public void Stop() { run = null; wake.Set(); }
        public void PollNow() { wake.Set(); }

        void Loop(object token)
        {
            while (run == token)
            {
                State s = null;
                ApiException error = null;
                try { s = State.From(api.Get("/api/state")); }
                catch (ApiException e) { error = e; }
                if (run != token) return;
                var updated = Updated;
                var failed = Failed;
                if (s != null && updated != null) updated(s);
                if (error != null && failed != null) failed(error);
                wake.WaitOne(700);
            }
        }
    }
}
