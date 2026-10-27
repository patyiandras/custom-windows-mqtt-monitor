using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinMqttMonitor.Tray
{
    /// <summary>
    /// A tálcás segéd: jelenti a szolgáltatásnak a session állapotát (tétlenség, zárolás), mutatja a
    /// leállítás-visszaszámlálót, és végrehajtja a csak a felhasználói sessionben működő parancsokat.
    /// </summary>
    class TrayContext : ApplicationContext
    {
        readonly int sessionId = Process.GetCurrentProcess().SessionId;
        readonly NotifyIcon icon;
        readonly CountdownForm countdown = new CountdownForm();
        readonly Timer timer = new Timer { Interval = 250 };
        bool locked;
        DateTime nextReport = DateTime.MinValue;
        Foreground.Info foreground = new Foreground.Info();
        string reportedDisplay;
        DateTime nextForeground = DateTime.MinValue;
        string shownDeadline;

        public TrayContext()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Napló megnyitása", null, (s, e) => OpenLogs());
            icon = new NotifyIcon
            {
                Icon = AppIcon(),
                Text = "Home Assistant – a gép kapcsolata az okosotthonnal",
                ContextMenuStrip = menu,
                Visible = true,
            };
            countdown.CancelRequested += () => Ipc.DeleteSafe(Ipc.PendingFile);
            SystemEvents.SessionSwitch += OnSessionSwitch;
            timer.Tick += (s, e) => Tick();
            timer.Start();
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; }
            else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; }
            nextReport = DateTime.MinValue; // azonnal jelentjük
        }

        void Tick()
        {
            try
            {
                if (!Directory.Exists(Ipc.Dir)) { return; } // a szolgáltatás még nem hozta létre
                CheckForeground();
                if (DateTime.UtcNow >= nextReport) { Report(); }
                ShowPending();
                RunCommands();
            }
            catch (Exception) { } // a segéd ne álljon le egy átmeneti fájlhiba miatt
        }

        /// <summary>Másodpercenként: ha más program került előtérbe (vagy teljes képernyőre), azonnal jelentjük.</summary>
        void CheckForeground()
        {
            if (DateTime.UtcNow < nextForeground) { return; }
            nextForeground = DateTime.UtcNow.AddSeconds(1);
            var fg = Foreground.Get();
            if (fg.Key != foreground.Key || countdown.DisplayState != reportedDisplay) { nextReport = DateTime.MinValue; }
            foreground = fg;
        }

        void Report()
        {
            nextReport = DateTime.UtcNow.AddSeconds(10);
            Ipc.WriteJson(Ipc.SessionFile(sessionId), new Dictionary<string, object>
            {
                ["time"] = DateTime.UtcNow.ToString("o"),
                ["session"] = sessionId,
                ["user"] = Environment.UserName,
                ["locked"] = locked,
                ["idle_sec"] = (int)IdleTime().TotalSeconds,
                ["fg_app"] = foreground.App,
                ["fg_process"] = foreground.Process,
                ["fg_title"] = foreground.Title,
                ["fullscreen"] = foreground.Fullscreen,
                ["display"] = reportedDisplay = countdown.DisplayState,
            });
        }

        static bool TryTime(Dictionary<string, object> d, string key, out DateTime utc)
        {
            var ok = DateTime.TryParse(d.Str(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t);
            utc = t.ToUniversalTime();
            return ok;
        }

        void ShowPending()
        {
            var p = Ipc.ReadJson(Ipc.PendingFile);
            if (p == null || !TryTime(p, "deadline", out var deadline))
            {
                if (countdown.Visible) { countdown.Hide(); }
                shownDeadline = null;
                return;
            }
            var left = Math.Max(0, (deadline - DateTime.UtcNow).TotalSeconds);
            countdown.Message = $"{p.Str("label", "Leállítás")} {Math.Ceiling(left)} mp múlva";
            if (shownDeadline != p.Str("deadline"))
            {
                shownDeadline = p.Str("deadline");
                countdown.ShowInactiveTopmost();
            }
        }

        void RunCommands()
        {
            foreach (var file in Directory.GetFiles(Ipc.Dir, "cmd-*.json"))
            {
                var cmd = Ipc.ReadJson(file);
                if (cmd == null) { continue; }
                var stale = !TryTime(cmd, "time", out var t) || (DateTime.UtcNow - t).TotalMinutes > 2;
                if ((int)cmd.Num("session", -1) != sessionId && !stale) { continue; } // másik session dolga
                Ipc.DeleteSafe(file);
                if (stale) { continue; }
                switch (cmd.Str("action"))
                {
                    case "lock":
                        LockWorkStation();
                        break;
                    case "monitor_off":
                        SendMessage(countdown.Handle, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2);
                        break;
                    case "monitor_on":
                        WakeDisplay();
                        break;
                    case "notify":
                        icon.ShowBalloonTip(15000, cmd.Str("title", "🏠 Üzenet otthonról"), cmd.Str("text", ""), ToolTipIcon.None);
                        break;
                }
            }
        }

        /// <summary>A beágyazott ikon a tálca kis ikonméretében (DPI szerint 16/20/24/32 px).</summary>
        static Icon AppIcon()
        {
            try
            {
                using (var s = typeof(TrayContext).Assembly.GetManifestResourceStream("app.ico"))
                {
                    return new Icon(s, SystemInformation.SmallIconSize);
                }
            }
            catch (Exception) { return SystemIcons.Application; }
        }

        static void OpenLogs()
        {
            try { Process.Start("explorer.exe", Path.Combine(Ipc.DataDir, "logs")); } catch (Exception) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                timer.Dispose();
                icon.Visible = false;
                icon.Dispose();
                Ipc.DeleteSafe(Ipc.SessionFile(sessionId));
            }
            base.Dispose(disposing);
        }

        // --- Win32 -------------------------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("user32.dll")]
        static extern bool LockWorkStation();

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;

        /// <summary>
        /// A kijelző visszakapcsolása. Az SC_MONITORPOWER „be” (-1) üzenetet az újabb Windowsok gyakran figyelmen
        /// kívül hagyják, ezért a kijelző-üresjárati számlálót is lenullázzuk (ES_DISPLAY_REQUIRED), és egy nulla
        /// elmozdulású egérmozgást is szimulálunk — ez a bevitel ugyanúgy felébreszti a kijelzőt, mint egy valódi.
        /// </summary>
        void WakeDisplay()
        {
            SendMessage(countdown.Handle, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)(-1));
            SetThreadExecutionState(ES_DISPLAY_REQUIRED);
            var input = new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } };
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        const uint ES_DISPLAY_REQUIRED = 0x00000002, INPUT_MOUSE = 0, MOUSEEVENTF_MOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public MOUSEINPUT mi; }

        [DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint count, INPUT[] inputs, int size);

        static TimeSpan IdleTime()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO)) };
            if (!GetLastInputInfo(ref info)) { return TimeSpan.Zero; }
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
    }
}
