// ===========================================================================
//  MainForm - the window
// ---------------------------------------------------------------------------
//  The cards live in a CardSurface - one drawn control, not a panel of child
//  windows - so resizing and scrolling are a repaint, and this form
//  schedules none of it. See CardSurface.cs for why.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;

namespace Polyclicker
{
    sealed class MainForm : Form
    {
        AppConfig cfg;
        readonly ToolTip tips = new ToolTip();

        CardSurface surface;
        Label titleLabel;
        PictureBox titleIcon;
        ChipButton settingsBtn;
        Panel strip;
        ChipButton addBtn, stopAllBtn, saveProfBtn, delProfBtn;
        ChipButton killBtn;             // a mode: stays red while hotkeys are off
        DropButton profileCombo;
        NotifyIcon tray;
        ContextMenuStrip trayMenu;

        bool killActive;            // every hotkey suspended
        // Last "pressed but gated out" notice per card - a held key mustn't
        // spam the status line
        readonly Dictionary<int, DateTime> gateMissAt = new Dictionary<int, DateTime>();
        // Gate-activity cache, kept fresh by the 50 ms gate timer. OnHotkey
        // runs inside the keyboard hook callback, and answering "is the gate
        // window in front?" there cost an OpenProcess + image-name query per
        // gated card per keystroke for ahk_exe gates - heavy enough to court
        // the hook timeout that silently kills every hotkey. The hook reads
        // this instead; 50 ms of staleness is far below how fast a person
        // can alt-tab and press.
        readonly Dictionary<int, bool> gateActive = new Dictionary<int, bool>();
        IntPtr lastFg;              // gate checks re-run only when it changes
        readonly Dictionary<int, int> flickFindAt = new Dictionary<int, int>();
        Icon trayIconOn, trayIconOff;
        int armedIndex = -1;        // card readied for recording, -1 = none
        readonly Timer gateTimer = new Timer();
        readonly Timer statusTimer = new Timer();
        bool suppressSave;

        public MainForm()
        {
            cfg = AppConfig.Load();
            Theme.Dark = Theme.ResolveDark(cfg.ThemeMode);

            Text = "Polyclicker";
            DoubleBuffered = true;
            Font = Theme.UIFont;
            MinimumSize = new Size(CardSurface.MinWidth + 60, 300);
            StartPosition = FormStartPosition.Manual;
            BuildChrome();
            ApplyTheme();
            RestorePlacement();

            Engine.Startup();
            Engine.Stopped += OnEngineStopped;

            Hotkeys.Key += OnHotkey;
            Hotkeys.Install();

            foreach (SlotConfig s in cfg.Slots) surface.Add(s);
            // A first run has nothing saved, and an empty window doesn't show
            // what this is or where to start. The default card goes into
            // cfg.Slots too: created outside the list, everything about it -
            // the name the new user types first of all - saved as nothing.
            if (surface.Count == 0)
            {
                var first = new SlotConfig();
                cfg.Slots.Add(first);
                surface.Add(first);
            }
            MacroFile.MigrateHeaders(AppConfig.MacroDir);
            RefreshMacroLists();
            RefreshProfileList(cfg.CurrentProfile);
            Renumber();

            gateTimer.Interval = 50;
            gateTimer.Tick += delegate { UpdateGates(); };
            gateTimer.Start();
            // Give the pointer room to breathe before a strip-button tip shows,
            // and don't let one overstay once it has
            tips.InitialDelay = 600;
            tips.ReshowDelay = 600;
            tips.AutoPopDelay = 5000;

            statusTimer.Interval = 200;
            int watchdogTicks = 0;
            statusTimer.Tick += delegate
            {
                RefreshRunningStatuses();
                watchdogTicks++;
                if (watchdogTicks % 5 == 0) RefreshGatePresence();
                // Windows removes a low-level hook that ever answers slowly -
                // silently, permanently. Re-registering every few seconds makes
                // that self-healing instead of "hotkeys died until restart".
                if (watchdogTicks % 15 == 0) Hotkeys.Reinstall();
                // A hook also never SEES keys while an elevated window is in
                // front (Windows forbids it). That reads as "my hotkeys only
                // die in this one game" - so say what's happening, once per app
                if (watchdogTicks % 10 == 0) WarnIfForegroundElevated();
                // One line every five minutes. After a hang or a hard kill,
                // the last timestamp bounds when the process stopped.
                if (watchdogTicks % 1500 == 0)
                    Log.Line("alive - " + Engine.RunningCount() + " running");
            };
            statusTimer.Start();

            // The wheel must never edit a card on its way past - see WheelFilter
            Application.AddMessageFilter(new WheelFilter(surface));
            Application.AddMessageFilter(new WordDeleteFilter());

            // The crash path's flush: the 400 ms save debounce never fires in
            // a dying process. cfg only, not SavePlacement - reading control
            // properties from a foreign thread is its own crash.
            Program.SaveOnCrash = delegate { cfg.Save(); };

            FormClosing += delegate { Log.Line("clean exit"); SavePlacement(); cfg.Save(); };
            FormClosed += delegate { Hotkeys.Uninstall(); Engine.Shutdown(); tray.Visible = false; };
        }

        // --- chrome ---------------------------------------------------------

