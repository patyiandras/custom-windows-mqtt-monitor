using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinMqttMonitor.Tray
{
    /// <summary>
    /// Az előtérben lévő program (aktív ablak): barátságos név (a fájl leírása, pl. „Google Chrome”),
    /// folyamatnév, ablakcím; és hogy fut-e teljes képernyős alkalmazás (játék, film, prezentáció).
    /// </summary>
    static class Foreground
    {
        public class Info
        {
            public string App, Process, Title;
            public bool Fullscreen;
            public string Key => $"{Process}|{Title}|{Fullscreen}";
        }

        static readonly Dictionary<int, (string process, string app)> Cache = new Dictionary<int, (string, string)>();

        public static Info Get()
        {
            var info = new Info { Fullscreen = IsFullscreen() };
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) { return info; }
            info.Title = WindowTitle(hwnd);
            GetWindowThreadProcessId(hwnd, out var pid);
            var (process, app) = Describe((int)pid);
            // A Store-os (UWP) appok ablakát az ApplicationFrameHost birtokolja: a valódi program a gyerekablaké.
            if (string.Equals(process, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                EnumChildWindows(hwnd, (child, _) =>
                {
                    GetWindowThreadProcessId(child, out var cpid);
                    if (cpid == pid) { return true; }
                    (process, app) = Describe((int)cpid);
                    return false;
                }, IntPtr.Zero);
            }
            info.Process = process;
            info.App = app;
            return info;
        }

        static (string process, string app) Describe(int pid)
        {
            if (Cache.TryGetValue(pid, out var hit)) { return hit; }
            string process = null, app = null;
            try
            {
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) { process = p.ProcessName; }
                var path = ImagePath(pid);
                if (path != null)
                {
                    var vi = FileVersionInfo.GetVersionInfo(path);
                    app = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription.Trim()
                        : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName.Trim() : null;
                }
            }
            catch (Exception) { }
            var result = (process, app ?? process);
            if (Cache.Count > 200) { Cache.Clear(); }
            Cache[pid] = result;
            return result;
        }

        /// <summary>Emelt jogú folyamatnál is működik (a MainModule ott „hozzáférés megtagadva” lenne).</summary>
        static string ImagePath(int pid)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) { return null; }
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) && File.Exists(sb.ToString()) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        static string WindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        /// <summary>QUNS_BUSY (teljes képernyős app), QUNS_RUNNING_D3D_FULL_SCREEN (játék), QUNS_PRESENTATION_MODE.</summary>
        static bool IsFullscreen()
        {
            try { return SHQueryUserNotificationState(out var s) == 0 && (s == 2 || s == 3 || s == 4); }
            catch (Exception) { return false; }
        }

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    }
}
