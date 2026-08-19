// ===========================================================================
//  Config - the saved state, one INI
// ---------------------------------------------------------------------------
//  The format is deliberately stable: same file name, same section names,
//  same keys across versions, so a user upgrading keeps every card, profile
//  and macro they already had.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Polyclicker
{
    // A single auto-clicker card's settings. Field names match the INI keys.
    sealed class SlotConfig
    {
        public string Name = "";
        public string Macro = "";
        public string Hotkey = "";
        public int Interval = 50;
        public string Input = "Left Click";
        public string CustomKey = "";
        public string PosMode = "Current Position";
        public int X, Y;
        public string Mode = "Toggle";          // Toggle | Hold
        public string WinTitle = "";
        public bool RestoreCursor;
        public int StopClicks, StopSeconds;
        public int JitterMs, PosJitter;
        // How long each click stays pressed, as a percentage of the interval -
        // so it scales with the click rate instead of being retuned by hand.
        // 0 = instant press-and-release, the original behaviour.
        public int HoldPercent;
        public bool FocusWindow;
        public bool FlickFocus;                 // per-click: focus gate, click, focus back
        public bool HotkeyOff;                  // per-card lock, inverted on disk
        public bool KeepWhileLocked;            // lock blocks the hotkey only
        public bool StopOnInput;                // any real input stops the card
        public bool MacroRelative;              // replay positions relative to
                                                // the window the take recorded
        public string Color = "";               // card tint, by palette name
        public bool Collapsed;                  // rolled up to a single line

        public bool IsMacro { get { return Input == "Macro"; } }
        public bool IsCustomKey { get { return Input == "Custom Key"; } }
        public bool IsFixed { get { return PosMode == "Fixed Position"; } }
        public bool IsGated { get { return WinTitle.Trim().Length > 0; } }

        public SlotConfig Clone()
        {
            return (SlotConfig)MemberwiseClone();
        }

        // The window gate as the user reads it: "ahk_exe game.exe" is how the
        // picker writes it, but the exe alone is what anyone recognises.
        public string GateName()
        {
            string w = WinTitle.Trim();
            if (w.Length == 0) return "";
            if (w.StartsWith("ahk_exe ", StringComparison.OrdinalIgnoreCase))
                w = w.Substring(8).Trim();
            return w.Length > 22 ? w.Substring(0, 21) + "…" : w;
        }

        // The gate name at its tersest, for collapsed cards: no ahk_exe, no .exe
        public string GateShort()
        {
            string w = GateName();
            if (w.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                w = w.Substring(0, w.Length - 4);
            return w;
        }
    }

    static class Ini
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetPrivateProfileStringW(string sec, string key, string def,
                                                   StringBuilder ret, int size, string file);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool WritePrivateProfileStringW(string sec, string key, string val, string file);
        // Writing a whole section in one call rather than key at a time:
        // key-at-a-time is a separate full-file rewrite per key - 18 per card.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool WritePrivateProfileSectionW(string sec, string data, string file);

        public static string Read(string file, string sec, string key, string def)
        {
            var sb = new StringBuilder(1024);
            GetPrivateProfileStringW(sec, key, def, sb, sb.Capacity, file);
            return sb.ToString();
        }

        public static int ReadInt(string file, string sec, string key, int def)
        {
            int v;
            string s = Read(file, sec, key, def.ToString(CultureInfo.InvariantCulture));
            return int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }

        public static bool ReadBool(string file, string sec, string key, bool def)
        {
            return ReadInt(file, sec, key, def ? 1 : 0) != 0;
        }

        public static void Write(string file, string sec, string key, string val)
        {
            WritePrivateProfileStringW(sec, key, val, file);
        }

        // pairs are "Key=Value"; the API wants them NUL-separated and double
        // NUL-terminated, and it replaces the whole section in one write.
        public static void WriteSection(string file, string sec, IEnumerable<string> pairs)
        {
            var sb = new StringBuilder();
            foreach (string p in pairs) { sb.Append(p); sb.Append('\0'); }
            sb.Append('\0');
            WritePrivateProfileSectionW(sec, sb.ToString(), file);
        }

        public static void DeleteSection(string file, string sec)
        {
            WritePrivateProfileStringW(sec, null, null, file);
        }
    }

    // Everything the app persists: the cards, the global hotkeys, the window.
    sealed class AppConfig
    {
        public readonly List<SlotConfig> Slots = new List<SlotConfig>();
        public string StopAllKey = "^!Escape";
        public string KillSwitchKey = "^!k";
        public string RecordKey = "^!F9";
        // Which profile the working set came from - so the dropdown still
        // reads "Cookie Clicker" after a restart instead of sitting blank
        public string CurrentProfile = "";
        public string ThemeMode = "light";      // light | dark | system
        public int WinX = int.MinValue, WinY = int.MinValue, WinW, WinH;

        // Data lives in the user's roaming AppData, so the exe is a single
        // self-contained file that can sit anywhere - Downloads, a USB stick,
        // Program Files - without scattering its settings next to itself or
        // failing where it can't write.
        //
        // POLYCLICKER_DATA overrides the location when set (both pre-rename
        // names still work): it keeps the test harness hermetic, and doubles
        // as a portable mode for anyone who wants everything on the stick
        // after all.
        static string _dir;
        public static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                string overridden = Environment.GetEnvironmentVariable("POLYCLICKER_DATA");
                if (string.IsNullOrEmpty(overridden))
                    overridden = Environment.GetEnvironmentVariable("MULTICLICKER_DATA");
                if (string.IsNullOrEmpty(overridden))
                    overridden = Environment.GetEnvironmentVariable("MULTIAUTOCLICKER_DATA");
                if (!string.IsNullOrEmpty(overridden))
                {
                    Directory.CreateDirectory(overridden);
                    _dir = overridden.TrimEnd('\\');
                    return _dir;
                }
                string d = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Polyclicker");
                Directory.CreateDirectory(d);
                // Newest previous name first, then the older ones, then the
                // ancient beside-the-exe layout; the first folder that has
                // data wins and the rest are skipped
                string appData =
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                MigrateFrom(Path.Combine(appData, "Multiclicker"), d);
                MigrateFrom(Path.Combine(appData, "Multi Auto-Clicker"), d);
                MigrateLegacy(d);
                _dir = d;
                return _dir;
            }
        }

        // The app has been renamed twice - Multi Auto-Clicker, then
        // Multiclicker. The first run copies the newest older folder's data
        // across. Copied, not moved - the old folder stays as a backup until
        // the user deletes it.
        static void MigrateFrom(string src, string dest)
        {
            try
            {
                string oldIni = Path.Combine(src, "AutoClickerProfiles.ini");
                string newIni = Path.Combine(dest, "AutoClickerProfiles.ini");
                if (!System.IO.File.Exists(oldIni) || System.IO.File.Exists(newIni)) return;

                System.IO.File.Copy(oldIni, newIni);
                CopyAll(Path.Combine(src, "Macros"), Path.Combine(dest, "Macros"), "*.macro");
                CopyAll(Path.Combine(src, "Profiles"), Path.Combine(dest, "Profiles"), "*.ini");
            }
            catch { }   // a failed migration just means starting fresh
        }
        public static string File { get { return Path.Combine(Dir, "AutoClickerProfiles.ini"); } }
        public static string MacroDir { get { return Path.Combine(Dir, "Macros"); } }
        public static string ProfileDir { get { return Path.Combine(Dir, "Profiles"); } }

        // Earlier builds kept everything next to the exe. The first run against an empty AppData folder copies that data
        // across, so upgrading never costs anyone their cards, profiles or
        // recordings. Copied rather than moved: the older install keeps working.
        static void MigrateLegacy(string dest)
        {
            try
            {
                string src = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
                string oldIni = Path.Combine(src, "AutoClickerProfiles.ini");
                string newIni = Path.Combine(dest, "AutoClickerProfiles.ini");
                if (!System.IO.File.Exists(oldIni) || System.IO.File.Exists(newIni)) return;

                System.IO.File.Copy(oldIni, newIni);
                CopyAll(Path.Combine(src, "Macros"), Path.Combine(dest, "Macros"), "*.macro");
                CopyAll(Path.Combine(src, "Profiles"), Path.Combine(dest, "Profiles"), "*.ini");
            }
            catch { }   // a failed migration just means starting fresh
        }

        static void CopyAll(string from, string to, string pattern)
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from, pattern))
            {
                string target = Path.Combine(to, Path.GetFileName(f));
                if (!System.IO.File.Exists(target)) System.IO.File.Copy(f, target);
            }
        }

        static readonly string[] ValidInputs =
        {
            "Left Click", "Right Click", "Middle Click", "X1 Button",
            "X2 Button", "Custom Key", "Macro"
        };

        public static AppConfig Load()
        {
            var cfg = new AppConfig();
            string f = File;
            if (!System.IO.File.Exists(f)) return cfg;

            cfg.StopAllKey     = Ini.Read(f, "Global", "StopAll", cfg.StopAllKey);
            cfg.KillSwitchKey  = Ini.Read(f, "Global", "KillSwitch", cfg.KillSwitchKey);
            cfg.RecordKey      = Ini.Read(f, "Global", "Record", cfg.RecordKey);
            cfg.CurrentProfile = Ini.Read(f, "Global", "Profile", "");
            // Theme= is the setting; older files only carry the Dark= bool
            string tm = Ini.Read(f, "Global", "Theme", "").Trim().ToLowerInvariant();
            cfg.ThemeMode = (tm == "light" || tm == "dark" || tm == "system") ? tm
                          : Ini.ReadBool(f, "Global", "Dark", false) ? "dark" : "light";
            cfg.WinX = Ini.ReadInt(f, "Window", "X", int.MinValue);
            cfg.WinY = Ini.ReadInt(f, "Window", "Y", int.MinValue);
            cfg.WinW = Ini.ReadInt(f, "Window", "W", 0);
            cfg.WinH = Ini.ReadInt(f, "Window", "H", 0);

            int count = Ini.ReadInt(f, "Meta", "Count", 0);
            for (int i = 1; i <= count; i++)
                cfg.Slots.Add(ReadSlot(f, "Slot" + i));
            return cfg;
        }

        public static SlotConfig ReadSlot(string file, string sec)
        {
            var s = new SlotConfig();
            s.Name      = Ini.Read(file, sec, "Name", "");
            s.Macro     = Ini.Read(file, sec, "Macro", "");
            s.Hotkey    = Ini.Read(file, sec, "Hotkey", "");
            s.Interval  = Math.Max(1, Ini.ReadInt(file, sec, "Interval", 50));
            s.Input     = Ini.Read(file, sec, "Input", "Left Click");
            s.CustomKey = Ini.Read(file, sec, "CustomKey", "");
            s.PosMode   = Ini.Read(file, sec, "PosMode", "Current Position");
            s.X         = Ini.ReadInt(file, sec, "X", 0);
            s.Y         = Ini.ReadInt(file, sec, "Y", 0);
            s.Mode      = Ini.Read(file, sec, "Mode", "Toggle") == "Hold" ? "Hold" : "Toggle";
            s.WinTitle  = Ini.Read(file, sec, "WinTitle", "");
            s.RestoreCursor = Ini.ReadBool(file, sec, "RestoreCursor", false);
            s.StopClicks    = Ini.ReadInt(file, sec, "StopClicks", 0);
            s.StopSeconds   = Ini.ReadInt(file, sec, "StopSeconds", 0);
            s.JitterMs      = Math.Max(0, Ini.ReadInt(file, sec, "JitterMs", 0));
            s.PosJitter     = Math.Max(0, Ini.ReadInt(file, sec, "PosJitter", 0));
            // 90% ceiling: a press that fills its whole interval never lifts,
            // so the next beat has nothing to press
            s.HoldPercent   = Math.Max(0, Math.Min(90, Ini.ReadInt(file, sec, "HoldPercent", 0)));
            s.FocusWindow   = Ini.ReadBool(file, sec, "FocusWindow", false);
            s.FlickFocus    = Ini.ReadBool(file, sec, "FlickFocus", false);
            s.HotkeyOff     = Ini.ReadBool(file, sec, "HotkeyOff", false);
            s.KeepWhileLocked = Ini.ReadBool(file, sec, "KeepWhileLocked", false);
            s.StopOnInput   = Ini.ReadBool(file, sec, "StopOnInput", false);
            s.MacroRelative = Ini.ReadBool(file, sec, "MacroRelative", false);
            s.Color         = Ini.Read(file, sec, "Color", "").Trim().ToLowerInvariant();
            s.Collapsed     = Ini.ReadBool(file, sec, "Collapsed", false);

            // "Recorded Macro" was this entry's name before the rename; without
            // the migration every saved macro card fails the check below and
            // silently comes back as a plain left click.
            if (s.Input == "Recorded Macro") s.Input = "Macro";
            if (s.PosMode == "Current Mouse Position") s.PosMode = "Current Position";
            if (s.PosMode != "Current Position" && s.PosMode != "Fixed Position")
                s.PosMode = "Current Position";
            bool ok = false;
            foreach (string v in ValidInputs) if (v == s.Input) { ok = true; break; }
            if (!ok) s.Input = "Left Click";
            return s;
        }

        // One composed write. The Win32 profile API rewrites the whole file on
        // every section it touches, so saving N cards section-by-section is N
        // full-file rewrites - and the app saves on every keystroke. With 20
        // cards that alone makes typing feel laggy. Reading still goes through
        // GetPrivateProfileString, which is perfectly happy with this output.
        public void Save()
        {
            var sb = new StringBuilder();
            AppendSlots(sb);
            sb.AppendLine("[Global]");
            sb.AppendLine("StopAll=" + StopAllKey);
            sb.AppendLine("KillSwitch=" + KillSwitchKey);
            sb.AppendLine("Record=" + RecordKey);
            sb.AppendLine("Profile=" + CurrentProfile);
            sb.AppendLine("Theme=" + ThemeMode);
            // Dark= kept as the resolved value, so an older build reading this
            // file still comes up in the right colors
            sb.AppendLine("Dark=" + (Theme.ResolveDark(ThemeMode) ? "1" : "0"));
            if (WinW > 0)
            {
                sb.AppendLine("[Window]");
                sb.AppendLine("X=" + WinX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Y=" + WinY.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("W=" + WinW.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("H=" + WinH.ToString(CultureInfo.InvariantCulture));
            }
            try { WriteAtomic(File, sb.ToString()); } catch { }
        }

        // Shared by the working file and by named profiles, so the two can't
        // drift apart as fields are added.
        public void WriteSlotsTo(string file)
        {
            var sb = new StringBuilder();
            AppendSlots(sb);
            WriteAtomic(file, sb.ToString());
        }

        // Straight WriteAllText truncates first and writes second, so a crash
        // or power cut in between leaves an empty config - every card gone.
        // Writing beside and swapping in means the file is always either the
        // old version or the new one, never half of one.
        //
        // UTF-16 with its BOM, deliberately: every read goes through
        // GetPrivateProfileStringW, which treats a BOM-less file as ANSI.
        // The default WriteAllText (UTF-8, no BOM) round-tripped any
        // non-ASCII card name or window gate into mojibake that compounded
        // on each save. UTF-16 is the one Unicode form the profile API
        // recognises.
        static void WriteAtomic(string file, string text)
        {
            string tmp = file + ".tmp";
            System.IO.File.WriteAllText(tmp, text, Encoding.Unicode);
            if (System.IO.File.Exists(file)) System.IO.File.Replace(tmp, file, null);
            else System.IO.File.Move(tmp, file);
        }

        void AppendSlots(StringBuilder sb) { AppendSlots(sb, Slots); }

        // What "these auto-clickers" are, as text. Comparing this against the
        // same rendering of a profile on disk answers "is there unsaved work
        // here?" without keeping a shadow copy in sync with every edit.
        public static string Fingerprint(IList<SlotConfig> slots)
        {
            var sb = new StringBuilder();
            AppendSlots(sb, slots);
            return sb.ToString();
        }

        static void AppendSlots(StringBuilder sb, IList<SlotConfig> Slots)
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                SlotConfig s = Slots[i];
                sb.AppendLine("[Slot" + (i + 1) + "]");
                sb.AppendLine("Macro=" + s.Macro);
                sb.AppendLine("Name=" + s.Name);
                sb.AppendLine("Hotkey=" + s.Hotkey);
                sb.AppendLine("Interval=" + s.Interval.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Input=" + s.Input);
                sb.AppendLine("CustomKey=" + s.CustomKey);
                sb.AppendLine("PosMode=" + s.PosMode);
                sb.AppendLine("X=" + s.X.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Y=" + s.Y.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("Mode=" + s.Mode);
                sb.AppendLine("WinTitle=" + s.WinTitle);
                sb.AppendLine("RestoreCursor=" + (s.RestoreCursor ? "1" : "0"));
                sb.AppendLine("StopClicks=" + s.StopClicks.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("StopSeconds=" + s.StopSeconds.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("JitterMs=" + s.JitterMs.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("PosJitter=" + s.PosJitter.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HoldPercent=" + s.HoldPercent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("FocusWindow=" + (s.FocusWindow ? "1" : "0"));
                sb.AppendLine("FlickFocus=" + (s.FlickFocus ? "1" : "0"));
                sb.AppendLine("HotkeyOff=" + (s.HotkeyOff ? "1" : "0"));
                sb.AppendLine("KeepWhileLocked=" + (s.KeepWhileLocked ? "1" : "0"));
                sb.AppendLine("StopOnInput=" + (s.StopOnInput ? "1" : "0"));
                sb.AppendLine("MacroRelative=" + (s.MacroRelative ? "1" : "0"));
                sb.AppendLine("Color=" + s.Color);
                sb.AppendLine("Collapsed=" + (s.Collapsed ? "1" : "0"));
            }
            sb.AppendLine("[Meta]");
            sb.AppendLine("Count=" + Slots.Count.ToString(CultureInfo.InvariantCulture));
        }

        public static List<SlotConfig> ReadSlotsFrom(string file)
        {
            var list = new List<SlotConfig>();
            int count = Ini.ReadInt(file, "Meta", "Count", 0);
            for (int i = 1; i <= count; i++)
                list.Add(ReadSlot(file, "Slot" + i));
            return list;
        }
    }
}
