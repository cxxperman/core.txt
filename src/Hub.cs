// core.txt. Hub.cs — хаб экосистемы: одна точка на компьютере (127.0.0.1:42080), через которую приложения
// видят друг друга, сообщают состояние, отдают свои настройки и принимают команды.
// Протокол описан в kit.txt (hub/PROTOCOL.md); образец поведения — hub/hub.js оттуда же, и те же проверки
// (hub/test.js) гоняются против этого файла. Менять поведение можно только вместе с ними.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevTxt.Core
{
    class HubClient
    {
        public string Id, App;
        public Dictionary<string, object> Info;
        public readonly List<string> Subs = new List<string>();
        public Action<Dictionary<string, object>> Send;
        public Action Kick;
    }

    // Сам хаб: кто подключён, что они сказали последним, кто что слушает. О сети ничего не знает.
    class Hub
    {
        public const int Protocol = 1, DefaultPort = 42080;
        static readonly string[] Origins = { "discord.com", "discordapp.com", "steamloopback.host" };
        static readonly Regex AppName = new Regex("^[a-z][a-z0-9-]{0,31}$"), Hex = new Regex("^#[0-9a-fA-F]{6}$");

        readonly object gate = new object();
        readonly List<HubClient> clients = new List<HubClient>();
        readonly Dictionary<string, KeyValuePair<string, object>> retained = new Dictionary<string, KeyValuePair<string, object>>();   // тема → (от кого, данные)
        string mode = "dark", accent;
        bool reducedMotion;
        int seq;

        public string Name = "core.txt", Version = "";
        public event Action ThemeChanged;   // в потоке того, кто прислал команду

        public Hub(string accent, bool reducedMotion) { this.accent = accent; this.reducedMotion = reducedMotion; }

        public string Accent { get { lock (gate) return accent; } }
        public bool ReducedMotion { get { lock (gate) return reducedMotion; } }

        // Страница в браузере всегда присылает Origin; пускаем только страницы внутри Discord и Steam
        public static bool OriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;   // обычная программа на этом компьютере
            Uri u;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https")) return false;
            string host = u.Host.ToLowerInvariant();
            foreach (string d in Origins) if (host == d || host.EndsWith("." + d)) return true;
            return false;
        }

        public static bool TopicMatches(string pattern, string topic)
        {
            return pattern == "*" || pattern == topic || (pattern.EndsWith(".*") && topic.StartsWith(pattern.Substring(0, pattern.Length - 1)));
        }

        public static Dictionary<string, object> Msg(params object[] pairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < pairs.Length; i += 2) d[(string)pairs[i]] = pairs[i + 1];
            return d;
        }

        static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }

        public HubClient Open(Action<Dictionary<string, object>> send, Action kick)
        {
            lock (gate)
            {
                var c = new HubClient { Id = "c" + (++seq) + "-" + Guid.NewGuid().ToString("N").Substring(0, 12), Send = send, Kick = kick };
                clients.Add(c);
                return c;
            }
        }

        public void Close(HubClient c)
        {
            lock (gate)
            {
                if (!clients.Remove(c) || c.App == null) return;
                var gone = new List<string>();
                foreach (var r in retained) if (r.Value.Key == c.App) gone.Add(r.Key);
                foreach (string topic in gone)
                {
                    retained.Remove(topic);
                    FanOut(Msg("type", "state", "topic", topic, "from", c.App, "data", null), c);
                }
                PublishApps();
            }
        }

        HubClient ByApp(string app) { return clients.Find(delegate(HubClient c) { return c.App == app; }); }

        object[] Apps()
        {
            var list = new List<object>();
            foreach (var c in clients) if (c.App != null) list.Add(c.Info);
            return list.ToArray();
        }

        Dictionary<string, object> Theme() { return Msg("mode", mode, "accent", accent, "reducedMotion", reducedMotion); }

        void PublishApps() { FanOut(Msg("type", "state", "topic", "hub.apps", "from", "hub", "data", Apps()), null); }

        void FanOut(Dictionary<string, object> m, HubClient except)
        {
            string topic = (string)m["topic"];
            foreach (var c in clients.ToArray())
                if (c != except && c.App != null && c.Subs.Exists(delegate(string p) { return TopicMatches(p, topic); })) c.Send(m);
        }

        // Меняет общий вид от имени самого core.txt (его окно настроек) — то же, что команда theme.set
        public void SetTheme(string newAccent, bool? newReduced)
        {
            lock (gate)
            {
                if (newAccent == "" || (newAccent != null && Hex.IsMatch(newAccent))) accent = newAccent == "" ? null : newAccent;
                if (newReduced.HasValue) reducedMotion = newReduced.Value;
                FanOut(Msg("type", "state", "topic", "hub.theme", "from", "hub", "data", Theme()), null);
            }
        }

        public void Receive(HubClient c, Dictionary<string, object> m)
        {
            if (m == null) return;
            string type = Get(m, "type") as string;
            if (type == null) return;
            bool themeChanged = false;
            Action kick = null;
            lock (gate)
            {
                if (type == "ping") { c.Send(Msg("type", "pong")); return; }
                if (type == "bye") { Close(c); kick = c.Kick; }
                else if (type == "hello")
                {
                    if (c.App != null) return;
                    string app = Get(m, "app") as string;
                    if (app == null || !AppName.IsMatch(app) || app == "hub") { c.Send(Msg("type", "error", "code", "bad-app")); return; }
                    double v;
                    if (!double.TryParse(Convert.ToString(Get(m, "v"), System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) || (int)Math.Floor(v) != Protocol)
                    { c.Send(Msg("type", "error", "code", "bad-version", "v", Protocol)); return; }
                    HubClient old = ByApp(app);   // то же приложение запустили ещё раз: остаётся новое
                    if (old != null) { Close(old); kick = old.Kick; }
                    var commands = new List<object>();
                    var given = Get(m, "commands") as IEnumerable;
                    if (given != null && !(given is string)) foreach (object o in given) if (o is string && commands.Count < 64) commands.Add(o);
                    string name = Get(m, "name") as string, version = Get(m, "version") as string;
                    c.App = app;
                    c.Info = Msg("app", app, "name", name == null ? app : name.Substring(0, Math.Min(64, name.Length)), "version", version == null ? "" : version.Substring(0, Math.Min(32, version.Length)), "commands", commands.ToArray());
                    c.Send(Msg("type", "welcome", "v", Protocol, "hub", Msg("name", Name, "version", Version), "client", c.Id, "apps", Apps()));
                    PublishApps();
                }
                else if (c.App == null) { c.Send(Msg("type", "error", "code", "hello-first")); return; }
                else if (type == "subscribe")
                {
                    var topics = Get(m, "topics") as IEnumerable;
                    if (topics == null || topics is string) return;
                    foreach (object o in topics)
                    {
                        string p = o as string;
                        if (p == null || c.Subs.Contains(p) || c.Subs.Count >= 64) continue;
                        c.Subs.Add(p);
                        if (TopicMatches(p, "hub.apps")) c.Send(Msg("type", "state", "topic", "hub.apps", "from", "hub", "data", Apps()));
                        if (TopicMatches(p, "hub.theme")) c.Send(Msg("type", "state", "topic", "hub.theme", "from", "hub", "data", Theme()));
                        foreach (var r in retained)
                            if (r.Value.Key != c.App && TopicMatches(p, r.Key)) c.Send(Msg("type", "state", "topic", r.Key, "from", r.Value.Key, "data", r.Value.Value));
                    }
                }
                else if (type == "state" || type == "event")
                {
                    // Приложение говорит только от своего имени: «aura.fx» может прислать только aura
                    string topic = Get(m, "topic") as string;
                    if (topic == null || !topic.StartsWith(c.App + ".")) { c.Send(Msg("type", "error", "code", "bad-topic", "topic", topic)); return; }
                    object data = Get(m, "data");
                    if (type == "state")
                    {
                        if (data == null) retained.Remove(topic); else retained[topic] = new KeyValuePair<string, object>(c.App, data);
                    }
                    FanOut(Msg("type", type, "topic", topic, "from", c.App, "data", data), c);
                }
                else if (type == "command")
                {
                    object id = Get(m, "id");
                    string to = Get(m, "to") as string, name = Get(m, "name") as string;
                    if (to == null || name == null) { c.Send(Msg("type", "result", "id", id, "from", to, "ok", false, "error", "bad-command")); return; }
                    if (to == "hub")
                    {
                        if (name != "theme.set") { c.Send(Msg("type", "result", "id", id, "from", to, "ok", false, "error", "unknown-command")); return; }
                        var a = Get(m, "args") as Dictionary<string, object> ?? new Dictionary<string, object>();
                        string md = Get(a, "mode") as string;
                        if (md == "dark" || md == "light") mode = md;
                        if (a.ContainsKey("accent"))
                        {
                            string ac = a["accent"] as string;
                            if (a["accent"] == null) accent = null; else if (ac != null && Hex.IsMatch(ac)) accent = ac;
                        }
                        if (Get(a, "reducedMotion") is bool) reducedMotion = (bool)a["reducedMotion"];
                        FanOut(Msg("type", "state", "topic", "hub.theme", "from", "hub", "data", Theme()), null);
                        c.Send(Msg("type", "result", "id", id, "from", to, "ok", true, "data", Theme()));
                        themeChanged = true;
                    }
                    else
                    {
                        HubClient target = ByApp(to);
                        if (target == null) c.Send(Msg("type", "result", "id", id, "from", to, "ok", false, "error", "offline"));
                        else target.Send(Msg("type", "command", "id", id, "from", c.App, "name", name, "args", Get(m, "args") ?? new Dictionary<string, object>()));
                    }
                }
                else if (type == "result")
                {
                    HubClient target = Get(m, "to") is string ? ByApp((string)m["to"]) : null;
                    if (target != null)
                    {
                        var r = Msg("type", "result", "id", Get(m, "id"), "from", c.App, "ok", Get(m, "ok") is bool && (bool)m["ok"]);
                        if (m.ContainsKey("data")) r["data"] = m["data"];
                        if (m.ContainsKey("error")) r["error"] = m["error"];
                        target.Send(r);
                    }
                }
                // Всё остальное — из более новой версии протокола: пропускается молча
            }
            if (kick != null) kick();
            if (themeChanged && ThemeChanged != null) ThemeChanged();
        }
    }

    // Доставка: WebSocket и обычный HTTP на одном порту, только для этого компьютера
    class HubServer
    {
        const int MaxMessage = 1024 * 1024, PollHoldMs = 20000, HttpTtlMs = 45000;
        readonly Hub hub;
        HttpListener listener;
        Timer sweeper;
        public string Error;

        class Peer
        {
            public HubClient Client;
            public readonly List<object> Queue = new List<object>();
            public HttpListenerContext Waiter;
            public int WaiterUntil, Seen;
        }
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();

        public HubServer(Hub hub) { this.hub = hub; }

        public bool Start(int port)
        {
            if (listener != null) return true;
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://127.0.0.1:" + port + "/");   // только этот компьютер, снаружи порт не виден
                l.Start();
                listener = l;
                Error = null;
            }
            catch (Exception) { Error = "порт " + port + " занят другой программой"; return false; }
            new Thread(Loop) { IsBackground = true }.Start(listener);
            sweeper = new Timer(delegate { Sweep(); }, null, 1000, 1000);
            return true;
        }

        public void Stop()
        {
            var l = listener; listener = null;
            if (sweeper != null) sweeper.Dispose();
            try { if (l != null) l.Close(); } catch { }
        }

        void Loop(object o)
        {
            var l = (HttpListener)o;
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); } catch { return; }
                var c = ctx;
                new Thread(delegate() { try { Serve(c); } catch { try { c.Response.Abort(); } catch { } } }) { IsBackground = true }.Start();
            }
        }

        static void Reply(HttpListenerContext ctx, int code, string json)
        {
            try
            {
                ctx.Response.StatusCode = code;
                if (json != null)
                {
                    byte[] b = Encoding.UTF8.GetBytes(json);
                    ctx.Response.ContentType = "application/json";
                    ctx.Response.Headers["Cache-Control"] = "no-store";
                    ctx.Response.ContentLength64 = b.Length;
                    ctx.Response.OutputStream.Write(b, 0, b.Length);
                }
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }

        void Serve(HttpListenerContext ctx)
        {
            var rq = ctx.Request;
            string path = rq.Url.AbsolutePath;
            if (!Hub.OriginAllowed(rq.Headers["Origin"])) { Reply(ctx, 403, null); return; }
            if (path == "/hub" && rq.IsWebSocketRequest) { ServeSocket(ctx); return; }
            if (path == "/hub" && rq.HttpMethod == "GET") { Reply(ctx, 200, Json.Write(Hub.Msg("hub", hub.Name, "version", hub.Version, "v", Hub.Protocol))); return; }
            if (path != "/hub/http") { Reply(ctx, 404, null); return; }

            if (rq.HttpMethod == "GET")
            {
                Peer p;
                lock (peers) peers.TryGetValue(rq.QueryString["client"] ?? "", out p);
                if (p == null) { Reply(ctx, 410, "{}"); return; }
                HttpListenerContext previous;
                object[] ready = null;
                lock (p)
                {
                    p.Seen = Environment.TickCount;
                    previous = p.Waiter;   // у подключения один ждущий запрос
                    p.Waiter = null;
                    if (p.Queue.Count > 0) { ready = p.Queue.ToArray(); p.Queue.Clear(); }
                    else { p.Waiter = ctx; p.WaiterUntil = Environment.TickCount + PollHoldMs; }
                }
                if (previous != null) Reply(previous, 200, "{\"messages\":[]}");
                if (ready != null) Reply(ctx, 200, Json.Write(Hub.Msg("messages", ready)));
                return;
            }
            if (rq.HttpMethod != "POST") { Reply(ctx, 405, "{}"); return; }
            if (rq.ContentLength64 > MaxMessage) { Reply(ctx, 413, "{}"); return; }
            Dictionary<string, object> body;
            try
            {
                using (var r = new StreamReader(rq.InputStream, Encoding.UTF8)) body = Json.Parse(r.ReadToEnd());
                if (body == null) throw new Exception();
            }
            catch { Reply(ctx, 400, "{}"); return; }

            Peer peer;
            string id = Json.Str(body, "client");
            if (id == null)
            {
                var np = new Peer { Seen = Environment.TickCount };
                np.Client = hub.Open(delegate(Dictionary<string, object> m) { Push(np, m); }, delegate { Drop(np); });
                lock (peers) peers[np.Client.Id] = np;
                peer = np;
            }
            else
            {
                lock (peers) peers.TryGetValue(id, out peer);
                if (peer == null) { Reply(ctx, 410, "{}"); return; }
                lock (peer) peer.Seen = Environment.TickCount;
            }
            foreach (object o in Json.List(body, "messages")) hub.Receive(peer.Client, o as Dictionary<string, object>);
            object[] outgoing = new object[0];
            lock (peer) { if (peer.Waiter == null && peer.Queue.Count > 0) { outgoing = peer.Queue.ToArray(); peer.Queue.Clear(); } }
            Reply(ctx, 200, Json.Write(Hub.Msg("client", peer.Client.Id, "messages", outgoing)));
        }

        void Push(Peer p, Dictionary<string, object> m)
        {
            HttpListenerContext waiter = null;
            object[] ready = null;
            lock (p)
            {
                p.Queue.Add(m);
                if (p.Waiter != null) { waiter = p.Waiter; p.Waiter = null; p.Seen = Environment.TickCount; ready = p.Queue.ToArray(); p.Queue.Clear(); }
            }
            if (waiter != null) Reply(waiter, 200, Json.Write(Hub.Msg("messages", ready)));
        }

        void Drop(Peer p)
        {
            lock (peers) peers.Remove(p.Client.Id);
            HttpListenerContext waiter;
            lock (p) { waiter = p.Waiter; p.Waiter = null; }
            if (waiter != null) Reply(waiter, 410, "{}");
        }

        void Sweep()
        {
            Peer[] all;
            lock (peers) { all = new Peer[peers.Count]; peers.Values.CopyTo(all, 0); }
            int now = Environment.TickCount;
            foreach (var p in all)
            {
                HttpListenerContext late = null;
                bool dead = false;
                lock (p)
                {
                    if (p.Waiter != null) { if (now - p.WaiterUntil >= 0) { late = p.Waiter; p.Waiter = null; p.Seen = now; } }
                    else if (now - p.Seen > HttpTtlMs) dead = true;
                }
                if (late != null) Reply(late, 200, "{\"messages\":[]}");
                if (dead) { Drop(p); hub.Close(p.Client); }
            }
        }

        void ServeSocket(HttpListenerContext ctx)
        {
            WebSocket ws = ctx.AcceptWebSocketAsync(null).Result.WebSocket;
            var outgoing = new BlockingCollection<string>();
            HubClient client = hub.Open(
                delegate(Dictionary<string, object> m) { try { outgoing.Add(Json.Write(m)); } catch { } },
                delegate { try { ws.Abort(); } catch { } });
            // Отправка — своим потоком: медленный получатель не задерживает хаб
            new Thread(delegate()
            {
                try
                {
                    foreach (string text in outgoing.GetConsumingEnumerable())
                        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, CancellationToken.None).Wait();
                }
                catch { try { ws.Abort(); } catch { } }
            }) { IsBackground = true }.Start();
            try
            {
                var buf = new byte[16384];
                var ms = new MemoryStream();
                while (ws.State == WebSocketState.Open)
                {
                    var r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > MaxMessage) break;
                    if (!r.EndOfMessage) continue;
                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);
                    Dictionary<string, object> m = null;
                    try { m = Json.Parse(json); } catch { }
                    hub.Receive(client, m);
                }
            }
            catch { }
            finally
            {
                hub.Close(client);
                outgoing.CompleteAdding();
                try { ws.Abort(); ws.Dispose(); } catch { }
            }
        }
    }
}
