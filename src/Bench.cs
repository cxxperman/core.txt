// core.txt. Bench.cs — замер (--bench): сколько стоит проверка приложений настоящего каталога на этом компьютере.
// Печатает время по частям: что уходит в фон и что остаётся в потоке окна. Настройки не пишет, реестр не трогает.
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DevTxt.Core
{
    static class Bench
    {
        public static int Run()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            State.Load();
            State.Frozen = true;   // Apply может запомнить путь — в файл это не попадёт
            Catalog.Load();
            var apps = Catalog.Apps;
            var st = new List<AppStatus>();
            foreach (var a in apps) st.Add(new AppStatus());
            var sw = new Stopwatch();
            for (int round = 0; round < 12; round++)
            {
                // Первое состояние при запуске окна: без процессов, в потоке окна
                sw.Restart();
                foreach (var a in apps) AppOps.Look(a, AppOps.SavedPath(a), null);
                double first = sw.Elapsed.TotalMilliseconds;
                // Снимок процессов и весь фоновый проход
                sw.Restart();
                ProcSnap.Take(AppOps.ProcessNames(apps));
                double snap = sw.Elapsed.TotalMilliseconds;
                var saved = apps.ConvertAll(delegate(AppEntry a) { return AppOps.SavedPath(a); });
                sw.Restart();
                var found = AppOps.LookAll(apps, saved);
                double background = sw.Elapsed.TotalMilliseconds;
                // Что остаётся в потоке окна: применить готовое
                sw.Restart();
                bool changed = false;
                for (int i = 0; i < apps.Count; i++) if (found[i] != null && AppOps.Apply(apps[i], st[i], found[i])) changed = true;
                double ui = sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                string pn;
                for (int i = 0; i < 10; i++) FullScreen.Ahead(out pn);
                double ahead = sw.Elapsed.TotalMilliseconds / 10;
                Console.WriteLine(string.Format("round {0,2}: first state {1,6:0.00} ms | process snapshot {2,6:0.00} ms | background pass {3,7:0.00} ms | window thread {4,5:0.000} ms (changed: {5}) | FullScreen.Ahead {6,5:0.000} ms",
                    round, first, snap, background, ui, changed ? "yes" : "no ", ahead));
            }
            for (int i = 0; i < apps.Count; i++) Console.WriteLine(apps[i].Id + ": " + (st[i].Path ?? "-") + " v" + st[i].Version + (st[i].Running ? " running" : ""));
            return 0;
        }
    }
}
