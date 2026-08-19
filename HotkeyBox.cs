// ===========================================================================
//  HotkeyBox - a field that records the next key combination pressed
// ---------------------------------------------------------------------------
//  WinForms has no hotkey control. The Win32 one (msctls_hotkey32) exists but
//  won't display F13-F24: a card bound to F13 shows an empty box while the
//  status line insists it is set.
//
//  Drawn, not a TextBox: it never edits text - it displays a combo and
//  captures keys - and as a native EDIT it wore the system's square chrome
//  while every other field in the app is drawn flat with the theme's field
//  colors. Now it paints exactly like a DropButton face, with the accent
//  border while it's focused and listening.
// ===========================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Polyclicker
{
    sealed class HotkeyBox : Control
    {
        HotkeyCombo _combo;
        public event EventHandler ComboChanged;
        bool hot;

        // Single-key mode, for the card's Custom Key field: it records ONE
        // key rather than a combination, so a bare modifier - Shift, Ctrl -
        // is a valid choice instead of "the user still typing", and held
        // modifiers are not folded into the result.
        public bool SingleKey;

        public HotkeyBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                   | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                   | ControlStyles.Selectable, true);
            Cursor = Theme.Hand;
            TabStop = true;
            Text = "None";
            BackColor = Theme.FieldBack;
            ForeColor = Theme.FieldText;
        }

        public HotkeyCombo Combo
        {
            get { return _combo; }
            set { _combo = value; Text = _combo.IsSet ? _combo.ToString() : "None"; }
        }

        public string Spec
        {
            get { return HotkeyParser.ToSpec(_combo); }
            set { Combo = HotkeyParser.Parse(value); }
        }

        // What sits behind the box - the corners are painted in it, since a
        // square control can't be transparent and Region clips chew the arcs
        public Func<Color> Backdrop = delegate { return Theme.FormBack; };

        static Rectangle Optical(Rectangle r) { r.Offset(0, -1); return r; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Backdrop())) g.FillRectangle(b, ClientRectangle);
            var prevSm = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = Theme.RoundPath(r, 4))
            {
                using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
                // The accent border says "listening"
                using (var p = new Pen(Focused ? Theme.Accent
                                     : hot ? Theme.MutedText : Theme.FieldLine))
                    g.DrawPath(p, path);
            }
            g.SmoothingMode = prevSm;
            TextRenderer.DrawText(g, Text, Font,
                Optical(new Rectangle(5, 0, Width - 10, Height)),
                _combo.IsSet ? ForeColor : Theme.MutedText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
              | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); Focus(); }

        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); hot = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); hot = false; Invalidate(); }

        protected override void OnTextChanged(EventArgs e)
        { base.OnTextChanged(e); Invalidate(); }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetKeyState(int vk);
        static bool Down(int vk) { return (GetKeyState(vk) & 0x8000) != 0; }

        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const int WM_CHAR = 0x0102, WM_SYSCHAR = 0x0106, WM_DEADCHAR = 0x0103;

        protected override bool IsInputKey(Keys keyData) { return true; }

        // Two routes in, because WinForms decides between them before the
        // control gets a say:
        //
        //  - Anything with Alt arrives as WM_SYSKEYDOWN and is offered around as
        //    a menu mnemonic first, so it has to be claimed in ProcessCmdKey or
        //    it never becomes a KeyDown.
        //  - Ctrl+Alt together is AltGr as far as the framework is concerned. It
        //    deliberately keeps those out of the command-key path so they can
        //    type characters, which is why Ctrl+Alt+K - the shape of every
        //    default hotkey this app ships with - recorded nothing at all while
        //    Ctrl+K and Alt+K each worked. That one only shows up in WndProc.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Focused || IsNavKey((int)(keyData & Keys.KeyCode)))
                return base.ProcessCmdKey(ref msg, keyData);
            // Leave AltGr alone here; WndProc below picks it up
            if ((keyData & (Keys.Control | Keys.Alt)) == (Keys.Control | Keys.Alt))
                return base.ProcessCmdKey(ref msg, keyData);
            TakeKey((int)(keyData & Keys.KeyCode));
            return true;                 // ours: no mnemonic, no dialog key
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYDOWN || m.Msg == WM_SYSKEYDOWN)
            {
                int vk = m.WParam.ToInt32();
                // Modifiers have to reach DefWindowProc. Swallowing the Alt
                // keydown here left Windows' own key state mid-press, and the
                // key struck next was never dispatched to us at all - which is
                // exactly how Ctrl+Alt+K went missing while Ctrl+Shift+K was
                // fine. TakeKey ignores them anyway. Nav keys likewise pass so
                // Tab can leave the box.
                if ((SingleKey || !IsModifier(vk)) && !IsNavKey(vk))
                {
                    TakeKey(vk);
                    return;              // swallowed
                }
            }
            // The characters those keys would have produced
            if (m.Msg == WM_CHAR || m.Msg == WM_SYSCHAR || m.Msg == WM_DEADCHAR)
                return;
            base.WndProc(ref m);
        }

        // Shift, Ctrl, Alt and either Windows key.
        //
        // Both spellings matter. Window messages carry the neutral codes
        // (VK_CONTROL), but the hook reports the physical key - left Ctrl is
        // 0xA2, not 0x11. Missing those meant the capture path treated a held
        // Ctrl as the hotkey itself and swallowed it, so Windows never recorded
        // Ctrl as down and every combination came through stripped to its bare
        // key.
        public static bool IsModifier(int vk)
        {
            return vk == 0x10 || vk == 0x11 || vk == 0x12          // Shift/Ctrl/Alt
                || vk == 0x5B || vk == 0x5C                        // either Win
                || (vk >= 0xA0 && vk <= 0xA5);                     // left/right of each
        }

        // --- recording from the hook ------------------------------------------
        //
        //  Window messages are not enough. Hold Ctrl+Alt and Windows treats the
        //  pair as AltGr: the key struck next is never dispatched to the focused
        //  control at all - not as WM_KEYDOWN, not as WM_SYSKEYDOWN, not through
        //  the command-key path. Ctrl+Shift+K arrived fine while Ctrl+Alt+K, the
        //  shape of every hotkey this app ships with, arrived not at all.
        //
        //  The hook sees all of them, and sees them exactly as it will see them
        //  later when deciding whether a press matches - so recording from the
        //  hook also guarantees that what the box shows is what will actually
        //  fire. MainForm routes to whichever box has focus.
        public static HotkeyBox Capturing;

        protected override void OnGotFocus(EventArgs e)
        {
            Capturing = this;
            Invalidate();               // the listening border
            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            if (Capturing == this) Capturing = null;
            Invalidate();
            base.OnLostFocus(e);
        }

        // Keys that leave the box instead of binding: Tab moves focus and Enter
        // works the dialog's OK button. Without this, tabbing away from a box
        // you just set quietly rebound it to "Tab" - the box showed the key you
        // chose while the file said otherwise.
        public static bool IsNavKey(int vk) { return vk == 0x09 || vk == 0x0D; }

        // The hook reports physical keys - left Ctrl is 0xA2 - but everything
        // downstream (names, SendInput) speaks the neutral codes
        static ushort Neutral(int vk)
        {
            if (vk == 0xA0 || vk == 0xA1) return 0x10;   // Shift
            if (vk == 0xA2 || vk == 0xA3) return 0x11;   // Ctrl
            if (vk == 0xA4 || vk == 0xA5) return 0x12;   // Alt
            return (ushort)vk;
        }

        public void TakeFromHook(ushort vk, bool ctrl, bool alt, bool shift, bool win)
        {
            if ((!SingleKey && IsModifier(vk)) || IsNavKey(vk)) return;
            if (vk == (int)Keys.Escape || vk == (int)Keys.Back)
            {
                Combo = new HotkeyCombo();
                Raise();
                return;
            }
            var c = new HotkeyCombo();
            c.Vk = Neutral(vk);
            if (!SingleKey) { c.Ctrl = ctrl; c.Alt = alt; c.Shift = shift; c.Win = win; }
            Combo = c;
            Raise();
        }

        void TakeKey(int vk)
        {
            // A modifier on its own isn't a hotkey, it's the user still
            // typing - unless this box records a single key, where it's
            // exactly the kind of choice the mode exists for
            if (!SingleKey && IsModifier(vk)) return;
            // Escape and Backspace clear it - the way to say "no hotkey"
            if (vk == (int)Keys.Escape || vk == (int)Keys.Back)
            {
                Combo = new HotkeyCombo();
                Raise();
                return;
            }
            var c = new HotkeyCombo();
            c.Vk = Neutral(vk);
            if (!SingleKey)
            {
                c.Ctrl = Down(0x11); c.Alt = Down(0x12); c.Shift = Down(0x10);
                c.Win = Down(0x5B) || Down(0x5C);
            }
            Combo = c;
            Raise();
        }

        void Raise()
        {
            var h = ComboChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
