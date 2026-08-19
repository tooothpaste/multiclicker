// ===========================================================================
//  Hotkeys - a low-level keyboard hook, not RegisterHotKey
// ---------------------------------------------------------------------------
//  RegisterHotKey would be less code, but it always swallows the key and can
//  never be conditional. This app needs the opposite: a card tied to one
//  window must leave its key alone everywhere else, so pressing "2" outside
//  the game still types a 2. Only a hook can decide per press, and only a hook
//  can see a bare "2" without stealing it from every other program.
//
//  The callback runs on the UI thread and sits in the path of every keystroke
//  on the machine, so it does the smallest possible thing: work out whether
//  the press is ours, and if it is, hand the actual work to the UI thread
//  asynchronously and swallow the key.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Polyclicker
{
    // One parsed hotkey: modifiers plus a virtual-key code.
    struct HotkeyCombo
    {
        public ushort Vk;
        public bool Ctrl, Alt, Shift, Win;
        public bool IsSet { get { return Vk != 0; } }

        public bool Matches(ushort vk, bool ctrl, bool alt, bool shift, bool win)
        {
            return Vk == vk && Ctrl == ctrl && Alt == alt && Shift == shift && Win == win;
        }

        // As the user reads it: "Ctrl+Alt+F9"
        public override string ToString()
        {
            if (!IsSet) return "";
            string s = "";
            if (Ctrl) s += "Ctrl+";
            if (Alt) s += "Alt+";
            if (Shift) s += "Shift+";
            if (Win) s += "Win+";
            return s + HotkeyParser.VkToName(Vk);
        }
    }

    static class HotkeyParser
    {
        // The INI stores hotkeys in a compact prefix notation:
        // ^ Ctrl, ! Alt, + Shift, # Win, then the key name.
        public static HotkeyCombo Parse(string hk)
        {
            var c = new HotkeyCombo();
            if (hk == null) return c;
            hk = hk.Trim();
            int i = 0;
            while (i < hk.Length)
            {
                char ch = hk[i];
                if (ch == '^') { c.Ctrl = true; i++; }
                else if (ch == '!') { c.Alt = true; i++; }
                else if (ch == '+') { c.Shift = true; i++; }
                else if (ch == '#') { c.Win = true; i++; }
                else if (ch == '<' || ch == '>' || ch == '*' || ch == '~' || ch == '$') i++;
                else break;
            }
            c.Vk = KeyNameToVk(hk.Substring(i));
            return c;
        }

        public static string ToSpec(HotkeyCombo c)
        {
            if (!c.IsSet) return "";
            string s = "";
            if (c.Ctrl) s += "^";
            if (c.Alt) s += "!";
            if (c.Shift) s += "+";
            if (c.Win) s += "#";
            return s + VkToName(c.Vk);
        }

        public static ushort KeyNameToVk(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            name = name.Trim();
            if (name.Length == 1)
            {
                char ch = char.ToUpperInvariant(name[0]);
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')) return (ushort)ch;
            }
            switch (name.ToLowerInvariant())
            {
                case "escape": case "esc":   return 0x1B;
                case "space":                return 0x20;
                case "tab":                  return 0x09;
                case "enter": case "return": return 0x0D;
                case "backspace": case "bs": return 0x08;
                case "delete": case "del":   return 0x2E;
                case "insert": case "ins":   return 0x2D;
                case "home":                 return 0x24;
                case "end":                  return 0x23;
                case "pgup":                 return 0x21;
                case "pgdn":                 return 0x22;
                case "up":                   return 0x26;
                case "down":                 return 0x28;
                case "left":                 return 0x25;
                case "right":                return 0x27;
                case "capslock":             return 0x14;
                case "numlock":              return 0x90;
                case "printscreen":          return 0x2C;
                case "pause":                return 0x13;
                // Bare modifiers - a Custom Key card can hold Shift or Ctrl
                // down on its own
                case "shift":                return 0x10;
                case "ctrl": case "control": return 0x11;
                case "alt":                  return 0x12;
                case "win": case "lwin":     return 0x5B;
                case "rwin":                 return 0x5C;
            }
            if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f'))
            {
                int n;
                if (int.TryParse(name.Substring(1), out n) && n >= 1 && n <= 24)
                    return (ushort)(0x70 + n - 1);
            }
            // Numpad and the odd punctuation key, by their spelled-out names
            switch (name.ToLowerInvariant())
            {
                case "numpad0": return 0x60; case "numpad1": return 0x61;
                case "numpad2": return 0x62; case "numpad3": return 0x63;
                case "numpad4": return 0x64; case "numpad5": return 0x65;
                case "numpad6": return 0x66; case "numpad7": return 0x67;
                case "numpad8": return 0x68; case "numpad9": return 0x69;
                case "numpadmult": return 0x6A; case "numpadadd": return 0x6B;
                case "numpadsub": return 0x6D; case "numpaddiv": return 0x6F;
                case "numpaddot": return 0x6E;
            }
            return 0;
        }

        public static string VkToName(ushort vk)
        {
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9'))
                return ((char)vk).ToString();
            switch (vk)
            {
                case 0x1B: return "Esc";
                case 0x20: return "Space";
                case 0x09: return "Tab";
                case 0x0D: return "Enter";
                case 0x08: return "Backspace";
                case 0x2E: return "Delete";
                case 0x2D: return "Insert";
                case 0x24: return "Home";
                case 0x23: return "End";
                case 0x21: return "PgUp";
                case 0x22: return "PgDn";
                case 0x26: return "Up";
                case 0x28: return "Down";
                case 0x25: return "Left";
                case 0x27: return "Right";
                case 0x10: return "Shift";
                case 0x11: return "Ctrl";
                case 0x12: return "Alt";
                case 0x5B: return "Win";
                case 0x5C: return "RWin";
            }
            if (vk >= 0x60 && vk <= 0x69) return "Numpad" + (vk - 0x60);
            return vk == 0 ? "" : "VK" + vk.ToString("X2");
        }
    }

    // Raised for every key the machine sees. Set Consume to swallow it.
    sealed class HotkeyEventArgs : EventArgs
    {
        public ushort Vk;
        public bool Ctrl, Alt, Shift, Win;
        public bool IsDown;
        // A typematic repeat of a key already down. Handlers must not ACT on
        // these - acting restarts a Hold-mode pacer on every repeat, so a
        // held hotkey never clicks - but they still see them, because a
        // repeat of a key we own has to be swallowed like the first press or
        // it leaks into the focused app mid-hold.
        public bool Repeat;
        public bool Consume;
    }

    static class Hotkeys
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookExW(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const int WM_KEYUP = 0x0101, WM_SYSKEYUP = 0x0105;

        static IntPtr _hook;
        static HookProc _proc;      // held so the delegate isn't collected
        public static event EventHandler<HotkeyEventArgs> Key;

        public static void Install()
        {
            if (_hook != IntPtr.Zero) return;
            _proc = Callback;
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        }

        public static void Uninstall()
        {
            if (_hook == IntPtr.Zero) return;
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        // Windows silently removes a low-level hook whose callback doesn't
        // answer within its timeout - one slow moment on the UI thread while a
        // key arrives and every hotkey is dead with no error anywhere. There
        // is no API to ask whether the hook still lives, so the watchdog
        // re-registers it outright; SetWindowsHookEx is cheap and the gap
        // between the two calls is microseconds.
        public static void Reinstall()
        {
            if (_proc == null) return;          // never installed yet
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        }

        static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // Modifiers the USER's own hand is holding, tracked from the real
        // (unstamped) events this hook sees. GetAsyncKeyState alone can't
        // answer "whose Shift is that?" - it merges the user's Shift with one
        // a custom-key card is synthesising. Masking every Shift while the
        // engine held one broke the opposite case: a card whose hotkey is
        // Shift+Z and whose custom key is Shift read the user's stop press as
        // plain Z whenever it landed mid-hold.
        static int userMods;                    // 1 shift, 2 ctrl, 4 alt, 8 win

        static int UserModBit(uint vk)
        {
            switch (vk)
            {
                case 0x10: case 0xA0: case 0xA1: return 1;
                case 0x11: case 0xA2: case 0xA3: return 2;
                case 0x12: case 0xA4: case 0xA5: return 4;
                case 0x5B: case 0x5C: return 8;
                default: return 0;
            }
        }

        // Which keys the user's hand currently holds, from this hook's own
        // events - KBDLLHOOKSTRUCT has no previous-state bit, so autorepeat
        // is invisible without it. Self-synthesized keys never get here (the
        // SelfMark filter is upstream), so a custom-key card hammering "2"
        // can't make the user's own 2 look held.
        static readonly bool[] keyIsDown = new bool[256];

        static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                // Our own synthetic keys must never trigger our own hotkeys -
                // a custom-key clicker firing "2" would otherwise toggle the
                // card that fires it.
                if (k.dwExtraInfo != Engine.SelfMark)
                {
                    int m = wParam.ToInt32();
                    bool down = (m == WM_KEYDOWN || m == WM_SYSKEYDOWN);
                    bool up = (m == WM_KEYUP || m == WM_SYSKEYUP);
                    if (down || up)
                    {
                        int mb = UserModBit(k.vkCode);
                        if (mb != 0)
                        {
                            if (down) userMods |= mb;
                            else userMods &= ~mb;
                        }
                        int vki = (int)(k.vkCode & 0xFF);
                        bool repeat = down && keyIsDown[vki];
                        keyIsDown[vki] = down;
                        var e = new HotkeyEventArgs();
                        e.Vk = (ushort)k.vkCode;
                        e.IsDown = down;
                        e.Repeat = repeat;
                        // A modifier is part of what the user typed if THEIR
                        // hand holds it - the tracked bit, from their own
                        // events - or if it's physically down and the engine
                        // isn't the one holding it (covers a hand that was
                        // already down before the hook existed to see it).
                        //
                        // The tracked bit must stand ON ITS OWN, not be
                        // checked against GetAsyncKeyState: key state is one
                        // bit per key with no notion of whose. A card whose
                        // custom key is Shift releases its synthetic Shift
                        // and the state reads "up" WHILE the user's finger is
                        // still down - their Shift+Z stop press then read as
                        // plain Z whenever it landed in that window, which at
                        // their card's 40% hold was most presses.
                        e.Ctrl = (userMods & 2) != 0 || (Held(0x11) && !Engine.SelfCtrl);
                        e.Alt = (userMods & 4) != 0 || (Held(0x12) && !Engine.SelfAlt);
                        e.Shift = (userMods & 1) != 0 || (Held(0x10) && !Engine.SelfShift);
                        e.Win = (userMods & 8) != 0 || ((Held(0x5B) || Held(0x5C)) && !Engine.SelfWin);
                        var h = Key;
                        if (h != null)
                        {
                            try { h(null, e); } catch { }
                            if (e.Consume) return new IntPtr(1);
                        }
                    }
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }
    }
}
