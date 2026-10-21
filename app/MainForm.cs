// Placeholder main window: the next task replaces this file.
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Arrow
{
    public class MainForm : Form
    {
        readonly Label status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Connecting…" };
        readonly Poller poller;
        bool visible;

        public MainForm(Api api, bool show)
        {
            visible = show;
            Text = Program.Name;
            ClientSize = new Size(480, 200);
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(status);
            poller = new Poller(api);
            poller.Updated += s => Ui(() => status.Text = "Connected · " + s.Downloads.Count(d => d.Status != "pending") + " downloads");
            poller.Failed += e => Ui(() => status.Text = e.Message);
            Program.ShowRequested += () => Ui(() => { visible = true; Show(); if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal; Activate(); });
            poller.Start();
        }

        // --tray: stay hidden until a second start asks to show
        protected override void SetVisibleCore(bool value)
        {
            if (!visible && !IsHandleCreated) CreateHandle();
            base.SetVisibleCore(visible && value);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AppIcon.Apply(this);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            poller.Stop();
            base.OnFormClosed(e);
        }

        void Ui(Action a)
        {
            try { if (IsHandleCreated) BeginInvoke(a); }
            catch (InvalidOperationException) { } // closing
        }
    }
}
