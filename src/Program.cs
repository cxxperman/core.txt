// core.txt — лаунчер dev.txt: одно окно для всех приложений: поставить, обновить, запустить.
// Ключи для проверки:
//   --demo              выдуманные состояния приложений; сеть и диск не трогаются
//   --shot файл.png     нарисовать окно в файл и выйти (обычно вместе с --demo)
//   --motion папка      вместе с --demo: показать окно, проиграть все действия и сложить кадры движения в папку
//   --page имя          открыть сразу страницу: scenarios, settings, welcome или раздел настроек — general, games, keys,
//                       replace, app:<приложение>; --scroll N — прокрутить её (для снимков)
//   --menu              вместе с --demo --shot: нарисовать меню значка в трее вместо окна
//   --tray              запуститься без окна, только значок в трее и хаб
//   --selftest          проверить разбор каталога, ответа GitHub и замену файла; код выхода 0 — всё в порядке.
//                       Настоящие настройки core.txt (%APPDATA%\dev.txt\core) не трогает: работает во временной папке
//   --hub порт          запустить только хаб на этом порту, без окна (для проверок из kit.txt)
//   --uninstall имя     удалить приложение без окна; настройки остаются
//   --install имя       поставить или обновить приложение из каталога без окна и напечатать, чем кончилось
//   --latest            спросить у GitHub последние версии приложений из каталога и напечатать их
//   --bench             замерить, сколько стоит проверка приложений (диск и процессы) на этом компьютере
//   --fetch адрес сумма размер   скачать файл с проверкой размера и SHA-256 (для проверки на своём компьютере)
//   --range адрес начало длина   скачать кусок файла запросом Range и напечатать его SHA-256
//   --patch адрес сумма размер файл   собрать файл релиза по картам блоков: «прежний» — испорченная копия файла,
//                       недостающее — запросами Range (карты строятся на месте; настоящих настроек и запаса не трогает)
using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace DevTxt.Core
{
    static class Program
    {
        public static string VersionText
        {
            get { Version v = typeof(Program).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2: без него GitHub не отвечает
            bool demo = Array.IndexOf(args, "--demo") >= 0;
            string shot = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--shot") shot = args[i + 1];
            if (Array.IndexOf(args, "--selftest") >= 0) return SelfTest.Run();
            if (Array.IndexOf(args, "--latest") >= 0) return SelfTest.Latest();
            if (Array.IndexOf(args, "--bench") >= 0) return Bench.Run();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--install") return SelfTest.InstallNow(args[i + 1]);
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--uninstall") return SelfTest.UninstallNow(args[i + 1]);
            for (int i = 0; i + 1 < args.Length; i++)
                if (args[i] == "--hub")
                {
                    // Только хаб, без окна и без самого core.txt в списке приложений — для проверок протокола
                    var only = new HubServer(new Hub(null, false) { Version = VersionText });
                    if (!only.Start(int.Parse(args[i + 1]))) return 1;
                    Thread.Sleep(Timeout.Infinite);
                }
            if (args.Length == 4 && args[0] == "--fetch") return SelfTest.Fetch(args[1], args[2], args[3]);
            if (args.Length == 4 && args[0] == "--range") return SelfTest.RangeNow(args[1], args[2], args[3]);
            if (args.Length == 5 && args[0] == "--patch") return SelfTest.PatchNow(args[1], args[2], args[3], args[4]);

            int page = 0;
            string section = null;   // раздел настроек
            double scroll = 0;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--page")
                {
                    string p = args[i + 1];
                    if (p == "scenarios") page = 1;
                    else if (p == "settings") page = 2;
                    else if (p == "welcome") page = 3;
                    else if (p == "general" || p == "games" || p == "keys" || p == "replace" || CoreFamily.ShowPage(Hub.Msg("page", p)) == p && p.StartsWith("app:")) { page = 2; section = p; }
                }
                if (args[i] == "--scroll") double.TryParse(args[i + 1], out scroll);
            }

            bool created = true;
            Mutex mutex = null;
            string motion = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--motion" && demo) motion = args[i + 1];
            if (shot == null && motion == null) mutex = new Mutex(true, "devtxt_core_single_instance", out created);
            bool handover = Array.IndexOf(args, "--after-update") >= 0 || Array.IndexOf(args, "--after-install") >= 0;
            if (!created && handover)
            {
                // Нас запустила прежняя копия (обновление или установка) и сейчас закроется — ждём её
                try { created = mutex.WaitOne(15000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                // Уже запущен (возможно, сидит в трее): просим показать окно и выходим. Нас запустил человек — значит,
                // нам Windows разрешает вывести окно вперёд; делимся этим с той копией, иначе окно останется позади
                MainWindow.LetForeground();
                try { EventWaitHandle.OpenExisting("devtxt_core_show").Set(); } catch { }
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Ui.Styles(app.Resources);   // тёмные подсказки и тонкие полосы прокрутки, как во всех приложениях dev.txt
            UiContext();
            if (!demo) { State.Load(); SelfUpdate.Cleanup(); }
            else State.Frozen = true;   // демо и снимки ничего не записывают в настройки
            Catalog.Load();
            var w = new MainWindow(demo, page, section, Array.IndexOf(args, "--after-install") >= 0);
            if (shot != null)
            {
                if (Array.IndexOf(args, "--menu") >= 0) w.MenuShot(shot); else w.Shot(shot, scroll);
                return 0;
            }
            if (motion != null)
            {
                int code = 0;
                app.DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e) { Console.Error.WriteLine(e.Exception); code = 1; e.Handled = true; app.Shutdown(); };
                w.MotionTest(motion, delegate { app.Shutdown(); });
                app.Run();
                return code;
            }

            var show = new EventWaitHandle(false, EventResetMode.AutoReset, "devtxt_core_show");
            new Thread(delegate() { while (true) { show.WaitOne(); w.Dispatcher.BeginInvoke(new Action(w.ShowWindow)); } }) { IsBackground = true }.Start();
            w.Closed += delegate { app.Shutdown(); };   // окно закрыли совсем, а не спрятали в трей
            if (Array.IndexOf(args, "--tray") < 0) w.Show();   // --tray — запуск вместе с Windows: тихо, без окна
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        // Контекст WPF для потока окна — ещё до app.Run(), который ставит его только на время своей работы. Окно
        // строится раньше, и async-методы, начатые в его конструкторе (проверка обновлений при запуске, установка
        // выбранного на первом запуске), без этого продолжались бы в пуле потоков и роняли core.txt на первом же
        // обращении к окну (InvalidOperationException из Dispatcher.VerifyAccess в CheckAll).
        internal static void UiContext()
        {
            if (!(SynchronizationContext.Current is DispatcherSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        }
    }

    static class SelfTest
    {
        static int failed;
        static void Check(bool ok, string what) { if (!ok) { failed++; Console.Error.WriteLine("FAIL  " + what); } }

        // Проверки пишут State (пути, найденные версии): State.Save() в установке, переносе и удалении. Всё это — только
        // во временную папку. Раньше проверка писала в настоящий state.json, и работающий core.txt после перезапуска
        // открылся бы как в первый раз.
        public static int Run()
        {
            string real = State.FilePath, before = ReadText(real);
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-state-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            State.DirOverride = temp;
            try { Checks(); }
            finally { State.DirOverride = null; }
            // Настоящий файл мог поменять только работающий core.txt — но не эта проверка (её следы — пути «selftest-…»)
            string after = ReadText(real);
            Check(after == before || after == null || after.IndexOf("selftest-", StringComparison.Ordinal) < 0, "the self-test leaves the real state.json of core.txt alone");
            try { System.IO.Directory.Delete(temp, true); } catch { }
            Console.WriteLine(failed == 0 ? "all passed" : failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        static string ReadText(string path)
        {
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; } catch { return null; }
        }

        // Продолжение после await — в каком потоке оно случилось
        static async void AfterAwait(Action<int> done)
        {
            await Task.Delay(20);   // как Task.Run в CheckAll: к await задача ещё не готова
            done(Thread.CurrentThread.ManagedThreadId);
        }

        // Maska: подключение к Discord — только на поддельных папках во временной директории. Настоящие %APPDATA%\Vencord
        // и Discord не трогаются: папки подменяются переменными CORE_TEST_*, а запущенный Discord на них не смотрится
        static void DiscordChecks()
        {
            var cat = Catalog.Read(@"{ ""apps"": [
                { ""id"": ""m"", ""name"": ""M"", ""kind"": ""integration"", ""marker"": ""%APPDATA%\\Vencord\\dist\\kapsula.txt"", ""connector"": ""discord"" },
                { ""id"": ""f"", ""name"": ""F"", ""kind"": ""integration"", ""marker"": ""%APPDATA%\\x.txt"", ""connector"": ""telegram"", ""via"": ""kapsula"", ""connect"": ""x.connect"" },
                { ""id"": ""p"", ""name"": ""P"", ""kind"": ""portable"", ""repo"": ""cxxperman/p"", ""asset"": ""P.exe"", ""exe"": ""P.exe"", ""connector"": ""discord"" }
            ] }");
            Check(cat.Count == 3 && cat[0].IsConnector && cat[0].Connector == "discord" && cat[0].Via == null, "an integration with a known connector needs no via");
            Check(cat.Count == 3 && !cat[1].IsConnector && cat[1].Connector == null && cat[1].IsIntegration && cat[1].Via == "kapsula" && cat[1].Connect == "x.connect", "an unknown connector is ignored, the entry stays an ordinary integration");
            Check(cat.Count == 3 && !cat[2].IsConnector, "only an integration can be a connector");
            AppEntry maska = Catalog.Apps.Find(delegate(AppEntry a) { return a.Id == "maska"; });
            Check(maska != null && maska.IsConnector && maska.Via == null && maska.Connect == null && maska.Marker == "%APPDATA%\\Vencord\\dist\\kapsula.txt", "the built-in catalogue connects Maska from core.txt, with the old marker");

            string pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-discord-" + pid);
            string roam = System.IO.Path.Combine(tmp, "Roaming"), loc = System.IO.Path.Combine(tmp, "Local"), build = System.IO.Path.Combine(tmp, "build");
            string res = System.IO.Path.Combine(loc, "Discord", "app-1.0.9268", "resources"), older = System.IO.Path.Combine(loc, "Discord", "app-1.0.9100", "resources");
            string dist = System.IO.Path.Combine(roam, "Vencord", "dist"), bak = System.IO.Path.Combine(roam, "Vencord", "dist-before-kapsula");
            string[] files = { "patcher.js", "preload.js", "renderer.js", "renderer.css" };
            Func<string, string> read = delegate(string p) { return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p).Trim() : null; };
            Action<string, string> fill = delegate(string dir, string text)
            {
                System.IO.Directory.CreateDirectory(dir);
                foreach (string f in files) System.IO.File.WriteAllText(System.IO.Path.Combine(dir, f), text);
            };
            Func<bool> discordAsWas = delegate { return read(System.IO.Path.Combine(res, "app.asar")) == "discord" && !System.IO.File.Exists(System.IO.Path.Combine(res, "_app.asar")); };
            var entry = new AppEntry { Id = "maska", Name = "Maska", Kind = "integration", Marker = "x", Connector = "discord" };
            try
            {
                System.IO.Directory.CreateDirectory(res); System.IO.Directory.CreateDirectory(older);
                string ourBuild = "// \"Maska\" build" + new string(' ', 120);
                fill(build, ourBuild);
                System.IO.File.WriteAllText(System.IO.Path.Combine(res, "app.asar"), "discord");
                Environment.SetEnvironmentVariable(DiscordInstall.TestRoaming, roam);
                Environment.SetEnvironmentVariable(DiscordInstall.TestLocal, loc);
                Environment.SetEnvironmentVariable(DiscordInstall.TestBuild, build);
                Check(DiscordInstall.Marker.StartsWith(tmp, StringComparison.OrdinalIgnoreCase), "discord: the checks work in fake folders only");
                Check(!DiscordInstall.Running(), "discord: on fake folders the real Discord does not count");

                // «Наша ли сборка»: по строке "Maska" или, у сборок kapsula.txt, "Kapsula"
                string b1 = System.IO.Path.Combine(tmp, "b1"), b2 = System.IO.Path.Combine(tmp, "b2"), b3 = System.IO.Path.Combine(tmp, "b3");
                fill(b1, "x \"Maska\" y"); fill(b2, "x \"Kapsula\" y"); fill(b3, "x Maska Kapsula y");
                Check(DiscordInstall.OurBuild(b1) && DiscordInstall.OurBuild(b2) && !DiscordInstall.OurBuild(b3) && !DiscordInstall.OurBuild(System.IO.Path.Combine(tmp, "none")), "discord: our build is told by \"Maska\" or \"Kapsula\" in quotes");

                // 1) Vencord не было: core.txt встраивает сам — исходный app.asar → _app.asar, на его месте заглушка
                Check(DiscordInstall.Install() == null, "discord: connect without Vencord");
                Check(read(System.IO.Path.Combine(dist, "renderer.js")) == ourBuild.Trim(), "discord: the build is in place");
                Check(read(System.IO.Path.Combine(res, "_app.asar")) == "discord", "discord: the original app.asar is kept as _app.asar");
                byte[] stub = System.IO.File.ReadAllBytes(System.IO.Path.Combine(res, "app.asar"));
                Check(stub.Length > 16 && BitConverter.ToUInt32(stub, 0) == 4 && DiscordInstall.IsStub(System.IO.Path.Combine(res, "app.asar")) && System.Text.Encoding.UTF8.GetString(stub).Contains("Vencord\\\\dist\\\\patcher.js"), "discord: app.asar is the Vencord stub that loads patcher.js");
                Check(!System.IO.File.Exists(System.IO.Path.Combine(older, "_app.asar")), "discord: an older Discord version is left alone");
                string[] mark = System.IO.File.ReadAllLines(DiscordInstall.Marker);
                Check(mark.Length >= 2 && mark[0].StartsWith("core.txt · ") && mark[1] == "vencord=kapsula" && Array.IndexOf(mark, "injected=" + res) > 0, "discord: the marker keeps the kapsula.txt format, vencord=kapsula");
                var st = new AppStatus();
                AppOps.Refresh(entry, st);
                Check(st.Installed && !st.Lost && !st.Running, "discord: the card sees it connected");
                Check(DiscordInstall.Uninstall() == null, "discord: disconnect");
                Check(discordAsWas() && !System.IO.File.Exists(System.IO.Path.Combine(dist, "renderer.js")) && !DiscordInstall.Installed && !System.IO.Directory.Exists(bak), "discord: after disconnect Discord is as it was and our build is gone");
                AppOps.Refresh(entry, st);
                Check(!st.Installed && !st.Lost, "discord: the card sees it disconnected");

                // 2) Чужой Vencord уже стоит: меняются только его файлы, прежние — в резерв и обратно
                System.IO.File.Move(System.IO.Path.Combine(res, "app.asar"), System.IO.Path.Combine(res, "_app.asar"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(res, "app.asar"), "vencord stub");
                fill(dist, "official vencord");
                Check(DiscordInstall.Install() == null, "discord: connect over Vencord");
                Check(read(System.IO.Path.Combine(res, "app.asar")) == "vencord stub", "discord: Discord itself is not touched");
                Check(read(System.IO.Path.Combine(bak, "renderer.js")) == "official vencord", "discord: the previous Vencord is in dist-before-kapsula");
                Check(System.IO.File.ReadAllText(DiscordInstall.Marker).Contains("vencord=before"), "discord: the marker says vencord=before");
                // Vencord переустановили — плагин пропал, карточка это видит; «Подключить заново» резерв не портит
                System.IO.File.WriteAllText(System.IO.Path.Combine(dist, "renderer.js"), "official vencord again");
                AppOps.Refresh(entry, st);
                Check(st.Installed && st.Lost, "discord: a plugin gone from the build is noticed");
                Check(DiscordInstall.Install() == null && DiscordInstall.PluginInBuild, "discord: connect again");
                Check(read(System.IO.Path.Combine(bak, "renderer.js")) == "official vencord", "discord: connecting again keeps the backup");
                Check(DiscordInstall.Uninstall() == null, "discord: disconnect from Vencord");
                Check(read(System.IO.Path.Combine(dist, "renderer.js")) == "official vencord" && !System.IO.Directory.Exists(bak) && !DiscordInstall.Installed && read(System.IO.Path.Combine(res, "app.asar")) == "vencord stub", "discord: the official Vencord is back");

                // 3) Подключала kapsula.txt (отметка без подписи core.txt): узнаётся и снимается
                System.IO.Directory.Delete(System.IO.Path.Combine(roam, "Vencord"), true);
                System.IO.File.Delete(System.IO.Path.Combine(res, "app.asar"));
                fill(dist, "// kapsula build \"Kapsula\"" + new string(' ', 120));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(res, "app.asar"), stub);
                System.IO.File.WriteAllText(DiscordInstall.Marker, "Kapsula · 2026-05-01T10:00:00\r\nvencord=kapsula\r\ninjected=" + res + "\r\n");
                AppOps.Refresh(entry, st);
                Check(st.Installed && !st.Lost, "discord: a connection made by kapsula.txt counts as connected");
                Check(DiscordInstall.Uninstall() == null && discordAsWas() && !System.IO.File.Exists(System.IO.Path.Combine(dist, "renderer.js")) && !DiscordInstall.Installed, "discord: a connection made by kapsula.txt is undone");
                // …и поверх чужого Vencord, с отметкой самых первых версий (одно слово в первой строке)
                System.IO.File.Move(System.IO.Path.Combine(res, "app.asar"), System.IO.Path.Combine(res, "_app.asar"));
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(res, "app.asar"), stub);
                fill(bak, "official vencord");
                fill(dist, "// kapsula build \"Kapsula\"" + new string(' ', 120));
                System.IO.File.WriteAllText(DiscordInstall.Marker, "Kapsula\r\nvencord=before\r\ninjected=" + res + "\r\n");
                Check(DiscordInstall.Uninstall() == null && discordAsWas() && read(System.IO.Path.Combine(dist, "renderer.js")) == "official vencord" && !System.IO.Directory.Exists(bak) && !DiscordInstall.Installed, "discord: kapsula.txt over Vencord — the previous Vencord comes back");
                System.IO.Directory.Delete(System.IO.Path.Combine(roam, "Vencord"), true);

                // 4) Discord держит app.asar: не вышло — после попытки ничего не осталось
                using (var lk = new System.IO.FileStream(System.IO.Path.Combine(res, "app.asar"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
                    Check(DiscordInstall.Install() == DiscordInstall.NeedClose, "discord: a busy Discord asks to be closed");
                Check(!System.IO.Directory.Exists(System.IO.Path.Combine(roam, "Vencord")) && discordAsWas(), "discord: after a failed attempt nothing is left");

                // 5) BetterDiscord — отказ, ничего не меняется; Discord не найден — тоже
                System.IO.Directory.CreateDirectory(System.IO.Path.Combine(roam, "BetterDiscord"));
                Check(DiscordInstall.Install() == "у вас стоит BetterDiscord — вместе с Vencord он не уживается", "discord: refuses next to BetterDiscord");
                Check(!System.IO.Directory.Exists(System.IO.Path.Combine(roam, "Vencord")) && discordAsWas(), "discord: next to BetterDiscord nothing is touched");
                System.IO.Directory.Delete(System.IO.Path.Combine(roam, "BetterDiscord"));
                Environment.SetEnvironmentVariable(DiscordInstall.TestLocal, System.IO.Path.Combine(tmp, "nowhere"));
                Check(DiscordInstall.Install() == "Discord не найден", "discord: no Discord — says so");
                Check(DiscordInstall.Uninstall() == null, "discord: disconnecting what is not connected does nothing");
            }
            finally
            {
                Environment.SetEnvironmentVariable(DiscordInstall.TestRoaming, null);
                Environment.SetEnvironmentVariable(DiscordInstall.TestLocal, null);
                Environment.SetEnvironmentVariable(DiscordInstall.TestBuild, null);
                try { System.IO.Directory.Delete(tmp, true); } catch { }
            }
        }

        // Разбор и правка JSON с сохранением текста: всё, кроме изменённого, остаётся байт в байт
        static void JsonDocChecks()
        {
            string sample = "{\r\n  \"Подложка окна\": \"Размытие\",\r\n  \"n\": [1, 1.0, -0.5, 1e5, 2E-3, 12345678901234567890123],\r\n  \"esc\": \"\\u0041\\n\\\"\\\\\\/\",\r\n  \"z\": {}, \"e\": [],\r\n  \"b\": [true, false, null]\r\n}";
            JsonDoc doc = null;
            try { doc = JsonDoc.Parse(sample); } catch { }
            Check(doc != null && doc.Result() == sample, "json: an unchanged document is written back byte for byte");
            Check(doc != null && doc.Root.Keys.Count == 6 && doc.Root.Keys[0] == "Подложка окна" && doc.Root.Keys[5] == "b", "json: keys keep their order and Cyrillic");
            Check(doc != null && doc.Get(doc.Root, "esc").Text == "A\n\"\\/", "json: escapes are read");
            Check(doc != null && doc.Raw(doc.Get(doc.Root, "n").Items[1]) == "1.0" && doc.Raw(doc.Get(doc.Root, "n").Items[5]) == "12345678901234567890123", "json: numbers keep their exact text");
            if (doc != null)
            {
                doc.Replace(doc.Get(doc.Root, "Подложка окна"), JsonDoc.Quote("Акрил \"x\""));
                Check(doc.Result() == sample.Replace("\"Размытие\"", "\"Акрил \\\"x\\\"\""), "json: a replaced value changes only that value");
            }
            Check(JsonDoc.Quote("Тема\t\"a\"\\\n\u0001") == "\"Тема\\t\\\"a\\\"\\\\\\n\\u0001\"", "json: quoting escapes only what it must and keeps Cyrillic");
            foreach (string bad in new string[] { "{", "{\"a\":}", "[1,]", "{\"a\":1} x", "01", "\"a\u0001\"", "{'a':1}", "{\"a\" 1}", "[1.]", "\"\\x\"", "", "tru" })
            {
                bool threw = false;
                try { JsonDoc.Parse(bad); } catch { threw = true; }
                Check(threw, "json: broken input is refused: " + bad);
            }
            // Новый ключ в объект с отступами — тем же отступом; в пустой — развёрнуто
            var d2 = JsonDoc.Parse("{\n    \"a\": {},\n    \"b\": 1\n}");
            d2.Add(d2.Root, "c", delegate(string ind) { return d2.ArrayText(new System.Collections.Generic.List<string> { "\"x\"" }, ind); });
            d2.Add(d2.Get(d2.Root, "a"), "k", delegate(string ind) { return "2"; });
            Check(d2.Result() == "{\n    \"a\": {\n        \"k\": 2\n    },\n    \"b\": 1,\n    \"c\": [\n        \"x\"\n    ]\n}", "json: added keys follow the document's indentation");
        }

        // Набор для Steam (Arkada) в один щелчок — только на поддельной папке Steam во временной директории: путь Steam
        // подменяется CORE_TEST_STEAM, и настоящий Steam при этом не «запущен», не закрывается и не запускается
        static void SteamChecks()
        {
            var utf = new System.Text.UTF8Encoding(false);
            Check(BundleNames.FromManifest("{\"activeTheme\":\"../evil\",\"enablePlugin\":\"a b\",\"replaces\":[\"ok-1\",\"..\",\"x\\\\y\",5,\"arkada\",\"" + new string('a', 41) + "\"],\"installTo\":\"C:\\\\Windows\"}", "arkada").Theme == "arkada", "manifest: a theme name with a path is refused");
            BundleNames bn = BundleNames.FromManifest("{\"activeTheme\":\"../evil\",\"enablePlugin\":\"a b\",\"replaces\":[\"ok-1\",\"..\",\"x\\\\y\",5,\"arkada\",\"" + new string('a', 41) + "\"]}", "arkada");
            Check(bn.Plugin == "arkada" && bn.Replaces.Count == 1 && bn.Replaces[0] == "ok-1", "manifest: only safe names are taken, our own plugin is never «replaced»");
            BundleNames bd = BundleNames.FromManifest("not json", "arkada");
            Check(bd.Theme == "arkada" && bd.Plugin == "arkada" && bd.Replaces.Count == 0, "manifest: a broken manifest means the defaults");
            BundleNames bg = BundleNames.FromManifest("{\"activeTheme\":\"Arkada.v2\",\"enablePlugin\":\"arkada_p\",\"replaces\":[\"size-on-disk\"]}", "arkada");
            Check(bg.Theme == "Arkada.v2" && bg.Plugin == "arkada_p" && bg.Replaces[0] == "size-on-disk", "manifest: good names are taken");

            AppEntry ark = Catalog.Apps.Find(delegate(AppEntry a) { return a.Id == "arkada"; });
            Check(ark != null && ark.IsBundle, "steam: the built-in catalogue has the Arkada bundle");
            if (ark == null) return;
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-steam-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            string steam = System.IO.Path.Combine(tmp, "Steam"), mil = System.IO.Path.Combine(steam, "millennium");
            string cfgDir = System.IO.Path.Combine(mil, "config"), cfg = System.IO.Path.Combine(cfgDir, "config.json"), backup = cfg + ".before-arkada";
            string plug = System.IO.Path.Combine(mil, "plugins", "arkada"), theme = System.IO.Path.Combine(mil, "themes", "arkada");
            string zip = System.IO.Path.Combine(tmp, "arkada.zip");
            // Как настоящий config.json Millennium: CRLF, два пробела, русские ключи и значения, числа
            Func<string, string, string> config = delegate(string active, string plugins)
            {
                return "{\r\n  \"general\": {\r\n    \"accentColor\": \"DEFAULT_ACCENT_COLOR\",\r\n    \"onMillenniumUpdate\": 2,\r\n    \"ratio\": 1.0,\r\n    \"big\": 12345678901234567890,\r\n    \"one\": 1\r\n  },\r\n"
                    + "  \"plugins\": {\r\n    \"enabledPlugins\": " + plugins + "\r\n  },\r\n"
                    + "  \"themes\": {\r\n    \"activeTheme\": \"" + active + "\",\r\n    \"allowedScripts\": true,\r\n    \"conditions\": {\r\n      \"arkada\": {\r\n        \"Подложка окна\": \"Размытие\",\r\n        \"«Что нового» на главной\": \"Показывать\"\r\n      },\r\n      \"Zehn\": {\r\n        \"Waifu Scale\": \"100\"\r\n      }\r\n    }\r\n  }\r\n}";
            };
            Func<string[], string> list = delegate(string[] names)
            {
                if (names.Length == 0) return "[]";
                return "[\r\n      \"" + string.Join("\",\r\n      \"", names) + "\"\r\n    ]";
            };
            string original = config("Zehn", list(new string[] { "dotastats", "size-on-disk", "steam-share" }));
            Action<string> makeZip = delegate(string manifest)
            {
                if (System.IO.File.Exists(zip)) System.IO.File.Delete(zip);
                using (var fs = System.IO.File.Create(zip))
                using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var files = new System.Collections.Generic.Dictionary<string, string> { { "plugin/plugin.json", "{\"name\":\"arkada\",\"version\":\"0.3.0\"}" }, { "plugin/backend/main.lua", "x" }, { "theme/skin.json", "{}" } };
                    if (manifest != null) files["manifest.json"] = manifest;
                    foreach (var f in files) using (var w = new System.IO.StreamWriter(z.CreateEntry(f.Key).Open(), utf)) w.Write(f.Value);
                }
            };
            Func<string, string> read = delegate(string p) { return System.IO.File.Exists(p) ? System.IO.File.ReadAllText(p, utf) : null; };
            Func<string, byte[]> bytes = delegate(string p) { return System.IO.File.Exists(p) ? System.IO.File.ReadAllBytes(p) : null; };
            Func<byte[], byte[], bool> same = delegate(byte[] a, byte[] b) { return a != null && b != null && System.Linq.Enumerable.SequenceEqual(a, b); };
            var release = new Release { Version = new Version(0, 3, 0) };
            const string manifestOk = "{\"activeTheme\":\"arkada\",\"enablePlugin\":\"arkada\",\"replaces\":[\"size-on-disk\",\"steam-share\",\"not-there\"],\"installTo\":\"C:\\\\Windows\\\\x\",\"keepFolders\":[\"..\"]}";
            int starts = Steam.FakeStarts;
            try
            {
                foreach (string d in new string[] { cfgDir, System.IO.Path.Combine(mil, "plugins", "dotastats"), System.IO.Path.Combine(mil, "plugins", "size-on-disk"), System.IO.Path.Combine(mil, "plugins", "steam-share"), System.IO.Path.Combine(mil, "themes", "Zehn") })
                    System.IO.Directory.CreateDirectory(d);
                System.IO.File.WriteAllText(cfg, original, utf);
                Environment.SetEnvironmentVariable(Steam.TestDir, steam);
                Environment.SetEnvironmentVariable(Steam.TestRunning, null);
                Check(Steam.Dir() == steam && AppOps.Expand(ark.Marker).StartsWith(tmp, StringComparison.OrdinalIgnoreCase), "steam: the checks work in a fake Steam folder only");
                Check(!Steam.Running() && !Steam.Running(ProcSnap.Take(new string[] { "steam" })), "steam: on a fake folder the real Steam does not count");

                // 1) Установка: файлы на месте, тема и плагин включены, заменённые плагины отключены, остальное — байт в байт
                makeZip(manifestOk);
                var st = new AppStatus { Latest = release };
                System.Collections.Generic.List<string> off = null;
                try { off = AppOps.InstallBundle(ark, st, zip); } catch (Exception e) { Check(false, "steam: install threw " + e.Message + (e.InnerException != null ? " / " + e.InnerException.Message : "")); }
                Check(System.IO.File.Exists(System.IO.Path.Combine(plug, "plugin.json")) && System.IO.File.Exists(System.IO.Path.Combine(plug, "backend", "main.lua")) && System.IO.File.Exists(System.IO.Path.Combine(theme, "skin.json")), "steam: bundle files are in place");
                Check(!System.IO.File.Exists(System.IO.Path.Combine(plug, "manifest.json")) && !System.IO.Directory.Exists(System.IO.Path.Combine(tmp, "Windows")), "steam: the manifest decides no folders");
                string installed = config("arkada", list(new string[] { "dotastats", "arkada" }));
                Check(read(cfg) == installed, "steam: config.json — only the active theme and the plugin list changed");
                Check(bytes(cfg) != null && bytes(cfg)[0] == (byte)'{', "steam: config.json is written without BOM");
                Check(off != null && off.Count == 2 && off[0] == "size-on-disk" && off[1] == "steam-share", "steam: only the replaced plugins that were enabled are reported");
                Check(read(backup) == original, "steam: the backup of config.json is the original");
                var mem = Millennium.ReadMemory(plug);
                Check(mem.Found && mem.Before == "Zehn" && mem.Plugin == "arkada" && mem.Theme == "arkada" && mem.Disabled.Count == 2, "steam: what was before is remembered");
                var look = new AppStatus();
                AppOps.Refresh(ark, look);
                Check(look.Installed && look.Version == new Version(0, 3, 0) && !look.Running && !look.NoHost, "steam: the card sees it installed, Steam closed");
                Check(!System.IO.File.Exists(cfg + ".core-tmp"), "steam: no temporary file is left");

                // 2) Повторная установка (обновление): ничего не портит, резерв и «как было» не трогает, файл не переписывает
                System.IO.File.WriteAllText(backup, "backup", utf);
                DateTime written = System.IO.File.GetLastWriteTimeUtc(cfg);
                System.IO.File.SetLastWriteTimeUtc(cfg, written.AddMinutes(-5));
                written = System.IO.File.GetLastWriteTimeUtc(cfg);
                makeZip(manifestOk);
                off = AppOps.InstallBundle(ark, st, zip);
                Check(read(cfg) == installed && System.IO.File.GetLastWriteTimeUtc(cfg) == written, "steam: installing again leaves a correct config.json alone");
                Check(read(backup) == "backup" && off.Count == 0, "steam: installing again keeps the backup");
                mem = Millennium.ReadMemory(plug);
                Check(mem.Before == "Zehn" && mem.Disabled.Count == 2, "steam: installing again keeps what was before");

                // 3) Версия: нет .core-version — из plugin.json
                System.IO.File.Delete(System.IO.Path.Combine(plug, ".core-version"));
                System.IO.File.WriteAllText(System.IO.Path.Combine(plug, "plugin.json"), "{\"name\":\"arkada\",\"version\":\"0.2.5\"}", utf);
                AppOps.Refresh(ark, look);
                Check(look.Installed && look.Version == new Version(0, 2, 5), "steam: without .core-version the version comes from plugin.json");

                // 4) Удаление: прежняя тема и отключённые плагины вернулись, папок набора нет — config.json как до установки
                System.IO.File.WriteAllText(backup, original, utf);
                AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false);
                Check(read(cfg) == original, "steam: removal brings back the previous theme and plugins, the rest byte for byte");
                Check(!System.IO.Directory.Exists(plug) && !System.IO.Directory.Exists(theme), "steam: removal takes the bundle folders away");
                Check(System.IO.File.Exists(backup), "steam: the backup stays after removal");

                // 5) Steam открыт, а закрыть не просили — не трогаем ничего
                Environment.SetEnvironmentVariable(Steam.TestRunning, "1");
                makeZip(manifestOk);
                bool open = false;
                try { AppOps.InstallBundle(ark, st, zip); } catch (SteamOpen) { open = true; }
                Check(open && read(cfg) == original && !System.IO.Directory.Exists(plug), "steam: with Steam open and no permission to close it nothing changes");
                AppOps.Refresh(ark, look);
                Check(look.Running, "steam: the card sees Steam running (for «Закрыть Steam и …»)");
                // …закрыть попросили: закрыли, сделали, запустили снова — даже если работа не удалась
                bool ran = false, started = false;
                try { started = Steam.Around(true, null, delegate { ran = !Steam.Running(); throw new Exception("work failed"); }); } catch { }
                Check(ran && Steam.FakeStarts == starts + 1 && Steam.Running(), "steam: closed by us — started again even after a failure");
                started = Steam.Around(false, null, delegate { });
                Check(!started && Steam.FakeStarts == starts + 1, "steam: not asked to close — not closed, not started");
                // …Steam не закрылся — понятная ошибка, работа не начиналась
                Environment.SetEnvironmentVariable(Steam.TestRunning, "stuck");
                ran = false;
                string err = null;
                try { Steam.Around(true, null, delegate { ran = true; }); } catch (Exception e) { err = e.Message; }
                Check(!ran && err == Steam.NotClosed && Steam.FakeStarts == starts + 1, "steam: a Steam that does not close stops everything");
                Environment.SetEnvironmentVariable(Steam.TestRunning, null);
                // Закрыли и поставили «как по кнопке»
                Environment.SetEnvironmentVariable(Steam.TestRunning, "1");
                makeZip(manifestOk);
                started = Steam.Around(true, null, delegate { AppOps.InstallBundle(ark, st, zip); });
                Check(started && read(cfg) == installed && Steam.Running(), "steam: «close Steam and install» installs and starts Steam again");
                Environment.SetEnvironmentVariable(Steam.TestRunning, null);
                AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false);
                Check(read(cfg) == original, "steam: removed again");

                // 6) Нет config.json / битый config.json: файлы стоят, настройки не тронуты, ошибка понятная
                System.IO.File.Delete(cfg);
                System.IO.File.Delete(backup);
                makeZip(manifestOk);
                err = null;
                try { AppOps.InstallBundle(ark, st, zip); } catch (Exception e) { err = e.Message; }
                Check(err == Millennium.EnableFailed && System.IO.File.Exists(System.IO.Path.Combine(plug, "plugin.json")) && !System.IO.File.Exists(cfg) && !System.IO.File.Exists(backup), "steam: no config.json — files in place, says so");
                AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false);
                Check(!System.IO.Directory.Exists(plug) && !System.IO.File.Exists(cfg), "steam: removal without config.json works");
                byte[] broken = utf.GetBytes("{ \"themes\": { \"activeTheme\": \"Zehn\", }");
                System.IO.File.WriteAllBytes(cfg, broken);
                makeZip(manifestOk);
                err = null;
                try { AppOps.InstallBundle(ark, st, zip); } catch (Exception e) { err = e.Message; }
                Check(err == Millennium.EnableFailed && System.IO.File.Exists(System.IO.Path.Combine(theme, "skin.json")) && same(bytes(cfg), broken) && !System.IO.File.Exists(backup), "steam: a broken config.json — files in place, the file untouched");
                err = null;
                try { AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false); } catch (Exception e) { err = e.Message; }
                Check(err == Millennium.RestoreFailed && !System.IO.Directory.Exists(plug) && same(bytes(cfg), broken), "steam: removal with a broken config.json removes the folders and says the settings were not restored");
                System.IO.File.WriteAllBytes(cfg, new byte[] { 0xEF, 0xBB, 0xBF, 0x7B, 0x22, 0xFF, 0x22, 0x7D });   // не UTF-8
                makeZip(manifestOk);
                err = null;
                try { AppOps.InstallBundle(ark, st, zip); } catch (Exception e) { err = e.Message; }
                Check(err == Millennium.EnableFailed && System.IO.File.ReadAllBytes(cfg).Length == 8, "steam: a config.json that is not UTF-8 is not touched");
                try { AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false); } catch { }
                Check(System.IO.File.ReadAllBytes(cfg).Length == 8 && !System.IO.Directory.Exists(plug), "steam: removal leaves a config.json that is not UTF-8 alone");

                // 7) Без манифеста — имена по id; набор поставлен руками (тема уже наша, «как было» не запомнено) — удаление ставит «default»
                System.IO.File.WriteAllText(cfg, "\uFEFF" + config("arkada", list(new string[] { "arkada", "size-on-disk" })), utf);   // с меткой BOM
                System.IO.File.Delete(backup);
                makeZip(null);
                off = AppOps.InstallBundle(ark, st, zip);
                Check(off.Count == 0 && read(cfg) == config("arkada", list(new string[] { "arkada", "size-on-disk" })) && bytes(cfg)[0] == (byte)'{', "steam: no manifest — nothing is replaced; a BOM is dropped");
                Check(Millennium.ReadMemory(plug).Before == null, "steam: our own theme is not remembered as the previous one");
                AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false);
                Check(read(cfg) == config("default", list(new string[] { "size-on-disk" })), "steam: removal without a remembered theme sets «default»");

                // 8) В config.json нет нужных разделов — добавляются в конец тем же отступом; прежняя тема удалена — «default»
                System.IO.File.WriteAllText(cfg, "{\r\n  \"general\": {\r\n    \"one\": 1\r\n  }\r\n}", utf);
                makeZip(manifestOk);
                AppOps.InstallBundle(ark, st, zip);
                Check(read(cfg) == "{\r\n  \"general\": {\r\n    \"one\": 1\r\n  },\r\n  \"themes\": {\r\n    \"activeTheme\": \"arkada\"\r\n  },\r\n  \"plugins\": {\r\n    \"enabledPlugins\": [\r\n      \"arkada\"\r\n    ]\r\n  }\r\n}", "steam: missing sections are added with the same indentation");
                AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false);
                Check(read(cfg) == "{\r\n  \"general\": {\r\n    \"one\": 1\r\n  },\r\n  \"themes\": {\r\n    \"activeTheme\": \"default\"\r\n  },\r\n  \"plugins\": {\r\n    \"enabledPlugins\": []\r\n  }\r\n}", "steam: an empty plugin list is written as []");
                System.IO.File.WriteAllText(cfg, original, utf);
                makeZip(manifestOk);
                AppOps.InstallBundle(ark, st, zip);
                System.IO.Directory.Delete(System.IO.Path.Combine(mil, "themes", "Zehn"), true);
                System.IO.Directory.Delete(System.IO.Path.Combine(mil, "plugins", "steam-share"), true);
                AppOps.Uninstall(ark, new AppStatus { Path = AppOps.Expand(ark.Marker) }, false);
                Check(read(cfg) == config("default", list(new string[] { "dotastats", "size-on-disk" })), "steam: a previous theme or plugin that is gone is not brought back");

                // 9) Нет Millennium — карточка это знает (кнопка steambrew.app), ставить отказывается как раньше
                System.IO.Directory.Delete(mil, true);
                AppOps.Refresh(ark, look);
                Check(look.NoHost && !look.Installed, "steam: without Millennium the card knows it");
                makeZip(manifestOk);
                err = null;
                try { AppOps.InstallBundle(ark, st, zip); } catch (Exception e) { err = e.Message; }
                Check(err == ark.RequiresHint, "steam: without Millennium the install says what is needed");
            }
            finally
            {
                Environment.SetEnvironmentVariable(Steam.TestDir, null);
                Environment.SetEnvironmentVariable(Steam.TestRunning, null);
                try { System.IO.Directory.Delete(tmp, true); } catch { }
            }
        }

        static byte[] Gzip(byte[] data)
        {
            var ms = new System.IO.MemoryStream();
            using (var z = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress)) z.Write(data, 0, data.Length);
            return ms.ToArray();
        }

        // Карта в формате electron-builder для данных data: блоки заданной длины с offset; сумма блока — SHA-256 в base64
        // (какие суммы у electron-builder, неважно: core.txt их только сравнивает)
        internal static string MapJson(byte[] data, long offset, long[] sizes)
        {
            var sums = new System.Collections.Generic.List<object>();
            var lens = new System.Collections.Generic.List<object>();
            long pos = offset;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                foreach (long n in sizes) { sums.Add(Convert.ToBase64String(sha.ComputeHash(data, (int)pos, (int)n))); lens.Add(n); pos += n; }
            var file = new System.Collections.Generic.Dictionary<string, object> { { "name", "file" }, { "offset", offset }, { "checksums", sums }, { "sizes", lens } };
            return Json.Write(new System.Collections.Generic.Dictionary<string, object> { { "version", "2" }, { "files", new object[] { file } } });
        }

        // Блоки по block байт подряд, последний — сколько осталось
        internal static long[] Even(long length, long block)
        {
            var list = new System.Collections.Generic.List<long>();
            for (long at = 0; at < length; at += block) list.Add(Math.Min(block, length - at));
            return list.ToArray();
        }

        static string Sha(byte[] data)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        // Обновление по карте блоков: разбор карты, план, сборка, запас — всё без сети
        static void BlockMapChecks()
        {
            var utf = System.Text.Encoding.UTF8;
            var rnd = new Random(7);
            var old = new byte[800];
            rnd.NextBytes(old);
            Func<int, byte[]> block = delegate(int i) { var b = new byte[100]; Array.Copy(old, i * 100, b, 0, 100); return b; };
            var fresh = new byte[100]; rnd.NextBytes(fresh);
            var other = new byte[100]; rnd.NextBytes(other);
            var head = new byte[10]; rnd.NextBytes(head);
            var tail = new byte[5]; rnd.NextBytes(tail);
            // Новый файл: заголовок вне карты, блоки 0,1 как были, новый, 3,4 как были, 6 и 5 поменялись местами, новый, хвост вне карты
            var parts = new byte[][] { head, block(0), block(1), fresh, block(3), block(4), block(6), block(5), other, tail };
            var ms = new System.IO.MemoryStream();
            foreach (byte[] p in parts) ms.Write(p, 0, p.Length);
            byte[] neu = ms.ToArray();
            string oldJson = MapJson(old, 0, Even(800, 100)), newJson = MapJson(neu, 10, Even(800, 100));

            // Разбор: gzip, сырой deflate и просто JSON — одно и то же; мусор и пустота — «карты нет»
            BlockMap om = BlockMap.Parse(Gzip(utf.GetBytes(oldJson)));
            Check(om != null && om.Offset == 0 && om.Sizes.Length == 8 && om.Sizes[7] == 100 && om.End == 800 && om.Sums[0] == Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(block(0))),"a gzip block map is read");
            var deflated = new System.IO.MemoryStream();
            using (var z = new System.IO.Compression.DeflateStream(deflated, System.IO.Compression.CompressionMode.Compress)) { byte[] b = utf.GetBytes(newJson); z.Write(b, 0, b.Length); }
            BlockMap nm = BlockMap.Parse(deflated.ToArray());
            Check(nm != null && nm.Offset == 10 && nm.End == 810, "a raw deflate block map is read");
            BlockMap plain = BlockMap.Parse(utf.GetBytes(newJson));
            Check(plain != null && plain.Offset == 10 && plain.Sums[2] == nm.Sums[2], "a plain JSON block map is read");
            var junk = new byte[300]; rnd.NextBytes(junk);
            Check(BlockMap.Parse(junk) == null && BlockMap.Parse(new byte[0]) == null && BlockMap.Parse(null) == null && BlockMap.Parse(Gzip(junk)) == null, "junk or an empty file is no block map");
            Check(BlockMap.Parse(utf.GetBytes("{\"version\":\"2\",\"files\":[{\"name\":\"file\",\"offset\":0,\"checksums\":[\"a\",\"b\"],\"sizes\":[5]}]}")) == null
                && BlockMap.Parse(utf.GetBytes("{\"version\":\"2\",\"files\":[{\"name\":\"file\",\"offset\":0,\"checksums\":[\"a\"],\"sizes\":[0]}]}")) == null
                && BlockMap.Parse(utf.GetBytes("{\"version\":\"2\",\"files\":[]}")) == null && BlockMap.Parse(utf.GetBytes("[1,2]")) == null, "a block map that does not add up is no block map");

            // План: что берётся из прежнего файла и откуда, что качается
            Func<PatchPlan, string> show = delegate(PatchPlan p)
            {
                if (p == null) return "full";
                var sb = new System.Text.StringBuilder();
                foreach (Piece x in p.Pieces) sb.Append(x.Local ? "L" + x.At + "-" + (x.At + x.Length) + "@" + x.From + " " : "R" + x.At + "-" + (x.At + x.Length) + " ");
                return sb.ToString().Trim();
            };
            PatchPlan plan = Patch.Make(om, 800, nm, neu.Length, 0, 100, 0.7);
            Check(show(plan) == "R0-10 L10-210@0 R210-310 L310-510@300 L510-610@600 L610-710@500 R710-815", "the plan reuses unchanged blocks and fetches the rest, got " + show(plan));
            Check(plan != null && plan.Requests == 3 && plan.Download == 10 + 100 + 105, "the plan counts what goes over the network");
            PatchPlan close = Patch.Make(om, 800, nm, neu.Length, 200, 100, 0.7);
            Check(show(close) == "R0-310 L310-510@300 L510-610@600 L610-710@500 R710-815" && close.Requests == 2 && close.Download == 415, "ranges with a small gap between them become one request, got " + show(close));
            Check(Patch.Make(om, 800, nm, neu.Length, 200, 100, 0.4) == null, "too much to fetch — the whole file instead");
            Check(Patch.Make(om, 800, nm, neu.Length, 0, 2, 0.7) == null, "too many requests — the whole file instead");
            Check(Patch.Make(om, 800, nm, neu.Length) == null, "a tiny file with the default gap is fetched whole");
            Check(Patch.Make(om, 799, nm, neu.Length, 0, 100, 0.7) == null && Patch.Make(om, 800, nm, 809, 0, 100, 0.7) == null && Patch.Make(null, 800, nm, neu.Length, 0, 100, 0.7) == null,
                "a map larger than its file means no partial download");
            PatchPlan same = Patch.Make(om, 800, om, 800, 0, 100, 0.7);
            Check(show(same) == "L0-800@0" && same.Requests == 0 && same.Download == 0, "the same file needs nothing from the network");

            // Сборка: из прежнего файла и подставного «сервера» выходит ровно новый файл
            int asked = 0;
            Func<long, long, System.IO.Stream> server = delegate(long at, long length) { asked++; return new System.IO.MemoryStream(neu, (int)at, (int)length, false); };
            var built = new System.IO.MemoryStream();
            long got = 0;
            string hash = Patch.Assemble(plan, new System.IO.MemoryStream(old, false), server, built, delegate(long n) { got = n; });
            Check(Convert.ToBase64String(built.ToArray()) == Convert.ToBase64String(neu) && hash == Sha(neu), "the assembled file equals the new one byte for byte");
            Check(asked == plan.Requests && got == plan.Download, "assembly asks only the planned ranges and counts them, got " + asked + " requests, " + got + " bytes");
            built = new System.IO.MemoryStream();
            Check(Patch.Assemble(close, new System.IO.MemoryStream(old, false), server, built, null) == Sha(neu), "merged ranges assemble the same file");
            // Прежний файл испорчен — сумма не сходится, и такой файл не ставится
            var broken = (byte[])old.Clone();
            broken[350] ^= 0xFF;
            Check(Patch.Assemble(plan, new System.IO.MemoryStream(broken, false), server, new System.IO.MemoryStream(), null) != Sha(neu), "a damaged old file gives a different checksum");
            Func<Func<long, long, System.IO.Stream>, bool> fails = delegate(Func<long, long, System.IO.Stream> f)
            {
                try { Patch.Assemble(plan, new System.IO.MemoryStream(old, false), f, new System.IO.MemoryStream(), null); return false; }
                catch { return true; }
            };
            Check(fails(delegate(long at, long length) { return new System.IO.MemoryStream(neu, (int)at, (int)length - 1, false); }), "a range shorter than asked is refused");
            Check(fails(delegate(long at, long length) { return new System.IO.MemoryStream(neu, (int)at, (int)Math.Min(length + 1, neu.Length - at), false); }), "a range longer than asked is refused");
            Check(fails(delegate(long at, long length) { throw new System.Net.WebException("no network"); }), "a network failure stops the assembly");

            // Релиз помнит адрес карты; чужая карта и карта под другим именем не считаются; старые записи читаются
            var app = new AppEntry { Repo = "cxxperman/two", Asset = "Two-Setup-*.exe" };
            string sha = new string('b', 64), dl = "https://github.com/cxxperman/two/releases/download/";
            Release r = Releases.Pick(app, @"[
                { ""tag_name"": ""v2.0.0"", ""assets"": [
                    { ""name"": ""Two-Setup-2.0.0.exe.blockmap"", ""size"": 3, ""browser_download_url"": """ + dl + @"v2.0.0/Two-Setup-2.0.0.exe.blockmap"" },
                    { ""name"": ""Two-Setup-2.0.0.exe"", ""size"": 9, ""browser_download_url"": """ + dl + @"v2.0.0/Two-Setup-2.0.0.exe"", ""digest"": ""sha256:" + sha + @""" } ] },
                { ""tag_name"": ""v1.0.0"", ""assets"": [
                    { ""name"": ""Two-Setup-1.0.0.exe"", ""size"": 9, ""browser_download_url"": """ + dl + @"v1.0.0/Two-Setup-1.0.0.exe"", ""digest"": ""sha256:" + sha + @""" } ] }
            ]");
            Check(r != null && r.Version == new Version(2, 0, 0) && r.Name == "Two-Setup-2.0.0.exe" && r.MapUrl == dl + "v2.0.0/Two-Setup-2.0.0.exe.blockmap", "the block map next to the file is found");
            Release back = r == null ? null : Release.From(Json.Parse(Json.Write(r.ToDict())));
            Check(back != null && back.MapUrl == r.MapUrl && back.Url == r.Url && back.Sha == r.Sha, "a release with a block map survives saving and loading");
            Release foreign = Releases.Pick(app, @"[ { ""tag_name"": ""v2.0.0"", ""assets"": [
                    { ""name"": ""Two-Setup-2.0.0.exe"", ""size"": 9, ""browser_download_url"": """ + dl + @"v2.0.0/Two-Setup-2.0.0.exe"", ""digest"": ""sha256:" + sha + @""" },
                    { ""name"": ""Two-Setup-2.0.0.exe.blockmap"", ""size"": 3, ""browser_download_url"": ""https://github.com/someone/else/releases/download/v2.0.0/Two-Setup-2.0.0.exe.blockmap"" },
                    { ""name"": ""Two-Setup-1.9.0.exe.blockmap"", ""size"": 3, ""browser_download_url"": """ + dl + @"v2.0.0/Two-Setup-1.9.0.exe.blockmap"" } ] } ]");
            Check(foreign != null && foreign.MapUrl == null, "a block map from another repository or for another file is ignored");
            Release legacy = Release.From(Json.Parse("{\"version\":\"1.0.0\",\"url\":\"" + dl + "v1.0.0/Two-Setup-1.0.0.exe\",\"sha\":\"" + sha + "\",\"name\":\"Two-Setup-1.0.0.exe\",\"size\":\"9\"}"));
            Check(legacy != null && legacy.MapUrl == null && legacy.Size == 9, "a release saved before block maps loads without one");
            Release none = Release.From(Json.Parse(Json.Write(legacy.ToDict())));
            Check(none != null && none.MapUrl == null, "a release without a block map stays without one after saving");

            // Запас: кладётся переносом, заменяет прежний, стирается; файл из запаса установка не уносит
            Check(Stash.Root.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "the self-test keeps its stash in a temporary folder");
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-stash-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            System.IO.Directory.CreateDirectory(tmp);
            try
            {
                string f1 = System.IO.Path.Combine(tmp, "Two-Setup-1.0.0.exe"), f2 = System.IO.Path.Combine(tmp, "Two-Setup-2.0.0.exe");
                System.IO.File.WriteAllBytes(f1, old);
                string in1 = Stash.Put("selftest-two", f1, utf.GetBytes(oldJson));
                string file; byte[] map;
                Check(in1 != f1 && !System.IO.File.Exists(f1) && System.IO.File.Exists(in1) && Stash.Holds(in1) && !Stash.Holds(f2), "a downloaded file moves into the stash, no second copy");
                Check(Stash.Find("selftest-two", out file, out map) && file == in1 && BlockMap.Parse(map) != null, "the stash gives back the file and its map");
                System.IO.File.WriteAllBytes(f2, neu);
                string in2 = Stash.Put("selftest-two", f2, utf.GetBytes(newJson));
                Check(System.IO.File.Exists(in2) && !System.IO.File.Exists(in1) && System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(in2)).Length == 2, "a new file replaces the previous one: one file and one map");
                Check(Stash.Find("selftest-two", out file, out map) && file == in2 && BlockMap.Parse(map).Offset == 10, "the stash holds the newest file");
                Check(Stash.Put("..", f2, utf.GetBytes(newJson)) == f2 && Stash.Put("selftest-two", f2, null) == f2, "no stash without a map or for a strange id");

                // Переносное приложение из запаса — копией: запас остаётся для следующего обновления
                AppOps.HomeOverride = System.IO.Path.Combine(tmp, "home");
                string target = System.IO.Path.Combine(AppOps.HomeOverride, "Two", "Two.exe");
                AppOps.Install(new AppEntry { Id = "selftest-two", Name = "Two", Kind = "portable", Exe = "Two.exe", Process = "CoreTxtNoSuchProcess" }, new AppStatus { Path = target }, in2);
                Check(System.IO.File.Exists(target) && System.IO.File.Exists(in2), "installing from the stash copies the file and keeps the stash");
                State.Paths.Remove("selftest-two");

                Stash.Clear("selftest-two");
                Check(!Stash.Find("selftest-two", out file, out map) && !System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(in2)), "the stash of an app is wiped");
            }
            finally { AppOps.HomeOverride = null; try { System.IO.Directory.Delete(tmp, true); } catch { } }
        }

        // Движение без окна (как на снимке): бегущая полоса стоит отрезком, а не залита целиком; бесконечное движение
        // помнит, что должно идти, но не идёт, и смена «Меньше движения» его не запускает. С окном — в --motion.
        static void MotionChecks()
        {
            var bar = new Progress(System.Windows.Media.Colors.Orange);
            bar.Root.Measure(new Size(300, 3));
            bar.Root.Arrange(new Rect(0, 0, 300, 3));
            bar.Busy = true;
            var fill = (System.Windows.Controls.Border)((System.Windows.Controls.Grid)bar.Root.Child).Children[1];
            Check(fill.Width > 0 && fill.Width < 150, "a busy bar that cannot move is a segment, not a full bar (width " + fill.Width + ")");
            Check(Motion.Wants(bar.Root) && !Motion.Looping(bar.Root), "a busy bar off screen wants to run but does not");
            Ui.ForceReducedMotion = true;
            Ui.ForceReducedMotion = false;
            Check(Motion.Wants(bar.Root) && !Motion.Looping(bar.Root), "switching reduced motion does not start a loop off screen");
            bar.Busy = false;
            Check(!Motion.Wants(bar.Root) && fill.Width == 0, "an idle bar is empty again");
            var dot = new System.Windows.Shapes.Ellipse();
            Motion.Blink(dot, true);
            Check(Motion.Wants(dot) && !Motion.Looping(dot) && dot.Opacity == 1, "a blink off screen stays still");
            Motion.Blink(dot, false);
            Check(!Motion.Wants(dot), "a blink switched off is forgotten");
        }

        static void Checks()
        {
            Check(State.FilePath.StartsWith(System.IO.Path.GetTempPath(), StringComparison.OrdinalIgnoreCase), "the self-test keeps its state in a temporary folder");
            MotionChecks();
            var apps = Catalog.Read(@"{ ""apps"": [
                { ""id"": ""one"", ""name"": ""One"", ""kind"": ""portable"", ""repo"": ""cxxperman/one"", ""asset"": ""One.exe"", ""exe"": ""One.exe"", ""process"": ""One"", ""color"": ""#F08A4B"" },
                { ""id"": ""two"", ""name"": ""Two"", ""kind"": ""installer"", ""repo"": ""cxxperman/two"", ""asset"": ""Two-Setup-*.exe"", ""installed"": ""%TEMP%\\two.exe"" },
                { ""id"": ""evil"", ""name"": ""Evil"", ""kind"": ""portable"", ""repo"": ""someone/else"", ""asset"": ""x.exe"", ""exe"": ""x.exe"" },
                { ""id"": ""path"", ""name"": ""Path"", ""kind"": ""portable"", ""repo"": ""cxxperman/p"", ""asset"": ""x.exe"", ""exe"": ""..\\x.exe"" },
                { ""id"": ""new"", ""name"": ""New"", ""kind"": ""something-from-the-future"" },
                { ""id"": ""one"", ""name"": ""Twice"", ""kind"": ""integration"", ""marker"": ""x"" },
                { ""id"": ""link"", ""name"": ""Link"", ""kind"": ""integration"", ""marker"": ""%TEMP%\\m.txt"", ""via"": ""one"" }
            ] }");
            Check(apps.Count == 3, "catalog keeps the three valid entries, got " + apps.Count);
            Check(apps.Count > 0 && apps[0].Color == System.Windows.Media.Color.FromRgb(0xF0, 0x8A, 0x4B), "colour is read");
            Check(Catalog.Apps.Count == 0 || true, "");
            Catalog.Load();
            Check(Catalog.Apps.Count >= 4, "built-in catalog loads");

            Check(Releases.AssetMatches("Aura-Setup-*.exe", "Aura-Setup-0.26.2.exe"), "asset pattern matches");
            Check(!Releases.AssetMatches("Aura-Setup-*.exe", "Aura-Setup-0.26.2.exe.blockmap"), "asset pattern is anchored");
            Check(!Releases.AssetMatches("Kapsula.exe", "Kapsula.exe.sha256"), "exact asset name is exact");

            var app = new AppEntry { Repo = "cxxperman/one", Asset = "One.exe" };
            string sha = new string('a', 64);
            string feed = @"[
                { ""tag_name"": ""v9.0.0"", ""draft"": true, ""assets"": [ { ""name"": ""One.exe"", ""size"": 5, ""browser_download_url"": ""https://github.com/cxxperman/one/releases/download/v9.0.0/One.exe"", ""digest"": ""sha256:" + sha + @""" } ] },
                { ""tag_name"": ""v3.0.0"", ""assets"": [ { ""name"": ""One.exe"", ""size"": 5, ""browser_download_url"": ""https://example.com/One.exe"" } ] },
                { ""tag_name"": ""nightly"", ""assets"": [ { ""name"": ""One.exe"", ""size"": 5, ""browser_download_url"": ""https://github.com/cxxperman/one/releases/download/nightly/One.exe"" } ] },
                { ""tag_name"": ""v1.2.0"", ""assets"": [ { ""name"": ""One.exe"", ""size"": 7, ""browser_download_url"": ""https://github.com/cxxperman/one/releases/download/v1.2.0/One.exe"", ""digest"": ""sha256:" + sha + @""" } ] },
                { ""tag_name"": ""v1.10.0"", ""prerelease"": true, ""assets"": [ { ""name"": ""One.exe"", ""size"": 9, ""browser_download_url"": ""https://github.com/cxxperman/one/releases/download/v1.10.0/One.exe"", ""digest"": ""sha256:" + sha + @""" } ] },
                { ""tag_name"": ""v2.0.0"", ""assets"": [ { ""name"": ""Other.exe"", ""size"": 5, ""browser_download_url"": ""https://github.com/cxxperman/one/releases/download/v2.0.0/Other.exe"" } ] }
            ]";
            Release r = Releases.Pick(app, feed);
            Check(r != null && r.Version == new Version(1, 10, 0), "newest usable release is 1.10.0, got " + (r == null ? "none" : r.Version.ToString()));
            Check(r != null && r.Size == 9 && r.Sha == sha, "size and checksum are read");
            Release back = r == null ? null : Release.From(Json.Parse(Json.Write(r.ToDict())));
            Check(back != null && back.Version == r.Version && back.Url == r.Url && back.Sha == r.Sha && back.Size == r.Size, "release survives saving and loading");
            Check(Releases.Pick(app, "[]") == null, "no releases — nothing to install");

            var s = new AppStatus { Path = "x", Version = new Version(1, 9, 0), Latest = r };
            Check(s.UpdateAvailable, "1.10.0 is newer than 1.9.0");
            s.Version = new Version(1, 10, 0);
            Check(!s.UpdateAvailable, "same version is not an update");

            // Одна папка на всё: переносная программа переезжает в общий дом, лишняя копия снаружи убирается
            {
                string home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-home-" + System.Diagnostics.Process.GetCurrentProcess().Id);
                string away = System.IO.Path.Combine(home, "elsewhere");
                System.IO.Directory.CreateDirectory(away);
                AppOps.HomeOverride = System.IO.Path.Combine(home, "dev.txt");
                try
                {
                    var one = new AppEntry { Id = "selftest-home", Name = "One", Kind = "portable", Exe = "One.exe", Process = "CoreTxtNoSuchProcess" };
                    string outside = System.IO.Path.Combine(away, "One.exe"), inside = System.IO.Path.Combine(AppOps.HomeOverride, "One", "One.exe");
                    System.IO.File.WriteAllText(outside, "app");
                    var st = new AppStatus { Path = outside };
                    Check(AppOps.Outside(one, st), "a program outside the home folder is noticed");
                    AppOps.Gather(one, st);
                    Check(System.IO.File.Exists(inside) && !System.IO.File.Exists(outside) && !System.IO.Directory.Exists(away), "it moves into the home folder and the empty old folder goes away");
                    Check(!AppOps.Outside(one, new AppStatus { Path = inside }), "inside the home folder there is nothing to move");
                    System.IO.Directory.CreateDirectory(away);
                    System.IO.File.WriteAllText(outside, "copy");
                    AppOps.Gather(one, new AppStatus { Path = outside });
                    Check(!System.IO.File.Exists(outside) && System.IO.File.ReadAllText(inside) == "app", "a second copy outside is removed, the one at home is kept");
                    Check(!AppOps.Outside(new AppEntry { Kind = "installer", Installed = "x" }, new AppStatus { Path = outside }), "what an installer placed is never moved");
                    State.Paths.Remove("selftest-home");
                }
                finally { AppOps.HomeOverride = null; try { System.IO.Directory.Delete(home, true); } catch { } }
                Check(AppOps.PathFromCommand("\"C:\\A b\\Kapsula.exe\" --wait") == "C:\\A b\\Kapsula.exe" && AppOps.PathFromCommand("C:\\X\\Dock.exe --tray") == "C:\\X\\Dock.exe" && AppOps.PathFromCommand("") == null, "the program path is read from an autostart entry");
                Check(AppOps.ReplacePath("\"c:\\old\\Kapsula.exe\" --wait", "C:\\Old\\Kapsula.exe", "D:\\new\\Kapsula.exe") == "\"D:\\new\\Kapsula.exe\" --wait", "an autostart entry follows the program to its new place");
            }

            // Временная папка: пробная сборка оттуда — не установленное приложение, core.txt её не запоминает и не запускает
            {
                AppOps.TempOverride = @"C:\Fake\Temp\";
                try
                {
                    Check(AppOps.InTemp(@"C:\Fake\Temp\a\Smena.exe"), "a file inside the temporary folder is noticed");
                    Check(!AppOps.InTemp(@"C:\Fake\Temp2\Smena.exe"), "a sibling folder with the same prefix is not the temporary folder");
                    Check(!AppOps.InTemp(@"C:\Fake\Programs\Smena.exe"), "a file elsewhere is not in the temporary folder");
                    Check(!AppOps.InTemp(null) && !AppOps.InTemp(""), "an empty path is not in the temporary folder");
                }
                finally { AppOps.TempOverride = null; }

                string pid = System.Diagnostics.Process.GetCurrentProcess().Id.ToString();
                string tdir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-temp-" + pid);
                string thome = tdir + "-home";
                try
                {
                    System.IO.Directory.CreateDirectory(tdir);
                    string texe = System.IO.Path.Combine(tdir, "One.exe");
                    System.IO.File.WriteAllText(texe, "app");
                    var one = new AppEntry { Id = "selftest-temp", Name = "One", Kind = "portable", Exe = "One.exe", Process = "CoreTxtNoSuchProcess" };
                    State.Paths["selftest-temp"] = texe;
                    AppOps.HomeOverride = System.IO.Path.Combine(thome, "dev.txt");
                    var st = new AppStatus();
                    AppOps.Refresh(one, st);
                    Check(st.Path == null, "a program remembered inside the temporary folder is not taken as installed");
                    Check(!State.Paths.ContainsKey("selftest-temp"), "the remembered path in the temporary folder is dropped");
                    bool launched = false;
                    try { AppOps.Launch(texe); launched = true; } catch { }
                    Check(!launched, "a program from the temporary folder is not launched");
                }
                finally
                {
                    AppOps.HomeOverride = null;
                    State.Paths.Remove("selftest-temp");
                    try { System.IO.Directory.Delete(tdir, true); } catch { }
                    try { System.IO.Directory.Delete(thome, true); } catch { }
                }
            }

            // Проверка карточек: один снимок процессов вместо обхода на каждое имя, версия файла запоминается,
            // а окно перерисовывается, только если что-то поменялось
            {
                var none = ProcSnap.Take(new string[] { "CoreTxtNoSuchProcess" });
                Check(!none.Has("CoreTxtNoSuchProcess") && none.Paths("CoreTxtNoSuchProcess").Count == 0 && System.Diagnostics.Process.GetProcessesByName("CoreTxtNoSuchProcess").Length == 0, "the process snapshot agrees with the old lookup for a missing process");
                using (var me = System.Diagnostics.Process.GetCurrentProcess())
                {
                    var mine = ProcSnap.Take(new string[] { me.ProcessName });
                    string path = me.MainModule.FileName;
                    Check(mine.Has(me.ProcessName.ToUpperInvariant()) && new System.Collections.Generic.List<string>(mine.Paths(me.ProcessName)).Exists(delegate(string p) { return string.Equals(p, path, StringComparison.OrdinalIgnoreCase); }), "the process snapshot finds a running process and its file, like MainModule");
                }
                Check(!ProcSnap.Take(new string[0]).Has("Core") && !DiscordInstall.Running(null), "an empty snapshot has nothing running");

                string vdir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-version-" + System.Diagnostics.Process.GetCurrentProcess().Id);
                try
                {
                    System.IO.Directory.CreateDirectory(vdir);
                    string exe = System.IO.Path.Combine(vdir, "Copy.exe"), self = typeof(Program).Assembly.Location;
                    Version mine = typeof(Program).Assembly.GetName().Version;
                    mine = new Version(mine.Major, mine.Minor, mine.Build);
                    System.IO.File.Copy(self, exe);
                    Check(AppOps.FileVersion(exe) == mine && AppOps.FileVersion(exe) == mine, "the file version is read, and read again from memory");
                    System.IO.File.WriteAllText(exe, "not a program any more");
                    Check(AppOps.FileVersion(exe) == new Version(0, 0, 0), "a changed file is read anew, not taken from memory");
                    System.IO.File.Copy(self, exe, true);
                    Check(AppOps.FileVersion(exe) == mine, "the version comes back with the program");
                    Check(AppOps.FileVersion(System.IO.Path.Combine(vdir, "none.exe")) == null, "a missing file has no version");

                    // Состояние: то же самое второй раз окно не перерисовывает
                    var seen = new AppEntry { Id = "selftest-seen", Name = "One", Kind = "portable", Exe = "Copy.exe", Process = "CoreTxtNoSuchProcess" };
                    AppOps.HomeOverride = vdir;   // папка проверки во временной папке — но это «дом», значит, путь годится
                    State.Paths["selftest-seen"] = exe;
                    var st = new AppStatus();
                    var procs = ProcSnap.Take(AppOps.ProcessNames(new AppEntry[] { seen }));
                    Check(AppOps.Apply(seen, st, AppOps.Look(seen, AppOps.SavedPath(seen), procs)) && st.Path == exe && st.Version == mine && !st.Running, "the first check changes the card");
                    int checks = st.Checks;
                    Check(!AppOps.Apply(seen, st, AppOps.Look(seen, AppOps.SavedPath(seen), procs)) && st.Checks == checks + 1, "the same answer again changes nothing, but counts as a check");
                    st.Running = true;
                    Check(!AppOps.Apply(seen, st, AppOps.Look(seen, AppOps.SavedPath(seen), null)) && st.Running, "a check without processes leaves «running» as it was");
                    Check(AppOps.Apply(seen, st, AppOps.Look(seen, AppOps.SavedPath(seen), procs)) && !st.Running, "a process that is gone is a change");
                    var r1 = new Release { Version = new Version(1, 2, 3), Url = "u" };
                    var a1 = new AppStatus { Path = "x", Version = new Version(1, 0, 0), Latest = r1 };
                    var a2 = new AppStatus { Path = "X", Version = new Version(1, 0, 0), Latest = new Release { Version = new Version(1, 2, 3), Url = "u" } };
                    Check(Seen.Of(a1).Same(Seen.Of(a2)), "the same state compares equal");
                    a2.Lost = true;
                    Check(!Seen.Of(a1).Same(Seen.Of(a2)), "a lost plugin is a change");
                    a2.Lost = false; a2.Latest = new Release { Version = new Version(1, 3, 0), Url = "u" };
                    Check(!Seen.Of(a1).Same(Seen.Of(a2)), "a new release is a change");
                    a2.Latest = r1; a2.Path = null;
                    Check(!Seen.Of(a1).Same(Seen.Of(a2)), "an app that disappeared is a change");
                }
                finally
                {
                    AppOps.HomeOverride = null;
                    State.Paths.Remove("selftest-seen");
                    try { System.IO.Directory.Delete(vdir, true); } catch { }
                }
            }

            // Удаление: программа уходит вместе с пустой папкой; настройки — только по просьбе и только из профиля
            {
                string home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-remove-" + System.Diagnostics.Process.GetCurrentProcess().Id);
                AppOps.HomeOverride = System.IO.Path.Combine(home, "dev.txt");
                try
                {
                    string exe = System.IO.Path.Combine(AppOps.HomeOverride, "One", "One.exe"), data = System.IO.Path.Combine(home, "data");
                    var one = new AppEntry { Id = "selftest-remove", Name = "One", Kind = "portable", Exe = "One.exe", Process = "CoreTxtNoSuchProcess", Data = data };
                    Action seed = delegate
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(exe)); System.IO.File.WriteAllText(exe, "app");
                        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(data, "deep")); System.IO.File.WriteAllText(System.IO.Path.Combine(data, "deep", "settings.txt"), "x");
                    };
                    seed();
                    AppOps.Uninstall(one, new AppStatus { Path = exe }, false);
                    Check(!System.IO.File.Exists(exe) && !System.IO.Directory.Exists(System.IO.Path.GetDirectoryName(exe)), "the program and its empty folder are removed");
                    Check(System.IO.File.Exists(System.IO.Path.Combine(data, "deep", "settings.txt")), "settings stay unless asked");
                    seed();
                    AppOps.Uninstall(one, new AppStatus { Path = exe }, true);
                    Check(!System.IO.Directory.Exists(data), "settings are erased when asked");
                    AppOps.Uninstall(one, new AppStatus(), true);   // не установлено — ничего не делает и не падает
                }
                finally { AppOps.HomeOverride = null; try { System.IO.Directory.Delete(home, true); } catch { } }

                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                Check(AppOps.SafeDataDir(System.IO.Path.Combine(roaming, "Kapsula")) && !AppOps.SafeDataDir(roaming) && !AppOps.SafeDataDir(roaming + "\\") && !AppOps.SafeDataDir("C:\\") && !AppOps.SafeDataDir("C:\\Windows\\Temp") && !AppOps.SafeDataDir(System.IO.Path.Combine(roaming, "..", "..", "Documents")) && !AppOps.SafeDataDir(""), "only a folder inside the user's profile may be erased as settings");
                var withData = Catalog.Read(@"{ ""apps"": [
                    { ""id"": ""a"", ""name"": ""A"", ""kind"": ""portable"", ""repo"": ""cxxperman/a"", ""asset"": ""A.exe"", ""exe"": ""A.exe"", ""data"": ""%APPDATA%\\dev.txt\\a"" },
                    { ""id"": ""b"", ""name"": ""B"", ""kind"": ""portable"", ""repo"": ""cxxperman/b"", ""asset"": ""B.exe"", ""exe"": ""B.exe"", ""data"": ""%APPDATA%"" },
                    { ""id"": ""c"", ""name"": ""C"", ""kind"": ""portable"", ""repo"": ""cxxperman/c"", ""asset"": ""C.exe"", ""exe"": ""C.exe"", ""data"": ""%USERPROFILE%\\Documents"" },
                    { ""id"": ""d"", ""name"": ""D"", ""kind"": ""portable"", ""repo"": ""cxxperman/d"", ""asset"": ""D.exe"", ""exe"": ""D.exe"", ""data"": ""%APPDATA%\\..\\x"" }
                ] }");
                Check(withData.Count == 4 && withData[0].Data != null && withData[1].Data == null && withData[2].Data == null && withData[3].Data == null, "the catalogue may name a settings folder only inside the profile");
                string ux, ua;
                Check(AppOps.SplitCommand("\"C:\\P\\Uninstall Aura.exe\" /currentuser /S", out ux, out ua) && ux == "C:\\P\\Uninstall Aura.exe" && ua == "/currentuser /S", "an uninstall command is split into the program and its arguments");
            }

            DiscordChecks();
            JsonDocChecks();
            SteamChecks();
            BlockMapChecks();

            // Набор (тема и плагин для Steam): принимается только в папку Steam и раскладывается строго по своим местам
            {
                var bundles = Catalog.Read(@"{ ""apps"": [
                    { ""id"": ""ok"", ""name"": ""Ok"", ""kind"": ""bundle"", ""repo"": ""cxxperman/x"", ""asset"": ""x.zip"", ""marker"": ""{steam}\\millennium\\plugins\\x\\plugin.json"", ""requires"": ""{steam}\\millennium"",
                      ""targets"": [ { ""from"": ""plugin/"", ""to"": ""{steam}\\millennium\\plugins\\x"" }, { ""from"": ""theme/"", ""to"": [ ""{steam}\\millennium\\themes\\x"", ""{steam}\\steamui\\skins\\x"" ] } ] },
                    { ""id"": ""out"", ""name"": ""Out"", ""kind"": ""bundle"", ""repo"": ""cxxperman/x"", ""asset"": ""x.zip"", ""marker"": ""{steam}\\a\\m.json"", ""targets"": [ { ""from"": ""plugin/"", ""to"": ""C:\\Windows\\x"" } ] },
                    { ""id"": ""up"", ""name"": ""Up"", ""kind"": ""bundle"", ""repo"": ""cxxperman/x"", ""asset"": ""x.zip"", ""marker"": ""{steam}\\a\\m.json"", ""targets"": [ { ""from"": ""plugin/"", ""to"": ""{steam}\\..\\x"" } ] },
                    { ""id"": ""exe"", ""name"": ""Exe"", ""kind"": ""bundle"", ""repo"": ""cxxperman/x"", ""asset"": ""x.exe"", ""marker"": ""{steam}\\a\\m.json"", ""targets"": [ { ""from"": ""plugin/"", ""to"": ""{steam}\\a"" } ] }
                ] }");
                Check(bundles.Count == 1 && bundles[0].Id == "ok" && bundles[0].Targets.Count == 2 && bundles[0].Targets[1].To.Count == 2, "a bundle may only target the Steam folder, got " + bundles.Count);

                string bdir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-bundle-" + System.Diagnostics.Process.GetCurrentProcess().Id);
                System.IO.Directory.CreateDirectory(bdir);
                try
                {
                    string zipPath = System.IO.Path.Combine(bdir, "b.zip");
                    Action<string, string[]> make = delegate(string path, string[] names)
                    {
                        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                        using (var fs = System.IO.File.Create(path))
                        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
                            foreach (string n in names) using (var w = new System.IO.StreamWriter(z.CreateEntry(n).Open())) w.Write(n);
                    };
                    make(zipPath, new string[] { "plugin/plugin.json", "plugin/.millennium/Dist/index.js", "theme\\skin.json", "readme.txt" });
                    string p1 = System.IO.Path.Combine(bdir, "plugins", "x"), p2 = System.IO.Path.Combine(bdir, "themes", "x");
                    var parts = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> {
                        new System.Collections.Generic.KeyValuePair<string, string>("plugin/", p1), new System.Collections.Generic.KeyValuePair<string, string>("theme/", p2) };
                    AppOps.ExtractBundle(zipPath, parts);
                    Check(System.IO.File.Exists(System.IO.Path.Combine(p1, "plugin.json")) && System.IO.File.Exists(System.IO.Path.Combine(p1, ".millennium", "Dist", "index.js")) && System.IO.File.Exists(System.IO.Path.Combine(p2, "skin.json")), "bundle parts land in their folders");
                    Check(System.IO.Directory.GetFiles(bdir, "readme.txt", System.IO.SearchOption.AllDirectories).Length == 0, "files outside the listed folders are not extracted");
                    make(zipPath, new string[] { "plugin/../../evil.txt" });
                    bool refused = false;
                    try { AppOps.ExtractBundle(zipPath, parts); } catch { refused = true; }
                    Check(refused && !System.IO.File.Exists(System.IO.Path.Combine(bdir, "evil.txt")), "a path that climbs out of the folder is refused");
                    make(zipPath, new string[] { "other/a.txt" });
                    refused = false;
                    try { AppOps.ExtractBundle(zipPath, parts); } catch { refused = true; }
                    Check(refused, "an archive without the expected folder is refused");
                }
                finally { try { System.IO.Directory.Delete(bdir, true); } catch { } }
            }

            // Сценарии: пока условие верно, настройка держится; потом возвращается как было
            {
                var states = new System.Collections.Generic.Dictionary<string, object>();
                var sent = new System.Collections.Generic.List<string>();
                var on = new System.Collections.Generic.HashSet<string> { "call-aura-on" };
                Func<bool, object> aura = delegate(bool power) { return Json.Parse("{\"groups\":[{\"items\":[{\"key\":\"power\",\"type\":\"switch\",\"value\":" + (power ? "true" : "false") + "}]}]}"); };
                var sc = new Scenarios(
                    delegate(string topic) { object v; return states.TryGetValue(topic, out v) ? v : null; },
                    delegate(string target, string key, object value) { sent.Add(target + "." + key + "=" + value); states["aura.settings"] = aura(value is bool && (bool)value); },
                    delegate(string id) { return on.Contains(id); });
                states["maska.voice"] = Json.Parse("{\"inCall\":true}");
                sc.Update();
                Check(sent.Count == 0 && !sc.IsActive("call-aura-on"), "scenario waits until the target app is connected");
                states["aura.settings"] = aura(false);
                sc.Update();
                Check(sent.Count == 1 && sent[0] == "aura.power=True" && sc.IsActive("call-aura-on"), "call starts — Aura is switched on");
                sc.Update();
                Check(sent.Count == 1, "nothing is sent twice");
                states["maska.voice"] = Json.Parse("{\"inCall\":false}");
                sc.Update();
                Check(sent.Count == 2 && sent[1] == "aura.power=False" && !sc.IsActive("call-aura-on"), "call ends — Aura goes back to off");

                sent.Clear();
                states["aura.settings"] = aura(true);   // была включена ещё до звонка
                states["maska.voice"] = Json.Parse("{\"inCall\":true}");
                sc.Update();
                states.Remove("maska.voice");   // Discord закрыли посреди звонка
                sc.Update();
                Check(sent.Count == 0, "what was already on is left alone, before and after");

                states["aura.settings"] = aura(false);
                states["maska.voice"] = Json.Parse("{\"inCall\":true}");
                sc.Update();
                on.Clear();   // сценарий выключили посреди звонка
                sc.Update();
                Check(sent.Count == 2 && sent[1] == "aura.power=False", "switching a running scenario off restores the setting");

                sent.Clear();
                on.Add("call-aura-on");
                states["maska.voice"] = Json.Parse("{\"inCall\":true}");
                sc.Update();
                states["aura.settings"] = aura(false);   // человек сам выключил Aura во время звонка
                states["maska.voice"] = Json.Parse("{\"inCall\":false}");
                sc.Update();
                Check(sent.Count == 1, "a setting changed by hand meanwhile is not overwritten");
                Check(!Scenarios.Truthy(null, null) && Scenarios.Truthy(Json.Parse("{}"), null) && !Scenarios.Truthy(Json.Parse("{\"mute\":0}"), "mute"), "conditions read the state correctly");
            }

            // Мелочи вида и поведения: подписи, раскладка, порядок карточек, место окна
            {
                const long mb = 1024 * 1024;
                Check(Releases.Megabytes(34 * mb + 700000, 151 * mb) == "34 из 151 МБ", "download progress is shown in megabytes, got " + Releases.Megabytes(34 * mb + 700000, 151 * mb));
                Check(Releases.Megabytes(450000, 2 * mb + 100000) == "0,4 из 2,1 МБ", "a small file is counted in tenths, got " + Releases.Megabytes(450000, 2 * mb + 100000));
                Check(Releases.Megabytes(5, 0) == "" && Releases.Megabytes(999 * mb, 20 * mb) == "20 из 20 МБ", "progress never runs past the total");

                Check(Errors.Short(new WebException("The remote name could not be resolved")) == Errors.Offline, "no network is said in Russian");
                var resetUtc = GitHubLimit.Until("1795134000", DateTime.UtcNow);
                Check(resetUtc == new DateTime(2026, 11, 20, 0, 20, 0, DateTimeKind.Utc) && GitHubLimit.Until("x", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) == new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc), "GitHub limit reset time is read from the header, an hour if missing");
                Check(Errors.Short(new GitHubLimit(resetUtc)) == Errors.Limit(resetUtc.ToLocalTime()) && Errors.Limit(new DateTime(2026, 1, 1, 18, 37, 0)) == "GitHub временно ограничил проверки — попробуйте после 18:37", "a GitHub request limit is not called a network problem");
                Check(Errors.Short(new System.IO.IOException("The process cannot access the file", unchecked((int)0x80070020))) == Errors.Busy, "a locked file asks to close the app");
                Check(Errors.Short(new System.IO.IOException("read failed", new System.Net.Sockets.SocketException(10054))) == Errors.Offline, "a dropped download is a network problem");
                Check(Errors.Short(new UnauthorizedAccessException("Access to the path is denied.")) == Errors.Denied, "no access to the folder is said in Russian");
                Check(Errors.Short(new Exception("Контрольная сумма файла не совпала с релизом")) == Errors.Checksum, "a checksum mismatch has a short phrase");
                var foreign = new InvalidOperationException("Something odd happened");
                Check(Errors.Short(foreign) == Errors.Unknown && Errors.Detail(foreign) == "Something odd happened", "an unknown error keeps its text only for the tooltip");
                var own = new Exception("Установщик не закончил работу за десять минут");
                Check(Errors.Short(own) == own.Message && Errors.Detail(own) == null, "our own messages are shown as they are");
                var st = new AppStatus();
                st.Fail("Не удалось запустить", new UnauthorizedAccessException("denied"));
                Check(st.Error == "Не удалось запустить: нет доступа к папке" && st.ErrorDetail == "denied", "an error after a colon continues in lower case, got " + st.Error);
                st.Clear();
                Check(st.Error == null && st.ErrorDetail == null, "clearing an error clears its details");
                Check(!st.Launching, "nothing is launching by default");
                st.LaunchUntil = DateTime.UtcNow.AddSeconds(6);
                Check(st.Launching, "an app is launching until it is seen running");
                st.Running = true;
                Check(!st.Launching, "a running app is no longer launching");

                double x = 5000, y = -300, w = 3000, h = 200;
                State.Fit(ref x, ref y, ref w, ref h, 0, 0, 1920, 1040, 1000, 600);
                Check(w == 1920 && h == 600 && x == 0 && y == 0, "a window is brought back onto the screen, got " + x + "," + y + " " + w + "x" + h);
                x = 300; y = 200; w = 1200; h = 700;
                State.Fit(ref x, ref y, ref w, ref h, 0, 0, 1920, 1040, 1000, 600);
                Check(x == 300 && y == 200 && w == 1200 && h == 700, "a window that fits stays where it was");
                x = -1900; y = 500; w = 1120; h = 720;
                State.Fit(ref x, ref y, ref w, ref h, -1920, 0, 1920, 1040, 1000, 600);
                Check(x == -1900 && y == 320, "a window on the left monitor stays there, got " + x + "," + y);

                Check(MainWindow.Columns(640) == 1 && MainWindow.Columns(860) == 2 && MainWindow.Columns(1239) == 2 && MainWindow.Columns(1600) == 3, "card columns follow the width");
                Check(MainWindow.AllLost(5, 5) && !MainWindow.AllLost(5, 4) && !MainWindow.AllLost(0, 0), "no connection is reported only when every request failed");
                Check(MainWindow.AccentOf(null) == Tokens.Palette.Brand && MainWindow.AccentOf("") == Tokens.Palette.Brand && MainWindow.AccentOf("nonsense") == Tokens.Palette.Brand
                    && MainWindow.AccentOf("#0A84FF") == System.Windows.Media.Color.FromRgb(0x0A, 0x84, 0xFF), "the accent falls back to the brand colour");
                var order = MainWindow.MergeOrder(new string[] { "new", "aura", "dock" }, new string[] { "aura", "gone-busy", "gone-idle", "dock" }, delegate(string id) { return id == "gone-busy" || id == "aura"; });
                Check(string.Join(",", order.ToArray()) == "new,aura,dock,gone-busy", "a catalogue update keeps a card that is still working, got " + string.Join(",", order.ToArray()));

                Status.Kind kind;
                Check(ScenariosPage.Note(true, false, new string[] { "arkada.txt" }, out kind) == "не работает: нет arkada.txt" && kind == Status.Kind.Warn, "an enabled scenario without its apps says it does not work");
                Check(ScenariosPage.Note(true, true, new string[0], out kind) == "действует сейчас" && kind == Status.Kind.Ok, "an active scenario says so");
                Check(ScenariosPage.Note(false, false, new string[] { "maska.txt", "aura.txt" }, out kind) == "не подключены: maska.txt, aura.txt" && kind == Status.Kind.None, "a disabled scenario only names what is missing");
                Check(ScenariosPage.Note(true, false, new string[0], out kind) == null && ScenariosPage.Note(false, false, null, out kind) == null, "a scenario that simply waits says nothing");

                Check(!SettingsPage.UseChips(new string[] { "Обычный", "Жёсткий" }) && !SettingsPage.UseChips(new string[] { "Не выравнивать", "−18 LUFS", "−16 LUFS", "−14 LUFS" }), "short choices sit in a row");
                Check(SettingsPage.UseChips(new string[] { "dock.txt", "Компактная панель Windows", "dock.txt и панель Windows" }) && SettingsPage.UseChips(new string[] { "1", "2", "3", "4", "5" }), "long or many choices become chips");
                var item = Json.Parse("{\"key\":\"bar\",\"type\":\"choice\",\"label\":\"Внизу\",\"hint\":\"a\",\"value\":\"dock\",\"options\":[{\"value\":\"dock\",\"label\":\"dock.txt\"},{\"value\":\"both\",\"label\":\"Оба\"}]}");
                string sig = SettingsPage.ItemSig(item);
                item["value"] = "both"; item["hint"] = "b";
                Check(SettingsPage.ItemSig(item) == sig, "a new value or hint does not rebuild the settings row");
                item["label"] = "Вверху";
                Check(SettingsPage.ItemSig(item) != sig, "a new label does rebuild it");
            }

            // Семья: core.family и «Не беспокоить»
            {
                long now = 1800000000000L;
                Check(CoreFamily.FocusExpired(true, now - 1, now) && CoreFamily.FocusExpired(true, now, now), "«do not disturb» whose end has come turns off");
                Check(!CoreFamily.FocusExpired(true, 0, now) && !CoreFamily.FocusExpired(true, now + 60000, now) && !CoreFamily.FocusExpired(false, now - 1, now), "until switched off, or still ahead, or off — nothing to do");
                Check(CoreFamily.FocusUntil(60, now) == now + 3600000 && CoreFamily.FocusUntil(0, now) == 0 && CoreFamily.FocusUntil(-5, now) == 0 && CoreFamily.FocusUntil(double.NaN, now) == 0, "minutes become an end time; none — until switched off");
                Check(CoreFamily.FocusUntil(1e12, now) == now + CoreFamily.MaxFocusMinutes * 60000, "a huge time is capped at a week");
                Check(CoreFamily.Minutes(30) == 30 && CoreFamily.Minutes("15") == 15 && CoreFamily.Minutes(null) == 0 && CoreFamily.Minutes(true) == 0 && CoreFamily.Minutes("x") == 0, "minutes are read from the command");

                var fam = CoreFamily.Make(true, true, true, now + 60000, true, "cs2");
                var data = fam.ToData();
                var fields = new System.Collections.Generic.List<string>(data.Keys);
                fields.Sort(StringComparer.Ordinal);
                Check(string.Join(",", fields.ToArray()) == "focus,focusUntil,game,gameApp,tray", "core.family has exactly the protocol fields, got " + string.Join(",", fields.ToArray()));
                var trip = FamilyState.From(Json.Parse(Json.Write(data)));
                Check(trip != null && trip.Tray && trip.Focus && trip.FocusUntil == now + 60000 && trip.Game && trip.GameApp == "cs2", "core.family survives the trip through the hub");
                Check(Json.Write(CoreFamily.Make(true, true, false, 0, false, null).ToData()) == "{\"tray\":true,\"focus\":false,\"focusUntil\":0,\"game\":false,\"gameApp\":\"\"}", "core.family looks exactly as in PROTOCOL.md");
                Check(!CoreFamily.Make(true, false, false, 0, false, null).Tray && !CoreFamily.Make(false, true, false, 0, false, null).Tray, "one tray icon only when the setting is on and core.txt really has its icon");
                var off = CoreFamily.Make(true, true, false, now + 5, false, "cs2");
                Check(off.FocusUntil == 0 && off.GameApp == "", "focus off has no end time; no game has no game name");

                var t = new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Local);
                Check(CoreFamily.Until(t.AddMinutes(90), t) == "15:30" && CoreFamily.Until(t.AddHours(19), t) == "завтра, 09:00" && CoreFamily.Until(new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Local), t) == "25 ноября, 09:00",
                    "the end is said as a time, «tomorrow» or a date, got " + CoreFamily.Until(new DateTime(2026, 11, 25, 9, 0, 0, DateTimeKind.Local), t));
                long until = (long)(t.AddMinutes(90).ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                Check(CoreFamily.FocusLabel(CoreFamily.Make(true, true, true, until, false, null), t) == "Не беспокоить до 15:30", "the tray item says until when, got " + CoreFamily.FocusLabel(CoreFamily.Make(true, true, true, until, false, null), t));
                Check(CoreFamily.FocusLabel(CoreFamily.Make(true, true, true, 0, false, null), t) == "Не беспокоить" && CoreFamily.FocusLabel(CoreFamily.Make(true, true, false, until, false, null), t) == "Не беспокоить" && CoreFamily.FocusLabel(null, t) == "Не беспокоить", "without an end time the item is plain");
                Check(CoreFamily.Cut("  metla.txt ", 60) == "metla.txt" && CoreFamily.Cut(new string('а', 250), 200).Length == 200 && CoreFamily.Cut(null, 5) == "", "island notices are trimmed to the protocol limits");

                // Команда show: какую страницу открыть
                Func<object, string> show = delegate(object p) { return CoreFamily.ShowPage(p == null ? new System.Collections.Generic.Dictionary<string, object>() : Hub.Msg("page", p)); };
                Check(show("games") == "games" && show("keys") == "keys" && show(" General ") == "general" && show("app:kapsula") == "app:kapsula" && show("replace") == "replace", "known pages are opened");
                Check(show(null) == null && show("nonsense") == null && show("app:") == null && show("app:../x") == null && show("app:Kap sula") == null && show(5) == null && CoreFamily.ShowPage(null) == null, "an unknown page just shows the window");

                // Команда focus.set: on обязательно, minutes — по желанию
                bool fon;
                double fmin;
                Check(CoreFamily.FocusArgs(Hub.Msg("on", true, "minutes", 30), out fon, out fmin) == null && fon && fmin == 30, "focus.set with minutes is understood");
                Check(CoreFamily.FocusArgs(Hub.Msg("on", false), out fon, out fmin) == null && !fon && fmin == 0, "focus.set off is understood");
                Check(CoreFamily.FocusArgs(Hub.Msg("on", "yes"), out fon, out fmin) != null && CoreFamily.FocusArgs(new System.Collections.Generic.Dictionary<string, object>(), out fon, out fmin) != null && CoreFamily.FocusArgs(null, out fon, out fmin) != null, "focus.set without a clear on is refused");

                // Через настоящий хаб (без сети): core.family ждёт тех, кто подпишется позже, focus.set доходит до core.txt
                var hub = new Hub(null, false);
                var toCore = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
                var toApp = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
                var coreClient = hub.Open(delegate(System.Collections.Generic.Dictionary<string, object> m) { toCore.Add(m); }, null);
                hub.Receive(coreClient, MainWindow.Hello());
                hub.Receive(coreClient, Hub.Msg("type", "state", "topic", CoreFamily.Topic, "data", CoreFamily.Make(true, true, true, 0, false, null).ToData()));
                var appClient = hub.Open(delegate(System.Collections.Generic.Dictionary<string, object> m) { toApp.Add(m); }, null);
                hub.Receive(appClient, Hub.Msg("type", "hello", "v", 1, "app", "smena", "name", "smena.txt", "version", "0.5.0", "commands", new object[] { "tray.run" }));
                hub.Receive(appClient, Hub.Msg("type", "subscribe", "topics", new object[] { CoreFamily.Topic, "hub.apps" }));
                FamilyState heard = null;
                object appsNow = null;
                foreach (var m in toApp)
                {
                    if (Json.Str(m, "type") != "state") continue;
                    if (Json.Str(m, "topic") == CoreFamily.Topic && Json.Str(m, "from") == "core") heard = FamilyState.From(m["data"]);
                    if (Json.Str(m, "topic") == "hub.apps") appsNow = m["data"];
                }
                Check(heard != null && heard.Tray && heard.Focus && !heard.Game, "an app that connects later still hears core.family");
                Check(CoreFamily.Has(appsNow, "core", "show") && CoreFamily.Has(appsNow, "core", Family.FocusSet), "core.txt says it understands show and focus.set");
                hub.Receive(appClient, Hub.Msg("type", "command", "id", "smena-1", "to", "core", "name", Family.FocusSet, "args", Hub.Msg("on", true)));
                Check(toCore.Exists(delegate(System.Collections.Generic.Dictionary<string, object> m) { return Json.Str(m, "type") == "command" && Json.Str(m, "name") == Family.FocusSet && Json.Str(m, "from") == "smena"; }), "focus.set from an app reaches core.txt");
            }

            // Семья: пункты с полем section, общее меню в трее, кто что умеет и горячие клавиши
            {
                var st = Json.Parse("{\"groups\":[{\"title\":\"Клавиши\",\"items\":[{\"key\":\"replace\",\"type\":\"switch\"},{\"key\":\"games\",\"type\":\"switch\",\"section\":\"games\"}]},{\"title\":\"Запуск\",\"items\":[{\"key\":\"autostart\",\"type\":\"switch\",\"section\":\"startup\"},{\"key\":\"share\",\"type\":\"switch\",\"section\":\"family\"}]},{\"title\":\"Другое\",\"items\":[{\"key\":\"odd\",\"type\":\"switch\",\"section\":\"somewhere\"}]}]}");
                var own = CoreFamily.OwnGroups(st);
                Check(own.Count == 2 && own[0].Key == "Клавиши" && own[0].Value.Count == 1 && Json.Str(own[0].Value[0], "key") == "replace" && own[1].Key == "Другое", "the app page keeps its own items; a group that moved away entirely disappears");
                Check(CoreFamily.ItemsIn(st, "games").Count == 1 && Json.Str(CoreFamily.ItemsIn(st, "startup")[0], "key") == "autostart" && CoreFamily.ItemsIn(st, "family").Count == 1 && CoreFamily.ItemsIn(null, "games").Count == 0, "items with a section go to the shared pages");
                Check(string.Join(",", CoreFamily.MovedTo(st).ToArray()) == "games,startup,family" && CoreFamily.MovedTo(Json.Parse("{\"groups\":[]}")).Count == 0, "where the items went is known");
                Check(CoreFamily.SectionOf(Json.Parse("{\"section\":\"somewhere\"}")) == null && CoreFamily.SectionOf(Json.Parse("{\"section\":\"games\"}")) == "games", "an unknown section stays on the app page");
                Check(CoreFamily.MovedHint(new System.Collections.Generic.List<string> { "games" }) == "Поведение в играх — в разделе «Игры»"
                    && CoreFamily.MovedHint(CoreFamily.MovedTo(st)) == "Запуск и обновления, связь с другими приложениями — в разделе «Общие»; поведение в играх — в разделе «Игры»"
                    && CoreFamily.MovedHint(new System.Collections.Generic.List<string>()) == null, "the app page says what moved and where, got " + CoreFamily.MovedHint(CoreFamily.MovedTo(st)));

                var catalog = new System.Collections.Generic.List<AppEntry> { new AppEntry { Id = "kapsula", Name = "Kapsula", Sprite = new string[] { "#E" } }, new AppEntry { Id = "aura", Name = "Aura" }, new AppEntry { Id = "smena", Name = "Smena" }, new AppEntry { Id = "dock", Name = "Dock" } };
                var states = new System.Collections.Generic.Dictionary<string, object>();
                Func<string, object> get = delegate(string topic) { object v; return states.TryGetValue(topic, out v) ? v : null; };
                states["smena.tray"] = Json.Parse("{\"items\":[{\"key\":\"show\",\"label\":\"Открыть\"},{\"key\":\"pause\",\"label\":\"Приостановить\",\"check\":true}]}");
                states["kapsula.tray"] = Json.Parse("{\"items\":[{\"key\":\"a\",\"label\":\"1\"},{\"key\":\"b\",\"label\":\"2\",\"enabled\":false},{\"label\":\"без ключа\"},{\"key\":\"c\",\"label\":\"3\"},{\"key\":\"d\",\"label\":\"4\"},{\"key\":\"e\",\"label\":\"5\"}]}");
                states["aura.tray"] = Json.Parse("{\"items\":[]}");
                states["dock.tray"] = Json.Parse("{\"items\":[{\"key\":\"x\",\"label\":\"Автоскрытие\",\"check\":false}]}");
                states["newapp.tray"] = Json.Parse("{\"items\":[{\"key\":\"show\",\"label\":\"Открыть\"}]}");
                var connected = new System.Collections.Generic.HashSet<string> { "smena", "kapsula", "aura", "newapp", "core" };
                var byApp = CoreFamily.ByApp(catalog, delegate(string id) { return id == "smena" || id == "aura" ? st : null; }, CoreFamily.Startup);
                Check(byApp.Count == 2 && byApp[0].Key.Id == "aura" && byApp[1].Key.Id == "smena" && byApp[1].Value.Count == 1 && Json.Str(byApp[1].Value[0], "key") == "autostart"
                    && CoreFamily.ByApp(catalog, get, CoreFamily.Games).Count == 0, "shared pages group items by app, in catalogue order");
                var groups = CoreFamily.TrayGroups(catalog, connected, get);
                Check(groups.Count == 3 && groups[0].App == "kapsula" && groups[1].App == "smena" && groups[2].App == "newapp", "tray groups follow the catalogue; empty and disconnected apps are left out, unknown ones come last");
                Check(groups.Count == 3 && groups[0].Items.Count == 4 && !groups[0].Items[1].Enabled && groups[0].Items[2].Key == "c" && groups[0].Name == "kapsula.txt" && groups[0].Sprite != null, "at most four items; a disabled one stays visible");
                Check(groups.Count == 3 && groups[1].Items[1].Check == true && groups[1].Items[0].Check == null && groups[2].Name == "newapp.txt" && groups[2].Sprite == null, "check marks are read; an app outside the catalogue still has a name");

                var hubApps = Json.ParseAny("[{\"app\":\"kapsula\",\"commands\":[\"show\",\"notify\"]},{\"app\":\"aura\",\"commands\":[\"show\"]}]");
                Check(CoreFamily.Has(hubApps, "kapsula", "notify") && !CoreFamily.Has(hubApps, "aura", "notify") && !CoreFamily.Has(hubApps, "metla", "show") && !CoreFamily.Has(null, "kapsula", "notify"), "notices go to the island only when it says it can take them");

                states["kapsula.keys"] = Json.Parse("{\"keys\":[{\"id\":\"dictate\",\"label\":\"Диктовка\",\"keys\":\"Ctrl+Alt+D\",\"on\":true},{\"id\":\"rec\",\"label\":\"Запись экрана\",\"keys\":\"Ctrl+Alt+R\",\"on\":false},{\"id\":\"area\",\"label\":\"Снимок\",\"keys\":\"Ctrl+Alt+S\",\"on\":true},{\"id\":\"copy\",\"label\":\"Копия\",\"keys\":\"ctrl + alt + s\",\"on\":true}]}");
                states["aura.keys"] = Json.Parse("{\"keys\":[{\"id\":\"mute\",\"label\":\"Заглушить микрофон\",\"keys\":\"alt+ctrl+d\",\"on\":true},{\"id\":\"rec\",\"label\":\"Запись\",\"keys\":\"Ctrl+Alt+R\",\"on\":true},{\"id\":\"none\",\"label\":\"Пусто\",\"keys\":\"\",\"on\":true}]}");
                states["dock.keys"] = Json.Parse("{\"keys\":[{\"id\":\"menu\",\"label\":\"Меню\",\"keys\":\"Ctrl+Alt+D\",\"on\":true}]}");   // dock не подключён
                var rows = CoreFamily.KeyRows(catalog, connected, get);
                int clashes = CoreFamily.FindClashes(rows);
                Check(rows.Count == 7 && rows[0].App == "kapsula" && rows[4].App == "aura" && clashes == 2, "keys of connected apps in catalogue order, two clashes, got " + rows.Count + " rows, " + clashes + " clashes");
                Check(rows.Count == 7 && CoreFamily.ClashText(rows[0]) == "то же сочетание: «Заглушить микрофон» в aura.txt" && CoreFamily.ClashText(rows[4]) == "то же сочетание: «Диктовка» в kapsula.txt", "a clash between apps names the other app and action");
                Check(rows.Count == 7 && CoreFamily.ClashText(rows[2]) == "то же сочетание: «Копия» в этом же приложении", "a clash inside one app is found too");
                Check(rows.Count == 7 && rows[1].Clash.Count == 0 && rows[5].Clash.Count == 0 && rows[6].Clash.Count == 0 && CoreFamily.ClashText(rows[1]) == null, "a switched-off or empty key clashes with nothing");
                Check(string.Join(" ", CoreFamily.Caps("alt+ctrl+space").ToArray()) == "Ctrl Alt Пробел" && string.Join(" ", CoreFamily.Caps("Win").ToArray()) == "Win" && CoreFamily.Caps("").Count == 0, "keys are shown one by one, as on the keyboard");
            }

            // Замена файла программы: новая версия ложится на место старой, лишнего рядом не остаётся
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-test-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string target = System.IO.Path.Combine(dir, "app", "One.exe"), fresh = System.IO.Path.Combine(dir, "download.exe");
                AppOps.HomeOverride = dir;   // проверка не должна оставлять ярлыков в настоящем меню «Пуск»
                var one = new AppEntry { Id = "selftest-one", Name = "One", Kind = "portable", Exe = "One.exe", Process = "CoreTxtNoSuchProcess" };
                System.IO.File.WriteAllText(fresh, "new");
                AppOps.Install(one, new AppStatus { Path = target }, fresh);
                Check(System.IO.File.Exists(target) && System.IO.File.ReadAllText(target) == "new", "first install puts the file in place");
                System.IO.File.WriteAllText(fresh, "newer");
                AppOps.Install(one, new AppStatus { Path = target }, fresh);
                Check(System.IO.File.ReadAllText(target) == "newer", "update replaces the file");
                Check(System.IO.Directory.GetFiles(System.IO.Path.GetDirectoryName(target)).Length == 1 && !System.IO.File.Exists(fresh), "nothing is left behind");
                State.Paths.Remove("selftest-one");
            }
            finally { AppOps.HomeOverride = null; try { System.IO.Directory.Delete(dir, true); } catch { } }
            Check(System.IO.File.Exists(State.FilePath), "install, move and removal saved their state into the temporary folder");

            // Что core.txt помнит о семье, переживает перезапуск; испорченное время окончания выключает «Не беспокоить»,
            // а не роняет окно и меню
            {
                long now = FamilyState.Now();
                State.FamilyTray = false; State.Focus = true; State.FocusUntil = now + 3600000;
                State.Save();
                State.FamilyTray = true; State.Focus = false; State.FocusUntil = 0;
                State.Load();
                Check(!State.FamilyTray && State.Focus && State.FocusUntil == now + 3600000, "one tray icon and «do not disturb» with its end survive a restart");
                State.Focus = true; State.FocusUntil = 0;
                State.Save();
                State.Focus = false;
                State.Load();
                Check(State.Focus && State.FocusUntil == 0, "«do not disturb» until switched off survives a restart");
                System.IO.File.WriteAllText(State.FilePath, "{\"settings\":{\"focus\":true,\"focusUntil\":500000000000000}}");
                State.Load();
                Check(!State.Focus && State.FocusUntil == 0 && State.FamilyTray, "a broken end time turns «do not disturb» off; one tray icon is on by default");
                System.IO.File.WriteAllText(State.FilePath, "{\"settings\":{\"focus\":true,\"focusUntil\":" + (now - 60000) + "}}");
                State.Load();
                Check(State.Focus && CoreFamily.FocusExpired(State.Focus, State.FocusUntil, now), "an end that passed while core.txt was closed turns it off on the first check");
                Check(CoreFamily.FocusLabel(CoreFamily.Make(true, true, true, 500000000000000L, false, null), DateTime.Now) == "Не беспокоить" && CoreFamily.Ends(CoreFamily.Make(true, true, true, long.MaxValue, false, null)) == null,
                    "a broken end time is not shown and does not throw");
                Check(CoreFamily.UntilOk(CoreFamily.FocusUntil(1e9, now), now) && !CoreFamily.UntilOk(now + (CoreFamily.MaxFocusMinutes + 2) * 60000.0, now) && !CoreFamily.UntilOk(0, now), "only an end within a week is accepted");
                State.Frozen = true;
                System.IO.File.Delete(State.FilePath);
                State.Save();
                Check(!System.IO.File.Exists(State.FilePath), "the demo writes nothing");
                State.Frozen = false;
                State.Focus = false; State.FocusUntil = 0; State.FamilyTray = true;
            }

            // Уведомления core.txt и core.family: куда уходит уведомление, и одно и то же состояние второй раз не шлётся
            {
                Check(CoreFamily.NotifyRoute(true, true, true, true) == "island" && CoreFamily.NotifyRoute(false, true, false, false) == "balloon"
                    && CoreFamily.NotifyRoute(false, true, true, false) == null && CoreFamily.NotifyRoute(false, true, false, true) == null && CoreFamily.NotifyRoute(false, false, false, false) == null,
                    "a notice goes to the island; without it — to Windows, but not during «do not disturb», a game, or without an icon");
                var fo = new FamilyOut();
                Check(fo.Changed(CoreFamily.Make(true, true, false, 0, false, null)) && !fo.Changed(CoreFamily.Make(true, true, false, 0, false, null))
                    && fo.Changed(CoreFamily.Make(true, true, true, 0, false, null)) && fo.Changed(CoreFamily.Make(true, true, false, 0, false, null)), "core.family is sent only when it changes");
            }

            // Клавиши — как на клавиатуре, а не английскими словами кода; плюс на цифровой клавиатуре не теряется
            {
                Func<int, int, string> caps = delegate(int mods, int vk) { return string.Join("|", CoreFamily.Caps(KeyNames.Text(mods, vk)).ToArray()); };
                Check(caps(2, 0x6B) == "Ctrl|Num +" && CoreFamily.Norm("ctrl + num+") == "Ctrl+Num+" && CoreFamily.Norm("Ctrl+Num+") != CoreFamily.Norm("Ctrl+Num-") && CoreFamily.Norm("Num+") == "Num+",
                    "the keypad plus keeps its plus, got " + caps(2, 0x6B));
                Check(caps(3, 0x22) == "Ctrl|Alt|PgDn" && caps(0, 0xB3) == "Воспр./пауза" && caps(0, 0xAF) == "Громче" && caps(1, 0xC0) == "Alt|Ё" && caps(8, 0x5D) == "Win|Меню"
                    && caps(2, 0x99) == "Ctrl|Клавиша 99" && caps(0, 0x61) == "Num 1" && caps(4, 0x2C) == "Shift|PrtSc",
                    "keys are named as on the keyboard, got " + caps(3, 0x22) + " " + caps(1, 0xC0) + " " + caps(8, 0x5D));
            }

            // Сценарии про игры: игра на весь экран (core.family) или игра из Steam (arkada.game) — хватит одного
            {
                var states = new System.Collections.Generic.Dictionary<string, object>();
                var sent = new System.Collections.Generic.List<string>();
                Func<bool, object> kapsula = delegate(bool hide) { return Json.Parse("{\"groups\":[{\"items\":[{\"key\":\"autoHide\",\"type\":\"switch\",\"value\":" + (hide ? "true" : "false") + "},{\"key\":\"walk\",\"type\":\"switch\",\"value\":true}]}]}"); };
                var sc = new Scenarios(
                    delegate(string topic) { object v; return states.TryGetValue(topic, out v) ? v : null; },
                    delegate(string target, string key, object value) { sent.Add(target + "." + key + "=" + value); if (key == "autoHide") states["kapsula.settings"] = kapsula(value is bool && (bool)value); },
                    delegate(string id) { return id == "game-hide-island"; });
                states["kapsula.settings"] = kapsula(false);
                Check(!sc.Update() && sent.Count == 0, "no game — nothing to do");
                states[CoreFamily.Topic] = CoreFamily.Make(true, true, false, 0, true, "cs2").ToData();
                Check(sc.Update() && sent.Count == 1 && sent[0] == "kapsula.autoHide=True" && sc.IsActive("game-hide-island"), "a full-screen game hides the island without arkada.txt");
                states[CoreFamily.Topic] = CoreFamily.Make(true, true, false, 0, false, null).ToData();
                states["arkada.game"] = Json.Parse("{\"appId\":730,\"name\":\"Counter-Strike 2\",\"since\":1}");
                Check(!sc.Update() && sent.Count == 1 && sc.IsActive("game-hide-island"), "a Steam game in a window keeps the island hidden");
                states.Remove("arkada.game");
                Check(sc.Update() && sent.Count == 2 && sent[1] == "kapsula.autoHide=False" && !sc.IsActive("game-hide-island"), "no game at all — the island comes back");
                states["kapsula.settings"] = kapsula(true);
                states[CoreFamily.Topic] = CoreFamily.Make(true, true, false, 0, true, "cs2").ToData();
                Check(sc.Update() && sent.Count == 2 && sc.IsActive("game-hide-island"), "a scenario that needs no change still says it now works — its note is refreshed");
                Check(Array.TrueForAll(Scenarios.All, delegate(Scenario x) { return !x.Game || Array.IndexOf(x.Needs, "arkada") < 0; }), "game scenarios do not need arkada.txt");
            }

            // async-метод, начатый до app.Run() (проверка обновлений из конструктора окна), продолжается в потоке окна,
            // а не в пуле потоков: иначе первое же обращение к окну роняло core.txt
            {
                Program.UiContext();
                int ui = Thread.CurrentThread.ManagedThreadId, after = -1;
                var frame = new DispatcherFrame();
                var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                stop.Tick += delegate { stop.Stop(); frame.Continue = false; };
                stop.Start();
                AfterAwait(delegate(int id) { after = id; frame.Continue = false; });
                if (after == -1) Dispatcher.PushFrame(frame);
                stop.Stop();
                Check(after == ui, "work started before the window loop continues on the window thread");
            }
        }

        public static int Latest()
        {
            Catalog.Load();
            int bad = 0;
            foreach (var app in Catalog.Apps)
            {
                if (app.IsIntegration) continue;
                if (app.Soon) { Console.WriteLine(app.Id + ": not released yet"); continue; }
                try
                {
                    Release r = Releases.Latest(app);
                    Console.WriteLine(app.Id + ": " + (r == null ? "no release" : r.Version + " " + r.Name + " " + r.Size + " sha " + (r.Sha == null ? "none" : "ok")));
                    if (r == null || r.Sha == null) bad++;
                }
                catch (Exception e) { bad++; Console.WriteLine(app.Id + ": " + e.Message); }
            }
            return bad;
        }

        // То же, что кнопка «Установить» или «Обновить» на карточке, только без окна
        public static int InstallNow(string id)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            State.Load();
            Catalog.Load();
            AppEntry app = Catalog.Apps.Find(delegate(AppEntry a) { return a.Id == id; });
            if (app == null || app.IsIntegration) { Console.WriteLine("нет такого приложения в каталоге: " + id); return 2; }
            try
            {
                var s = new AppStatus();
                AppOps.Refresh(app, s);
                Console.WriteLine("было: " + (s.Installed ? (s.Version == null ? "установлено" : s.Version.ToString()) : "не установлено"));
                s.Latest = Releases.Latest(app);
                if (s.Latest == null) { Console.WriteLine("в релизах нет файла " + app.Asset); return 3; }
                if (s.Installed && s.Version != null && s.Latest.Version <= s.Version) { Console.WriteLine("уже последняя версия " + s.Version); return 0; }
                string file = Releases.Download(s.Latest, app.Id, null);
                AppOps.Install(app, s, file);
                AppOps.Refresh(app, s);
                Console.WriteLine("стало: " + (s.Installed ? (s.Version == null ? "установлено" : s.Version.ToString()) + " — " + s.Path : "не установлено"));
                return s.Installed ? 0 : 4;
            }
            catch (Exception e) { Console.WriteLine("не получилось: " + e.Message); return 1; }
        }

        // То же, что «Удалить» на карточке без галочки «Стереть и настройки»
        public static int UninstallNow(string id)
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            State.Load();
            Catalog.Load();
            AppEntry app = Catalog.Apps.Find(delegate(AppEntry a) { return a.Id == id; });
            if (app == null || !app.Removable) { Console.WriteLine("нет такого приложения в каталоге: " + id); return 2; }
            try
            {
                var s = new AppStatus();
                AppOps.Refresh(app, s);
                if (!s.Installed) { Console.WriteLine("не установлено"); return 0; }
                Console.WriteLine("было: " + s.Path);
                AppOps.Uninstall(app, s, false);
                AppOps.Refresh(app, s);
                InstalledApps.Sync(app, s);   // удалили из «Параметров» Windows — запись там тоже убирается
                Console.WriteLine("стало: " + (s.Installed ? "всё ещё установлено — " + s.Path : "не установлено"));
                return s.Installed ? 4 : 0;
            }
            catch (Exception e) { Console.WriteLine("не получилось: " + e.Message); return 1; }
        }

        public static int Fetch(string url, string sha, string size)
        {
            try
            {
                string file = Releases.Download(new Release { Url = url, Sha = sha, Size = long.Parse(size), Name = "fetch-test.bin" }, null);
                Console.WriteLine("ok " + new System.IO.FileInfo(file).Length);
                Releases.TryDelete(file);
                return 0;
            }
            catch (Exception e) { Console.WriteLine("refused: " + e.Message); return 1; }
        }

        public static int RangeNow(string url, string at, string length)
        {
            try
            {
                var ms = new System.IO.MemoryStream();
                using (var s = Releases.Range(url, long.Parse(at), long.Parse(length))) s.CopyTo(ms);
                Console.WriteLine("ok " + ms.Length + " " + Sha(ms.ToArray()));
                return 0;
            }
            catch (Exception e) { Console.WriteLine("refused: " + e.Message); return 1; }
        }

        // Настоящей карты блоков в релизах пока нет, поэтому карты строятся тут: новая — по целой копии файла,
        // «прежняя» — по той же копии с тремя испорченными местами. Сборка идёт тем же путём, что и обновление.
        public static int PatchNow(string url, string sha, string size, string copy)
        {
            string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "core.txt-patch-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            State.DirOverride = temp;
            try
            {
                byte[] whole = System.IO.File.ReadAllBytes(copy), damaged = (byte[])whole.Clone();
                foreach (double share in new[] { 0.1, 0.5, 0.9 }) for (int i = 0; i < 1000; i++) damaged[(int)(whole.Length * share) + i] ^= 0x5A;
                const long blockSize = 16 * 1024;
                string id = "selftest-patch", src = System.IO.Path.Combine(temp, "old.bin");
                System.IO.Directory.CreateDirectory(temp);
                System.IO.File.WriteAllBytes(src, damaged);
                Stash.Put(id, src, System.Text.Encoding.UTF8.GetBytes(MapJson(damaged, 0, Even(damaged.Length, blockSize))));
                byte[] map = Gzip(System.Text.Encoding.UTF8.GetBytes(MapJson(whole, 0, Even(whole.Length, blockSize))));
                var r = new Release { Url = url, Sha = sha.ToLowerInvariant(), Size = long.Parse(size), Name = "patch-test.bin" };
                string part = System.IO.Path.Combine(temp, "new.part");
                long done = 0, total = 0;
                bool ok = Releases.TryPatch(r, id, map, part, delegate(long d, long t) { done = d; total = t; });
                Console.WriteLine(ok ? "ok: fetched " + done + " of " + total + " bytes, file " + new System.IO.FileInfo(part).Length + " bytes" : "refused: fell back to the whole file");
                return ok ? 0 : 1;
            }
            catch (Exception e) { Console.WriteLine("refused: " + e.Message); return 1; }
            finally { State.DirOverride = null; try { System.IO.Directory.Delete(temp, true); } catch { } }
        }
    }
}
