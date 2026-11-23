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
        public string ErrorDetail;   // исходный текст ошибки — для подсказки; null, если добавить нечего
        public Phase Phase;
        public double Progress;
        public long Done, Total;     // сколько байт скачано и сколько всего
        public DateTime LaunchUntil; // «Открыть» нажали: до этого времени ждём, пока приложение появится среди запущенных
        // Подключение, которое core.txt делает сам (Maska): отметка есть, а нашего плагина в Discord уже нет, и чем
        // кончилось последнее «Подключить» или «Отключить», если всё получилось. Running у него — запущен ли Discord
        public bool Lost;
        public string Note;
        // Набор для Steam: Running — запущен ли Steam; NoHost — нет программы, для которой он (Millennium)
        public bool NoHost;

        // Сколько раз к карточке применяли проверку: фоновая проверка, начатая раньше последней, своё не кладёт
        public int Checks;

        public bool Launching { get { return !Running && LaunchUntil > DateTime.UtcNow; } }
        public void Fail(Exception e) { Error = Errors.Short(e); ErrorDetail = Errors.Detail(e); }
        public void Fail(string prefix, Exception e) { Error = prefix + ": " + Errors.Lower(Errors.Short(e)); ErrorDetail = Errors.Detail(e); }
        public void Clear() { Error = null; ErrorDetail = null; }

        public bool Installed { get { return Path != null; } }
        public bool UpdateAvailable { get { return Installed && Version != null && Latest != null && Latest.Version > Version; } }
    }

    // То из состояния, что меняет проверка: окно перерисовывается, только если это действительно поменялось
    class Seen
    {
        string path;
        Version version;
        bool running, lost, noHost;
        Release latest;

        public static Seen Of(AppStatus s) { return new Seen { path = s.Path, version = s.Version, running = s.Running, lost = s.Lost, noHost = s.NoHost, latest = s.Latest }; }

        public bool Same(Seen o)
        {
            return o != null && string.Equals(path, o.path, StringComparison.OrdinalIgnoreCase) && Equals(version, o.version) && running == o.running && lost == o.lost && noHost == o.noHost
                && (latest == o.latest || latest != null && o.latest != null && latest.Version == o.latest.Version && latest.Url == o.latest.Url);
        }
    }

    // Что проверка нашла на диске и среди процессов. Считается в фоне и сама ничего не записывает: применяет
    // найденное к карточке и к State окно (AppOps.Apply) — State не потокобезопасен.
    class Probe
    {
        public string Path;
        public Version Version;
        public bool Running, Lost, NoHost;
        public bool Procs;      // на процессы смотрели; нет — Running остаётся как был
        public string Saved;     // какой запомненный путь видела проверка
        public bool DropSaved;   // запомненный путь — во временной папке: забыть
    }

    // Снимок процессов на один проход: система обходится один раз, а не по разу на каждое приложение и каждое имя
    // Discord. Путь к файлу — через QueryFullProcessImageName: Process.MainModule у завершившегося, но ещё не
    // убранного системой процесса думает до секунды (на этой машине восемь таких Smena держали окно по шесть секунд)
    class ProcSnap
    {
        readonly Dictionary<string, List<string>> byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        // names — какие процессы нужны (имя без .exe); у остальных путь даже не спрашиваем
        public static ProcSnap Take(ICollection<string> names)
        {
            var snap = new ProcSnap();
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string n in names) if (!string.IsNullOrEmpty(n)) wanted.Add(n);
            if (wanted.Count == 0) return snap;
            Process[] all;
            try { all = Process.GetProcesses(); } catch { return snap; }
            foreach (var p in all)
            {
                try
                {
                    string name = p.ProcessName;
                    if (!wanted.Contains(name)) continue;
                    if (p.Threads.Count == 0) continue;   // процесс уже завершился, его просто ещё держат
                    bool gone;
                    string path = ImagePath(p.Id, out gone);
                    if (gone) continue;
                    List<string> list;
                    if (!snap.byName.TryGetValue(name, out list)) snap.byName[name] = list = new List<string>();
                    list.Add(path);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return snap;
        }

        // Путь к файлу процесса; null — не узнать (защищённый процесс). gone — процесс уже завершился
        static string ImagePath(int pid, out bool gone)
        {
            gone = false;
            IntPtr h = OpenProcess(0x1000 | 0x00100000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE
            if (h == IntPtr.Zero) return null;
            try
            {
                if (WaitForSingleObject(h, 0) == 0) { gone = true; return null; }
                var sb = new System.Text.StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        public bool Has(string name) { return !string.IsNullOrEmpty(name) && byName.ContainsKey(name); }

        // Пути запущенных копий; null в списке — копия есть, а путь не узнать
        public IList<string> Paths(string name)
        {
            List<string> list;
            return !string.IsNullOrEmpty(name) && byName.TryGetValue(name, out list) ? list : (IList<string>)new string[0];
        }
    }

    // Ошибки для человека: короткая фраза по-русски вместо текста исключения. Сам текст остаётся для подсказки.
    static class Errors
    {
        public const string Offline = "Нет связи с GitHub — проверьте интернет";

        // GitHub временно не отвечает на проверки с этого компьютера (60 запросов в час без входа в аккаунт)
        public static string Limit(DateTime resetLocal) { return "GitHub временно ограничил проверки — попробуйте после " + resetLocal.ToString("HH:mm"); }
        public const string Busy = "Файл занят — закройте приложение";
        public const string Denied = "Нет доступа к папке";
        public const string Checksum = "Контрольная сумма не совпала";
        public const string Unknown = "Что-то пошло не так";

        static bool Russian(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s) if (c >= 'а' && c <= 'я' || c >= 'А' && c <= 'Я' || c == 'ё' || c == 'Ё') return true;
            return false;
        }

        public static string Short(Exception e)
        {
            if (e == null) return Unknown;
            if (e is GitHubLimit) return e.Message;   // связь есть, GitHub просит подождать
            // Свои сообщения уже написаны по-русски и для человека
            if (Russian(e.Message) && e.GetType() == typeof(Exception)) return e.Message.IndexOf("онтрольная сумма", StringComparison.Ordinal) >= 0 ? Checksum : e.Message;
            for (Exception x = e; x != null; x = x.InnerException)
            {
                if (x is System.Net.WebException || x is System.Net.Sockets.SocketException || x is TimeoutException) return Offline;
                if (x is UnauthorizedAccessException || x is System.Security.SecurityException) return Denied;
                var win = x as System.ComponentModel.Win32Exception;
                if (win != null)
                {
                    if (win.NativeErrorCode == 5) return Denied;
                    if (win.NativeErrorCode == 32 || win.NativeErrorCode == 33) return Busy;
                    if (win.NativeErrorCode == 2 || win.NativeErrorCode == 3) return "Файл не найден";
                    if (win.NativeErrorCode == 1223) return "Вы отменили действие";
                }
                if (x is FileNotFoundException || x is DirectoryNotFoundException) return "Файл не найден";
                if (x is PathTooLongException) return "Слишком длинный путь к папке";
                if (x is IOException)
                {
                    int code = x.HResult & 0xFFFF;
                    if (code == 32 || code == 33) return Busy;
                    if (code == 112 || code == 39) return "Не хватает места на диске";
                    if (code == 5) return Denied;
                }
                if (x is InvalidDataException) return "Скачанный файл повреждён";
            }
            return Russian(e.Message) ? e.Message : Unknown;
        }

        // Что показать в подсказке: исходный текст, если он говорит больше короткой фразы
        public static string Detail(Exception e)
        {
            if (e == null || string.IsNullOrEmpty(e.Message)) return null;
            return e.Message == Short(e) ? null : e.Message;
        }

        // После двоеточия фраза продолжается со строчной: «Не удалось обновить core.txt: нет связи…»
        public static string Lower(string s) { return string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1); }
    }

    static class AppOps
    {
        public static string Expand(string path)
        {
            if (path.Contains("{steam}"))
            {
                string steam = Steam.Dir();
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

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        static extern uint GetLongPathName(string shortPath, System.Text.StringBuilder longPath, uint size);

        // Полный путь в длинном виде: «C:\Users\MAYLI~1\...» превращается в «C:\Users\mayli\...». Не вышло — просто полный путь.
        static string LongFull(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint n = GetLongPathName(full, sb, (uint)sb.Capacity);
                if (n > 0 && n < sb.Capacity) return sb.ToString();
            }
            catch { }
            return full;
        }

        // Длинный вид папки с разделителем на конце. Временная папка и дом спрашиваются у каждого пути при каждой
        // проверке — их длинный вид запоминается; ключ — сама папка, так что смена TempOverride или HomeOverride
        // в проверках сразу даёт новый ответ. Пара (папка, ответ) меняется целиком: читают и из фона
        static string[] tempDir, homeDir;
        static string DirKey(string dir, ref string[] cache)
        {
            string[] c = cache;
            if (c != null && c[0] == dir) return c[1];
            string d = LongFull(dir).TrimEnd('\\', '/') + "\\";
            if (Directory.Exists(dir)) cache = new string[] { dir, d };   // ещё не созданной папке длинного вида не узнать — спросим снова
            return d;
        }

        // Лежит ли путь внутри папки; граница по разделителю, чтобы «C:\Temp2» не считалась частью «C:\Temp»
        static bool InsideDir(string path, string dir, ref string[] cache)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
                return LongFull(path).StartsWith(DirKey(dir, ref cache), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string TempOverride;   // для проверок
        // Файл во временной папке — пробная сборка или распакованный архив, а не установленное приложение
        public static bool InTemp(string path)
        {
            return InsideDir(path, TempOverride ?? System.IO.Path.GetTempPath(), ref tempDir);
        }

        // Путь годится как место установленного приложения: не во временной папке (если только это не наш общий дом)
        static bool Trusted(string path)
        {
            return !InTemp(path) || InsideDir(path, Home, ref homeDir);
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
                // Пока Steam открыт, Millennium при выходе запишет свои настройки поверх наших — не трогаем ничего
                if (Steam.Running()) throw new SteamOpen();
                // Сначала настройки Millennium — как было до набора: что запомнили, лежит в папке плагина, она сейчас уйдёт
                bool restored = true;
                string steam = Steam.Dir(), marker = Expand(app.Marker);
                if (steam != "" && marker != null)
                    try { Millennium.Restore(steam, app.Id, System.IO.Path.GetDirectoryName(marker)); }
                    catch { restored = false; }
                // Набор: убираются папки, в которые он раскладывался — они целиком наши
                foreach (var t in app.Targets)
                    foreach (string to in t.To)
                    {
                        string dir = Expand(to);
                        try { if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true); }
                        catch { throw new Exception("Не удалось убрать папку — закройте Steam и попробуйте ещё раз"); }
                    }
                Stash.Clear(app.Id);
                if (!restored) throw new Exception(Millennium.RestoreFailed);
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
            Stash.Clear(app.Id);   // запас для обновлений удалённому приложению ни к чему
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

        // Среди одноимённых процессов берём тот, чей путь годится; пробная сборка из временной папки не в счёт
        static string RunningPath(AppEntry app, ProcSnap procs, out bool running)
        {
            running = false;
            if (string.IsNullOrEmpty(app.Process)) return null;
            string found = null;
            foreach (string f in procs.Paths(app.Process))
            {
                running = true;
                if (found == null && f != null && Trusted(f)) found = f;
            }
            return found;
        }

        // Версия из файла программы. Карточки спрашивают каждые несколько секунд — ответ запоминается, пока у файла
        // те же размер и время изменения
        static readonly Dictionary<string, object[]> versions = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);

        public static Version FileVersion(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                long length = fi.Length;
                DateTime time = fi.LastWriteTimeUtc;
                lock (versions)
                {
                    object[] seen;
                    if (versions.TryGetValue(fi.FullName, out seen) && (long)seen[0] == length && (DateTime)seen[1] == time) return (Version)seen[2];
                }
                var info = FileVersionInfo.GetVersionInfo(path);
                var v = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
                lock (versions) versions[fi.FullName] = new object[] { length, time, v };
                return v;
            }
            catch { return null; }
        }

        // Имена процессов, на которые смотрит проверка этих приложений
        public static List<string> ProcessNames(IEnumerable<AppEntry> apps)
        {
            var names = new List<string>();
            foreach (var app in apps)
            {
                if (app.IsConnector) names.AddRange(DiscordInstall.ProcessNames);
                else if (app.IsBundle) names.Add(Steam.Process);   // набор ставится в Steam: на кнопке — закроется ли он
                else if (!string.IsNullOrEmpty(app.Process)) names.Add(app.Process);
            }
            return names;
        }

        // Какой путь программы core.txt помнит. Читать State — только в потоке окна
        public static string SavedPath(AppEntry app)
        {
            string saved;
            return State.Paths.TryGetValue(app.Id, out saved) ? saved : null;
        }

        // Смотрит на диск и на список процессов. Сеть не трогает, ничего не записывает — можно звать из фона.
        // saved — путь из State.Paths, прочитанный в потоке окна; procs == null — на процессы не смотреть (первое,
        // быстрое состояние при запуске: «запущено» дотянет фоновая проверка)
        public static Probe Look(AppEntry app, string saved, ProcSnap procs)
        {
            var p = new Probe { Procs = procs != null, Saved = saved };
            if (app.IsConnector)
            {
                // Maska: подключена ли (отметка на месте), цел ли плагин в сборке и запущен ли сейчас Discord
                p.Path = DiscordInstall.Installed ? DiscordInstall.Marker : null;
                p.Lost = p.Path != null && !DiscordInstall.PluginInBuild;
                p.Running = procs != null && DiscordInstall.Running(procs);
                return p;
            }
            if (app.IsIntegration || app.IsBundle)
            {
                string marker = Expand(app.Marker);
                p.Path = marker != null && File.Exists(marker) ? marker : null;
                if (app.IsBundle)
                {
                    p.Version = p.Path != null ? BundleVersion(p.Path) : null;
                    p.Running = Steam.Running(procs);
                    p.NoHost = !Ready(app);
                }
                return p;
            }
            bool running = false;
            string live = procs != null ? RunningPath(app, procs, out running) : null;
            p.Running = running;
            // Пробные сборки во временной папке — не установленное приложение: ни запущенную оттуда, ни запомненную раньше не берём
            if (live != null && !Trusted(live)) live = null;
            if (saved != null && !Trusted(saved)) { p.DropSaved = true; saved = null; }
            string auto = AutostartPath(app);
            if (auto != null && !Trusted(auto)) auto = null;
            foreach (string c in new string[] { live, saved, DefaultPath(app), auto, LegacyPath(app) })
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { p.Path = c; break; }
            p.Version = p.Path != null ? FileVersion(p.Path) : null;
            return p;
        }

        // Кладёт найденное в карточку и запоминает путь программы. Только в потоке окна. true — видимое поменялось
        public static bool Apply(AppEntry app, AppStatus s, Probe p)
        {
            Seen before = Seen.Of(s);
            s.Checks++;
            if (app.IsConnector)
            {
                s.Path = p.Path;
                s.Lost = p.Lost;
                if (p.Procs) s.Running = p.Running;
            }
            else if (app.IsIntegration) s.Path = p.Path;
            else
            {
                s.Path = p.Path;
                s.Version = p.Version;
                if (app.IsBundle) s.NoHost = p.NoHost;
                if (p.Procs) s.Running = p.Running;   // у набора — запущен ли Steam
                if (!app.IsBundle)
                {
                    // Запомненное меняем, только если с начала проверки его никто не поменял
                    string now = SavedPath(app);
                    bool save = false;
                    if (p.DropSaved && now == p.Saved && now != null) { State.Paths.Remove(app.Id); now = null; save = true; }
                    if (p.Path != null && now != p.Path && now == (p.DropSaved ? null : p.Saved)) { State.Paths[app.Id] = p.Path; save = true; }
                    if (save) State.Save();
                }
                Release r;
                if (State.Latest.TryGetValue(app.Id, out r)) s.Latest = r;
            }
            return !before.Same(Seen.Of(s));
        }

        // Сразу и в этом потоке — после действий над приложением и в проверках без окна
        public static void Refresh(AppEntry app, AppStatus s) { Refresh(app, s, ProcSnap.Take(ProcessNames(new AppEntry[] { app }))); }

        public static void Refresh(AppEntry app, AppStatus s, ProcSnap procs) { Apply(app, s, Look(app, SavedPath(app), procs)); }

        // Проход по всем: один снимок процессов на всех. Вызывается в фоне; saved — пути из State, по порядку apps
        public static List<Probe> LookAll(IList<AppEntry> apps, IList<string> saved)
        {
            var procs = ProcSnap.Take(ProcessNames(apps));
            var list = new List<Probe>();
            for (int i = 0; i < apps.Count; i++)
            {
                Probe p;
                try { p = Look(apps[i], saved[i], procs); } catch { p = null; }
                list.Add(p);
            }
            return list;
        }

        const string VersionFile = ".core-version";

        // Какую версию набора ставили — записано рядом с отметкой. Нет записи (набор поставлен руками) — версия из самой
        // отметки, если это plugin.json с полем "version": тогда и такой набор узнает о своём обновлении
        public static Version BundleVersion(string marker)
        {
            Version v;
            try
            {
                string f = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(marker), VersionFile);
                if (File.Exists(f)) return Version.TryParse(File.ReadAllText(f).Trim(), out v) ? v : null;
            }
            catch { return null; }
            try
            {
                if (!marker.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || new FileInfo(marker).Length > 256 * 1024) return null;
                string text = Json.Str(Json.Parse(File.ReadAllText(marker, System.Text.Encoding.UTF8)), "version");
                return text != null && Version.TryParse(text.Trim(), out v) ? v : null;
            }
            catch { return null; }
        }

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
            if (!Trusted(path)) throw new Exception("Программа лежит во временной папке — запускать её оттуда нельзя");
            Process.Start(new ProcessStartInfo(path) { WorkingDirectory = System.IO.Path.GetDirectoryName(path), UseShellExecute = true });
        }

        // Ставит скачанный файл. Вызывается в фоне; ошибки — исключением с понятным текстом.
        // Есть ли куда ставить: набору для Steam нужен сам Steam с Millennium
        public static bool Ready(AppEntry app)
        {
            if (!app.IsBundle || app.Requires == null) return true;
            string need = Expand(app.Requires);
            return need != null && Directory.Exists(need);
        }

        // Набор: разложить файлы, записать версию и включить тему и плагин в Millennium. Возвращает, какие плагины
        // отключены как заменённые. Файлы легли, а настройки не вышли — исключение с Millennium.EnableFailed
        public static List<string> InstallBundle(AppEntry app, AppStatus s, string file)
        {
            string need = app.Requires != null ? Expand(app.Requires) : null;
            if (app.Requires != null && (need == null || !Directory.Exists(need))) throw new Exception(string.IsNullOrEmpty(app.RequiresHint) ? "Не найдена программа, для которой это дополнение" : app.RequiresHint);
            // Пока Steam открыт, файлы могут быть заняты, а Millennium при выходе запишет свои настройки поверх наших
            if (Steam.Running()) throw new SteamOpen();
            var parts = new List<KeyValuePair<string, string>>();
            foreach (var t in app.Targets)
            {
                string dest = PickTarget(t);
                if (dest == null) throw new Exception(string.IsNullOrEmpty(app.RequiresHint) ? "Не найдена папка Steam" : app.RequiresHint);
                parts.Add(new KeyValuePair<string, string>(t.From, dest));
            }
            BundleNames names = BundleNames.FromZip(file, app.Id);
            ExtractBundle(file, parts);
            string marker = Expand(app.Marker);
            if (marker != null && File.Exists(marker) && s.Latest != null)
                File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(marker), VersionFile), s.Latest.Version.ToString());
            if (!Stash.Holds(file)) Releases.TryDelete(file);   // файл из запаса нужен следующему обновлению
            string steam = Steam.Dir();
            if (steam == "" || marker == null || !File.Exists(marker)) throw new Exception(Millennium.EnableFailed);
            try { return Millennium.Enable(steam, app.Id, names, System.IO.Path.GetDirectoryName(marker)); }
            catch (Exception e) { throw new Exception(Millennium.EnableFailed, e); }
        }

        public static void Install(AppEntry app, AppStatus s, string file)
        {
            if (app.IsBundle) { InstallBundle(app, s, file); return; }
            if (app.Kind == "installer")
            {
                // Установщик сам закрывает приложение и кладёт файлы на место; «тихий» ключ — без окон
                bool wasOpen = !string.IsNullOrEmpty(app.Process) && Process.GetProcessesByName(app.Process).Length > 0;
                using (var p = Process.Start(new ProcessStartInfo(file, app.Silent) { UseShellExecute = false }))
                {
                    if (!p.WaitForExit(10 * 60 * 1000)) throw new Exception("Установщик не закончил работу за десять минут");
                    if (p.ExitCode != 0) throw new Exception("Установщик завершился с ошибкой " + p.ExitCode);
                }
                if (!Stash.Holds(file)) Releases.TryDelete(file);   // файл из запаса нужен следующему обновлению
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
                    try { if (Stash.Holds(file)) File.Copy(file, target); else File.Move(file, target); }   // из запаса — копия: он ещё пригодится
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
