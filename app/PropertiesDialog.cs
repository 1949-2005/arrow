// Properties (spec "7"): one download's header, Address (editable while nothing works on it, Core.CanChangeUrl),
// Description, facts; Save sends only what changed (POST /api/action 'url', 'describe'). Replaces "Change link…".
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Glide
{
    public class PropertiesDialog : Dialog
    {
        readonly Api api; // null in GLIDE_SHOTS
        readonly Download d;
        readonly bool editUrl;
        readonly Tile tile = new Tile();
        readonly Caption title = new Caption(Theme.Bold, () => Theme.Text) { MaxLines = Caption.Unlimited }; // the name, whole
        readonly Caption sub = new Caption(Theme.Small, () => Theme.Muted);
        readonly Caption urlLabel, descLabel = Label("Description");
        readonly TextField url = new TextField('\0', "https://");
        readonly TextBox urlText = new TextBox { ReadOnly = true, BorderStyle = BorderStyle.None, AccessibleName = "Address" }; // read-only: selectable, as Download File Info's URL
        readonly TextField desc = new TextField('\0', "Optional, shown in the list");
        readonly List<FactRow> facts = new List<FactRow>();
        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 4, Visible = false };
        readonly FlatButton folder = new FlatButton("Show in folder", '\0', ButtonKind.Secondary);
        readonly FlatButton cancel = Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        readonly FlatButton save = new FlatButton("Save", '\0', ButtonKind.Primary);
        bool busy;

        public PropertiesDialog(Api api, Download download, State s) : base("Properties", 460)
        {
            this.api = api;
            d = download;
            editUrl = Core.CanChangeUrl(d);
            var category = Core.Category(d, s.Categories);
            tile.Category = category;
            tile.File = string.IsNullOrEmpty(d.File) ? Core.ShortName(d) : d.File;
            title.Text = Core.ShortName(d);
            sub.Text = Core.PropertiesStatus(d, s.Queues);
            urlLabel = Label(editUrl ? "Address (change it if the link expired)" : "Address");
            url.Text = urlText.Text = d.Url ?? "";
            url.Box.AccessibleName = "Address";
            url.Visible = editUrl;
            urlText.Visible = !editUrl;
            urlText.Font = Theme.Body;
            url.Box.TextChanged += (o, e) => url.Invalid = false;
            desc.Text = d.Description ?? "";
            desc.Box.MaxLength = 300;
            desc.Box.AccessibleName = "Description";
            var queue = s.Queues.FirstOrDefault(q => q.Id == d.Queue);
            var pairs = new List<string> { "Saved to", !string.IsNullOrEmpty(d.File) ? Core.SplitPath(d.File)[0] : Core.FolderFor(s.Settings, category) };
            if (!string.IsNullOrEmpty(d.Referer)) pairs.AddRange(new[] { "Referer", d.Referer });
            pairs.AddRange(new[] { "Added", Core.DateLabel(d.Added, DateTime.Now), "Last try", d.LastTry == 0 ? "—" : Core.DateLabel(d.LastTry, DateTime.Now),
                "Queue", queue != null ? Core.QueueName(queue) : "Not in a queue" });
            for (var i = 0; i < pairs.Count; i += 2)
            {
                var f = new FactRow();
                f.Set(pairs[i], pairs[i + 1]);
                facts.Add(f);
            }
            facts[0].PathValue = true;
            Tips.SetToolTip(facts[0], string.IsNullOrEmpty(d.File) ? facts[0].Value : d.File);
            foreach (var c in new Control[] { tile, title, sub, urlLabel, url, urlText, descLabel, desc }.Concat(facts).Concat(new Control[] { error })) Add(c);
            folder.Enabled = !string.IsNullOrEmpty(d.File);
            Buttons(new[] { folder }, new[] { cancel, save });
            AcceptButton = save;
            CancelButton = cancel;
            folder.Click += (o, e) => { if (api != null) Dialogs.Call(this, () => api.Action(d.Id, "folder"), ShowError); };
            save.Click += (o, e) => Save();
            ActiveControl = editUrl ? url.Box : desc.Box;
            Relayout();
        }

        void ShowError(string text)
        {
            error.Text = text ?? "";
            error.Visible = !string.IsNullOrEmpty(text);
            Relayout();
        }

        void Save()
        {
            if (busy) return;
            var u = url.Text.Trim();
            var t = desc.Text.Trim();
            var newUrl = editUrl && u != (d.Url ?? "");
            var newDesc = t != (d.Description ?? "");
            if (newUrl && u == "") { url.Invalid = true; url.Box.Focus(); return; }
            if (!newUrl && !newDesc) { DialogResult = DialogResult.OK; return; }
            if (api == null) return;
            var a = api;
            busy = true;
            save.Enabled = false;
            ShowError(null);
            Dialogs.Call(this, () =>
            {
                if (newUrl) a.Action(d.Id, "url", "url", u);
                if (newDesc) a.Action(d.Id, "describe", "description", t);
            }, err =>
            {
                busy = false;
                save.Enabled = true;
                if (err == null) { DialogResult = DialogResult.OK; return; }
                ShowError(err);
                if (newUrl) { url.Invalid = true; url.Box.Focus(); }
            });
        }

        void Relayout()
        {
            int p = Pad, w = ClientSize.Width - 2 * p, row = Theme.Px(32), line = Theme.Px(20);
            tile.Location = new Point(p, p);
            var tx = tile.Right + Theme.Px(12);
            var nh = title.HeightFor(p + w - tx);
            var th = nh + Theme.Px(2) + sub.Height;
            title.Bounds = new Rectangle(tx, p + Math.Max(0, (tile.Height - th) / 2), p + w - tx, nh);
            sub.Bounds = new Rectangle(tx, title.Bottom + Theme.Px(2), p + w - tx, sub.Height);
            var y = Math.Max(tile.Bottom, sub.Bottom) + Theme.Px(16);
            urlLabel.Bounds = new Rectangle(p, y, w, line);
            y += line + Theme.Px(4);
            url.Bounds = new Rectangle(p, y, w, row);
            urlText.Bounds = new Rectangle(p, y + (row - urlText.PreferredHeight) / 2, w, urlText.PreferredHeight);
            y += row + Theme.Px(12);
            descLabel.Bounds = new Rectangle(p, y, w, line);
            y += line + Theme.Px(4);
            desc.Bounds = new Rectangle(p, y, w, row);
            y += row + Theme.Px(16);
            foreach (var f in facts)
            {
                f.Bounds = new Rectangle(p, y, w, f.Height);
                y = f.Bottom + Theme.Px(2);
            }
            if (error.Text != "")
            {
                var h = error.HeightFor(w);
                error.Bounds = new Rectangle(p, y + Theme.Px(6), w, h);
                y += h + Theme.Px(6);
            }
            BodyHeight(y + p);
        }
    }
}
