// core.txt — лаунчер dev.txt: одно окно для всех приложений: поставить, обновить, запустить.
// Ключи для проверки:
//   --demo              выдуманные состояния приложений; сеть и диск не трогаются
//   --shot файл.png     нарисовать окно в файл и выйти (обычно вместе с --demo)
//   --motion папка      вместе с --demo: показать окно, проиграть все действия и сложить кадры движения в папку
//   --page имя          открыть сразу страницу: scenarios, settings или welcome; --scroll N — прокрутить её (для снимков)
//   --tray              запуститься без окна, только значок в трее и хаб
//   --selftest          проверить разбор каталога, ответа GitHub и замену файла; код выхода 0 — всё в порядке
//   --hub порт          запустить только хаб на этом порту, без окна (для проверок из kit.txt)
//   --uninstall имя     удалить приложение без окна; настройки остаются
//   --install имя       поставить или обновить приложение из каталога без окна и напечатать, чем кончилось
//   --latest            спросить у GitHub последние версии приложений из каталога и напечатать их
//   --fetch адрес сумма размер   скачать файл с проверкой размера и SHA-256 (для проверки на своём компьютере)
using System;
using System.Net;
using System.Threading;
using System.Windows;

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

            int page = 0;
            double scroll = 0;
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--page") page = args[i + 1] == "scenarios" ? 1 : args[i + 1] == "settings" ? 2 : args[i + 1] == "welcome" ? 3 : 0;
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
                // Уже запущен (возможно, сидит в трее): просим показать окно и выходим
                try { EventWaitHandle.OpenExisting("devtxt_core_show").Set(); } catch { }
                return 0;
            }

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (!demo) { State.Load(); SelfUpdate.Cleanup(); }
            Catalog.Load();
            var w = new MainWindow(demo, page, Array.IndexOf(args, "--after-install") >= 0);
            if (shot != null) { w.Shot(shot, scroll); return 0; }
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
    }

    static class SelfTest
    {
        static int failed;
        static void Check(bool ok, string what) { if (!ok) { failed++; Console.Error.WriteLine("FAIL  " + what); } }

        public static int Run()
        {
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

            Console.WriteLine(failed == 0 ? "all passed" : failed + " failed");
            return failed == 0 ? 0 : 1;
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
                string file = Releases.Download(s.Latest, null);
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
    }
}
