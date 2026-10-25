// Pair an iPhone (Options > iPhone > Pair an iPhone): the QR code of a one-time link to the app on this PC's network
// (POST /api/remote/pair). It watches /api/remote and closes itself once the phone has paired.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Glide
{
    public class PhoneDialog : Dialog
    {
        readonly Api api; // null in GLIDE_SHOTS
        readonly bool[][] qr;
        readonly int before; // devices paired when the code was shown
        readonly Caption status = new Caption(Theme.Body, () => Theme.Muted) { MaxLines = 2 };
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1500 };
        bool asking;
        public bool Paired;

        public PhoneDialog(Api api, int devicesBefore, bool[][] qr, string url) : base("Pair an iPhone", 420)
        {
            this.api = api;
            this.qr = qr;
            before = devicesBefore;
            var y = Pad;
            var head = new Caption(Theme.Bold, () => Theme.Text) { Text = "Scan this code with your iPhone's camera", MaxLines = 2 };
            y = Paragraph(head, y) + Theme.Px(12);
            // the code: dark modules on white with the standard's quiet zone, in both themes
            var side = Theme.Px(232);
            var code = new Canvas(Paint) { Bounds = new Rectangle((ClientSize.Width - side) / 2, y, side, side), AccessibleName = "QR code", AccessibleRole = AccessibleRole.Graphic };
            Add(code);
            y = code.Bottom + Theme.Px(14);
            var steps = new Caption(Theme.Body, () => Theme.Text)
            {
                MaxLines = Caption.Unlimited,
                Text = "1. Open the Camera app and point it at the code.\n2. Tap the " + Program.Name + " link that appears.\n3. On that page: Share › Add to Home Screen, to keep " + Program.Name + " next to your apps.",
            };
            y = Paragraph(steps, y) + Theme.Px(10);
            status.Text = "Waiting for your iPhone… The code works once, for 10 minutes. The iPhone must be on the same Wi-Fi.";
            y = Paragraph(status, y) + Theme.Px(6);
            var link = new Caption(Theme.Small, () => Theme.Faint) { Text = url, MaxLines = 2 };
            y = Paragraph(link, y);
            BodyHeight(y + Pad);
            var close = Button("Close", ButtonKind.Secondary, DialogResult.Cancel);
            Buttons(new FlatButton[0], new[] { close });
            CancelButton = close;
            poll.Tick += (s, e) => Check();
            if (api != null) poll.Start();
        }

        void Paint(Graphics g, Rectangle r)
        {
            g.FillRectangle(Brushes.White, r);
            if (qr == null || qr.Length == 0) return;
            var n = qr.Length + 8; // 4 modules of quiet zone each side
            var cell = Math.Max(1, Math.Min(r.Width, r.Height) / n);
            var x0 = r.X + (r.Width - cell * n) / 2 + 4 * cell;
            var y0 = r.Y + (r.Height - cell * n) / 2 + 4 * cell;
            for (var y = 0; y < qr.Length; y++)
                for (var x = 0; x < qr[y].Length; x++)
                    if (qr[y][x]) g.FillRectangle(Brushes.Black, x0 + x * cell, y0 + y * cell, cell, cell);
        }

        void Check()
        {
            if (asking || Paired) return;
            asking = true;
            object answer = null;
            var a = api;
            Dialogs.Call(this, () => answer = a.Post("/api/remote", null), err =>
            {
                asking = false;
                if (err != null) return; // tried again at the next tick
                var info = RemoteInfo.From(answer as IDictionary<string, object>);
                if (info.Devices.Count <= before) return;
                Paired = true;
                poll.Stop();
                status.Fore = () => Theme.Done;
                status.Text = info.Devices[info.Devices.Count - 1].Name + " is paired ✓";
                var t = new System.Windows.Forms.Timer { Interval = 1400 };
                t.Tick += (s, e) => { t.Dispose(); if (!IsDisposed) { DialogResult = DialogResult.OK; } };
                t.Start();
            });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) poll.Dispose();
            base.Dispose(disposing);
        }
    }
}
