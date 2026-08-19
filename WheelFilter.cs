// ===========================================================================
//  WheelFilter - the wheel scrolls the list, it never edits a card
// ---------------------------------------------------------------------------
//  Windows delivers WM_MOUSEWHEEL to whichever control is under the pointer.
//  A closed DropDownList treats that as "change the selection", so scrolling
//  down the card list would silently flip a card from Current Position to
//  Fixed Position on the way past - a settings change nobody asked for, from
//  a gesture that should only move the view.
//
//  An application-wide filter is the fix: any wheel over the card area is
//  handed to the scrolling panel instead, whatever control it landed on. The
//  one exception is a dropdown that is actually open, where scrolling the
//  list is what the user means.
// ===========================================================================

using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Polyclicker
{
    sealed class WheelFilter : IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        readonly Control _target;

        public WheelFilter(Control target) { _target = target; }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || _target == null || !_target.IsHandleCreated)
                return false;

            POINT p;
            if (!GetCursorPos(out p)) return false;
            IntPtr under = WindowFromPoint(p);
            if (under == IntPtr.Zero) return false;

            Control c = Control.FromHandle(under);
            if (c == null) return false;

            // An open dropdown owns the wheel - that gesture is the user
            // picking from the list they just opened
            var combo = c as ComboBox;
            if (combo != null && combo.DroppedDown) return false;

            // Is it ours? Walk up to see whether the card area contains it
            for (Control w = c; w != null; w = w.Parent)
            {
                if (w == _target)
                {
                    SendMessage(_target.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
                    return true;        // swallowed: the control never sees it
                }
            }
            return false;
        }
    }

    // Ctrl+Backspace deletes the word left of the caret. Native EDIT controls
    // never learned the shortcut - they insert a box character instead - so
    // it's granted here to every text field at once. Hotkey-capture boxes are
    // ReadOnly and skipped.
    sealed class WordDeleteFilter : IMessageFilter
    {
        const int WM_KEYDOWN = 0x0100;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_KEYDOWN || (Keys)m.WParam != Keys.Back) return false;
            if ((Control.ModifierKeys & (Keys.Control | Keys.Alt)) != Keys.Control)
                return false;
            var box = Control.FromHandle(m.HWnd) as TextBoxBase;
            if (box == null || box.ReadOnly) return false;

            int caret = box.SelectionStart;
            if (box.SelectionLength == 0 && caret > 0)
            {
                // back over any whitespace, then over the word itself
                string t = box.Text;
                int i = caret;
                while (i > 0 && char.IsWhiteSpace(t[i - 1])) i--;
                while (i > 0 && !char.IsWhiteSpace(t[i - 1])) i--;
                box.Select(i, caret - i);
            }
            if (box.SelectionLength > 0) box.SelectedText = "";   // undo-friendly
            return true;    // swallowed either way - never type the ^H box glyph
        }
    }
}
