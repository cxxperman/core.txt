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
        public readonly AppStatus S = new AppStatus();
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

        public Card(AppEntry app)
        {
            App = app;
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
            tagline.TextWrapping = TextWrapping.Wrap; tagline.MaxHeight = 32; tagline.Margin = new Thickness(0, 2, 0, 0);
            var head = new StackPanel { Margin = new Thickness(14, 2, 0, 0), Children = { name, tagline } };
            var top = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(tile, Dock.Left);
            top.Children.Add(tile);
            top.Children.Add(head);

            var statusRow = status.Root;
            statusRow.Margin = new Thickness(0, 14, 0, 0);
            note = Ui.T("", Tokens.Size.Micro, Ui.Label3, FontWeights.Normal);
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(14, 3, 0, 0); note.MaxHeight = 28;

            bar = new Progress(app.Color);
            bar.Root.Margin = new Thickness(0, 10, 0, 0);
            bar.Root.Visibility = Visibility.Collapsed;

            Second.Root.Margin = new Thickness(0, 0, 8, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Children = { Second.Root, Main.Root } };

            // «Удалить» — тихая ссылка в углу: редкое действие не должно спорить с главной кнопкой
            removeLink = Ui.T("Удалить", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
            removeLink.HorizontalAlignment = HorizontalAlignment.Left; removeLink.VerticalAlignment = VerticalAlignment.Bottom;
            removeLink.Margin = new Thickness(0, 0, 0, 7); removeLink.Cursor = System.Windows.Input.Cursors.Hand; removeLink.Background = Brushes.Transparent;
            removeLink.MouseEnter += delegate { removeLink.Foreground = Ui.Label; };
            removeLink.MouseLeave += delegate { removeLink.Foreground = Ui.Label3; };
            removeLink.MouseLeftButtonUp += delegate { confirming = true; withData = false; if (Changed != null) Changed(); };

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

        static string V(Version v) { return v.Major + "." + v.Minor + "." + v.Build; }

        // Приводит карточку в соответствие с S. Действия кнопок задаёт окно.
        public void Render(Action<Card> install, Action<Card> open, Func<string, Card> find, Action<Card> connect, Func<string, bool> connected)
        {
            ShowIcon();
            Color dotColor = Tokens.Dark.Label3;
            string text, hint = "";
            bool busy = S.Phase != Phase.Idle;
            bar.Root.Visibility = S.Phase == Phase.Downloading ? Visibility.Visible : Visibility.Collapsed;
            bar.Value = S.Progress;
            Second.Hide();
            if (!S.Installed || busy) confirming = false;
            removeRow.Visibility = confirming && App.Data != null ? Visibility.Visible : Visibility.Collapsed;
            removeLink.Visibility = S.Installed && !busy && !confirming && App.Removable ? Visibility.Visible : Visibility.Collapsed;

            if (S.Phase == Phase.Removing)
            {
                text = "Удаляется…";
                dotColor = Tokens.Palette.Red;
                Main.Set("Удаление…", null, false, null);
            }
            else if (confirming)
            {
                text = "Удалить " + App.Name.ToLowerInvariant() + ".txt с этого компьютера?";
                dotColor = Tokens.Palette.Red;
                if (App.Data == null) hint = App.IsBundle ? "Папки темы и плагина будут убраны из Steam" : "Настройки останутся на месте";
                Main.Set("Удалить", Tokens.Palette.Red, true, delegate { bool data = withData; confirming = false; if (Remove != null) Remove(this, data); });
                Second.Set("Отмена", null, true, delegate { confirming = false; if (Changed != null) Changed(); });
            }
            else if (App.Soon && !S.Installed)
            {
                // Ещё не выпущено: честно говорим об этом и не показываем кнопку, которая не сработает
                text = "В разработке";
                hint = App.IsIntegration ? "" : "Появится здесь, когда выйдет первая версия";
                Main.Hide();
            }
            else if (App.IsIntegration)
            {
                Card via = App.Via != null ? find(App.Via) : null;
                text = S.Installed ? "Подключено" : "Не подключено";
                if (S.Installed) dotColor = Tokens.Palette.Green;
                hint = S.Installed ? "" : App.Hint;
                // Подключить можно прямо отсюда, если приложение, которое это делает, сейчас на связи
                if (!S.Installed && App.Connect != null && via != null && connected(via.App.Id)) Main.Set("Подключить", App.Color, true, delegate { connect(this); });
                else if (via != null && via.S.Installed) Main.Set("Открыть " + via.App.Name.ToLowerInvariant() + ".txt", null, true, delegate { open(via); });
                else if (via != null) Main.Set("Нужна " + via.App.Name.ToLowerInvariant() + ".txt", null, false, null);
                else Main.Hide();
            }
            else if (S.Phase == Phase.Downloading)
            {
                text = "Скачивается — " + (int)Math.Round(S.Progress * 100) + "%";
                dotColor = App.Color;
                Main.Set(S.Installed ? "Обновление…" : "Установка…", null, false, null);
            }
            else if (S.Phase == Phase.Installing)
            {
                text = "Устанавливается…";
                dotColor = App.Color;
                Main.Set(S.Installed ? "Обновление…" : "Установка…", null, false, null);
            }
            else if (!S.Installed)
            {
                text = S.Latest != null ? "Не установлено · версия " + V(S.Latest.Version) : "Не установлено";
                Main.Set("Установить", App.Color, true, delegate { install(this); });
            }
            else
            {
                text = (S.Version != null ? "Версия " + V(S.Version) : "Установлено") + (S.Running ? " · запущено" : "");
                if (!App.HasExe) dotColor = Tokens.Palette.Green;
                if (S.Running) dotColor = Tokens.Palette.Green;
                if (S.UpdateAvailable)
                {
                    hint = "Есть обновление " + V(S.Latest.Version) + (S.Running ? " — приложение перезапустится" : "");
                    dotColor = Tokens.Palette.Orange;
                    Main.Set("Обновить", App.Color, true, delegate { install(this); });
                    if (App.HasExe) Second.Set("Открыть", null, true, delegate { open(this); });
                }
                else if (App.HasExe) Main.Set("Открыть", null, true, delegate { open(this); });
                else { Main.Hide(); hint = App.Hint; }   // набор: открывать нечего, зато есть что сказать, как его включить
            }

            if (S.Error != null && !busy) { hint = S.Error; note.Foreground = Ui.Solid(Tokens.Palette.Red); }
            else note.Foreground = Ui.Label3;
            status.Set(text, dotColor);
            note.Text = hint;
            note.Visibility = hint == "" ? Visibility.Collapsed : Visibility.Visible;
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
