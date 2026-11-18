// core.txt. InstalledApps.cs — приложения dev.txt в «Параметры → Приложения → Установленные приложения».
// Там они видны как любая программа: имя, версия, издатель, размер, и кнопка «Удалить» (её выполняет core.txt).
// Приложения с собственным установщиком (Aura) записываются туда сами — их не трогаем.
using System;
using System.IO;
using Microsoft.Win32;

namespace DevTxt.Core
{
    static class InstalledApps
    {
        const string Root = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        static string KeyOf(AppEntry app) { return Root + @"\dev.txt." + app.Id; }

        // Приводит запись в соответствие с тем, что сейчас на диске. Пишет, только если что-то поменялось.
        public static void Sync(AppEntry app, AppStatus s)
        {
            if (AppOps.HomeOverride != null) return;   // проверки реестр не трогают
            if (app.Kind != "portable") return;
            try
            {
                if (!s.Installed || s.Phase != Phase.Idle && s.Phase != Phase.Downloading)
                {
                    if (!s.Installed) Registry.CurrentUser.DeleteSubKeyTree(KeyOf(app), false);
                    return;
                }
                string version = s.Version != null ? s.Version.Major + "." + s.Version.Minor + "." + s.Version.Build : "";
                string core = SelfUpdate.IsInstalled ? SelfUpdate.InstalledPath : SelfUpdate.CurrentPath;
                string uninstall = "\"" + core + "\" --uninstall " + app.Id;
                using (var k = Registry.CurrentUser.CreateSubKey(KeyOf(app)))
                {
                    if (k == null) return;
                    if (k.GetValue("DisplayVersion") as string == version && k.GetValue("DisplayIcon") as string == s.Path && k.GetValue("UninstallString") as string == uninstall) return;
                    k.SetValue("DisplayName", app.Name.ToLowerInvariant() + ".txt");
                    k.SetValue("DisplayVersion", version);
                    k.SetValue("Publisher", "dev.txt");
                    k.SetValue("DisplayIcon", s.Path);
                    k.SetValue("InstallLocation", Path.GetDirectoryName(s.Path));
                    k.SetValue("UninstallString", uninstall);
                    k.SetValue("QuietUninstallString", uninstall);
                    if (!string.IsNullOrEmpty(app.Repo)) k.SetValue("URLInfoAbout", "https://github.com/" + app.Repo);
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    try { k.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(s.Path).Length / 1024), RegistryValueKind.DWord); } catch { }
                }
            }
            catch { }
        }
    }
}
