// ===========================================================================
//  Theme - every color the app draws, in one place, in two moods
// ---------------------------------------------------------------------------
//  The card surface paints itself, so theming it is just answering "what
//  color" from here instead of from constants. The palette is a pastel
//  rainbow - Catppuccin-ish, a little paler and brighter - and every color
//  has a dark-mode twin so a colored card reads the same way in both.
//
//  System-drawn things (scrollbar, message boxes, the dropdown popups) keep
//  their Windows default colors for now - only what we paint follows this.
// ===========================================================================

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Polyclicker
{
    // One card color: the INI name, the label in the picker, and both moods.
    sealed class CardTint
    {
        public readonly string Name, Label;
        public readonly Color Light, Dark;
        public CardTint(string name, string label, Color light, Color dark)
        { Name = name; Label = label; Light = light; Dark = dark; }
    }

    static class Theme
    {
        public static bool Dark;

        // --- the pastel rainbow ------------------------------------------------
        // Equal citizens, one row: None plus eight tints around the wheel.
        //
        // The dark fills hold the same hues at about 70% of the colour.
        // Matching the light set's chroma outright measures as equal
        // colourfulness but does not look it: chroma is absolute, and the eye
        // reads colour against a surface's own lightness. These sit near
        // L* 23 where the light set sits near 92, so equal chroma is 3.5x the
        // saturation and the cards shout.
        //
        // The cut is on a*/b* with L* left alone. Mixing them toward the plain
        // card would have taken the lightness difference with it, and that
        // difference is much of what tells one card from the next - measured,
        // it put the closest pair under the just-noticeable threshold.
        public static readonly CardTint[] Palette = new CardTint[]
        {
            new CardTint("",         "None",     Color.FromArgb(252, 252, 253), Color.FromArgb(43, 43, 49)),
            new CardTint("rose",     "Rose",     Color.FromArgb(250, 226, 231), Color.FromArgb(62, 50, 55)),
            new CardTint("peach",    "Peach",    Color.FromArgb(251, 233, 218), Color.FromArgb(64, 56, 50)),
            new CardTint("sunshine", "Sunshine", Color.FromArgb(250, 243, 213), Color.FromArgb(63, 60, 50)),
            new CardTint("mint",     "Mint",     Color.FromArgb(222, 243, 223), Color.FromArgb(50, 61, 52)),
            new CardTint("teal",     "Teal",     Color.FromArgb(216, 241, 239), Color.FromArgb(48, 60, 60)),
            new CardTint("sky",      "Sky",      Color.FromArgb(220, 234, 250), Color.FromArgb(49, 56, 66)),
            new CardTint("lavender", "Lavender", Color.FromArgb(229, 229, 250), Color.FromArgb(55, 54, 68)),
            new CardTint("mauve",    "Mauve",    Color.FromArgb(243, 224, 245), Color.FromArgb(60, 50, 63)),
        };

        public static CardTint TintOf(string name)
        {
            name = (name ?? "").Trim().ToLowerInvariant();
            foreach (CardTint t in Palette) if (t.Name == name) return t;
            return Palette[0];
        }

        public static Color CardFill(string tintName)
        {
            CardTint t = TintOf(tintName);
            return Dark ? t.Dark : t.Light;
        }

        // The locked base gray - decisively darker than the near-white of a
        // plain card, so "plain and on" and "locked and off" can't be confused.
        public static Color LockedCard
        {
            get { return Dark ? Color.FromArgb(36, 36, 40) : Color.FromArgb(230, 230, 234); }
        }

        // A locked card's fill: the tint's fill pulled halfway to the locked
        // gray and softened - recognizably the card's color, clearly muted.
        // Untinted cards use the base gray outright.
        public static Color LockedCardFill(string tintName)
        {
            CardTint t = TintOf(tintName);
            if (t.Name.Length == 0) return LockedCard;
            return Desaturate(Mix(Dark ? t.Dark : t.Light, LockedCard, 0.45), 0.2);
        }

        // The same dot on a locked card, drained the way the card fill is,
        // so the picker does not stay vivid while every other control on the
        // card goes quiet.
        public static Color SwatchOfDim(string tintName)
        {
            return Desaturate(Mix(SwatchOf(tintName), LockedCard, 0.35), 0.25);
        }

        // A saturated dot of the tint for the picker swatches - the fill colors
        // are too pale to tell apart at 16px.
        public static Color SwatchOf(string tintName)
        {
            CardTint t = TintOf(tintName);
            if (t.Name.Length == 0) return Dark ? Color.FromArgb(120, 120, 130) : Color.FromArgb(190, 190, 198);
            // pull the pale fill toward its hue: deepen each channel's distance
            // from white until the tint is unmistakable at 14 px
            Color c = t.Light;
            return Color.FromArgb(Deepen(c.R), Deepen(c.G), Deepen(c.B));
        }

        static int Deepen(int channel)
        {
            return Math.Max(70, 255 - (255 - channel) * 4);
        }

        // --- chrome ------------------------------------------------------------
        public static Color FormBack   { get { return Dark ? Color.FromArgb(32, 32, 37)    : SystemColors.Control; } }
        // Deliberately NOT max-contrast: near-white-on-dark and near-black-on-
        // light both glare. A step toward the background keeps everything
        // comfortably readable while letting the pastels stay the loudest
        // thing on screen.
        public static Color FormText   { get { return Dark ? Color.FromArgb(204, 204, 212) : Color.FromArgb(60, 60, 68); } }
        public static Color MutedText  { get { return Dark ? Color.FromArgb(150, 150, 160) : Color.FromArgb(108, 108, 118); } }
        public static Color CardLine   { get { return Dark ? Color.FromArgb(69, 70, 80)    : Color.FromArgb(208, 208, 214); } }

        // The one interactive-accent color: focused fields, checked radios,
        // the lifted card while dragging. A muted violet from the palette's
        // lavender corner - deliberately NOT the Windows blue, which sat in
        // the pastels like a traffic light.
        public static Color Accent
        {
            get { return Dark ? Color.FromArgb(176, 166, 222) : Color.FromArgb(134, 120, 186); }
        }

        // Text on a locked card: quieter than muted, louder than invisible
        public static Color DimText
        {
            get { return Dark ? Color.FromArgb(109, 109, 117) : Color.FromArgb(130, 130, 138); }
        }

        // Fields sit a step lighter than the card in light mode, a step darker
        // in dark mode - so they read as wells either way.
        // "System default" follows the Windows apps theme. Read at startup and
        // again on WM_SETTINGCHANGE, so flipping Windows dark mode follows live.
        public static bool SystemPrefersDark()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        // The saved mode - "light" | "dark" | "system" - resolved to a yes/no.
        public static bool ResolveDark(string mode)
        {
            if (mode == "dark") return true;
            if (mode == "system") return SystemPrefersDark();
            return false;
        }

        public static Color FieldBack  { get { return Dark ? Color.FromArgb(30, 30, 34)    : Color.White; } }
        public static Color FieldText  { get { return Dark ? Color.FromArgb(210, 210, 218) : Color.FromArgb(52, 52, 60); } }
        // Every dark edge in here - card, field, chip - stops at about 80%
        // of the separation its light twin has. Matching outright measured
        // equal but looked louder: a pale line on a dark ground blooms in a
        // way a dark line on a pale one does not.
        public static Color FieldLine  { get { return Dark ? Color.FromArgb(82, 81, 92)    : Color.FromArgb(172, 172, 180); } }

        // The icon chips - neutral base for chrome that belongs to no card
        public static Color ChipBack   { get { return Dark ? Color.FromArgb(58, 58, 64)    : Color.FromArgb(250, 250, 252); } }
        // Hover lifts the chip a level; it does not change its hue. A tinted
        // hover on a neutral chip reads as a colour change rather than a
        // lift - and a blue one drifts toward the Windows blue the accent is
        // picked to avoid.
        public static Color ChipHot    { get { return Dark ? Color.FromArgb(76, 76, 85)    : Color.FromArgb(228, 228, 232); } }
        public static Color ChipLine   { get { return Dark ? Color.FromArgb(92, 92, 101)   : Color.FromArgb(198, 198, 208); } }
        public static Color ChipText   { get { return Dark ? Color.FromArgb(198, 198, 208) : Color.FromArgb(72, 72, 80); } }

        // The profile dropdown sits one small step above the strip in both
        // moods - measured, +3.9 in dark against light's +3.7. Neither shared
        // well fits: in dark the chip fill is a raised slab, and on a control
        // most of the strip wide it turns the whole bar into one grey band,
        // while the field well sits BELOW the strip and reads as a hole
        // punched in it. So it takes its own step, level with light's.
        public static Color DropBack { get { return Dark ? Color.FromArgb(40, 40, 45) : ChipBack; } }
        public static Color DropHot  { get { return Dark ? Color.FromArgb(48, 48, 54) : ChipHot; } }
        public static Color DropLine { get { return Dark ? FieldLine : ChipLine; } }
        public static Color DropText { get { return Dark ? FieldText : ChipText; } }

        // --- chip styles -------------------------------------------------------
        // The edge buttons wear their meaning: red removes, bronze locks, gray
        // configures. Deeper and more saturated than the pastel cards, but the
        // fills stay soft so they sit level with the rest of the surface.

        public sealed class ChipStyle
        {
            public readonly Color Back, Hot, Line, Text;
            public ChipStyle(Color back, Color hot, Color line, Color text)
            { Back = back; Hot = hot; Line = line; Text = text; }
        }

        public static ChipStyle NeutralChip
        {
            get { return new ChipStyle(ChipBack, ChipHot, ChipLine, ChipText); }
        }

        public static ChipStyle RemoveChip
        {
            get
            {
                return Dark
                    ? new ChipStyle(Color.FromArgb(70, 49, 46), Color.FromArgb(89, 60, 56),
                                    Color.FromArgb(146, 97, 91), Color.FromArgb(255, 161, 147))
                    : new ChipStyle(Color.FromArgb(248, 219, 214), Color.FromArgb(243, 198, 191),
                                    Color.FromArgb(212, 138, 127), Color.FromArgb(166, 68, 52));
            }
        }

        public static ChipStyle LockChip
        {
            get
            {
                return Dark
                    ? new ChipStyle(Color.FromArgb(72, 63, 46), Color.FromArgb(88, 78, 57),
                                    Color.FromArgb(137, 120, 88), Color.FromArgb(218, 180, 106))
                    : new ChipStyle(Color.FromArgb(245, 230, 196), Color.FromArgb(239, 219, 169),
                                    Color.FromArgb(196, 161, 94), Color.FromArgb(134, 95, 26));
            }
        }

        // Expand is an invitation, not a warning - it goes green
        public static ChipStyle GoChip
        {
            get
            {
                return Dark
                    ? new ChipStyle(Color.FromArgb(48, 63, 51), Color.FromArgb(59, 76, 62),
                                    Color.FromArgb(98, 127, 103), Color.FromArgb(120, 203, 145))
                    : new ChipStyle(Color.FromArgb(223, 241, 225), Color.FromArgb(204, 233, 208),
                                    Color.FromArgb(126, 182, 136), Color.FromArgb(26, 117, 53));
            }
        }

        public static ChipStyle GearChip
        {
            get
            {
                return Dark
                    ? new ChipStyle(Color.FromArgb(64, 64, 71), Color.FromArgb(80, 80, 89),
                                    Color.FromArgb(102, 103, 112), Color.FromArgb(203, 203, 213))
                    : new ChipStyle(Color.FromArgb(233, 233, 238), Color.FromArgb(221, 221, 229),
                                    Color.FromArgb(176, 176, 187), Color.FromArgb(86, 86, 96));
            }
        }

        // In-card buttons follow their card's tint: a step lighter than the
        // card in light mode, a step darker in dark mode, glyph in the deep hue
        public static ChipStyle TintChip(string tintName)
        {
            CardTint t = TintOf(tintName);
            if (t.Name.Length == 0) return NeutralChip;
            Color card = Dark ? t.Dark : t.Light;
            Color acc = SwatchOf(tintName);
            // Text matches the weight of the functional chips' glyph colors:
            // the pastel accent alone read washed-out next to them
            if (!Dark)
                return new ChipStyle(Mix(card, Color.White, 0.55),
                                     Mix(card, Color.White, 0.15),
                                     Mix(acc, Color.White, 0.42),
                                     Mix(Saturate(acc, 1.35), Color.Black, 0.35));
            return new ChipStyle(Mix(card, Color.Black, 0.28),
                                 Mix(card, Color.White, 0.10),
                                 Mix(card, Color.White, 0.30),
                                 Mix(Saturate(acc, 1.15), Color.White, 0.25));
        }

        // The same tinted chip, mostly drained: a locked card's buttons still
        // whisper which color the card is without claiming to be live.
        // On a live card the chips sit on the tint, so their fills lean far
        // toward white. A locked card is gray, so the same recipe read as
        // colorless - here the fill leans on the tint itself, softened a
        // touch, and stays clearly the card's color against the gray.
        public static ChipStyle TintChipDim(string tintName)
        {
            CardTint t = TintOf(tintName);
            if (t.Name.Length == 0) return NeutralChip;
            Color card = Dark ? t.Dark : t.Light;
            Color acc = SwatchOf(tintName);
            if (!Dark)
                return new ChipStyle(Desaturate(Mix(card, Color.White, 0.25), 0.2),
                                     Desaturate(card, 0.2),
                                     Desaturate(Mix(acc, Color.White, 0.3), 0.2),
                                     Desaturate(Mix(acc, Color.Black, 0.2), 0.15));
            return new ChipStyle(Desaturate(card, 0.2),
                                 Desaturate(Mix(card, Color.White, 0.12), 0.2),
                                 Desaturate(Mix(card, Color.White, 0.32), 0.2),
                                 Desaturate(Mix(acc, Color.White, 0.3), 0.15));
        }

        // Push a color away from gray - the inverse of Desaturate
        static Color Saturate(Color c, double f)
        {
            int lum = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
            return Color.FromArgb(Clamp(lum + (c.R - lum) * f),
                                  Clamp(lum + (c.G - lum) * f),
                                  Clamp(lum + (c.B - lum) * f));
        }

        static int Clamp(double v) { return v < 0 ? 0 : v > 255 ? 255 : (int)v; }

        static Color Desaturate(Color c, double amount)
        {
            int lum = (c.R * 30 + c.G * 59 + c.B * 11) / 100;
            return Mix(c, Color.FromArgb(lum, lum, lum), amount);
        }

        static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        // --- status line -------------------------------------------------------
        // Drawn from the same wells as the functional chips, so a running
        // card's green is the expand button's green and an error's red is the
        // remove button's red - one palette, not two.
        //
        // The four carry equal weight against the card: matched contrast and
        // matched chroma, hue left alone; unlevelled, green and amber shout
        // while blue sits back. Light levels at 5.6. Dark levels at 7.2,
        // deliberately bright rather than midway - a lower level balances the
        // four just as well but drains the theme.
        //
        // Two exceptions. Warn - see WarnText for why a readable yellow on a
        // pale card cannot exist. And the light red, a step darker than the
        // level (5.86 on the card against the others' 5.57-5.63): at the
        // level it makes only 4.39 on the remove chip it also draws on, under
        // the 4.5 floor, and the extra step is not visible beside the others.
        public static Color RunGreen   { get { return GoChip.Text; } }
        public static Color ReadyBlue  { get { return Dark ? Color.FromArgb(124, 191, 255) : Color.FromArgb(26, 103, 183); } }
        public static Color ArmedRed   { get { return RemoveChip.Text; } }
        public static Color OffText    { get { return Dark ? Color.FromArgb(128, 128, 138) : Color.FromArgb(0x8A, 0x8A, 0x94); } }
        public static Color NoticeRed  { get { return RemoveChip.Text; } }
        // Warn is the one status that does not come from a chip. Yellow is the
        // brightest hue there is - pure yellow scores 1.05 against a near-white
        // card - so on light it has to be darkened to be read, and dark yellow
        // is brown: measured down the hue at constant chroma, 2.5 is still
        // yellow, 3.5 dull gold, 4.5 bronze, 5.6 brown. This sits at 4.5, the
        // lightest that still carries as text, and stops short of the levelled
        // 5.6 the other three share on purpose. Dark has no such problem and
        // keeps the lock chip's yellow.
        public static Color WarnText
        {
            get { return Dark ? LockChip.Text : Color.FromArgb(145, 112, 24); }
        }

        // --- fonts -------------------------------------------------------------
        // Segoe UI Variable (Win11) is the rounder, lighter cut; plain Segoe UI
        // is the fallback everywhere else. Created once and cached.
        static Font _ui, _title;

        static Font FirstInstalled(string[] names, float size, FontStyle style)
        {
            foreach (string n in names)
            {
                try
                {
                    var f = new Font(n, size, style);
                    // GDI truncates face names to 31 chars, so a long name that
                    // loaded fine comes back clipped - match by prefix
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)
                        || (f.Name.Length >= 31 && n.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase)))
                        return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font("Segoe UI", size, style);
        }

        public static Font UIFont
        {
            get
            {
                if (_ui == null)
                    _ui = FirstInstalled(new string[] { "Segoe UI Variable Text", "Segoe UI" },
                                         9.75f, FontStyle.Regular);
                return _ui;
            }
        }

        public static Font TitleFont
        {
            get
            {
                if (_title == null)
                    _title = FirstInstalled(new string[] { "Segoe UI Variable Display Semilight",
                                                           "Segoe UI Semilight", "Segoe UI Light", "Segoe UI" },
                                            15.5f, FontStyle.Regular);
                return _title;
            }
        }

        // --- applying to real controls (dialogs, the bottom strip) -------------

        public static void Style(Button b)
        {
            if (Dark)
            {
                b.FlatStyle = FlatStyle.Flat;
                b.BackColor = ChipBack;
                b.ForeColor = ChipText;
                b.FlatAppearance.BorderColor = ChipLine;
                b.FlatAppearance.MouseOverBackColor = ChipHot;
            }
            else
            {
                // System-drawn buttons cannot render an Image; anything
                // carrying a glyph uses the themed Standard renderer instead
                b.FlatStyle = b.Image != null ? FlatStyle.Standard : FlatStyle.System;
                b.UseVisualStyleBackColor = true;
                b.ForeColor = ChipText;
            }
        }

        // Recursive: called from a dialog's OnLoad, after every control exists.
        // Gray labels are muted on purpose and keep their own color.
        public static void Apply(Control root)
        {
            root.BackColor = FormBack;
            root.ForeColor = FormText;
            ApplyChildren(root);
        }

        static void ApplyChildren(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is Button) Style((Button)c);
                else if (c is HotkeyBox)
                {
                    c.BackColor = FieldBack;
                    c.ForeColor = FieldText;
                    c.Invalidate();
                }
                else if (c is TextBox || c is ListBox)
                {
                    c.BackColor = Dark ? FieldBack : Color.White;
                    c.ForeColor = FieldText;
                    var tb = c as TextBox;
                    if (tb != null && Dark) tb.BorderStyle = BorderStyle.FixedSingle;
                    if (c is ListBox) StyleScrollbars(c);
                }
                else if (c is LinkLabel)
                {
                    var l = (LinkLabel)c;
                    l.LinkColor = ReadyBlue;
                    l.ActiveLinkColor = RunGreen;
                }
                else if (c is Label)
                {
                    var l = (Label)c;
                    if (l.ForeColor != Color.Gray) l.ForeColor = FormText;
                    else if (Dark) l.ForeColor = MutedText;
                }
                else if (c is GroupBox)
                {
                    // A GroupBox draws its border in ForeColor, so a light text
                    // color turned the frames searing white in dark mode.
                    // Leave ForeColor alone and repaint the whole thing softly.
                    if (!"themed".Equals(c.Tag)) { c.Tag = "themed"; c.Paint += GroupBoxPaint; }
                    c.Invalidate();
                }
                else if (c is CheckBox || c is RadioButton)
                    c.ForeColor = "gated".Equals(c.Tag) ? DimText : FormText;
                else if (c is RadioDot) c.Invalidate();   // reads Theme live
                ApplyChildren(c);
            }
        }

        // Windows ships dark visual-style parts for scrollbars and combos, it
        // just never applies them to Win32 apps unasked. Asking is one call.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        // Dark scrollbars on a control that carries WS_VSCROLL
        public static void StyleScrollbars(Control c)
        {
            try
            {
                if (c.IsHandleCreated)
                    SetWindowTheme(c.Handle, Dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        // The drawn scrollbar. No track and no arrows - just the thumb, sitting
        // a step off the surface it floats over, the way the chips do.
        public static Color ScrollThumb
        {
            get { return Dark ? Color.FromArgb(72, 72, 82) : Color.FromArgb(200, 200, 208); }
        }
        public static Color ScrollThumbHot
        {
            get { return Dark ? Color.FromArgb(98, 98, 110) : Color.FromArgb(168, 168, 178); }
        }

        // --- cursors -----------------------------------------------------------
        // WinForms carries its own Hand and IBeam as embedded 1-bit monochrome
        // resources - the pre-XP shapes, hard-edged and unsmoothed, on a desktop
        // where every other cursor is a 32-bit alpha bitmap. Over a button or a
        // text field the pointer visibly turns into a jagged black-and-white
        // one. Measured: Cursors.Hand and Cursors.IBeam report 1bpp, every other
        // Cursors.* reports 32bpp colour.
        //
        // These load the OS copies instead. The handles are shared system
        // objects: the Cursor wrapper below is the non-owning constructor, so it
        // never destroys them.
        [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, int id);
        const int IDC_IBEAM = 32513, IDC_HAND = 32649;
        static Cursor handCur, beamCur;
        public static Cursor Hand  { get { return handCur ?? (handCur = Sys(IDC_HAND, Cursors.Hand)); } }
        public static Cursor IBeam { get { return beamCur ?? (beamCur = Sys(IDC_IBEAM, Cursors.IBeam)); } }

        static Cursor Sys(int id, Cursor fallback)
        {
            try
            {
                IntPtr h = LoadCursorW(IntPtr.Zero, id);
                return h != IntPtr.Zero ? new Cursor(h) : fallback;
            }
            catch { return fallback; }
        }

        // --- menus -------------------------------------------------------------
        // The color picker (and any other ContextMenuStrip) needs its whole
        // chrome themed - background, image margin, hover, border - not just
        // the item text.

        sealed class DarkMenuColors : ProfessionalColorTable
        {
            static readonly Color Bg = Color.FromArgb(43, 43, 50);
            static readonly Color Hover = Color.FromArgb(66, 66, 78);
            static readonly Color Line = Color.FromArgb(84, 84, 96);
            public override Color ToolStripDropDownBackground { get { return Bg; } }
            public override Color ImageMarginGradientBegin { get { return Bg; } }
            public override Color ImageMarginGradientMiddle { get { return Bg; } }
            public override Color ImageMarginGradientEnd { get { return Bg; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemBorder { get { return Line; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color CheckBackground { get { return Hover; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Hover; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Bg; } }
        }

        public static void StyleMenu(ContextMenuStrip menu)
        {
            menu.Font = UIFont;          // ToolStrips default to the system menu font
            if (Dark)
            {
                menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
                menu.BackColor = Color.FromArgb(43, 43, 50);
                menu.ForeColor = FormText;
            }
            else
                menu.Renderer = new ToolStripProfessionalRenderer();
            foreach (ToolStripItem it in menu.Items)
                it.ForeColor = FormText;
        }

        // Fully repaints a GroupBox in dark mode: quiet border, muted caption.
        // In light mode the default painting shows through untouched.
        static void GroupBoxPaint(object sender, PaintEventArgs e)
        {
            if (!Dark) return;
            var gb = (GroupBox)sender;
            e.Graphics.Clear(FormBack);
            Size ts = TextRenderer.MeasureText(gb.Text, gb.Font);
            int ty = gb.Font.Height / 2;
            int w = gb.Width, h = gb.Height;
            using (var p = new Pen(CardLine))
            {
                e.Graphics.DrawLine(p, 0, ty, 0, h - 1);              // left
                e.Graphics.DrawLine(p, 0, h - 1, w - 1, h - 1);       // bottom
                e.Graphics.DrawLine(p, w - 1, ty, w - 1, h - 1);      // right
                e.Graphics.DrawLine(p, 0, ty, 6, ty);                 // top, up to the caption
                e.Graphics.DrawLine(p, 8 + ts.Width, ty, w - 1, ty);  // and past it
            }
            TextRenderer.DrawText(e.Graphics, gb.Text, gb.Font, new Point(8, 0), MutedText,
                                  TextFormatFlags.NoPrefix);
        }

        // The title bar follows the theme too - without this a dark window
        // wears a glowing white hat.
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // --- rounded corners ----------------------------------------------------

        public static System.Drawing.Drawing2D.GraphicsPath RoundPath(Rectangle r, int rad)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(r.X, r.Y, rad * 2, rad * 2, 180, 90);
            p.AddArc(r.Right - rad * 2, r.Y, rad * 2, rad * 2, 270, 90);
            p.AddArc(r.Right - rad * 2, r.Bottom - rad * 2, rad * 2, rad * 2, 0, 90);
            p.AddArc(r.X, r.Bottom - rad * 2, rad * 2, rad * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Windows 11 rounds top-level popups on request - proper antialiased
        // corners, no Region jaggies. Silently a no-op on Windows 10.
        public static void RoundPopup(IntPtr hwnd)
        {
            try
            {
                int v = 3;                              // DWMWCP_ROUNDSMALL
                DwmSetWindowAttribute(hwnd, 33, ref v, 4);
            }
            catch { }
        }

        public static void DarkTitleBar(Form f)
        {
            try
            {
                if (!f.IsHandleCreated) return;
                int v = Dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0)
                    DwmSetWindowAttribute(f.Handle, 19, ref v, 4);   // pre-20H1 builds
                TintCaption(f);
            }
            catch { }
        }

        // --- palette-matched window decoration ---------------------------------
        // When Windows paints title bars in the user's accent color, this app's
        // caption joins in - but wearing the palette's nearest tint instead of
        // the raw accent, so a pink Windows gives the window a rose hat that
        // matches the cards. Users who keep neutral title bars see no change.

        const int DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35,
                  DWMWA_TEXT_COLOR = 36;

        // The user's title-bar accent, or null when Windows isn't coloring
        // title bars at all (Settings > Personalization > "Show accent color
        // on title bars" off).
        static Color? TitleAccent()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\DWM"))
                {
                    if (k == null) return null;
                    object prev = k.GetValue("ColorPrevalence");
                    if (!(prev is int) || (int)prev == 0) return null;
                    object v = k.GetValue("AccentColor");
                    if (!(v is int)) return null;
                    int abgr = (int)v;                     // 0xAABBGGRR
                    return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF,
                                          (abgr >> 16) & 0xFF);
                }
            }
            catch { return null; }
        }

        // Nearest palette tint by hue, judged against the saturated swatches -
        // the pastel fills are all nearly white, so hue is the honest axis.
        // A gray accent matches nothing and keeps the default chrome.
        static CardTint ClosestTint(Color accent)
        {
            if (accent.GetSaturation() < 0.12f) return null;
            float hue = accent.GetHue();
            CardTint best = null;
            float bestD = float.MaxValue;
            foreach (CardTint t in Palette)
            {
                if (t.Name.Length == 0) continue;
                float d = Math.Abs(hue - SwatchOf(t.Name).GetHue());
                if (d > 180f) d = 360f - d;
                if (d < bestD) { bestD = d; best = t; }
            }
            return best;
        }

        static void TintCaption(Form f)
        {
            Color? accent = TitleAccent();
            CardTint tint = accent.HasValue ? ClosestTint(accent.Value) : null;
            if (tint == null) return;              // neutral chrome stays neutral
            Color cap = Dark ? tint.Dark : tint.Light;
            int capRef = cap.R | (cap.G << 8) | (cap.B << 16);
            if (DwmSetWindowAttribute(f.Handle, DWMWA_CAPTION_COLOR, ref capRef, 4) != 0)
                return;                            // pre-Win11: no caption colors
            Color txt = FormText;
            int txtRef = txt.R | (txt.G << 8) | (txt.B << 16);
            DwmSetWindowAttribute(f.Handle, DWMWA_TEXT_COLOR, ref txtRef, 4);
            DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref capRef, 4);
        }
    }
}
