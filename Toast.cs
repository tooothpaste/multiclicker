// ===========================================================================
//  CursorToast - a small confirmation that appears under the mouse
// ---------------------------------------------------------------------------
//  The kill switch works from anywhere, so its feedback has to reach anywhere
//  too: a tray icon changing 1400 pixels away is not feedback for a hotkey
//  pressed mid-game. This is a tiny topmost window at the cursor that never
//  takes focus - the game keeps the keyboard - and removes itself.
// ===========================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace Polyclicker
{
    // A small red dot at an exact screen point - the "show me where it
    // clicks" preview. Click-through and unfocusable: it marks the spot, it
    // must never BE the thing that gets clicked.
    sealed class SpotDot : Form
    {
        static SpotDot open;

        public static void Pop(int x, int y)
        {
            if (open != null) { try { open.Close(); } catch { } open = null; }
            var d = new SpotDot(x, y);
            open = d;
            d.Show();
        }

        readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();
        const int R = 9;                        // dot radius in px

        SpotDot(int x, int y)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Bounds = new Rectangle(x - R, y - R, R * 2, R * 2);
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(0, 0, R * 2, R * 2);
            Region = new Region(path);
            BackColor = Color.FromArgb(224, 58, 48);

            life.Interval = 1400;
            life.Tick += delegate { life.Stop(); if (open == this) open = null; Close(); };
            life.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // white ring so it reads on any background, dark center pip
            using (var p = new Pen(Color.White, 2f))
                g.DrawEllipse(p, 1.5f, 1.5f, R * 2 - 3, R * 2 - 3);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, R - 1.5f, R - 1.5f, 3f, 3f);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // NOACTIVATE | TOOLWINDOW | LAYERED | TRANSPARENT: never
                // focused, never in Alt-Tab, clicks fall straight through.
                // LAYERED matters: a top-level TRANSPARENT window without it
                // simply never paints.
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetLayeredWindowAttributes(Handle, 0, 255, 2);   // LWA_ALPHA, opaque
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) life.Dispose();
            base.Dispose(disposing);
        }
    }

    sealed class CursorToast : Form
    {
        static CursorToast open;

        public static void Pop(string text) { Pop(text, 1200, false); }

        // follow: the toast rides along with the pointer - for the pick-
        // position countdown, where the user is aiming at the same time.
        public static void Pop(string text, int lifeMs, bool follow)
        {
            if (open != null) { try { open.Close(); } catch { } open = null; }
            var t = new CursorToast(text, lifeMs, follow);
            open = t;
            t.Show();
        }

        readonly Timer life = new Timer();
        readonly Timer chase;

        CursorToast(string text, int lifeMs, bool follow)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Dark ? Color.FromArgb(58, 58, 66) : Color.FromArgb(0x32, 0x32, 0x38);
            ForeColor = Color.White;

            var l = new Label();
            l.AutoSize = true;
            l.Font = new Font(Theme.UIFont.FontFamily, 9.75f);
            l.ForeColor = Color.White;
            l.BackColor = BackColor;
            l.Text = text;
            l.Location = new Point(12, 7);
            Controls.Add(l);
            ClientSize = new Size(l.PreferredWidth + 24, l.PreferredHeight + 14);

            Reposition();
            HandleCreated += delegate { Theme.RoundPopup(Handle); };

            if (follow)
            {
                chase = new Timer();
                chase.Interval = 30;
                chase.Tick += delegate { Reposition(); };
                chase.Start();
            }

            life.Interval = lifeMs;
            life.Tick += delegate { life.Stop(); if (open == this) open = null; Close(); };
            life.Start();
        }

        // Just south-east of the pointer, nudged back on-screen at edges
        void Reposition()
        {
            Point p = Cursor.Position;
            Rectangle scr = Screen.FromPoint(p).WorkingArea;
            int x = Math.Min(p.X + 16, scr.Right - Width - 4);
            int y = Math.Min(p.Y + 22, scr.Bottom - Height - 4);
            Location = new Point(Math.Max(scr.Left + 4, x), Math.Max(scr.Top + 4, y));
        }

        // Never steal focus: the whole point is not interrupting whatever the
        // user is doing when the hotkey fires.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080;   // NOACTIVATE | TOOLWINDOW
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { life.Dispose(); if (chase != null) chase.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
