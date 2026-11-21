// core.txt. Releases.cs — что выложено на GitHub и как это скачать.
// Единственный источник — релизы репозитория приложения. Скачанный файл должен совпасть с тем, что указано
// в релизе: по размеру и по контрольной сумме SHA-256. Не совпал — файл удаляется и не запускается.
// Есть у релиза карта блоков — изменившееся докачивается к прежнему файлу из запаса (BlockMap.cs), проверка та же.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DevTxt.Core
{
    class Release
    {
        public Version Version;
        public string Url, Sha, Name;
        public long Size;
        public string MapUrl;   // карта блоков рядом с файлом (X.exe.blockmap) — по ней обновление докачивает только изменившееся

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "version", Version.ToString() }, { "url", Url }, { "sha", Sha ?? "" }, { "name", Name }, { "size", Size.ToString() }, { "map", MapUrl ?? "" } };
        }

        public static Release From(Dictionary<string, object> d)
        {
            Version v; long size;
            if (d == null || !Version.TryParse(Json.Str(d, "version") ?? "", out v) || !long.TryParse(Json.Str(d, "size") ?? "", out size)) return null;
            string url = Json.Str(d, "url"), name = Json.Str(d, "name");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) return null;
            string map = Json.Str(d, "map");
            if (map != null && !map.StartsWith("https://github.com/", StringComparison.Ordinal)) map = null;   // в старых записях карты нет
            return new Release { Version = v, Url = url, Sha = Json.Str(d, "sha"), Name = name, Size = size, MapUrl = map };
        }
    }

    static class Releases
    {
        static readonly Regex Tag = new Regex(@"^v?(\d+\.\d+\.\d+)$");

        static HttpWebRequest Request(string url)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "core.txt/" + Program.VersionText;
            rq.Accept = "application/vnd.github+json, application/json, */*";
            rq.Timeout = 15000;
            rq.ReadWriteTimeout = 30000;
            return rq;
        }

        public static string GetText(string url)
        {
            try
            {
                using (var rs = Request(url).GetResponse())
                using (var r = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
            }
            catch (WebException e)
            {
                // GitHub без входа в аккаунт отвечает на 60 запросов в час с одного компьютера; дальше — 403 или 429
                var http = e.Response as HttpWebResponse;
                if (http != null && ((int)http.StatusCode == 403 || (int)http.StatusCode == 429) && http.Headers["X-RateLimit-Remaining"] == "0")
                    throw new GitHubLimit(GitHubLimit.Until(http.Headers["X-RateLimit-Reset"], DateTime.UtcNow));
                throw;
            }
        }

        // Небольшой файл целиком, в память (сборка Vencord для Maska)
        public static byte[] GetBytes(string url)
        {
            using (var rs = Request(url).GetResponse())
            using (var input = rs.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                input.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static bool AssetMatches(string pattern, string name)
        {
            return Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
        }

        // Самая новая версия, в релизе которой есть нужный файл. Черновики не считаются, предварительные — считаются:
        // пока приложения в альфе, все их релизы помечены как предварительные.
        public static Release Latest(AppEntry app)
        {
            return Pick(app, GetText("https://api.github.com/repos/" + app.Repo + "/releases?per_page=20"));
        }

        public static Release Pick(AppEntry app, string json)
        {
            Release best = null;
            string prefix = "https://github.com/" + app.Repo + "/releases/download/";
            var list = Json.ParseAny(json) as System.Collections.IEnumerable;
            if (list == null) return null;
            foreach (object o in list)
            {
                var rel = o as Dictionary<string, object>;
                if (rel == null) continue;
                object draft;
                if (rel.TryGetValue("draft", out draft) && draft is bool && (bool)draft) continue;
                var m = Tag.Match(Json.Str(rel, "tag_name") ?? "");
                if (!m.Success) continue;
                var v = new Version(m.Groups[1].Value);
                if (best != null && v <= best.Version) continue;
                foreach (object ao in Json.List(rel, "assets"))
                {
                    var asset = ao as Dictionary<string, object>;
                    string name = Json.Str(asset, "name"), url = Json.Str(asset, "browser_download_url");
                    if (name == null || url == null || !AssetMatches(app.Asset, name)) continue;
                    if (!url.StartsWith(prefix, StringComparison.Ordinal)) continue;   // файл только из релиза этого же репозитория
                    string digest = Json.Str(asset, "digest") ?? "";
                    object size;
                    asset.TryGetValue("size", out size);
                    best = new Release
                    {
                        Version = v, Url = url, Name = name, Size = Convert.ToInt64(size ?? 0),
                        Sha = digest.StartsWith("sha256:") ? digest.Substring(7).Trim().ToLowerInvariant() : null
                    };
                    // Карта блоков — ровно «имя файла + .blockmap», из того же релиза того же репозитория
                    foreach (object mo in Json.List(rel, "assets"))
                    {
                        var ma = mo as Dictionary<string, object>;
                        string mapUrl = Json.Str(ma, "browser_download_url");
                        if (Json.Str(ma, "name") == name + ".blockmap" && mapUrl != null && mapUrl.StartsWith(prefix, StringComparison.Ordinal)) { best.MapUrl = mapUrl; break; }
                    }
                    break;
                }
            }
            return best;
        }

        // Сколько скачано, по-человечески: «34 из 151 МБ»; у маленьких файлов — с десятыми: «0,4 из 2,1 МБ»
        public static string Megabytes(long done, long total)
        {
            const double mb = 1024 * 1024;
            if (total <= 0) return "";
            done = Math.Max(0, Math.Min(done, total));
            if (total >= 10 * mb) return Math.Floor(done / mb) + " из " + Math.Round(total / mb) + " МБ";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return (Math.Floor(done / mb * 10) / 10).ToString("0.0", inv).Replace('.', ',') + " из " + Math.Max(0.1, total / mb).ToString("0.0", inv).Replace('.', ',') + " МБ";
        }

        // Скачивает файл релиза во временную папку и возвращает путь к нему. progress — сколько байт скачано и сколько всего.
        public static string Download(Release r, Action<long, long> progress) { return Download(r, null, progress); }

        // stash — id приложения, для которого держится запас (null — не держится, как у самого core.txt). Если у релиза
        // есть карта блоков, а в запасе лежит прежний файл со своей картой, качается только изменившееся, и progress
        // считает именно то, что идёт из сети. Скачанный файл с картой ложится в запас, и тогда возвращается путь в запасе.
        public static string Download(Release r, string stash, Action<long, long> progress)
        {
            if (string.IsNullOrEmpty(r.Sha)) throw new Exception("В релизе нет контрольной суммы файла — ставить его небезопасно");
            string dir = Path.Combine(Path.GetTempPath(), "core.txt");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Path.GetFileName(r.Name)), part = file + ".part";
            byte[] map = null;
            if (stash != null && r.MapUrl != null)
            {
                try { map = GetBytes(r.MapUrl); } catch { }
                if (BlockMap.Parse(map) == null) map = null;   // непонятная карта — как будто её нет
            }
            if (map == null || !TryPatch(r, stash, map, part, progress)) Full(r, part, progress);
            try
            {
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
            }
            catch { TryDelete(part); throw; }
            if (map != null) return Stash.Put(stash, file, map);
            if (stash != null && r.MapUrl == null) Stash.Clear(stash);   // у релиза карты нет — прежний запас больше не пригодится
            return file;
        }

        // Сборка по картам. Любая неудача — false, без шума: дальше файл качается целиком
        internal static bool TryPatch(Release r, string id, byte[] map, string part, Action<long, long> progress)
        {
            try
            {
                string oldFile;
                byte[] oldMap;
                if (!Stash.Find(id, out oldFile, out oldMap)) return false;
                PatchPlan plan = Patch.Make(BlockMap.Parse(oldMap), new FileInfo(oldFile).Length, BlockMap.Parse(map), r.Size);
                if (plan == null) return false;
                string hash;
                int last = Environment.TickCount;
                using (var input = new FileStream(oldFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = File.Create(part))
                    hash = Patch.Assemble(plan, input, delegate(long at, long length) { return Range(r.Url, at, length); }, output,
                        delegate(long got) { if (progress != null && Environment.TickCount - last > 80) { last = Environment.TickCount; progress(got, plan.Download); } });
                if (new FileInfo(part).Length != r.Size || hash != r.Sha) { TryDelete(part); return false; }
                if (progress != null) progress(plan.Download, plan.Download);
                return true;
            }
            catch { TryDelete(part); return false; }
        }

        // Байты файла с at, ровно length штук. Сервер, который вместо куска отдаёт весь файл (200) или не тот кусок, —
        // отказ: такой ответ нельзя вклеить в файл
        internal static Stream Range(string url, long at, long length)
        {
            var rq = Request(url);
            rq.Timeout = 30000;
            rq.AddRange(at, at + length - 1);
            var rs = (HttpWebResponse)rq.GetResponse();
            try
            {
                string range = rs.Headers["Content-Range"] ?? "";
                if (rs.StatusCode != HttpStatusCode.PartialContent || !range.StartsWith("bytes " + at + "-" + (at + length - 1) + "/", StringComparison.Ordinal))
                    throw new Exception("the server did not send the asked range");
                return rs.GetResponseStream();   // закрытие потока освобождает и ответ
            }
            catch { rs.Close(); throw; }
        }

        static void Full(Release r, string part, Action<long, long> progress)
        {
            try
            {
                var rq = Request(r.Url);
                rq.Timeout = 30000;
                string hash;
                long total = 0;
                using (var rs = rq.GetResponse())
                using (var input = rs.GetResponseStream())
                using (var output = File.Create(part))
                using (var sha = SHA256.Create())
                {
                    var buf = new byte[81920];
                    int n, last = Environment.TickCount;
                    while ((n = input.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        sha.TransformBlock(buf, 0, n, null, 0);
                        total += n;
                        if (total > r.Size) throw new Exception("Файл больше, чем указано в релизе");
                        if (progress != null && Environment.TickCount - last > 80) { last = Environment.TickCount; progress(total, r.Size); }
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                if (total != r.Size) throw new Exception("Файл скачался не целиком");
                if (hash != r.Sha) throw new Exception("Контрольная сумма файла не совпала с релизом");
            }
            catch (WebException) { TryDelete(part); throw new Exception("Не удалось скачать: нет связи с GitHub"); }
            catch { TryDelete(part); throw; }
        }

        public static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
    }

    // GitHub исчерпал запросы для этого компьютера: связь есть, просто надо подождать до Reset (UTC)
    class GitHubLimit : Exception
    {
        public readonly DateTime Reset;
        public GitHubLimit(DateTime reset) : base(Errors.Limit(reset.ToLocalTime())) { Reset = reset; }

        // Заголовок X-RateLimit-Reset — секунды с 1970 года; не разобрать — через час
        public static DateTime Until(string header, DateTime nowUtc)
        {
            long sec;
            if (long.TryParse(header, out sec) && sec > 0) return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(sec);
            return nowUtc.AddHours(1);
        }
    }
}
