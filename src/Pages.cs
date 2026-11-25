// core.txt. Pages.cs — общие кирпичики страниц и две страницы: «Сценарии» и первый запуск.
// Сценарии про игры живут на странице «Игры» (SettingsPage.cs) — теми же строками, что здесь.
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
        protected Panel target;   // куда сейчас складываются заголовки и строки; null — прямо на страницу

        protected Page(MainWindow w)
        {
            this.w = w;
            Root = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(25, 0, 25, 0), Content = stack, Focusable = false, Visibility = Visibility.Collapsed };
        }

        // Появление страницы: строки поднимаются волной. Только по действию человека, не при каждом обновлении.
        public void Animate() { Motion.Stagger(stack); }

        protected void Title(string text)
        {
            var t = Ui.T(text, Tokens.Size.Heading, Ui.Label2, FontWeights.SemiBold);
            t.Margin = new Thickness(4, (target ?? stack).Children.Count == 0 ? 2 : 20, 0, 8);
            (target ?? stack).Children.Add(t);
        }

        // Строка, которую потом можно поправить на месте, не собирая страницу заново
        protected class RowRef
        {
            public Border Root;
            public StackPanel Text;
            public TextBlock Label, Hint;

            public void SetHint(string hint)
            {
                Motion.Swap(Hint, hint ?? "");
                Hint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        protected RowRef Row(string label, string hint, FrameworkElement control) { return Row(label, hint, control, null); }

        // below — что-то широкое под названием (например, плашки вариантов)
        protected RowRef Row(string label, string hint, FrameworkElement control, FrameworkElement below)
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            var name = Ui.T(label, Tokens.Size.Body, Ui.Label, FontWeights.Normal);
            name.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(name);
            var h = Ui.T(hint ?? "", Tokens.Size.Micro, Ui.Label3, FontWeights.Normal);
            h.TextWrapping = TextWrapping.Wrap; h.Margin = new Thickness(0, 1, 0, 0);
            if (string.IsNullOrEmpty(hint)) h.Visibility = Visibility.Collapsed;
            text.Children.Add(h);
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
            (target ?? stack).Children.Add(row);
            return new RowRef { Root = row, Text = text, Label = name, Hint = h };
        }

        // Заголовок группы одного приложения: значок, название и, если нужно, кнопка справа. Высота одна — с кнопкой
        // и без: строки под заголовками стоят на одинаковом расстоянии.
        protected void AppHeader(AppEntry app, string name, FrameworkElement right)
        {
            var panel = target ?? stack;
            var line = new DockPanel { LastChildFill = true, MinHeight = 32, Margin = new Thickness(4, panel.Children.Count == 0 ? 0 : 14, 0, 8) };
            if (right != null)
            {
                right.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(right, Dock.Right);
                line.Children.Add(right);
            }
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (app != null && app.Sprite != null)
            {
                var s = Ui.Sprite(app.Sprite, 16);
                s.Margin = new Thickness(0, 0, 8, 0); s.VerticalAlignment = VerticalAlignment.Center;
                title.Children.Add(s);
            }
            var t = Ui.T(name, Tokens.Size.Heading, Ui.Label2, FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            title.Children.Add(t);
            line.Children.Add(title);
            panel.Children.Add(line);
        }

        // Подпись внутри группы: чьи строки идут ниже — значок и название приложения, как подпись приложения в меню
        // значка в трее. Нужна там, где в одной группе строки разных приложений («Запуск и обновления»).
        protected FrameworkElement Owner(AppEntry app, string name)
        {
            var caption = Owner(app != null ? app.Sprite : null, name);
            if (app != null) Owned(app.Id, caption);
            return caption;
        }

        protected FrameworkElement Owner(string[] sprite, string name)
        {
            var panel = target ?? stack;
            // Сразу под заголовком группы — вплотную, между строками разных приложений — с отступом
            bool afterTitle = panel.Children.Count == 0 || panel.Children[panel.Children.Count - 1] is TextBlock;
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, afterTitle ? 0 : 6, 0, 6) };
            if (sprite != null)
            {
                var s = Ui.Sprite(sprite, 14);
                s.Margin = new Thickness(0, 0, 6, 0); s.VerticalAlignment = VerticalAlignment.Center;
                line.Children.Add(s);
            }
            var t = Ui.T(name, Tokens.Size.Caption, Ui.Label3, FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(t);
            panel.Children.Add(line);
            return line;
        }

        // На странице появилась подпись приложения appId — страница может запомнить её, чтобы потом прокрутить к ней
        protected virtual void Owned(string appId, FrameworkElement caption) { }

        // Строка сценария: переключатель и записка под ним — действует ли он и чего ему не хватает.
        // Возвращает, чем её обновить по нынешнему положению дел.
        protected Action ScenarioRow(Scenario s)
        {
            var scenario = s;
            var sw = new Switch(State.Scenarios.Contains(s.Id), delegate(bool on) { w.SetScenario(scenario.Id, on); });
            var row = Row(s.Title, s.Hint, sw.Root);
            var note = new Status();
            note.Root.Margin = new Thickness(0, 5, 0, 0); note.Root.HorizontalAlignment = HorizontalAlignment.Left; note.Root.Visibility = Visibility.Collapsed;
            row.Text.Children.Add(note.Root);
            Action paint = delegate
            {
                var missing = new List<string>();
                foreach (string id in scenario.Needs) if (!w.Connected(id)) missing.Add(Names(new string[] { id }));
                bool enabled = State.Scenarios.Contains(scenario.Id);
                Status.Kind kind;
                string text = ScenariosPage.Note(enabled, w.ScenarioActive(scenario.Id), missing.ToArray(), out kind);
                sw.Value = enabled;
                if (text != null) note.Set(text, kind);
                Motion.Show(note.Root, text != null);
            };
            paint();
            return paint;
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

        // Кружки выбора цвета, которые сейчас на странице, и цвет каждого ("#RRGGBB" или null — свой у каждого)
        readonly List<Border> rings = new List<Border>();
        readonly List<string> ringHex = new List<string>();

        // Кольцо — вокруг выбранного цвета. Двигается сразу, на месте.
        protected void PaintSwatches()
        {
            for (int i = 0; i < rings.Count; i++)
                rings[i].BorderBrush = string.Equals(ringHex[i], State.Accent, StringComparison.OrdinalIgnoreCase) ? Ui.Label : Brushes.Transparent;
        }

        // Выбор акцентного цвета: «свой у каждого» и пять общих
        protected FrameworkElement Swatches()
        {
            rings.Clear(); ringHex.Clear();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var colors = new Color?[] { null, Tokens.Palette.Brand, Tokens.Palette.Blue, Tokens.Palette.Purple, Tokens.Palette.Green, Tokens.Palette.Pink };
            var names = new string[] { "Свой у каждого приложения", "Оранжевый", "Синий", "Фиолетовый", "Зелёный", "Розовый" };
            for (int i = 0; i < colors.Length; i++)
            {
                Color? c = colors[i];
                string hex = c.HasValue ? "#" + c.Value.R.ToString("X2") + c.Value.G.ToString("X2") + c.Value.B.ToString("X2") : null;
                var inner = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = c.HasValue ? Ui.Solid(c.Value) : Ui.GlassStrong, BorderBrush = c.HasValue ? null : Ui.Rim, BorderThickness = new Thickness(c.HasValue ? 0 : 1) };
                var ring = new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent,
                    Background = Brushes.Transparent, Child = inner, Cursor = Cursors.Hand, ToolTip = names[i], Margin = new Thickness(4, 0, 0, 0)
                };
                inner.HorizontalAlignment = HorizontalAlignment.Center; inner.VerticalAlignment = VerticalAlignment.Center;
                Action pick = delegate { w.SetTheme(hex ?? "", null); PaintSwatches(); };
                ring.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; pick(); };
                Ui.Keyboard(ring, pick);
                rings.Add(ring); ringHex.Add(hex);
                row.Children.Add(ring);
            }
            PaintSwatches();
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
    // Сценарии про игры — на странице «Игры».
    class ScenariosPage : Page
    {
        public ScenariosPage(MainWindow w) : base(w) { }

        // Строки собираются один раз и дальше меняются на месте: переключатель доезжает, фокус с клавиатуры не теряется
        readonly List<Action> rows = new List<Action>();
        bool built;

        // Что сказать под сценарием: действует, не работает (включён, а приложений нет) или просто чего не хватает.
        // missing — названия неподключённых приложений; null — сказать нечего.
        public static string Note(bool enabled, bool active, string[] missing, out Status.Kind kind)
        {
            kind = Status.Kind.None;
            if (active) { kind = Status.Kind.Ok; return "действует сейчас"; }
            if (missing == null || missing.Length == 0) return null;
            string names = string.Join(", ", missing);
            if (enabled) { kind = Status.Kind.Warn; return "не работает: нет " + names; }
            return (missing.Length == 1 ? "не подключено: " : "не подключены: ") + names;
        }

        public void Render()
        {
            if (!built)
            {
                built = true;
                foreach (var s in Scenarios.All) if (!s.Game) rows.Add(ScenarioRow(s));
                // Сценарии про игры живут на «Играх» — отсюда туда ведёт одна строка
                var games = new Btn();
                games.Set("Открыть «Игры»", null, true, delegate { w.Go("games"); });
                Row("Сценарии про игры — в разделе «Игры»", "Рядом с тем, что каждое приложение делает, пока впереди игра", games.Root);
                return;
            }
            foreach (var paint in rows) paint();
        }
    }

    // Строка хода установки: значок, что делается, чем кончилось. Живёт всё время установки и меняется на месте,
    // поэтому полоса едет плавно, а «готово» отмечается хлопком значка и вспышкой кромки.
    class TaskLine
    {
        public readonly Border Root;
        readonly FrameworkElement icon;
        readonly Status status = new Status();
        readonly Progress bar = new Progress(Ui.Accent);
        Status.Kind last = Status.Kind.None;

        // Подсказка к строке: полный текст ошибки
        public string Tip { set { if (!Equals(Root.ToolTip, value)) Root.ToolTip = value; } }

        public TaskLine(string label, string[] sprite)
        {
            icon = Ui.Sprite(sprite, 20);
            icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 10, 0);
            status.Root.VerticalAlignment = VerticalAlignment.Center;
            var name = Ui.T(label, Tokens.Size.Body, Ui.Label, FontWeights.Normal);
            name.VerticalAlignment = VerticalAlignment.Center;
            var line = new DockPanel { LastChildFill = true, MinHeight = 26 };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(status.Root, Dock.Right);
            line.Children.Add(icon);
            line.Children.Add(status.Root);
            line.Children.Add(name);
            bar.Root.Margin = new Thickness(0, 8, 0, 0);
            bar.Root.Visibility = Visibility.Collapsed;
            Root = Ui.Row(new StackPanel { Children = { line, bar.Root } });
            Root.Margin = new Thickness(0, 0, 0, 8);
        }

        // progress: 0..1 — скачивание; меньше нуля — идёт работа без счёта; больше единицы — полоса не нужна
        public void Set(string state, Status.Kind kind, double progress, bool working)
        {
            status.Set(state, kind);
            status.Busy = working;
            Motion.Show(bar.Root, progress <= 1);
            bar.Busy = progress < 0;
            if (progress >= 0 && progress <= 1) bar.Value = progress;
            if (kind == Status.Kind.Ok && last != Status.Kind.Ok) { Motion.Pop(icon); Motion.Glow(Root, Tokens.Palette.Green); }
            if (kind == Status.Kind.Bad && last != Status.Kind.Bad) Motion.Shake(Root);
            last = kind;
        }
    }

    // Установщик — то, что человек видит, запустив скачанный Core.exe в первый раз. Три шага в одном окне:
    // что поставить, как выглядеть и запускаться, и ход установки. Слева — кто мы, справа — вопросы.
    // Окно собирается один раз и дальше меняется на месте: шаги въезжают сбоку, строки хода живут до конца.
    class WelcomePage : Page
    {
        // Значок core.txt — тот же рисунок, что в kit.txt (icons/sprites.json, «core»)
        static readonly string[] CoreSprite = { ".....#....", "....##....", ".########.", "##########", ".########.", "..######..", "...##E#...", "...##E#...", "..######..", ".########.", "##########", ".########." };
        static readonly string[] Heads = { "Что поставить", "Как выглядеть и запускаться", "Установка" };

        readonly HashSet<string> chosen = new HashSet<string>();
        readonly List<string> tasks = new List<string>();   // что ставится на третьем шаге
        readonly Dictionary<string, TaskLine> lines = new Dictionary<string, TaskLine>();
        bool autostart, gather = true, movedSelf, gathering;
        string selfError, selfTip;   // core.txt не смог встать в общую папку: это видно в его строке на третьем шаге
        int step, shown = -1;
        string drawn;          // по чему нарисованы строки шага: если не изменилось — не перерисовываем, и переключатели доигрывают своё движение
        bool? working;         // идёт ли установка: смена — повод для движения

        FrameworkElement mark;
        readonly Border[] segs = new Border[3];
        TextBlock count, head, tip;
        StackPanel rows;
        readonly Btn back = new Btn(), next = new Btn(), all = new Btn();

        public WelcomePage(MainWindow w) : base(w) { }

        // Третий шаг: показать ход установки этих приложений (и переноса, если просили)
        public void ShowProgress(List<string> installing, bool selfInstalled, bool gatherAsked)
        {
            tasks.Clear();
            tasks.AddRange(installing);
            movedSelf = selfInstalled;
            gathering = gatherAsked;
            step = 2;
        }

        public void SetStep(int n) { step = n; }

        // Сам core.txt не установился (или не переехал): показать это в его строке, а не прятать
        public void SelfFailed(string error, string tip) { selfError = error; selfTip = tip; }

        bool NeedsHome { get { return w.DemoMode || !SelfUpdate.IsInstalled; } }

        void Build()
        {
            foreach (var c in w.Cards) if (c.App.HasExe && !c.App.Soon && !c.S.Installed && c.App.Recommended) chosen.Add(c.App.Id);
            autostart = false;   // в автозапуск core.txt попадает, только если человек сам это включил или выбрал «Установить всё»

            mark = Ui.Sprite(CoreSprite, 132);
            mark.HorizontalAlignment = HorizontalAlignment.Left;
            var name = Ui.Brand("core", 40, FontWeights.Bold);
            name.Margin = new Thickness(0, 18, 0, 6);
            var line = Ui.T("Все приложения dev.txt в одном окне: поставить, обновить, настроить.", Tokens.Size.Heading, Ui.Label2, FontWeights.Normal);
            line.TextWrapping = TextWrapping.Wrap;
            var small = Ui.T("Одна папка, одни настройки, один стиль. Ничего не уходит с этого компьютера, кроме запросов к GitHub за новыми версиями.", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
            small.TextWrapping = TextWrapping.Wrap; small.Margin = new Thickness(0, 10, 0, 0);
            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 36, 40), Children = { mark, name, line, small } };

            var bar = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            for (int i = 0; i < segs.Length; i++)
            {
                bar.ColumnDefinitions.Add(new ColumnDefinition());
                segs[i] = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.AccentBrush, Visibility = Visibility.Hidden };
                var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), Background = Ui.Fill2, Child = segs[i] };
                Grid.SetColumn(track, i);
                bar.Children.Add(track);
            }
            count = Ui.T("", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
            head = Ui.T("", Tokens.Size.Display, Ui.Label, FontWeights.Bold);
            head.Margin = new Thickness(0, 2, 0, 14);
            rows = new StackPanel();
            tip = Ui.T("", Tokens.Size.Micro, Ui.Label3, FontWeights.Normal);
            tip.TextWrapping = TextWrapping.Wrap; tip.VerticalAlignment = VerticalAlignment.Center; tip.Margin = new Thickness(4, 0, 16, 0);

            next.Root.MinWidth = 140;
            back.Root.Margin = new Thickness(0, 0, 8, 0);
            all.Root.Margin = new Thickness(0, 0, 8, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { back.Root, all.Root, next.Root } };
            var foot = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(buttons, Dock.Right);
            foot.Children.Add(buttons);
            foot.Children.Add(tip);

            var right = new StackPanel { Children = { bar, count, head, rows, foot } };
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(right, 1);
            grid.Children.Add(left);
            grid.Children.Add(right);
            stack.Children.Add(grid);
        }

        void AppRow(Card c)
        {
            var card = c;
            FrameworkElement icon = c.App.Sprite != null ? Ui.Sprite(c.App.Sprite, 26) : (FrameworkElement)Ui.T(c.App.Name.Substring(0, 1), Tokens.Size.Title, Ui.Label, FontWeights.Bold);
            icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
            var tile = Ui.Tile(icon);
            tile.Width = 40; tile.Height = 40; tile.VerticalAlignment = VerticalAlignment.Center;
            var name = Ui.Brand(c.App.Name, Tokens.Size.Heading, FontWeights.SemiBold);
            var tag = Ui.T(c.S.Installed ? c.App.Tagline + " · уже установлено" : c.App.Tagline, Tokens.Size.Micro, Ui.Label3, FontWeights.Normal);
            tag.TextWrapping = TextWrapping.Wrap;
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 14, 0), Children = { name, tag } };
            var line = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(tile, Dock.Left);
            line.Children.Add(tile);
            if (!c.S.Installed)
            {
                // Включили — значок приложения подпрыгивает: видно, что именно выбрано
                var sw = new Switch(chosen.Contains(c.App.Id), delegate(bool on) { if (on) { chosen.Add(card.App.Id); Motion.Pop(icon); } else chosen.Remove(card.App.Id); }).Root;
                sw.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(sw, Dock.Right);
                line.Children.Add(sw);
            }
            line.Children.Add(text);
            var row = Ui.Row(line);
            row.Margin = new Thickness(0, 0, 0, 8);
            rows.Children.Add(row);
        }

        List<string> Everything()
        {
            var ids = new List<string>();
            foreach (var c in w.Cards) if (c.App.HasExe && !c.App.Soon && !c.S.Installed) ids.Add(c.App.Id);
            return ids;
        }

        // Строки шага. Собираются заново только когда шаг сменился или изменилось то, от чего они зависят.
        void BuildStep()
        {
            rows.Children.Clear();
            lines.Clear();
            target = rows;
            if (step == 0)
            {
                foreach (var c in w.Cards) if (c.App.HasExe && !(c.App.Soon && !c.S.Installed)) AppRow(c);
            }
            else if (step == 1)
            {
                Row("Акцентный цвет", "Один цвет для всех приложений — или свой у каждого", Swatches());
                Row("Меньше движения", "Без пружин и плавных переходов во всех приложениях", new Switch(State.ReducedMotion, delegate(bool on) { w.SetTheme(null, on); }).Root);
                Row("Запускать core.txt вместе с Windows", "Тихо, в трее: приложения сразу видят друг друга и общие настройки", new Switch(autostart, delegate(bool on) { autostart = on; }).Root);
                if (w.DemoMode || w.OutsideCount() > (SelfUpdate.IsInstalled ? 0 : 1))
                    Row("Собрать приложения в одну папку", "Те, что уже стоят, переедут в общую папку dev.txt и будут перезапущены. aura.txt остаётся там, куда её поставил установщик", new Switch(gather, delegate(bool on) { gather = on; }).Root);
            }
            else
            {
                if (movedSelf || selfError != null || w.DemoMode || tasks.Count == 0 && !gathering) Line("core", "core.txt", CoreSprite);
                foreach (string id in tasks)
                {
                    Card c = w.Cards.Find(delegate(Card x) { return x.App.Id == id; });
                    if (c != null) Line(id, c.App.Name.ToLowerInvariant() + ".txt", c.App.Sprite ?? CoreSprite);
                }
                if (gathering) Line("gather", "Приложения в одной папке", CoreSprite);
            }
            target = null;
        }

        void Line(string id, string label, string[] sprite)
        {
            var l = new TaskLine(label, sprite);
            lines[id] = l;
            rows.Children.Add(l.Root);
        }

        // Ход установки: каждая строка приводится к тому, что сейчас происходит. Возвращает «ещё идёт».
        bool UpdateTasks(out bool failed)
        {
            bool busy = false;
            failed = false;
            TaskLine l;
            if (lines.TryGetValue("core", out l))
            {
                if (selfError != null) { failed = true; l.Set(selfError, Status.Kind.Bad, 2, false); l.Tip = selfTip; }
                else { l.Set(movedSelf || w.DemoMode ? "в папке dev.txt, ярлык в меню «Пуск»" : "готов к работе", Status.Kind.Ok, 2, false); l.Tip = null; }
            }
            foreach (string id in tasks)
            {
                Card c = w.Cards.Find(delegate(Card x) { return x.App.Id == id; });
                if (c == null || !lines.TryGetValue(id, out l)) continue;
                l.Tip = c.S.Phase == Phase.Idle && c.S.Error != null ? (c.S.ErrorDetail != null ? c.S.Error + "\n" + c.S.ErrorDetail : c.S.Error) : null;
                if (c.S.Phase == Phase.Downloading) { busy = true; l.Set("скачивается — " + (c.S.Total > 0 ? Releases.Megabytes(c.S.Done, c.S.Total) : (int)Math.Round(c.S.Progress * 100) + "%"), Status.Kind.Warn, c.S.Progress, true); }
                else if (c.S.Phase != Phase.Idle) { busy = true; l.Set("устанавливается…", Status.Kind.Warn, -1, true); }
                else if (c.S.Error != null) { failed = true; l.Set(c.S.Error, Status.Kind.Bad, 2, false); }
                else if (c.S.Installed) l.Set("установлено", Status.Kind.Ok, 2, false);
                else { busy = true; l.Set("ждёт очереди", Status.Kind.None, 2, false); }
            }
            if (lines.TryGetValue("gather", out l))
            {
                int outside = w.OutsideCount();
                bool moving = outside > 0 && w.GatherBusy;
                busy = busy || moving;
                if (outside == 0) l.Set("собраны", Status.Kind.Ok, 2, false);
                else if (moving) l.Set("переносятся…", Status.Kind.Warn, -1, true);
                else l.Set("осталось перенести: " + outside, Status.Kind.Warn, 2, false);
            }
            return busy;
        }

        public void Render()
        {
            if (mark == null) Build();
            bool stepChanged = shown != step;
            string sig = step + "|" + string.Join(",", w.Cards.ConvertAll(delegate(Card c) { return c.App.Id + (c.S.Installed ? "1" : "0"); }).ToArray());
            if (step == 2) sig = "2|" + string.Join(",", tasks.ToArray()) + "|" + movedSelf + gathering + (selfError != null);
            if (sig != drawn) { BuildStep(); drawn = sig; }
            if (step == 1) PaintSwatches();   // цвет выбрали — кольцо переезжает на месте, строки не пересобираются

            for (int i = 0; i < segs.Length; i++)
            {
                bool on = i <= step, was = segs[i].Visibility == Visibility.Visible;
                segs[i].Visibility = on ? Visibility.Visible : Visibility.Hidden;
                if (on && !was) Motion.FillIn(segs[i]);
            }
            Motion.Swap(count, "Шаг " + (step + 1) + " из " + Heads.Length);

            bool busy = false, failed = false;
            string title = Heads[step];
            if (step == 2)
            {
                busy = UpdateTasks(out failed);
                if (!busy) title = failed ? "Почти готово" : "Готово";
            }
            Motion.Swap(head, title);

            // Значок слева дышит, пока идёт установка, и хлопает, когда всё встало
            if (step == 2 && working != busy)
            {
                if (busy) Motion.Breathe(mark, true);
                else if (working == true) { Motion.Breathe(mark, false); Motion.Pop(mark); }   // всё встало — значок перестаёт «дышать»
                working = busy;
            }

            all.Hide();
            if (step == 0)
            {
                back.Hide();
                List<string> every = Everything();
                if (every.Count > 0 || NeedsHome)
                    all.Set("Установить всё", null, true, delegate { w.FinishWelcome(Everything(), NeedsHome, true, true); });
                // Главная кнопка ведёт дальше с тем, что отмечено переключателями; «Установить всё» — запасной короткий путь
                next.Set("Далее", Ui.Accent, true, delegate { step = 1; w.RenderWelcome(); });
                Motion.Swap(tip, "«Далее» — поставить отмеченное и выбрать вид. «Установить всё» — все приложения сразу: одна папка, запуск вместе с Windows.");
            }
            else if (step == 1)
            {
                back.Set("Назад", null, true, delegate { step = 0; w.RenderWelcome(); });
                next.Set(NeedsHome ? "Установить" : "Готово", Ui.Accent, true, delegate { w.FinishWelcome(new List<string>(chosen), NeedsHome, autostart, gather); });
                Motion.Swap(tip, NeedsHome ? "core.txt встанет в папку программ dev.txt в вашем профиле и появится в меню «Пуск». Права администратора не нужны." : "");
            }
            else
            {
                back.Hide();
                next.Set(busy ? "Устанавливается…" : "Открыть core.txt", Ui.Accent, !busy, delegate { w.LeaveWelcome(); });
                Motion.Swap(tip, selfError != null && !busy ? "core.txt остался там, откуда запущен. Перенести его можно позже: «Общие» → «Собрать»." : failed ? "Что не установилось, можно поставить ещё раз с карточки приложения." : busy ? "Окно можно свернуть — установка продолжится." : "Тему для Steam и подключение Discord можно добавить с карточек в core.txt.");
            }

            if (stepChanged)
            {
                // Шаг сменился: строки въезжают с той стороны, куда идём, и поднимаются волной
                if (shown >= 0) Motion.SlideIn(rows, step > shown ? 36 : -36);
                else Motion.GrowFrom(mark, new Point(0.5, 0.5));
                Motion.Stagger(rows);
                Root.ScrollToVerticalOffset(0);
                shown = step;
            }
        }
    }
}
