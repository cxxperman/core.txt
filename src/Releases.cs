// core.txt. Releases.cs — что выложено на GitHub и как это скачать.
// Единственный источник — релизы репозитория приложения. Скачанный файл должен совпасть с тем, что указано
// в релизе: по размеру и по контрольной сумме SHA-256. Не совпал — файл удаляется и не запускается.
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

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> { { "version", Version.ToString() }, { "url", Url }, { "sha", Sha ?? "" }, { "name", Name }, { "size", Size.ToString() } };
        }

        public static Release From(Dictionary<string, object> d)
        {
            Version v; long size;
            if (d == null || !Version.TryParse(Json.Str(d, "version") ?? "", out v) || !long.TryParse(Json.Str(d, "size") ?? "", out size)) return null;
            string url = Json.Str(d, "url"), name = Json.Str(d, "name");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) return null;
            return new Release { Version = v, Url = url, Sha = Json.Str(d, "sha"), Name = name, Size = size };
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
            using (var rs = Request(url).GetResponse())
            using (var r = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return r.ReadToEnd();
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
                    break;
                }
            }
            return best;
        }

        // Скачивает файл релиза во временную папку и возвращает путь к нему. progress — доля от 0 до 1.
        public static string Download(Release r, Action<double> progress)
        {
            if (string.IsNullOrEmpty(r.Sha)) throw new Exception("В релизе нет контрольной суммы файла — ставить его небезопасно");
            string dir = Path.Combine(Path.GetTempPath(), "core.txt");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Path.GetFileName(r.Name)), part = file + ".part";
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
                        if (progress != null && Environment.TickCount - last > 80) { last = Environment.TickCount; progress(r.Size > 0 ? (double)total / r.Size : 0); }
                    }
                    sha.TransformFinalBlock(buf, 0, 0);
                    hash = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                }
                if (total != r.Size) throw new Exception("Файл скачался не целиком");
                if (hash != r.Sha) throw new Exception("Контрольная сумма файла не совпала с релизом");
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
                return file;
            }
            catch (WebException) { TryDelete(part); throw new Exception("Не удалось скачать: нет связи с GitHub"); }
            catch { TryDelete(part); throw; }
        }

        public static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }
    }
}
