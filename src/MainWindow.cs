// core.txt. MainWindow.cs — окно: шапка с вкладками, страницы (приложения, сценарии, настройки, первый запуск),
// строка состояния. Здесь же окно держит хаб, сценарии и значок в трее — один на всю семью dev.txt: core.txt
// сообщает всем core.family («Не беспокоить», игра на весь экран, чей значок в трее) и собирает в своё меню
// быстрые действия приложений (kit.txt, hub/PROTOCOL.md, раздел «Семья»; расчёты — в CoreFamily.cs).
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DevTxt.Core
{
    class MainWindow : Window
    {
        const double W = 1120, H = 720;
        const int PApps = 0, PScenarios = 1, PSettings = 2, PWelcome = 3;
        // Значок core.txt — тот же рисунок, что в kit.txt (icons/sprites.json, «core»)
        internal static readonly string[] CoreSprite = { ".....#....", "....##....", ".########.", "##########", ".########.", "..######..", "...##E#...", "...##E#...", "..######..", ".########.", "##########", ".########." };

        internal readonly bool DemoMode;
        internal readonly List<Card> Cards = new List<Card>();
        internal readonly Dictionary<string, object> States = new Dictionary<string, object>();   // что приложения сообщили хабу
        readonly HashSet<string> connectedApps = new HashSet<string>();
        readonly UniformGrid grid = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
        readonly TextBlock footer = Ui.T("", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
        readonly Btn checkBtn = new Btn(), allBtn = new Btn();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly SettingsPage settings;
        readonly ScenariosPage scenariosPage;
        readonly WelcomePage welcome;
        readonly Scenarios scenarios;
        ScrollViewer appsView;
        FrameworkElement bottom;
        NavShell shell;                                   // общий каркас окна dev.txt
        readonly List<string> navKeys = new List<string>(); // что открывает каждый раздел слева
        string navSig;
        int page;
        bool checking, quitting, updatingSelf;
        string notice;      // последняя ошибка — показывается в строке состояния
        string noticeTip;   // её исходный текст — в подсказке
        readonly HashSet<string> userCommands = new HashSet<string>();   // команды хабу, которые отправил человек, а не сценарий
        readonly DispatcherTimer launchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };   // ждём, пока открытое приложение появится

        Hub hub;
        HubServer server;
        HubClient local;
        int commandSeq;
        System.Windows.Forms.NotifyIcon tray;

        // Семья: что core.txt сейчас сообщает всем (core.family) и что впереди на экране
        FamilyState family = new FamilyState();
        readonly FamilyOut familyOut = new FamilyOut();   // одно и то же core.family второй раз не шлём
        bool gameNow;           // впереди игра или другое окно на весь экран
        string gameAppNow = "";
        readonly DispatcherTimer familyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // О чём было последнее уведомление: щелчок по нему (на острове или в Windows) открывает эту страницу
        string noticePage;
        DateTime noticeAt = DateTime.MinValue;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int pid);

        // Человек только что щёлкнул в core.txt — значит, Windows разрешает ему выводить окна вперёд. Этим правом
        // делимся с тем, кого просим показать окно: иначе окно другого приложения откроется позади остальных
        // или только мигнёт на панели задач. -1 — любому процессу, до следующего действия человека.
        internal static void LetForeground() { try { AllowSetForegroundWindow(-1); } catch { } }

        // startSection — раздел настроек, который открыть сразу («general», «games», «keys», «replace», «app:<id>»)
        public MainWindow(bool demo, int startPage, string startSection, bool afterInstall)
        {
            DemoMode = demo;
            page = !demo && !State.Onboarded ? PWelcome : startPage;
            Title = "core.txt";
            Width = W; Height = H;
            MinWidth = 1000; MinHeight = 600;
            // Родное окно Windows 11: системная рамка и тень, Mica, тема Windows, размер меняется, окно прилипает к краям
            Ui.ThemeFollows = !demo;
            Ui.Native(this, true);
            if (demo && Environment.GetEnvironmentVariable("CORE_DEMO_LIGHT") != null) Ui.ApplyTheme(true);   // снимок в светлой теме
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            if (!demo) PlaceWindow();
            ApplyAccent();
            Background = Ui.Bg;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            Ui.ForceReducedMotion = State.ReducedMotion;
            settings = new SettingsPage(this);
            scenariosPage = new ScenariosPage(this);
            welcome = new WelcomePage(this);
            scenarios = new Scenarios(
                delegate(string topic) { object data; return States.TryGetValue(topic, out data) ? data : null; },
                delegate(string app, string key, object value) { SendCommand(app, "settings.set", Hub.Msg("key", key, "value", value), false); },
                delegate(string id) { return State.Scenarios.Contains(id); });
            Content = Build();
            BuildCards();
            if (startSection != null) settings.Section = startSection;
            if (!demo && page == PApps) RestoreSection();
            ShowPage(page);
            // Esc убирает вопрос об удалении с карточки
            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key != Key.Escape || page != PApps) return;
                foreach (var c in Cards) if (c.CancelRemove()) e.Handled = true;
            };

            if (demo) return;

            StartHub();
            StartTray();
            // core.family — сразу, как только есть хаб и значок: приложения при запуске ждут его, чтобы не мигать своими значками
            PublishFamily();
            familyTimer.Tick += delegate { CheckFamily(); };
            familyTimer.Start();
            timer.Tick += delegate { RefreshLocal(); TaskbarGuard.Check(); };   // док пропал, а панель задач спрятана — возвращаем
            timer.Start();
            launchTimer.Tick += delegate { WatchLaunches(); };
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                KeepBounds();
                if (!quitting && State.Tray && State.Onboarded)
                {
                    // Окно закрыли — core.txt остаётся в трее и держит хаб. В первый раз говорим об этом
                    e.Cancel = true;
                    Hide();
                    // Сказали — больше не повторяем. Промолчали («Не беспокоить», игра впереди) — скажем в следующий раз
                    if (!State.TrayHint && tray != null && Notify("core.txt остался в трее", "Приложения dev.txt по-прежнему на связи. Выход — в меню значка", null))
                        State.TrayHint = true;
                    State.Save();
                    return;
                }
                State.Save();
                familyTimer.Stop();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (server != null) server.Stop();   // связь пропала — приложения сразу показывают свои значки
            };
            if (State.Onboarded)
            {
                // Нас только что поставил установщик: показываем третий шаг — что именно сейчас ставится
                bool gatherNow = State.GatherPending;
                if (afterInstall && (State.Pending.Count > 0 || gatherNow))
                {
                    welcome.ShowProgress(new List<string>(State.Pending), true, gatherNow);
                    ShowPage(PWelcome);
                }
                if (gatherNow) { State.GatherPending = false; State.Save(); GatherAll(); }
                StartPending();
                if (State.AutoCheck && DateTime.UtcNow - State.Checked > TimeSpan.FromHours(1)) CheckAll();
            }
        }

        // ───────── Разметка ─────────
        FrameworkElement Build()
        {
            // Общий каркас dev.txt: слева значок, имя и разделы, справа заголовок, страница и нижняя строка
            shell = new NavShell(this, CoreSprite, "core", "версия " + Program.VersionText, Close);
            shell.Changed = delegate(int i) { if (i >= 0 && i < navKeys.Count) Go(navKeys[i]); };

            // Низ: строка состояния и кнопки
            checkBtn.Root.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(checkBtn.Root, Dock.Right);
            shell.Bottom.Children.Add(checkBtn.Root);
            allBtn.Root.VerticalAlignment = VerticalAlignment.Center; allBtn.Root.Margin = new Thickness(0, 0, 8, 0);
            allBtn.Hide();
            DockPanel.SetDock(allBtn.Root, Dock.Right);
            shell.Bottom.Children.Add(allBtn.Root);
            footer.VerticalAlignment = VerticalAlignment.Center;
            footer.TextWrapping = TextWrapping.Wrap; footer.Margin = new Thickness(0, 0, 16, 0);
            shell.Bottom.Children.Add(footer);
            bottom = shell.Bottom;

            appsView = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(25, 0, 25, 0), Content = grid, Focusable = false };
            // Сколько карточек в ряд — по ширине окна
            appsView.SizeChanged += delegate { int n = Columns(appsView.ActualWidth); if (grid.Columns != n) grid.Columns = n; };
            shell.Body.Children.Add(appsView);
            shell.Body.Children.Add(scenariosPage.Root);
            shell.Body.Children.Add(settings.Root);
            shell.Body.Children.Add(welcome.Root);
            return shell.Root;
        }

        FrameworkElement WindowButton(string glyph, string tip, bool danger, Action click)
        {
            var t = new TextBlock { Text = glyph, FontFamily = Ui.IconFont, FontSize = 10, Foreground = Ui.Label2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 36, Height = 26, CornerRadius = new CornerRadius(Tokens.Radius.Tip), Background = Brushes.Transparent, Child = t, ToolTip = tip };
            b.MouseEnter += delegate { b.Background = danger ? Ui.Solid(Tokens.Palette.Red) : Ui.Fill2; t.Foreground = Ui.Label; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; t.Foreground = Ui.Label2; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        // Карточек в ряд: в узком окне — одна, в обычном — две, в широком — три. width — ширина области карточек.
        // Карточка не бывает у́же примерно 360 точек: иначе подпись и кнопки в неё не помещаются.
        internal static int Columns(double width) { return width < 720 ? 1 : width < 1240 ? 2 : 3; }

        // Общий цвет экосистемы красит и сам core.txt; «свой у каждого» — оранжевый dev.txt
        void ApplyAccent() { Ui.SetAccent(AccentOf(State.Accent)); }

        internal static Color AccentOf(string hex)
        {
            try { if (!string.IsNullOrEmpty(hex)) return (Color)ColorConverter.ConvertFromString(hex); } catch { }
            return Tokens.Palette.Brand;
        }

        // ───────── Где было окно и что в нём было открыто ─────────
        void PlaceWindow()
        {
            if (State.WinW <= 0 || State.WinH <= 0 || double.IsNaN(State.WinX) || double.IsNaN(State.WinY)) return;
            try
            {
                // Рабочая область экрана, на котором окно было: в точках WPF, а не в пикселях
                double k = 1;
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) k = Math.Max(0.5, g.DpiX / 96.0);
                var px = new System.Drawing.Rectangle((int)(State.WinX * k), (int)(State.WinY * k), (int)(State.WinW * k), (int)(State.WinH * k));
                var area = System.Windows.Forms.Screen.FromRectangle(px).WorkingArea;
                double x = State.WinX, y = State.WinY, w = State.WinW, h = State.WinH;
                State.Fit(ref x, ref y, ref w, ref h, area.X / k, area.Y / k, area.Width / k, area.Height / k, MinWidth, MinHeight);
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x; Top = y; Width = w; Height = h;
                if (State.WinMax) WindowState = WindowState.Maximized;
            }
            catch { }
        }

        void KeepBounds()
        {
            if (DemoMode) return;
            Rect r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (r.IsEmpty || double.IsNaN(r.X) || double.IsNaN(r.Y) || r.Width < 100 || r.Height < 100) return;   // окно ни разу не показывали
            State.WinX = r.X; State.WinY = r.Y; State.WinW = r.Width; State.WinH = r.Height;
            State.WinMax = WindowState == WindowState.Maximized;
        }

        // Раздел, открытый в прошлый раз. Раздела приложения может уже не быть — тогда остаёмся на «Приложениях».
        void RestoreSection()
        {
            string key = State.Section;
            if (!State.Onboarded || key == null || key == "apps") return;
            if (key == "scenarios") { page = PScenarios; return; }
            if (key.StartsWith("app:"))
            {
                Card c = Find(key.Substring(4));
                if (c == null || !HasSettings(c)) return;
            }
            settings.Section = key;
            page = PSettings;
        }

        void ShowPage(int i)
        {
            page = i;
            bool first = i == PWelcome;   // на первом запуске нет ни разделов, ни нижней строки — только вопросы
            shell.Chrome(!first);
            appsView.Visibility = i == PApps ? Visibility.Visible : Visibility.Collapsed;
            scenariosPage.Root.Visibility = i == PScenarios ? Visibility.Visible : Visibility.Collapsed;
            settings.Root.Visibility = i == PSettings ? Visibility.Visible : Visibility.Collapsed;
            welcome.Root.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
            RenderAll();
        }

        // Раздел слева: страница, её заголовок и вход снизу, как в «Параметрах» Windows 11
        internal void Go(string key)
        {
            if (key == "apps") ShowPage(PApps);
            else if (key == "scenarios") ShowPage(PScenarios);
            else { settings.Section = key; ShowPage(PSettings); }
            if (!DemoMode && State.Section != key) { State.Section = key; State.Save(); }
            FrameworkElement now = page == PApps ? appsView : page == PScenarios ? scenariosPage.Root : (FrameworkElement)settings.Root;
            Motion.Enter(now);
            Animate();
        }

        string CurrentKey { get { return page == PApps ? "apps" : page == PScenarios ? "scenarios" : page == PSettings ? settings.Section : ""; } }

        bool HasSettings(Card c)
        {
            object d;
            return States.TryGetValue(c.App.Id + ".settings", out d) && d != null || c.App.HasExe && c.S.Installed;
        }

        // Разделы слева. Собираются заново, только когда меняется их список (появилось или пропало приложение).
        void BuildNav()
        {
            var keys = new List<string> { "apps", "scenarios", "general", "games", "keys", "replace" };
            foreach (var c in Cards) if (HasSettings(c)) keys.Add("app:" + c.App.Id);
            string sig = string.Join(",", keys.ToArray());
            if (sig != navSig)
            {
                navSig = sig;
                navKeys.Clear();
                shell.Clear();
                Action<string, string, FrameworkElement> add = delegate(string key, string label, FrameworkElement icon) { shell.Add(label, icon); navKeys.Add(key); };
                add("apps", "Приложения", Ui.Glyph("\uE71D"));
                add("scenarios", "Сценарии", Ui.Glyph("\uE945"));
                shell.Header("Настройки");
                add("general", "Общие", Ui.Glyph("\uE713"));
                add("games", "Игры", Ui.Glyph("\uE7FC"));
                add("keys", "Клавиши", Ui.Glyph("\uE765"));
                add("replace", "Замены Windows", Ui.Glyph("\uE770"));
                bool any = false;
                foreach (var c in Cards)
                {
                    if (!HasSettings(c)) continue;
                    if (!any) { shell.Header("Приложения dev.txt"); any = true; }
                    add("app:" + c.App.Id, c.App.Name.ToLowerInvariant() + ".txt", c.App.Sprite != null ? Ui.Sprite(c.App.Sprite, 20) : (FrameworkElement)Ui.Glyph("\uE74C"));
                }
            }
            shell.Selected = navKeys.IndexOf(CurrentKey);
            // Заголовок раздела
            string key2 = CurrentKey;
            if (page == PWelcome) shell.Title("", "");
            else if (key2 == "apps") shell.Title("Приложения", "Установить, обновить и открыть всё, что есть в dev.txt");
            else if (key2 == "scenarios") shell.Title("Сценарии", "Приложения помогают друг другу сами — включите то, что нужно");
            else if (key2 == "general") shell.Title("Общие", "Вид, «Не беспокоить», запуск и обновления — для всех приложений сразу");
            else if (key2 == "games") shell.Title("Игры", "Что делают приложения, пока впереди игра или другое окно на весь экран");
            else if (key2 == "keys") shell.Title("Клавиши", "Горячие клавиши всех приложений dev.txt и что с чем совпало");
            else if (key2 == "replace") shell.Title("Замены Windows", "Что из Windows заменяют приложения dev.txt. Выключите — и Windows вернётся как была");
            else if (key2.StartsWith("app:"))
            {
                Card c = Find(key2.Substring(4));
                shell.Title(c != null ? c.App.Name.ToLowerInvariant() + ".txt" : "", c != null ? c.App.Tagline : "");
            }
        }

        // Волна при переходе на страницу
        void Animate()
        {
            if (page == PApps) Motion.Stagger(grid);
            else if (page == PScenarios) scenariosPage.Animate();
            else if (page == PSettings) settings.Animate();
        }

        void BuildCards()
        {
            Cards.Clear();
            grid.Children.Clear();
            foreach (var app in Catalog.Apps)
            {
                var c = new Card(app);
                c.Remove = RemoveApp;
                c.Changed = RenderAll;
                Cards.Add(c);
                grid.Children.Add(c.Root);
            }
            if (DemoMode) Demo(); else foreach (var c in Cards) { AppOps.Refresh(c.App, c.S); InstalledApps.Sync(c.App, c.S); }
        }

        // В каком порядке встанут карточки после обновления каталога: как в каталоге; приложение, которого там больше нет,
        // остаётся в конце, пока с ним идёт работа (установка, удаление) — иначе она потерялась бы.
        internal static List<string> MergeOrder(IList<string> catalog, IList<string> shown, Predicate<string> busy)
        {
            var order = new List<string>(catalog);
            foreach (string id in shown) if (!order.Contains(id) && busy(id)) order.Add(id);
            return order;
        }

        // Каталог обновился. Карточки не строятся заново: занятая остаётся той же самой (её ждёт идущая установка),
        // у свободной меняется только описание из каталога, а всё, что известно о программе, сохраняется.
        void MergeCards()
        {
            var old = new List<Card>(Cards);
            Func<string, Card> was = delegate(string id) { return old.Find(delegate(Card c) { return c.App.Id == id; }); };
            var order = MergeOrder(Catalog.Apps.ConvertAll(delegate(AppEntry a) { return a.Id; }), old.ConvertAll(delegate(Card c) { return c.App.Id; }),
                delegate(string id) { Card c = was(id); return c != null && c.S.Phase != Phase.Idle; });
            Cards.Clear();
            grid.Children.Clear();
            foreach (string id in order)
            {
                Card before = was(id), c = before;
                AppEntry app = Catalog.Apps.Find(delegate(AppEntry a) { return a.Id == id; });
                if (app != null && (before == null || before.S.Phase == Phase.Idle))
                {
                    c = new Card(app, before != null ? before.S : null);
                    c.Remove = RemoveApp;
                    c.Changed = RenderAll;
                    AppOps.Refresh(c.App, c.S);
                    InstalledApps.Sync(c.App, c.S);
                }
                Cards.Add(c);
                grid.Children.Add(c.Root);
            }
            navSig = null;   // названия и значки разделов могли измениться
        }

        Card Find(string id) { return Cards.Find(delegate(Card c) { return c.App.Id == id; }); }

        bool AnyBusy { get { return GatherBusy || Cards.Exists(delegate(Card c) { return c.S.Phase != Phase.Idle; }); } }

        // Ошибка в строке состояния: короткая фраза, а исходный текст — в подсказке
        void Say(string text, string tip) { notice = text; noticeTip = text == null ? null : tip; }
        void Say(string prefix, Exception e) { Say(prefix == null ? Errors.Short(e) : prefix + ": " + Errors.Lower(Errors.Short(e)), Errors.Detail(e)); }

        void RenderAll()
        {
            if (shell != null) BuildNav();
            foreach (var c in Cards) c.Render(Install, Open, Find, Connect, Connected);
            if (page == PSettings) settings.Render();
            else if (page == PScenarios) scenariosPage.Render();
            else if (page == PWelcome) welcome.Render();

            int updates = Cards.FindAll(delegate(Card c) { return c.S.UpdateAvailable; }).Count;
            string when = State.Checked == DateTime.MinValue ? "Обновления ещё не проверялись" : "Проверено " + State.Checked.ToLocalTime().ToString("d MMMM, HH:mm");
            string line;
            if (notice != null) line = notice;
            else if (updatingSelf) line = "Обновляю core.txt…";
            else if (checking) line = "Проверяю обновления…";
            else if (page == PSettings && settings.Section == "keys" && KeyRows().Count > 0)
            {
                // Итог страницы «Клавиши»: сколько сочетаний и есть ли совпадения. Пустая страница говорит о связи, как остальные
                var rows = KeyRows();
                int clashes = CoreFamily.FindClashes(rows);
                line = "Сочетаний: " + rows.Count + (clashes > 0 ? " · совпадений: " + clashes + " — поменяйте сочетание в одном из приложений" : " · совпадений нет");
            }
            else if (page == PSettings) line = server != null && server.Error != null ? "Связь между приложениями не работает: " + server.Error : "Связь между приложениями работает · подключено: " + connectedApps.Count;
            else if (page == PScenarios) line = "Сценарии действуют, пока core.txt запущен";
            else line = (updates > 0 ? "Обновлений: " + updates + " · " : "") + when;
            Motion.Swap(footer, line + " · версия " + Program.VersionText + " · dev.txt™");
            int missing = Cards.FindAll(Installable).Count;
            if (page == PApps && missing > 1) allBtn.Set("Установить всё", Ui.Accent, true, InstallAll); else allBtn.Hide();
            footer.Foreground = notice != null ? Ui.Solid(Tokens.Palette.Red) : Ui.Label3;
            string tip = notice != null ? noticeTip : null;
            if (!Equals(footer.ToolTip, tip)) footer.ToolTip = tip;
            // Обновление закрывает core.txt — пока что-то ставится или переносится, кнопка ждёт
            if (SelfUpdate.Available) checkBtn.Set("Обновить core.txt до " + SelfUpdate.Latest.Version, Ui.Accent, !updatingSelf && !AnyBusy, UpdateSelf);
            else checkBtn.Set("Проверить обновления", null, !checking, CheckAll);
        }

        // ───────── Хаб ─────────
        void StartHub()
        {
            hub = new Hub(State.Accent, State.ReducedMotion) { Version = Program.VersionText };
            server = new HubServer(hub);
            server.Start(Hub.DefaultPort);   // порт занят — остальное работает, просто приложения не подключатся (причина — в строке состояния)
            // Сам core.txt — такое же приложение хаба, только без сети
            local = hub.Open(delegate(Dictionary<string, object> m) { Dispatcher.BeginInvoke(new Action(delegate { OnHub(m); })); }, null);
            hub.Receive(local, Hello());
            hub.Receive(local, Hub.Msg("type", "subscribe", "topics", new object[] { "*" }));
            hub.ThemeChanged += delegate { Dispatcher.BeginInvoke(new Action(delegate { TakeTheme(); })); };
        }

        // Как core.txt представляется своему хабу: такое же приложение, только без сети
        internal static Dictionary<string, object> Hello()
        {
            return Hub.Msg("type", "hello", "v", Hub.Protocol, "app", "core", "name", "core.txt", "version", Program.VersionText, "commands", new object[] { "show", Family.FocusSet });
        }

        void OnHub(Dictionary<string, object> m)
        {
            string type = Json.Str(m, "type");
            if (type == "state")
            {
                string topic = Json.Str(m, "topic");
                object data;
                m.TryGetValue("data", out data);
                if (topic == null) return;
                if (data == null) States.Remove(topic); else States[topic] = data;
                if (topic == "hub.apps")
                {
                    connectedApps.Clear();
                    foreach (object o in data as System.Collections.IEnumerable ?? new object[0])
                    {
                        string app = Json.Str(o as Dictionary<string, object>, "app");
                        if (app != null && app != "core") connectedApps.Add(app);
                    }
                }
                // Сценарий начал или перестал действовать — его записка на «Сценариях» и «Играх» должна это сказать,
                // даже если ни одна настройка при этом не поменялась
                bool moved = scenarios.Update();
                if (page == PScenarios || (page == PSettings && (moved || topic == "hub.apps" || topic.EndsWith(".settings") || topic.EndsWith(".keys")))) RenderAll();
            }
            else if (type == "result")
            {
                object ok, rid;
                m.TryGetValue("id", out rid);
                // Ответ на команду сценария строку состояния не трогает: человек ничего не нажимал и не поймёт, о чём она
                if (!userCommands.Remove(Convert.ToString(rid))) return;
                bool good = m.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
                string error = Json.Str(m, "error");
                if (good) Say(null, (string)null);
                else if (error == "offline") Say("Не получилось: приложение не запущено", (string)null);
                else if (error == "timeout") Say("Не получилось: приложение не ответило", (string)null);
                else if (error == "unknown-command") Say("Не получилось: приложение не знает такой команды — обновите его", (string)null);
                else Say("Не получилось: приложение отказало", error);
                if (good) RefreshLocal();   // подключение могло только что появиться на диске
                RenderAll();
            }
            else if (type == "command")
            {
                string name = Json.Str(m, "name");
                var args = Json.Obj(m, "args") ?? new Dictionary<string, object>();
                object id, from;
                m.TryGetValue("id", out id); m.TryGetValue("from", out from);
                var reply = Hub.Msg("type", "result", "id", id, "to", from, "ok", true);
                try
                {
                    if (name == "show") ShowFromHub(args, from as string);   // и щелчок по карточке на острове
                    else if (name == Family.FocusSet)
                    {
                        bool on;
                        double minutes;
                        string bad = CoreFamily.FocusArgs(args, out on, out minutes);
                        if (bad != null) throw new Exception(bad);
                        SetFocus(on, minutes);
                        reply["data"] = family.ToData();   // ответ — новое core.family
                    }
                    else { reply["ok"] = false; reply["error"] = "unknown-command"; }
                }
                catch (Exception e) { reply["ok"] = false; reply["error"] = e.Message; }
                hub.Receive(local, reply);
            }
        }

        // Команда show: показать окно и, если названа знакомая страница, открыть её. Незнакомая — окно просто показывается.
        // from — кто просит: приложение, которое открывает «Общие» или «Игры» ради своих пунктов, увидит их сразу.
        void ShowFromHub(Dictionary<string, object> args, string from)
        {
            ShowWindow();
            string key = CoreFamily.ShowPage(args);
            // Щелчок по карточке core.txt на острове приходит как show без страницы: открываем то, о чём была карточка
            if (key == null && from == "kapsula") key = TakeNoticePage();
            if (key == null || page == PWelcome) return;   // первый запуск не прерываем
            if (key.StartsWith("app:"))
            {
                Card c = Find(key.Substring(4));
                if (c == null || !HasSettings(c)) return;
            }
            if (key != CurrentKey) Go(key);
            if ((key == "general" || key == "games") && from != null) settings.Reveal(from);
        }

        // Страница последнего уведомления, если оно было недавно; забирается один раз
        string TakeNoticePage()
        {
            string key = noticePage != null && DateTime.UtcNow - noticeAt < TimeSpan.FromMinutes(10) ? noticePage : null;
            noticePage = null;
            return key;
        }

        // Команда приложению от человека: чем она кончилась, скажет строка состояния
        internal void SendCommand(string app, string name, Dictionary<string, object> args) { SendCommand(app, name, args, true); }

        // byUser == false — команду отправил сценарий: её ответ строку состояния не меняет
        void SendCommand(string app, string name, Dictionary<string, object> args, bool byUser)
        {
            if (hub == null) return;
            string id = "core-" + (++commandSeq);
            if (byUser) { Say(null, (string)null); userCommands.Add(id); }
            hub.Receive(local, Hub.Msg("type", "command", "id", id, "to", app, "name", name, "args", args));
        }

        internal bool Connected(string app) { return connectedApps.Contains(app); }

        // Подключение (Maska для Discord): его выполняет другое приложение по команде хаба, ответ придёт результатом
        void Connect(Card c)
        {
            if (DemoMode || c.App.Connect == null || c.App.Via == null) return;
            SendCommand(c.App.Via, c.App.Connect, new Dictionary<string, object>());
            RenderAll();
        }

        // Общий вид меняется здесь: accent — "#RRGGBB", "" (свой у каждого) или null (не менять)
        internal void SetTheme(string accent, bool? reduced)
        {
            if (accent != null) State.Accent = accent == "" ? null : accent;
            if (reduced.HasValue) State.ReducedMotion = reduced.Value;
            Ui.ForceReducedMotion = State.ReducedMotion;
            ApplyAccent();
            if (!DemoMode) State.Save();
            if (hub != null) hub.SetTheme(accent, reduced);
            RenderAll();
        }

        // Вид поменяло другое приложение командой хабу
        void TakeTheme()
        {
            State.Accent = hub.Accent;
            State.ReducedMotion = hub.ReducedMotion;
            Ui.ForceReducedMotion = State.ReducedMotion;
            ApplyAccent();
            State.Save();
            RenderAll();
        }

        // ───────── Семья: core.family, «Не беспокоить», игра на весь экран ─────────

        // Что core.txt сейчас сообщает всем приложениям
        internal FamilyState FamilyNow { get { return family; } }

        // Что приложение сообщило хабу последним; null — ничего или оно отключилось
        internal object StateOf(string topic) { object data; return States.TryGetValue(topic, out data) ? data : null; }

        // Приложение подключено и понимает команду (по hub.apps)
        internal bool AppHas(string app, string command) { return CoreFamily.Has(StateOf("hub.apps"), app, command); }

        // Горячие клавиши подключённых приложений, в порядке каталога, с совпадениями
        internal List<KeyRow> KeyRows() { return CoreFamily.KeyRows(Cards.ConvertAll(delegate(Card c) { return c.App; }), connectedApps, StateOf); }

        // Раз в секунду: не кончилось ли «Не беспокоить» и не впереди ли игра. Отдаётся только то, что изменилось.
        void CheckFamily()
        {
            string app;
            gameNow = FullScreen.Ahead(out app);
            gameAppNow = app ?? "";
            PublishFamily();
        }

        // Собирает core.family из настроек и того, что сейчас на экране, и отдаёт хабу, если что-то изменилось.
        // Хаб не возвращает приложению его же состояние — поэтому core.family кладётся и в свои States.
        void PublishFamily()
        {
            if (CoreFamily.FocusExpired(State.Focus, State.FocusUntil, FamilyState.Now()))
            {
                State.Focus = false;
                State.FocusUntil = 0;
                if (!DemoMode) State.Save();
            }
            var next = DemoMode
                ? CoreFamily.Make(State.FamilyTray, true, State.Focus, State.FocusUntil, family.Game, family.GameApp)   // на снимках игра выдуманная
                : CoreFamily.Make(State.FamilyTray, tray != null, State.Focus, State.FocusUntil, gameNow, gameAppNow);
            if (!familyOut.Changed(next)) return;
            family = next;
            var data = next.ToData();
            States[CoreFamily.Topic] = data;
            if (hub != null && local != null) hub.Receive(local, Hub.Msg("type", "state", "topic", CoreFamily.Topic, "data", data));
            scenarios.Update();
            RenderAll();
        }

        // «Не беспокоить» для всех: из окна, из меню значка и командой focus.set. minutes <= 0 — пока не выключат.
        internal void SetFocus(bool on, double minutes)
        {
            State.Focus = on;
            State.FocusUntil = on ? CoreFamily.FocusUntil(minutes, FamilyState.Now()) : 0;
            if (!DemoMode) State.Save();
            PublishFamily();
        }

        // Один значок в трее на всю семью: выключили — приложения сразу показывают свои
        internal void SetFamilyTray(bool on)
        {
            State.FamilyTray = on;
            if (!DemoMode) State.Save();
            PublishFamily();
        }

        // Пункт приложения в общем меню: команда уходит тихо, строку состояния ответ не трогает.
        // Пункт может открыть окно приложения — разрешаем ему выйти вперёд.
        void RunTray(string app, string key) { LetForeground(); SendCommand(app, Family.TrayRun, Hub.Msg("key", key), false); }

        // Открыть другое приложение на его странице («Изменить» на «Клавишах»). Чем кончилось — скажет строка состояния.
        internal void ShowApp(string app, string page) { LetForeground(); SendCommand(app, "show", Hub.Msg("page", page)); }

        // ───────── Сценарии ─────────
        internal bool ScenarioActive(string id) { return scenarios.IsActive(id); }

        internal void SetScenario(string id, bool on)
        {
            if (on) State.Scenarios.Add(id); else State.Scenarios.Remove(id);
            if (!DemoMode) State.Save();
            scenarios.Update();   // выключили действующий — он сразу возвращает всё как было
            RenderAll();
        }

        // ───────── Первый запуск ─────────
        internal void FinishWelcome(List<string> install, bool installSelf, bool autostart, bool gather)
        {
            if (DemoMode) return;
            State.Onboarded = true;
            State.Pending = install;
            State.GatherPending = gather && installSelf;   // переносит уже установленная копия, после переезда самого core.txt
            if (autostart) Autostart.Set(true, installSelf ? SelfUpdate.InstalledPath : SelfUpdate.CurrentPath);
            State.Save();
            if (installSelf)
            {
                // Дальше работает уже установленная копия: она и поставит выбранные приложения
                try { SelfUpdate.InstallSelf(); Quit(); return; }
                catch (Exception e)
                {
                    // Не встал в общую папку — работаем отсюда и честно показываем это в строке core.txt
                    welcome.SelfFailed("не установлен: " + Errors.Lower(Errors.Short(e)), e.Message);
                    State.GatherPending = false;
                    State.Save();
                }
            }
            welcome.ShowProgress(install, false, gather);
            RenderAll();
            if (gather) GatherAll();
            StartPending();
            if (State.AutoCheck) CheckAll();
        }

        internal void RenderWelcome() { if (page == PWelcome) welcome.Render(); }
        internal void LeaveWelcome() { ShowPage(PApps); Animate(); }
        internal bool GatherBusy;

        // ───────── Одна папка на всё ─────────
        internal int OutsideCount()
        {
            int n = Cards.FindAll(delegate(Card c) { return AppOps.Outside(c.App, c.S); }).Count;
            return n + (SelfUpdate.IsInstalled || DemoMode ? 0 : 1);
        }

        // Переносит приложения dev.txt в общий дом, а последним — сам core.txt
        internal async void GatherAll()
        {
            if (DemoMode || GatherBusy) return;   // перенос уже идёт — второй поверх него не начинаем
            Say(null, (string)null);
            GatherBusy = true;
            bool failed = false;
            var tried = new HashSet<string>();
            RenderAll();
            while (true)
            {
                Card card = Cards.Find(delegate(Card c) { return c.S.Phase == Phase.Idle && !tried.Contains(c.App.Id) && AppOps.Outside(c.App, c.S); });
                if (card == null)
                {
                    // Что-то ещё ставится или обновляется: ждём — сам core.txt переезжает (и закрывается) последним
                    if (!Cards.Exists(delegate(Card c) { return c.S.Phase != Phase.Idle; }) && !updatingSelf) break;
                    await Task.Delay(300);
                    continue;
                }
                tried.Add(card.App.Id);
                card.S.Phase = Phase.Installing;
                RenderAll();
                try { await Task.Run(delegate { AppOps.Gather(card.App, card.S); }); }
                catch (Exception e) { failed = true; Say(null, e); }
                card.S.Phase = Phase.Idle;
                AppOps.Refresh(card.App, card.S);
            }
            GatherBusy = false;
            RenderAll();
            if (!failed && !SelfUpdate.IsInstalled)
            {
                try { SelfUpdate.InstallSelf(); Quit(); }
                catch (Exception e)
                {
                    Say("Не удалось перенести core.txt", e);
                    welcome.SelfFailed("не перенесён: " + Errors.Lower(Errors.Short(e)), e.Message);
                    RenderAll();
                }
            }
        }

        internal void OpenHome()
        {
            if (DemoMode) return;
            try { System.IO.Directory.CreateDirectory(AppOps.Home); System.Diagnostics.Process.Start("explorer.exe", "\"" + AppOps.Home + "\""); } catch { }
        }

        // Ставит то, что выбрали на первом запуске
        void StartPending()
        {
            if (State.Pending.Count == 0) return;
            var ids = new List<string>(State.Pending);
            State.Pending.Clear();
            State.Save();
            foreach (string id in ids)
            {
                Card c = Find(id);
                if (c != null && c.App.HasExe && !c.S.Installed) Install(c);
            }
        }

        // ───────── Трей ─────────
        void StartTray()
        {
            try
            {
                tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    Text = "core.txt", Visible = true
                };
                tray.MouseClick += delegate(object s, System.Windows.Forms.MouseEventArgs e) { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow(); };
                tray.BalloonTipClicked += delegate { OpenNotice(); };   // щелчок по уведомлению открывает core.txt там, о чём оно
                // Меню Windows 11, а не серое меню WinForms
                tray.MouseUp += delegate(object s, System.Windows.Forms.MouseEventArgs e)
                {
                    if (e.Button != System.Windows.Forms.MouseButtons.Right) return;
                    var menu = new PopMenu();
                    FillTrayMenu(menu);
                    menu.ShowAtCursor();
                };
            }
            catch { tray = null; }
        }

        // Одно меню на всю семью: быстрые действия подключённых приложений (их <app>.tray), под ними — общие пункты.
        // Приложение отключилось — его состояние пропало, и в меню его больше нет.
        void FillTrayMenu(PopMenu menu)
        {
            menu.Brand(CoreSprite, "core");
            if (State.FamilyTray)
            {
                var groups = CoreFamily.TrayGroups(Cards.ConvertAll(delegate(Card c) { return c.App; }), connectedApps, StateOf);
                foreach (var g in groups)
                {
                    menu.Caption(g.Sprite, g.Name);
                    foreach (var t in g.Items)
                    {
                        string app = g.App, key = t.Key;
                        menu.Item(t.Label, t.Check == true, t.Enabled ? (Action)delegate { RunTray(app, key); } : null);
                    }
                    // Своего значка у приложения сейчас нет — закрыть его можно только отсюда
                    if (CoreFamily.Has(StateOf("hub.apps"), g.App, Family.QuitCommand) || DemoMode)
                    {
                        string who = g.App;
                        menu.Item("Закрыть", false, delegate { SendCommand(who, Family.QuitCommand, new Dictionary<string, object>(), false); });
                    }
                }
                if (groups.Count > 0) menu.Line();
            }
            menu.Item(CoreFamily.FocusLabel(family, DateTime.Now), State.Focus, delegate { SetFocus(!State.Focus, 0); });
            menu.Item("Открыть core.txt", false, ShowWindow);
            menu.Item("Проверить обновления", false, delegate { ShowWindow(); CheckAll(); });
            // Своя строка core.txt, а не всей семьи: так же она называется в «Общих» (у приложений — свои, там же)
            menu.Item("Запускать core.txt вместе с Windows", DemoMode || Autostart.Enabled, delegate { if (!DemoMode) Autostart.Set(!Autostart.Enabled); RenderAll(); });
            menu.Line();
            menu.Item("Выход", false, Quit);
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            // Показать просит другое приложение, а Windows не отдала окну фокус: тогда хотя бы поверх остальных окон
            if (!Activate()) { Topmost = true; Topmost = false; }
            RefreshLocal();
        }

        void Quit()
        {
            quitting = true;
            KeepBounds();
            if (!DemoMode) State.Save();
            Close();
            Application.Current.Shutdown();
        }

        // ───────── Действия с приложениями ─────────
        void RefreshLocal()
        {
            if (!IsVisible) return;
            foreach (var c in Cards) if (c.S.Phase == Phase.Idle) { AppOps.Refresh(c.App, c.S); InstalledApps.Sync(c.App, c.S); }
            if (page == PApps) RenderAll();
        }

        async void CheckAll()
        {
            if (checking || DemoMode) return;
            checking = true;
            Say(null, (string)null);
            RenderAll();
            bool any = false;
            int asked = 0, lost = 0;   // сколько раз спросили GitHub и сколько раз он не ответил
            GitHubLimit limit = null;   // GitHub ответил «хватит на этот час» — дело не в связи
            if (await Task.Run(delegate { return Catalog.Refresh(); })) MergeCards();   // каталог изменился: идущие установки при этом не теряются
            foreach (var c in Cards.ToArray())
            {
                if (c.App.IsIntegration) continue;
                var app = c.App;
                asked++;
                try
                {
                    Release r = await Task.Run(delegate { return Releases.Latest(app); });
                    if (r == null && app.Soon) continue;   // релизов ещё нет — так и должно быть
                    if (r != null) { State.Latest[app.Id] = r; c.S.Latest = r; any = true; }
                    if (c.S.Phase == Phase.Idle) c.S.Clear();
                }
                catch (Exception e)
                {
                    lost++;
                    if (e is GitHubLimit) limit = (GitHubLimit)e;
                    if (c.S.Phase == Phase.Idle && c.S.Latest == null && !app.Soon) { c.S.Error = e is GitHubLimit ? e.Message : "Не удалось узнать последнюю версию: нет связи с GitHub"; c.S.ErrorDetail = null; }
                }
            }
            // И сам core.txt: его релизов может ещё не быть — тогда просто ничего не предлагаем
            asked++;
            try { SelfUpdate.Latest = await Task.Run(delegate { return Releases.Latest(SelfUpdate.Entry); }); } catch (Exception e) { lost++; if (e is GitHubLimit) limit = (GitHubLimit)e; }
            if (any) { State.Checked = DateTime.UtcNow; State.Save(); }
            if (limit != null) Say(limit.Message, (string)null);   // связь есть, GitHub просит подождать
            else if (AllLost(asked, lost)) Say(Errors.Offline, (string)null);   // не ответил никто — дело в связи, а не в приложениях
            checking = false;
            RenderAll();
            int updates = Cards.FindAll(delegate(Card c) { return c.S.UpdateAvailable; }).Count + (SelfUpdate.Available ? 1 : 0);
            if (updates > 0) Notify("Есть обновления: " + updates, "Откройте core.txt, чтобы обновить", "apps");
        }

        // Проверка обновлений: ни один запрос не прошёл
        internal static bool AllLost(int asked, int lost) { return asked > 0 && lost >= asked; }

        async void UpdateSelf()
        {
            if (updatingSelf || DemoMode || !SelfUpdate.Available || AnyBusy) return;
            updatingSelf = true;
            Say(null, (string)null);
            RenderAll();
            try
            {
                Release r = SelfUpdate.Latest;
                string file = await Task.Run(delegate { return Releases.Download(r, null); });
                SelfUpdate.Apply(file);
                Quit();   // новая версия уже запущена и ждёт, пока эта закроется
                return;
            }
            catch (Exception e) { Say("Не удалось обновить core.txt", e); }
            updatingSelf = false;
            RenderAll();
        }

        async void Install(Card c)
        {
            if (c.S.Phase != Phase.Idle || DemoMode) return;
            var app = c.App;
            c.S.Clear();
            c.S.Progress = 0; c.S.Done = 0; c.S.Total = 0;
            c.S.Phase = Phase.Downloading;
            RenderAll();
            try
            {
                if (c.S.Latest == null)
                {
                    Release found = await Task.Run(delegate { return Releases.Latest(app); });
                    if (found == null) throw new Exception("В релизах пока нет файла для установки");
                    c.S.Latest = found; State.Latest[app.Id] = found; State.Save();
                }
                Release r = c.S.Latest;
                Action<long, long> progress = delegate(long done, long total) { Dispatcher.BeginInvoke(new Action(delegate { c.S.Done = done; c.S.Total = total; c.S.Progress = total > 0 ? (double)done / total : 0; if (c.S.Phase == Phase.Downloading) { c.Render(Install, Open, Find, Connect, Connected); RenderWelcome(); } })); };
                string file = await Task.Run(delegate { return Releases.Download(r, progress); });
                c.S.Phase = Phase.Installing;
                RenderAll();
                await Task.Run(delegate { AppOps.Install(app, c.S, file); });
            }
            catch (Exception e) { c.S.Fail(e); }
            c.S.Phase = Phase.Idle;
            AppOps.Refresh(app, c.S);
            InstalledApps.Sync(app, c.S);
            RenderAll();
            // Окно спрятано — о готовой установке скажет уведомление Windows
            if (c.S.Error == null && c.S.Installed) Notify(app.Name.ToLowerInvariant() + ".txt установлено", "Открыть можно из core.txt или из меню «Пуск»", "apps");
            else if (c.S.Error != null) Notify("Не удалось установить " + app.Name.ToLowerInvariant() + ".txt", c.S.Error, "apps");
        }

        // Уведомление — только когда окна core.txt не видно: при открытом окне всё и так на экране.
        // Есть остров kapsula.txt — карточка на нём; острова нет — уведомление Windows, но не при «Не беспокоить»
        // и не поверх игры (CoreFamily.NotifyRoute). Щелчок по карточке или уведомлению открывает страницу openPage
        // (null — просто окно). false — ничего не показано.
        internal bool Notify(string title, string text, string openPage)
        {
            if (DemoMode || IsVisible && IsActive) return false;
            string route = CoreFamily.NotifyRoute(hub != null && AppHas("kapsula", Family.NotifyCommand), tray != null, family.Focus, family.Game);
            if (route == null) return false;
            if (route == "island")
                SendCommand("kapsula", Family.NotifyCommand, Hub.Msg("title", CoreFamily.Cut(title, 60), "text", CoreFamily.Cut(text, 200), "open", true), false);
            else
            {
                try { tray.ShowBalloonTip(6000, title, text, System.Windows.Forms.ToolTipIcon.None); }
                catch { return false; }
            }
            noticePage = openPage;
            noticeAt = DateTime.UtcNow;
            return true;
        }

        // Щелчок по уведомлению Windows: окно и страница, о которой оно было
        void OpenNotice()
        {
            ShowWindow();
            string key = TakeNoticePage();
            if (key != null && page != PWelcome && key != CurrentKey) Go(key);
        }

        internal void InstallId(string id) { Card c = Find(id); if (c != null) Install(c); }

        // Можно ли поставить это прямо сейчас одним щелчком
        bool Installable(Card c)
        {
            return !c.App.Soon && !c.App.IsIntegration && !c.S.Installed && c.S.Phase == Phase.Idle && AppOps.Ready(c.App);
        }

        // «Установить всё»: всё, чего ещё нет и что можно поставить на этот компьютер
        void InstallAll()
        {
            foreach (var c in Cards.FindAll(Installable)) Install(c);
        }

        async void RemoveApp(Card c, bool withData)
        {
            if (c.S.Phase != Phase.Idle || DemoMode) return;
            var app = c.App;
            c.S.Clear();
            c.S.Phase = Phase.Removing;
            RenderAll();
            try { await Task.Run(delegate { AppOps.Uninstall(app, c.S, withData); }); }
            catch (Exception e) { c.S.Fail(e); }
            c.S.Phase = Phase.Idle;
            AppOps.Refresh(app, c.S);
            InstalledApps.Sync(app, c.S);
            RenderAll();
        }

        internal void Open(Card c)
        {
            if (DemoMode || c.S.Path == null) return;
            if (c.S.Launching) return;
            try
            {
                AppOps.Launch(c.S.Path);
                c.S.Clear();
                // Окно приложения появится не сразу: до тех пор кнопка говорит «Запускается…» (не дольше шести секунд)
                if (!c.S.Running && !string.IsNullOrEmpty(c.App.Process)) { c.S.LaunchUntil = DateTime.UtcNow.AddSeconds(6); launchTimer.Start(); }
            }
            catch (Exception e) { c.S.LaunchUntil = DateTime.MinValue; c.S.Fail("Не удалось запустить", e); }
            RenderAll();
        }

        // Пока какое-то приложение запускается, смотрим на список процессов чаще обычного
        void WatchLaunches()
        {
            bool waiting = false;
            foreach (var c in Cards)
            {
                if (c.S.LaunchUntil == DateTime.MinValue) continue;
                if (c.S.Phase == Phase.Idle) AppOps.Refresh(c.App, c.S);
                if (c.S.Launching) waiting = true; else c.S.LaunchUntil = DateTime.MinValue;
            }
            if (!waiting) launchTimer.Stop();
            RenderAll();
        }

        // ───────── Для снимков и проверки вида: выдуманные состояния, сеть, диск и реестр не трогаются ─────────
        void Demo()
        {
            State.Checked = new DateTime(2026, 11, 17, 9, 30, 0, DateTimeKind.Local).ToUniversalTime();
            connectedApps.Add("kapsula"); connectedApps.Add("aura");
            State.Scenarios.Add("game-hide-island");
            foreach (var c in Cards)
            {
                if (c.App.Id == "kapsula") { c.S.Path = "demo"; c.S.Version = new Version(0, 7, 5); c.S.Running = true; c.S.Latest = new Release { Version = new Version(0, 7, 6) }; }
                else if (c.App.Id == "aura") { c.S.Path = "demo"; c.S.Version = new Version(0, 26, 2); c.S.Running = true; }
                else if (c.App.Id == "maska") c.S.Path = "demo";
                else if (c.App.Id == "dock" || c.App.Id == "smena") { c.S.Path = "demo"; c.S.Version = new Version(0, 1, 2); c.S.Running = true; }
            }
            // То, что Kapsula присылает на самом деле (см. её CoreHub.cs), здесь — часть списка
            { string sec = Environment.GetEnvironmentVariable("CORE_DEMO_SECTION"); if (sec != null) settings.Section = sec; }   // снимок нужного раздела настроек
            string demoStep = Environment.GetEnvironmentVariable("CORE_DEMO_STEP");   // снимок нужного шага установщика
            if (demoStep == "1") welcome.SetStep(1);
            if (demoStep == "2")
            {
                welcome.ShowProgress(new List<string> { "kapsula", "aura", "dock" }, true, true);
                foreach (var c in Cards)
                {
                    if (c.App.Id == "kapsula") { c.S.Path = "demo"; c.S.Version = new Version(0, 8, 0); }
                    if (c.App.Id == "aura") { c.S.Path = null; c.S.Phase = Phase.Downloading; c.S.Progress = 0.62; c.S.Total = 151L * 1024 * 1024; c.S.Done = (long)(c.S.Total * 0.62); }
                    if (c.App.Id == "dock") { c.S.Path = null; }
                }
            }
            else if (demoStep != null) foreach (var c in Cards) if (c.App.Id == "aura" || c.App.Id == "dock") { c.S.Path = null; c.S.Running = false; }
            string ask = Environment.GetEnvironmentVariable("CORE_DEMO_ASK");   // снимок карточки с вопросом об удалении
            if (ask != null && Find(ask) != null) Find(ask).AskRemove();
            States["kapsula.settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""autoHide"",""type"":""switch"",""label"":""Автоскрытие"",""hint"":""Остров уезжает за край и возвращается, когда курсор у верхней кромки экрана или есть что показать"",""value"":false},{""key"":""seconds"",""type"":""switch"",""label"":""Секунды в часах"",""hint"":""В капсуле вместо 12:30 будет 12:30:45"",""value"":false},{""key"":""notifications"",""type"":""switch"",""label"":""Уведомления Windows на острове"",""hint"":""Новые уведомления других программ показываются карточкой. Нигде не сохраняются"",""value"":true},{""key"":""rim"",""type"":""switch"",""label"":""Светлая кромка"",""hint"":""Тонкий блик по краю острова — он не сливается с тёмными обоями и окнами"",""value"":true},{""key"":""shadow"",""type"":""switch"",""label"":""Тень под островом"",""hint"":""Мягкая тень: на светлых окнах остров словно парит над ними"",""value"":false}]},{""title"":""Зверёк"",""items"":[{""key"":""eyes"",""type"":""switch"",""label"":""Зверёк в капсуле"",""hint"":""Следит за курсором, пугается таймера, радуется удаче"",""value"":true},{""key"":""walk"",""type"":""switch"",""label"":""Зверёк гуляет по окнам"",""hint"":""Когда остров свёрнут и ничего не происходит, выходит посидеть на краю окна"",""value"":true}]}]}");
            // То, что Aura присылает на самом деле (см. её app.js, hubSettingsNow)
            States["dock.settings"] = Json.Parse(@"{""groups"":[{""title"":""Панель задач"",""items"":[{""key"":""bar"",""type"":""choice"",""label"":""Внизу экрана"",""hint"":""dock.txt — панель задач Windows прячется. Компактная панель Windows — тонкая полоса с часами"",""value"":""dock"",""options"":[{""value"":""dock"",""label"":""dock.txt""},{""value"":""modern"",""label"":""Компактная панель Windows""},{""value"":""both"",""label"":""dock.txt и панель Windows""}]},{""key"":""winKey"",""type"":""switch"",""label"":""Кнопка Windows открывает меню дока"",""hint"":""Вместо «Пуска» — меню приложений dock.txt. Сочетания вроде Win+E работают как обычно"",""value"":true}]}]}");
            States["smena.settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""replace"",""type"":""switch"",""label"":""Заменять Alt+Tab"",""hint"":""Вместо системного переключателя окон открывается этот"",""value"":true}]}]}");
            States["aura.settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""power"",""type"":""switch"",""label"":""Обработка голоса"",""hint"":""Включает и выключает aura.txt"",""value"":true},{""key"":""preset"",""type"":""choice"",""label"":""Готовый звук"",""value"":""studio"",""options"":[{""value"":""auto"",""label"":""Под вас""},{""value"":""studio"",""label"":""Подкаст""},{""value"":""radio"",""label"":""Эфир""},{""value"":""crystal"",""label"":""Стрим""},{""value"":""velvet"",""label"":""Бархат""},{""value"":""cinema"",""label"":""Кино""},{""value"":""whisper"",""label"":""Чистый""},{""value"":""custom"",""label"":""Свой""}]},{""key"":""mute"",""type"":""switch"",""label"":""Заглушить микрофон"",""hint"":""Тишина для всех программ"",""value"":false},{""key"":""auto"",""type"":""switch"",""label"":""Авто-уровень"",""hint"":""Выравнивает громкость, вытягивает тихий микрофон"",""value"":true},{""key"":""monitor"",""type"":""switch"",""label"":""Слышать себя"",""hint"":""Только в наушниках, иначе будет эхо"",""value"":false}]},{""title"":""Звук"",""items"":[{""key"":""nsEngine"",""type"":""choice"",""label"":""Шумодав"",""value"":""0"",""options"":[{""value"":""0"",""label"":""Обычный""},{""value"":""1"",""label"":""Жёсткий""}]},{""key"":""loudTarget"",""type"":""choice"",""label"":""Громкость на выходе"",""value"":""0"",""options"":[{""value"":""0"",""label"":""Не выравнивать""},{""value"":""-18"",""label"":""−18 LUFS""},{""value"":""-16"",""label"":""−16 LUFS""},{""value"":""-14"",""label"":""−14 LUFS""}]},{""key"":""dehum"",""type"":""switch"",""label"":""Убрать гул сети"",""hint"":""Вырезает 50 Гц и его гармоники — гудение от розетки и плохой земли"",""value"":false}]}]}");
            DemoFamily();
            // Сценарии — по выдуманным состояниям: игра впереди, значит, «Во время игры прятать остров» действует.
            // Хаба в демо нет, поэтому команды никуда не уходят.
            scenarios.Update();
        }

        // Семья на снимках: быстрые действия для общего меню, горячие клавиши (одно совпадение — нарочно),
        // пункты настроек с полем section, кто что умеет и core.family с выдуманной игрой
        void DemoFamily()
        {
            connectedApps.Add("smena"); connectedApps.Add("dock"); connectedApps.Add("metla");
            // metla.txt на связи — значит, стоит и запущена (снимки установщика оставляем как были)
            if (Environment.GetEnvironmentVariable("CORE_DEMO_STEP") == null)
                foreach (var c in Cards) if (c.App.Id == "metla") { c.S.Path = "demo"; c.S.Version = new Version(0, 1, 0); c.S.Running = true; }
            States["hub.apps"] = Json.ParseAny(@"[{""app"":""core"",""commands"":[""show"",""focus.set""]},{""app"":""kapsula"",""commands"":[""settings.set"",""show"",""tray.run"",""notify""]},{""app"":""aura"",""commands"":[""settings.set"",""show"",""tray.run""]},{""app"":""dock"",""commands"":[""settings.set"",""tray.run""]},{""app"":""smena"",""commands"":[""settings.set"",""show"",""tray.run""]},{""app"":""metla"",""commands"":[""settings.set"",""show"",""tray.run""]}]");
            // «Не беспокоить» примерно на полтора часа, до ровных получаса, и игра впереди
            State.Focus = true;
            DateTime soon = DateTime.Now.AddMinutes(90);
            DateTime end = soon.Date.AddHours(soon.Hour).AddMinutes(soon.Minute < 30 ? 0 : 30);
            State.FocusUntil = (long)(end.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            family = CoreFamily.Make(State.FamilyTray, true, State.Focus, State.FocusUntil, true, "cs2");
            States[CoreFamily.Topic] = family.ToData();
            if (Environment.GetEnvironmentVariable("CORE_DEMO_EMPTY") != null) return;   // снимок пустых «Игр», «Клавиш» и меню: приложения ещё ничего не прислали

            // Пункты трея — те, что приложения присылают на самом деле (kapsula: CoreHub.TrayItems, dock: FamilyRules.Tray)
            States["kapsula.tray"] = Json.Parse(@"{""items"":[{""key"":""show"",""label"":""Настройки""},{""key"":""awake"",""label"":""Не засыпать"",""check"":false},{""key"":""lyrics"",""label"":""Тексты песен"",""check"":true},{""key"":""shots"",""label"":""Папка со снимками""}]}");
            States["aura.tray"] = Json.Parse(@"{""items"":[{""key"":""show"",""label"":""Открыть""},{""key"":""power"",""label"":""Обработка голоса"",""check"":true},{""key"":""mute"",""label"":""Заглушить микрофон"",""check"":false}]}");
            States["dock.tray"] = Json.Parse(@"{""items"":[{""key"":""autoHide"",""label"":""Автоскрытие"",""check"":false},{""key"":""settings"",""label"":""Настройки""}]}");
            States["smena.tray"] = Json.Parse(@"{""items"":[{""key"":""show"",""label"":""Открыть""},{""key"":""pause"",""label"":""Приостановить"",""check"":false}]}");
            States["metla.tray"] = Json.Parse(@"{""items"":[{""key"":""show"",""label"":""Открыть""},{""key"":""scan"",""label"":""Проверить диск""}]}");
            // Сочетания — часть того, что приложения присылают на самом деле (выключенное kapsula.txt шлёт пустым);
            // «Заглушить микрофон» в aura.txt нарочно совпадает с «Диктовкой», чтобы было видно предупреждение
            States["kapsula.keys"] = Json.Parse(@"{""keys"":[{""id"":""cmd"",""label"":""Строка команд"",""keys"":""Alt+Space"",""on"":true},{""id"":""area"",""label"":""Снимок области с разметкой"",""keys"":""Ctrl+Alt+S"",""on"":true},{""id"":""full"",""label"":""Снимок всего экрана"",""keys"":""Ctrl+Alt+Shift+S"",""on"":true},{""id"":""dictate"",""label"":""Диктовка"",""keys"":""Ctrl+Alt+D"",""on"":true},{""id"":""rec"",""label"":""Запись экрана"",""keys"":"""",""on"":false}]}");
            States["aura.keys"] = Json.Parse(@"{""keys"":[{""id"":""power"",""label"":""Обработка голоса"",""keys"":""Ctrl+Shift+F9"",""on"":true},{""id"":""mute"",""label"":""Заглушить микрофон"",""keys"":""Ctrl+Alt+D"",""on"":true}]}");
            States["dock.keys"] = Json.Parse(@"{""keys"":[{""id"":""winKey"",""label"":""Меню приложений"",""keys"":""Win"",""on"":true}]}");
            States["smena.keys"] = Json.Parse(@"{""keys"":[{""id"":""switch"",""label"":""Переключение окон"",""keys"":""Alt+Tab"",""on"":true},{""id"":""sameApp"",""label"":""Окна того же приложения"",""keys"":""Alt+`"",""on"":true}]}");
            States["metla.settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""remind"",""type"":""switch"",""label"":""Напоминать, когда можно освободить больше 5 ГБ"",""hint"":""metla.txt остаётся в трее и раз в день сама проверяет диск. Удаляете всё равно только вы"",""value"":true}]}]}");
            // Пункты с полем section — те же, что присылают kapsula.txt (CoreHub.SectionOf), aura.txt (hubSettingsNow)
            // и smena.txt (AppHub): core.txt показывает их на «Играх» и в «Общих», а не на странице приложения
            DemoItems("kapsula", "Игры",
                @"{""key"":""showInGames"",""type"":""switch"",""label"":""Показывать остров в играх"",""hint"":""Остров остаётся на месте поверх игр и видео на весь экран, а не прячется за край"",""value"":false,""section"":""games""}",
                @"{""key"":""fsTrack"",""type"":""switch"",""label"":""Новый трек поверх игр и видео"",""hint"":""Спрятанный от игры остров выглядывает на пару секунд с названием нового трека"",""value"":true,""section"":""games""}",
                @"{""key"":""gameLoad"",""type"":""switch"",""label"":""Нагрузка в капсуле во время игры"",""hint"":""Пока остров показан поверх игры, капсула вместо часов показывает процессор, видеокарту и память"",""value"":false,""section"":""games""}");
            DemoItems("kapsula", "Связь с другими приложениями",
                @"{""key"":""share"",""type"":""switch"",""label"":""Делиться с приложениями dev.txt"",""hint"":""Трек, погода и сегодняшние события — только название и время; непрочитанное в Discord и Steam — только числа. Дальше этого компьютера не уходит"",""value"":true,""section"":""family""}");
            DemoItems("kapsula", "Запуск и обновления",
                @"{""key"":""autostart"",""type"":""switch"",""label"":""Запускать вместе с Windows"",""value"":true,""section"":""startup""}",
                @"{""key"":""autoUpdate"",""type"":""switch"",""label"":""Автообновление"",""hint"":""kapsula.txt сама скачивает новые версии с GitHub и сверяет контрольную сумму файла"",""value"":true,""section"":""startup""}");
            DemoItems("aura", "Запуск и обновления",
                @"{""key"":""autostart"",""type"":""switch"",""label"":""Запускать вместе с Windows"",""hint"":""aura.txt открывается свёрнутой в трей, обработка голоса работает сразу"",""value"":true,""section"":""startup""}",
                @"{""key"":""autoUpdate"",""type"":""switch"",""label"":""Проверять обновления"",""hint"":""При запуске спрашивает у GitHub, вышла ли новая версия. Устанавливается только по вашему нажатию"",""value"":true,""section"":""startup""}");
            DemoItems("smena", "",
                @"{""key"":""games"",""type"":""switch"",""label"":""Не вмешиваться в играх"",""hint"":""Когда впереди игра или другое окно на весь экран, работает обычный Alt+Tab"",""value"":true,""section"":""games""}");
            // Снимок страницы приложения, у которого все настройки переехали на общие страницы
            string moved = Environment.GetEnvironmentVariable("CORE_DEMO_MOVED");
            if (moved != null && States.ContainsKey(moved + ".settings"))
                States[moved + ".settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""autostart"",""type"":""switch"",""label"":""Запускать вместе с Windows"",""value"":true,""section"":""startup""}]}]}");
        }

        // Добавляет к выдуманным настройкам приложения ещё одну группу пунктов
        void DemoItems(string app, string title, params string[] items)
        {
            object now;
            var settings = States.TryGetValue(app + ".settings", out now) ? now as Dictionary<string, object> : null;
            if (settings == null) return;
            var groups = new List<object>();
            foreach (object g in Json.List(settings, "groups")) groups.Add(g);
            var group = Json.Parse(@"{""items"":[" + string.Join(",", items) + "]}");
            group["title"] = title;
            groups.Add(group);
            settings["groups"] = groups.ToArray();
        }

        // Рисует окно в файл PNG, не показывая его на экране. scroll — на сколько прокрутить открытую страницу.
        // Проверка движения: окно показывается по-настоящему, действия проигрываются по очереди на выдуманных
        // состояниях (сеть и диск не трогаются), а кадры посреди движения и после него складываются в папку.
        public void MotionTest(string dir, Action done)
        {
            System.IO.Directory.CreateDirectory(dir);
            ShowActivated = false;
            Show();
            var plan = new List<KeyValuePair<int, Action>>();
            int n = 0;
            Action<int, Action> at = delegate(int wait, Action a) { plan.Add(new KeyValuePair<int, Action>(wait, a)); };
            Action<int, string> grab = delegate(int wait, string name)
            {
                at(wait, delegate
                {
                    var bmp = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render((Visual)Content);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var f = System.IO.File.Create(System.IO.Path.Combine(dir, (++n).ToString("00") + "-" + name + ".png"))) enc.Save(f);
                });
            };
            Card aura = Find("aura"), dockCard = Find("dock"), arkada = Find("arkada");

            // Установщик: шаги
            at(0, delegate { foreach (var c in Cards) if (c.App.Id == "aura" || c.App.Id == "dock") { c.S.Path = null; c.S.Running = false; } welcome.SetStep(0); ShowPage(PWelcome); });
            grab(140, "step1-in"); grab(800, "step1");
            at(0, delegate { welcome.SetStep(1); RenderWelcome(); });
            grab(140, "step2-in"); grab(800, "step2");
            // Установщик: ход
            at(0, delegate { welcome.ShowProgress(new List<string> { "kapsula", "aura", "dock" }, true, false); aura.S.Phase = Phase.Downloading; aura.S.Progress = 0.08; RenderAll(); });
            grab(140, "run-in"); grab(700, "run");
            at(0, delegate { aura.S.Progress = 0.7; RenderAll(); });
            grab(110, "run-bar-moving"); grab(500, "run-bar");
            at(0, delegate { aura.S.Phase = Phase.Installing; dockCard.S.Phase = Phase.Downloading; dockCard.S.Progress = 0.4; RenderAll(); });
            grab(450, "run-installing");
            at(0, delegate { aura.S.Phase = Phase.Idle; aura.S.Path = "demo"; dockCard.S.Phase = Phase.Idle; dockCard.S.Error = "Нет связи с GitHub"; RenderAll(); });
            grab(120, "run-done-moving"); grab(1300, "run-done");
            // Окно приложений: вход, установка на карточке, ошибка, успех
            at(0, delegate { dockCard.S.Error = null; LeaveWelcome(); });
            grab(140, "apps-in"); grab(900, "apps");
            at(0, delegate { arkada.S.Phase = Phase.Downloading; arkada.S.Progress = 0.35; RenderAll(); });
            grab(160, "card-download-in"); grab(600, "card-download");
            at(0, delegate { arkada.S.Phase = Phase.Installing; RenderAll(); });
            grab(500, "card-installing");
            at(0, delegate { arkada.S.Phase = Phase.Idle; arkada.S.Error = "Нет связи с GitHub"; RenderAll(); });
            grab(120, "card-error-moving"); grab(700, "card-error");
            at(0, delegate { arkada.S.Error = null; arkada.S.Phase = Phase.Installing; RenderAll(); });
            at(300, delegate { arkada.S.Phase = Phase.Idle; arkada.S.Path = "demo"; RenderAll(); });
            grab(150, "card-done-moving"); grab(1300, "card-done");
            // Вопрос об удалении и вкладки
            at(0, delegate { aura.AskRemove(); RenderAll(); });
            grab(120, "remove-ask-in"); grab(500, "remove-ask");
            at(0, delegate { ShowPage(PSettings); Animate(); });
            grab(120, "tab-moving"); grab(800, "tab");
            at(0, delegate { ShowPage(PApps); Animate(); });
            grab(700, "back");
            at(0, done);

            int i = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Tick += delegate
            {
                timer.Stop();
                if (i >= plan.Count) return;
                plan[i++].Value();
                if (i < plan.Count) { timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, plan[i].Key)); timer.Start(); }
            };
            timer.Interval = TimeSpan.FromMilliseconds(400);
            timer.Start();
        }

        // Снимок общего меню значка в трее — те же пункты, что по правому щелчку, только без окна на экране
        public void MenuShot(string file)
        {
            var menu = new PopMenu();
            FillTrayMenu(menu);
            var box = (FrameworkElement)menu.Content;
            menu.Content = null;
            var stage = new Border { Background = Ui.Bg, Padding = new Thickness(12), Child = box };
            stage.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            stage.Arrange(new Rect(stage.DesiredSize));
            stage.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)Math.Ceiling(stage.ActualWidth * 1.5), (int)Math.Ceiling(stage.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
            bmp.Render(stage);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = System.IO.File.Create(file)) enc.Save(f);
        }

        public void Shot(string file, double scroll)
        {
            var root = (FrameworkElement)Content;
            Content = null;
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            RenderAll();
            root.UpdateLayout();
            // Снимок раздела, открытого «для приложения»: прокрутка к его пунктам, как по команде show от него
            string reveal = DemoMode && page == PSettings ? Environment.GetEnvironmentVariable("CORE_DEMO_REVEAL") : null;
            double? at = reveal != null ? settings.OffsetOf(reveal) : null;
            if (at.HasValue) scroll = at.Value;
            if (scroll > 0)
            {
                ScrollViewer view = page == PSettings ? settings.Root : page == PScenarios ? scenariosPage.Root : page == PWelcome ? welcome.Root : appsView;
                view.ScrollToVerticalOffset(scroll);
                root.UpdateLayout();
            }
            var bmp = new RenderTargetBitmap((int)(W * 1.5), (int)(H * 1.5), 144, 144, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = System.IO.File.Create(file)) enc.Save(f);
        }
    }
}
