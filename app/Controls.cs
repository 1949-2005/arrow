// Custom-drawn controls in the theme (docs/NATIVE_UI.md). They read Theme's colors when they paint, so a theme
// switch only needs Theme.Restyle(form) and a repaint.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Arrow
{
    // Controls holding native children whose colors must be set again after a theme switch
    public interface IThemed { void Restyle(); }

    public static class Themed
    {
        // Title bar, native scrollbars, text boxes and combos of a whole window, then a repaint
        public static void Apply(Form form)
        {
            Theme.TitleBar(form);
            Walk(form);
            form.Invalidate(true);
        }

        static void Walk(Control c)
        {
            var t = c as IThemed;
            if (t != null) t.Restyle();
            if (c is ComboBox) { c.BackColor = Theme.Field; c.ForeColor = Theme.Text; Theme.Native(c, true); }
            else if (c is ScrollableControl && ((ScrollableControl)c).AutoScroll) Theme.Native(c, false);
            foreach (Control child in c.Controls) Walk(child);
        }
    }

    // Base for painted controls: double-buffered, background of the parent
    public class Painted : Control
    {
        public Painted()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        protected Color Surface { get { return Parent != null ? Parent.BackColor : Theme.Inspector; } }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, ClientRectangle);
        }
    }

    // Paints with a delegate: logos, overlays, one-off pictures
    public class Canvas : Painted
    {
        public Action<Graphics, Rectangle> Painter;
        public Canvas(Action<Graphics, Rectangle> painter) { Painter = painter; }
        protected override void OnPaint(PaintEventArgs e) { if (Painter != null) Painter(e.Graphics, ClientRectangle); }
    }

    public enum ButtonKind { Primary, Secondary, Ghost, Danger }

    // Rounded button: Primary (accent fill), Secondary (line border), Ghost (no border; muted glyph when icon-only),
    // Danger (err fill, white text). Icon is a Glyph char or '\0'. Works as a form's AcceptButton / CancelButton.
    public class FlatButton : Control, IButtonControl
    {
        public ButtonKind Kind;
        public char Icon;
        public bool Destructive; // err text on Secondary and Ghost
        public bool MutedGlyph; // the glyph in muted next to its text (command bar)
        public bool Chevron; // a 10 px chevron after the content: the button opens a menu
        public int Pad = Theme.Px(12); // left and right of the content (the command bar's ghost buttons: 8)
        public int Radius = Theme.Px(8);
        protected bool hover, pressed;

        public FlatButton(string text, char icon, ButtonKind kind)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            SetStyle(ControlStyles.StandardDoubleClick, false); // two quick clicks are two clicks
            Text = text;
            Icon = icon;
            Kind = kind;
            Font = Theme.Body;
            Size = new Size(PreferredWidth, Theme.Px(32));
            AccessibleRole = AccessibleRole.PushButton;
            Cursor = Cursors.Default;
        }

        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        public int PreferredWidth
        {
            get
            {
                var w = Pad * 2;
                if (Icon != '\0') w += Theme.Px(16);
                if (!string.IsNullOrEmpty(Text)) w += Theme.Measure(Text, Font).Width + (Icon != '\0' ? Theme.Px(8) : 0);
                var chevron = Chevron ? Theme.Px(4 + 10) : 0;
                return Icon != '\0' && string.IsNullOrEmpty(Text) ? Theme.Px(34) + chevron : w + chevron;
            }
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        protected override void OnClick(EventArgs e)
        {
            var f = FindForm();
            if (f != null && DialogResult != DialogResult.None) f.DialogResult = DialogResult;
            base.OnClick(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Inspector)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            Color fill = Color.Empty, fore, border = Color.Empty;
            var iconOnly = string.IsNullOrEmpty(Text);
            switch (Kind)
            {
                case ButtonKind.Primary:
                    fill = !Enabled ? Theme.Soft : Theme.Mix(Theme.Accent, Theme.OnAccent, pressed ? 0.22 : hover ? 0.12 : 0);
                    fore = Enabled ? Theme.OnAccent : Theme.Faint;
                    break;
                case ButtonKind.Danger:
                    fill = !Enabled ? Theme.Soft : Theme.Mix(Theme.Err, Color.Black, pressed ? 0.2 : hover ? 0.1 : 0);
                    fore = Enabled ? Color.White : Theme.Faint;
                    break;
                default:
                    if (pressed) fill = Theme.Soft;
                    else if (hover && Enabled) fill = Theme.Hover;
                    if (Kind == ButtonKind.Secondary) border = Theme.Line;
                    fore = !Enabled ? Theme.Faint : Destructive ? Theme.Err : Kind == ButtonKind.Ghost && iconOnly ? Theme.Muted : Theme.Text;
                    break;
            }
            if (fill != Color.Empty) Theme.Fill(g, r, Radius, fill);
            if (border != Color.Empty) Theme.Border(g, r, Radius, border, 1);
            var textW = iconOnly ? 0 : Theme.Measure(Text, Font).Width;
            var icon = Icon != '\0' ? Theme.Px(16) : 0;
            var gap = icon > 0 && textW > 0 ? Theme.Px(8) : 0;
            var chevron = Chevron ? Theme.Px(4 + 10) : 0;
            var x = (r.Width - (icon + gap + textW + chevron)) / 2;
            if (Kind == ButtonKind.Ghost && !iconOnly && x < Pad) x = Pad;
            if (icon > 0) Theme.DrawGlyph(g, Icon, new Rectangle(x, 0, icon, r.Height), MutedGlyph && Enabled ? Theme.Muted : fore, 16);
            if (textW > 0) Theme.DrawText(g, Text, Font, new Rectangle(x + icon + gap, 0, Math.Min(textW + 1, r.Width - x - icon - gap), r.Height), fore);
            if (Chevron) Theme.DrawGlyph(g, Glyph.ChevronDown, new Rectangle(x + icon + gap + textW + Theme.Px(4), 0, Theme.Px(10), r.Height), Enabled ? Theme.Muted : Theme.Faint, 10);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, r, Radius);
        }
    }

    // Themed drop-down list: a secondary button showing the chosen item and a chevron, opening a menu of Items.
    // Up/Down pick the previous/next item. Picked fires for the user's choices only, not for SelectedIndex.
    public class Picker : FlatButton
    {
        public readonly List<string> Items = new List<string>();
        int index = -1;
        public event Action Picked;

        public Picker() : base("", '\0', ButtonKind.Secondary)
        {
            AccessibleRole = AccessibleRole.ComboBox;
            Height = Theme.Px(28);
        }

        public int SelectedIndex
        {
            get { return index; }
            set { index = value; Text = value >= 0 && value < Items.Count ? Items[value] : ""; AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1); }
        }

        void Pick(int i)
        {
            if (i < 0 || i >= Items.Count || i == index) return;
            SelectedIndex = i;
            var h = Picked;
            if (h != null) h();
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new PickerAccessible(this); }
        class PickerAccessible : ControlAccessibleObject
        {
            public PickerAccessible(Picker p) : base(p) { }
            public override string Value { get { return Owner.Text; } }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var m = new ContextMenuStrip { Renderer = Theme.Menus, ShowImageMargin = false, ShowCheckMargin = true, Font = Font };
            for (var i = 0; i < Items.Count; i++)
            {
                var k = i;
                var item = new ToolStripMenuItem(Items[i]) { Checked = i == index };
                item.Click += (s, a) => Pick(k);
                m.Items.Add(item);
            }
            m.Closed += (s, a) => m.BeginInvoke((MethodInvoker)m.Dispose);
            m.MinimumSize = new Size(Width, 0);
            m.Show(this, new Point(0, Height + Theme.Px(2)));
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down) { if (e.Alt) PerformClick(); else Pick(index + (e.KeyCode == Keys.Up ? -1 : 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.F4) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            if (pressed || hover && Enabled) Theme.Fill(g, r, Radius, pressed ? Theme.Soft : Theme.Hover);
            else Theme.Fill(g, r, Radius, Theme.Field);
            Theme.Border(g, r, Radius, Theme.Line, 1);
            var chevron = Theme.Px(12);
            Theme.DrawText(g, Text, Font, new Rectangle(Theme.Px(10), 0, r.Width - Theme.Px(10 + 8) - chevron - Theme.Px(4), r.Height), Enabled ? Theme.Text : Theme.Faint);
            Theme.DrawGlyph(g, Glyph.ChevronDown, new Rectangle(r.Width - Theme.Px(8) - chevron, 0, chevron, r.Height), Theme.Muted, 12);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, r, Radius);
        }
    }

    // Rounded text field: field fill, line border (accent while focused, err when Invalid), optional glyph at the left,
    // and a borderless TextBox (Box) with the native cue banner. Set Box.Multiline for a multi-line field.
    public class TextField : Painted, IThemed
    {
        public readonly TextBox Box = new TextBox { BorderStyle = BorderStyle.None };
        public char Icon;
        string cue;
        bool invalid;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public TextField(char icon, string cueText)
        {
            Icon = icon;
            cue = cueText;
            Height = Theme.Px(34);
            Box.Font = Theme.Body;
            Box.HandleCreated += (s, e) => { SetCue(); Theme.Native(Box, !Box.Multiline); }; // also when the box gets its handle after Restyle
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
            Restyle();
        }

        public string Cue { get { return cue; } set { cue = value; SetCue(); } }
        public bool Invalid { get { return invalid; } set { if (invalid != value) { invalid = value; Invalidate(); } } }
        public override string Text { get { return Box.Text; } set { Box.Text = value; } }

        void SetCue() { if (Box.IsHandleCreated) SendMessage(Box.Handle, 0x1501, (IntPtr)1, cue ?? ""); } // EM_SETCUEBANNER, shown also while focused

        public void Restyle()
        {
            Box.BackColor = Theme.Field;
            Box.ForeColor = Theme.Text;
            Theme.Native(Box, !Box.Multiline); // Explorer: dark scrollbars on a multiline box
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var left = Icon != '\0' ? Theme.Px(12 + 16 + 8) : Theme.Px(10);
            if (Box.Multiline) Box.Bounds = new Rectangle(left, Theme.Px(8), Width - left - Theme.Px(8), Height - Theme.Px(16));
            else Box.Bounds = new Rectangle(left, (Height - Box.PreferredHeight) / 2, Width - left - Theme.Px(10), Box.PreferredHeight);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Box.Focus(); base.OnMouseDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            Theme.Fill(g, r, Theme.Px(8), Theme.Field);
            Theme.Border(g, r, Theme.Px(8), invalid ? Theme.Err : Box.Focused ? Theme.Accent : Theme.Line, invalid ? Theme.Px(1.5) : 1);
            if (Icon != '\0') Theme.DrawGlyph(g, Icon, new Rectangle(Theme.Px(12), Box.Multiline ? Theme.Px(8) : 0, Theme.Px(16), Box.Multiline ? Theme.Px(18) : r.Height), Theme.Faint, 16);
        }
    }

    // Check box in the theme: a 16 px box, radius 4 (field fill, line border; checked = accent fill with an onAccent
    // check), the text in Text. The native CheckBox keeps the keyboard, clicks and accessibility.
    public class FlatCheck : CheckBox
    {
        public FlatCheck(string text)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Font = Theme.Body;
            AutoSize = false;
            Size = new Size(PreferredWidth, Theme.Px(24));
        }

        public int PreferredWidth { get { return Theme.Px(16 + 8 + 4) + Theme.Measure(Text, Font).Width; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Inspector)) g.FillRectangle(b, ClientRectangle);
            var s = Theme.Px(16);
            var box = new Rectangle(Theme.Px(2), (Height - s) / 2, s, s);
            if (Checked)
            {
                Theme.Fill(g, box, Theme.Px(4), Enabled ? Theme.Accent : Theme.Soft);
                Theme.DrawGlyph(g, Glyph.CheckMark, box, Enabled ? Theme.OnAccent : Theme.Faint, 12);
            }
            else
            {
                Theme.Fill(g, box, Theme.Px(4), Theme.Field);
                Theme.Border(g, box, Theme.Px(4), Theme.Line, 1);
            }
            var x = box.Right + Theme.Px(8);
            Theme.DrawText(g, Text, Font, new Rectangle(x, 0, Width - x, Height), Enabled ? Theme.Text : Theme.Faint);
            if (Focused && ShowFocusCues) Theme.FocusRing(g, ClientRectangle, Theme.Px(4));
        }
    }

    public enum BarKind { Normal, Paused, Done, Err }

    // Progress bar made of pieces [left, width] (fractions, Core.Pieces). Rounded track; Done fills it green.
    public class SegmentBar : Painted
    {
        public List<double[]> Pieces = new List<double[]>();
        public BarKind Kind;

        public SegmentBar() { Height = Theme.Px(8); }

        public void Set(List<double[]> pieces, BarKind kind)
        {
            Pieces = pieces;
            Kind = kind;
            Invalidate();
        }

        public static Color FillColor(BarKind k)
        {
            return k == BarKind.Done ? Theme.Done : k == BarKind.Err ? Theme.Err : k == BarKind.Paused ? Theme.Faint : Theme.Accent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Draw(e.Graphics, ClientRectangle, Kind == BarKind.Done ? new List<double[]> { new[] { 0.0, 1.0 } } : Pieces, Theme.Bar, FillColor(Kind));
        }

        public static void Draw(Graphics g, Rectangle r, IList<double[]> pieces, Color track, Color fill)
        {
            var radius = r.Height / 2f;
            Theme.Fill(g, r, radius, track);
            if (pieces == null) return;
            if (pieces.Count == 1 && pieces[0][0] <= 0)
            {
                var w = (float)(Math.Max(0, Math.Min(1, pieces[0][1])) * r.Width);
                if (w > 0) Theme.Fill(g, new RectangleF(r.X, r.Y, Math.Max(w, r.Height), r.Height), radius, fill);
                return;
            }
            using (var path = Theme.Round(r, radius))
            using (var b = new SolidBrush(fill))
            {
                var clip = g.Clip;
                g.SetClip(path, CombineMode.Intersect);
                foreach (var p in pieces)
                {
                    var x = r.X + (float)(p[0] * r.Width);
                    g.FillRectangle(b, x, r.Y, Math.Max(1f, (float)(p[1] * r.Width)), r.Height);
                }
                g.Clip = clip;
            }
        }
    }

    // Speed graph: the values right-aligned in Capacity slots, 1.5 px accent line over a 12% fill, a hairline baseline,
    // Label (the current speed) at the top right
    public class Sparkline : Painted
    {
        public IList<double> Values = new double[0];
        public int Capacity = 86; // 60 s of 700 ms polls
        public string Label = "";

        public Sparkline() { Height = Theme.Px(56); }

        public void Set(IList<double> values, string label)
        {
            Values = values;
            Label = label;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = ClientRectangle;
            var bottom = r.Bottom - 1;
            var top = Theme.Px(18);
            var n = Values.Count;
            if (n >= 2)
            {
                var max = Math.Max(1, Values.Max()) * 1.1;
                // until Capacity samples exist, the ones we have span the whole width
                var step = (r.Width - 1) / (float)(Math.Min(n, Capacity) - 1);
                var pts = new PointF[n];
                for (var i = 0; i < n; i++) pts[i] = new PointF(r.Right - 1 - (n - 1 - i) * step, bottom - (float)(Values[i] / max * (bottom - top)));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var area = new List<PointF>(pts) { new PointF(pts[n - 1].X, bottom), new PointF(pts[0].X, bottom) };
                using (var b = new SolidBrush(Color.FromArgb(31, Theme.Accent))) g.FillPolygon(b, area.ToArray());
                using (var p = new Pen(Theme.Accent, 1.5f * Theme.Scale) { LineJoin = LineJoin.Round }) g.DrawLines(p, pts);
                g.SmoothingMode = SmoothingMode.Default;
            }
            Theme.HLine(g, r.X, r.Right, bottom);
            Theme.DrawText(g, Label, Theme.Small, new Rectangle(r.X, 0, r.Width, Theme.Px(16)), Theme.Muted, Theme.Line1Right);
        }
    }

    // File-type tile (radius 12, category tint, glyph in the category color); Complete = green check tile.
    // With File set: Windows' icon for that file's type at 32 px instead of the tinted tile.
    public class Tile : Painted
    {
        public string Category = "Other";
        public string File;
        public bool Complete;

        public Tile() { Size = new Size(Theme.Px(48), Theme.Px(48)); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var r = ClientRectangle;
            if (!Complete)
            {
                if (File == null || !FileIcons.Draw(e.Graphics, Core.IconExt(File, Category), r, 32)) Theme.DrawTile(e.Graphics, r, Category, Surface);
                return;
            }
            Theme.Fill(e.Graphics, r, Theme.Px(12) * r.Width / (float)Theme.Px(48), Theme.Tint(Theme.Done, Surface));
            Theme.DrawGlyph(e.Graphics, Glyph.CheckCircle, r, Theme.Done, r.Width >= Theme.Px(40) ? 24 : 16);
        }
    }

    // Painted text: one line with an ellipsis, or up to MaxLines wrapped lines. Color is read at paint time.
    public class Caption : Painted
    {
        public Func<Color> Fore;
        public int MaxLines = 1;
        public bool Right;

        public Caption(Font font, Func<Color> color)
        {
            Font = font;
            Fore = color;
            Height = font.Height;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

        public int HeightFor(int width)
        {
            if (MaxLines <= 1) return Font.Height;
            return Math.Min(Theme.MeasureHeight(Text, Font, width), Font.Height * MaxLines);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var flags = MaxLines <= 1 ? (Right ? Theme.Line1Right : Theme.Line1) : Theme.Wrap;
            Theme.DrawText(e.Graphics, Text, Font, ClientRectangle, Fore(), flags);
        }
    }

    // Label (muted, left) and value (right, ellipsis), 22 high. Link = hand cursor, Click acts.
    public class FactRow : Painted
    {
        public string Label = "", Value = "";
        public Func<Color> ValueColor = () => Theme.Text;
        public bool PathValue; // ellipsis in the middle, so a folder's own name stays visible

        public FactRow() { Height = Theme.Px(22); }

        public void Set(string label, string value)
        {
            if (label == Label && value == Value) return;
            Label = label;
            Value = value;
            AccessibleName = label + ": " + value;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var lw = Math.Min(Theme.Measure(Label, Theme.Body).Width + Theme.Px(12), Width / 2);
            Theme.DrawText(g, Label, Theme.Body, new Rectangle(0, 0, lw, Height), Theme.Muted);
            var flags = PathValue ? (Theme.Line1Right & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.PathEllipsis : Theme.Line1Right;
            Theme.DrawText(g, Value, Theme.Body, new Rectangle(lw, 0, Width - lw, Height), ValueColor(), flags);
        }
    }

    // The banner under the top bar: glyph, text, and buttons on the right (SetButtons)
    public class Strip : Painted
    {
        public char Icon;
        public Func<Color> IconColor = () => Theme.Text;

        public Strip() { Height = Theme.Px(36); AccessibleRole = AccessibleRole.Alert; }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }

        public void SetButtons(params FlatButton[] buttons)
        {
            foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
            foreach (var b in buttons)
            {
                b.Height = Theme.Px(28);
                b.Width = b.PreferredWidth;
                Controls.Add(b);
            }
            PerformLayout();
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var x = Width - Theme.Px(12);
            for (var i = Controls.Count - 1; i >= 0; i--)
            {
                var b = Controls[i];
                x -= b.Width;
                b.Location = new Point(x, (Height - 1 - b.Height) / 2);
                x -= Theme.Px(4);
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Inspector)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var h = Height - 1;
            Theme.DrawGlyph(g, Icon, new Rectangle(Theme.Px(16), 0, Theme.Px(16), h), IconColor(), 16);
            var right = Controls.Count > 0 ? Controls.Cast<Control>().Min(c => c.Left) - Theme.Px(12) : Width - Theme.Px(16);
            var x = Theme.Px(16 + 16 + 8);
            Theme.DrawText(g, Text, Theme.Body, new Rectangle(x, 0, Math.Max(0, right - x), h), Theme.Text);
            Theme.HLine(g, 0, Width, h);
        }
    }

    // A row of a custom-drawn list, for screen readers
    public class ItemAccessible : AccessibleObject
    {
        readonly AccessibleObject parent;
        readonly Func<string> name;
        readonly Func<bool> selected, focused;
        readonly Func<Rectangle> bounds;
        readonly Action select;

        public ItemAccessible(AccessibleObject parent, Func<string> name, Func<bool> selected, Func<bool> focused, Func<Rectangle> bounds, Action select)
        {
            this.parent = parent; this.name = name; this.selected = selected; this.focused = focused; this.bounds = bounds; this.select = select;
        }

        public override string Name { get { return name(); } }
        public override AccessibleRole Role { get { return AccessibleRole.ListItem; } }
        public override AccessibleObject Parent { get { return parent; } }
        public override Rectangle Bounds { get { return bounds(); } }
        public override AccessibleStates State
        {
            get
            {
                var s = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (selected()) s |= AccessibleStates.Selected;
                if (focused()) s |= AccessibleStates.Focused;
                return s;
            }
        }
        public override void Select(AccessibleSelection flags) { select(); }
        public override void DoDefaultAction() { select(); }
    }

    public class ListAccessible : Control.ControlAccessibleObject
    {
        readonly Func<int> count;
        readonly Func<int, AccessibleObject> child;

        public ListAccessible(Control owner, Func<int> count, Func<int, AccessibleObject> child) : base(owner) { this.count = count; this.child = child; }
        public override AccessibleRole Role { get { return AccessibleRole.List; } }
        public override int GetChildCount() { return count(); }
        public override AccessibleObject GetChild(int index) { return index >= 0 && index < count() ? child(index) : null; }
    }

    // Left column: headed groups of items (glyph, label, count), scrolled by the wheel when they don't fit. Up/Down move the selection.
    public class Sidebar : Painted
    {
        public class Item
        {
            public string Key, Label;
            public char Icon;
            public string Type; // a category's icon (its SVG, else its glyph in its color); null: the glyph in muted
            public bool Dot; // a done-color dot before the count: a running queue
            public int Count;
        }

        public readonly List<Item> Items = new List<Item>();
        readonly Dictionary<int, string> headings = new Dictionary<int, string>(); // before item index
        string selected;
        int hover = -1, scroll;
        public event Action SelectedChanged;

        public Sidebar()
        {
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
            AccessibleName = "Sections";
        }

        public void Heading(string text) { headings[Items.Count] = text; }
        public void Add(string key, string label, char icon, string type) { Items.Add(new Item { Key = key, Label = label, Icon = icon, Type = type }); }
        // Before building the items again; the selection stays when its key comes back
        public void Clear() { Items.Clear(); headings.Clear(); hover = -1; }

        public string Selected
        {
            get { return selected; }
            set
            {
                if (selected == value || !Items.Any(i => i.Key == value)) return;
                selected = value;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, Items.FindIndex(i => i.Key == value));
                var h = SelectedChanged;
                if (h != null) h();
            }
        }

        public void SetCounts(Func<string, int> count)
        {
            foreach (var i in Items) i.Count = count(i.Key);
            Invalidate();
        }

        Rectangle ItemRect(int index)
        {
            int y = Theme.Px(8) - scroll, pad = Theme.Px(8);
            for (var i = 0; i <= index; i++)
            {
                if (headings.ContainsKey(i)) y += (i > 0 ? Theme.Px(12) : 0) + Theme.Px(28);
                if (i == index) return new Rectangle(pad, y, Width - 2 * pad - 1, Theme.Px(30));
                y += Theme.Px(30);
            }
            return Rectangle.Empty;
        }

        int IndexAt(Point p)
        {
            for (var i = 0; i < Items.Count; i++) if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var i = IndexAt(e.Location);
            if (i != hover) { hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var i = IndexAt(e.Location);
            if (i >= 0) { Focus(); Selected = Items[i].Key; }
            base.OnMouseDown(e);
        }

        void ScrollTo(int y)
        {
            var max = Items.Count == 0 ? 0 : Math.Max(0, ItemRect(Items.Count - 1).Bottom + scroll + Theme.Px(8) - Height);
            y = Math.Max(0, Math.Min(max, y));
            if (y != scroll) { scroll = y; Invalidate(); }
        }

        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(scroll - e.Delta * Theme.Px(30) / 120); base.OnMouseWheel(e); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ScrollTo(scroll); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || k == Keys.Home || k == Keys.End || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            var i = Items.FindIndex(x => x.Key == selected);
            var j = e.KeyCode == Keys.Up ? i - 1 : e.KeyCode == Keys.Down ? i + 1 : e.KeyCode == Keys.Home ? 0 : e.KeyCode == Keys.End ? Items.Count - 1 : -2;
            if (j != -2 && Items.Count > 0)
            {
                j = Math.Max(0, Math.Min(Items.Count - 1, j));
                Selected = Items[j].Key;
                // keep it in view, with its group's heading above the first item
                var r = ItemRect(j);
                var head = headings.ContainsKey(j) ? Theme.Px(28) : 0;
                if (r.Top - head < 0) ScrollTo(scroll + r.Top - head - Theme.Px(8));
                else if (r.Bottom > Height) ScrollTo(scroll + r.Bottom - Height + Theme.Px(8));
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Sidebar)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            for (var i = 0; i < Items.Count; i++)
            {
                var it = Items[i];
                var r = ItemRect(i);
                string head;
                if (headings.TryGetValue(i, out head))
                    Theme.DrawText(g, head, Theme.Small, new Rectangle(r.X + Theme.Px(8), r.Y - Theme.Px(24), r.Width, Theme.Px(20)), Theme.Muted);
                var sel = it.Key == selected;
                if (sel || i == hover) Theme.Fill(g, r, Theme.Px(6), sel ? Theme.Soft : Theme.Hover);
                // Types: the user's SVG icon at 28 px; Library: its glyph at 18 px, centered in the same 28 px slot
                var slot = new Rectangle(r.X + Theme.Px(8), r.Y, Theme.Px(28), r.Height);
                if (it.Type == null || !FileIcons.DrawType(g, it.Type, slot, 28))
                    Theme.DrawGlyph(g, it.Icon, slot, it.Type != null ? Theme.Category(it.Type) : sel ? Theme.Text : Theme.Muted, it.Type != null ? 16 : 18);
                var count = it.Count.ToString();
                var cw = Theme.Measure(count, Theme.Body).Width;
                Theme.DrawText(g, count, Theme.Body, new Rectangle(r.Right - Theme.Px(8) - cw, r.Y, cw, r.Height), Theme.Muted, Theme.Line1Right);
                if (it.Dot)
                {
                    cw += Theme.Px(10 + 8);
                    Theme.Fill(g, new Rectangle(r.Right - Theme.Px(8) - cw, r.Y + (r.Height - Theme.Px(8)) / 2, Theme.Px(8), Theme.Px(8)), Theme.Px(4), Theme.Done);
                }
                var x = r.X + Theme.Px(8 + 28 + 10);
                Theme.DrawText(g, it.Label, sel ? Theme.Bold : Theme.Body, new Rectangle(x, r.Y, r.Right - Theme.Px(16) - cw - x, r.Height), Theme.Text);
                if (sel && Focused && ShowFocusCues) Theme.FocusRing(g, r, Theme.Px(6));
            }
            Theme.VLine(g, Width - 1, 0, Height);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            var me = this;
            ListAccessible list = null;
            list = new ListAccessible(this, () => Items.Count, i => new ItemAccessible(list,
                () => Items[i].Label + (Items[i].Dot ? ", running" : "") + ", " + Items[i].Count,
                () => Items[i].Key == selected, () => Focused && Items[i].Key == selected,
                () => me.RectangleToScreen(ItemRect(i)), () => Selected = Items[i].Key));
            return list;
        }
    }

    // The download list: custom-drawn 48 px rows, native (themed) scrollbar with smooth wheel scrolling, hover,
    // multi-select (Ctrl, Shift, Ctrl+A), keyboard (arrows, Home/End, PageUp/PageDown, Enter, Space, menu key).
    public class DownloadList : ScrollableControl
    {
        public Func<Download, string> CategoryOf = d => "Other";
        public event Action SelectionChanged;
        public event Action<Download> Activated; // Enter or double-click
        public event Action ToggleRequested;     // Space: pause or resume
        public event Action<Point> MenuRequested;
        public event Action<string> HeaderClicked;       // a column title: sort by it
        public event Action<Point> HeaderMenuRequested;  // right-click on the header: the column chooser, at a point in Header

        // Columns: the chosen ones (Core.Columns keys after Name), those that fit, the sort shown in the header
        public List<string> Chosen = new List<string>(Core.DefaultColumns);
        public List<string> Shown;
        public string SortColumn;
        public bool SortDesc;
        public IList<Queue> Queues = new List<Queue>(); // for "In <queue>"
        public readonly ListHeader Header;

        // Empty state: FirstRun shows the logo, the title, the hint and EmptyButton; else the title and the hint
        public bool FirstRun;
        public string EmptyTitle = "", EmptyHint = "";
        public Image Logo;
        public readonly FlatButton EmptyButton = new FlatButton("Add links…", '\0', ButtonKind.Secondary);

        List<Download> items = new List<Download>();
        HashSet<string> selected = new HashSet<string>();
        string anchor, cursor;
        int hover = -1;
        readonly Timer scroller = new Timer { Interval = 15 };
        int scrollTarget;

        public DownloadList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            AutoScroll = true;
            TabStop = true;
            AccessibleName = "Downloads";
            AccessibleRole = AccessibleRole.List;
            EmptyButton.Visible = false;
            Controls.Add(EmptyButton);
            Header = new ListHeader(this);
            Fit();
            scroller.Tick += (s, e) =>
            {
                var y = -AutoScrollPosition.Y;
                var d = scrollTarget - y;
                if (Math.Abs(d) <= 1) { scroller.Stop(); SetScroll(scrollTarget); return; }
                SetScroll(y + (int)Math.Round(d * 0.35));
            };
        }

        int RowH { get { return Theme.Px(48); } }
        public IList<Download> Items { get { return items; } }
        public List<Download> Selected { get { return items.Where(d => selected.Contains(d.Id)).ToList(); } }
        public Download Current { get { return items.FirstOrDefault(d => d.Id == cursor); } }

        // New rows in display order; the selection keeps the ids still shown
        public void SetItems(IList<Download> list)
        {
            items = new List<Download>(list);
            var ids = new HashSet<string>(items.Select(d => d.Id));
            var lost = selected.RemoveWhere(id => !ids.Contains(id)) > 0;
            if (cursor != null && !ids.Contains(cursor)) cursor = items.Count > 0 ? items[0].Id : null;
            if (anchor != null && !ids.Contains(anchor)) anchor = null;
            AutoScrollMinSize = new Size(0, items.Count * RowH);
            if (hover >= items.Count) hover = -1;
            PlaceEmptyButton();
            Invalidate();
            if (lost) Changed();
        }

        public void ClearSelection() { if (selected.Count == 0) return; selected.Clear(); Invalidate(); Changed(); }

        // Selects rows from..to (indices in display order) and shows the first
        public void SelectRange(int from, int to)
        {
            selected = new HashSet<string>(items.Skip(from).Take(to - from + 1).Select(d => d.Id));
            anchor = cursor = from < items.Count ? items[from].Id : null;
            if (cursor != null) EnsureVisible(from);
            Invalidate();
            Changed();
        }

        public void Select(string id) { var i = items.FindIndex(d => d.Id == id); if (i >= 0) SelectRange(i, i); }
        public void SelectAll() { selected = new HashSet<string>(items.Select(d => d.Id)); Invalidate(); Changed(); }

        void Changed()
        {
            var i = items.FindIndex(d => d.Id == cursor);
            if (i >= 0 && Focused) AccessibilityNotifyClients(AccessibleEvents.Focus, i);
            AccessibilityNotifyClients(AccessibleEvents.Selection, Math.Max(0, i));
            var h = SelectionChanged;
            if (h != null) h();
        }

        // how: only, toggle, range (Shift), move (Ctrl + arrows: the cursor only)
        void Pick(int index, string how)
        {
            var id = items[index].Id;
            var a = items.FindIndex(d => d.Id == anchor);
            if (how == "range" && a >= 0)
            {
                selected = new HashSet<string>(items.Skip(Math.Min(a, index)).Take(Math.Abs(a - index) + 1).Select(d => d.Id));
                cursor = id;
            }
            else if (how == "move") cursor = id;
            else
            {
                if (how != "toggle") selected.Clear();
                if (!selected.Remove(id)) selected.Add(id);
                anchor = cursor = id;
            }
            EnsureVisible(index);
            Invalidate();
            Changed();
        }

        void EnsureVisible(int i)
        {
            var y = -AutoScrollPosition.Y;
            var top = i * RowH;
            if (top < y) ScrollTo(top, false);
            else if (top + RowH > y + ClientSize.Height) ScrollTo(top + RowH - ClientSize.Height, false);
        }

        void SetScroll(int y) { AutoScrollPosition = new Point(0, Math.Max(0, y)); Invalidate(); }

        void ScrollTo(int y, bool smooth)
        {
            scrollTarget = Math.Max(0, Math.Min(y, items.Count * RowH - ClientSize.Height));
            if (smooth) scroller.Start();
            else { scroller.Stop(); SetScroll(scrollTarget); }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var lines = SystemInformation.MouseWheelScrollLines;
            if (lines <= 0) lines = 3;
            var from = scroller.Enabled ? scrollTarget : -AutoScrollPosition.Y;
            ScrollTo(from - e.Delta * lines * Theme.Px(24) / 120, true);
        }

        int IndexAt(int y)
        {
            var i = (y - AutoScrollPosition.Y) / RowH;
            return y >= 0 && i >= 0 && i < items.Count ? i : -1;
        }

        Rectangle RowRect(int i) { return new Rectangle(0, i * RowH + AutoScrollPosition.Y, ClientSize.Width, RowH); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var i = IndexAt(e.Y);
            if (i != hover) { hover = i; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var i = IndexAt(e.Y);
            if (e.Button == MouseButtons.Left)
            {
                if (i < 0) ClearSelection();
                else Pick(i, ModifierKeys.HasFlag(Keys.Shift) ? "range" : ModifierKeys.HasFlag(Keys.Control) ? "toggle" : "only");
            }
            else if (e.Button == MouseButtons.Right && i >= 0)
            {
                if (!selected.Contains(items[i].Id)) Pick(i, "only");
                var h = MenuRequested;
                if (h != null) h(e.Location);
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            var i = IndexAt(e.Y);
            var h = Activated;
            if (e.Button == MouseButtons.Left && i >= 0 && h != null) h(items[i]);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override bool IsInputKey(Keys k)
        {
            switch (k & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown: case Keys.Enter: case Keys.Space: return true;
            }
            return base.IsInputKey(k);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (items.Count == 0) return;
            var i = items.FindIndex(d => d.Id == cursor);
            var page = Math.Max(1, ClientSize.Height / RowH - 1);
            int? j = null;
            switch (e.KeyCode)
            {
                case Keys.Up: j = i - 1; break;
                case Keys.Down: j = i + 1; break;
                case Keys.Home: j = 0; break;
                case Keys.End: j = items.Count - 1; break;
                case Keys.PageUp: j = i - page; break;
                case Keys.PageDown: j = Math.Max(0, i) + page; break;
            }
            if (j != null) Pick(Math.Max(0, Math.Min(items.Count - 1, j.Value)), e.Shift ? "range" : e.Control ? "move" : "only");
            else if (e.KeyCode == Keys.Space && e.Control && i >= 0) Pick(i, "toggle");
            else if (e.KeyCode == Keys.Space) { var h = ToggleRequested; if (h != null) h(); }
            else if (e.KeyCode == Keys.Enter) { var d = Current ?? Selected.FirstOrDefault(); var h = Activated; if (d != null && h != null) h(d); }
            else if (e.KeyCode == Keys.Apps || e.KeyCode == Keys.F10 && e.Shift)
            {
                var r = RowRect(Math.Max(0, i));
                var h = MenuRequested;
                if (h != null) h(new Point(r.X + Theme.Px(40), r.Bottom - Theme.Px(8)));
            }
            else return;
            e.Handled = e.SuppressKeyPress = true;
        }

        // ---- columns: Name takes the rest; the chosen ones that don't fit are left out from the right (Core.FitColumns)

        public void SetColumns(IEnumerable<string> chosen)
        {
            var set = new HashSet<string>(chosen);
            Chosen = Core.Columns.Where(c => c != "name" && set.Contains(c)).ToList();
            Fit();
        }

        void Fit()
        {
            var fit = Core.FitColumns(Chosen, ClientSize.Width, Theme.Scale);
            if (Shown != null && fit.SequenceEqual(Shown)) return;
            Shown = fit;
            Invalidate();
            Header.Invalidate();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); Header.Invalidate(); }

        // [column, x, width] from Name to the right
        public List<Tuple<string, int, int>> Spans()
        {
            var widths = Shown.Select(c => Theme.Px(Core.ColumnWidths[Array.IndexOf(Core.Columns, c)])).ToList();
            var spans = new List<Tuple<string, int, int>> { Tuple.Create("name", 0, Math.Max(Theme.Px(220), ClientSize.Width - widths.Sum())) };
            var x = spans[0].Item3;
            for (var i = 0; i < Shown.Count; i++) { spans.Add(Tuple.Create(Shown[i], x, widths[i])); x += widths[i]; }
            return spans;
        }

        // The row's text for screen readers: the name and the shown cells
        string RowName(Download d)
        {
            return string.Join(", ", new[] { Core.ShortName(d) }.Concat(Shown.Select(c => Core.Cell(d, c, Queues, DateTime.Now))).Where(s => s != ""));
        }

        internal void HeaderClick(string column, MouseButtons button, Point p)
        {
            if (button == MouseButtons.Left) { var h = HeaderClicked; if (h != null && column != null) h(column); }
            else if (button == MouseButtons.Right) { var h = HeaderMenuRequested; if (h != null) h(p); }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.List)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (items.Count == 0) { PaintEmpty(g); return; }
            var first = Math.Max(0, IndexAt(0));
            for (var i = first; i < items.Count; i++)
            {
                var r = RowRect(i);
                if (r.Top > ClientSize.Height) break;
                if (r.IntersectsWith(e.ClipRectangle)) PaintRow(g, items[i], r, i);
            }
        }

        void PaintRow(Graphics g, Download d, Rectangle r, int i)
        {
            var sel = selected.Contains(d.Id);
            if (sel || i == hover) using (var b = new SolidBrush(sel ? Theme.Sel : Theme.Hover)) g.FillRectangle(b, r);
            var pad = Theme.Px(12);
            // a line only where there is progress to show: empty tracks under Scheduled and Failed are noise
            var bar = d.Status == "downloading" || d.Status == "queued" || d.Status == "paused" || (d.Status == "error" || d.Status == "scheduled") && Core.Fraction(d, true) > 0;
            var lineY = bar ? r.Y + Theme.Px(7) : r.Y + (r.Height - Theme.Px(20)) / 2;
            var lineH = Theme.Px(20);
            var cat = CategoryOf(d);
            // Windows' icon for the file type, centered on the name's line; drawn as is on the selection
            var icon = new Rectangle(r.X + pad, lineY + (lineH - Theme.Px(24)) / 2, Theme.Px(24), Theme.Px(24));
            if (!FileIcons.Draw(g, Core.IconExt(string.IsNullOrEmpty(d.File) ? Core.ShortName(d) : d.File, cat), icon, 24))
                Theme.DrawGlyph(g, Glyph.Of(cat), icon, sel ? Theme.OnSel : Theme.Category(cat), 16);
            var spans = Spans();
            var now = DateTime.Now;
            // finished rows: everything but the name in muted; the status of a failed one in err
            foreach (var c in spans.Skip(1))
            {
                var color = sel ? Theme.OnSel : d.Status == "done" ? Theme.Muted : c.Item1 == "status" && d.Status == "error" ? Theme.Err : Theme.Text;
                Theme.DrawText(g, Core.Cell(d, c.Item1, Queues, now), Theme.Body, new Rectangle(r.X + c.Item2, lineY, Math.Max(0, c.Item3 - pad), lineH), color);
            }
            var right = r.X + spans[0].Item3 - pad;
            var x = r.X + pad + Theme.Px(24 + 12);
            Theme.DrawText(g, Core.ShortName(d), Theme.Body, new Rectangle(x, lineY, Math.Max(0, right - x), lineH), sel ? Theme.OnSel : Theme.Text);
            if (bar)
            {
                var kind = d.Status == "error" ? BarKind.Err : d.Status == "paused" || d.Status == "scheduled" ? BarKind.Paused : BarKind.Normal;
                // on the selection color the accent would vanish: onSel over a faded onSel track
                var track = sel ? Theme.Mix(Theme.Sel, Theme.OnSel, 0.3) : Theme.Bar;
                var fill = sel && kind == BarKind.Normal ? Theme.OnSel : SegmentBar.FillColor(kind);
                SegmentBar.Draw(g, new Rectangle(x, r.Y + Theme.Px(32), right - x, Theme.Px(3)), new List<double[]> { new[] { 0, Core.Fraction(d, true) } }, track, fill);
            }
            if (d.Id == cursor && Focused && ShowFocusCues)
            {
                var ring = new Rectangle(r.X, r.Y, r.Width, r.Height);
                using (var p = new Pen(sel ? Theme.OnSel : Theme.Accent, Theme.Px(2))) g.DrawRectangle(p, ring.X + Theme.Px(2), ring.Y + Theme.Px(2), ring.Width - Theme.Px(4), ring.Height - Theme.Px(4));
            }
        }

        Rectangle[] EmptyLayout()
        {
            var w = Math.Min(ClientSize.Width - Theme.Px(48), Theme.Px(360));
            var logo = FirstRun ? Theme.Px(64) : 0;
            var titleH = Theme.Bold.Height;
            var hintH = Theme.MeasureHeight(EmptyHint, Theme.Body, w);
            var btnH = FirstRun ? EmptyButton.Height : 0;
            var total = logo + (logo > 0 ? Theme.Px(16) : 0) + titleH + Theme.Px(6) + hintH + (btnH > 0 ? Theme.Px(16) + btnH : 0);
            var y = Math.Max(Theme.Px(16), (ClientSize.Height - total) / 2);
            var cx = ClientSize.Width / 2;
            var rLogo = new Rectangle(cx - logo / 2, y, logo, logo);
            if (logo > 0) y += logo + Theme.Px(16);
            var rTitle = new Rectangle(cx - w / 2, y, w, titleH);
            y += titleH + Theme.Px(6);
            var rHint = new Rectangle(cx - w / 2, y, w, hintH);
            y += hintH + Theme.Px(16);
            var rBtn = new Rectangle(cx - EmptyButton.Width / 2, y, EmptyButton.Width, btnH);
            return new[] { rLogo, rTitle, rHint, rBtn };
        }

        void PlaceEmptyButton()
        {
            EmptyButton.Visible = items.Count == 0 && FirstRun;
            if (EmptyButton.Visible) EmptyButton.Bounds = EmptyLayout()[3];
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            PlaceEmptyButton();
        }

        void PaintEmpty(Graphics g)
        {
            var l = EmptyLayout();
            if (FirstRun && Logo != null)
            {
                using (var a = new System.Drawing.Imaging.ImageAttributes())
                {
                    a.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.4f });
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(Logo, l[0], 0, 0, Logo.Width, Logo.Height, GraphicsUnit.Pixel, a);
                }
            }
            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            Theme.DrawText(g, EmptyTitle, Theme.Bold, l[1], Theme.Text, center);
            Theme.DrawText(g, EmptyHint, Theme.Body, l[2], Theme.Muted, center);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            var me = this;
            ListAccessible list = null;
            list = new ListAccessible(this, () => items.Count, i =>
            {
                var d = items[i];
                return new ItemAccessible(list, () => RowName(d), () => selected.Contains(d.Id), () => Focused && d.Id == cursor,
                    () => me.RectangleToScreen(RowRect(items.IndexOf(d))), () => { var k = items.IndexOf(d); if (k >= 0) Pick(k, "only"); });
            });
            return list;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) scroller.Dispose();
            base.Dispose(disposing);
        }
    }

    // The list's column titles (32 high, muted 12 px, bottom hairline), on the list's columns. Click sorts (the list's
    // HeaderClicked), right-click opens the column chooser; the keyboard sorts through the Sort menu.
    public class ListHeader : Painted
    {
        readonly DownloadList list;

        public ListHeader(DownloadList list)
        {
            this.list = list;
            Height = Theme.Px(32);
            AccessibleName = "Column headers";
            AccessibleRole = AccessibleRole.ColumnHeader;
        }

        string ColumnAt(int x)
        {
            var c = list.Spans().FirstOrDefault(s => x >= s.Item2 && x < s.Item2 + s.Item3);
            return c == null ? null : c.Item1;
        }

        protected override void OnMouseClick(MouseEventArgs e) { list.HeaderClick(ColumnAt(e.X), e.Button, e.Location); base.OnMouseClick(e); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.List)) e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var pad = Theme.Px(12);
            var h = Height - 1;
            foreach (var c in list.Spans())
            {
                var title = Core.ColumnTitles[Array.IndexOf(Core.Columns, c.Item1)];
                // over the cells: Name's title above the icon, the others where their text starts
                var x = c.Item2 + (c.Item1 == "name" ? pad : 0);
                var w = Math.Max(0, c.Item3 - (c.Item1 == "name" ? 2 * pad : pad));
                var sorted = c.Item1 == list.SortColumn;
                var tw = Math.Min(Theme.Measure(title, Theme.Small).Width + Theme.Px(1), w);
                Theme.DrawText(g, title, Theme.Small, new Rectangle(x, 0, tw, h), sorted ? Theme.Text : Theme.Muted);
                if (sorted) Theme.DrawGlyph(g, list.SortDesc ? Glyph.ChevronDown : Glyph.ChevronUp, new Rectangle(x + tw + Theme.Px(4), 0, Theme.Px(9), h), Theme.Text, 9);
            }
            Theme.HLine(g, 0, Width, h);
        }
    }
}
