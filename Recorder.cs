// ===========================================================================
//  Recorder - captures a timeline of mouse and key events with LL hooks
// ---------------------------------------------------------------------------
//  The hooks live on their own thread with a
//  message pump and exist only while a take is running - the rest of the time
//  the app costs the input path nothing. (The app's hotkey hook is separate
//  and much lighter: it only inspects keys, this one records everything.)
//
//  Everything the engine synthesises is stamped with SelfMark and skipped, so
//  recording while a clicker runs can't capture that clicker and play it back
//  on top of itself.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace Polyclicker
{
    static class Recorder
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookExW(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint lo, uint hi);
        [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG m, IntPtr h, uint lo, uint hi, uint remove);
        [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long v);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long v);

        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const uint WM_QUIT = 0x0012;

        // Movement is sampled rather than taken raw: the mouse can generate
        // hundreds of events a second and replaying every one adds nothing a
        // person can see, while bloating the file.
        const double MoveMinGapMs = 8.0;

        static IntPtr _kbHook, _msHook;
        static HookProc _kbProc, _msProc;    // held so the GC can't collect them
        static Thread _thread;
        static uint _threadId;
        static long _freq;

        public static volatile bool Recording;
        static string _path;
        static readonly List<Ev> _events = new List<Ev>();
        static long _start;
        static bool _started;                // clock begins at the first kept event
        static RECT _window;                 // foreground window when the take began
        static bool _windowValid;
        static long _lastMoveTick;
        static int _lastX = int.MinValue, _lastY = int.MinValue;

        // Two tiers of hotkey filtering. The main key (F9) and Escape only ever
        // mean start/stop, so they are dropped at the source. The chord's
        // MODIFIERS are different: dropping every Ctrl and Alt for the whole
        // take silently rewrites any recorded Ctrl+click or Alt+combo into
        // its bare key. They are recorded normally, and only the presses
        // that actually bracket the take are trimmed off afterwards.
        static readonly HashSet<int> _ignoreAlways = new HashSet<int>();
        static readonly HashSet<int> _chordVks = new HashSet<int>();
        // Keys currently down, so autorepeat doesn't spam the take with
        // duplicate downs while a key is held
        static readonly HashSet<int> _kbDown = new HashSet<int>();

        public static int EventCount { get { lock (_events) return _events.Count; } }
        public static string LastPath { get { return _path; } }

        public static void Start(string path, IEnumerable<int> ignoreAlways, IEnumerable<int> chordVks)
        {
            if (Recording) return;
            if (_freq == 0) QueryPerformanceFrequency(out _freq);
            lock (_events)
            {
                _events.Clear();
                _path = path;
                _started = false;
                _lastX = int.MinValue;
                _lastY = int.MinValue;
                _kbDown.Clear();
                _ignoreAlways.Clear();
                foreach (int vk in ignoreAlways) if (vk > 0) _ignoreAlways.Add(vk);
                _chordVks.Clear();
                foreach (int vk in chordVks) if (vk > 0) _chordVks.Add(vk);
                Recording = true;
            }
            StartHookThread();
        }

        // Returns the number of events kept, or -1 when the take was empty and
        // nothing was written.
        public static int Stop(bool discard)
        {
            if (!Recording) return -1;
            Recording = false;
            StopHookThread();
            if (discard) return -1;

            List<Ev> copy;
            string path;
            lock (_events)
            {
                copy = new List<Ev>(_events);
                path = _path;
            }
            // The stop press: trailing chord-modifier DOWNS (Ctrl, Alt held to
            // reach the record key - the key itself never records). Downs only:
            // a trailing UP belongs to the user releasing their own combo.
            while (copy.Count > 0)
            {
                Ev last = copy[copy.Count - 1];
                if (last.Type == 3 && _chordVks.Contains(last.A)) copy.RemoveAt(copy.Count - 1);
                else break;
            }
            // The start press's releases: chord-key UPs whose DOWN happened
            // before the take began. A user's own mid-take Ctrl has its down
            // recorded, so it survives this untouched.
            var seenDown = new HashSet<int>();
            for (int i = 0; i < copy.Count; )
            {
                Ev e = copy[i];
                if (e.Type == 3) { seenDown.Add(e.A); i++; }
                else if (e.Type == 4 && _chordVks.Contains(e.A) && !seenDown.Contains(e.A))
                    copy.RemoveAt(i);
                else i++;
            }
            if (copy.Count == 0) return -1;
            try
            {
                MacroFile.Save(path, copy, _freq, _windowValid,
                               _window.L, _window.T, _window.R - _window.L, _window.B - _window.T);
            }
            catch { return -1; }
            return copy.Count;
        }

        static void StartHookThread()
        {
            if (_thread != null && _thread.IsAlive) return;
            // The caller waits until the thread has published its id AND owns
            // a message queue. Without that, a stop landing before the thread
            // ran saw _threadId == 0, posted no WM_QUIT, and orphaned a
            // thread pumping two leaked machine-wide hooks forever.
            var ready = new ManualResetEvent(false);
            _thread = new Thread(delegate()
            {
                _threadId = GetCurrentThreadId();
                MSG pk;
                // A thread has no message queue until it asks for one; force
                // it into existence so a WM_QUIT posted right now can land
                PeekMessageW(out pk, IntPtr.Zero, 0, 0, 0);
                ready.Set();
                _kbProc = KeyboardHook;
                _msProc = MouseHook;
                _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, IntPtr.Zero, 0);
                _msHook = SetWindowsHookExW(WH_MOUSE_LL, _msProc, IntPtr.Zero, 0);

                MSG msg;
                while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0) { }

                if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
                if (_msHook != IntPtr.Zero) UnhookWindowsHookEx(_msHook);
                _kbHook = _msHook = IntPtr.Zero;
            });
            _thread.IsBackground = true;
            _thread.Start();
            ready.WaitOne(2000);
        }

        static void StopHookThread()
        {
            Thread t = _thread;
            if (_threadId != 0) PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _threadId = 0;
            _thread = null;
            // Joined, not abandoned: a quick stop-then-start otherwise raced
            // this thread's shutdown, which unhooked whatever the NEW thread
            // had just written into the shared _kbHook/_msHook statics - the
            // fresh take recorded nothing and the old hooks leaked.
            if (t != null && t.IsAlive) t.Join(2000);
        }

        static void Note(byte type, int a, int b)
        {
            lock (_events)
            {
                if (!Recording) return;
                // The record key and Escape only ever mean start/stop
                if (type >= 3 && _ignoreAlways.Contains(a)) return;
                // Autorepeat: a held key streams downs; one is enough
                if (type == 3) { if (!_kbDown.Add(a)) return; }
                else if (type == 4) _kbDown.Remove(a);

                long now;
                QueryPerformanceCounter(out now);
                if (!_started)
                {
                    // Clock starts at the first event actually kept, so a macro
                    // never opens with dead air from the operator's reaction
                    _started = true;
                    _start = now;
                    // Remember where this take happened, so playback can one
                    // day follow the window and warn when its size has changed
                    IntPtr fg = GetForegroundWindow();
                    _windowValid = fg != IntPtr.Zero && GetWindowRect(fg, out _window);
                }
                var e = new Ev();
                e.Off = now - _start;
                e.Type = type;
                e.A = a;
                e.B = b;
                _events.Add(e);
            }
        }

        static IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Recording)
            {
                var k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                if (k.dwExtraInfo != Engine.SelfMark)
                {
                    int m = wParam.ToInt32();
                    int ext = (k.flags & 0x01) != 0 ? 1 : 0;
                    if (m == 0x0100 || m == 0x0104) Note(3, (int)k.vkCode, ext);
                    else if (m == 0x0101 || m == 0x0105) Note(4, (int)k.vkCode, ext);
                }
            }
            return CallNextHookEx(_kbHook, code, wParam, lParam);
        }

        static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && Recording)
            {
                var m = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if (m.dwExtraInfo != Engine.SelfMark && m.dwExtraInfo != Engine.SelfMarkPinned)
                {
                    switch (wParam.ToInt32())
                    {
                        case 0x0200:   // WM_MOUSEMOVE
                        {
                            long now;
                            QueryPerformanceCounter(out now);
                            double sinceMs = (now - _lastMoveTick) * 1000.0 / _freq;
                            if ((m.pt.X != _lastX || m.pt.Y != _lastY) && sinceMs >= MoveMinGapMs)
                            {
                                _lastMoveTick = now;
                                _lastX = m.pt.X; _lastY = m.pt.Y;
                                Note(0, m.pt.X, m.pt.Y);
                            }
                            break;
                        }
                        case 0x0201: Note(0, m.pt.X, m.pt.Y); Note(1, 0, 0); break;  // LDOWN
                        case 0x0202: Note(2, 0, 0); break;
                        case 0x0204: Note(0, m.pt.X, m.pt.Y); Note(1, 1, 0); break;  // RDOWN
                        case 0x0205: Note(2, 1, 0); break;
                        case 0x0207: Note(0, m.pt.X, m.pt.Y); Note(1, 2, 0); break;  // MDOWN
                        case 0x0208: Note(2, 2, 0); break;
                        case 0x020B: Note(0, m.pt.X, m.pt.Y);
                                     Note(1, (m.mouseData >> 16) == 1 ? 3 : 4, 0); break; // XDOWN
                        case 0x020C: Note(2, (int)(m.mouseData >> 16) == 1 ? 3 : 4, 0); break;
                        case 0x020A: Note(5, (short)(m.mouseData >> 16), 0); break;   // WHEEL
                    }
                }
            }
            return CallNextHookEx(_msHook, code, wParam, lParam);
        }
    }
}
