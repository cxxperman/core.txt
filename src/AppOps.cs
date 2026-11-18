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
            foreach (string c in new string[] { live, saved, DefaultPath(app) })
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
