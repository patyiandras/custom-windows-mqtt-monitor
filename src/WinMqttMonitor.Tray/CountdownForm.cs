using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinMqttMonitor.Tray
{
    /// <summary>
    /// Leállítás-visszaszámláló a képernyő jobb alsó sarkában. Mindig felül van, de nem veszi el a fókuszt
    /// (játék, gépelés közben se ugorjon elő aktív ablakként) — a „Mégse” gombbal megszakítható.
    /// </summary>
    class CountdownForm : Form
    {
        readonly Label label = new Label();
        readonly Button cancel = new Button();
        public event Action CancelRequested;

        public CountdownForm()
        {
            Text = "Home Assistant";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ControlBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            ClientSize = new Size(260, 120);

            label.SetBounds(12, 14, 236, 40);
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            cancel.SetBounds(12, 62, 236, 46);
            cancel.Text = "Mégse";
            cancel.FlatStyle = FlatStyle.Flat;
            cancel.Font = new Font(Font.FontFamily, 10F);
            cancel.Click += (s, e) => { CancelRequested?.Invoke(); Hide(); };
            Controls.Add(label);
            Controls.Add(cancel);
            CreateHandle(); // a képernyő-kikapcsolás (SendMessage) a rejtett ablak handle-jét használja
            // A kijelző be/ki állapota: a Windows feliratkozáskor azonnal, utána minden változáskor
            // WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE üzenetet küld erre az ablakra.
            var guid = GUID_CONSOLE_DISPLAY_STATE;
            RegisterPowerSettingNotification(Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        /// <summary>A konzol kijelzőjének állapota: "on", "off", "dimmed" (halványítva), vagy null, ha még nem jött jelzés.</summary>
        public string DisplayState { get; private set; }

        public string Message { set { if (label.Text != value) { label.Text = value; } } }

        public void ShowInactiveTopmost()
        {
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
            SetWindowPos(Handle, HWND_TOPMOST, Left, Top, Width, Height, SWP_NOACTIVATE);
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_TOPMOST = 0x00000008;
                var p = base.CreateParams;
                p.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
                return p;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 0x0003;
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
            {
                var setting = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(POWERBROADCAST_SETTING));
                if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                {
                    DisplayState = setting.Data == 0 ? "off" : setting.Data == 2 ? "dimmed" : "on";
                }
            }
            base.WndProc(ref m);
        }

        const int WM_POWERBROADCAST = 0x0218, PBT_POWERSETTINGCHANGE = 0x8013, DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        struct POWERBROADCAST_SETTING
        {
            public Guid PowerSetting;
            public uint DataLength;
            public uint Data; // a kijelző-állapotnál 4 bájtos DWORD: 0 = ki, 1 = be, 2 = halványítva
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);

        const int SW_SHOWNOACTIVATE = 4;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
