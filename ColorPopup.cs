// ===========================================================================
//  ColorPopup - the card color picker: nine dots in a row
// ---------------------------------------------------------------------------
//  Color is self-describing; names next to swatches were noise. A small
//  borderless strip appears at the cursor, one dot per tint (a hollow ring
//  for none), the current choice wears a ring. Click picks, clicking
//  anywhere else - or Escape - dismisses.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Polyclicker
{
    // A real control wearing the drawn-chip look, for chrome that lives
    // outside the card surface. Same rounded body, same glyph set, same hover
    // behavior as the chips on the cards. Implements IButtonControl so it can
    // be a dialog's OK / Cancel.
    sealed class ChipButton : Control, IButtonControl
    {
        public string Kind = "gear";
        public Func<Theme.ChipStyle> Style;
        bool hot;
        DialogResult result = DialogResult.None;

        public ChipButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Theme.Hand;
        }

        public DialogResult DialogResult
        {
            get { return result; }
            set { result = value; }
        }

        public void NotifyDefault(bool value) { }
        public void PerformClick() { OnClick(EventArgs.Empty); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (result != DialogResult.None)
            {
                Form f = FindForm();
                if (f != null) f.DialogResult = result;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var b = new SolidBrush(Theme.FormBack))
                e.Graphics.FillRectangle(b, ClientRectangle);
            Theme.ChipStyle cs = Style != null ? Style() : Theme.NeutralChip;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Glyphs.Chip(e.Graphics, r, Text.Length > 0 ? "" : Kind, hot, cs);
            if (Text.Length == 0) return;
            if (Kind.Length > 0)
            {
                // glyph beside centered text
                int tw = TextRenderer.MeasureText(Text, Font).Width;
                int total = 18 + 4 + tw;
                int sx = (Width - total) / 2;
                Glyphs.Draw(e.Graphics, new Rectangle(sx, Height / 2 - 10, 20, 20), Kind,
                            cs.Text, hot ? cs.Hot : cs.Back);
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new Point(sx + 22, (Height - Font.Height) / 2 - 1), cs.Text,
                    TextFormatFlags.NoPrefix);
            }
            else
                TextRenderer.DrawText(e.Graphics, Text, Font, r, cs.Text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); hot = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override void OnTextChanged(EventArgs e)
        { base.OnTextChanged(e); Invalidate(); }
    }

    // A drawn radio button: ring and dot in the app's accent, instead of the
    // system's Windows-blue glyph. Mutual exclusion is by parent - checking
    // one unchecks its RadioDot siblings, the way RadioButtons group.
    sealed class RadioDot : Control
    {
        public event EventHandler CheckedChanged;
        bool isChecked, hot;

        public RadioDot()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Theme.Hand;
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (isChecked == value) return;
                isChecked = value;
                Invalidate();
                if (value && Parent != null)
                    foreach (Control c in Parent.Controls)
                    {
                        var r = c as RadioDot;
                        if (r != null && r != this) r.Checked = false;
                    }
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            var prev = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var ring = new Rectangle(1, (Height - 14) / 2, 13, 13);
            using (var b = new SolidBrush(Theme.FieldBack)) g.FillEllipse(b, ring);
            using (var p = new Pen(isChecked ? Theme.Accent
                                 : hot ? Theme.MutedText : Theme.FieldLine, 1.4f))
                g.DrawEllipse(p, ring);
            if (isChecked)
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillEllipse(b, ring.X + 4, ring.Y + 4, ring.Width - 8, ring.Height - 8);
            g.SmoothingMode = prev;
            TextRenderer.DrawText(g, Text, Font,
                new Rectangle(ring.Right + 7, 0, Width - ring.Right - 7, Height),
                Theme.FormText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
              | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); Checked = true; }

        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); hot = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); hot = false; Invalidate(); }
    }

    // The dropdown popup: a borderless drawn list in theme colors, replacing
    // the system COMBOLBOX whose chrome can't be styled. Click picks; Escape,
    // clicking elsewhere, or losing focus dismisses. Arrow keys, Home/End and
    // Enter work the way the native list's did.
    sealed class DropList : Form
    {
        internal static Rectangle Optical(Rectangle r) { r.Offset(0, -1); return r; }

        readonly IList<string> items;
        readonly Action<string> pick;
        int sel;                    // keyboard selection
        int hot = -1;               // mouse hover
        int top;                    // first visible row (long lists scroll)
        readonly int visible;

        int ItemH { get { return Font.Height + 8; } }

        public static void Open(Control anchor, Rectangle screenRect,
                                IList<string> items, string current, Action<string> onPick)
        {
            if (items.Count == 0) return;
            var d = new DropList(items, current, onPick, screenRect);
            Form owner = anchor != null ? anchor.FindForm() : null;
            if (owner != null) d.Show(owner); else d.Show();
            d.Activate();
        }

        DropList(IList<string> items, string current, Action<string> onPick, Rectangle at)
        {
            this.items = items;
            pick = onPick;
            Font = Theme.UIFont;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            // The list is the button opened, so it wears the button's fill,
            // edge and text rather than a pair of its own.
            BackColor = Theme.DropBack;

            sel = Math.Max(0, items.IndexOf(current));
            visible = Math.Min(items.Count, 14);
            if (sel >= visible) top = Math.Min(sel, items.Count - visible);

            HandleCreated += delegate { Theme.RoundPopup(Handle); };

            ClientSize = new Size(Math.Max(120, at.Width), visible * ItemH + 2);
            Rectangle scr = Screen.FromPoint(at.Location).WorkingArea;
            int x = Math.Max(scr.Left + 2, Math.Min(at.Left, scr.Right - Width - 2));
            int y = at.Bottom + 1;
            if (y + Height > scr.Bottom) y = at.Top - Height - 1;   // flip above
            Location = new Point(x, Math.Max(scr.Top + 2, y));

            Deactivate += delegate { Close(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;        // TOOLWINDOW
                return cp;
            }
        }

        int IndexAt(Point p)
        {
            int i = top + (p.Y - 1) / ItemH;
            return i >= top && i < Math.Min(items.Count, top + visible) ? i : -1;
        }

        void EnsureVisible()
        {
            if (sel < top) top = sel;
            else if (sel >= top + visible) top = sel - visible + 1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (var p = new Pen(Theme.DropLine))
                g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            for (int row = 0; row < visible; row++)
            {
                int i = top + row;
                if (i >= items.Count) break;
                var r = new Rectangle(1, 1 + row * ItemH, ClientSize.Width - 2, ItemH);
                bool active = i == (hot >= 0 ? hot : sel);
                if (active)
                    using (var b = new SolidBrush(Theme.Dark ? Color.FromArgb(56, 56, 64)
                                                             : Color.FromArgb(230, 230, 236)))
                        g.FillRectangle(b, r);
                TextRenderer.DrawText(g, items[i], Font,
                    Optical(new Rectangle(r.X + 7, r.Y, r.Width - 12, r.Height)),
                    Theme.DropText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i != hot) { hot = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); if (hot != -1) { hot = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = IndexAt(e.Location);
            Close();
            if (i >= 0 && pick != null) pick(items[i]);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max = Math.Max(0, items.Count - visible);
            top = Math.Max(0, Math.Min(max, top - Math.Sign(e.Delta) * 3));
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Escape: Close(); break;
                case Keys.Up: sel = Math.Max(0, sel - 1); hot = -1; EnsureVisible(); Invalidate(); break;
                case Keys.Down: sel = Math.Min(items.Count - 1, sel + 1); hot = -1; EnsureVisible(); Invalidate(); break;
                case Keys.Home: sel = 0; hot = -1; EnsureVisible(); Invalidate(); break;
                case Keys.End: sel = items.Count - 1; hot = -1; EnsureVisible(); Invalidate(); break;
                case Keys.Enter:
                    int i = hot >= 0 ? hot : sel;
                    Close();
                    if (i >= 0 && i < items.Count && pick != null) pick(items[i]);
                    break;
            }
        }
    }

    // The drawn combo face that opens a DropList - the last native combo gone.
    // Text is the selection, so WM_GETTEXT (and any tooling) reads it plainly.
    sealed class DropButton : Control
    {
        static Rectangle Optical(Rectangle r) { return DropList.Optical(r); }

        public readonly List<string> Items = new List<string>();
        // Shown, muted, only while there is no value. A standing label beside
        // the control was a second thing to read and paid for the width twice;
        // once something is selected the name says what the control is.
        public string Placeholder = "";
        public event Action<string> Picked;
        public event Action Opening;             // repopulate just-in-time
        bool hot;

        public DropButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Theme.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            // Corners painted as the form behind - no Region, no chewed arcs
            using (var b = new SolidBrush(Theme.FormBack)) g.FillRectangle(b, ClientRectangle);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            var prevSm = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.RoundPath(r, 4))
            {
                using (var b = new SolidBrush(hot ? Theme.DropHot : Theme.DropBack))
                    g.FillPath(b, path);
                using (var p = new Pen(Theme.DropLine)) g.DrawPath(p, path);
            }
            g.SmoothingMode = prevSm;
            const TextFormatFlags TF = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                     | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                                     | TextFormatFlags.NoPadding;
            bool empty = Text.Length == 0;
            TextRenderer.DrawText(g, empty ? Placeholder : Text, Font,
                Optical(new Rectangle(8, 0, Math.Max(4, Width - 30), Height)),
                empty ? Theme.MutedText : Theme.DropText, TF);
            // the arrow, in the same stroke style as everything else
            var prev = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.MutedText, 1.6f))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                float ax = Width - 14, ay = Height / 2f - 1.5f;
                g.DrawLine(p, ax - 3.5f, ay, ax, ay + 3.5f);
                g.DrawLine(p, ax + 3.5f, ay, ax, ay + 3.5f);
            }
            g.SmoothingMode = prev;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Opening != null) Opening();
            DropList.Open(this, RectangleToScreen(ClientRectangle), Items, Text,
                delegate(string v)
                {
                    Text = v;
                    if (Picked != null) Picked(v);
                });
        }

        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); hot = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override void OnTextChanged(EventArgs e)
        { base.OnTextChanged(e); Invalidate(); }
    }

    sealed class ColorPopup : Form
    {
        const int Pad = 10, Step = 28, DotR = 7;

        readonly Action<string> pick;
        readonly string current;
        int hot = -1;

        public ColorPopup(string current, Action<string> pick)
        {
            this.current = (current ?? "").ToLowerInvariant();
            this.pick = pick;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = Theme.Dark ? Color.FromArgb(43, 43, 50) : Color.White;
            ClientSize = new Size(Pad * 2 + Step * Theme.Palette.Length, 36);
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) Close();
            };
            Deactivate += delegate { Close(); };
            HandleCreated += delegate { Theme.RoundPopup(Handle); };

            // At the cursor, nudged back onto the screen at edges
            Point p = Cursor.Position;
            Rectangle scr = Screen.FromPoint(p).WorkingArea;
            int x = Math.Min(p.X - 12, scr.Right - Width - 4);
            int y = Math.Min(p.Y + 14, scr.Bottom - Height - 4);
            Location = new Point(Math.Max(scr.Left + 4, x), Math.Max(scr.Top + 4, y));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;        // TOOLWINDOW: no taskbar, no alt-tab
                return cp;
            }
        }

        Rectangle CellAt(int i)
        {
            return new Rectangle(Pad + i * Step, 0, Step, ClientSize.Height);
        }

        int IndexAt(Point p)
        {
            for (int i = 0; i < Theme.Palette.Length; i++)
                if (CellAt(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.ChipLine))
                g.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

            for (int i = 0; i < Theme.Palette.Length; i++)
            {
                Rectangle cell = CellAt(i);
                float cx = cell.X + cell.Width / 2f, cy = ClientSize.Height / 2f;
                if (i == hot)
                    using (var b = new SolidBrush(Theme.ChipHot))
                        g.FillEllipse(b, cx - 11, cy - 11, 22, 22);

                CardTint t = Theme.Palette[i];
                Color dot = Theme.SwatchOf(t.Name);
                if (t.Name.Length == 0)
                    using (var p = new Pen(dot, 1.8f))
                        g.DrawEllipse(p, cx - DotR, cy - DotR, DotR * 2, DotR * 2);
                else
                    using (var b = new SolidBrush(dot))
                        g.FillEllipse(b, cx - DotR, cy - DotR, DotR * 2, DotR * 2);

                if (t.Name == current)
                    using (var p = new Pen(Theme.FormText, 1.6f))
                        g.DrawEllipse(p, cx - DotR - 3.5f, cy - DotR - 3.5f,
                                      (DotR + 3.5f) * 2, (DotR + 3.5f) * 2);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexAt(e.Location);
            if (i != hot) { hot = i; Invalidate(); }
            Cursor = i >= 0 ? Theme.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hot != -1) { hot = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = IndexAt(e.Location);
            if (i >= 0 && pick != null) pick(Theme.Palette[i].Name);
            Close();
        }
    }
}
