// core.txt. TaskbarGuard.cs — страховка для dock.txt: если док спрятал панель задач Windows и пропал
// (упал, его закрыли через диспетчер задач), core.txt возвращает её такой, какой она была до дока.
// Док записывает прежнее состояние панели в %APPDATA%\dev.txt\dock\taskbar.txt и сам удаляет файл, когда возвращает её.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DevTxt.Core
{
    static class TaskbarGuard
    {
        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int Size; public IntPtr Hwnd; public uint Callback, Edge; public int L, T, R, B; public IntPtr Param; }
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);

        public static string Marker
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.txt", "dock", "taskbar.txt"); }
        }

        // Раз в несколько секунд: след есть, а дока нет — возвращаем панель задач. true — вернули.
        public static bool Check()
        {
            try
            {
                if (!File.Exists(Marker)) return false;
                if (Process.GetProcessesByName("Dock").Length > 0) return false;   // док жив — это его дело
                int before;
                if (!int.TryParse(File.ReadAllText(Marker).Trim(), out before)) before = 0;
                IntPtr bar = FindWindow("Shell_TrayWnd", null);
                var d = new APPBARDATA { Size = Marshal.SizeOf(typeof(APPBARDATA)), Hwnd = bar, Param = new IntPtr(before) };
                SHAppBarMessage(10, ref d);   // ABM_SETSTATE: автоскрытие — как было
                if (bar != IntPtr.Zero) ShowWindow(bar, 5);
                IntPtr other = IntPtr.Zero;
                while ((other = FindWindowEx(IntPtr.Zero, other, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero) ShowWindow(other, 5);
                File.Delete(Marker);
                return true;
            }
            catch { return false; }
        }
    }
}
