using System.Threading;
using System.Windows.Forms;

namespace WinMqttMonitor.Tray
{
    static class Program
    {
        [System.STAThread]
        static void Main()
        {
            // Sessionönként egy példány (a Run-kulcs minden bejelentkezéskor indítja).
            using (new Mutex(true, @"Local\WinMqttMonitorTray", out var created))
            {
                if (!created) { return; }
                // Váratlan hiba: naplózás az ipc mappába (oda a felhasználó is írhat), hogy kideríthető legyen
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => CrashLog(e.Exception);
                System.AppDomain.CurrentDomain.UnhandledException += (s, e) => CrashLog(e.ExceptionObject as System.Exception);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }

        static void CrashLog(System.Exception ex)
        {
            try
            {
                var file = System.IO.Path.Combine(Ipc.Dir, $"tray-{System.Diagnostics.Process.GetCurrentProcess().SessionId}.log");
                System.IO.File.AppendAllText(file, $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n");
            }
            catch (System.Exception) { }
        }
    }
}
