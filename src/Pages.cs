// core.txt. Pages.cs — общие кирпичики страниц и две страницы: «Сценарии» и первый запуск.
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevTxt.Core
{
    // Страница — прокручиваемый столбец из заголовков групп и стеклянных строк
    class Page
    {
        public readonly ScrollViewer Root;
        protected readonly StackPanel stack = new StackPanel { Margin = new Thickness(7, 0, 7, 12) };
        protected readonly MainWindow w;

        protected Page(MainWindow w)
        {
            this.w = w;
            Root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Padding = new Thickness(25, 0, 25, 0), Content = stack, Focusable = false, Visibility = Visibility.Collapsed };
        }

        // Появление страницы: строки поднимаются волной. Только по действию человека, не при каждом обновлении.
        public void Animate() { Motion.Stagger(stack); }

        protected void Title(string text)
        {
            var t = Ui.T(text, Tokens.Size.Heading, Ui.Label2, FontWeights.SemiBold);
            t.Margin = new Thickness(4, stack.Children.Count == 0 ? 2 : 20, 0, 8);
            stack.Children.Add(t);
        }

        protected void Row(string label, string hint, FrameworkElement control) { Row(label, hint, control, null); }

        // below — что-то широкое под названием (например, плашки вариантов)
        protected void Row(string label, string hint, FrameworkElement control, FrameworkElement below)
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
        protected void AppTitle(Card c)
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

        // Выбор акцентного цвета: «свой у каждого» и пять общих
        protected FrameworkElement Swatches()
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

        // Названия приложений через запятую, как их пишут в экосистеме: «maska.txt, aura.txt»
        protected string Names(string[] ids)
        {
            var list = new List<string>();
            foreach (string id in ids)
            {
                Card c = w.Cards.Find(delegate(Card x) { return x.App.Id == id; });
                list.Add((c != null ? c.App.Name : id).ToLowerInvariant() + ".txt");
            }
            return string.Join(", ", list.ToArray());
        }
    }

    // «Сценарии»: готовые правила, по которым приложения реагируют друг на друга. Каждое включается переключателем.
    class ScenariosPage : Page
    {
        public ScenariosPage(MainWindow w) : base(w) { }

        public void Render()
        {
            double offset = Root.VerticalOffset;
            stack.Children.Clear();
            var intro = Ui.T("Приложения могут помогать друг другу сами. Включите то, что вам подходит: сценарий действует, пока верно его условие, а потом возвращает всё как было.", Tokens.Size.Body, Ui.Label2, FontWeights.Normal);
            intro.TextWrapping = TextWrapping.Wrap; intro.Margin = new Thickness(4, 2, 4, 14);
            stack.Children.Add(intro);

            foreach (var s in Scenarios.All)
            {
                var scenario = s;
                var missing = new List<string>();
                foreach (string id in s.Needs) if (!w.Connected(id)) missing.Add(id);
                string hint = s.Hint;
                if (w.ScenarioActive(s.Id)) hint += " · действует сейчас";
                else if (missing.Count > 0) hint += " · " + (missing.Count == 1 ? "не подключено: " : "не подключены: ") + Names(missing.ToArray());
                Row(s.Title, hint, new Switch(State.Scenarios.Contains(s.Id), delegate(bool on) { w.SetScenario(scenario.Id, on); }).Root);
            }
            Root.ScrollToVerticalOffset(offset);
        }
    }

    // Первый запуск: всё, что обычно ищут по разным окнам, — на одной странице
    class WelcomePage : Page
    {
        readonly HashSet<string> chosen = new HashSet<string>();
        bool autostart, installSelf, seeded;

        public WelcomePage(MainWindow w) : base(w) { }

        public void Render()
        {
            if (!seeded)
            {
                seeded = true;
                foreach (var c in w.Cards) if (c.App.HasExe && !c.App.Soon && !c.S.Installed && c.App.Recommended) chosen.Add(c.App.Id);
                installSelf = w.DemoMode || !SelfUpdate.IsInstalled;
                autostart = false;   // в автозапуск core.txt попадает, только если человек сам это включил
            }
            double offset = Root.VerticalOffset;
            stack.Children.Clear();

            var hello = Ui.T("Добро пожаловать", Tokens.Size.Display, Ui.Label, FontWeights.Bold);
            hello.Margin = new Thickness(4, 0, 0, 4);
            stack.Children.Add(hello);
            var about = Ui.T("core.txt ставит и обновляет приложения dev.txt, собирает их настройки в одном месте и связывает их между собой. Пара вопросов — и всё готово.", Tokens.Size.Body, Ui.Label2, FontWeights.Normal);
            about.TextWrapping = TextWrapping.Wrap; about.Margin = new Thickness(4, 0, 4, 6);
            stack.Children.Add(about);

            bool any = false;
            foreach (var c in w.Cards)
            {
                if (!c.App.HasExe || (c.App.Soon && !c.S.Installed)) continue;   // дополнения для Discord и Steam ставятся потом, с карточки
                var card = c;
                if (!any) { Title("Что поставить"); any = true; }
                if (c.S.Installed) Row(c.App.Name.ToLowerInvariant() + ".txt", c.App.Tagline + " · уже установлено", null);
                else Row(c.App.Name.ToLowerInvariant() + ".txt", c.App.Tagline, new Switch(chosen.Contains(c.App.Id), delegate(bool on) { if (on) chosen.Add(card.App.Id); else chosen.Remove(card.App.Id); }).Root);
            }

            Title("Вид");
            Row("Акцентный цвет", "Один цвет для всех приложений — или свой у каждого", Swatches());
            Row("Меньше движения", "Без пружин и плавных переходов во всех приложениях", new Switch(State.ReducedMotion, delegate(bool on) { w.SetTheme(null, on); }).Root);

            Title("Запуск");
            if (w.DemoMode || !SelfUpdate.IsInstalled)
                Row("Установить core.txt на этот компьютер", "Скопировать в папку программ и добавить в меню «Пуск». Иначе он работает из той папки, где лежит сейчас", new Switch(installSelf, delegate(bool on) { installSelf = on; }).Root);
            Row("Запускать core.txt вместе с Windows", "Тихо, в трее: приложения сразу видят друг друга и общие настройки", new Switch(autostart, delegate(bool on) { autostart = on; }).Root);

            var done = new Btn();
            done.Set("Готово", Tokens.Palette.Brand, true, delegate { w.FinishWelcome(new List<string>(chosen), installSelf, autostart); });
            done.Root.HorizontalAlignment = HorizontalAlignment.Right;
            done.Root.Margin = new Thickness(0, 14, 0, 0);
            done.Root.MinWidth = 140;
            stack.Children.Add(done.Root);
            Root.ScrollToVerticalOffset(offset);
        }
    }
}
