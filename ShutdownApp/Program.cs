using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShutdownDaemon
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            var shutdownfile = Application.StartupPath + "\\shutdown.xml";
            try
            {
                var w = new Form1();
                while (true)
                {
                    while (!File.Exists(shutdownfile))
                    {
                        Thread.Sleep(500);
                    }
                    ShowInactiveTopmost(w);
                    var desiredtime = DateTime.Now.AddSeconds(10);
                    var tminus = (desiredtime - DateTime.Now).TotalSeconds;
                    while (tminus > 0 && !w.Cancelled)
                    {
                        w.LabelText = $"Shutting down in {Math.Round(tminus)} sec";
                        Application.DoEvents();
                        Thread.Sleep(40);
                        tminus = (desiredtime - DateTime.Now).TotalSeconds;
                    }
                    if (w.Cancelled) { DeleteFileSafe(shutdownfile); w.Cancelled = false; }
                    w.Hide();
                }
            }
            finally
            {
                DeleteFileSafe(shutdownfile);
            }
            //System.Diagnostics.Process.Start("shutdown", "/s /t 0 /f");
        }

        static void DeleteFileSafe(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); }
            }
            catch (Exception ex)
            {

            }
        }

        static void ShowInactiveTopmost(Form frm)
        {
            ShowWindow(frm.Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(frm.Handle.ToInt32(), HWND_TOPMOST,
            frm.Left, frm.Top, frm.Width, frm.Height,
            SWP_NOACTIVATE);
        }

        private const int SW_SHOWNOACTIVATE = 4;
        private const int HWND_TOPMOST = -1;
        private const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
        static extern bool SetWindowPos(
             int hWnd,             // Window handle
             int hWndInsertAfter,  // Placement-order handle
             int X,                // Horizontal position
             int Y,                // Vertical position
             int cx,               // Width
             int cy,               // Height
             uint uFlags);         // Window positioning flags

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
