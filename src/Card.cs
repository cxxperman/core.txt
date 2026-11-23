// core.txt. Card.cs — карточка одного приложения и значок из его файла.
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
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DevTxt.Core
{
    // Карточка одного приложения
    class Card
    {
        public readonly AppEntry App;
        public readonly AppStatus S;
        public readonly Border Root;
        public readonly Btn Main = new Btn(), Second = new Btn();
        readonly Border tile;
        readonly TextBlock letter, note;
        readonly Image icon;
        readonly Status status = new Status();
        readonly Progress bar;
        string iconPath;
        // Удаление: сначала вопрос прямо на карточке, с выбором — стирать ли настройки
        public Action<Card, bool> Remove;
        public Action Changed;   // карточку надо перерисовать
        readonly TextBlock removeLink;
        readonly StackPanel removeRow;
        bool confirming, withData;
        // Maska: на нажатой кнопке было написано «Закрыть Discord и …» — значит, Discord закрываем и потом запускаем снова
        public bool CloseFirst;
        // Что было в прошлый раз — чтобы отметить движением то, что изменилось
        bool drawn, wasBusy, wasInstalled;
        string wasError;

        public Card(AppEntry app) : this(app, null) { }

        // status — состояние прежней карточки этого же приложения: каталог обновился, а что известно о программе — осталось
        public Card(AppEntry app, AppStatus known)
        {
            App = app;
            S = known ?? new AppStatus();
            letter = new TextBlock { Text = app.Name.Substring(0, 1).ToUpperInvariant(), FontSize = 24, FontWeight = FontWeights.Bold, FontFamily = Ui.DisplayFont, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon = new Image { Width = 40, Height = 40, Visibility = Visibility.Collapsed };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            tile = Ui.Tile(new Grid { Children = { letter, icon } });
            tile.VerticalAlignment = VerticalAlignment.Top;
            tile.Background = Ui.Tinted(app.Color);
            if (app.Sprite != null)
            {
                // Свой пиксельный значок из каталога — он главнее значка из файла программы
                ((Grid)tile.Child).Children.Add(Ui.Sprite(app.Sprite, 36));
                letter.Visibility = Visibility.Collapsed;
                tile.Background = Ui.Fill;
            }

            var name = Ui.Brand(app.Name, Tokens.Size.Title, FontWeights.SemiBold);
            var tagline = Ui.T(app.Tagline, Tokens.Size.Caption, Ui.Label2, FontWeights.Normal);
            tagline.TextWrapping = TextWrapping.Wrap; tagline.TextTrimming = TextTrimming.CharacterEllipsis; tagline.MaxHeight = 32; tagline.Margin = new Thickness(0, 2, 0, 0);
            if (app.Tagline.Length > 60) tagline.ToolTip = app.Tagline;   // длинная подпись обрезается — целиком она в подсказке
            var head = new StackPanel { Margin = new Thickness(14, 2, 0, 0), Children = { name, tagline } };
            var top = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(tile, Dock.Left);
            top.Children.Add(tile);
            top.Children.Add(head);

            var statusRow = status.Root;
            statusRow.Margin = new Thickness(0, 14, 0, 0);
            note = Ui.T("", Tokens.Size.Micro, Ui.Label3, FontWeights.Normal);
            note.TextWrapping = TextWrapping.Wrap; note.TextTrimming = TextTrimming.CharacterEllipsis; note.Margin = new Thickness(14, 3, 0, 0); note.MaxHeight = 28;

            bar = new Progress(app.Color);
            bar.Root.Margin = new Thickness(0, 10, 0, 0);
            bar.Root.Visibility = Visibility.Collapsed;

            Second.Root.Margin = new Thickness(0, 0, 8, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Children = { Second.Root, Main.Root } };

            // «Удалить» — тихая ссылка в углу: редкое действие не должно спорить с главной кнопкой.
            // У подключения, которое core.txt делает сам (Maska), на её месте — «Отключить», с тем же вопросом
            removeLink = Ui.T(app.IsConnector ? "Отключить" : "Удалить", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
            removeLink.HorizontalAlignment = HorizontalAlignment.Left; removeLink.VerticalAlignment = VerticalAlignment.Bottom;
            removeLink.Margin = new Thickness(0, 0, 0, 7); removeLink.Cursor = System.Windows.Input.Cursors.Hand; removeLink.Background = Brushes.Transparent;
            removeLink.MouseEnter += delegate { removeLink.Foreground = Ui.Label; };
            removeLink.MouseLeave += delegate { removeLink.Foreground = Ui.Label3; };
            Action ask = delegate
            {
                confirming = true; withData = false;
                if (Changed != null) Changed();
                // Вопрос задан — фокус на «Отмена»: Enter ничего не удалит, Esc закроет вопрос
                Second.Root.Dispatcher.BeginInvoke(new Action(delegate { if (confirming) Second.Root.Focus(); }), DispatcherPriority.Input);
            };
            removeLink.MouseLeftButtonUp += delegate { ask(); };
            Ui.Keyboard(removeLink, ask);

            var dataLabel = Ui.T("Стереть и настройки", Tokens.Size.Caption, Ui.Label2, FontWeights.Normal);
            dataLabel.VerticalAlignment = VerticalAlignment.Center; dataLabel.Margin = new Thickness(10, 0, 0, 0);
            var dataSwitch = new Switch(false, delegate(bool on) { withData = on; });
            removeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, Children = { dataSwitch.Root, dataLabel } };
            removeRow.IsVisibleChanged += delegate { if (!removeRow.IsVisible) dataSwitch.Value = false; };

            var body = new DockPanel { LastChildFill = true };
            foreach (FrameworkElement e in new FrameworkElement[] { top, statusRow, note, removeRow, bar.Root }) { DockPanel.SetDock(e, Dock.Top); body.Children.Add(e); }
            body.Children.Add(new Grid { Children = { removeLink, buttons } });

            Root = Ui.Block(body, true);
            Root.Padding = new Thickness(18, 18, 18, 16);
            Root.Margin = new Thickness(7);
            Root.Height = 190;
        }

        void ShowIcon()
        {
            if (App.Sprite != null) return;
            string path = App.IsIntegration ? null : S.Path;
            if (path == iconPath) return;
            iconPath = path;
            ImageSource img = path != null ? Icons.From(path) : null;
            icon.Source = img;
            icon.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
            letter.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
            tile.Background = img != null ? Ui.Fill : Ui.Tinted(App.Color);
        }

        // Показать вопрос об удалении (для снимков)
        public void AskRemove() { confirming = true; }

        // Esc: убрать вопрос об удалении. true — вопрос был
        public bool CancelRemove()
        {
            if (!confirming) return false;
            confirming = false;
            if (Changed != null) Changed();
            return true;
        }

        // Кнопка «Открыть»: пока приложение запускается, она это и говорит и второй раз не нажимается
        static void OpenButton(Btn b, Card target, string label, Action<Card> open)
        {
            if (target.S.Launching) b.Set("Запускается…", null, false, null);
            else b.Set(label, null, true, delegate { open(target); });
        }

        static string V(Version v) { return v.Major + "." + v.Minor + "." + v.Build; }

        // Приводит карточку в соответствие с S. Действия кнопок задаёт окно.
        public void Render(Action<Card> install, Action<Card> open, Func<string, Card> find, Action<Card> connect, Func<string, bool> connected)
        {
            ShowIcon();
            Color dotColor = (Ui.Light ? Tokens.Light.Label3 : Tokens.Dark.Label3);   // по теме Windows   // audit:ok — цвет выбран по теме
            string text, hint = "";
            bool busy = S.Phase != Phase.Idle;
            // Полоса: при скачивании считает, при установке и удалении — бегает
            Motion.Show(bar.Root, busy);
            bar.Busy = busy && S.Phase != Phase.Downloading;
            if (S.Phase == Phase.Downloading) bar.Value = S.Progress;
            status.Busy = busy;
            Second.Hide();
            if (!S.Installed || busy) confirming = false;
            Motion.Show(removeRow, confirming && App.Data != null);
            removeLink.Visibility = S.Installed && !busy && !confirming && (App.Removable || App.IsConnector) ? Visibility.Visible : Visibility.Collapsed;
            // Discord закрываем только по кнопке, на которой так и написано: что на ней сейчас, то и сделаем
            bool closeDiscord = App.IsConnector && S.Running;
            // Набор для Steam — так же: Steam запущен — на кнопке сказано, что он закроется (и потом запустится снова)
            bool closeSteam = App.IsBundle && S.Running;

            if (S.Phase == Phase.Removing)
            {
                text = App.IsBundle && S.Note != null ? S.Note : App.IsConnector ? "Отключается…" : "Удаляется…";   // у набора сначала «Закрывается Steam…»
                dotColor = Tokens.Palette.Red;
                Main.Set(App.IsConnector ? "Отключение…" : "Удаление…", null, false, null);
            }
            else if (confirming && App.IsConnector)
            {
                text = "Отключить " + App.Name.ToLowerInvariant() + ".txt от Discord?";
                dotColor = Tokens.Palette.Red;
                hint = "Discord вернётся к тому, что было до подключения";
                Main.Set(closeDiscord ? "Закрыть Discord и отключить" : "Отключить", Tokens.Palette.Red, true, delegate { confirming = false; CloseFirst = closeDiscord; if (Remove != null) Remove(this, false); });
                Second.Set("Отмена", null, true, delegate { confirming = false; if (Changed != null) Changed(); });
            }
            else if (confirming)
            {
                text = "Удалить " + App.Name.ToLowerInvariant() + ".txt с этого компьютера?";
                dotColor = Tokens.Palette.Red;
                if (App.Data == null) hint = App.IsBundle ? "Тема и плагин уйдут из Steam, вернётся прежняя тема" : "Настройки останутся на месте";
                Main.Set(closeSteam ? "Закрыть Steam и удалить" : "Удалить", Tokens.Palette.Red, true, delegate { bool data = withData; confirming = false; CloseFirst = closeSteam; if (Remove != null) Remove(this, data); });
                Second.Set("Отмена", null, true, delegate { confirming = false; if (Changed != null) Changed(); });
            }
            else if (App.Soon && !S.Installed)
            {
                // Ещё не выпущено: честно говорим об этом и не показываем кнопку, которая не сработает
                text = "В разработке";
                hint = App.IsIntegration ? "" : "Появится здесь, когда выйдет первая версия";
                Main.Hide();
            }
            else if (App.IsConnector)
            {
                // Maska: core.txt подключает её к Discord сам (DiscordInstall)
                if (S.Phase == Phase.Installing)
                {
                    text = "Подключается…";
                    dotColor = App.Color;
                    Main.Set("Подключение…", null, false, null);
                }
                else
                {
                    Action go = delegate { CloseFirst = closeDiscord; connect(this); };
                    if (!S.Installed)
                    {
                        text = "Не подключено";
                        hint = S.Note ?? (closeDiscord ? "Discord закроется и запустится снова" : "Ставит в Discord сборку Vencord с нашим плагином");
                        Main.Set(closeDiscord ? "Закрыть Discord и подключить" : "Подключить", Ui.Accent, true, go);
                    }
                    else if (S.Lost)
                    {
                        // Отметка есть, а плагина в сборке нет: Vencord, видимо, переустановили — ставим нашу поверх
                        text = "Плагин пропал из Discord";
                        dotColor = Tokens.Palette.Orange;
                        hint = "Видимо, Vencord переустановили";
                        Main.Set(closeDiscord ? "Закрыть Discord и подключить" : "Подключить заново", Ui.Accent, true, go);
                    }
                    else
                    {
                        text = "Подключено";
                        dotColor = Tokens.Palette.Green;
                        hint = S.Note ?? "";
                        Main.Hide();
                    }
                }
            }
            else if (App.IsIntegration)
            {
                Card via = App.Via != null ? find(App.Via) : null;
                text = S.Installed ? "Подключено" : "Не подключено";
                if (S.Installed) dotColor = Tokens.Palette.Green;
                hint = S.Installed ? "" : App.Hint;
                // Подключить можно прямо отсюда, если приложение, которое это делает, сейчас на связи
                if (!S.Installed && App.Connect != null && via != null && connected(via.App.Id)) Main.Set("Подключить", Ui.Accent, true, delegate { connect(this); });
                else if (via != null && via.S.Installed) OpenButton(Main, via, "Открыть " + via.App.Name.ToLowerInvariant() + ".txt", open);
                else if (via != null) Main.Set("Нужна " + via.App.Name.ToLowerInvariant() + ".txt", null, false, null);
                else Main.Hide();
            }
            else if (S.Phase == Phase.Downloading)
            {
                text = "Скачивается — " + (S.Total > 0 ? Releases.Megabytes(S.Done, S.Total) : (int)Math.Round(S.Progress * 100) + "%");
                dotColor = App.Color;
                Main.Set(S.Installed ? "Обновление…" : "Установка…", null, false, null);
            }
            else if (S.Phase == Phase.Installing)
            {
                text = App.IsBundle && S.Note != null ? S.Note : "Устанавливается…";
                dotColor = App.Color;
                Main.Set(S.Installed ? "Обновление…" : "Установка…", null, false, null);
            }
            else if (!S.Installed)
            {
                text = S.Latest != null ? "Не установлено · версия " + V(S.Latest.Version) : "Не установлено";
                if (App.IsBundle && S.NoHost)
                {
                    // Без Millennium ставить некуда: говорим, что нужно, и даём открыть его сайт — ставит его человек сам
                    hint = App.RequiresHint ?? "";
                    Second.Set("Открыть steambrew.app", null, true, delegate { Steam.OpenMillenniumSite(); });
                }
                else if (App.IsBundle) hint = S.Note ?? (closeSteam ? "Steam закроется и запустится снова" : "");
                Main.Set(closeSteam ? "Закрыть Steam и установить" : "Установить", Ui.Accent, true, delegate { CloseFirst = closeSteam; install(this); });
            }
            else
            {
                text = (S.Version != null ? "Версия " + V(S.Version) : "Установлено") + (S.Running && App.HasExe ? " · запущено" : S.Launching ? " · запускается…" : "");
                if (!App.HasExe) dotColor = Tokens.Palette.Green;
                if (S.Running) dotColor = Tokens.Palette.Green;
                if (S.UpdateAvailable)
                {
                    hint = "Есть обновление " + V(S.Latest.Version) + (S.Running ? (App.IsBundle ? " — Steam перезапустится" : " — приложение перезапустится") : "");
                    dotColor = Tokens.Palette.Orange;
                    Main.Set(closeSteam ? "Закрыть Steam и обновить" : "Обновить", Ui.Accent, true, delegate { CloseFirst = closeSteam; install(this); });
                    if (App.HasExe) OpenButton(Second, this, "Открыть", open);
                }
                else if (App.HasExe) OpenButton(Main, this, "Открыть", open);
                else { Main.Hide(); hint = S.Note ?? App.Hint; }   // набор: открывать нечего — чем кончилась установка, а потом подсказка из каталога
            }

            bool failed = S.Error != null && !busy;
            if (failed) { hint = S.Error; note.Foreground = Ui.Solid(Tokens.Palette.Red); }
            else note.Foreground = Ui.Label3;
            status.Set(text, dotColor);
            if (hint != "") Motion.Swap(note, hint);
            // Строка под состоянием короткая и может обрезаться — целиком она в подсказке, у ошибки ещё и с исходным текстом
            string full = hint == "" ? null : failed && S.ErrorDetail != null ? hint + "\n" + S.ErrorDetail : hint.Length > 60 || failed ? hint : null;
            if (!Equals(note.ToolTip, full)) note.ToolTip = full;
            Motion.Show(note, hint != "");

            // Движением отмечаем то, что случилось с прошлого раза: работа началась, получилось, не получилось
            string error = busy ? null : S.Error;
            if (drawn)
            {
                if (busy && !wasBusy) Motion.Breathe(tile, true);
                else if (!busy && wasBusy)
                {
                    Motion.Breathe(tile, false);   // работа кончилась — значок перестаёт «дышать», и после удачи тоже
                    if (error == null) { Motion.Pop(tile); Motion.Glow(Root, S.Installed ? App.Color : (Ui.Light ? Tokens.Light.Label3 : Tokens.Dark.Label3)); }   // audit:ok — цвет выбран по теме
                }
                if (error != null && error != wasError) Motion.Shake(Root);
                else if (!busy && !wasBusy && S.Installed != wasInstalled) Motion.Pop(tile);
            }
            drawn = true; wasBusy = busy; wasInstalled = S.Installed; wasError = error;
        }
    }

    static class Icons
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        // Значок программы из её файла, крупный — чтобы не расплывался
        public static ImageSource From(string exe)
        {
            try
            {
                var h = new IntPtr[1];
                if (PrivateExtractIcons(exe, 0, 128, 128, h, new uint[1], 1, 0) == 0 || h[0] == IntPtr.Zero) return null;
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    return src;
                }
                finally { DestroyIcon(h[0]); }
            }
            catch { return null; }
        }
    }
}
