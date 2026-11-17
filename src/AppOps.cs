// core.txt. AppOps.cs — что с приложением сейчас (стоит ли, какая версия, запущено ли) и действия над ним:
// поставить, обновить, запустить.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace DevTxt.Core
{
    enum Phase { Idle, Downloading, Installing }

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

        public static string DefaultPath(AppEntry app)
        {
            if (app.Kind == "installer") return Expand(app.Installed);
            return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", app.Name, app.Exe);
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
            bool running;
            string live = RunningPath(app, out running);
            s.Running = running;
            string saved;
            State.Paths.TryGetValue(app.Id, out saved);
            string found = null;
            foreach (string c in new string[] { live, saved, DefaultPath(app) })
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) { found = c; break; }
            s.Path = found;
            s.Version = found != null ? FileVersion(found) : null;
            if (found != null && saved != found) { State.Paths[app.Id] = found; State.Save(); }
            Release r;
            if (State.Latest.TryGetValue(app.Id, out r)) s.Latest = r;
        }

        public static void Launch(string path)
        {
            Process.Start(new ProcessStartInfo(path) { WorkingDirectory = System.IO.Path.GetDirectoryName(path), UseShellExecute = true });
        }

        // Ставит скачанный файл. Вызывается в фоне; ошибки — исключением с понятным текстом.
        public static void Install(AppEntry app, AppStatus s, string file)
        {
            if (app.Kind == "installer")
            {
                // Установщик сам закрывает приложение и кладёт файлы на место; «тихий» ключ — без окон
                using (var p = Process.Start(new ProcessStartInfo(file, app.Silent) { UseShellExecute = false }))
                {
                    if (!p.WaitForExit(10 * 60 * 1000)) throw new Exception("Установщик не закончил работу за десять минут");
                    if (p.ExitCode != 0) throw new Exception("Установщик завершился с ошибкой " + p.ExitCode);
                }
                Releases.TryDelete(file);
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
            if (wasRunning) { try { Launch(target); } catch { } }
        }
    }
}
