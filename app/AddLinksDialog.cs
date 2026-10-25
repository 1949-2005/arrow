// Add links (docs/NATIVE_UI.md "Dialogs", the old ui.html Add URL dialog): links one per line with batch ranges,
// Quality while a YouTube link is in them, Save as for a single link, Start later. Each link is a POST /api/add.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Glide
{
    public class AddLinksDialog : Dialog
    {
        readonly Api api; // null in GLIDE_SHOTS
        readonly Caption linksLabel = Label("Links"), qualityLabel = Label("Quality"), nameLabel = Label("Save as");
        readonly TextField links = new TextField('\0', "One link per line. img[001-120].jpg adds 120 links.");
        readonly Picker quality = new Picker { AccessibleName = "Quality", Height = Theme.Px(32) };
        readonly TextField name = new TextField('\0', "Optional, for a single link");
        readonly FlatCheck later = new FlatCheck("Start later");
        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 6, Visible = false };
        readonly FlatButton add = new FlatButton("Download", '\0', ButtonKind.Primary);
        readonly FlatButton cancel = Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        bool busy;

        public AddLinksDialog(Api api, string prefill) : base("Add links", 520)
        {
            this.api = api;
            links.Box.Multiline = true;
            links.Box.AcceptsReturn = true;
            links.Box.ScrollBars = ScrollBars.Vertical;
            links.Box.AccessibleName = "Links";
            links.Box.Text = prefill ?? "";
            name.Box.AccessibleName = "Save as";
            quality.Items.AddRange(Core.QualityNames);
            quality.SelectedIndex = 0;
            foreach (var c in new Control[] { linksLabel, links, qualityLabel, quality, nameLabel, name, later, error }) Add(c);
            Buttons(new FlatButton[0], new[] { cancel, add });
            AcceptButton = add;
            CancelButton = cancel;
            add.Click += (s, e) => Submit();
            links.Box.TextChanged += (s, e) => { links.Invalid = false; Relayout(); };
            ActiveControl = links.Box;
            Relayout();
        }

        List<string> Typed() { return Regex.Split(links.Text, @"\s+").Where(x => x != "").ToList(); }

        // Enter adds a line in Links; Ctrl+Enter downloads from anywhere
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Enter)) { add.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowError(string text)
        {
            error.Text = text ?? "";
            error.Visible = !string.IsNullOrEmpty(text);
            Relayout();
        }

        void Submit()
        {
            if (busy) return;
            var typed = Typed();
            if (typed.Count == 0) { links.Invalid = true; links.Box.Focus(); return; }
            List<string> urls;
            try { urls = Core.ExpandBatch(typed); }
            catch (ArgumentException ex) { ShowError(ex.Message); return; }
            if (urls.Count > typed.Count && !Dialogs.Confirm(this, "Add links", "Add " + urls.Count + " downloads?\n\n" + urls[0] + "\n…\n" + urls[urls.Count - 1], "Add", false)) return;
            if (api == null) return;
            var single = urls.Count == 1 ? name.Text.Trim() : "";
            var scheduled = later.Checked;
            var q = Core.QualityKeys[Math.Max(0, quality.SelectedIndex)];
            string current = null;
            var a = api;
            Lock(true);
            ShowError(null);
            Dialogs.Call(this, () =>
            {
                foreach (var u in urls)
                {
                    current = u;
                    var body = new Dictionary<string, object> { { "url", u }, { "scheduled", scheduled } };
                    if (single != "") body["name"] = single;
                    if (Core.IsVideoLink(u)) body["quality"] = q;
                    a.Post("/api/add", body);
                }
            }, err =>
            {
                Lock(false);
                if (err == null) { DialogResult = DialogResult.OK; return; }
                ShowError(current + "\n" + err); // stops at the first link the engine refuses
            });
        }

        void Lock(bool on)
        {
            busy = on;
            foreach (var c in new Control[] { links, quality, name, later, add }) c.Enabled = !on;
        }

        void Relayout()
        {
            int p = Pad, lw = Theme.Px(72), x = p + lw + Theme.Px(12), vw = ClientSize.Width - p - x, row = Theme.Px(32), gap = Theme.Px(12);
            var y = p;
            linksLabel.Bounds = new Rectangle(p, y, lw, row);
            links.Bounds = new Rectangle(x, y, vw, Theme.Px(120));
            y += links.Height + gap;
            var yt = Typed().Any(Core.IsVideoLink);
            qualityLabel.Visible = quality.Visible = yt;
            if (yt)
            {
                qualityLabel.Bounds = new Rectangle(p, y, lw, row);
                quality.Bounds = new Rectangle(x, y, Theme.Px(180), row);
                y += row + gap;
            }
            nameLabel.Bounds = new Rectangle(p, y, lw, row);
            name.Bounds = new Rectangle(x, y, vw, row);
            y += row + Theme.Px(8);
            later.Location = new Point(x, y);
            y += later.Height;
            if (error.Text != "")
            {
                var h = error.HeightFor(vw);
                error.Bounds = new Rectangle(x, y + Theme.Px(8), vw, h);
                y += h + Theme.Px(8);
            }
            BodyHeight(y + p);
        }
    }
}
