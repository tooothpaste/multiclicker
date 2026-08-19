// ===========================================================================
//  Dialogs - Settings, per-card Advanced options, the window picker, and the
//  small text prompt used for naming profiles and macros
// ---------------------------------------------------------------------------
//  Everything opens centered on the owner (CenterParent) - a pop-up must
//  never land in the middle of another monitor.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace Polyclicker
{
    // A dialog scaffold: owner-centered, fixed border, ESC cancels.
    class AppDialog : Form
    {
        public AppDialog(string title)
        {
            Text = title;
            Font = Theme.UIFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
        }

        // After every control exists, and before the user sees any of them
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Theme.Apply(this);
            Theme.DarkTitleBar(this);
        }

        // The theme flipped while this dialog is open (the live dark toggle):
        // restyle everything, including any glyph images, in place
        public void ReTheme()
        {
            Theme.Apply(this);
            Theme.DarkTitleBar(this);
            Invalidate(true);
        }

        protected Label Muted(string text, int x, int y, int w)
        {
            var l = new Label();
            l.Text = text;
            l.ForeColor = Color.Gray;
            l.Location = new Point(x, y);
            l.Size = new Size(w, 1000);
            l.AutoSize = false;
            // +6, not +4: measuring is exact to the glyph box, and a wrapped
            // line's descenders sat right on the edge of the label
            l.Size = new Size(w, TextRenderer.MeasureText(text, Font, new Size(w, 1000),
                              TextFormatFlags.WordBreak).Height + 6);
            Controls.Add(l);
            return l;
        }

        protected Label Plain(string text, int x, int y, int w)
        {
            var l = new Label();
            l.Text = text;
            l.Location = new Point(x, y);
            l.AutoSize = w == 0;
            if (w > 0) l.Size = new Size(w, 20);
            Controls.Add(l);
            return l;
        }

        // Every dialog button is a drawn chip - rounded in both themes, unlike
        // the stock renderer which only rounds in light mode
        protected ChipButton Btn(string text, int x, int y, int w, int h)
        {
            var b = new ChipButton();
            b.Kind = "";
            b.Text = text;
            b.Style = delegate { return Theme.NeutralChip; };
            b.SetBounds(x, y, w, h);
            Controls.Add(b);
            return b;
        }

        // The same chip with a glyph beside its label
        protected ChipButton IconBtn(string kind, string text, int x, int y, int w, int h)
        {
            ChipButton b = Btn(text, x, y, w, h);
            b.Kind = kind;
            return b;
        }
    }

    // --- the small "give it a name" prompt ---------------------------------
    sealed class TextPrompt : AppDialog
    {
        readonly Field box = new Field();

        public string Value { get { return box.Text.Trim(); } }

        public TextPrompt(string title, string caption, string prefill) : base(title)
        {
            ClientSize = new Size(340, 118);
            Plain(caption, 14, 12, 312);
            box.SetBounds(14, 40, 312, 25);
            box.Text = prefill ?? "";
            box.SelectAll();
            Controls.Add(box);

            ChipButton ok = Btn("OK", 154, 78, 82, 28);
            ChipButton cancel = Btn("Cancel", 244, 78, 82, 28);
            ok.Style = delegate { return Theme.GoChip; };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    // --- Settings ----------------------------------------------------------
    sealed class SettingsDialog : AppDialog
    {
        readonly HotkeyBox stopBox = new HotkeyBox();
        readonly HotkeyBox killBox = new HotkeyBox();
        readonly HotkeyBox recBox = new HotkeyBox();

        public string StopAllKey  { get { return stopBox.Spec; } }
        public string KillKey     { get { return killBox.Spec; } }
        public string RecordKey   { get { return recBox.Spec; } }

        public SettingsDialog(AppConfig cfg, string recordKeyShown, Action<string> onThemeChange)
            : base("Settings")
        {
            // The same computed flow as the Advanced dialog: a running cursor
            // positions each group, and each group is sized to what it holds -
            // equal margins by construction, nothing to clip.
            const int Mx = 12;              // outer margin, all four sides
            const int Gw = 452;             // group width
            const int Ix = 26;              // content indent
            const int Iw = 424;             // content width inside a group
            const int CapH = 26;            // group caption band
            const int Pad = 12;             // padding inside a group's bottom
            const int Gap = 10;             // between groups
            int y = Mx, cy;

            var gHot = new GroupBox();
            gHot.Text = " Global hotkeys ";
            gHot.Location = new Point(Mx, y);
            Controls.Add(gHot);
            cy = y + CapH;
            cy = Muted("Active whatever window has focus. Press Backspace in a box to unassign.",
                       Ix, cy, Iw).Bottom + 4;

            string[] labels = { "Emergency stop", "Toggle all hotkeys", "Start / stop recording" };
            HotkeyBox[] boxes = { stopBox, killBox, recBox };
            string[] values = { cfg.StopAllKey, cfg.KillSwitchKey, cfg.RecordKey };
            for (int i = 0; i < 3; i++)
            {
                Plain(labels[i], Ix, cy + 4, 214);
                boxes[i].SetBounds(250, cy, 200, 25);
                boxes[i].Spec = values[i];
                Controls.Add(boxes[i]);
                boxes[i].BringToFront();
                cy += i < 2 ? 34 : 25;      // last row: its own height, no pitch
            }
            gHot.Size = new Size(Gw, cy + Pad - y);
            y = gHot.Bottom + Gap;

            var gMac = new GroupBox();
            gMac.Text = " Macros ";
            gMac.Location = new Point(Mx, y);
            Controls.Add(gMac);
            cy = y + CapH;
            cy = Muted("Ready a clicker with its ⏺ button, then press " + recordKeyShown
                + " to start and stop recording. The take is saved under that card's name,"
                + " in the Macros folder.", Ix, cy, Iw).Bottom + 6;
            ChipButton openBtn = IconBtn("folder", "Open Macros folder", Ix, cy, 180, 28);
            openBtn.Click += delegate
            {
                try
                {
                    System.IO.Directory.CreateDirectory(AppConfig.MacroDir);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + AppConfig.MacroDir + "\"");
                }
                catch { }
            };
            cy += 28;
            gMac.Size = new Size(Gw, cy + Pad - y);
            y = gMac.Bottom + Gap;

            var gTheme = new GroupBox();
            gTheme.Text = " Theme ";
            gTheme.Location = new Point(Mx, y);
            Controls.Add(gTheme);
            cy = y + CapH;
            // Applies the instant it's clicked - the main window changes
            // behind this dialog, and the dialog itself follows suit
            string[] modes = { "light", "dark", "system" };
            string[] modeLabels = { "Light", "Dark", "System default" };
            int rx = Ix;
            for (int i = 0; i < 3; i++)
            {
                var rb = new RadioDot();
                rb.Text = modeLabels[i];
                int tw = TextRenderer.MeasureText(modeLabels[i], Font).Width;
                rb.SetBounds(rx, cy, tw + 26, 20);
                rb.Checked = cfg.ThemeMode == modes[i];
                string mode = modes[i];
                rb.CheckedChanged += delegate(object s, EventArgs ev)
                {
                    if (!((RadioDot)s).Checked) return;
                    if (onThemeChange != null) onThemeChange(mode);
                    ReTheme();
                };
                Controls.Add(rb);
                rb.BringToFront();
                rx += tw + 46;
            }
            cy += 20;
            gTheme.Size = new Size(Gw, cy + Pad - y);
            y = gTheme.Bottom + Gap + 4;

            ChipButton resetBtn = IconBtn("undo", "Restore defaults", Mx, y, 150, 28);
            resetBtn.Click += delegate
            {
                stopBox.Spec = "^!Escape";
                killBox.Spec = "^!F10";
                recBox.Spec = "^!F9";
            };

            // right edge aligned with the group boxes above
            ChipButton ok = Btn("OK", Mx + Gw - 174, y, 82, 28);
            ChipButton cancel = Btn("Cancel", Mx + Gw - 82, y, 82, 28);
            ok.Style = delegate { return Theme.GoChip; };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
            ClientSize = new Size(Gw + Mx * 2, y + 28 + Mx);

            foreach (var g in new[] { gHot, gMac, gTheme }) g.SendToBack();
        }
    }

    // --- per-card advanced options (the gear) ------------------------------
    sealed class AdvancedDialog : AppDialog
    {
        readonly SlotConfig o;
        readonly DropButton modeDDL = new DropButton();
        readonly DropButton winDDL = new DropButton();
        readonly List<string> winValues = new List<string>();
        string winValue;                        // the gate as stored in the INI
        const string Anywhere = "(Anywhere)";
        readonly CheckBox focusChk = new CheckBox();
        readonly NumberBox clicksEdit = new NumberBox();
        readonly NumberBox secsEdit = new NumberBox();
        readonly NumberBox jitterEdit = new NumberBox();
        readonly NumberBox posJitEdit = new NumberBox();
        readonly CheckBox restoreChk = new CheckBox();
        readonly CheckBox relChk = new CheckBox();
        readonly CheckBox flickChk = new CheckBox();
        readonly NumberBox holdEdit = new NumberBox();
        readonly CheckBox lockChk = new CheckBox();
        readonly CheckBox stopInputChk = new CheckBox();
        Label holdHint;

        public bool ModeChanged;

        public AdvancedDialog(SlotConfig cfg, string cardLabel) : base(cardLabel + " - Advanced")
        {
            o = cfg;
            bool isMacro = cfg.IsMacro;
            string unit = isMacro ? "repeats" : "clicks";

            // One grid for the whole dialog. Every group is positioned from a
            // running cursor and sized to whatever it ends up holding, so the
            // margins stay equal and nothing can be clipped by a stale
            // hardcoded height when a string or a control changes.
            const int Mx = 12;              // outer margin, all four sides
            const int Gw = 394;             // group width
            const int Iw = 366;             // content width inside a group
            const int CapH = 26;            // group caption band
            const int Pad = 12;             // padding inside a group's bottom
            const int Gap = 10;             // between groups
            // Two columns, each with its own running cursor: what starts and
            // where it runs on the left; what stops it and how it clicks on
            // the right. One column had outgrown comfortable dialog height.
            int xL = Mx, xR = Mx + Gw + Gap;
            int ixL = xL + 14, ixR = xR + 14;
            int yL = Mx, yR = Mx, cy;

            // --- Trigger (left) --------------------------------------------
            var gTrig = new GroupBox();
            gTrig.Text = " Trigger ";
            gTrig.Location = new Point(xL, yL);
            Controls.Add(gTrig);
            cy = yL + CapH;
            Plain("Mode:", ixL, cy + 4, 66);
            modeDDL.Items.Add("Toggle  -  press once to start, again to stop");
            modeDDL.Items.Add("Hold  -  runs only while the key is held");
            modeDDL.SetBounds(xL + 88, cy, 290, 26);
            modeDDL.Text = modeDDL.Items[cfg.Mode == "Hold" ? 1 : 0];
            Controls.Add(modeDDL);
            modeDDL.BringToFront();
            cy += 26 + 6;
            lockChk.Text = "Keep running while locked";
            lockChk.SetBounds(ixL, cy, Iw, 22);
            lockChk.Checked = cfg.KeepWhileLocked;
            Controls.Add(lockChk);
            lockChk.BringToFront();
            cy += 26;
            cy = Muted("Locking a running card normally stops it. With this on, the lock"
                + " only blocks the hotkey and the card keeps going.",
                ixL, cy, Iw).Bottom;
            gTrig.Size = new Size(Gw, cy + Pad - yL);
            yL = gTrig.Bottom + Gap;

            // --- Where it runs (left) --------------------------------------
            var gWin = new GroupBox();
            gWin.Text = " Where it runs ";
            gWin.Location = new Point(xL, yL);
            Controls.Add(gWin);
            cy = yL + CapH;
            Plain("Window:", ixL, cy + 4, 66);
            winValue = cfg.WinTitle.Trim();
            winDDL.SetBounds(xL + 88, cy, 206, 26);
            winDDL.Text = winValue.Length == 0 ? Anywhere : cfg.GateName();
            winDDL.Opening += delegate { PopulateWindows(); };
            winDDL.Picked += delegate(string display)
            {
                int i = winDDL.Items.IndexOf(display);
                if (i >= 0 && i < winValues.Count) winValue = winValues[i];
            };
            Controls.Add(winDDL);
            winDDL.BringToFront();

            // Point, don't browse: click the button, then click the window
            ChipButton pickBtn = IconBtn("target", "Pick", xL + 300, cy - 1, 78, 27);
            pickBtn.BringToFront();
            // A ToolTip is a component, not a child control - the form's
            // Dispose never reaches it unless it's wired up by hand
            var pickTip = new ToolTip();
            pickTip.SetToolTip(pickBtn, "Then click the window you want (right-click cancels)");
            Disposed += delegate { pickTip.Dispose(); };
            pickBtn.Click += delegate
            {
                // Step aside WITHOUT hiding: turning Visible off on a modal
                // dialog ends its ShowDialog loop. Off-screen keeps the modal
                // session alive while the user clicks the window they want.
                Point home = Location;
                Location = new Point(-4000, home.Y);
                CursorToast.Pop("Click the window to target  ·  right-click cancels");
                WindowPick.Once(this, delegate(IntPtr h)
                {
                    Location = home;
                    Activate();
                    if (h == IntPtr.Zero) return;
                    string exe = WindowMatcher.ExeOf(h);
                    if (exe.Length == 0) return;
                    winValue = "ahk_exe " + exe;
                    winDDL.Text = exe;
                });
            };
            cy += 26 + 6;
            focusChk.Text = "Bring that window up when the hotkey is pressed";
            // Full group width: at the edit's indent the tail of the sentence
            // was cut off
            focusChk.SetBounds(ixL, cy, Iw, 22);
            focusChk.Checked = cfg.FocusWindow;
            Controls.Add(focusChk);
            focusChk.BringToFront();
            cy += 26;

            // Keep-running: the card clicks the gate window in the background
            // instead of pausing when the user switches away.
            flickChk.Text = "Keep running when I switch away";
            flickChk.SetBounds(ixL, cy, Iw, 22);
            flickChk.Checked = cfg.FlickFocus;
            Controls.Add(flickChk);
            flickChk.BringToFront();
            cy += 26;
            cy = Muted("Anywhere runs it everywhere. With a window set, starting needs it"
                + " in front, and it pauses when you switch away - unless you keep it"
                + " running, which clicks it in the background.",
                ixL, cy, Iw).Bottom;
            gWin.Size = new Size(Gw, cy + Pad - yL);
            yL = gWin.Bottom + Gap;

            // --- Stop automatically (right) --------------------------------
            var gStop = new GroupBox();
            gStop.Text = " Stop automatically ";
            gStop.Location = new Point(xR, yR);
            Controls.Add(gStop);
            cy = yR + CapH;
            Plain("After:", ixR, cy + 4, 66);
            clicksEdit.SetBounds(xR + 88, cy, 56, 25);
            clicksEdit.Text = cfg.StopClicks.ToString();
            Controls.Add(clicksEdit); clicksEdit.BringToFront();
            Plain(unit, xR + 150, cy + 4, 62);
            Plain("or", xR + 226, cy + 4, 24);
            secsEdit.SetBounds(xR + 252, cy, 56, 25);
            secsEdit.Text = cfg.StopSeconds.ToString();
            Controls.Add(secsEdit); secsEdit.BringToFront();
            Plain("seconds", xR + 314, cy + 4, 64);
            cy += 25 + 6;
            cy = Muted("0 means no limit. With both set, whichever hits first.",
                       xR + 88, cy, 292).Bottom + 4;
            stopInputChk.Text = "Stop when I use the mouse or keyboard";
            stopInputChk.SetBounds(ixR, cy, Iw, 22);
            stopInputChk.Checked = cfg.StopOnInput;
            Controls.Add(stopInputChk);
            stopInputChk.BringToFront();
            cy += 26;
            cy = Muted("Nudging the pointer a few pixels is ignored; real movement,"
                + " a click, or a key stops it at once.", ixR, cy, Iw).Bottom;
            gStop.Size = new Size(Gw, cy + Pad - yR);
            yR = gStop.Bottom + Gap;

            // --- Each click (right) ----------------------------------------
            var gClick = new GroupBox();
            gClick.Text = " Each click ";
            gClick.Location = new Point(xR, yR);
            Controls.Add(gClick);
            cy = yR + CapH;
            Plain("Hold for", ixR, cy + 4, 64);
            holdEdit.SetBounds(xR + 82, cy, 46, 25);
            holdEdit.Text = cfg.HoldPercent.ToString();
            holdEdit.ReadOnly = isMacro;    // macros replay their own timings
            Controls.Add(holdEdit); holdEdit.BringToFront();
            Plain("% of the interval", xR + 134, cy + 4, 116);
            // Built by hand, not Muted(): that sizes to its text, and an empty
            // string would give a 4 px tall label the live hint can't show in
            holdHint = new Label();
            holdHint.ForeColor = Color.Gray;
            holdHint.SetBounds(xR + 256, cy + 4, 124, 20);
            Controls.Add(holdHint);
            holdHint.BringToFront();
            // Percent is the setting, but milliseconds is what a person
            // pictures - show both, live
            EventHandler refreshHint = delegate
            {
                int pct;
                if (!int.TryParse(holdEdit.Text, out pct)) pct = 0;
                pct = Math.Max(0, Math.Min(90, pct));
                holdHint.Text = pct == 0
                    ? "instant (default)"
                    : "= " + (Math.Max(1, cfg.Interval) * pct / 100) + " ms per click";
            };
            holdEdit.TextChanged += refreshHint;
            refreshHint(null, EventArgs.Empty);
            cy += 25 + 6;
            cy = Muted(isMacro
                ? "Macros replay the press lengths they recorded."
                : "How long the button stays down. As a share of the interval it"
                + " keeps its feel when you change the click rate.", ixR, cy, Iw).Bottom;
            gClick.Size = new Size(Gw, cy + Pad - yR);
            yR = gClick.Bottom + Gap;

            // --- Randomise (right) -----------------------------------------
            var gRand = new GroupBox();
            gRand.Text = " Randomise ";
            gRand.Location = new Point(xR, yR);
            Controls.Add(gRand);
            cy = yR + CapH;
            Plain(isMacro ? "Loop gap ±" : "Interval ±", ixR, cy + 4, 74);
            jitterEdit.SetBounds(xR + 92, cy, 50, 25);
            jitterEdit.Text = cfg.JitterMs.ToString();
            Controls.Add(jitterEdit); jitterEdit.BringToFront();
            Plain("ms", xR + 148, cy + 4, 26);
            Plain("Position ±", xR + 206, cy + 4, 76);
            posJitEdit.SetBounds(xR + 284, cy, 50, 25);
            posJitEdit.Text = cfg.PosJitter.ToString();
            posJitEdit.ReadOnly = isMacro;      // ReadOnly keeps theme colors; Enabled=false goes unreadable in dark
            Controls.Add(posJitEdit); posJitEdit.BringToFront();
            Plain("px", xR + 340, cy + 4, 26);
            cy += 25 + 6;
            cy = Muted(isMacro
                ? "0 = exact. Position isn't randomised for macros - they replay the positions they recorded."
                : "0 = exact timing and position. Position needs Fixed Position; it is"
                + " ignored when following the mouse.", ixR, cy, Iw).Bottom;
            gRand.Size = new Size(Gw, cy + Pad - yR);
            yR = gRand.Bottom + Gap;

            // --- Misc (left, under the groups) -----------------------------
            restoreChk.Text = isMacro
                ? "Put the cursor back afterwards (not used by macros)"
                : "Put the cursor back afterwards (fixed-position clicking only)";
            restoreChk.SetBounds(xL + 2, yL, Gw, 22);
            restoreChk.Checked = cfg.RestoreCursor;
            Gate(restoreChk, !isMacro);
            Controls.Add(restoreChk);
            yL += 26;

            // Macro-only: replay anchored to wherever the window is now rather
            // than to the screen. The recording carries the window's position
            // and size; a size mismatch warns on the card when playback starts.
            relChk.Text = isMacro
                ? "Follow the window: replay positions relative to where it is now"
                : "Follow the window (macros only - they carry window info)";
            relChk.SetBounds(xL + 2, yL, Gw, 22);
            relChk.Checked = cfg.MacroRelative;
            Gate(relChk, isMacro);
            Controls.Add(relChk);
            yL += 22 + Gap + 4;

            int y = Math.Max(yL, yR);
            int fullW = Gw * 2 + Gap;
            ChipButton ok = Btn("OK", Mx + fullW - 174, y, 82, 28);
            ChipButton cancel = Btn("Cancel", Mx + fullW - 82, y, 82, 28);
            // The margin that opened the dialog closes it
            ClientSize = new Size(fullW + Mx * 2, y + 28 + Mx);
            ok.Style = delegate { return Theme.GoChip; };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
            ok.Click += delegate { Apply(); };

            foreach (var g in new[] { gTrig, gWin, gStop, gClick, gRand }) g.SendToBack();
        }

        // The dropdown lists what is open right now, one entry per program -
        // built when it opens, so it is never stale
        void PopulateWindows()
        {
            winDDL.Items.Clear();
            winValues.Clear();
            winDDL.Items.Add(Anywhere);
            winValues.Add("");
            var seen = new List<string>();
            foreach (KeyValuePair<string, string> w in WindowMatcher.OpenWindows(IntPtr.Zero))
            {
                string exe = w.Value;
                if (exe.Length == 0) continue;
                bool dup = false;
                foreach (string sx in seen)
                    if (string.Equals(sx, exe, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (dup) continue;
                seen.Add(exe);
                winDDL.Items.Add(exe);
                winValues.Add("ahk_exe " + exe);
            }
            // a saved gate that isn't running right now (or a title match)
            // stays selectable rather than silently vanishing
            if (winValue.Length > 0 && !winValues.Contains(winValue))
            {
                winDDL.Items.Insert(1, o.GateName());
                winValues.Insert(1, winValue);
            }
        }

        // A "disabled" checkbox that stays readable: it keeps its colors but
        // stops toggling. Enabled=false hands painting to the system, which is
        // unreadable on the dark background.
        static void Gate(CheckBox c, bool usable)
        {
            c.AutoCheck = usable;
            c.Tag = usable ? null : "gated";
            c.Cursor = usable ? Cursors.Default : Cursors.No;
        }

        void Apply()
        {
            string mode = modeDDL.Text.StartsWith("Hold") ? "Hold" : "Toggle";
            ModeChanged = o.Mode != mode;
            o.Mode = mode;
            o.WinTitle = winValue;
            int v;
            o.StopClicks  = int.TryParse(clicksEdit.Text, out v) ? Math.Max(0, v) : 0;
            o.StopSeconds = int.TryParse(secsEdit.Text, out v) ? Math.Max(0, v) : 0;
            o.JitterMs    = int.TryParse(jitterEdit.Text, out v) ? Math.Max(0, v) : 0;
            o.PosJitter   = int.TryParse(posJitEdit.Text, out v) ? Math.Max(0, v) : 0;
            o.HoldPercent = int.TryParse(holdEdit.Text, out v) ? Math.Max(0, Math.Min(90, v)) : 0;
            o.KeepWhileLocked = lockChk.Checked;
            o.StopOnInput = stopInputChk.Checked;
            o.RestoreCursor = restoreChk.Checked;
            o.FocusWindow = focusChk.Checked;
            o.FlickFocus = flickChk.Checked;
            if (relChk.AutoCheck) o.MacroRelative = relChk.Checked;
        }
    }

    // --- "Pick..." : the open windows, choose one as the gate --------------
    sealed class PickWindowDialog : AppDialog
    {
        readonly ListBox list = new ListBox();
        readonly RadioButton byExe = new RadioButton();
        readonly RadioButton byTitle = new RadioButton();
        readonly List<KeyValuePair<string, string>> wins;

        public string Chosen = "";

        public PickWindowDialog() : base("Pick a window")
        {
            ClientSize = new Size(430, 342);
            Plain("Currently open windows:", 14, 10, 300);
            list.SetBounds(14, 34, 402, 220);
            list.IntegralHeight = false;
            Controls.Add(list);

            // By program is the durable choice - titles change with the
            // document, the exe stays put - so it is the default.
            byExe.Text = "Match the program (any window it owns)";
            byExe.SetBounds(14, 262, 402, 22);
            byExe.Checked = true;
            byTitle.Text = "Match this exact title";
            byTitle.SetBounds(14, 284, 402, 22);
            Controls.Add(byExe);
            Controls.Add(byTitle);

            ChipButton ok = Btn("OK", 244, 310, 82, 26);
            ChipButton cancel = Btn("Cancel", 334, 310, 82, 26);
            ok.Style = delegate { return Theme.GoChip; };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;

            IntPtr own = IntPtr.Zero;
            try { own = Application.OpenForms.Count > 0 ? Application.OpenForms[0].Handle : IntPtr.Zero; }
            catch { }
            wins = WindowMatcher.OpenWindows(own);
            foreach (var w in wins)
                list.Items.Add(w.Key + "   [" + w.Value + "]");
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            list.DoubleClick += delegate { DialogResult = DialogResult.OK; Close(); };
            ok.Click += delegate { Take(); };
            FormClosing += delegate
            {
                if (DialogResult == DialogResult.OK) Take();
            };
        }

        void Take()
        {
            int i = list.SelectedIndex;
            if (i < 0 || i >= wins.Count) { Chosen = ""; return; }
            Chosen = byExe.Checked ? "ahk_exe " + wins[i].Value : wins[i].Key;
        }
    }
}
