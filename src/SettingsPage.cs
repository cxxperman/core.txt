// core.txt. SettingsPage.cs — страница «Настройки»: общие настройки экосистемы и настройки каждого приложения.
// Свои настройки приложение присылает через хаб состоянием «<приложение>.settings» (описание — в kit.txt,
// hub/PROTOCOL.md), а core.txt рисует их общими элементами. Поэтому у всех приложений они выглядят одинаково,
// и новое приложение получает страницу настроек, ничего не рисуя само.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevTxt.Core
{
    static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "core.txt";

        public static bool Enabled
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Key)) return k != null && k.GetValue(Name) != null; }
                catch { return false; }
            }
        }

        // Запуск вместе с Windows — тихо, в трее. Меняется только по переключателю в настройках.
        public static void Set(bool on) { Set(on, System.Reflection.Assembly.GetExecutingAssembly().Location); }

        // exe — какой файл запускать: при установке core.txt это уже его постоянное место
        public static void Set(bool on, string exe)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (on) k.SetValue(Name, "\"" + exe + "\" --tray");
                    else k.DeleteValue(Name, false);
                }
            }
            catch { }
        }
    }

    class SettingsPage : Page
    {
        public SettingsPage(MainWindow w) : base(w) { }

        static double Num(object v, double fallback)
        {
            try { return v == null ? fallback : Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }
        static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }
        static bool Same(object a, object b) { return Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture); }

        // Раздел собирается заново, только когда изменился его состав (набор строк). Всё остальное меняется на месте:
        // тогда переключатель доезжает до конца, а фокус с клавиатуры остаётся там, где был.
        readonly List<Action> onFacts = new List<Action>();    // подписи, кнопки, кольцо цвета — сразу
        readonly List<Action> onValues = new List<Action>();   // положение переключателей и выбора — см. touched ниже
        string drawnSig;

        Dictionary<string, object> SettingsOf(string appId)
        {
            object data;
            w.States.TryGetValue(appId + ".settings", out data);
            return data as Dictionary<string, object>;
        }

        // Описание одной настройки приложения, как оно выглядит прямо сейчас; null — такой настройки нет
        Dictionary<string, object> FindItem(string appId, string key)
        {
            Dictionary<string, object> found = null;
            foreach (object g in Json.List(SettingsOf(appId), "groups"))
                foreach (object o in Json.List(g as Dictionary<string, object>, "items"))
                {
                    var it = o as Dictionary<string, object>;
                    if (it != null && Json.Str(it, "key") == key) found = it;
                }
            return found;
        }

        // Выбор из вариантов: короткие подписи встают в ряд справа, длинные или многочисленные — плашками под названием
        public static bool UseChips(IList<string> labels)
        {
            if (labels.Count > 4) return true;
            int total = 0;
            foreach (string s in labels)
            {
                if (s.Length > 18) return true;
                total += s.Length;
            }
            return total > 40;
        }

        // Всё, от чего зависит, из чего собрана строка настройки. Значение и подсказка сюда не входят — они меняются на месте.
        public static string ItemSig(Dictionary<string, object> it)
        {
            var parts = new List<string> { Json.Str(it, "key"), Json.Str(it, "type"), Json.Str(it, "label"), Json.Str(it, "unit"), Json.Str(it, "button"),
                Convert.ToString(Get(it, "min"), CultureInfo.InvariantCulture), Convert.ToString(Get(it, "max"), CultureInfo.InvariantCulture), Convert.ToString(Get(it, "step"), CultureInfo.InvariantCulture) };
            foreach (object o in Json.List(it, "options"))
            {
                var opt = o as Dictionary<string, object>;
                if (opt != null) parts.Add(Convert.ToString(Get(opt, "value"), CultureInfo.InvariantCulture) + "=" + Json.Str(opt, "label"));
            }
            return string.Join("\u0001", parts.ToArray());
        }

        void Item(AppEntry app, Dictionary<string, object> it) { Item(app, it, null); }

        // Одна настройка приложения — по её описанию. title — своё название вместо присланного.
        void Item(AppEntry app, Dictionary<string, object> it, string title)
        {
            string key = Json.Str(it, "key"), type = Json.Str(it, "type"), label = title ?? Json.Str(it, "label") ?? key, hint = Json.Str(it, "hint");
            if (key == null || type == null) return;
            object value = Get(it, "value");
            string appId = app.Id;
            Action<object> set = delegate(object v) { w.SendCommand(appId, "settings.set", Hub.Msg("key", key, "value", v)); };
            Func<Dictionary<string, object>> now = delegate { return FindItem(appId, key); };
            RowRef row = null;

            if (type == "switch")
            {
                var sw = new Switch(value is bool && (bool)value, delegate(bool on) { set(on); });
                row = Row(label, hint, sw.Root);
                onValues.Add(delegate { var cur = now(); if (cur != null) { object v = Get(cur, "value"); sw.Value = v is bool && (bool)v; } });
            }
            else if (type == "choice")
            {
                var vals = new List<object>();
                var labels = new List<string>();
                foreach (object o in Json.List(it, "options"))
                {
                    var opt = o as Dictionary<string, object>;
                    if (opt == null) continue;
                    vals.Add(Get(opt, "value"));
                    labels.Add(Json.Str(opt, "label") ?? Convert.ToString(Get(opt, "value"), CultureInfo.InvariantCulture));
                }
                if (vals.Count == 0) return;
                Func<int> current = delegate
                {
                    var cur = now();
                    object v = cur != null ? Get(cur, "value") : value;
                    return Math.Max(0, vals.FindIndex(delegate(object x) { return Same(x, v); }));
                };
                if (!UseChips(labels))
                {
                    var seg = new Seg(labels.ToArray(), current(), delegate(int i) { set(vals[i]); });
                    row = Row(label, hint, seg.Root);
                    onValues.Add(delegate { seg.Index = current(); });
                }
                else
                {
                    // Вариантов много или подписи длинные — все на виду, плашками под названием
                    var chips = new WrapPanel { Margin = new Thickness(-3, 8, -3, -3) };
                    var boxes = new List<Border>();
                    int chosen = current();
                    Action paint = delegate
                    {
                        for (int n = 0; n < boxes.Count; n++)
                        {
                            bool on = n == chosen;
                            var t = (TextBlock)boxes[n].Child;
                            t.Foreground = on ? Ui.Label : Ui.Label2;
                            t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                            boxes[n].Background = on ? Ui.GlassStrong : boxes[n].IsMouseOver ? Ui.GlassHover : Ui.GlassFill;
                            boxes[n].BorderBrush = on ? Ui.Rim : Brushes.Transparent;
                        }
                    };
                    for (int n = 0; n < vals.Count; n++)
                    {
                        int index = n;
                        var t = Ui.T(labels[n], Tokens.Size.Body, Ui.Label2, FontWeights.Normal);
                        t.VerticalAlignment = VerticalAlignment.Center;
                        var chip = new Border
                        {
                            Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(13, 0, 13, 1), Margin = new Thickness(3), BorderThickness = new Thickness(1),
                            Child = t, Cursor = Cursors.Hand
                        };
                        // Нажали — плашка выбрана сразу, не дожидаясь ответа приложения
                        Action pick = delegate { if (chosen == index) return; chosen = index; paint(); set(vals[index]); };
                        chip.MouseEnter += delegate { paint(); };
                        chip.MouseLeave += delegate { paint(); };
                        chip.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; pick(); };
                        Ui.Keyboard(chip, pick);
                        boxes.Add(chip);
                        chips.Children.Add(chip);
                    }
                    paint();
                    row = Row(label, hint, null, chips);
                    onValues.Add(delegate { int c = current(); if (c != chosen) { chosen = c; paint(); } });
                }
            }
            else if (type == "slider")
            {
                double min = Num(Get(it, "min"), 0), max = Num(Get(it, "max"), 100), step = Num(Get(it, "step"), 1);
                string unit = Json.Str(it, "unit") ?? "";
                var number = Ui.T("", Tokens.Size.Caption, Ui.Label2, FontWeights.Normal);
                number.VerticalAlignment = VerticalAlignment.Center; number.Margin = new Thickness(0, 0, 10, 0); number.MinWidth = 44; number.TextAlignment = TextAlignment.Right;
                Func<double, string> show = delegate(double v) { return v.ToString("0.##", CultureInfo.CurrentCulture) + (unit == "" ? "" : " " + unit); };
                var slider = new Slider(min, max, step, Num(value, min), app.Color, delegate(double v) { set(v); });
                number.Text = show(slider.Value);
                slider.Root.MouseMove += delegate { number.Text = show(slider.Value); };
                slider.Root.MouseLeftButtonDown += delegate { number.Text = show(slider.Value); };
                row = Row(label, hint, new StackPanel { Orientation = Orientation.Horizontal, Children = { number, slider.Root } });
                onValues.Add(delegate
                {
                    var cur = now();
                    if (cur == null || slider.Root.IsMouseCaptured) return;   // бегунок в руке — не выдёргиваем
                    slider.Value = Num(Get(cur, "value"), min);
                    number.Text = show(slider.Value);
                });
            }
            else if (type == "action")
            {
                var b = new Btn();
                b.Set(Json.Str(it, "button") ?? "Выполнить", null, true, delegate { w.SendCommand(appId, "settings.run", Hub.Msg("key", key)); });
                row = Row(label, hint, b.Root);
            }
            // Вид настройки, которого эта версия core.txt не знает, пропускается
            if (row != null) onFacts.Add(delegate { var cur = now(); if (cur != null) row.SetHint(Json.Str(cur, "hint")); });
        }

        // Чем сейчас показана замена: "install" — приложения нет, "open" — не запущено, "item" — его настройка; null — такого приложения нет
        string ReplaceKind(string appId, string key, out Card card, out Dictionary<string, object> found)
        {
            found = null;
            card = w.Cards.Find(delegate(Card c) { return c.App.Id == appId; });
            if (card == null) return null;
            if (!card.S.Installed) return "install";
            found = FindItem(appId, key);
            return found == null ? "open" : "item";
        }

        // Кнопка, которая открывает приложение: пока оно запускается, она это и говорит
        void OpenButton(Btn b, Card card, string label, Color? tint)
        {
            Action paint = delegate
            {
                if (card.S.Launching) b.Set("Запускается…", null, false, null);
                else b.Set(label, tint, true, delegate { w.Open(card); });
            };
            paint();
            onFacts.Add(paint);
        }

        // Одна замена: настройка самого приложения под понятным названием; приложения нет — «Установить», не запущено — «Открыть»
        void Replacement(string what, string appId, string key)
        {
            Card card;
            Dictionary<string, object> found;
            string kind = ReplaceKind(appId, key, out card, out found);
            if (kind == null) return;
            string name = card.App.Name.ToLowerInvariant() + ".txt";
            if (kind == "install")
            {
                var b = new Btn();
                Action paint = delegate
                {
                    bool idle = card.S.Phase == Phase.Idle;
                    b.Set(idle ? "Установить" : "Установка…", idle ? (Color?)Ui.Accent : null, idle, delegate { w.InstallId(appId); });
                };
                paint();
                onFacts.Add(paint);
                Row(what, "Заменяет " + name + ". Пока его нет, работает то, что в Windows", b.Root);
            }
            else if (kind == "open")
            {
                var b = new Btn();
                OpenButton(b, card, "Открыть " + name, null);
                Row(what, name + " не запущен — пока он закрыт, работает то, что в Windows", b.Root);
            }
            else Item(card.App, found, what + " — " + name);
        }

        // Какой раздел показан: «general», «replace» или «app:<id>»
        public string Section = "general";
        string shownSection;

        // Пока человек только что нажал переключатель, его положение не трогаем: иначе ответ приложения приходит
        // посреди движения бегунка, и тот прыгает на место вместо того, чтобы доехать. Подписи и кнопки меняются сразу.
        DateTime touched = DateTime.MinValue;
        System.Windows.Threading.DispatcherTimer later;
        bool hooked;

        // Из чего сейчас должен состоять раздел. Пока это не изменилось, раздел не собирается заново.
        string Signature()
        {
            if (Section == "general") return "general";
            var parts = new List<string> { Section };
            if (Section == "replace")
            {
                foreach (string[] r in Replaced)
                {
                    Card card;
                    Dictionary<string, object> found;
                    string kind = ReplaceKind(r[1], r[2], out card, out found);
                    parts.Add(kind == "item" ? ItemSig(found) : kind ?? "");
                }
                return string.Join("\u0002", parts.ToArray());
            }
            Card c = w.Cards.Find(delegate(Card x) { return "app:" + x.App.Id == Section; });
            if (c == null) return Section + "|none";
            var settings = SettingsOf(c.App.Id);
            if (settings == null) return Section + (!c.App.HasExe || !c.S.Installed ? "|none" : c.S.Running ? "|silent" : "|closed");
            foreach (object g in Json.List(settings, "groups"))
            {
                var group = g as Dictionary<string, object>;
                if (group == null) continue;
                parts.Add("#" + Json.Str(group, "title"));
                foreach (object i in Json.List(group, "items"))
                {
                    var it = i as Dictionary<string, object>;
                    if (it != null) parts.Add(ItemSig(it));
                }
            }
            return string.Join("\u0002", parts.ToArray());
        }

        // Что из Windows заменяют приложения: название, приложение, его настройка
        static readonly string[][] Replaced =
        {
            new string[] { "Панель задач", "dock", "bar" },
            new string[] { "Кнопка «Пуск»", "dock", "winKey" },
            new string[] { "Переключение окон Alt+Tab", "smena", "replace" },
        };

        // ───────── Раздел — по текущему состоянию ─────────
        public void Render()
        {
            if (!hooked)
            {
                hooked = true;
                Root.PreviewMouseLeftButtonDown += delegate { touched = DateTime.UtcNow; };
                Root.PreviewKeyDown += delegate { touched = DateTime.UtcNow; };
            }
            string sig = Signature();
            double wait = 400 - (DateTime.UtcNow - touched).TotalMilliseconds;
            bool same = sig == drawnSig && shownSection == Section;
            if (same || wait > 0 && shownSection == Section)
            {
                foreach (var a in onFacts.ToArray()) a();
                if (wait > 0)
                {
                    if (later == null) { later = new System.Windows.Threading.DispatcherTimer(); later.Tick += delegate { later.Stop(); Render(); }; }
                    later.Stop(); later.Interval = TimeSpan.FromMilliseconds(wait); later.Start();
                }
                else foreach (var a in onValues.ToArray()) a();
                return;
            }

            double offset = shownSection == Section ? Root.VerticalOffset : 0;
            shownSection = Section;
            drawnSig = sig;
            stack.Children.Clear();
            onFacts.Clear();
            onValues.Clear();
            if (Section == "replace") foreach (string[] r in Replaced) Replacement(r[0], r[1], r[2]);
            else if (Section == "general") General();
            else AppSection();
            Root.ScrollToVerticalOffset(offset);
        }

        void General()
        {
            Title("Вид");
            Row("Акцентный цвет", "Один цвет для всех приложений — или свой у каждого", Swatches());
            onFacts.Add(PaintSwatches);
            var motion = new Switch(State.ReducedMotion, delegate(bool on) { w.SetTheme(null, on); });
            Row("Меньше движения", "Без пружин и плавных переходов во всех приложениях", motion.Root);

            Title("Запуск");
            var auto = new Switch(w.DemoMode ? true : Autostart.Enabled, delegate(bool on) { if (!w.DemoMode) Autostart.Set(on); });
            Row("Запускать core.txt вместе с Windows", "Тихо, в трее: приложения сразу видят друг друга и общие настройки", auto.Root);
            var tray = new Switch(State.Tray, delegate(bool on) { State.Tray = on; if (!w.DemoMode) State.Save(); });
            Row("Оставаться в трее после закрытия окна", "Иначе core.txt закрывается совсем, и связь между приложениями пропадает", tray.Root);

            Title("Папка");
            var home = new Btn();
            var homeRow = Row("Все приложения в одной папке", "", home.Root);
            Action paintHome = delegate
            {
                int outside = w.OutsideCount();
                // Пока идёт перенос, кнопка не нажимается: второй перенос поверх первого не начнётся
                if (w.GatherBusy) home.Set("Переносится…", null, false, null);
                else if (outside > 0) home.Set("Собрать", Ui.Accent, true, delegate { w.GatherAll(); });
                else home.Set("Открыть папку", null, true, delegate { w.OpenHome(); });
                homeRow.SetHint(outside > 0
                    ? "Не в общей папке dev.txt: " + outside + ". Перенос закроет и снова запустит эти приложения; aura.txt остаётся там, куда её поставил установщик"
                    : "Приложения dev.txt лежат вместе, в папке программ dev.txt");
            };
            paintHome();
            onFacts.Add(paintHome);

            Title("Обновления");
            var check = new Switch(State.AutoCheck, delegate(bool on) { State.AutoCheck = on; if (!w.DemoMode) State.Save(); });
            Row("Проверять обновления при запуске", null, check.Root);

            onValues.Add(delegate
            {
                motion.Value = State.ReducedMotion;
                auto.Value = w.DemoMode ? true : Autostart.Enabled;
                tray.Value = State.Tray;
                check.Value = State.AutoCheck;
            });
        }

        // Раздел одного приложения: то, что оно само прислало хабу
        void AppSection()
        {
            Card c = w.Cards.Find(delegate(Card x) { return "app:" + x.App.Id == Section; });
            if (c == null) return;
            var settings = SettingsOf(c.App.Id);
            if (settings == null)
            {
                if (!c.App.HasExe || !c.S.Installed) return;   // показывать нечего
                var b = new Btn();
                if (c.S.Running)
                {
                    OpenButton(b, c, "Открыть", null);
                    Row("Эта версия ещё не передаёт настройки в core.txt", "Пока они меняются в самом приложении", b.Root);
                }
                else
                {
                    OpenButton(b, c, "Запустить", c.App.Color);
                    Row("Приложение не запущено", "Его настройки появятся здесь, как только оно заработает", b.Root);
                }
                return;
            }
            foreach (object g in Json.List(settings, "groups"))
            {
                var group = g as Dictionary<string, object>;
                if (group == null) continue;
                string title = Json.Str(group, "title");
                if (!string.IsNullOrEmpty(title)) Title(title);
                foreach (object i in Json.List(group, "items"))
                {
                    var it = i as Dictionary<string, object>;
                    if (it != null) Item(c.App, it);
                }
            }
        }
    }
}
