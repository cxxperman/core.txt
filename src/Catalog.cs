// core.txt. Catalog.cs — список приложений и то, что лаунчер помнит между запусками.
// Каталог — файл catalog.json: он лежит внутри exe, а свежий забирается из репозитория лаунчера. Новое приложение
// добавляется одной записью в этом файле; пересобирать лаунчер для этого не нужно.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace DevTxt.Core
{
    class AppEntry
    {
        public string Id, Name, Tagline, Kind, Repo, Asset, Exe, Process, Silent, Installed, Marker, Via, Hint;
        public Color Color;
        public bool Soon;         // ещё не выпущено: пока его нет на компьютере, вместо кнопки — «В разработке»
        public string[] Sprite;   // пиксельный значок: строки из «.» (пусто), «#» (тело) и «E» (глаз)
        public bool IsIntegration { get { return Kind == "integration"; } }
    }

    static class Json
    {
        public static Dictionary<string, object> Parse(string text)
        {
            return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(text) as Dictionary<string, object>;
        }
        public static object ParseAny(string text) { return new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 }.DeserializeObject(text); }
        public static string Write(object o) { return new JavaScriptSerializer().Serialize(o); }
        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as string : null;
        }
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }
        public static IEnumerable List(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is IEnumerable && !(v is string) ? (IEnumerable)v : new object[0];
        }
    }

    static class Catalog
    {
        // Откуда приложения можно ставить. Каталог из сети не может привести лаунчер в чужой репозиторий.
        public const string Owner = "cxxperman";
        public const string RemoteUrl = "https://raw.githubusercontent.com/cxxperman/core.txt/main/catalog.json";

        public static List<AppEntry> Apps = new List<AppEntry>();

        static readonly Regex SafeId = new Regex("^[a-z][a-z0-9-]{0,31}$");
        static readonly Regex SafeRepo = new Regex("^" + Owner + "/[A-Za-z0-9._-]{1,100}$");
        static readonly Regex SafeFile = new Regex(@"^[A-Za-z0-9._*-]{1,100}$");

        public static void Load()
        {
            // Сохранённый с прошлого раза каталог из сети новее встроенного
            List<AppEntry> list = null;
            try { if (File.Exists(State.CatalogFile)) list = Read(File.ReadAllText(State.CatalogFile, Encoding.UTF8)); } catch { }
            if (list == null || list.Count == 0) list = Read(Embedded());
            Apps = list;
        }

        // Вызывается в фоне. true — каталог изменился, окно надо перестроить.
        public static bool Refresh()
        {
            try
            {
                string text = Releases.GetText(RemoteUrl);
                var list = Read(text);
                if (list.Count == 0) return false;
                string old = File.Exists(State.CatalogFile) ? File.ReadAllText(State.CatalogFile, Encoding.UTF8) : Embedded();
                if (old == text) return false;
                Directory.CreateDirectory(State.Dir);
                File.WriteAllText(State.CatalogFile, text, Encoding.UTF8);
                Apps = list;
                return true;
            }
            catch { return false; }
        }

        static string Embedded()
        {
            using (var s = typeof(Catalog).Assembly.GetManifestResourceStream("Core.catalog.json"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        static string[] ReadSprite(Dictionary<string, object> d)
        {
            var rows = new List<string>();
            foreach (object o in Json.List(d, "sprite"))
            {
                string row = o as string;
                if (row == null || row.Length == 0 || row.Length > 24 || !Regex.IsMatch(row, "^[.#E]+$")) return null;
                if (rows.Count > 0 && row.Length != rows[0].Length) return null;
                rows.Add(row);
            }
            return rows.Count > 0 && rows.Count <= 24 ? rows.ToArray() : null;
        }

        // Записи, в которых что-то не так, пропускаются: одна плохая запись не ломает остальные
        public static List<AppEntry> Read(string text)
        {
            var list = new List<AppEntry>();
            foreach (object o in Json.List(Json.Parse(text), "apps"))
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                var a = new AppEntry
                {
                    Id = Json.Str(d, "id"), Name = Json.Str(d, "name"), Tagline = Json.Str(d, "tagline") ?? "", Kind = Json.Str(d, "kind"),
                    Repo = Json.Str(d, "repo"), Asset = Json.Str(d, "asset"), Exe = Json.Str(d, "exe"), Process = Json.Str(d, "process"),
                    Silent = Json.Str(d, "silent") ?? "", Installed = Json.Str(d, "installed"), Marker = Json.Str(d, "marker"),
                    Via = Json.Str(d, "via"), Hint = Json.Str(d, "hint") ?? "",
                    Color = Tokens.Palette.Accent
                };
                if (a.Id == null || !SafeId.IsMatch(a.Id) || string.IsNullOrEmpty(a.Name)) continue;
                if (list.Exists(delegate(AppEntry x) { return x.Id == a.Id; })) continue;
                try { a.Color = (Color)ColorConverter.ConvertFromString(Json.Str(d, "color")); } catch { }
                a.Sprite = ReadSprite(d);
                object soon;
                a.Soon = d.TryGetValue("soon", out soon) && soon is bool && (bool)soon;
                if (a.Kind == "integration")
                {
                    if (string.IsNullOrEmpty(a.Marker)) continue;
                }
                else if (a.Kind == "portable" || a.Kind == "installer")
                {
                    if (a.Repo == null || !SafeRepo.IsMatch(a.Repo) || a.Asset == null || !SafeFile.IsMatch(a.Asset)) continue;
                    if (a.Kind == "portable" && (a.Exe == null || !SafeFile.IsMatch(a.Exe) || a.Exe.Contains("*"))) continue;
                    if (a.Kind == "installer" && string.IsNullOrEmpty(a.Installed)) continue;
                }
                else continue;   // вид, которого эта версия лаунчера не знает
                list.Add(a);
            }
            return list;
        }
    }

    // Что лаунчер помнит: где лежат приложения и какие версии он видел в прошлый раз
    static class State
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.txt", "core");
        public static string CatalogFile { get { return Path.Combine(Dir, "catalog.json"); } }
        static string FilePath { get { return Path.Combine(Dir, "state.json"); } }

        public static Dictionary<string, string> Paths = new Dictionary<string, string>();
        public static Dictionary<string, Release> Latest = new Dictionary<string, Release>();
        public static DateTime Checked = DateTime.MinValue;

        // Настройки самого core.txt и общий вид экосистемы
        public static string Accent;              // "#RRGGBB" или null — у каждого приложения свой цвет
        public static bool ReducedMotion;
        public static bool Tray = true;           // оставаться в трее после закрытия окна
        public static bool AutoCheck = true;      // проверять обновления при запуске
        public static bool Onboarded;             // первый запуск пройден
        public static readonly HashSet<string> Scenarios = new HashSet<string>();   // включённые сценарии
        public static List<string> Pending = new List<string>();                    // что поставить после первого запуска

        static bool Flag(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = Json.Parse(File.ReadAllText(FilePath, Encoding.UTF8));
                var paths = Json.Obj(d, "paths");
                if (paths != null) foreach (var p in paths) if (p.Value is string) Paths[p.Key] = (string)p.Value;
                var latest = Json.Obj(d, "latest");
                if (latest != null)
                    foreach (var p in latest)
                    {
                        var r = Release.From(p.Value as Dictionary<string, object>);
                        if (r != null) Latest[p.Key] = r;
                    }
                long ticks;
                if (long.TryParse(Json.Str(d, "checked") ?? "", out ticks)) Checked = new DateTime(ticks, DateTimeKind.Utc);
                var set = Json.Obj(d, "settings");
                string accent = Json.Str(set, "accent");
                Accent = accent != null && System.Text.RegularExpressions.Regex.IsMatch(accent, "^#[0-9a-fA-F]{6}$") ? accent : null;
                ReducedMotion = Flag(set, "reducedMotion", false);
                Tray = Flag(set, "tray", true);
                AutoCheck = Flag(set, "autoCheck", true);
                Onboarded = Flag(set, "onboarded", false);
                foreach (object o in Json.List(d, "scenarios")) if (o is string) Scenarios.Add((string)o);
                foreach (object o in Json.List(d, "pending")) if (o is string) Pending.Add((string)o);
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                var latest = new Dictionary<string, object>();
                foreach (var p in Latest) latest[p.Key] = p.Value.ToDict();
                var set = new Dictionary<string, object> { { "accent", Accent }, { "reducedMotion", ReducedMotion }, { "tray", Tray }, { "autoCheck", AutoCheck }, { "onboarded", Onboarded } };
                var d = new Dictionary<string, object> { { "paths", Paths }, { "latest", latest }, { "checked", Checked.Ticks.ToString() }, { "settings", set },
                    { "scenarios", new List<string>(Scenarios) }, { "pending", Pending } };
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, Json.Write(d), Encoding.UTF8);
            }
            catch { }
        }
    }
}
