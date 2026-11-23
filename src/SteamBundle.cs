// core.txt. SteamBundle.cs — набор для Steam (Arkada) в один щелчок: закрыть Steam и запустить снова, включить тему
// и плагин в настройках Millennium, а при удалении вернуть, как было.
// Millennium переписывает свой config.json, когда Steam выходит, поэтому настройки правятся только при полностью
// закрытом Steam: открыт, а закрыть его не просили — не трогаем ничего. Закрывается Steam только по кнопке
// «Закрыть Steam и …», которую нажал сам человек.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DevTxt.Core
{
    static class Steam
    {
        // Для проверок (--selftest): поддельная папка Steam; настоящий Steam на ней «не запущен» и не закрывается.
        // CORE_TEST_STEAM_RUNNING: "1" — поддельный Steam запущен, "stuck" — запущен и не закрывается
        public const string TestDir = "CORE_TEST_STEAM", TestRunning = "CORE_TEST_STEAM_RUNNING";
        static bool Fake { get { return Environment.GetEnvironmentVariable(TestDir) != null; } }
        public static int FakeStarts;   // сколько раз проверки «запускали» поддельный Steam

        public const string Process = "steam";
        public const string NotClosed = "Steam не закрылся — закройте его сами и нажмите ещё раз";

        // Папка Steam: как её записал сам Steam (HKCU), иначе — установщик (HKLM); "" — Steam не найден
        public static string Dir()
        {
            if (Fake) return Environment.GetEnvironmentVariable(TestDir);
            try
            {
                string p = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
                if (string.IsNullOrEmpty(p)) p = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
                return (p ?? "").Replace('/', '\\').TrimEnd('\\');
            }
            catch { return ""; }
        }

        // По общему снимку процессов карточек — его можно взять и в потоке окна
        public static bool Running(ProcSnap procs)
        {
            if (Fake) { string r = Environment.GetEnvironmentVariable(TestRunning); return r == "1" || r == "stuck"; }
            return procs != null && procs.Has(Process);
        }

        // Обходит систему — только в фоне
        public static bool Running() { return Running(Fake ? null : ProcSnap.Take(new string[] { Process })); }

        // Закрывает Steam так, как он закрывается сам (steam.exe -shutdown), и ждёт выхода всех его процессов: Millennium
        // дописывает свои настройки при выходе. true — Steam был запущен и закрыт нами. Не закрылся — исключение, ничего не тронуто
        public static bool Close()
        {
            if (!Running()) return false;
            if (Fake)
            {
                if (Environment.GetEnvironmentVariable(TestRunning) == "stuck") throw new Exception(NotClosed);
                Environment.SetEnvironmentVariable(TestRunning, null);
                return true;
            }
            string exe = Path.Combine(Dir(), "steam.exe");
            if (!File.Exists(exe)) throw new Exception(NotClosed);
            try { using (System.Diagnostics.Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = false })) { } }
            catch { throw new Exception(NotClosed); }
            // Ждём не только сам Steam: его окна (steamwebhelper) и бэкенды плагинов Millennium выходят позже
            // и могут дописать настройки — править config.json можно, только когда ушли все
            string[] all = { Process, "steamwebhelper", "millennium.luavm64" };
            DateTime until = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                ProcSnap snap = ProcSnap.Take(all);
                bool left = false;
                foreach (string name in all) if (snap.Has(name)) left = true;
                if (!left) break;
                if (DateTime.UtcNow > until) throw new Exception(NotClosed);
                Thread.Sleep(500);
            }
            Thread.Sleep(1000);   // файлы отпускаются не сразу после выхода
            return true;
        }

        // Запускает Steam тем же steam.exe. false — не вышло: человек запустит сам
        public static bool Start()
        {
            if (Fake) { FakeStarts++; Environment.SetEnvironmentVariable(TestRunning, "1"); return true; }
            try
            {
                string dir = Dir(), exe = Path.Combine(dir, "steam.exe");
                if (!File.Exists(exe)) return false;
                using (System.Diagnostics.Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true })) { }
                return true;
            }
            catch { return false; }
        }

        // close — на нажатой кнопке было «Закрыть Steam и …». closed — Steam уже закрыт (или закрывать не пришлось), начинается
        // работа. Закрывали мы — запускаем снова, даже если работа не удалась. true — Steam запущен снова
        public static bool Around(bool close, Action closed, Action work)
        {
            bool ours = close && Close();
            if (closed != null) closed();
            bool started = false;
            try { work(); }
            finally { if (ours) started = Start(); }
            return started;
        }

        // Millennium ставится с его сайта: core.txt его не скачивает и не запускает, только открывает страницу
        public const string MillenniumSite = "https://steambrew.app";
        public static void OpenMillenniumSite()
        {
            try { using (System.Diagnostics.Process.Start(new ProcessStartInfo(MillenniumSite) { UseShellExecute = true })) { } } catch { }
        }
    }

    // Steam открыт, а закрыть его не просили: ни файлов, ни настроек не трогаем
    class SteamOpen : Exception
    {
        public SteamOpen() : base("Steam запущен — закройте его и нажмите ещё раз") { }
    }

    // Как называются тема и плагин набора и какие плагины он заменяет — из manifest.json в корне архива. Берутся только
    // эти три поля и только как имена папок; куда класть файлы, решает каталог core.txt, а не архив
    class BundleNames
    {
        public string Theme, Plugin;
        public List<string> Replaces = new List<string>();

        static readonly Regex Safe = new Regex(@"^[A-Za-z0-9._-]{1,40}$");
        public static bool Ok(string s) { return s != null && Safe.IsMatch(s) && s.Trim('.').Length > 0; }   // «.» и «..» — не имена

        public static BundleNames Defaults(string id) { return new BundleNames { Theme = id, Plugin = id }; }

        public static BundleNames FromManifest(string json, string id)
        {
            var n = Defaults(id);
            Dictionary<string, object> d = null;
            try { d = Json.Parse(json); } catch { }
            if (d == null) return n;
            string t = Json.Str(d, "activeTheme"), p = Json.Str(d, "enablePlugin");
            if (Ok(t)) n.Theme = t;
            if (Ok(p)) n.Plugin = p;
            foreach (object o in Json.List(d, "replaces"))
            {
                string r = o as string;
                if (Ok(r) && r != n.Plugin && !n.Replaces.Contains(r) && n.Replaces.Count < 20) n.Replaces.Add(r);
            }
            return n;
        }

        public static BundleNames FromZip(string zipFile, string id)
        {
            try
            {
                using (var zip = System.IO.Compression.ZipFile.OpenRead(zipFile))
                    foreach (var e in zip.Entries)
                    {
                        if (!string.Equals(e.FullName, "manifest.json", StringComparison.OrdinalIgnoreCase) || e.Length > 64 * 1024) continue;
                        using (var r = new StreamReader(e.Open(), Encoding.UTF8)) return FromManifest(r.ReadToEnd(), id);
                    }
            }
            catch { }
            return Defaults(id);
        }
    }

    // Разбор JSON, который помнит, где в тексте лежит каждое значение. Правка меняет только нужные места, всё остальное —
    // порядок ключей, числа, кириллица, отступы — остаётся байт в байт: общий сериализатор этого не обещает
    class JsonDoc
    {
        public class Node
        {
            public char Kind;          // '{' объект, '[' массив, 's' строка, 'n' число, 'l' true/false/null
            public int Start, End;     // где в тексте: [Start, End)
            public string Text;        // у строки — её значение
            public List<string> Keys;  // у объекта — ключи по порядку
            public List<int> KeyAt;    // …и где они начинаются
            public List<Node> Items;   // значения объекта или элементы массива
        }

        class Edit { public int Start, End, Seq; public string Text; }

        readonly string src;
        int pos;
        public Node Root;
        readonly string nl, unit;
        readonly List<Edit> edits = new List<Edit>();
        readonly List<Node> addTo = new List<Node>();
        readonly Dictionary<Node, List<KeyValuePair<string, Func<string, string>>>> adds = new Dictionary<Node, List<KeyValuePair<string, Func<string, string>>>>();

        JsonDoc(string text)
        {
            src = text;
            nl = text.Contains("\r\n") ? "\r\n" : text.Contains("\n") ? "\n" : "";
            unit = nl == "" ? "" : "  ";
            // Шаг отступа — как у первой строки с отступом
            if (nl != "")
                foreach (string line in text.Split('\n'))
                {
                    int i = 0;
                    while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
                    if (i > 0 && i < line.Length && line[i] != '\r') { unit = line.Substring(0, i); break; }
                }
        }

        public static JsonDoc Parse(string text)
        {
            var d = new JsonDoc(text);
            d.Root = d.Value(0);
            d.Ws();
            if (d.pos != text.Length) throw d.Bad();
            return d;
        }

        Exception Bad() { return new FormatException("bad JSON at " + pos); }
        void Ws() { while (pos < src.Length && (src[pos] == ' ' || src[pos] == '\t' || src[pos] == '\n' || src[pos] == '\r')) pos++; }
        char Peek() { return pos < src.Length ? src[pos] : '\0'; }
        char Next() { if (pos >= src.Length) throw Bad(); return src[pos++]; }

        Node Value(int depth)
        {
            if (depth > 200) throw Bad();
            Ws();
            char c = Peek();
            var n = new Node { Start = pos };
            if (c == '{')
            {
                n.Kind = '{'; n.Keys = new List<string>(); n.KeyAt = new List<int>(); n.Items = new List<Node>();
                pos++; Ws();
                if (Peek() == '}') pos++;
                else
                    while (true)
                    {
                        Ws();
                        if (Peek() != '"') throw Bad();
                        int at = pos;
                        string key = Str();
                        Ws();
                        if (Next() != ':') throw Bad();
                        Node v = Value(depth + 1);
                        n.Keys.Add(key); n.KeyAt.Add(at); n.Items.Add(v);
                        Ws();
                        char e = Next();
                        if (e == ',') continue;
                        if (e == '}') break;
                        throw Bad();
                    }
            }
            else if (c == '[')
            {
                n.Kind = '['; n.Items = new List<Node>();
                pos++; Ws();
                if (Peek() == ']') pos++;
                else
                    while (true)
                    {
                        n.Items.Add(Value(depth + 1));
                        Ws();
                        char e = Next();
                        if (e == ',') continue;
                        if (e == ']') break;
                        throw Bad();
                    }
            }
            else if (c == '"') { n.Kind = 's'; n.Text = Str(); }
            else if (c == '-' || c >= '0' && c <= '9') { n.Kind = 'n'; Num(); }
            else
            {
                n.Kind = 'l';
                string word = null;
                foreach (string w in new string[] { "true", "false", "null" })
                    if (string.CompareOrdinal(src, pos, w, 0, w.Length) == 0) { word = w; break; }
                if (word == null) throw Bad();
                pos += word.Length;
            }
            n.End = pos;
            return n;
        }

        string Str()
        {
            if (Next() != '"') throw Bad();
            var sb = new StringBuilder();
            while (true)
            {
                char ch = Next();
                if (ch == '"') break;
                if (ch < 0x20) throw Bad();
                if (ch != '\\') { sb.Append(ch); continue; }
                char e = Next();
                switch (e)
                {
                    case '"': case '\\': case '/': sb.Append(e); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (pos + 4 > src.Length) throw Bad();
                        string hex = src.Substring(pos, 4);
                        if (!Regex.IsMatch(hex, "^[0-9A-Fa-f]{4}$")) throw Bad();
                        sb.Append((char)Convert.ToInt32(hex, 16));
                        pos += 4;
                        break;
                    default: throw Bad();
                }
            }
            return sb.ToString();
        }

        void Digits() { int s = pos; while (pos < src.Length && src[pos] >= '0' && src[pos] <= '9') pos++; if (pos == s) throw Bad(); }

        void Num()
        {
            if (Peek() == '-') pos++;
            if (Peek() == '0') pos++; else Digits();
            if (Peek() == '.') { pos++; Digits(); }
            if (Peek() == 'e' || Peek() == 'E') { pos++; if (Peek() == '+' || Peek() == '-') pos++; Digits(); }
        }

        // Значение по ключу; null — ключа нет или это не объект
        public Node Get(Node obj, string key)
        {
            if (obj == null || obj.Kind != '{') return null;
            int i = obj.Keys.IndexOf(key);
            return i >= 0 ? obj.Items[i] : null;
        }

        public string Raw(Node n) { return src.Substring(n.Start, n.End - n.Start); }

        // Строка JSON: кириллица и прочее остаются как есть, экранируется только то, что обязано
        public static string Quote(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c == '\b') sb.Append("\\b");
                else if (c == '\f') sb.Append("\\f");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // Отступ строки, в которой стоит это место
        string Indent(int at)
        {
            int s = at;
            while (s > 0 && src[s - 1] != '\n') s--;
            int e = s;
            while (e < src.Length && (src[e] == ' ' || src[e] == '\t')) e++;
            return src.Substring(s, e - s);
        }

        string Break(string ind) { return nl == "" ? " " : nl + ind; }

        // Массив из готовых значений; ind — отступ строки, где он начинается. Пустой — «[]», как пишет сам Millennium
        public string ArrayText(List<string> raws, string ind)
        {
            if (raws.Count == 0) return "[]";
            return "[" + Break(ind + unit) + string.Join("," + Break(ind + unit), raws.ToArray()) + Break(ind) + "]";
        }

        public string ObjectText(List<KeyValuePair<string, Func<string, string>>> members, string ind)
        {
            if (members.Count == 0) return "{}";
            var parts = new List<string>();
            foreach (var m in members) parts.Add(Quote(m.Key) + ": " + m.Value(ind + unit));
            return "{" + Break(ind + unit) + string.Join("," + Break(ind + unit), parts.ToArray()) + Break(ind) + "}";
        }

        public void Replace(Node n, string text) { edits.Add(new Edit { Start = n.Start, End = n.End, Text = text, Seq = edits.Count }); }

        // Ваше значение по месту — по отступу строки, где окажется ключ
        public void ReplaceAt(Node n, Func<string, string> text) { Replace(n, text(Indent(n.Start))); }

        // Новый ключ в конец объекта; значение получает отступ строки, на которой встанет ключ
        public void Add(Node obj, string key, Func<string, string> value)
        {
            List<KeyValuePair<string, Func<string, string>>> list;
            if (!adds.TryGetValue(obj, out list)) { adds[obj] = list = new List<KeyValuePair<string, Func<string, string>>>(); addTo.Add(obj); }
            list.Add(new KeyValuePair<string, Func<string, string>>(key, value));
        }

        public string Result()
        {
            var all = new List<Edit>(edits);
            foreach (Node obj in addTo)
            {
                var list = adds[obj];
                if (obj.Items.Count == 0) { all.Add(new Edit { Start = obj.Start, End = obj.End, Text = ObjectText(list, Indent(obj.Start)), Seq = all.Count }); continue; }
                string ind = Indent(obj.KeyAt[0]);
                var sb = new StringBuilder();
                foreach (var m in list) sb.Append(",").Append(Break(ind)).Append(Quote(m.Key)).Append(": ").Append(m.Value(ind));
                int at = obj.Items[obj.Items.Count - 1].End;
                all.Add(new Edit { Start = at, End = at, Text = sb.ToString(), Seq = all.Count });
            }
            // С конца к началу: правка не сдвигает места тех, что ещё впереди
            all.Sort(delegate(Edit a, Edit b) { return a.Start != b.Start ? b.Start.CompareTo(a.Start) : b.Seq.CompareTo(a.Seq); });
            var text = new StringBuilder(src);
            foreach (Edit e in all) { text.Remove(e.Start, e.End - e.Start); text.Insert(e.Start, e.Text); }
            return text.ToString();
        }
    }

    // Настройки Millennium (millennium\config\config.json): какая тема включена и какие плагины. Путь строится здесь от
    // папки Steam — ни каталог, ни архив его не задают
    static class Millennium
    {
        public const string NoTheme = "default";   // так Millennium сам пишет «без темы»
        public const string MemoryFile = ".core-millennium";   // что было до нас — рядом с .core-version в папке плагина
        public const string EnableFailed = "Файлы на месте, но включить тему в настройках Millennium не удалось — включите её там сами";
        public const string RestoreFailed = "Тема и плагин убраны, но вернуть прежнюю тему в настройках Millennium не удалось — выберите её там сами";

        public static string Config(string steam) { return Path.Combine(Path.Combine(Path.Combine(steam, "millennium"), "config"), "config.json"); }
        public static string Backup(string steam, string id) { return Config(steam) + ".before-" + id; }

        static bool ThemeExists(string steam, string name)
        {
            return Directory.Exists(Path.Combine(steam, "millennium", "themes", name)) || Directory.Exists(Path.Combine(steam, "steamui", "skins", name));
        }
        static bool PluginExists(string steam, string name) { return Directory.Exists(Path.Combine(steam, "millennium", "plugins", name)); }

        // Что core.txt запомнил при установке: свои имена, прежнюю тему и плагины, которые отключил
        public class Memory
        {
            public string Plugin, Theme, Before;
            public List<string> Disabled = new List<string>();
            public bool Found;
        }

        public static Memory ReadMemory(string dir)
        {
            var m = new Memory();
            try
            {
                string f = Path.Combine(dir, MemoryFile);
                if (!File.Exists(f)) return m;
                m.Found = true;
                foreach (string line in File.ReadAllLines(f, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1).Trim();
                    if (!BundleNames.Ok(v)) continue;
                    if (k == "plugin") m.Plugin = v;
                    else if (k == "theme") m.Theme = v;
                    else if (k == "before") m.Before = v;
                    else if (k == "disabled" && !m.Disabled.Contains(v)) m.Disabled.Add(v);
                }
            }
            catch { }
            return m;
        }

        static void WriteMemory(string dir, Memory m)
        {
            var sb = new StringBuilder();
            sb.Append("core.txt · ").Append(DateTime.Now.ToString("s")).Append("\r\n");
            sb.Append("plugin=").Append(m.Plugin).Append("\r\n");
            sb.Append("theme=").Append(m.Theme).Append("\r\n");
            if (m.Before != null) sb.Append("before=").Append(m.Before).Append("\r\n");
            foreach (string d in m.Disabled) sb.Append("disabled=").Append(d).Append("\r\n");
            File.WriteAllText(Path.Combine(dir, MemoryFile), sb.ToString(), new UTF8Encoding(false));
        }

        // Текст файла: строго UTF-8; метку BOM снимаем — Millennium пишет без неё
        static string Read(string path, out bool bom)
        {
            byte[] raw = File.ReadAllBytes(path);
            string text = new UTF8Encoding(false, true).GetString(raw);
            bom = text.Length > 0 && text[0] == '﻿';
            return bom ? text.Substring(1) : text;
        }

        // Через временный файл рядом: оборвись запись на середине — прежний файл цел
        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".core-tmp";
            File.WriteAllBytes(tmp, new UTF8Encoding(false).GetBytes(text));
            try { File.Replace(tmp, path, null); }
            catch { File.Copy(tmp, path, true); Releases.TryDelete(tmp); }
        }

        static JsonDoc Open(string text, out JsonDoc.Node themes, out JsonDoc.Node plugins, out JsonDoc.Node active, out JsonDoc.Node enabled)
        {
            var doc = JsonDoc.Parse(text);
            if (doc.Root.Kind != '{') throw new FormatException("config.json is not an object");
            themes = doc.Get(doc.Root, "themes");
            plugins = doc.Get(doc.Root, "plugins");
            if (themes != null && themes.Kind != '{' || plugins != null && plugins.Kind != '{') throw new FormatException("unexpected config.json");
            active = doc.Get(themes, "activeTheme");
            enabled = doc.Get(plugins, "enabledPlugins");
            if (enabled != null && enabled.Kind != '[') throw new FormatException("enabledPlugins is not a list");
            return doc;
        }

        static Func<string, string> Fixed(string text) { return delegate(string ind) { return text; }; }

        // Включает тему и плагин набора, отключает заменённые плагины. memoryDir — папка плагина: там запоминается, как было.
        // Возвращает, какие плагины отключили сейчас. Не вышло — исключение; файл тогда не тронут
        public static List<string> Enable(string steam, string id, BundleNames names, string memoryDir)
        {
            string path = Config(steam);
            if (!ThemeExists(steam, names.Theme) || !PluginExists(steam, names.Plugin)) throw new Exception("theme or plugin folder is missing");
            bool bom;
            string text = Read(path, out bom);
            JsonDoc.Node themes, plugins, active, enabled;
            var doc = Open(text, out themes, out plugins, out active, out enabled);
            string before = active != null && active.Kind == 's' ? active.Text : null;

            string theme = JsonDoc.Quote(names.Theme);
            if (active == null)
            {
                if (themes != null) doc.Add(themes, "activeTheme", Fixed(theme));
                else doc.Add(doc.Root, "themes", delegate(string ind) { return doc.ObjectText(new List<KeyValuePair<string, Func<string, string>>> { new KeyValuePair<string, Func<string, string>>("activeTheme", Fixed(theme)) }, ind); });
            }
            else if (!(active.Kind == 's' && active.Text == names.Theme)) doc.Replace(active, theme);

            var raws = new List<string>();
            var disabled = new List<string>();
            bool has = false, changed = false;
            if (enabled != null)
                foreach (JsonDoc.Node item in enabled.Items)
                {
                    if (item.Kind == 's' && names.Replaces.Contains(item.Text)) { if (!disabled.Contains(item.Text)) disabled.Add(item.Text); changed = true; continue; }
                    if (item.Kind == 's' && item.Text == names.Plugin) has = true;
                    raws.Add(doc.Raw(item));
                }
            if (!has) { raws.Add(JsonDoc.Quote(names.Plugin)); changed = true; }
            if (changed)
            {
                Func<string, string> list = delegate(string ind) { return doc.ArrayText(raws, ind); };
                if (enabled != null) doc.ReplaceAt(enabled, list);
                else if (plugins != null) doc.Add(plugins, "enabledPlugins", list);
                else doc.Add(doc.Root, "plugins", delegate(string ind) { return doc.ObjectText(new List<KeyValuePair<string, Func<string, string>>> { new KeyValuePair<string, Func<string, string>>("enabledPlugins", list) }, ind); });
            }
            string result = doc.Result();

            // Как было — один раз, при первой установке; обновление дописывает только новые отключённые плагины
            var mem = ReadMemory(memoryDir);
            if (!mem.Found) mem.Before = before != null && before != names.Theme && BundleNames.Ok(before) ? before : null;
            mem.Plugin = names.Plugin; mem.Theme = names.Theme;
            foreach (string d in disabled) if (!mem.Disabled.Contains(d)) mem.Disabled.Add(d);
            WriteMemory(memoryDir, mem);

            if (result == text && !bom) return disabled;   // всё уже так — файл не переписываем
            string backup = Backup(steam, id);
            if (!File.Exists(backup)) File.Copy(path, backup);   // резерв — один раз, обновления его не трогают
            WriteAtomic(path, result);
            return disabled;
        }

        // Возвращает, как было до набора: прежнюю тему (если её запомнили и она ещё стоит, иначе «без темы»), убирает наш
        // плагин и включает отключённые нами, если их папки на месте. Звать до того, как убраны папки набора
        public static void Restore(string steam, string id, string memoryDir)
        {
            var mem = ReadMemory(memoryDir);
            string plugin = mem.Plugin ?? id, ours = mem.Theme ?? id;
            string path = Config(steam);
            if (!File.Exists(path)) return;   // настроек нет — возвращать нечего
            bool bom;
            string text = Read(path, out bom);
            JsonDoc.Node themes, plugins, active, enabled;
            var doc = Open(text, out themes, out plugins, out active, out enabled);
            if (active != null && active.Kind == 's' && active.Text == ours)   // тему сменили сами — её не трогаем
                doc.Replace(active, JsonDoc.Quote(mem.Before != null && mem.Before != ours && ThemeExists(steam, mem.Before) ? mem.Before : NoTheme));
            if (enabled != null)
            {
                var raws = new List<string>();
                var present = new List<string>();
                bool changed = false;
                foreach (JsonDoc.Node item in enabled.Items)
                {
                    if (item.Kind == 's' && item.Text == plugin) { changed = true; continue; }
                    if (item.Kind == 's') present.Add(item.Text);
                    raws.Add(doc.Raw(item));
                }
                foreach (string d in mem.Disabled)
                    if (d != plugin && !present.Contains(d) && PluginExists(steam, d)) { raws.Add(JsonDoc.Quote(d)); present.Add(d); changed = true; }
                if (changed) doc.ReplaceAt(enabled, delegate(string ind) { return doc.ArrayText(raws, ind); });
            }
            string result = doc.Result();
            if (result == text && !bom) return;
            WriteAtomic(path, result);
        }
    }
}
