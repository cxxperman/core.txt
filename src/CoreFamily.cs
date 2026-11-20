// core.txt. CoreFamily.cs — сторона core.txt в «семье» (kit.txt, hub/PROTOCOL.md, раздел «Семья»): что уходит всем
// в core.family, «Не беспокоить», общее меню значка в трее, пункты настроек с полем section и страница «Клавиши».
// Здесь только расчёты, без окон и хаба, — их проверяет --selftest. Окно, хаб и значок в трее — в MainWindow.cs.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DevTxt.Core
{
    // Группа общего меню в трее: приложение и его быстрые действия
    class TrayGroup
    {
        public string App, Name;          // «smena», «smena.txt»
        public string[] Sprite;           // значок приложения; null — без значка
        public List<TrayItem> Items = new List<TrayItem>();
    }

    // Строка страницы «Клавиши»: сочетание одного приложения и с чем оно совпало
    class KeyRow
    {
        public string App, Name;          // «kapsula», «kapsula.txt»
        public KeyInfo Key;
        public string Norm;               // CoreFamily.Norm(Key.Keys): по нему ищутся совпадения
        public readonly List<KeyRow> Clash = new List<KeyRow>();
    }

    // core.family уходит хабу, только когда что-то изменилось: одно и то же второй раз не шлём
    class FamilyOut
    {
        string last;

        public bool Changed(FamilyState s)
        {
            string text = Json.Write(s.ToData());
            if (text == last) return false;
            last = text;
            return true;
        }
    }

    static class CoreFamily
    {
        public const string Topic = "core.family";
        // Общие страницы core.txt, на которые переезжают пункты настроек приложений (поле section)
        public const string Games = "games", Startup = "startup", Kin = "family";
        static readonly string[] Sections = { Games, Startup, Kin };
        // Страницы, которые можно открыть командой show
        static readonly string[] Pages = { "apps", "scenarios", "general", "games", "keys", "replace" };
        static readonly Regex AppId = new Regex("^[a-z][a-z0-9-]{0,31}$");
        static readonly CultureInfo Ru = new CultureInfo("ru-RU");
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public const long MaxFocusMinutes = 60L * 24 * 7;   // «Не беспокоить» на время — не дольше недели

        // ───────── core.family и «Не беспокоить» ─────────

        // «Не беспокоить» было включено на время, и это время прошло
        public static bool FocusExpired(bool focus, long until, long now) { return focus && until > 0 && now >= until; }

        // Когда выключить «Не беспокоить», включённое на minutes минут; 0 — пока не выключат
        public static long FocusUntil(double minutes, long now)
        {
            if (double.IsNaN(minutes) || minutes <= 0) return 0;
            return now + (long)(Math.Min(minutes, MaxFocusMinutes) * 60000);
        }

        // Время окончания годится: после 1970 года и не дальше недели вперёд (с запасом в минуту). Другое бывает только
        // в испорченном или поправленном руками файле настроек.
        public static bool UntilOk(double until, long now) { return until > 0 && until <= now + (MaxFocusMinutes + 1) * 60000.0; }

        // Когда «Не беспокоить» кончится, по местному времени; null — выключено, пока не выключат, или время не годится.
        // То же, что FamilyState.FocusEnds, только без исключения на испорченном числе.
        public static DateTime? Ends(FamilyState s)
        {
            if (s == null || !s.Focus || s.FocusUntil <= 0) return null;
            try { return Epoch.AddMilliseconds(s.FocusUntil).ToLocalTime(); }
            catch { return null; }
        }

        // Минуты из команды focus.set: число или строка с числом; всё остальное — 0, то есть пока не выключат
        public static double Minutes(object v)
        {
            if (v == null || v is bool) return 0;
            try
            {
                double m = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                return double.IsNaN(m) || double.IsInfinity(m) ? 0 : m;
            }
            catch { return 0; }
        }

        // Разбор команды focus.set { on, minutes? }: null — всё понятно, иначе текст ответа «не получилось»
        public static string FocusArgs(Dictionary<string, object> args, out bool on, out double minutes)
        {
            on = false;
            minutes = 0;
            object v, m;
            if (args == null || !args.TryGetValue("on", out v) || !(v is bool)) return "on — да или нет";
            on = (bool)v;
            if (args.TryGetValue("minutes", out m)) minutes = Minutes(m);
            return null;
        }

        // Что core.txt сообщает всем. Значок у семьи один, только если так настроено и он у core.txt правда есть.
        public static FamilyState Make(bool oneTray, bool trayIcon, bool focus, long until, bool game, string gameApp)
        {
            return new FamilyState
            {
                Tray = oneTray && trayIcon, Focus = focus, FocusUntil = focus ? Math.Max(0, until) : 0,
                Game = game, GameApp = game ? gameApp ?? "" : ""
            };
        }

        // До какого времени: «15:30», «завтра, 09:00», «25 ноября, 09:00» — по местному времени
        public static string Until(DateTime ends, DateTime now)
        {
            string time = ends.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (ends.Date == now.Date) return time;
            if (ends.Date == now.Date.AddDays(1)) return "завтра, " + time;
            return ends.ToString("d MMMM", Ru) + ", " + time;
        }

        // Подпись «Не беспокоить» в меню значка: со временем, если оно выключится само
        public static string FocusLabel(FamilyState s, DateTime now)
        {
            DateTime? ends = Ends(s);
            return ends.HasValue ? "Не беспокоить до " + Until(ends.Value, now) : "Не беспокоить";
        }

        // Уведомление на острове: короткие заголовок и текст, как просит протокол
        public static string Cut(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        // Куда уходит уведомление core.txt: «island» — карточкой на остров kapsula.txt (он сам решает, показать сразу
        // или тихо положить в историю), «balloon» — уведомлением Windows; null — никуда: значка нет, включено
        // «Не беспокоить» или впереди игра
        public static string NotifyRoute(bool island, bool tray, bool focus, bool game)
        {
            if (island) return "island";
            return tray && !focus && !game ? "balloon" : null;
        }

        // ───────── Команда show ─────────

        // Какую страницу открыть по команде show: «apps», «scenarios», «general», «games», «keys», «replace» или «app:<id>».
        // null — страница не названа или незнакомая: окно просто показывается.
        public static string ShowPage(Dictionary<string, object> args)
        {
            string p = Json.Str(args, "page");
            if (p == null) return null;
            p = p.Trim().ToLowerInvariant();
            if (Array.IndexOf(Pages, p) >= 0) return p;
            return p.StartsWith("app:") && AppId.IsMatch(p.Substring(4)) ? p : null;
        }

        // Приложение app подключено к хабу и понимает команду command (по данным hub.apps)
        public static bool Has(object apps, string app, string command)
        {
            var all = apps as IEnumerable;
            if (all == null || apps is string) return false;
            foreach (object o in all)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || Json.Str(d, "app") != app) continue;
                foreach (object c in Json.List(d, "commands")) if (c as string == command) return true;
            }
            return false;
        }

        // ───────── Пункты настроек с полем section ─────────

        // Общая страница пункта: «games», «startup» или «family»; null — пункт остаётся на странице приложения
        public static string SectionOf(Dictionary<string, object> item)
        {
            string s = Json.Str(item, "section");
            return s != null && Array.IndexOf(Sections, s) >= 0 ? s : null;
        }

        // Пункты настроек приложения, которые показываются на общей странице section
        public static List<Dictionary<string, object>> ItemsIn(object settings, string section)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (object g in Json.List(settings as Dictionary<string, object>, "groups"))
                foreach (object o in Json.List(g as Dictionary<string, object>, "items"))
                {
                    var it = o as Dictionary<string, object>;
                    if (it != null && SectionOf(it) == section) list.Add(it);
                }
            return list;
        }

        // Пункты приложений для общей страницы section — по приложениям, в порядке apps; приложения без таких пунктов
        // пропускаются. settingsOf(id) — что приложение прислало в <id>.settings.
        public static List<KeyValuePair<AppEntry, List<Dictionary<string, object>>>> ByApp(IList<AppEntry> apps, Func<string, object> settingsOf, string section)
        {
            var list = new List<KeyValuePair<AppEntry, List<Dictionary<string, object>>>>();
            foreach (var a in apps)
            {
                var items = ItemsIn(settingsOf(a.Id), section);
                if (items.Count > 0) list.Add(new KeyValuePair<AppEntry, List<Dictionary<string, object>>>(a, items));
            }
            return list;
        }

        // Группы для страницы самого приложения: без переехавших пунктов. Группа, в которой ничего не осталось, пропадает.
        public static List<KeyValuePair<string, List<Dictionary<string, object>>>> OwnGroups(object settings)
        {
            var groups = new List<KeyValuePair<string, List<Dictionary<string, object>>>>();
            foreach (object g in Json.List(settings as Dictionary<string, object>, "groups"))
            {
                var group = g as Dictionary<string, object>;
                if (group == null) continue;
                var items = new List<Dictionary<string, object>>();
                foreach (object o in Json.List(group, "items"))
                {
                    var it = o as Dictionary<string, object>;
                    if (it != null && SectionOf(it) == null) items.Add(it);
                }
                if (items.Count > 0) groups.Add(new KeyValuePair<string, List<Dictionary<string, object>>>(Json.Str(group, "title"), items));
            }
            return groups;
        }

        // На какие общие страницы переехали пункты приложения: «games», «startup», «family» — без повторов
        public static List<string> MovedTo(object settings)
        {
            var list = new List<string>();
            foreach (string s in Sections) if (ItemsIn(settings, s).Count > 0) list.Add(s);
            return list;
        }

        // Подсказка к строке-указателю на странице приложения: что и куда переехало.
        // «Запуск и обновления — в разделе «Общие»; поведение в играх — в разделе «Игры»». null — ничего не переехало.
        public static string MovedHint(List<string> moved)
        {
            var general = new List<string>();
            if (moved.Contains(Startup)) general.Add("запуск и обновления");
            if (moved.Contains(Kin)) general.Add("связь с другими приложениями");
            var parts = new List<string>();
            if (general.Count > 0) parts.Add(string.Join(", ", general.ToArray()) + " — в разделе «Общие»");
            if (moved.Contains(Games)) parts.Add("поведение в играх — в разделе «Игры»");
            if (parts.Count == 0) return null;
            string text = string.Join("; ", parts.ToArray());
            return char.ToUpper(text[0], Ru) + text.Substring(1);
        }

        // ───────── Общее меню в трее ─────────

        // Подключённые приложения: сначала в порядке каталога, потом те, кого в каталоге нет, по имени. Сам core.txt — не в счёт.
        static List<string> Order(IList<AppEntry> catalog, ICollection<string> connected)
        {
            var order = new List<string>();
            foreach (var a in catalog) if (a.Id != "core" && connected.Contains(a.Id) && !order.Contains(a.Id)) order.Add(a.Id);
            var rest = new List<string>();
            foreach (string id in connected) if (id != null && id != "core" && !order.Contains(id) && AppId.IsMatch(id)) rest.Add(id);
            rest.Sort(StringComparer.Ordinal);
            order.AddRange(rest);
            return order;
        }

        static AppEntry Entry(IList<AppEntry> catalog, string id)
        {
            foreach (var a in catalog) if (a.Id == id) return a;
            return null;
        }

        // Название приложения, как его пишут в интерфейсе: «kapsula.txt»
        public static string NameOf(AppEntry a, string id) { return (a != null && !string.IsNullOrEmpty(a.Name) ? a.Name : id).ToLowerInvariant() + ".txt"; }

        // Группы меню: подключённые приложения, у которых есть быстрые действия (<app>.tray), в порядке каталога
        public static List<TrayGroup> TrayGroups(IList<AppEntry> catalog, ICollection<string> connected, Func<string, object> state)
        {
            var groups = new List<TrayGroup>();
            foreach (string id in Order(catalog, connected))
            {
                var items = TrayItem.From(state(id + ".tray"));
                if (items.Count == 0) continue;
                AppEntry a = Entry(catalog, id);
                groups.Add(new TrayGroup { App = id, Name = NameOf(a, id), Sprite = a != null ? a.Sprite : null, Items = items });
            }
            return groups;
        }

        // ───────── Клавиши ─────────

        // Все сочетания подключённых приложений (<app>.keys) в порядке каталога, с найденными совпадениями
        public static List<KeyRow> KeyRows(IList<AppEntry> catalog, ICollection<string> connected, Func<string, object> state)
        {
            var rows = new List<KeyRow>();
            foreach (string id in Order(catalog, connected))
            {
                AppEntry a = Entry(catalog, id);
                foreach (var k in KeyInfo.From(state(id + ".keys")))
                    rows.Add(new KeyRow { App = id, Name = NameOf(a, id), Key = k, Norm = Norm(k.Keys) });
            }
            FindClashes(rows);
            return rows;
        }

        // Сочетание для сравнения — как KeyNames.Normal, только «Num+» (плюс на цифровой клавиатуре) не теряет свой плюс:
        // KeyNames делит запись по «+» и принял бы «Ctrl+Num+» за «Ctrl+Num»
        public static string Norm(string keys)
        {
            string t = (keys ?? "").Trim();
            if (!t.EndsWith("Num+", StringComparison.OrdinalIgnoreCase)) return KeyNames.Normal(t);
            string mods = KeyNames.Normal(t.Substring(0, t.Length - 4));
            return mods.Length == 0 ? "Num+" : mods + "+Num+";
        }

        // Совпадения: одно и то же сочетание у двух включённых действий — в разных приложениях или в одном.
        // Возвращает, сколько пар совпало.
        public static int FindClashes(List<KeyRow> rows)
        {
            int pairs = 0;
            foreach (var r in rows) r.Clash.Clear();
            for (int i = 0; i < rows.Count; i++)
                for (int j = i + 1; j < rows.Count; j++)
                {
                    KeyRow a = rows[i], b = rows[j];
                    if (!a.Key.On || !b.Key.On || string.IsNullOrEmpty(a.Norm) || a.Norm != b.Norm) continue;
                    a.Clash.Add(b);
                    b.Clash.Add(a);
                    pairs++;
                }
            return pairs;
        }

        // Что сказать под совпавшим сочетанием: «то же сочетание: «Диктовка» в kapsula.txt»; null — совпадений нет
        public static string ClashText(KeyRow row)
        {
            var parts = new List<string>();
            foreach (var o in row.Clash) parts.Add("«" + o.Key.Label + "» " + (o.App == row.App ? "в этом же приложении" : "в " + o.Name));
            return parts.Count == 0 ? null : "то же сочетание: " + string.Join(", ", parts.ToArray());
        }

        // Клавиши сочетания по отдельности, как на клавиатуре: «alt+ctrl+s» → Ctrl, Alt, S. Подписи — как на самих
        // клавишах (PgUp, PrtSc), по-русски — там, где на клавише нет слов (пробел, стрелки, мультимедиа); «`» — «Ё».
        public static List<string> Caps(string keys)
        {
            var list = new List<string>();
            string norm = Norm(keys);
            bool numPlus = norm.EndsWith("Num+");
            if (numPlus) norm = norm.Substring(0, norm.Length - 4);
            foreach (string p in norm.Split('+')) if (p.Length > 0) list.Add(Cap(p));
            if (numPlus) list.Add("Num +");
            return list;
        }

        // Подпись одной клавиши из записи KeyNames
        static string Cap(string key)
        {
            switch (key)
            {
                case "Space": return "Пробел";
                case "Left": return "←";
                case "Right": return "→";
                case "Up": return "↑";
                case "Down": return "↓";
                case "PageUp": return "PgUp";
                case "PageDown": return "PgDn";
                case "PrintScreen": return "PrtSc";
                case "CapsLock": return "Caps Lock";
                case "NumLock": return "Num Lock";
                case "ScrollLock": return "Scroll Lock";
                case "Insert": return "Ins";
                case "Delete": return "Del";
                case "PlayPause": return "Воспр./пауза";
                case "NextTrack": return "След. трек";
                case "PrevTrack": return "Пред. трек";
                case "VolumeUp": return "Громче";
                case "VolumeDown": return "Тише";
                case "Mute": return "Без звука";
                case "`": return "Ё";
                case "0x5D": return "Меню";
            }
            if (key.Length == 4 && key.StartsWith("Num")) return "Num " + key.Substring(3);    // Num1 → Num 1, Num* → Num *
            if (key.Length > 2 && key.StartsWith("0x")) return "Клавиша " + key.Substring(2);  // у клавиши нет названия — её код
            return key;
        }
    }
}
