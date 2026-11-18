// core.txt. MainWindow.cs — окно: шапка с вкладками, страницы (приложения, сценарии, настройки, первый запуск),
// строка состояния. Здесь же окно держит хаб, сценарии и значок в трее.
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
        static readonly string[] CoreSprite = { ".....#....", "....##....", ".########.", "##########", ".########.", "..######..", "...##E#...", "...##E#...", "..######..", ".########.", "##########", ".########." };

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
        string notice;   // последняя ошибка — показывается в строке состояния

        Hub hub;
        HubServer server;
        HubClient local;
        int commandSeq;
        System.Windows.Forms.NotifyIcon tray;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainWindow(bool demo, int startPage, bool afterInstall)
        {
            DemoMode = demo;
            page = !demo && !State.Onboarded ? PWelcome : startPage;
            Title = "core.txt";
            Width = W; Height = H;
            MinWidth = 1000; MinHeight = 600;
            // Родное окно Windows 11: системная рамка и тень, Mica, тема Windows, размер меняется, окно прилипает к краям
            Ui.ThemeFollows = !demo;
            Ui.Native(this, true);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
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
                delegate(string app, string key, object value) { SendCommand(app, "settings.set", Hub.Msg("key", key, "value", value)); },
                delegate(string id) { return State.Scenarios.Contains(id); });
            Content = Build();
            BuildCards();
            ShowPage(page);

            if (demo) return;

            StartHub();
            StartTray();
            timer.Tick += delegate { RefreshLocal(); TaskbarGuard.Check(); };   // док пропал, а панель задач спрятана — возвращаем
            timer.Start();
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!quitting && State.Tray && State.Onboarded) { e.Cancel = true; Hide(); return; }   // окно закрыли — core.txt остаётся в трее и держит хаб
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (server != null) server.Stop();
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

            appsView = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Padding = new Thickness(25, 0, 25, 0), Content = grid, Focusable = false };
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
            var keys = new List<string> { "apps", "scenarios", "general", "replace" };
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
            else if (key2 == "general") shell.Title("Общие", "Вид, запуск, папка и обновления — для всех приложений сразу");
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

        Card Find(string id) { return Cards.Find(delegate(Card c) { return c.App.Id == id; }); }

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
            else if (page == PSettings) line = server != null && server.Error != null ? "Связь между приложениями не работает: " + server.Error : "Связь между приложениями работает · подключено: " + connectedApps.Count;
            else if (page == PScenarios) line = "Сценарии действуют, пока core.txt запущен";
            else line = (updates > 0 ? "Обновлений: " + updates + " · " : "") + when;
            Motion.Swap(footer, line + " · версия " + Program.VersionText + " · dev.txt™");
            int missing = Cards.FindAll(Installable).Count;
            if (page == PApps && missing > 1) allBtn.Set("Установить всё", Tokens.Palette.Brand, true, InstallAll); else allBtn.Hide();
            footer.Foreground = notice != null ? Ui.Solid(Tokens.Palette.Red) : Ui.Label3;
            if (SelfUpdate.Available) checkBtn.Set("Обновить core.txt до " + SelfUpdate.Latest.Version, Tokens.Palette.Brand, !updatingSelf, UpdateSelf);
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
            hub.Receive(local, Hub.Msg("type", "hello", "v", Hub.Protocol, "app", "core", "name", "core.txt", "version", Program.VersionText, "commands", new object[] { "show" }));
            hub.Receive(local, Hub.Msg("type", "subscribe", "topics", new object[] { "*" }));
            hub.ThemeChanged += delegate { Dispatcher.BeginInvoke(new Action(delegate { TakeTheme(); })); };
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
                scenarios.Update();
                if (page == PScenarios || (page == PSettings && (topic == "hub.apps" || topic.EndsWith(".settings")))) RenderAll();
            }
            else if (type == "result")
            {
                object ok;
                bool good = m.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
                string error = Json.Str(m, "error");
                notice = good ? null : "Не получилось: " + (error == "offline" ? "приложение не запущено" : error == "timeout" ? "приложение не ответило" : error ?? "приложение отказало");
                if (good) RefreshLocal();   // подключение могло только что появиться на диске
                RenderAll();
            }
            else if (type == "command")
            {
                bool known = Json.Str(m, "name") == "show";
                if (known) ShowWindow();
                object id, from;
                m.TryGetValue("id", out id); m.TryGetValue("from", out from);
                var reply = Hub.Msg("type", "result", "id", id, "to", from, "ok", known);
                if (!known) reply["error"] = "unknown-command";
                hub.Receive(local, reply);
            }
        }

        internal void SendCommand(string app, string name, Dictionary<string, object> args)
        {
            if (hub == null) return;
            notice = null;
            hub.Receive(local, Hub.Msg("type", "command", "id", "core-" + (++commandSeq), "to", app, "name", name, "args", args));
        }

        internal bool Connected(string app) { return connectedApps.Contains(app); }

        // Подключение (Maska для Discord): его выполняет другое приложение по команде хаба, ответ придёт результатом
        void Connect(Card c)
        {
            if (DemoMode || c.App.Connect == null || c.App.Via == null) return;
            SendCommand(c.App.Via, c.App.Connect, new Dictionary<string, object>());
            notice = null;
            RenderAll();
        }

        // Общий вид меняется здесь: accent — "#RRGGBB", "" (свой у каждого) или null (не менять)
        internal void SetTheme(string accent, bool? reduced)
        {
            if (accent != null) State.Accent = accent == "" ? null : accent;
            if (reduced.HasValue) State.ReducedMotion = reduced.Value;
            Ui.ForceReducedMotion = State.ReducedMotion;
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
            State.Save();
            RenderAll();
        }

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
                catch (Exception e) { notice = "Не удалось установить core.txt: " + e.Message; }
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
            if (DemoMode) return;
            notice = null;
            GatherBusy = true;
            foreach (var c in Cards.ToArray())
            {
                if (!AppOps.Outside(c.App, c.S) || c.S.Phase != Phase.Idle) continue;
                var card = c;
                card.S.Phase = Phase.Installing;
                RenderAll();
                try { await Task.Run(delegate { AppOps.Gather(card.App, card.S); }); }
                catch (Exception e) { notice = e.Message; }
                card.S.Phase = Phase.Idle;
                AppOps.Refresh(card.App, card.S);
            }
            GatherBusy = false;
            RenderAll();
            if (notice == null && !SelfUpdate.IsInstalled)
            {
                try { SelfUpdate.InstallSelf(); Quit(); }
                catch (Exception e) { notice = "Не удалось перенести core.txt: " + e.Message; RenderAll(); }
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
                tray.BalloonTipClicked += delegate { ShowWindow(); };   // щелчок по уведомлению открывает core.txt
                // Меню Windows 11, а не серое меню WinForms
                tray.MouseUp += delegate(object s, System.Windows.Forms.MouseEventArgs e)
                {
                    if (e.Button != System.Windows.Forms.MouseButtons.Right) return;
                    NativeMenu.Show(new NativeMenu.Item("Открыть core.txt", ShowWindow), new NativeMenu.Item("Проверить обновления", delegate { ShowWindow(); CheckAll(); }), NativeMenu.Item.Separator, new NativeMenu.Item("Выход", Quit));
                };
            }
            catch { tray = null; }
        }

        public void ShowWindow()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            RefreshLocal();
        }

        void Quit()
        {
            quitting = true;
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
            notice = null;
            RenderAll();
            bool any = false;
            if (await Task.Run(delegate { return Catalog.Refresh(); })) BuildCards();   // в каталоге появилось новое приложение
            foreach (var c in Cards.ToArray())
            {
                if (c.App.IsIntegration) continue;
                var app = c.App;
                try
                {
                    Release r = await Task.Run(delegate { return Releases.Latest(app); });
                    if (r == null && app.Soon) continue;   // релизов ещё нет — так и должно быть
                    if (r != null) { State.Latest[app.Id] = r; c.S.Latest = r; any = true; }
                    if (c.S.Phase == Phase.Idle) c.S.Error = null;
                }
                catch { if (c.S.Phase == Phase.Idle && c.S.Latest == null && !app.Soon) c.S.Error = "Не удалось узнать последнюю версию: нет связи с GitHub"; }
            }
            // И сам core.txt: его релизов может ещё не быть — тогда просто ничего не предлагаем
            try { SelfUpdate.Latest = await Task.Run(delegate { return Releases.Latest(SelfUpdate.Entry); }); } catch { }
            if (any) { State.Checked = DateTime.UtcNow; State.Save(); }
            checking = false;
            RenderAll();
            int updates = Cards.FindAll(delegate(Card c) { return c.S.UpdateAvailable; }).Count + (SelfUpdate.Available ? 1 : 0);
            if (updates > 0) Notify("Есть обновления: " + updates, "Откройте core.txt, чтобы обновить");
        }

        async void UpdateSelf()
        {
            if (updatingSelf || DemoMode || !SelfUpdate.Available) return;
            updatingSelf = true;
            notice = null;
            RenderAll();
            try
            {
                Release r = SelfUpdate.Latest;
                string file = await Task.Run(delegate { return Releases.Download(r, null); });
                SelfUpdate.Apply(file);
                Quit();   // новая версия уже запущена и ждёт, пока эта закроется
                return;
            }
            catch (Exception e) { notice = "Не удалось обновить core.txt: " + e.Message; }
            updatingSelf = false;
            RenderAll();
        }

        async void Install(Card c)
        {
            if (c.S.Phase != Phase.Idle || DemoMode) return;
            var app = c.App;
            c.S.Error = null;
            c.S.Progress = 0;
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
                Action<double> progress = delegate(double p) { Dispatcher.BeginInvoke(new Action(delegate { c.S.Progress = p; if (c.S.Phase == Phase.Downloading) { c.Render(Install, Open, Find, Connect, Connected); RenderWelcome(); } })); };
                string file = await Task.Run(delegate { return Releases.Download(r, progress); });
                c.S.Phase = Phase.Installing;
                RenderAll();
                await Task.Run(delegate { AppOps.Install(app, c.S, file); });
            }
            catch (System.Net.WebException) { c.S.Error = "Нет связи с GitHub"; }
            catch (Exception e) { c.S.Error = e.Message; }
            c.S.Phase = Phase.Idle;
            AppOps.Refresh(app, c.S);
            InstalledApps.Sync(app, c.S);
            RenderAll();
            // Окно спрятано — о готовой установке скажет уведомление Windows
            if (c.S.Error == null && c.S.Installed) Notify(app.Name.ToLowerInvariant() + ".txt установлено", "Открыть можно из core.txt или из меню «Пуск»");
            else if (c.S.Error != null) Notify("Не удалось установить " + app.Name.ToLowerInvariant() + ".txt", c.S.Error);
        }

        // Уведомление Windows — только когда окна core.txt не видно: при открытом окне всё и так на экране
        internal void Notify(string title, string text)
        {
            if (tray == null || DemoMode || IsVisible && IsActive) return;
            try { tray.ShowBalloonTip(6000, title, text, System.Windows.Forms.ToolTipIcon.None); } catch { }
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
            c.S.Error = null;
            c.S.Phase = Phase.Removing;
            RenderAll();
            try { await Task.Run(delegate { AppOps.Uninstall(app, c.S, withData); }); }
            catch (Exception e) { c.S.Error = e.Message; }
            c.S.Phase = Phase.Idle;
            AppOps.Refresh(app, c.S);
            InstalledApps.Sync(app, c.S);
            RenderAll();
        }

        internal void Open(Card c)
        {
            if (DemoMode || c.S.Path == null) return;
            try { AppOps.Launch(c.S.Path); c.S.Error = null; }
            catch (Exception e) { c.S.Error = "Не удалось запустить: " + e.Message; }
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
                    if (c.App.Id == "aura") { c.S.Path = null; c.S.Phase = Phase.Downloading; c.S.Progress = 0.62; }
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

        public void Shot(string file, double scroll)
        {
            var root = (FrameworkElement)Content;
            Content = null;
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            RenderAll();
            root.UpdateLayout();
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
