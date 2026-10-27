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
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayContext());
            }
        }
    }
}
