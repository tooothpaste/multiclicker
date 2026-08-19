// ===========================================================================
//  Field - a TextBox wearing the theme's border instead of the system's
//  NumberBox - the same, but digits only
// ---------------------------------------------------------------------------
//  A native TextBox draws its frame in non-client space with system colors:
//  square gray at rest, Windows blue when focused - neither matches the drawn
//  fields everywhere else in the app. The frame is repainted here over the
//  WM_NCPAINT pass: FieldLine at rest, the app's accent while focused, same
//  as the HotkeyBox and every drawn field.
//
//  NumberBox uses ES_NUMBER. Filtering on KeyPress would be close, but the
//  style also blocks a non-numeric paste and beeps the way the rest of
//  Windows does. It matters more than it looks: the interval field falls back
//  to a default when it can't parse, so a field that accepts "12ab3" turns a
//  typo into a silent change of clicking speed.
// ===========================================================================

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Polyclicker
{
    class Field : TextBox
    {
        public Field() { BorderStyle = BorderStyle.FixedSingle; }

        // The visual-styles EDIT paints its own frame - Windows-accent blue
        // when focused - straight over anything drawn here. Stripping the
        // theme returns the frame to the classic 1px NCPAINT pass, which the
        // override below can actually own.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowTheme(Handle, "", ""); } catch { }
        }

        const int WM_NCPAINT = 0x0085, WM_PAINT = 0x000F;
        const uint RDW_FRAME = 0x0400, RDW_INVALIDATE = 0x0001;
        [DllImport("user32.dll")] static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rc, IntPtr rgn, uint flags);

        // What actually sits behind this box - the corners are painted in it
        // to fake the transparency a square control can't have. A Region clip
        // won't do: regions are aliased, and the stair-steps chew the
        // border's corner arcs.
        public Func<Color> Backdrop = delegate { return Theme.FormBack; };

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            // Both passes: the EDIT redraws its classic frame from more than
            // one place, and whoever painted last wins
            if (m.Msg == WM_NCPAINT || m.Msg == WM_PAINT) PaintFrame();
            if (m.Msg == WM_PAINT) PaintSelection();
        }

        void PaintFrame()
        {
            if (!IsHandleCreated) return;
            IntPtr dc = GetWindowDC(Handle);
            if (dc == IntPtr.Zero) return;
            try
            {
                using (Graphics g = Graphics.FromHdc(dc))
                using (var path = Theme.RoundPath(new Rectangle(0, 0, Width - 1, Height - 1), 4))
                {
                    // Erase the classic square frame, then dress the corners
                    // as the surface behind, then the antialiased border
                    using (var p = new Pen(BackColor))
                        g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
                    using (var outside = new Region(new Rectangle(0, 0, Width, Height)))
                    {
                        outside.Exclude(path);
                        using (var b = new SolidBrush(Backdrop()))
                            g.FillRegion(b, outside);
                    }
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(Focused ? Theme.Accent : Theme.FieldLine))
                        g.DrawPath(p, path);
                }
            }
            finally { ReleaseDC(Handle, dc); }
        }

        // The EDIT highlights selection in the system color - a blue nothing
        // else in the app wears. Repaint the selected band in the accent, text
        // included, right after every paint.
        void PaintSelection()
        {
            if (!Focused || SelectionLength == 0 || TextLength == 0) return;
            int s = SelectionStart, e2 = s + SelectionLength;
            string sub = Text.Substring(s, e2 - s);
            Point p1 = GetPositionFromCharIndex(s);
            Rectangle client = ClientRectangle;
            using (Graphics g = CreateGraphics())
            {
                int x2 = e2 < TextLength
                    ? GetPositionFromCharIndex(e2).X
                    : p1.X + TextRenderer.MeasureText(g, sub, Font,
                          new Size(int.MaxValue, int.MaxValue),
                          TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width + 1;
                int x1 = Math.Max(client.Left, p1.X);
                x2 = Math.Min(client.Right, x2);
                if (x2 <= x1) return;
                int h = Math.Min(Font.Height + 1, client.Height - p1.Y);
                using (var b = new SolidBrush(Theme.Accent))
                    g.FillRectangle(b, new Rectangle(x1, p1.Y, x2 - x1, h));
                Color txt = Theme.Dark ? Color.FromArgb(28, 28, 33) : Color.White;
                TextRenderer.DrawText(g, sub, Font, new Point(p1.X, p1.Y), txt,
                    TextFormatFlags.NoPadding | TextFormatFlags.SingleLine
                  | TextFormatFlags.NoPrefix);
            }
        }

        // The frame color depends on focus, so focus changes redraw it
        void Reframe()
        {
            if (IsHandleCreated)
                RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, RDW_FRAME | RDW_INVALIDATE);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Reframe(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Reframe(); }
    }

    sealed class NumberBox : Field
    {
        const int ES_NUMBER = 0x2000;

        // ES_NUMBER rejects the minus sign outright, and screen coordinates
        // are negative on any monitor left of or above the primary - the X/Y
        // boxes couldn't type a position the Pick capture had no trouble
        // saving. Those boxes set this; the interval box keeps pure ES_NUMBER
        // (an interval is never negative, and the style's paste-blocking is
        // worth keeping where it fits). Set before the handle is created.
        public bool AllowNegative;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (!AllowNegative) cp.Style |= ES_NUMBER;
                return cp;
            }
        }

        // The by-hand filter for the negative boxes: digits everywhere, one
        // '-' and only as the first character. Control chars pass (backspace).
        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (!AllowNegative || e.Handled || char.IsControl(e.KeyChar)) return;
            if (char.IsDigit(e.KeyChar)) return;
            if (e.KeyChar == '-' && SelectionStart == 0
                && (SelectionLength > 0 || Text.IndexOf('-') < 0)) return;
            e.Handled = true;
        }
    }
}
