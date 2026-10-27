using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShutdownDaemon
{
    public partial class Form1 : Form
    {
        public bool Cancelled = false;
        public string LabelText { get => label1.Text; set => label1.Text = value; }

        public Form1()
        {
            InitializeComponent();
        }
        protected override bool ShowWithoutActivation => true;
        protected override void WndProc(ref Message m)
        {
            var WM_ACTIVATEAPP = 0x1c;
            var WM_NCACTIVATE = 0x86;
            var WM_SETFOCUS = 0x7;
            var WM_ACTIVATE = 0x0006;
            var WM_MOUSEACTIVATE = 0x0021;
            var MA_NOACTIVATE = 0x0003;
            if (m.Msg == WM_ACTIVATE) { return; }
            if (m.Msg == WM_ACTIVATEAPP) { return; }
            if (m.Msg == WM_NCACTIVATE) { return; }
            if (m.Msg == WM_SETFOCUS) { return; }
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }

            base.WndProc(ref m);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams baseParams = base.CreateParams;

                const int WS_EX_NOACTIVATE = 0x08000000;
                const int WS_EX_TOOLWINDOW = 0x00000080;
                baseParams.ExStyle |= (int)(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

                return baseParams;
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            Cancelled = true;
        }
    }
}