        void BuildChrome()
        {
            // A Label draws its Image under its text, so the mouse mark is a
            // proper PictureBox beside the title instead
            titleIcon = new PictureBox();
            titleIcon.Size = new Size(28, 28);
            titleIcon.Location = new Point(12, 9);
            titleIcon.SizeMode = PictureBoxSizeMode.Zoom;

            titleLabel = new Label();
            titleLabel.Text = "Polyclicker";
            titleLabel.Font = Theme.TitleFont;
            titleLabel.AutoSize = true;
            titleLabel.Location = new Point(46, 9);

            // Just a quiet gray chip, top right - rounded like every other
            // button, no text; the hotkey hints live in Settings, where they
            // belong
            settingsBtn = new ChipButton();
            settingsBtn.Kind = "gear";
            settingsBtn.Style = delegate { return Theme.NeutralChip; };
            settingsBtn.Size = new Size(28, 28);
            settingsBtn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            settingsBtn.Click += delegate { ShowSettings(); };
            tips.SetToolTip(settingsBtn, "Settings");

            // The whole card list is one drawn control; every event arrives
            // with the card's index
            surface = new CardSurface(tips);
            surface.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                           | AnchorStyles.Left | AnchorStyles.Right;
            surface.Changed += delegate(int i)
            {
                // Locking a running card stops it - a lock should mean OFF,
                // not "running but unreachable" - unless the card opted into
                // keep-running-while-locked (then the lock only shields the
                // hotkey from accidental presses).
                SlotConfig sc = cfg.Slots[i];
                if (sc.HotkeyOff && !sc.KeepWhileLocked && Engine.IsRunning(i))
                    Engine.Stop(i);
                gateActive.Remove(i);   // the edit may have been the gate
                SaveSoon();
                surface.RefreshStatus(i, Engine.IsRunning(i), Engine.ActualCps(i));
            };
            surface.RemoveClicked += RemoveCard;
            surface.DuplicateClicked += DuplicateCard;
            surface.OptionsClicked += ShowAdvanced;
            surface.RecordClicked += ToggleArm;
            surface.PickPosClicked += PickPosition;
            surface.PreviewClicked += delegate(int i)
            {
                SlotConfig s = surface.CfgAt(i);
                SpotDot.Pop(s.X, s.Y);
            };
            surface.RenameMacroClicked += RenameMacro;
            surface.DeleteMacroClicked += DeleteMacro;
            surface.ColorClicked += ShowColorMenu;
            // Reordering already moved the surface's card; keep the config,
            // the engine's running slots, and the bookkeeping indices in step
            surface.Reordered += delegate(int from, int to)
            {
                SlotConfig moved = cfg.Slots[from];
                cfg.Slots.RemoveAt(from);
                cfg.Slots.Insert(to, moved);
                Engine.MoveSlot(from, to);
                armedIndex = ShiftIndex(armedIndex, from, to);
                recordIndex = ShiftIndex(recordIndex, from, to);
                Renumber();
                SaveSoon();
            };

            strip = new Panel();
            strip.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            strip.Height = 52;

            addBtn = StripButton("plus", "Add clicker", delegate { AddCard(new SlotConfig(), true); });
            stopAllBtn = StripButton("stop", "Stop all", delegate { StopEverything(); });

            // The kill switch is a MODE, not an action: a labeled chip that
            // wears the remove-red while every hotkey is suspended
            killBtn = new ChipButton();
            killBtn.Kind = "toggle-on";
            killBtn.Style = delegate { return killActive ? Theme.RemoveChip : Theme.NeutralChip; };
            killBtn.Size = new Size(38, 32);
            tips.SetToolTip(killBtn, "Suspend every hotkey at once (" + HotkeyParser.Parse(cfg.KillSwitchKey) + ")");
            killBtn.Click += delegate { ToggleKill(); };

            // A drawn dropdown, popup included - the native combo's list
            // chrome can't be themed. Picked fires only on a real user choice.
            profileCombo = new DropButton();
            profileCombo.Placeholder = "Profile";
            profileCombo.Picked += delegate(string name) { OnProfileChosen(name); };
            tips.SetToolTip(profileCombo, "Profile");

            saveProfBtn = StripButton("save", "Save profile", delegate { SaveProfileAs(); });
            delProfBtn = StripButton("trash", "Delete profile", delegate { DeleteProfile(); });
            saveProfBtn.Anchor = delProfBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            strip.Controls.AddRange(new Control[] { addBtn, stopAllBtn, killBtn,
                                                    profileCombo,
                                                    saveProfBtn, delProfBtn });
            Controls.AddRange(new Control[] { titleIcon, titleLabel, settingsBtn, surface, strip });

            // The one icon, embedded in the exe, used everywhere: title bar,
            // taskbar, and tray. The tray gets a 16px cut explicitly - handing
            // it the multi-size icon lets the shell downscale a big frame,
            // which is what a blurry tray icon looks like.
            Icon appIcon = LoadAppIcon();
            if (appIcon != null)
            {
                Icon = appIcon;
                // the same logo, in-window, next to the name
                using (var cut = new Icon(appIcon, 32, 32))
                    titleIcon.Image = cut.ToBitmap();
            }

            // Two tray icons: the app icon, and the same icon greyed with a
            // red slash for "hotkeys suspended" - the tray answers at a glance
            trayIconOn = appIcon != null ? new Icon(appIcon, 16, 16) : SystemIcons.Application;
            trayIconOff = MakeOffIcon(trayIconOn);

            tray = new NotifyIcon();
            tray.Icon = trayIconOn;
            tray.Text = "Polyclicker";
            tray.Visible = true;
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("Show Window", null, delegate { Show(); WindowState = FormWindowState.Normal; });
            trayMenu.Items.Add("Stop All", null, delegate { StopEverything(); });
            trayMenu.Items.Add("Toggle all hotkeys", null, delegate { ToggleKill(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Exit", null, delegate { Close(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; };

        }

        // Rounded drawn chips, same as every button on the cards - the stock
        // themed Button only rounds its corners in light mode
        ChipButton StripButton(string kind, string tip, EventHandler onClick)
        {
            var b = new ChipButton();
            b.Kind = kind;
            b.Style = delegate { return Theme.NeutralChip; };
            b.Size = new Size(38, 32);
            b.Click += onClick;
            tips.SetToolTip(b, tip);
            return b;
        }

        // --- settings and advanced options ----------------------------------

        void ShowSettings()
        {
            string recShown = cfg.RecordKey.Trim().Length > 0
                            ? HotkeyParser.Parse(cfg.RecordKey).ToString() : "the record hotkey";
            // The theme choice applies the moment it's clicked - live, not on OK
            using (var d = new SettingsDialog(cfg, recShown, delegate(string mode)
            {
                cfg.ThemeMode = mode;
                Theme.Dark = Theme.ResolveDark(mode);
                ApplyTheme();
                SaveSoon();
            }))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                cfg.StopAllKey = d.StopAllKey;
                cfg.KillSwitchKey = d.KillKey;
                cfg.RecordKey = d.RecordKey;
                Renumber();           // the record key is quoted in status lines
                SaveSoon();
            }
        }

        void ShowAdvanced(int i)
        {
            using (var d = new AdvancedDialog(surface.CfgAt(i), Label(i)))
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                // Toggle and Hold act on different key edges, so a running
                // clicker can't be carried across the switch - stop it
                // deliberately rather than leaving it under the old behaviour
                if (d.ModeChanged && Engine.IsRunning(i)) Engine.Stop(i);
                surface.Reflow();          // the gate indicator lives on the card
                Renumber();
                SaveSoon();
            }
        }

        // --- recording -------------------------------------------------------

        int recordIndex = -1;         // card the running take lands in

        void ToggleRecording()
        {
            if (Recorder.Recording) StopRecordingFlow();
            else BeginRecording();
        }

        void BeginRecording()
        {
            if (armedIndex < 0 || armedIndex >= surface.Count) return;
            recordIndex = armedIndex;

            string baseName = MacroFile.Sanitize(surface.CfgAt(recordIndex).Name);
            if (baseName.Length == 0) baseName = "Clicker " + (recordIndex + 1);
            try { Directory.CreateDirectory(AppConfig.MacroDir); } catch { }
            string path = MacroFile.UniquePath(AppConfig.MacroDir, baseName);

            // The record key itself and Escape only ever mean start/stop, so
            // they never enter a take. The chord's modifiers DO record - a
            // take full of Ctrl+clicks must keep its Ctrls - and the recorder
            // trims just the presses that bracket the take.
            var ignore = new List<int>();
            ignore.Add(0x1B);                          // Escape
            var chord = new List<int>();
            HotkeyCombo rec = HotkeyParser.Parse(cfg.RecordKey);
            if (rec.IsSet)
            {
                ignore.Add(rec.Vk);
                // The hook reports physical keys, so both the neutral and the
                // left/right-specific codes have to be listed
                if (rec.Ctrl) { chord.Add(0x11); chord.Add(0xA2); chord.Add(0xA3); }
                if (rec.Alt) { chord.Add(0x12); chord.Add(0xA4); chord.Add(0xA5); }
                if (rec.Shift) { chord.Add(0x10); chord.Add(0xA0); chord.Add(0xA1); }
                if (rec.Win) { chord.Add(0x5B); chord.Add(0x5C); }
            }

            Recorder.Start(path, ignore, chord);
            surface.SetRecording(recordIndex, true, 0);
        }

        void StopRecordingFlow()
        {
            int events = Recorder.Stop(false);
            int i = recordIndex;
            recordIndex = -1;
            if (i < 0 || i >= surface.Count) return;
            surface.SetRecording(i, false, 0);

            if (events < 1)
            {
                surface.Notice(i, "Nothing was recorded.");
                return;
            }

            // Land the take in the armed card ready to play - switching the
            // input type here rather than at arm time means nothing changes
            // until a recording actually exists
            string fname = Path.GetFileName(Recorder.LastPath);
            SlotConfig s = surface.CfgAt(i);
            surface.CancelEdit();          // a live editor would write into the old shape
            s.Input = "Macro";
            s.Macro = fname;
            RefreshMacroLists();
            if (armedIndex >= 0 && armedIndex < surface.Count) surface.SetArmed(armedIndex, false);
            armedIndex = -1;
            surface.Reflow();
            surface.Notice(i, "Saved " + fname + "  (" + events + " events)");
            SaveSoon();
        }

        // --- macro management ------------------------------------------------

        void RenameMacro(int idx)
        {
            string cur = surface.CfgAt(idx).Macro.Trim();
            if (cur.Length == 0) { surface.Notice(idx, "No macro selected to rename."); return; }
            string oldBase = cur.EndsWith(".macro") ? cur.Substring(0, cur.Length - 6) : cur;

            using (var p = new TextPrompt("Rename macro", "New name for this macro:", oldBase))
            {
                if (p.ShowDialog(this) != DialogResult.OK) return;
                string newBase = MacroFile.Sanitize(p.Value);
                if (newBase.Length == 0 || newBase == oldBase) return;

                string src = Path.Combine(AppConfig.MacroDir, cur);
                string dst = Path.Combine(AppConfig.MacroDir, newBase + ".macro");
                if (File.Exists(dst))
                {
                    MessageBox.Show(this, "A macro called '" + newBase + "' already exists.",
                                    "Rename macro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                try { File.Move(src, dst); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't rename it:\n\n" + ex.Message,
                                    "Rename macro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Any card pointing at the old name has to follow it, or it
                // would quietly lose its selection when the list is rebuilt
                foreach (SlotConfig s in cfg.Slots)
                    if (s.Macro.Trim() == cur) s.Macro = newBase + ".macro";
                RefreshMacroLists();
                surface.Reflow();
                SaveSoon();
            }
        }

        void DeleteMacro(int idx)
        {
            string cur = surface.CfgAt(idx).Macro.Trim();
            if (cur.Length == 0) { surface.Notice(idx, "No macro selected to delete."); return; }

            // Name every card that would lose its recording, not just this one
            int users = 0;
            foreach (SlotConfig s in cfg.Slots) if (s.Macro.Trim() == cur) users++;
            string also = users > 1
                ? "\n\nIt is used by " + users + " auto-clickers, which will have no macro to play."
                : "";
            if (MessageBox.Show(this,
                    "Delete '" + cur + "'?" + also + "\n\nThe file is removed from the Macros folder.",
                    "Delete macro", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;

            try { File.Delete(Path.Combine(AppConfig.MacroDir, cur)); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't delete it:\n\n" + ex.Message,
                                "Delete macro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            foreach (SlotConfig s in cfg.Slots)
                if (s.Macro.Trim() == cur) s.Macro = "";
            RefreshMacroLists();
            surface.Reflow();
            SaveSoon();
        }

        // --- profiles --------------------------------------------------------

        bool SaveProfileAs()
        {
            string prefill = cfg.CurrentProfile.Length > 0 ? cfg.CurrentProfile : profileCombo.Text;
            if (prefill == NewProfileItem) prefill = "";
            using (var p = new TextPrompt("Save profile", "Save these auto-clickers as a profile:", prefill))
            {
                if (p.ShowDialog(this) != DialogResult.OK) return false;
                string name = MacroFile.Sanitize(p.Value);
                if (name.Length == 0) return false;

                string path = Path.Combine(AppConfig.ProfileDir, name + ".ini");
                if (File.Exists(path) && MessageBox.Show(this,
                        "'" + name + "' already exists. Overwrite it?", "Save profile",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return false;
                try
                {
                    Directory.CreateDirectory(AppConfig.ProfileDir);
                    if (File.Exists(path)) File.Delete(path);
                    cfg.WriteSlotsTo(path);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't save that profile:\n\n" + ex.Message,
                                    "Save profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                cfg.CurrentProfile = name;
                RefreshProfileList(name);
                SaveSoon();
                return true;
            }
        }

        // --- unsaved work -----------------------------------------------------

        // Is the working set different from the profile it came from? Answered
        // against the file on disk rather than a tracked flag, so it stays
        // right across restarts and however the cards were edited.
        bool HasUnsavedChanges()
        {
            string mine = AppConfig.Fingerprint(cfg.Slots);
            if (cfg.CurrentProfile.Length > 0)
            {
                string path = Path.Combine(AppConfig.ProfileDir, cfg.CurrentProfile + ".ini");
                if (!File.Exists(path)) return true;       // can't prove it's saved
                try { return mine != AppConfig.Fingerprint(AppConfig.ReadSlotsFrom(path)); }
                catch { return true; }
            }
            // No profile at all: only worth mentioning if there is something to
            // lose. The single blank card a fresh start opens with is not.
            var pristine = new List<SlotConfig>();
            pristine.Add(new SlotConfig());
            return mine != AppConfig.Fingerprint(pristine);
        }

        // Returns true to go ahead with whatever would discard the working set.
        // Silent when there is nothing at stake - the whole point is not making
        // someone work out for themselves whether they are about to lose work.
        bool OkToDiscard(string doingWhat)
        {
            if (!HasUnsavedChanges()) return true;
            string subject = cfg.CurrentProfile.Length > 0
                ? "'" + cfg.CurrentProfile + "' has unsaved changes."
                : "These auto-clickers aren't saved to a profile.";
            DialogResult r = MessageBox.Show(this,
                subject + "\n\nSave them before " + doingWhat + "?"
                + "\n\nYes - save, then continue"
                + "\nNo - continue anyway, discarding them"
                + "\nCancel - stay here",
                "Unsaved changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.No) return true;
            return SaveCurrent();          // Yes: only continue if it saved
        }

        // Write straight back to the profile in use; ask for a name when there
        // isn't one yet.
        bool SaveCurrent()
        {
            if (cfg.CurrentProfile.Length == 0) return SaveProfileAs();
            try
            {
                Directory.CreateDirectory(AppConfig.ProfileDir);
                cfg.WriteSlotsTo(Path.Combine(AppConfig.ProfileDir, cfg.CurrentProfile + ".ini"));
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't save '" + cfg.CurrentProfile + "':\n\n" + ex.Message,
                                "Save profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // Put the dropdown back on the profile actually in use - after a
        // cancelled action it must not sit on the entry that was declined.
        void RestoreProfileSelection()
        {
            profileCombo.Text = cfg.CurrentProfile.Length > 0
                             && profileCombo.Items.Contains(cfg.CurrentProfile)
                              ? cfg.CurrentProfile : "";
        }

        void DeleteProfile()
        {
            string name = profileCombo.Text;
            if (name.Length == 0 || name == NewProfileItem) return;
            if (MessageBox.Show(this,
                    "Delete the profile '" + name + "'?\n\nThe auto-clickers currently"
                    + " loaded are not affected.",
                    "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            try { File.Delete(Path.Combine(AppConfig.ProfileDir, name + ".ini")); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't delete it:\n\n" + ex.Message,
                                "Delete profile", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (cfg.CurrentProfile == name) { cfg.CurrentProfile = ""; SaveSoon(); }
            RefreshProfileList("");
        }

        // Load a profile file as the working set: the ordinary slot reader
        // pointed at a different file, so the working file and profiles can't
        // drift apart.
        void ApplyProfile(string path)
        {
            Engine.StopAll();
            if (Recorder.Recording) Recorder.Stop(true);
            armedIndex = -1;
            recordIndex = -1;

            suppressSave = true;
            try
            {
                surface.Clear();
                cfg.Slots.Clear();
                foreach (SlotConfig s in AppConfig.ReadSlotsFrom(path))
                {
                    cfg.Slots.Add(s);
                    surface.Add(s);
                }
                if (surface.Count == 0) AddCard(new SlotConfig(), true);
            }
            finally { suppressSave = false; }
            RefreshMacroLists();
            Renumber();
            SaveSoon();               // the loaded set is now the working one
            // The switch is the riskiest moment for the keyboard hook (see the
            // watchdog) - re-register right away rather than waiting for it
            Hotkeys.Reinstall();
        }

        // --- theme -----------------------------------------------------------

        void ApplyTheme()
        {
            BackColor = Theme.FormBack;
            titleLabel.ForeColor = Theme.FormText;
            titleLabel.BackColor = Theme.FormBack;

            strip.BackColor = Theme.FormBack;
            foreach (ChipButton b in new ChipButton[] { addBtn, stopAllBtn, saveProfBtn, delProfBtn, killBtn })
            {
                b.BackColor = Theme.FormBack;
                b.Invalidate();      // chips read Theme live when painting
            }
            // The chrome pictures redraw in theme colors too
            titleIcon.BackColor = Theme.FormBack;
            settingsBtn.Invalidate();     // reads Theme live when painting
            profileCombo.Invalidate();
            Theme.StyleMenu(trayMenu);
            surface.ThemeChanged();
            Theme.DarkTitleBar(this);
            if (IsHandleCreated) SetClassBrush();
            Invalidate(true);
        }

        // --- card colors -----------------------------------------------------

        void ShowColorMenu(int idx)
        {
            var popup = new ColorPopup(surface.CfgAt(idx).Color, delegate(string chosen)
            {
                if (idx < surface.Count)
                {
                    surface.CfgAt(idx).Color = chosen;
                    surface.Reflow();
                    SaveSoon();
                }
            });
            popup.Show(this);
        }

        static Icon LoadAppIcon()
        {
            try
            {
                var s = typeof(MainForm).Assembly.GetManifestResourceStream("Polyclicker.app.ico");
                return s != null ? new Icon(s) : null;
            }
            catch { return null; }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (surface == null) return;
            int top = 46;
            surface.SetBounds(0, top, ClientSize.Width, Math.Max(60, ClientSize.Height - top - strip.Height));
            strip.SetBounds(0, ClientSize.Height - strip.Height, ClientSize.Width, strip.Height);
            settingsBtn.Location = new Point(ClientSize.Width - settingsBtn.Width - 12, 8);

            // One rhythm across the strip: 12 px edges, 8 px gaps, everything
            // 32 tall and vertically centered
            int y = (strip.Height - 32) / 2;
            // The three action chips run left to right on a 46 px pitch
            // (38 wide, 8 apart); the profile section starts after a wider
            // gap, which is what separates the two groups.
            // GroupGap and the combo gap are measured to the glyphs, not the
            // label box: an AutoSize Label carries about 3 px of lead-in and 6 of
            // trail, so the raw numbers land ~9 px wider than they read.
            const int Edge = 12, ChipPitch = 46, GroupGap = 8;
            addBtn.Location = new Point(Edge, y);
            stopAllBtn.Location = new Point(Edge + ChipPitch, y);
            killBtn.Location = new Point(Edge + ChipPitch * 2, y);
            delProfBtn.Location = new Point(strip.Width - 50, y);
            saveProfBtn.Location = new Point(strip.Width - 96, y);
            int comboX = Edge + ChipPitch * 2 + killBtn.Width + GroupGap;
            // Same 32 as the chips beside it: at 26 it read as a thin slot
            // wedged between full-height buttons.
            profileCombo.SetBounds(comboX, y, Math.Max(80, strip.Width - 104 - comboX), 32);
            // The cards repaint themselves; there is nothing to schedule here
        }

        // --- cards ----------------------------------------------------------

        void AddCard(SlotConfig s, bool isNew)
        {
            if (isNew) cfg.Slots.Add(s);
            surface.Add(s);
            Renumber();
            if (isNew) SaveSoon();
        }

        void RemoveCard(int i)
        {
            if (MessageBox.Show(this,
                    "Remove " + Label(i) + "?\n\nIts settings are discarded. Any recorded"
                    + " macro it used stays in the Macros folder.",
                    "Remove auto-clicker", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes)
                return;
            Engine.Stop(i);
            Engine.ShiftForRemoval(i);      // running slots above follow their cards down
            if (armedIndex == i) armedIndex = -1;
            else if (armedIndex > i) armedIndex--;
            if (recordIndex == i) recordIndex = -1;
            else if (recordIndex > i) recordIndex--;
            cfg.Slots.RemoveAt(i);
            surface.RemoveAt(i);
            Renumber();
            SaveSoon();
        }

        void DuplicateCard(int i)
        {
            SlotConfig copy = cfg.Slots[i].Clone();
            // Named apart, or two cards answer to the same label in every
            // message - and a recording from either would overwrite the other's
            copy.Name = (copy.Name.Length > 0 ? copy.Name : "Auto-Clicker #" + (i + 1)) + " copy";
            cfg.Slots.Insert(i + 1, copy);
            surface.Insert(i + 1, copy);
            Engine.ShiftForInsert(i + 1);   // running slots below follow their cards up
            if (armedIndex > i) armedIndex++;
            if (recordIndex > i) recordIndex++;
            Renumber();
            SaveSoon();
        }

        void Renumber()
        {
            // Runs on every add, remove, reorder and profile switch - the
            // index-keyed gate caches would otherwise answer for the wrong
            // card until the next foreground change
            gateActive.Clear();
            flickFindAt.Clear();
            lastFg = IntPtr.Zero;
            for (int i = 0; i < surface.Count; i++)
                surface.RefreshStatus(i, Engine.IsRunning(i), Engine.ActualCps(i));
            surface.Invalidate();       // placeholder names carry the number
        }

        // Where a tracked index lands after the card at from moved to to
        static int ShiftIndex(int idx, int from, int to)
        {
            if (idx < 0) return idx;
            if (idx == from) return to;
            if (from < to && idx > from && idx <= to) return idx - 1;
            if (to < from && idx >= to && idx < from) return idx + 1;
            return idx;
        }

        string Label(int i)
        {
            string n = cfg.Slots[i].Name;
            return n.Length > 0 ? n : "Auto-Clicker #" + (i + 1);
        }

        // --- running --------------------------------------------------------

        void StopEverything()
        {
            Engine.StopAll();
            Renumber();
        }

        void ToggleKill()
        {
            killActive = !killActive;
            if (killActive) Engine.StopAll();
            killBtn.Kind = killActive ? "toggle-off" : "toggle-on";
            killBtn.Invalidate();
            tips.SetToolTip(killBtn, (killActive ? "Hotkeys suspended - click to resume ("
                                                : "Suspend every hotkey at once (")
                                    + HotkeyParser.Parse(cfg.KillSwitchKey) + ")");
            surface.AllHotkeysOff = killActive;
            tray.Icon = killActive ? trayIconOff : trayIconOn;
            tray.Text = killActive ? "Polyclicker - hotkeys OFF" : "Polyclicker";
            // Toggled from a game via the global hotkey: the answer has to
            // arrive where the user is looking, which is wherever the mouse is
            if (GetForegroundWindow() != Handle)
                CursorToast.Pop(killActive ? "⏸  Hotkeys OFF" : "▶  Hotkeys on");
            Renumber();
        }

        // The app icon, greyed out and struck through: suspended, at a glance
        static Icon MakeOffIcon(Icon baseIcon)
        {
            try
            {
                using (var bmp = new Bitmap(16, 16))
                using (var g = Graphics.FromImage(bmp))
                {
                    var gray = new System.Drawing.Imaging.ColorMatrix(new float[][]
                    {
                        new float[] { 0.30f, 0.30f, 0.30f, 0, 0 },
                        new float[] { 0.59f, 0.59f, 0.59f, 0, 0 },
                        new float[] { 0.11f, 0.11f, 0.11f, 0, 0 },
                        new float[] { 0, 0, 0, 1, 0 },
                        new float[] { 0, 0, 0, 0, 1 },
                    });
                    using (var attrs = new System.Drawing.Imaging.ImageAttributes())
                    using (var src = baseIcon.ToBitmap())
                    {
                        attrs.SetColorMatrix(gray);
                        g.DrawImage(src, new Rectangle(0, 0, 16, 16),
                                    0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                    }
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var p = new Pen(Color.FromArgb(220, 200, 40, 30), 2.4f))
                        g.DrawLine(p, 2, 13, 13, 2);
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { return baseIcon; }
        }

        void OnEngineStopped(int id)
        {
            Log.Line("slot " + (id + 1) + " stopped");
            if (IsDisposed) return;
            try { BeginInvoke((MethodInvoker)delegate { Renumber(); }); } catch { }
        }

        void RefreshRunningStatuses()
        {
            for (int i = 0; i < surface.Count; i++)
            {
                if (Engine.IsRunning(i))
                    surface.RefreshStatus(i, true, Engine.ActualCps(i));
                else if (surface.NoticeExpired(i))
                    surface.RefreshStatus(i, false, 0);
            }
            if (Recorder.Recording && recordIndex >= 0 && recordIndex < surface.Count)
                surface.SetRecording(recordIndex, true, Recorder.EventCount);
        }

        // A gate naming a window nobody has open can never fire; the card's
        // status says so instead of sitting on "Ready" forever. One window
        // scan per second covers every gated card.
        void RefreshGatePresence()
        {
            List<KeyValuePair<string, string>> wins = null;
            for (int i = 0; i < cfg.Slots.Count && i < surface.Count; i++)
            {
                SlotConfig s = cfg.Slots[i];
                bool missing = false;
                if (s.IsGated)
                {
                    if (wins == null) wins = WindowMatcher.OpenWindows(IntPtr.Zero);
                    missing = !WindowMatcher.AnyOpen(s.WinTitle, wins);
                }
                if (surface.SetGateMissing(i, missing))
                    surface.RefreshStatus(i, Engine.IsRunning(i), Engine.ActualCps(i));
            }
        }

        // A gated card's window comes and goes as the user alt-tabs, so the
        // gate is refreshed rather than the slot restarted. One foreground
        // query per tick, and per-card matching only re-runs when the
        // foreground actually changed - Matches() opens a process handle for
        // ahk_exe gates, and paying that per card per tick added up.
        void UpdateGates()
        {
            IntPtr fg = WindowMatcher.Foreground();
            bool fgChanged = fg != lastFg;
            lastFg = fg;
            for (int i = 0; i < cfg.Slots.Count; i++)
            {
                SlotConfig s = cfg.Slots[i];
                if (!s.IsGated) continue;
                bool active;
                if (fgChanged || !gateActive.TryGetValue(i, out active))
                    gateActive[i] = active = WindowMatcher.Matches(s.WinTitle, fg);
                if (!Engine.IsRunning(i)) continue;
                if (active) { Engine.SetGate(i, fg); continue; }
                if (s.FlickFocus)
                {
                    // A FlickFocus card clicks its window from BEHIND, so it
                    // needs the real handle StartTogether resolved - writing
                    // the gated-out sentinel here silently stopped the card
                    // within one tick of alt-tabbing away. A live handle is
                    // left alone; a dead one is re-found, at most once a
                    // second (Find walks every window on the desktop).
                    if (WindowMatcher.IsLive(Engine.GateOf(i))) continue;
                    int last;
                    if (flickFindAt.TryGetValue(i, out last)
                        && Environment.TickCount - last < 1000) continue;
                    flickFindAt[i] = Environment.TickCount;
                    IntPtr h = WindowMatcher.Find(s.WinTitle);
                    Engine.SetGate(i, h != IntPtr.Zero ? h : new IntPtr(1));
                }
                else Engine.SetGate(i, new IntPtr(1));
            }
        }

        // The cached answer to "is this card's gate window in front?", kept
        // fresh by UpdateGates. The direct query only runs on a cache miss -
        // a card added within the last tick - never per keystroke.
        bool GateActive(int i)
        {
            bool a;
            if (gateActive.TryGetValue(i, out a)) return a;
            return WindowMatcher.IsActive(cfg.Slots[i].WinTitle);
        }

        // --- hotkeys --------------------------------------------------------

        void OnHotkey(object sender, HotkeyEventArgs e)
        {
            if (IsDisposed) return;

            // A hotkey box with focus is asking what the user just pressed, and
            // that outranks every binding - otherwise a key already in use could
            // never be reassigned, and the kill switch could never be changed
            // because pressing it would only fire it. Modifiers stay untouched
            // so the user can hold them.
            // ...but only while this window is the one in front. Focus inside a
            // form outlives the form being active, so without this a user who
            // clicked a hotkey box and then switched to their game would have
            // every keystroke swallowed by a box they can't even see.
            // Field read first: this runs for every keystroke on the machine,
            // and asking the system for the foreground window on each one puts a
            // call in front of the user's typing for no reason.
            HotkeyBox capturing = HotkeyBox.Capturing;
            // The box's own window must be the one in front - and that window
            // is the Settings dialog for the three global boxes, not this form,
            // so the check asks the box rather than assuming
            if (capturing != null)
            {
                Form top = capturing.FindForm();
                if (top == null || !top.Visible || GetForegroundWindow() != top.Handle)
                    capturing = null;
            }
            // A single-key box takes modifiers too - Shift on its own is a
            // valid custom key there, not a combo half-typed
            if (capturing != null && e.IsDown
                && (capturing.SingleKey || !HotkeyBox.IsModifier(e.Vk))
                && !HotkeyBox.IsNavKey(e.Vk))
            {
                e.Consume = true;
                ushort vk = e.Vk;
                bool ct = e.Ctrl, al = e.Alt, sh = e.Shift, wn = e.Win;
                Post(delegate { capturing.TakeFromHook(vk, ct, al, sh, wn); });
                return;
            }

            // Typematic repeats act on nothing - the first down already
            // did, and acting again restarts the pacer, which keeps a held
            // Hold-mode hotkey from ever clicking. But a repeat of a key we
            // own must still be SWALLOWED, or holding a hotkey leaks the key
            // into the focused app. This mirrors the
            // ownership decisions below, minus their actions.
            if (e.IsDown && e.Repeat)
            {
                if (Match(cfg.StopAllKey, e) || Match(cfg.KillSwitchKey, e))
                { e.Consume = true; return; }
                if (killActive) return;
                if ((armedIndex >= 0 || Recorder.Recording) && Match(cfg.RecordKey, e))
                { e.Consume = true; return; }
                if (Recorder.Recording && e.Vk == 0x1B) { e.Consume = true; return; }
                for (int i = 0; i < cfg.Slots.Count; i++)
                {
                    SlotConfig s = cfg.Slots[i];
                    if (s.HotkeyOff || !Match(s.Hotkey, e)) continue;
                    // Same rule as a real down: a gated, stopped, no-focus
                    // card outside its window leaves the key alone
                    if (s.Mode == "Hold" || Engine.IsRunning(i) || !s.IsGated
                        || s.FocusWindow || GateActive(i))
                    { e.Consume = true; break; }
                }
                return;
            }

            // Global actions first. Emergency stop stays live even when the
            // kill switch has suspended everything else.
            if (e.IsDown && Match(cfg.StopAllKey, e))
            {
                e.Consume = true;
                Post(delegate { StopEverything(); });
                return;
            }
            // The kill switch must outrank its own suspension, or it is a
            // one-way switch: with hotkeys killed, the early return below
            // swallowed the very key that turns them back on
            if (e.IsDown && Match(cfg.KillSwitchKey, e))
            {
                e.Consume = true;
                Post(delegate { ToggleKill(); });
                return;
            }
            if (killActive) return;
            // The record key only means something with a card readied, so with
            // nothing armed it passes through to whatever has focus
            if (e.IsDown && (armedIndex >= 0 || Recorder.Recording) && Match(cfg.RecordKey, e))
            {
                e.Consume = true;
                Post(delegate { ToggleRecording(); });
                return;
            }
            // Escape also ends a take - it's the key people reach for - but is
            // only ours while one is actually running
            if (e.IsDown && Recorder.Recording && e.Vk == 0x1B)
            {
                e.Consume = true;
                Post(delegate { StopRecordingFlow(); });
                return;
            }

            // Card hotkeys. Which cards would act decides whether the key is
            // ours at all: if none would, it belongs to whatever has focus.
            var starts = new List<int>();
            bool anyActs = false;
            for (int i = 0; i < cfg.Slots.Count; i++)
            {
                SlotConfig s = cfg.Slots[i];
                if (s.HotkeyOff || !Match(s.Hotkey, e)) continue;
                bool running = Engine.IsRunning(i);

                if (!e.IsDown)
                {
                    // Hold mode releases on key-up, and that must work from
                    // anywhere or letting go outside the window strands it
                    if (s.Mode == "Hold" && running) { anyActs = true; Stop(i); }
                    // A toggle card owns its key's release too: the down
                    // toggled, so a naked up would leak into whatever has
                    // focus. Same pass-through rule as the down - a gated card
                    // outside its window leaves the key alone entirely.
                    else if (s.Mode != "Hold"
                          && !(s.IsGated && !running && !GateActive(i)
                               && !s.FocusWindow))
                        anyActs = true;
                    continue;
                }

                // Toggle cards sharing this key act as ONE unit: any of them
                // running means the press stops the runners, none running
                // means it starts them all. Toggling each card by its own
                // state let a group desync permanently - one card stopped by
                // its click limit (or skipped by its gate) flipped phase, and
                // every press after swapped which half was on.
                bool starting = s.Mode == "Hold" || !GroupRunning(e);
                // Click-through does NOT loosen the start rule: starting still
                // requires the gate window in front. The flick only KEEPS a
                // running card going after the user switches away.
                if (starting && s.IsGated && !GateActive(i))
                {
                    // ...unless the card asked to be taken to its window first
                    if (!s.FocusWindow)
                    {
                        // The key deliberately passes through, but a card that
                        // silently ignores its own hotkey looks broken. Say why
                        // on its status line - a gate pointing at a program
                        // that isn't running any more is invisible otherwise.
                        DateTime last;
                        if (!gateMissAt.TryGetValue(i, out last)
                            || (DateTime.Now - last).TotalSeconds > 5)
                        {
                            gateMissAt[i] = DateTime.Now;
                            int gi = i;
                            string key = HotkeyParser.Parse(s.Hotkey).ToString();
                            string gate = s.GateShort();
                            Post(delegate
                            {
                                if (gi >= surface.Count) return;
                                surface.Notice(gi, key + " pressed - " + gate
                                    + (surface.IsGateMissing(gi)
                                        ? " isn't open" : " isn't in front"));
                            });
                        }
                        continue;
                    }
                    anyActs = true;
                    int idx = i;
                    Post(delegate { FocusAndStart(idx); });
                    continue;
                }
                anyActs = true;
                if (starting) starts.Add(i);
                else if (running) Stop(i);
            }

            if (anyActs || starts.Count > 0)
            {
                e.Consume = true;
                if (starts.Count > 0)
                {
                    var list = starts;
                    Post(delegate { StartTogether(list); });
                }
            }
            // A real key that ISN'T one of ours is the user doing something -
            // cards that asked to stop on input stop now. Consumed keys are
            // our own hotkeys; the engine ignores this while nothing armed it.
            if (e.IsDown && !e.Consume) Engine.NoteUserKey();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        // --- elevated-window warning ----------------------------------------
        //
        // A non-elevated process's keyboard hook receives nothing while an
        // elevated window has focus - by OS design, not by bug. The symptom is
        // hotkeys that work everywhere except one particular game or tool, so
        // when that's about to happen, name it in a balloon rather than letting
        // the user conclude the app is broken.

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);

        static bool? _selfElevated;
        readonly HashSet<string> elevationWarned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool ProcessIsElevated(uint pid)
        {
            IntPtr proc = OpenProcess(0x1000, false, pid);      // QUERY_LIMITED_INFORMATION
            if (proc == IntPtr.Zero) return false;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(proc, 0x8, out token)) return false;   // TOKEN_QUERY
                try
                {
                    int elevated, got;
                    return GetTokenInformation(token, 20, out elevated, 4, out got) && elevated != 0;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(proc); }
        }

        void WarnIfForegroundElevated()
        {
            if (killActive) return;
            // Only worth saying if a hotkey could actually be waiting
            bool anyHotkey = false;
            foreach (SlotConfig s in cfg.Slots)
                if (!s.HotkeyOff && s.Hotkey.Trim().Length > 0) { anyHotkey = true; break; }
            if (!anyHotkey) return;

            if (_selfElevated == null)
            {
                try
                {
                    using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
                        _selfElevated = new System.Security.Principal.WindowsPrincipal(id)
                            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { _selfElevated = false; }
            }
            if (_selfElevated == true) return;      // we can reach everything

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle) return;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            if (pid == 0) return;
            try
            {
                if (!ProcessIsElevated(pid)) return;
                string exe = WindowMatcher.ExeOf(fg);
                if (exe.Length == 0 || !elevationWarned.Add(exe)) return;
                tray.ShowBalloonTip(6000, "Hotkeys can't reach " + exe,
                    exe + " runs as administrator, so Windows hides your keys from this app"
                    + " while it's focused. Run Polyclicker as administrator to use"
                    + " hotkeys there.", ToolTipIcon.Warning);
            }
            catch { }
        }

        // --- resize background ----------------------------------------------
        //
        // WinForms registers its window classes with no background brush, so
        // when the window is enlarged the system paints the newly exposed strip
        // black until the next WM_PAINT catches up - a black flash on every
        // outward drag. Giving the class a brush in the form's own colour makes
        // that pre-paint fill invisible. One brush of ours is alive at a time;
        // the previous one is deleted when the theme swaps it out.
        [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
        static extern IntPtr SetClassLongPtr(IntPtr hWnd, int index, IntPtr value);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern IntPtr CreateSolidBrush(int color);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr h);
        const int GCLP_HBRBACKGROUND = -10;
        IntPtr classBrush;

        void SetClassBrush()
        {
            int colorref = BackColor.R | (BackColor.G << 8) | (BackColor.B << 16);
            IntPtr b = CreateSolidBrush(colorref);
            SetClassLongPtr(Handle, GCLP_HBRBACKGROUND, b);
            if (classBrush != IntPtr.Zero) DeleteObject(classBrush);
            classBrush = b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetClassBrush();
            // ApplyTheme ran in the constructor, before this handle existed -
            // the DWM attributes (dark caption, palette caption tint) need a
            // real window to stick to
            Theme.DarkTitleBar(this);
        }

        // "System default" theme follows Windows live: flipping dark mode in
        // Windows Settings broadcasts WM_SETTINGCHANGE, and the app follows.
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SETTINGCHANGE && cfg != null && cfg.ThemeMode == "system")
            {
                bool dark = Theme.ResolveDark("system");
                if (dark != Theme.Dark) { Theme.Dark = dark; ApplyTheme(); }
            }
            // The user picked a new Windows accent - the caption tint follows
            if (m.Msg == WM_DWMCOLORIZATIONCOLORCHANGED && cfg != null)
                Theme.DarkTitleBar(this);
            base.WndProc(ref m);
        }

        // Is any toggle card bound to this same key running right now? The
        // group's shared state, for the all-start-or-all-stop rule above.
        bool GroupRunning(HotkeyEventArgs e)
        {
            for (int j = 0; j < cfg.Slots.Count; j++)
            {
                SlotConfig o = cfg.Slots[j];
                if (!o.HotkeyOff && o.Mode != "Hold" && Match(o.Hotkey, e)
                    && Engine.IsRunning(j))
                    return true;
            }
            return false;
        }

        // Parsed once per distinct spec, not once per keystroke: Match runs
        // inside the hook callback for the three global keys plus every card,
        // and Parse's Trim/Substring/ToLowerInvariant allocations added up on
        // a path that lags the whole machine's typing when it dawdles. Keyed
        // by the spec string itself, so an edited hotkey is simply a new key.
        readonly Dictionary<string, HotkeyCombo> comboCache =
            new Dictionary<string, HotkeyCombo>();

        bool Match(string spec, HotkeyEventArgs e)
        {
            if (string.IsNullOrEmpty(spec)) return false;
            HotkeyCombo c;
            if (!comboCache.TryGetValue(spec, out c))
            {
                if (comboCache.Count > 256) comboCache.Clear();   // edited-away specs
                comboCache[spec] = c = HotkeyParser.Parse(spec);
            }
            return c.IsSet && c.Matches(e.Vk, e.Ctrl, e.Alt, e.Shift, e.Win);
        }

        // The hook callback sits in front of every keystroke on the machine,
        // so it must never do the work itself.
        void Post(MethodInvoker action)
        {
            try { BeginInvoke(action); } catch { }
        }

        // Reached from inside the keyboard hook callback, so it uses the
        // engine's hook-safe stop: plain Stop waits on PointerLock for the
        // cursor restore, and a pace thread can hold that lock across a
        // SendInput retry loop - blocking the hook callback on it is how
        // hotkeys die machine-wide.
        void Stop(int i)
        {
            Engine.StopFromHook(i);
            Post(delegate { Renumber(); });
        }

        void FocusAndStart(int i)
        {
            if (i < 0 || i >= cfg.Slots.Count) return;    // captured pre-removal
            SlotConfig s = cfg.Slots[i];
            IntPtr h = WindowMatcher.Find(s.WinTitle);
            if (h == IntPtr.Zero) return;
            WindowMatcher.Activate(h);
            var one = new List<int>(); one.Add(i);
            StartTogether(one);
        }

        // Firing several cards on the same tick means one pointer trying to be
        // in several places at once. The engine keeps that correct, but a game
        // sampling input once a frame still only notices one of them - so each
        // card gets a share of the interval to itself.
        void StartTogether(List<int> ids)
        {
            // The hook captured these indices before this delegate reached
            // the UI thread; a card removed in between must not start its
            // neighbour (or throw). Filter, don't trust.
            var live = new List<int>();
            foreach (int id in ids) if (id >= 0 && id < cfg.Slots.Count) live.Add(id);
            ids = live;
            if (ids.Count == 0) return;
            int shortest = int.MaxValue;
            foreach (int i in ids) shortest = Math.Min(shortest, Math.Max(1, cfg.Slots[i].Interval));
            double step = ids.Count > 1 ? (double)shortest / ids.Count : 0;

            bool macroTaken = false;
            for (int k = 0; k < ids.Count; k++)
            {
                int i = ids[k];
                SlotConfig s = cfg.Slots[i];
                IntPtr gate = IntPtr.Zero;
                if (s.IsGated)
                {
                    IntPtr fg = WindowMatcher.Foreground();
                    if (WindowMatcher.Matches(s.WinTitle, fg)) gate = fg;
                    else if (s.FlickFocus)
                    {
                        // Click-through: resolve the actual window so the
                        // engine can flick focus to it per beat
                        IntPtr h = WindowMatcher.Find(s.WinTitle);
                        gate = h != IntPtr.Zero ? h : new IntPtr(1);
                    }
                    else gate = new IntPtr(1);
                }
                // Macro cards replay their take; the interval is the pause
                // between repeats rather than the gap between clicks. One
                // macro at a time: two takes sharing one hotkey would fight
                // over the pointer, so the first one on the key wins.
                if (s.IsMacro)
                {
                    if (macroTaken)
                    {
                        surface.Notice(i, "Only one macro can play at a time.");
                        continue;
                    }
                    // One take playing anywhere, not just one per hotkey. A new
                    // take interrupts whatever was running, at once - two of
                    // them driving the pointer through different recorded paths
                    // is not a blend of both, it is neither. Clickers are left
                    // alone: they share the pointer by design.
                    foreach (int m in Engine.RunningMacros())
                        if (m != i) Engine.Stop(m);
                    string mpath = Path.Combine(AppConfig.MacroDir, s.Macro.Trim());
                    string warn;
                    if (s.Macro.Trim().Length == 0 || !Engine.StartMacro(i, s, gate, mpath, out warn))
                        surface.Notice(i, "No macro selected to play - record one or pick one.");
                    else
                    {
                        macroTaken = true;
                        Log.Line("slot " + (i + 1) + " started (macro " + s.Macro.Trim() + ")");
                        if (warn != null) surface.Notice(i, warn);
                    }
                    continue;
                }
                Engine.Start(i, s, gate, step * k);
                Log.Line("slot " + (i + 1) + " started (" + s.Input + ")");
            }
            Renumber();
        }

        // --- odds and ends --------------------------------------------------

        void ToggleArm(int idx)
        {
            if (armedIndex == idx) { armedIndex = -1; surface.SetArmed(idx, false); return; }
            for (int i = 0; i < surface.Count; i++) surface.SetArmed(i, false);
            armedIndex = idx;
            surface.RecordKeyName = HotkeyParser.Parse(cfg.RecordKey).ToString();
            surface.SetArmed(idx, true);
        }

        void PickPosition(int idx)
        {
            Hide();
            var t = new Timer();
            int left = 3;
            t.Interval = 1000;
            t.Tick += delegate
            {
                if (--left > 0) { CursorToast.Pop("Capturing in " + left + "...", 1100, true); return; }
                t.Stop(); t.Dispose();
                System.Drawing.Point p = Cursor.Position;
                if (idx < surface.Count)
                {
                    surface.CfgAt(idx).X = p.X;
                    surface.CfgAt(idx).Y = p.Y;
                    surface.Reflow();
                }
                CursorToast.Pop("Saved " + p.X + ", " + p.Y);
                Show();
                SaveSoon();
            };
            CursorToast.Pop("Capturing in 3...", 1100, true);
            t.Start();
        }

        public void RefreshMacroLists()
        {
            string[] names;
            try
            {
                if (!Directory.Exists(AppConfig.MacroDir)) { names = new string[0]; }
                else
                {
                    string[] files = Directory.GetFiles(AppConfig.MacroDir, "*.macro");
                    names = new string[files.Length];
                    for (int i = 0; i < files.Length; i++) names[i] = Path.GetFileName(files[i]);
                }
            }
            catch { names = new string[0]; }
            surface.SetMacroList(names);
        }

        const string NewProfileItem = "(New - start fresh)";

        void RefreshProfileList(string select)
        {
            try
            {
                profileCombo.Items.Clear();
                profileCombo.Items.Add(NewProfileItem);
                if (Directory.Exists(AppConfig.ProfileDir))
                    foreach (string f in Directory.GetFiles(AppConfig.ProfileDir, "*.ini"))
                        profileCombo.Items.Add(Path.GetFileNameWithoutExtension(f));
                profileCombo.Text = select.Length > 0 && profileCombo.Items.Contains(select)
                                  ? select : "";
                profileCombo.Invalidate();
            }
            catch { }
        }

        void OnProfileChosen(string name)
        {
            if (name == null || name.Length == 0) return;
            if (name == NewProfileItem)
            {
                // Nothing at stake, no question asked - it just starts fresh
                if (!OkToDiscard("starting fresh")) { RestoreProfileSelection(); return; }
                Engine.StopAll();
                // Same as ApplyProfile: a take left rolling across the reset
                // would keep recording the user's unrelated input and land it
                // in the fresh blank card
                if (Recorder.Recording) Recorder.Stop(true);
                recordIndex = -1;
                armedIndex = -1;
                cfg.CurrentProfile = "";
                surface.Clear();
                cfg.Slots.Clear();
                AddCard(new SlotConfig(), true);
                profileCombo.Text = "";
                return;
            }
            string path = Path.Combine(AppConfig.ProfileDir, name + ".ini");
            if (!File.Exists(path)) { RefreshProfileList(""); return; }
            // Loading over the top discards just as thoroughly as starting fresh
            if (!OkToDiscard("loading '" + name + "'")) { RestoreProfileSelection(); return; }
            ApplyProfile(path);
            cfg.CurrentProfile = name;
            SaveSoon();
        }

        // --- persistence ----------------------------------------------------

        // Debounced: typing a name fires Changed per keystroke, and each save
        // writes the whole file. One write 400 ms after the last edit is the
        // same durability without the per-keystroke cost.
        Timer saveTimer;
        void SaveSoon()
        {
            if (suppressSave) return;
            if (saveTimer == null)
            {
                saveTimer = new Timer();
                saveTimer.Interval = 400;
                saveTimer.Tick += delegate { saveTimer.Stop(); cfg.Save(); };
            }
            saveTimer.Stop();
            saveTimer.Start();
        }

        void RestorePlacement()
        {
            int w = cfg.WinW > 0 ? cfg.WinW : 560;
            int h = cfg.WinH > 0 ? cfg.WinH : 560;
            ClientSize = new Size(Math.Max(MinimumSize.Width, w), Math.Max(300, h));
            if (cfg.WinX != int.MinValue && OnAScreen(cfg.WinX, cfg.WinY))
                Location = new Point(cfg.WinX, cfg.WinY);
            else
                StartPosition = FormStartPosition.CenterScreen;
        }

        // A saved position on a monitor that no longer exists would put the
        // window somewhere the title bar can't be grabbed.
        static bool OnAScreen(int x, int y)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle r = s.WorkingArea;
                if (x < r.Right - 60 && x + 60 > r.Left && y < r.Bottom - 30 && y + 30 > r.Top)
                    return true;
            }
            return false;
        }

        void SavePlacement()
        {
            if (WindowState != FormWindowState.Normal) return;
            cfg.WinX = Location.X; cfg.WinY = Location.Y;
            cfg.WinW = ClientSize.Width; cfg.WinH = ClientSize.Height;
        }
    }
}
