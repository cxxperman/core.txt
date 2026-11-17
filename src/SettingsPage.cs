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
        public static void Set(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (on) k.SetValue(Name, "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\" --tray");
                    else k.DeleteValue(Name, false);
                }
            }
            catch { }
        }
    }

    class SettingsPage
    {
        public readonly ScrollViewer Root;
        readonly StackPanel stack = new StackPanel { Margin = new Thickness(7, 0, 7, 12) };
        readonly MainWindow w;

        public SettingsPage(MainWindow w)
        {
            this.w = w;
            Root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Padding = new Thickness(25, 0, 25, 0), Content = stack, Focusable = false, Visibility = Visibility.Collapsed };
        }

        // ───────── Кирпичики страницы ─────────
        void Title(string text)
        {
            var t = Ui.T(text, Tokens.Size.Heading, Ui.Label2, FontWeights.SemiBold);
            t.Margin = new Thickness(4, stack.Children.Count == 0 ? 2 : 20, 0, 8);
            stack.Children.Add(t);
        }

        void Row(string label, string hint, FrameworkElement control) { Row(label, hint, control, null); }

        // below — что-то широкое под названием (например, плашки вариантов)
        void Row(string label, string hint, FrameworkElement control, FrameworkElement below)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            text.Children.Add(Ui.T(label, Tokens.Size.Body, Ui.Label, FontWeights.Normal));
            if (!string.IsNullOrEmpty(hint))
            {
                var h = Ui.T(hint, Tokens.Size.Micro, Ui.Label3, FontWeights.Normal);
                h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(0, 1, 0, 0);
                text.Children.Add(h);
            }
            var line = new DockPanel { LastChildFill = true, MinHeight = 30 };
            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(control, Dock.Right);
                line.Children.Add(control);
            }
            line.Children.Add(text);
            UIElement content = line;
            if (below != null) content = new StackPanel { Children = { line, below } };
            var row = Ui.Row(content);
            row.Margin = new Thickness(0, 0, 0, 8);
            stack.Children.Add(row);
        }

        // Шапка раздела приложения: значок и название с подписью
        void AppTitle(Card c)
        {
            var name = Ui.Brand(c.App.Name, Tokens.Size.Title, FontWeights.SemiBold);
            name.VerticalAlignment = VerticalAlignment.Center;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 26, 0, 10) };
            if (c.App.Sprite != null)
            {
                var s = Ui.Sprite(c.App.Sprite, 22);
                s.Margin = new Thickness(0, 0, 9, 0); s.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(s);
            }
            line.Children.Add(name);
            stack.Children.Add(line);
        }

        FrameworkElement Swatches()
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var colors = new Color?[] { null, Tokens.Palette.Brand, Tokens.Palette.Blue, Tokens.Palette.Purple, Tokens.Palette.Green, Tokens.Palette.Pink };
            var names = new string[] { "Свой у каждого приложения", "Оранжевый", "Синий", "Фиолетовый", "Зелёный", "Розовый" };
            for (int i = 0; i < colors.Length; i++)
            {
                Color? c = colors[i];
                string hex = c.HasValue ? "#" + c.Value.R.ToString("X2") + c.Value.G.ToString("X2") + c.Value.B.ToString("X2") : null;
                bool on = string.Equals(hex, State.Accent, StringComparison.OrdinalIgnoreCase);
                var inner = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = c.HasValue ? Ui.Solid(c.Value) : Ui.GlassStrong, BorderBrush = c.HasValue ? null : Ui.Rim, BorderThickness = new Thickness(c.HasValue ? 0 : 1) };
                var ring = new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), BorderBrush = on ? Ui.Label : Brushes.Transparent,
                    Background = Brushes.Transparent, Child = inner, Cursor = Cursors.Hand, ToolTip = names[i], Margin = new Thickness(4, 0, 0, 0)
                };
                inner.HorizontalAlignment = HorizontalAlignment.Center; inner.VerticalAlignment = VerticalAlignment.Center;
                ring.MouseLeftButtonDown += delegate { w.SetTheme(hex ?? "", null); };
                row.Children.Add(ring);
            }
            return row;
        }

        static double Num(object v, double fallback)
        {
            try { return v == null ? fallback : Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return fallback; }
        }
        static object Get(Dictionary<string, object> d, string key) { object v; return d.TryGetValue(key, out v) ? v : null; }
        static bool Same(object a, object b) { return Convert.ToString(a, CultureInfo.InvariantCulture) == Convert.ToString(b, CultureInfo.InvariantCulture); }

        // Одна настройка приложения — по её описанию
        void Item(AppEntry app, Dictionary<string, object> it)
        {
            string key = Json.Str(it, "key"), type = Json.Str(it, "type"), label = Json.Str(it, "label") ?? key, hint = Json.Str(it, "hint");
            if (key == null || type == null) return;
            object value = Get(it, "value");
            Action<object> set = delegate(object v) { w.SendCommand(app.Id, "settings.set", Hub.Msg("key", key, "value", v)); };

            if (type == "switch") Row(label, hint, new Switch(value is bool && (bool)value, delegate(bool on) { set(on); }).Root);
            else if (type == "choice")
            {
                var values = new List<object>();
                var labels = new List<string>();
                foreach (object o in Json.List(it, "options"))
                {
                    var opt = o as Dictionary<string, object>;
                    if (opt == null) continue;
                    values.Add(Get(opt, "value"));
                    labels.Add(Json.Str(opt, "label") ?? Convert.ToString(Get(opt, "value"), CultureInfo.InvariantCulture));
                }
                if (values.Count == 0) return;
                int current = Math.Max(0, values.FindIndex(delegate(object v) { return Same(v, value); }));
                if (values.Count <= 4) Row(label, hint, new Seg(labels.ToArray(), current, delegate(int i) { set(values[i]); }).Root);
                else
                {
                    // Вариантов много — все на виду, плашками под названием
                    var chips = new WrapPanel { Margin = new Thickness(-3, 8, -3, -3) };
                    for (int n = 0; n < values.Count; n++)
                    {
                        object v = values[n];
                        bool on = n == current;
                        var t = Ui.T(labels[n], Tokens.Size.Body, on ? Ui.Label : Ui.Label2, on ? FontWeights.SemiBold : FontWeights.Normal);
                        t.VerticalAlignment = VerticalAlignment.Center;
                        var chip = new Border
                        {
                            Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(13, 0, 13, 1), Margin = new Thickness(3), BorderThickness = new Thickness(1),
                            Background = on ? Ui.GlassStrong : Ui.GlassFill, BorderBrush = on ? Ui.Rim : Brushes.Transparent, Child = t, Cursor = Cursors.Hand
                        };
                        if (!on)
                        {
                            chip.MouseEnter += delegate { chip.Background = Ui.GlassHover; };
                            chip.MouseLeave += delegate { chip.Background = Ui.GlassFill; };
                            chip.MouseLeftButtonDown += delegate { set(v); };
                        }
                        chips.Children.Add(chip);
                    }
                    Row(label, hint, null, chips);
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
                Row(label, hint, new StackPanel { Orientation = Orientation.Horizontal, Children = { number, slider.Root } });
            }
            else if (type == "action")
            {
                var b = new Btn();
                b.Set(Json.Str(it, "button") ?? "Выполнить", null, true, delegate { w.SendCommand(app.Id, "settings.run", Hub.Msg("key", key)); });
                Row(label, hint, b.Root);
            }
            // Вид настройки, которого эта версия core.txt не знает, пропускается
        }

        // ───────── Вся страница заново — по текущему состоянию ─────────
        public void Render()
        {
            double offset = Root.VerticalOffset;
            stack.Children.Clear();

            Title("Вид");
            Row("Акцентный цвет", "Один цвет для всех приложений — или свой у каждого", Swatches());
            Row("Меньше движения", "Без пружин и плавных переходов во всех приложениях", new Switch(State.ReducedMotion, delegate(bool on) { w.SetTheme(null, on); }).Root);

            Title("Запуск");
            Row("Запускать core.txt вместе с Windows", "Тихо, в трее: приложения сразу видят друг друга и общие настройки", new Switch(w.DemoMode ? true : Autostart.Enabled, delegate(bool on) { if (!w.DemoMode) Autostart.Set(on); }).Root);
            Row("Оставаться в трее после закрытия окна", "Иначе core.txt закрывается совсем, и связь между приложениями пропадает", new Switch(State.Tray, delegate(bool on) { State.Tray = on; if (!w.DemoMode) State.Save(); }).Root);

            Title("Обновления");
            Row("Проверять обновления при запуске", null, new Switch(State.AutoCheck, delegate(bool on) { State.AutoCheck = on; if (!w.DemoMode) State.Save(); }).Root);

            foreach (var c in w.Cards)
            {
                var card = c;
                object data;
                w.States.TryGetValue(c.App.Id + ".settings", out data);
                var settings = data as Dictionary<string, object>;
                if (settings == null && (c.App.IsIntegration || !c.S.Installed)) continue;   // показывать нечего
                AppTitle(c);
                if (settings == null)
                {
                    var b = new Btn();
                    if (c.S.Running)
                    {
                        b.Set("Открыть", null, true, delegate { w.Open(card); });
                        Row("Эта версия ещё не передаёт настройки в core.txt", "Пока они меняются в самом приложении", b.Root);
                    }
                    else
                    {
                        b.Set("Запустить", c.App.Color, true, delegate { w.Open(card); });
                        Row("Приложение не запущено", "Его настройки появятся здесь, как только оно заработает", b.Root);
                    }
                    continue;
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
            Root.ScrollToVerticalOffset(offset);
        }
    }
}
