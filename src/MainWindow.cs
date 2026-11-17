// core.txt. MainWindow.cs — окно: шапка с вкладками, страница приложений, страница настроек, строка состояния.
// Здесь же окно держит хаб и значок в трее.
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
        const double W = 920, H = 640;
        // Значок core.txt — тот же рисунок, что в kit.txt (icons/sprites.json, «core»)
        static readonly string[] CoreSprite = { ".....#....", "....##....", ".########.", "##########", ".########.", "..######..", "...##E#...", "...##E#...", "..######..", ".########.", "##########", ".########." };

        internal readonly bool DemoMode;
        internal readonly List<Card> Cards = new List<Card>();
        internal readonly Dictionary<string, object> States = new Dictionary<string, object>();   // что приложения сообщили хабу
        readonly UniformGrid grid = new UniformGrid { Columns = 2, VerticalAlignment = VerticalAlignment.Top };
        readonly TextBlock footer = Ui.T("", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
        readonly Btn checkBtn = new Btn();
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        readonly SettingsPage settings;
        ScrollViewer appsView;
        Seg tabs;
        int page;
        bool checking, quitting;
        string notice;   // последняя ошибка команды — показывается в строке состояния

        Hub hub;
        HubServer server;
        HubClient local;
        int commandSeq, connected;
        System.Windows.Forms.NotifyIcon tray;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public MainWindow(bool demo, int startPage)
        {
            DemoMode = demo;
            page = startPage;
            Title = "core.txt";
            Width = W; Height = H;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanMinimize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Ui.Bg;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            Ui.ForceReducedMotion = State.ReducedMotion;
            settings = new SettingsPage(this);
            Content = Build();
            BuildCards();
            ShowPage(page);

            SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int round = 2, dark = 1;   // скруглённые углы и тёмная рамка окна в Windows 11
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            };
            if (demo) return;

            StartHub();
            StartTray();
            timer.Tick += delegate { RefreshLocal(); };
            timer.Start();
            Closing += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                if (!quitting && State.Tray) { e.Cancel = true; Hide(); return; }   // окно закрыли — core.txt остаётся в трее и держит хаб
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (server != null) server.Stop();
            };
            if (State.AutoCheck && DateTime.UtcNow - State.Checked > TimeSpan.FromHours(1)) CheckAll();
        }

        // ───────── Разметка ─────────
        FrameworkElement Build()
        {
            var root = new Grid { Background = Ui.Bg, Width = W, Height = H, ClipToBounds = true };
            root.Children.Add(Ui.Backdrop());
            var layout = new DockPanel { LastChildFill = true };
            root.Children.Add(layout);

            // Шапка: за неё окно перетаскивается
            var header = new Grid { Height = 92, Background = Brushes.Transparent };
            header.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            var mark = Ui.Sprite(CoreSprite, 40);
            mark.VerticalAlignment = VerticalAlignment.Center;
            var title = Ui.Brand("core", Tokens.Size.Display, FontWeights.Bold);
            title.Margin = new Thickness(10, 0, 0, 2); title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(32, 22, 0, 0), VerticalAlignment = VerticalAlignment.Center, Children = { mark, title } });

            tabs = new Seg(new string[] { "Приложения", "Настройки" }, page, delegate(int i) { ShowPage(i); });
            tabs.Root.HorizontalAlignment = HorizontalAlignment.Right; tabs.Root.VerticalAlignment = VerticalAlignment.Center;
            tabs.Root.Margin = new Thickness(0, 24, 32, 0);
            header.Children.Add(tabs.Root);

            var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 8, 0) };
            controls.Children.Add(WindowButton("", "Свернуть", false, delegate { WindowState = WindowState.Minimized; }));
            controls.Children.Add(WindowButton("", "Закрыть", true, delegate { Close(); }));
            header.Children.Add(controls);
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);

            // Низ: строка состояния и кнопка проверки обновлений
            var bottom = new DockPanel { Height = 64, Margin = new Thickness(32, 0, 32, 6) };
            checkBtn.Root.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(checkBtn.Root, Dock.Right);
            bottom.Children.Add(checkBtn.Root);
            footer.VerticalAlignment = VerticalAlignment.Center;
            bottom.Children.Add(footer);
            DockPanel.SetDock(bottom, Dock.Bottom);
            layout.Children.Add(bottom);

            appsView = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Padding = new Thickness(25, 0, 25, 0), Content = grid, Focusable = false };
            layout.Children.Add(new Grid { Children = { appsView, settings.Root } });
            return root;
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
            tabs.Index = i;
            appsView.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            settings.Root.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            RenderAll();
        }

        void BuildCards()
        {
            Cards.Clear();
            grid.Children.Clear();
            foreach (var app in Catalog.Apps)
            {
                var c = new Card(app);
                Cards.Add(c);
                grid.Children.Add(c.Root);
            }
            if (DemoMode) Demo(); else foreach (var c in Cards) AppOps.Refresh(c.App, c.S);
        }

        Card Find(string id) { return Cards.Find(delegate(Card c) { return c.App.Id == id; }); }

        void RenderAll()
        {
            foreach (var c in Cards) c.Render(Install, Open, Find);
            if (page == 1) settings.Render();
            int updates = Cards.FindAll(delegate(Card c) { return c.S.UpdateAvailable; }).Count;
            string when = State.Checked == DateTime.MinValue ? "Обновления ещё не проверялись" : "Проверено " + State.Checked.ToLocalTime().ToString("d MMMM, HH:mm");
            string line;
            if (notice != null) line = notice;
            else if (checking) line = "Проверяю обновления…";
            else if (page == 1) line = server != null && server.Error != null ? "Связь между приложениями не работает: " + server.Error : "Связь между приложениями работает · подключено: " + connected;
            else line = (updates > 0 ? "Обновлений: " + updates + " · " : "") + when;
            footer.Text = line + " · версия " + Program.VersionText + " · dev.txt™";
            footer.Foreground = notice != null ? Ui.Solid(Tokens.Palette.Red) : Ui.Label3;
            checkBtn.Set("Проверить обновления", null, !checking, CheckAll);
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
                    connected = 0;
                    foreach (object o in data as System.Collections.IEnumerable ?? new object[0])
                    {
                        var info = o as Dictionary<string, object>;
                        if (info != null && Json.Str(info, "app") != "core") connected++;
                    }
                }
                if (page == 1 && (topic == "hub.apps" || topic.EndsWith(".settings"))) RenderAll();
            }
            else if (type == "result")
            {
                object ok;
                bool good = m.TryGetValue("ok", out ok) && ok is bool && (bool)ok;
                string error = Json.Str(m, "error");
                notice = good ? null : "Не получилось изменить настройку: " + (error == "offline" ? "приложение не запущено" : error == "timeout" ? "приложение не ответило" : error ?? "приложение отказало");
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

        // ───────── Трей ─────────
        void StartTray()
        {
            try
            {
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Открыть core.txt", null, delegate { ShowWindow(); });
                menu.Items.Add("Выход", null, delegate { Quit(); });
                tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    Text = "core.txt", ContextMenuStrip = menu, Visible = true
                };
                tray.MouseClick += delegate(object s, System.Windows.Forms.MouseEventArgs e) { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowWindow(); };
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
            foreach (var c in Cards) if (c.S.Phase == Phase.Idle) AppOps.Refresh(c.App, c.S);
            if (page == 0) RenderAll();
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
                    if (r != null) { State.Latest[app.Id] = r; c.S.Latest = r; any = true; }
                    if (c.S.Phase == Phase.Idle) c.S.Error = null;
                }
                catch { if (c.S.Phase == Phase.Idle && c.S.Latest == null) c.S.Error = "Не удалось узнать последнюю версию: нет связи с GitHub"; }
            }
            if (any) { State.Checked = DateTime.UtcNow; State.Save(); }
            checking = false;
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
                Action<double> progress = delegate(double p) { Dispatcher.BeginInvoke(new Action(delegate { c.S.Progress = p; if (c.S.Phase == Phase.Downloading) c.Render(Install, Open, Find); })); };
                string file = await Task.Run(delegate { return Releases.Download(r, progress); });
                c.S.Phase = Phase.Installing;
                RenderAll();
                await Task.Run(delegate { AppOps.Install(app, c.S, file); });
            }
            catch (System.Net.WebException) { c.S.Error = "Нет связи с GitHub"; }
            catch (Exception e) { c.S.Error = e.Message; }
            c.S.Phase = Phase.Idle;
            AppOps.Refresh(app, c.S);
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
            connected = 2;
            foreach (var c in Cards)
            {
                if (c.App.Id == "kapsula") { c.S.Path = "demo"; c.S.Version = new Version(0, 7, 5); c.S.Running = true; c.S.Latest = new Release { Version = new Version(0, 7, 6) }; }
                else if (c.App.Id == "aura") { c.S.Path = "demo"; c.S.Version = new Version(0, 26, 2); c.S.Running = true; }
                else if (c.App.Id == "maska") c.S.Path = "demo";
            }
            // То, что Kapsula присылает на самом деле (см. её CoreHub.cs), здесь — часть списка
            States["kapsula.settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""autoHide"",""type"":""switch"",""label"":""Автоскрытие"",""hint"":""Остров уезжает за край и возвращается, когда курсор у верхней кромки экрана или есть что показать"",""value"":false},{""key"":""seconds"",""type"":""switch"",""label"":""Секунды в часах"",""hint"":""В капсуле вместо 12:30 будет 12:30:45"",""value"":false},{""key"":""notifications"",""type"":""switch"",""label"":""Уведомления Windows на острове"",""hint"":""Новые уведомления других программ показываются карточкой. Нигде не сохраняются"",""value"":true},{""key"":""rim"",""type"":""switch"",""label"":""Светлая кромка"",""hint"":""Тонкий блик по краю острова — он не сливается с тёмными обоями и окнами"",""value"":true},{""key"":""shadow"",""type"":""switch"",""label"":""Тень под островом"",""hint"":""Мягкая тень: на светлых окнах остров словно парит над ними"",""value"":false}]},{""title"":""Зверёк"",""items"":[{""key"":""eyes"",""type"":""switch"",""label"":""Зверёк в капсуле"",""hint"":""Следит за курсором, пугается таймера, радуется удаче"",""value"":true},{""key"":""walk"",""type"":""switch"",""label"":""Зверёк гуляет по окнам"",""hint"":""Когда остров свёрнут и ничего не происходит, выходит посидеть на краю окна"",""value"":true}]}]}");
            // То, что Aura присылает на самом деле (см. её app.js, hubSettingsNow)
            States["aura.settings"] = Json.Parse(@"{""groups"":[{""title"":"""",""items"":[{""key"":""power"",""type"":""switch"",""label"":""Обработка голоса"",""hint"":""Включает и выключает Aura"",""value"":true},{""key"":""preset"",""type"":""choice"",""label"":""Готовый звук"",""value"":""studio"",""options"":[{""value"":""auto"",""label"":""Под вас""},{""value"":""studio"",""label"":""Подкаст""},{""value"":""radio"",""label"":""Эфир""},{""value"":""crystal"",""label"":""Стрим""},{""value"":""velvet"",""label"":""Бархат""},{""value"":""cinema"",""label"":""Кино""},{""value"":""whisper"",""label"":""Чистый""},{""value"":""custom"",""label"":""Свой""}]},{""key"":""mute"",""type"":""switch"",""label"":""Заглушить микрофон"",""hint"":""Тишина для всех программ"",""value"":false},{""key"":""auto"",""type"":""switch"",""label"":""Авто-уровень"",""hint"":""Выравнивает громкость, вытягивает тихий микрофон"",""value"":true},{""key"":""monitor"",""type"":""switch"",""label"":""Слышать себя"",""hint"":""Только в наушниках, иначе будет эхо"",""value"":false}]},{""title"":""Звук"",""items"":[{""key"":""nsEngine"",""type"":""choice"",""label"":""Шумодав"",""value"":""0"",""options"":[{""value"":""0"",""label"":""Обычный""},{""value"":""1"",""label"":""Жёсткий""}]},{""key"":""loudTarget"",""type"":""choice"",""label"":""Громкость на выходе"",""value"":""0"",""options"":[{""value"":""0"",""label"":""Не выравнивать""},{""value"":""-18"",""label"":""−18 LUFS""},{""value"":""-16"",""label"":""−16 LUFS""},{""value"":""-14"",""label"":""−14 LUFS""}]},{""key"":""dehum"",""type"":""switch"",""label"":""Убрать гул сети"",""hint"":""Вырезает 50 Гц и его гармоники — гудение от розетки и плохой земли"",""value"":false}]}]}");
        }

        // Рисует окно в файл PNG, не показывая его на экране. scroll — на сколько прокрутить страницу настроек.
        public void Shot(string file, double scroll)
        {
            var root = (FrameworkElement)Content;
            Content = null;
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            RenderAll();
            root.UpdateLayout();
            if (scroll > 0) { settings.Root.ScrollToVerticalOffset(scroll); root.UpdateLayout(); }
            var bmp = new RenderTargetBitmap((int)(W * 1.5), (int)(H * 1.5), 144, 144, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var f = System.IO.File.Create(file)) enc.Save(f);
        }
    }
}
