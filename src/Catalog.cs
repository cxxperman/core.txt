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
        public bool Recommended;   // отмечено на первом запуске по умолчанию
        public bool Soon;         // ещё не выпущено: пока его нет на компьютере, вместо кнопки — «В разработке»
        public string[] Sprite;   // пиксельный значок: строки из «.» (пусто), «#» (тело) и «E» (глаз)
        public bool IsIntegration { get { return Kind == "integration"; } }
        // «Набор»: архив из релиза, который раскладывается по папкам другой программы (тема и плагин для Steam)
        public bool IsBundle { get { return Kind == "bundle"; } }
        public string Connect;          // для подключения: команда хаба приложению Via, которая его выполняет
        public string Requires, RequiresHint;   // для набора: папка, без которой ставить некуда, и что сказать, если её нет
        public List<BundleTarget> Targets = new List<BundleTarget>();
        public bool HasExe { get { return Kind == "portable" || Kind == "installer"; } }
        public string Data;   // папка с настройками приложения — её можно стереть вместе с ним, если человек попросит
        public bool Removable { get { return HasExe || IsBundle; } }
    }

    // Часть набора: что из архива (папка «from/») куда положить. Мест может быть несколько — берётся первое,
    // у которого уже есть родительская папка: разные версии программы держат темы в разных местах.
    class BundleTarget
    {
        public string From;
        public List<string> To = new List<string>();
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
        static readonly Regex SafeFolder = new Regex(@"^[A-Za-z0-9._-]{1,40}/$");
        // Папка с настройками — только внутри профиля пользователя, без «.» и «..» в пути
        static readonly Regex SafeData = new Regex(@"^%(APPDATA|LOCALAPPDATA)%(\\(?!\.{1,2}(\\|$))[A-Za-z0-9._-]{1,40}){1,3}$");
        static readonly Regex SafeCommand = new Regex(@"^[a-z][a-z0-9.]{0,40}$");
        // Набор можно класть только внутрь папки Steam: каталог из сети не должен уметь писать куда попало
        static readonly Regex SafeTarget = new Regex(@"^\{steam\}(\\(?!\.{1,2}(\\|$))[A-Za-z0-9._-]{1,40}){1,6}$");   // без «.» и «..» в пути

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
                object rec;
                a.Recommended = d.TryGetValue("recommended", out rec) && rec is bool && (bool)rec;
                a.Data = Json.Str(d, "data");
                if (a.Data != null && !SafeData.IsMatch(a.Data)) a.Data = null;
                a.Connect = Json.Str(d, "connect");
                if (a.Connect != null && !SafeCommand.IsMatch(a.Connect)) a.Connect = null;
                a.Requires = Json.Str(d, "requires");
                a.RequiresHint = Json.Str(d, "requiresHint");
                if (a.Kind == "integration")
                {
                    if (string.IsNullOrEmpty(a.Marker)) continue;
                }
                else if (a.Kind == "bundle")
                {
                    if (a.Repo == null || !SafeRepo.IsMatch(a.Repo) || a.Asset == null || !SafeFile.IsMatch(a.Asset) || a.Asset.Contains("*") || !a.Asset.EndsWith(".zip")) continue;
                    if (string.IsNullOrEmpty(a.Marker) || !SafeTarget.IsMatch(a.Marker)) continue;   // рядом с отметкой пишется файл версии
                    if (a.Requires != null && !SafeTarget.IsMatch(a.Requires)) continue;
                    bool ok = true;
                    foreach (object to in Json.List(d, "targets"))
                    {
                        var td = to as Dictionary<string, object>;
                        var target = new BundleTarget { From = Json.Str(td, "from") };
                        string one = Json.Str(td, "to");
                        if (one != null) target.To.Add(one);
                        else foreach (object p in Json.List(td, "to")) if (p is string) target.To.Add((string)p);
                        if (target.From == null || !SafeFolder.IsMatch(target.From) || target.To.Count == 0 || !target.To.TrueForAll(delegate(string p) { return SafeTarget.IsMatch(p); })) { ok = false; break; }
                        a.Targets.Add(target);
                    }
                    if (!ok || a.Targets.Count == 0) continue;
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
        public static bool GatherPending;         // после установки core.txt перенести приложения в общий дом
        public static bool TrayHint;              // про то, что core.txt остаётся в трее, уже сказали
        public static string Section;             // раздел, открытый в прошлый раз
        public static double WinX = double.NaN, WinY = double.NaN, WinW, WinH;   // где и какого размера было окно
        public static bool WinMax;
        public static readonly HashSet<string> Scenarios = new HashSet<string>();   // включённые сценарии
        public static List<string> Pending = new List<string>();                    // что поставить после первого запуска

        static bool Flag(Dictionary<string, object> d, string key, bool fallback)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback;
        }

        static double Number(Dictionary<string, object> d, string key, double fallback)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null || v is string || v is bool) return fallback;
            try { double n = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); return double.IsNaN(n) || double.IsInfinity(n) ? fallback : n; } catch { return fallback; }
        }

        // Окно возвращается туда, где было, но целиком на экран: не больше рабочей области и не за её краем.
        // (sx, sy, sw, sh) — рабочая область экрана. Размер меньше наименьшего не берётся.
        public static void Fit(ref double x, ref double y, ref double w, ref double h, double sx, double sy, double sw, double sh, double minW, double minH)
        {
            w = Math.Min(Math.Max(w, minW), Math.Max(sw, 1));
            h = Math.Min(Math.Max(h, minH), Math.Max(sh, 1));
            x = Math.Max(sx, Math.Min(x, sx + sw - w));
            y = Math.Max(sy, Math.Min(y, sy + sh - h));
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
                GatherPending = Flag(set, "gather", false);
                TrayHint = Flag(set, "trayHint", false);
                string section = Json.Str(set, "section");
                Section = section != null && System.Text.RegularExpressions.Regex.IsMatch(section, "^(apps|scenarios|general|replace|app:[a-z][a-z0-9-]{0,31})$") ? section : null;
                var win = Json.Obj(d, "window");
                WinX = Number(win, "x", double.NaN); WinY = Number(win, "y", double.NaN);
                WinW = Number(win, "w", 0); WinH = Number(win, "h", 0);
                WinMax = Flag(win, "max", false);
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
                var set = new Dictionary<string, object> { { "accent", Accent }, { "reducedMotion", ReducedMotion }, { "tray", Tray }, { "autoCheck", AutoCheck }, { "onboarded", Onboarded }, { "gather", GatherPending },
                    { "trayHint", TrayHint }, { "section", Section } };
                var d = new Dictionary<string, object> { { "paths", Paths }, { "latest", latest }, { "checked", Checked.Ticks.ToString() }, { "settings", set },
                    { "scenarios", new List<string>(Scenarios) }, { "pending", Pending } };
                if (WinW > 0 && WinH > 0 && !double.IsNaN(WinX) && !double.IsNaN(WinY))
                    d["window"] = new Dictionary<string, object> { { "x", Math.Round(WinX) }, { "y", Math.Round(WinY) }, { "w", Math.Round(WinW) }, { "h", Math.Round(WinH) }, { "max", WinMax } };
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, Json.Write(d), Encoding.UTF8);
            }
            catch { }
        }
    }
}
