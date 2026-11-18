// core.txt. Scenarios.cs — сценарии: приложения реагируют друг на друга.
// Сценарий — правило «пока верно условие, держать настройку другого приложения в нужном значении»: пока идёт
// звонок в Discord, обработка голоса в Aura включена; звонок кончился — настройка возвращается к тому, что
// было до него. Условие берётся из состояния, которое приложение сообщает хабу, а действие — обычная команда
// settings.set, та же, что со страницы настроек. Поэтому сценарию не нужно ничего знать о самих приложениях,
// и новый сценарий — это одна запись в списке ниже.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace DevTxt.Core
{
    class Scenario
    {
        public string Id, Title, Hint;
        public string[] Needs;      // какие приложения участвуют
        public string Topic, Field; // условие: поле Field состояния Topic истинно (Field == null — состояние просто есть)
        public string Target, Key;  // что менять: настройка Key приложения Target…
        public object On;           // …на это значение, пока условие верно
    }

    class Scenarios
    {
        public static readonly Scenario[] All =
        {
            new Scenario
            {
                Id = "call-aura-on", Title = "В звонке Discord включать обработку голоса",
                Hint = "Зашли в голосовой канал или приняли звонок — Aura включается. Вышли — возвращается как было",
                Needs = new string[] { "maska", "aura" }, Topic = "maska.voice", Field = "inCall", Target = "aura", Key = "power", On = true
            },
            new Scenario
            {
                Id = "mute-aura", Title = "Микрофон выключен в Discord — заглушить и в Aura",
                Hint = "Тишина во всех программах, пока вы заглушены в звонке",
                Needs = new string[] { "maska", "aura" }, Topic = "maska.voice", Field = "mute", Target = "aura", Key = "mute", On = true
            },
            new Scenario
            {
                Id = "game-hide-island", Title = "Во время игры прятать остров",
                Hint = "Пока запущена игра из Steam, остров уезжает за край и не мешает. После игры — как было",
                Needs = new string[] { "arkada", "kapsula" }, Topic = "arkada.game", Field = null, Target = "kapsula", Key = "autoHide", On = true
            },
            new Scenario
            {
                Id = "game-pet-home", Title = "Во время игры зверёк не гуляет по окнам",
                Hint = "Пока запущена игра из Steam, зверёк сидит в капсуле",
                Needs = new string[] { "arkada", "kapsula" }, Topic = "arkada.game", Field = null, Target = "kapsula", Key = "walk", On = false
            },
        };

        readonly Func<string, object> state;              // тема → что приложение сообщило хабу (или null)
        readonly Action<string, string, object> set;      // (приложение, настройка, значение)
        readonly Func<string, bool> enabled;              // включён ли сценарий
        readonly Dictionary<string, bool> active = new Dictionary<string, bool>();        // условие сейчас верно и сценарий применён
        readonly Dictionary<string, object> previous = new Dictionary<string, object>();  // что было до сценария — только если он что-то поменял

        public Scenarios(Func<string, object> state, Action<string, string, object> set, Func<string, bool> enabled)
        {
            this.state = state; this.set = set; this.enabled = enabled;
        }

        public static bool Truthy(object data, string field)
        {
            if (data == null) return false;
            if (field == null) return true;
            var d = data as Dictionary<string, object>;
            object v;
            if (d == null || !d.TryGetValue(field, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            if (v is string) return (string)v != "";
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0; } catch { return true; }
        }

        // Значение настройки из описания «<приложение>.settings»; null — такой настройки сейчас нет
        public static object SettingValue(object settings, string key)
        {
            foreach (object g in Json.List(settings as Dictionary<string, object>, "groups"))
                foreach (object i in Json.List(g as Dictionary<string, object>, "items"))
                {
                    var item = i as Dictionary<string, object>;
                    object v;
                    if (item != null && Json.Str(item, "key") == key) return item.TryGetValue("value", out v) ? v : null;
                }
            return null;
        }

        static bool Same(object a, object b) { return Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture); }

        public bool IsActive(string id) { bool on; return active.TryGetValue(id, out on) && on; }

        // Сверяет все сценарии с тем, что сейчас известно хабу. Вызывается после каждого изменения состояний
        // и после того, как сценарий включили или выключили.
        public void Update()
        {
            foreach (var s in All)
            {
                bool was = IsActive(s.Id);
                bool want = enabled(s.Id) && Truthy(state(s.Topic), s.Field);
                if (want == was) continue;
                if (want)
                {
                    object current = SettingValue(state(s.Target + ".settings"), s.Key);
                    if (current == null) continue;   // приложение не подключено или такой настройки сейчас нет — попробуем при следующем изменении
                    active[s.Id] = true;
                    if (Same(current, s.On)) continue;   // уже как надо: менять нечего и возвращать потом нечего
                    previous[s.Id] = current;
                    set(s.Target, s.Key, s.On);
                }
                else
                {
                    active[s.Id] = false;
                    object before;
                    if (!previous.TryGetValue(s.Id, out before)) continue;
                    previous.Remove(s.Id);
                    // Возвращаем, только если настройку с тех пор не поменяли руками
                    object current = SettingValue(state(s.Target + ".settings"), s.Key);
                    if (current != null && Same(current, s.On)) set(s.Target, s.Key, before);
                }
            }
        }
    }
}
