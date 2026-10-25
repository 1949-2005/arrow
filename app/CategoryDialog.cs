// The category editor (spec "8 Options › Categories"): Name (read-only for the five built-ins), File types, Folder +
// Browse…, saved at once with POST /api/settings {types, folders}. Opened through Dialogs.EditCategory.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Glide
{
    public class CategoryDialog : Dialog
    {
        readonly Api api; // null in GLIDE_SHOTS
        readonly Settings settings;
        readonly string original; // null for a new category
        readonly bool builtIn;
        readonly Caption nameLabel = Label("Name"), typesLabel = Label("File types"), folderLabel = Label("Folder");
        readonly TextField name = new TextField('\0', "");
        readonly Caption fixedName = new Caption(Theme.Body, () => Theme.Text); // a built-in's name
        readonly TextField types = new TextField('\0', "srt vtt");
        readonly Caption typesHint = new Caption(Theme.Small, () => Theme.Muted) { Text = "Separated by spaces, without the dot" };
        readonly TextField folder = new TextField('\0', "");
        readonly FlatButton browse = new FlatButton("Browse…", '\0', ButtonKind.Secondary);
        readonly Caption error = new Caption(Theme.Small, () => Theme.Err) { MaxLines = 4, Visible = false };
        readonly FlatButton save = new FlatButton("Save", '\0', ButtonKind.Primary);
        readonly FlatButton cancel = Button("Cancel", ButtonKind.Secondary, DialogResult.Cancel);
        bool busy;

        public Settings Saved; // the engine's settings after Save
        public string SavedName;

        public CategoryDialog(Api api, Settings s, string category) : base(category == null ? "New category" : "Edit category", 480)
        {
            this.api = api;
            settings = s;
            original = category;
            builtIn = Core.BuiltInCategories.Contains(category);
            name.Box.Text = category ?? "";
            name.Box.MaxLength = 30;
            name.Box.AccessibleName = "Name";
            name.Visible = !builtIn;
            fixedName.Text = category ?? "";
            fixedName.AccessibleName = "Name: " + fixedName.Text;
            fixedName.Visible = builtIn;
            List<string> exts;
            types.Box.Text = category != null && s.Types.TryGetValue(category, out exts) ? string.Join(" ", exts) : "";
            types.Box.AccessibleName = "File types, separated by spaces";
            string kept;
            folder.Box.Text = category != null && s.Folders.TryGetValue(category, out kept) ? kept : "";
            folder.Box.AccessibleName = "Folder, empty = the default folder";
            Tips.SetToolTip(folder.Box, "Empty: the default folder. Missing folders are created.");
            Cue();
            name.Box.TextChanged += (o, e) => { name.Invalid = false; Cue(); };
            types.Box.TextChanged += (o, e) => types.Invalid = false;
            folder.Box.TextChanged += (o, e) => folder.Invalid = false;
            foreach (var c in new Control[] { nameLabel, name, fixedName, typesLabel, types, typesHint, folderLabel, folder, browse, error }) Add(c);
            Buttons(new FlatButton[0], new[] { cancel, save });
            AcceptButton = save;
            CancelButton = cancel;
            browse.Click += (o, e) => Browse();
            save.Click += (o, e) => Save();
            ActiveControl = builtIn ? types.Box : name.Box;
            Relayout();
        }

        string CategoryName { get { return builtIn ? original : name.Text.Trim(); } }

        // the folder its files get when none is set here
        void Cue() { folder.Cue = Core.FolderFor(new Settings { Dir = settings.Dir, Categories = settings.Categories }, CategoryName == "" ? "Other" : CategoryName); }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Folder for " + (CategoryName == "" ? "this category" : CategoryName), SelectedPath = folder.Text.Trim(), ShowNewFolderButton = true })
                if (dlg.ShowDialog(this) == DialogResult.OK) folder.Text = dlg.SelectedPath;
            browse.Focus();
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
            var n = CategoryName;
            var nameError = builtIn ? null : Core.CategoryNameError(n, settings.Types.Keys.Where(k => k != original));
            string typeError;
            var exts = Core.ParseTypes(types.Text, out typeError);
            var dir = folder.Text.Trim();
            var folderError = dir == "" || Core.IsFullPath(dir) ? null : "Folder needs a full path, like D:\\Courses, or stays empty.";
            name.Invalid = nameError != null;
            types.Invalid = typeError != null;
            folder.Invalid = folderError != null;
            var problem = nameError ?? typeError ?? folderError;
            ShowError(problem);
            if (problem != null)
            {
                (nameError != null ? name : typeError != null ? types : folder).Box.Focus();
                return;
            }
            if (api == null) return;
            var body = Core.CategoryBody(settings, original, n, exts, dir);
            var a = api;
            object answer = null;
            Lock(true);
            Dialogs.Call(this, () => answer = a.Post("/api/settings", body), err =>
            {
                Lock(false);
                if (err != null) { ShowError(err); return; }
                Saved = Settings.From(answer as IDictionary<string, object>);
                SavedName = n;
                DialogResult = DialogResult.OK;
            });
        }

        void Lock(bool on)
        {
            busy = on;
            foreach (var c in new Control[] { name, types, folder, browse, save }) c.Enabled = !on;
        }

        void Relayout()
        {
            int p = Pad, lw = Theme.Px(86), x = p + lw + Theme.Px(12), vw = ClientSize.Width - p - x, row = Theme.Px(32), gap = Theme.Px(12);
            var y = p;
            nameLabel.Bounds = new Rectangle(p, y, lw, row);
            name.Bounds = new Rectangle(x, y, vw, row);
            fixedName.Bounds = new Rectangle(x, y, vw, row);
            y += row + gap;
            typesLabel.Bounds = new Rectangle(p, y, lw, row);
            types.Bounds = new Rectangle(x, y, vw, row);
            y += row + Theme.Px(4);
            typesHint.Bounds = new Rectangle(x, y, vw, typesHint.Height);
            y += typesHint.Height + gap;
            folderLabel.Bounds = new Rectangle(p, y, lw, row);
            browse.Size = new Size(Math.Max(Theme.Px(88), browse.PreferredWidth), row);
            browse.Location = new Point(x + vw - browse.Width, y);
            folder.Bounds = new Rectangle(x, y, vw - browse.Width - Theme.Px(8), row);
            y += row;
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
