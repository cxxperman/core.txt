// core.txt. SelfUpdate.cs — установка и обновление самого core.txt.
// Обновление — как у остальных приложений: файл из релиза своего репозитория, принятый только при совпадении
// размера и SHA-256. Работающий exe Windows позволяет переименовать, поэтому старый файл уходит в «.old»,
// новый встаёт на его место, запускается, а «.old» удаляется при следующем старте.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace DevTxt.Core
{
    static class SelfUpdate
    {
        public static readonly AppEntry Entry = new AppEntry { Id = "core", Name = "core", Kind = "portable", Repo = Catalog.Owner + "/core.txt", Asset = "Core.exe", Exe = "Core.exe", Process = "Core" };

        public static Release Latest;   // последняя версия в релизах; null — не узнавали или релизов нет

        public static string CurrentPath { get { return Assembly.GetExecutingAssembly().Location; } }
        public static Version Current { get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, v.Build); } }
        public static bool Available { get { return Latest != null && Latest.Version > Current; } }

        // Постоянное место core.txt на этом компьютере
        public static string InstalledPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "core.txt", "Core.exe"); }
        }
        public static bool IsInstalled { get { return string.Equals(CurrentPath, InstalledPath, StringComparison.OrdinalIgnoreCase); } }

        static string ShortcutPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "core.txt.lnk"); }
        }

        // Убирает «.old», оставшийся от прошлого обновления
        public static void Cleanup()
        {
            Releases.TryDelete(CurrentPath + ".old");
        }

        // Ставит скачанный файл на место target; прежний уходит в «.old». Годится и для работающего exe.
        public static void Swap(string target, string file)
        {
            string old = target + ".old";
            Releases.TryDelete(old);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target)) File.Move(target, old);
            try { File.Move(file, target); }
            catch
            {
                if (File.Exists(old) && !File.Exists(target)) File.Move(old, target);   // не вышло — возвращаем прежний
                throw;
            }
        }

        // Обновляет себя скачанным файлом и запускает новую версию. После вызова core.txt нужно закрыть.
        public static void Apply(string file)
        {
            Swap(CurrentPath, file);
            Process.Start(new ProcessStartInfo(CurrentPath, "--after-update") { UseShellExecute = true });
        }

        // Копирует core.txt в папку программ, добавляет ярлык в меню «Пуск» и запускает оттуда.
        // После вызова эту копию нужно закрыть.
        public static void InstallSelf()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(InstalledPath));
            File.Copy(CurrentPath, InstalledPath, true);
            try { Shortcut(ShortcutPath, InstalledPath); } catch { }   // без ярлыка тоже можно жить
            Process.Start(new ProcessStartInfo(InstalledPath, "--after-install") { UseShellExecute = true });
        }

        // Ярлык делает сама Windows (WScript.Shell): своего формата .lnk не пишем
        static void Shortcut(string lnk, string target)
        {
            Type type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            object shell = Activator.CreateInstance(type);
            try
            {
                object s = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = s.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, s, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, s, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, s, new object[] { "core.txt — лаунчер dev.txt" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, s, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(s);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }
    }
}
