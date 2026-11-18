// core.txt. AppOps.cs — что с приложением сейчас (стоит ли, какая версия, запущено ли) и действия над ним:
// поставить, обновить, запустить.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DevTxt.Core
{
    enum Phase { Idle, Downloading, Installing, Removing }

    class AppStatus
    {
        public string Path;          // где лежит программа (или файл-отметка подключения); null — не установлено
        public Version Version;
        public bool Running;
        public Release Latest;
        public string Error;
        public Phase Phase;
        public double Progress;

        public bool Installed { get { return Path != null; } }
        public bool UpdateAvailable { get { return Installed && Version != null && Latest != null && Latest.Version > Version; } }
    }

    static class AppOps
    {
        static string SteamDir()
        {
            try { return (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? "").Replace('/', '\\'); }
            catch { return ""; }
        }

        public static string Expand(string path)
        {
            if (path.Contains("{steam}"))
            {
                string steam = SteamDir();
                if (steam == "") return null;
                path = path.Replace("{steam}", steam);
            }
            return Environment.ExpandEnvironmentVariables(path);
        }

        // Один дом на всю экосистему: «Программы\dev.txt». Здесь лежит сам core.txt и по папке на каждое приложение.
        public static string HomeOverride;   // для проверок
        public static string Home
        {
            get { return HomeOverride ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "dev.txt"); }
        }

        public static string DefaultPath(AppEntry app)
        {
            if (app.Kind == "installer") return Expand(app.Installed);
            return System.IO.Path.Combine(Home, app.Name, app.Exe);
        }

        // Куда приложение ставил core.txt 0.1.0 — чтобы найти его и перенести в общий дом
        static string LegacyPath(AppEntry app)
        {
            if (app.Kind != "portable") return null;
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", app.Name, app.Exe);
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Путь к программе из записи автозапуска вида «"C:\…\App.exe" --ключ» (или без кавычек)
        public static string PathFromCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return null;
            command = command.Trim();
            if (command.StartsWith("\"")) { int end = command.IndexOf('"', 1); return end > 1 ? command.Substring(1, end - 1) : null; }
            int exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? command.Substring(0, exe + 4) : null;
        }

        // Переносная программа может лежать где угодно. Если она сейчас не запущена, её выдаёт запись в автозапуске Windows.
        static string AutostartPath(AppEntry app)
        {
            // Имя файла программы: у переносной оно в каталоге, у поставленной установщиком — в конце её пути
            string exe = app.Kind == "portable" ? app.Exe : app.Kind == "installer" && app.Installed != null ? System.IO.Path.GetFileName(app.Installed) : null;
            if (string.IsNullOrEmpty(exe)) return null;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return null;
                    foreach (string name in k.GetValueNames())
                    {
                        string path = PathFromCommand(k.GetValue(name) as string);
                        if (path != null && string.Equals(System.IO.Path.GetFileName(path), exe, StringComparison.OrdinalIgnoreCase)) return path;
                    }
                }
            }
            catch { }
            return null;
        }

        public static string ReplacePath(string command, string from, string to)
        {
            int at = command.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? command : command.Substring(0, at) + to + command.Substring(at + from.Length);
        }

        // Программа переехала — записи автозапуска, которые вели на старое место, ведут на новое
        static void MoveAutostart(string from, string to)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string command = k.GetValue(name) as string;
                        if (command != null && command.IndexOf(from, StringComparison.OrdinalIgnoreCase) >= 0) k.SetValue(name, ReplacePath(command, from, to));
                    }
                }
            }
            catch { }
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return false; }
        }

        // Убирает записи автозапуска, которые ведут на этот файл
        static void RemoveAutostart(string path)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string target = PathFromCommand(k.GetValue(name) as string);
                        if (target != null && SamePath(target, path)) k.DeleteValue(name, false);
                    }
                }
            }
            catch { }
        }

        // Папку с настройками можно стереть, только если она лежит внутри профиля пользователя и не есть сам профиль
        public static bool SafeDataDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            string full;
            try { full = System.IO.Path.GetFullPath(dir).TrimEnd('\\'); } catch { return false; }
            foreach (var root in new Environment.SpecialFolder[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
            {
                string r = Environment.GetFolderPath(root).TrimEnd('\\');
                if (full.Length > r.Length + 1 && full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // Команда удаления, которую программа оставила Windows: ищем ту, чей деинсталлятор лежит в папке программы
        public static bool FindUninstaller(string installDir, out string exe, out string args)
        {
            exe = null; args = null;
            try
            {
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (root == null) return false;
                    foreach (string name in root.GetSubKeyNames())
                        using (var k = root.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            string quiet = k.GetValue("QuietUninstallString") as string, plain = k.GetValue("UninstallString") as string;
                            string command = quiet ?? plain;
                            string path = PathFromCommand(command);
                            if (path == null || !File.Exists(path) || !SamePath(System.IO.Path.GetDirectoryName(path), installDir)) continue;
                            if (SplitCommand(command, out exe, out args))
                            {
                                if (quiet == null) args = (args + " /S").Trim();   // тихий ключ установщиков этого вида
                                return true;
                            }
                        }
                }
            }
            catch { }
            return false;
        }

        public static bool SplitCommand(string command, out string exe, out string args)
        {
            exe = PathFromCommand(command); args = "";
            if (exe == null) return false;
            int at = command.IndexOf(exe, StringComparison.OrdinalIgnoreCase) + exe.Length;
            args = command.Substring(at).TrimStart('"').Trim();
            return true;
        }

        // Убирает приложение с компьютера. withData — стереть и его настройки (только если каталог знает, где они).
        public static void Uninstall(AppEntry app, AppStatus s, bool withData)
        {
            if (!s.Installed) return;
            if (app.IsBundle)
            {
                // Набор: убираются папки, в которые он раскладывался — они целиком наши
                foreach (var t in app.Targets)
                    foreach (string to in t.To)
                    {
                        string dir = Expand(to);
                        try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
                        catch { throw new Exception("Не удалось убрать папку — закройте Steam и попробуйте ещё раз"); }
                    }
                return;
            }
            string path = s.Path;
            if (app.Kind == "installer")
            {
                // Удаляет собственный деинсталлятор программы, как это делает Windows: он знает, что ещё она оставила в системе
                string exe, args;
                if (!FindUninstaller(System.IO.Path.GetDirectoryName(path), out exe, out args)) throw new Exception("Не найден деинсталлятор — удалите через «Параметры Windows → Приложения»");
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true }))
                    if (p != null) p.WaitForExit(120000);
                // Такой деинсталлятор сразу перезапускает себя из временной папки, поэтому ждём не его, а исчезновения программы
                for (int i = 0; i < 240 && File.Exists(path); i++) Thread.Sleep(500);
                if (File.Exists(path)) throw new Exception("Деинсталлятор не закончил работу — проверьте, не ждёт ли он вашего ответа");
            }
            else
            {
                CloseRunning(app, path);
                Exception last = null;
                for (int i = 0; i < 20; i++)   // файл освобождается не мгновенно
                {
                    try { File.Delete(path); last = null; break; }
                    catch (Exception e) { last = e; Thread.Sleep(250); }
                }
                if (last != null) throw new Exception("Не удалось удалить файл программы: " + (last is UnauthorizedAccessException ? "нет прав на её папку" : "он занят"));
                Releases.TryDelete(path + ".old");
                try { string dir = System.IO.Path.GetDirectoryName(path); if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }
                RemoveAutostart(path);
                Shortcuts.Remove(app.Name);
            }
            State.Paths.Remove(app.Id);
            State.Save();
            if (withData && app.Data != null)
            {
                string data = Expand(app.Data);
                if (!SafeDataDir(data)) throw new Exception("Программа удалена, но папку с настройками трогать небезопасно — она осталась на месте");
                try { if (Directory.Exists(data)) Directory.Delete(data, true); }
                catch { throw new Exception("Программа удалена, но часть настроек занята и осталась на месте"); }
            }
        }

        // Стоит не в общем доме — можно перенести. Только для переносных: то, что ставил установщик, остаётся на его месте.
        public static bool Outside(AppEntry app, AppStatus s)
        {
            return app.Kind == "portable" && s.Path != null && !SamePath(s.Path, DefaultPath(app));
        }

        // Закрывает программу, если запущен именно этот файл. true — была запущена.
        static bool CloseRunning(AppEntry app, string path)
        {
            bool was = false;
            if (string.IsNullOrEmpty(app.Process)) return false;
            foreach (var p in Process.GetProcessesByName(app.Process))
            {
                try
                {
                    if (!SamePath(p.MainModule.FileName, path)) continue;   // другая копия — не наша забота
                    was = true;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return was;
        }

        // Переносит программу в общий дом: закрывает её, перекладывает файл, поправляет автозапуск и ярлык, запускает снова.
        // Если в доме уже лежит копия не старее — лишняя снаружи просто убирается.
        public static void Gather(AppEntry app, AppStatus s)
        {
            if (!Outside(app, s)) return;
            string from = s.Path, to = DefaultPath(app);
            bool wasRunning = CloseRunning(app, from);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
            Exception last = null;
            for (int i = 0; i < 20; i++)   // файл освобождается не мгновенно
            {
                try
                {
                    if (File.Exists(to))
                    {
                        Version have = FileVersion(to), bring = FileVersion(from);
                        if (have != null && bring != null && bring > have) File.Delete(to);
                        else { File.Delete(from); last = null; break; }
                    }
                    File.Move(from, to);
                    last = null;
                    break;
                }
                catch (Exception e) { last = e; Thread.Sleep(250); }
            }
            if (last != null) throw new Exception("Не удалось перенести " + app.Name + ": " + (last is UnauthorizedAccessException ? "нет прав на её папку" : "файл занят"));
            Releases.TryDelete(from + ".old");
            try { string dir = System.IO.Path.GetDirectoryName(from); if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); } catch { }   // пустую прежнюю папку — тоже
            MoveAutostart(from, to);
            Shortcuts.Add(app.Name, to);
            State.Paths[app.Id] = to;
            State.Save();
            if (wasRunning) { try { Launch(to); } catch { } }
        }

        static string RunningPath(AppEntry app, out bool running)
        {
            running = false;
            if (string.IsNullOrEmpty(app.Process)) return null;
            string found = null;
            foreach (var p in Process.GetProcessesByName(app.Process))
            {
                running = true;
                try { if (found == null) found = p.MainModule.FileName; } catch { }
                p.Dispose();
            }
            return found;
        }

        public static Version FileVersion(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
            }
            catch { return null; }
        }

        // Смотрит на диск и на список процессов. Сеть не трогает.
        public static void Refresh(AppEntry app, AppStatus s)
        {
            if (app.IsIntegration)
            {
                string marker = Expand(app.Marker);
                s.Path = marker != null && File.Exists(marker) ? marker : null;
                return;
            }
            if (app.IsBundle)
            {
                string marker = Expand(app.Marker);
                s.Path = marker != null && File.Exists(marker) ? marker : null;
                s.Version = null;
                s.Running = false;
                if (s.Path != null)
                {
                    // Какую версию ставили — записано рядом; набор, поставленный руками, версии не имеет
                    try { Version v; if (Version.TryParse(File.ReadAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(s.Path), VersionFile)).Trim(), out v)) s.Version = v; } catch { }
                }
                Release latest;
                if (State.Latest.TryGetValue(app.Id, out latest)) s.Latest = latest;
                return;
            }
            bool running;
            string live = RunningPath(app, out running);
            s.Running = running;
            string saved;
            State.Paths.TryGetValue(app.Id, out saved);
            string found = null;
            foreach (string c in new string[] { live, saved, DefaultPath(app), AutostartPath(app), LegacyPath(app) })
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { found = c; break; }
            s.Path = found;
            s.Version = found != null ? FileVersion(found) : null;
            if (found != null && saved != found) { State.Paths[app.Id] = found; State.Save(); }
            Release r;
            if (State.Latest.TryGetValue(app.Id, out r)) s.Latest = r;
        }

        const string VersionFile = ".core-version";

        // Куда класть часть набора: первое из мест, у которого уже есть родительская папка; иначе просто первое
        public static string PickTarget(BundleTarget t)
        {
            foreach (string to in t.To)
            {
                string p = Expand(to);
                if (p != null && Directory.Exists(System.IO.Path.GetDirectoryName(p))) return p;
            }
            return Expand(t.To[0]);
        }

        // Раскладывает архив набора по местам. Каждый файл обязан лечь строго внутрь своей папки назначения.
        public static void ExtractBundle(string zipFile, List<KeyValuePair<string, string>> parts)
        {
            using (var zip = System.IO.Compression.ZipFile.OpenRead(zipFile))
                foreach (var part in parts)
                {
                    string from = part.Key, dest = System.IO.Path.GetFullPath(part.Value);
                    int count = 0;
                    foreach (var entry in zip.Entries)
                    {
                        string name = entry.FullName.Replace('\\', '/');
                        if (!name.StartsWith(from, StringComparison.OrdinalIgnoreCase) || name.EndsWith("/")) continue;
                        string rel = name.Substring(from.Length);
                        if (rel.Length == 0 || rel.Contains("..") || rel.Contains(":") || rel.StartsWith("/")) throw new Exception("В архиве файл с недопустимым путём: " + name);
                        string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(dest, rel.Replace('/', '\\')));
                        if (!target.StartsWith(dest + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("В архиве файл с недопустимым путём: " + name);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                        using (var input = entry.Open())
                        using (var output = File.Create(target)) input.CopyTo(output);
                        count++;
                    }
                    if (count == 0) throw new Exception("В архиве нет папки «" + from.TrimEnd('/') + "»");
                }
        }

        public static void Launch(string path)
        {
            Process.Start(new ProcessStartInfo(path) { WorkingDirectory = System.IO.Path.GetDirectoryName(path), UseShellExecute = true });
        }

        // Ставит скачанный файл. Вызывается в фоне; ошибки — исключением с понятным текстом.
        public static void Install(AppEntry app, AppStatus s, string file)
        {
            if (app.IsBundle)
            {
                string need = app.Requires != null ? Expand(app.Requires) : null;
                if (app.Requires != null && (need == null || !Directory.Exists(need))) throw new Exception(string.IsNullOrEmpty(app.RequiresHint) ? "Не найдена программа, для которой это дополнение" : app.RequiresHint);
                var parts = new List<KeyValuePair<string, string>>();
                foreach (var t in app.Targets)
                {
                    string dest = PickTarget(t);
                    if (dest == null) throw new Exception(string.IsNullOrEmpty(app.RequiresHint) ? "Не найдена папка Steam" : app.RequiresHint);
                    parts.Add(new KeyValuePair<string, string>(t.From, dest));
                }
                ExtractBundle(file, parts);
                string marker = Expand(app.Marker);
                if (marker != null && File.Exists(marker) && s.Latest != null)
                    File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(marker), VersionFile), s.Latest.Version.ToString());
                Releases.TryDelete(file);
                return;
            }
            if (app.Kind == "installer")
            {
                // Установщик сам закрывает приложение и кладёт файлы на место; «тихий» ключ — без окон
                bool wasOpen = !string.IsNullOrEmpty(app.Process) && Process.GetProcessesByName(app.Process).Length > 0;
                using (var p = Process.Start(new ProcessStartInfo(file, app.Silent) { UseShellExecute = false }))
                {
                    if (!p.WaitForExit(10 * 60 * 1000)) throw new Exception("Установщик не закончил работу за десять минут");
                    if (p.ExitCode != 0) throw new Exception("Установщик завершился с ошибкой " + p.ExitCode);
                }
                Releases.TryDelete(file);
                // Установщик закрыл приложение и не открыл снова — возвращаем как было
                string installed = s.Path != null && File.Exists(s.Path) ? s.Path : Expand(app.Installed);
                if (wasOpen && installed != null && File.Exists(installed)) { try { Launch(installed); } catch { } }
                return;
            }

            string target = s.Path ?? DefaultPath(app);
            bool wasRunning = false;
            foreach (var p in Process.GetProcessesByName(app.Process))
            {
                try
                {
                    if (!string.Equals(p.MainModule.FileName, target, StringComparison.OrdinalIgnoreCase)) continue;   // другая копия — не наша забота
                    wasRunning = true;
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch { }
                finally { p.Dispose(); }
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            string old = target + ".old";
            Exception last = null;
            for (int i = 0; i < 20; i++)   // файл освобождается не мгновенно
            {
                try
                {
                    Releases.TryDelete(old);
                    if (File.Exists(target)) File.Move(target, old);   // прежняя версия остаётся рядом, пока новая не легла на место
                    try { File.Move(file, target); }
                    catch { if (File.Exists(old) && !File.Exists(target)) File.Move(old, target); throw; }
                    Releases.TryDelete(old);
                    last = null;
                    break;
                }
                catch (Exception e) { last = e; Thread.Sleep(250); }
            }
            if (last != null) throw new Exception("Не удалось заменить файл программы: " + (last is UnauthorizedAccessException ? "нет прав на запись в её папку" : "он занят"));
            State.Paths[app.Id] = target;
            State.Save();
            Shortcuts.Add(app.Name, target);
            if (wasRunning) { try { Launch(target); } catch { } }
        }
    }

    // Ярлыки в меню «Пуск» — тоже в одном месте: папка «dev.txt»
    static class Shortcuts
    {
        public static string Folder
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "dev.txt"); }
        }

        public static void Remove(string name)
        {
            if (AppOps.HomeOverride != null) return;
            Releases.TryDelete(System.IO.Path.Combine(Folder, name.ToLowerInvariant() + ".txt.lnk"));
        }

        public static void Add(string name, string target)
        {
            if (AppOps.HomeOverride != null) return;   // проверки в меню «Пуск» ничего не пишут
            try
            {
                Directory.CreateDirectory(Folder);
                Create(System.IO.Path.Combine(Folder, name.ToLowerInvariant() + ".txt.lnk"), target, name.ToLowerInvariant() + ".txt — dev.txt");
            }
            catch { }   // без ярлыка тоже можно жить
        }

        // Ярлык делает сама Windows (WScript.Shell): своего формата .lnk не пишем
        static void Create(string lnk, string target, string description)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            object shell = Activator.CreateInstance(type);
            try
            {
                object s = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = s.GetType();
                st.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { target });
                st.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { System.IO.Path.GetDirectoryName(target) });
                st.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, s, new object[] { description });
                st.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, s, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(s);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }
    }
}
