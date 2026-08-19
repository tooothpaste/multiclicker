// ===========================================================================
//  Engine - the click pacer
// ---------------------------------------------------------------------------
//  A thread per running slot paced off QueryPerformanceCounter, a
//  high-resolution waitable timer so pacing costs almost no CPU, and one
//  lock around the pointer so simultaneous slots can't interleave.
//
//  Measured: a 50 ms interval paces at 50.01 ms typical with no
//  sub-millisecond doubling, against 62 ms typical and 20% doubles when a
//  message-loop timer does the pacing.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Polyclicker
{
    static class Engine
    {
        // --- Win32 ----------------------------------------------------------
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public uint type; public InputUnion U; }
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint SendInput(uint n, IntPtr p, int cb);
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        // --- background clicking (keep running when switched away) ----------
        // Posting the click straight to the window is the only way to click
        // something that isn't in front WITHOUT taking it over: the pointer
        // never moves, focus never changes, and the user keeps working.
        // Synthetic input can't do this - it always goes to the foreground -
        // and focus-flicking per beat thrashed the user's window out of the
        // foreground. The deepest child window at the point is the one that
        // gets the message: Chromium-based games (Cookie Clicker's Steam
        // build) render into a child HWND, and a message to the top-level
        // frame would never reach the page.
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);

        const uint WM_MOUSEMOVE_M = 0x0200;
        const uint WM_LBUTTONDOWN_M = 0x0201, WM_LBUTTONUP_M = 0x0202;
        const uint WM_RBUTTONDOWN_M = 0x0204, WM_RBUTTONUP_M = 0x0205;
        const uint WM_MBUTTONDOWN_M = 0x0207, WM_MBUTTONUP_M = 0x0208;
        const uint WM_XBUTTONDOWN_M = 0x020B, WM_XBUTTONUP_M = 0x020C;
        const int MK_LBUTTON = 0x0001, MK_RBUTTON = 0x0002, MK_MBUTTON = 0x0010,
                  MK_XBUTTON1 = 0x0020, MK_XBUTTON2 = 0x0040;

        static bool PostClick(Slot s, int x, int y)
        {
            var pt = new POINT(); pt.X = x; pt.Y = y;
            IntPtr target = WindowFromPoint(pt);        // deepest child at the point
            if (target == IntPtr.Zero) return false;
            // It must belong to the gate window's tree, or the point is over
            // something else entirely (another app in front of the game)
            if (RootOf(target) != s.Gate) target = s.Gate;

            var cp = pt;
            if (!ScreenToClient(target, ref cp)) return false;
            IntPtr lp = (IntPtr)((cp.Y << 16) | (cp.X & 0xFFFF));

            // Every input type BuildInput handles has a case here too - the
            // X buttons ride in the wParam's HIGH word (on the up as well),
            // unlike the three main buttons, so they get whole wParams rather
            // than sharing the mk-only shape.
            uint down, up; IntPtr wDown, wUp;
            switch (s.Cfg.Input)
            {
                case "Right Click":  down = WM_RBUTTONDOWN_M; up = WM_RBUTTONUP_M;
                                     wDown = (IntPtr)MK_RBUTTON; wUp = IntPtr.Zero; break;
                case "Middle Click": down = WM_MBUTTONDOWN_M; up = WM_MBUTTONUP_M;
                                     wDown = (IntPtr)MK_MBUTTON; wUp = IntPtr.Zero; break;
                case "X1 Button":    down = WM_XBUTTONDOWN_M; up = WM_XBUTTONUP_M;
                                     wDown = (IntPtr)(MK_XBUTTON1 | (1 << 16)); wUp = (IntPtr)(1 << 16); break;
                case "X2 Button":    down = WM_XBUTTONDOWN_M; up = WM_XBUTTONUP_M;
                                     wDown = (IntPtr)(MK_XBUTTON2 | (2 << 16)); wUp = (IntPtr)(2 << 16); break;
                default:             down = WM_LBUTTONDOWN_M; up = WM_LBUTTONUP_M;
                                     wDown = (IntPtr)MK_LBUTTON; wUp = IntPtr.Zero; break;
            }
            // A move first: hover-driven UIs (and Cookie Clicker's big cookie)
            // want the pointer "over" the target before the press registers
            PostMessageW(target, WM_MOUSEMOVE_M, IntPtr.Zero, lp);
            PostMessageW(target, down, wDown, lp);
            if (s.HoldTicks > 0)
            {
                // Same press duration as a foreground click, so the card
                // behaves identically whether or not the user switched away
                long start, now;
                QueryPerformanceCounter(out start);
                while (s.Run)
                {
                    QueryPerformanceCounter(out now);
                    if (now - start >= s.HoldTicks) break;
                    Thread.Sleep(1);
                }
            }
            return PostMessageW(target, up, wUp, lp);
        }

        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
        static IntPtr RootOf(IntPtr h) { return h == IntPtr.Zero ? h : GetAncestor(h, 2); }

        // --- conversion observer --------------------------------------------
        // A low-level mouse hook on its own pumping thread. Its only job is
        // counting OUR button-ups (SelfMark-stamped) as the input stack
        // CONVERTS them - the ground truth the restore logic synchronizes on.
        // GetAsyncKeyState's latch can't say WHICH click set it: a
        // late-converting previous beat vouches for the current one, and its
        // click lands on the user's hand.
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int X, Y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookExW(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint lo, uint hi);

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205,
                  WM_MBUTTONUP = 0x0208, WM_XBUTTONUP = 0x020C;
        static IntPtr obsHook;
        static HookProc obsProc;
        static Thread obsThread;
        // Bitmask of REAL mouse buttons the user is physically holding, kept
        // by the observer. A fixed-position beat defers while any is down:
        // teleporting the pointer mid-press stole the user's button-up to the
        // click spot, so the thing under their hand never got its click.
        static volatile int userButtons;
        // How many pinned (fixed-position, mouse) slots are running. At zero,
        // ANY pinned event surfacing at conversion is a stop-time straggler:
        // its move part would jump the parked-no-more pointer back to the
        // click spot with nothing left running to bring it home - the
        // "mouse keeps jumping after I turn it off" report. Discard them.
        static int fixedRunning;                // Interlocked


        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204,
                  WM_MBUTTONDOWN = 0x0207, WM_XBUTTONDOWN = 0x020B;

        static bool vetoPairOpen;               // hook thread only

        // Set when one of our fixed clicks converts and puts the cursor on its
        // spot; cleared by the restore that takes it off again. While it is
        // set the cursor is ours, not the user's, and home must not be read.
        //
        // This is what a distrust radius could only approximate. The radius
        // has to be wide enough to cover how far a hand can move while the
        // pointer is parked - and a hand that moves further than that during
        // one slow beat had its drifted position adopted as home. The pointer
        // then really was there, so it stayed: the rare, permanent "it just
        // disappears". Knowing when the cursor is parked needs no radius.
        static volatile bool Parked;
        static long ParkedSince;

        // --- stop on input --------------------------------------------------
        // A card can ask to stop the moment the user does anything real: a
        // button, a key, or moving the pointer more than a nudge. Armed only
        // while some running slot wants it, so the per-move cost is one flag
        // read for everyone else.
        const int WM_MOUSEMOVE = 0x0200;
        static readonly int OffX = (int)Marshal.OffsetOf(typeof(MSLLHOOKSTRUCT), "X");
        static readonly int OffY = (int)Marshal.OffsetOf(typeof(MSLLHOOKSTRUCT), "Y");
        static readonly int OffExtra = (int)Marshal.OffsetOf(typeof(MSLLHOOKSTRUCT), "dwExtraInfo");
        const int StopMoveGrace = 15;           // px a pointer may wander free
        static volatile bool stopInputArmed;
        static long siArmedTick;                // settle: the starting press itself
        static long siAnchor = long.MinValue;   // hook thread only
        static long siAnchorTick;               //   "

        // Recomputed on every start and stop, next to the spot list.
        static void ArmStopOnInput()
        {
            bool want = false;
            lock (Running)
                foreach (Slot s in Running.Values)
                    if (s.Cfg.StopOnInput) { want = true; break; }
            if (want && !stopInputArmed)
            {
                QueryPerformanceCounter(out siArmedTick);
                siAnchor = long.MinValue;       // re-anchor at the next move
            }
            stopInputArmed = want;
        }

        // Real input arrived: stop every running card that asked for this.
        // Not StopAll - cards that didn't opt in keep going.
        static void StopForInput()
        {
            // The press that STARTED the card is not the user interrupting it,
            // and neither is the hand still settling from reaching the hotkey
            long now;
            QueryPerformanceCounter(out now);
            if (now - Interlocked.Read(ref siArmedTick) < Freq * 3 / 10) return;
            stopInputArmed = false;
            // The actual stopping is handed off: this runs inside the LL
            // mouse hook callback, and Stop -> ReturnCursor takes PointerLock,
            // which a pace thread can hold across its SendInput retry loop.
            // Blocking the hook on that lock stalls every mouse event on the
            // machine and courts the OS hook timeout - and the stop is
            // signal-only anyway, so a thread hop's latency is invisible.
            ThreadPool.QueueUserWorkItem(delegate
            {
                var ids = new List<int>();
                lock (Running)
                    foreach (KeyValuePair<int, Slot> kv in Running)
                        if (kv.Value.Cfg.StopOnInput) ids.Add(kv.Key);
                // Each worker's own exit raises Stopped and the UI catches up
                foreach (int id in ids) Stop(id);
            });
        }

        // The keyboard hook's side of the feature: MainForm calls this for any
        // real key-down that wasn't one of our hotkeys.
        public static void NoteUserKey()
        {
            if (stopInputArmed) StopForInput();
        }

        static IntPtr ObserverCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int m = wParam.ToInt32();
                if (m == WM_MOUSEMOVE)
                {
                    // Only ever inspected while some card wants stop-on-input;
                    // for everyone else a move costs one flag read.
                    if (stopInputArmed
                        && Marshal.ReadIntPtr(lParam, OffExtra) == IntPtr.Zero)
                    {
                        int mx = Marshal.ReadInt32(lParam, OffX);
                        int my = Marshal.ReadInt32(lParam, OffY);
                        // A user delta that lands while the cursor is parked
                        // on a spot reads AS the spot plus the delta - a
                        // phantom jump of thousands of pixels. Those readings
                        // are poison here just as they are for home.
                        if (!AtRunningSpot(mx, my))
                        {
                            long now;
                            QueryPerformanceCounter(out now);
                            // The anchor trails the pointer by up to 400 ms:
                            // drifting a few px is free, but covering more
                            // than the grace within one window is the user
                            // taking the mouse back.
                            if (siAnchor == long.MinValue
                                || now - siAnchorTick > Freq * 2 / 5)
                            {
                                siAnchor = PackPoint(mx, my);
                                siAnchorTick = now;
                            }
                            else
                            {
                                int ax = (int)(siAnchor >> 32), ay = (int)(uint)siAnchor;
                                if (Math.Abs(mx - ax) > StopMoveGrace
                                 || Math.Abs(my - ay) > StopMoveGrace)
                                    StopForInput();
                            }
                        }
                    }
                    return CallNextHookEx(obsHook, code, wParam, lParam);
                }
                bool isUp = m == WM_LBUTTONUP || m == WM_RBUTTONUP
                         || m == WM_MBUTTONUP || m == WM_XBUTTONUP;
                bool isDown = m == WM_LBUTTONDOWN || m == WM_RBUTTONDOWN
                           || m == WM_MBUTTONDOWN || m == WM_XBUTTONDOWN;
                if (isUp || isDown)
                {
                    var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                    if (info.dwExtraInfo == SelfMarkPinned)
                    {
                        // The gatekeeper. A fixed-position click surfacing at
                        // conversion AWAY from every known click target has
                        // been mangled by the input stack (measured: its
                        // coordinates rewritten to the user's moving hand) -
                        // and one surfacing with NO pinned slot running is a
                        // stop-time straggler whose move would jump the
                        // pointer back to the spot. Discard either outright -
                        // a missed beat is invisible, a click on the user's
                        // hand or a post-stop jump is not. Pairs stay
                        // symmetric: vetoing a down vetoes its up too.
                        bool bad = Interlocked.CompareExchange(ref fixedRunning, 0, 0) == 0
                                || !NearRecentTarget(info.X, info.Y)
                                || (isUp && vetoPairOpen);
                        if (isDown) vetoPairOpen = bad;
                        else if (bad) vetoPairOpen = false;
                        // A vetoed event is discarded and never reaches the
                        // cursor, so it parks nothing
                        if (bad) return new IntPtr(1);
                        // This one is about to move the cursor onto its spot.
                        // If it converted after its own beat's restore - the
                        // queue running behind us - the pointer STAYS there
                        // until some later beat restores, and every reading
                        // taken meanwhile is the spot, plus however far the
                        // hand has moved since. Those readings are the ones
                        // home must never adopt.
                        QueryPerformanceCounter(out ParkedSince);
                        Parked = true;
                    }
                    else if (info.dwExtraInfo == SelfMark)
                    {
                        // ours, healthy - nothing to do
                    }
                    else
                    {
                        // The user's own hand - track which buttons are down
                        int bit = m == WM_LBUTTONDOWN || m == WM_LBUTTONUP ? 1
                                : m == WM_RBUTTONDOWN || m == WM_RBUTTONUP ? 2
                                : m == WM_MBUTTONDOWN || m == WM_MBUTTONUP ? 4 : 8;
                        if (isDown) userButtons |= bit;
                        else userButtons &= ~bit;
                        // A real press is unambiguous - no grace needed
                        if (isDown && stopInputArmed) StopForInput();
                    }
                }
            }
            return CallNextHookEx(obsHook, code, wParam, lParam);
        }

        static void EnsureObserver()
        {
            if (obsThread != null) return;
            obsThread = new Thread(delegate()
            {
                obsProc = ObserverCallback;
                obsHook = SetWindowsHookExW(WH_MOUSE_LL, obsProc, IntPtr.Zero, 0);
                MSG m;
                while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0) { }
                if (obsHook != IntPtr.Zero) UnhookWindowsHookEx(obsHook);
            });
            obsThread.IsBackground = true;
            // The hook sits in the path of EVERY mouse event on the machine;
            // if this thread is ever starved the whole system's input lags
            obsThread.Priority = ThreadPriority.Highest;
            obsThread.Start();
        }
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceCounter(out long v);
        [DllImport("kernel32.dll")] static extern bool QueryPerformanceFrequency(out long v);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint mapType);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateWaitableTimerExW(IntPtr attr, IntPtr name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetWaitableTimer(IntPtr t, ref long due, int period, IntPtr r, IntPtr a, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_EXTENDEDKEY = 0x0001;
        const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
        const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
        const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
        const uint MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
        const uint MOUSEEVENTF_WHEEL = 0x0800;
        const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_ABSOLUTE = 0x8000,
                   MOUSEEVENTF_VIRTUALDESK = 0x4000;
        const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77,
                  SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
        const uint TIMER_HIGH_RESOLUTION = 0x00000002, TIMER_ALL_ACCESS = 0x1F0003;

        // Stamped on everything we synthesise so the recorder can tell our own
        // output from the user's hands. Without it, recording while a clicker
        // runs would capture that clicker and play it back on top.
        public static readonly IntPtr SelfMark = new IntPtr(0x4D414331);   // "MAC1"
        // A second stamp just for fixed-position click pairs: the conversion
        // gatekeeper (see ObserverCallback) may veto THESE if they surface at
        // the wrong coordinates, without ever touching current-position or
        // macro clicks, which legitimately land wherever the pointer is.
        public static readonly IntPtr SelfMarkPinned = new IntPtr(0x4D414332); // "MAC2"

        // Held while a slot moves the pointer and clicks, so the two can't be
        // separated by another slot doing the same thing. Two slots due at the
        // same moment would otherwise interleave as move-A, move-B, click,
        // click, with both clicks landing on B.
        static readonly object PointerLock = new object();

        // Where the user's hand is - ONE shared home for every restoring slot,
        // guarded by PointerLock. With a saved spot per slot instead, several
        // fixed slots interleaving under a congested input queue let one slot
        // read ANOTHER slot's click spot as "the user's position" and adopt
        // it as home - after which the pointer ping-pongs between click spots
        // instead of returning to the hand.
        static int HomeX, HomeY;
        static bool HomeValid;

        static long PackPoint(int x, int y)
        {
            return ((long)(uint)x << 32) | (uint)y;
        }

        // The click spot of every RUNNING fixed slot. Home is read from the
        // live cursor, and this is the list of places the cursor can be that
        // aren't the user: between a click converting and the next beat's
        // restore, the pointer is parked on a spot, and a hand moving during
        // that window reads as "the spot, plus however far they moved".
        //
        // Two things about this list matter, and getting either wrong is what
        // produced the "it doesn't return to the right spot" reports.
        //
        // It is the running slots' spots, not a ring of places we recently
        // aimed at. A ring needs an expiry and a capacity, and neither can be
        // set safely: a 10 ms card pushes a hundred entries a second, so it
        // evicts a 1000 ms card's spot within a third of a second, and any
        // expiry short enough to keep the ring current is shorter than the
        // input queue's lag under load. Either way a slow card's spot stopped
        // being distrusted while that card was still clicking it - so when one
        // of its clicks converted and parked the cursor there, the spot was
        // read AS the hand. Home then stuck, because every later reading was
        // taken at that bad home, far from every spot, and so confirmed it.
        //
        // The tolerance is tens of pixels, nothing wider: at 150 a profile
        // with a column of targets merges its halos into a dead zone covering
        // the strip of screen the hand actually works in, and home stops
        // following it there. 48 is the measured floor - below it a
        // hand moving fast enough gets its drift mistaken for a real position
        // and the pointer ends up stranded by the spot.
        //
        // A hand resting INSIDE the band is the one case this can't read, and
        // there it holds the last good home rather than adopting the spot: the
        // pointer keeps returning to where the hand last was, and picks it up
        // again the moment it moves clear.
        static readonly long[] SpotAt = new long[64];
        static readonly int[] SpotTol = new int[64];
        static int SpotN;
        const int SpotHalo = 48;

        // The spot table's own lock, never held across SendInput or
        // SetCursorPos, so the observer hook can take it without ever waiting
        // on a pace thread's in-flight click. PointerLock guarded these reads
        // before - but the hook-side caller couldn't take PointerLock (a pace
        // thread holds it across SendInput retries) and read the table bare,
        // racing the Array.Copy below into torn spots. Lock order where both
        // are held: PointerLock, then SpotLock.
        static readonly object SpotLock = new object();

        // Rebuilt whenever the running set changes. Takes Running before
        // SpotLock, the same order every other caller uses.
        static void RefreshSpots()
        {
            var at = new long[SpotAt.Length];
            var tol = new int[SpotTol.Length];
            int n = 0;
            lock (Running)
            {
                foreach (Slot s in Running.Values)
                {
                    if (!s.FixedSnap || n >= at.Length) continue;
                    at[n] = PackPoint(s.Cfg.X, s.Cfg.Y);
                    tol[n] = s.Cfg.PosJitter + SpotHalo;
                    n++;
                }
            }
            lock (SpotLock)
            {
                Array.Copy(at, SpotAt, n);
                Array.Copy(tol, SpotTol, n);
                SpotN = n;
            }
            ArmStopOnInput();
        }

        // Is the cursor currently somewhere WE put it? True from the moment
        // one of our clicks converts onto its spot until a restore takes it
        // off again.
        //
        // The staleness escape is a genuine last resort, not a timeout on the
        // park. In normal running every beat's restore clears the flag, so it
        // only stays set while the cursor really is sitting on a spot - and
        // that lasts until the next restore, which for a 1000 ms card is most
        // of a second. An escape shorter than that hands back precisely the
        // readings this exists to reject: at a quarter second, a column of
        // slow cards still lost the pointer to the spot once every few sweeps
        // of the hand. Two seconds is longer than any real park and still
        // rescues home if a conversion never reaches the hook at all.
        static bool PointerIsOurs()
        {
            if (!Parked) return false;
            long now;
            QueryPerformanceCounter(out now);
            if (now - Interlocked.Read(ref ParkedSince) > Freq * 2)
            {
                Parked = false;
                return false;
            }
            return true;
        }

        // Takes SpotLock itself: callable from the pace loop (which holds
        // PointerLock - SpotLock nests inside it) and from the observer hook
        // (which holds nothing and must never wait on PointerLock).
        static bool AtRunningSpot(int x, int y)
        {
            lock (SpotLock)
            {
                for (int i = 0; i < SpotN; i++)
                {
                    int sx = (int)(SpotAt[i] >> 32), sy = (int)(uint)SpotAt[i];
                    if (Math.Abs(x - sx) <= SpotTol[i] && Math.Abs(y - sy) <= SpotTol[i])
                        return true;
                }
                return false;
            }
        }

        // 32 deep: a 10 ms card alone pushes ~100 entries a second, and the
        // 300 ms distrust window must survive that churn for the slow cards'
        // spots too (the gatekeeper consults the same ring at conversion)
        static readonly int[] TgtX = new int[32], TgtY = new int[32];
        static readonly long[] TgtTick = new long[32];
        static int TgtN, TgtAt;

        // Used by the conversion gatekeeper to ask "did this fixed-position
        // click surface anywhere near a spot we just aimed at?" A healthy one
        // surfaces ON its target, a mangled one at the user's hand, so this
        // only has to be wide enough to cover rounding and in-flight drift
        // (measured at 10-30 px under a frantic hand).
        const int TargetHalo = 48;

        // Is a real mouse button physically down right now? Our own synthetic
        // clicks are far too brief to be caught here in practice, and the
        // deferral is bounded anyway, so a false positive costs one beat.
        static bool UserHolding()
        {
            return (GetAsyncKeyState(0x01) & 0x8000) != 0      // left
                || (GetAsyncKeyState(0x02) & 0x8000) != 0      // right
                || (GetAsyncKeyState(0x04) & 0x8000) != 0;     // middle
        }

        // Entries expire: a spot is only distrusted for the queue-lag window
        // after we actually clicked it, not forever. Without the age check,
        // twenty 1-second cards kept their whole click column permanently
        // suspect and home refused readings from an area the hand uses.
        static bool NearRecentTarget(int x, int y)
        {
            long now;
            QueryPerformanceCounter(out now);
            long maxAge = Freq * 3 / 10;                // 300 ms
            for (int i = 0; i < TgtN; i++)
            {
                if (now - TgtTick[i] > maxAge) continue;
                if (Math.Abs(x - TgtX[i]) <= TargetHalo && Math.Abs(y - TgtY[i]) <= TargetHalo)
                    return true;
            }
            return false;
        }

        static void PushTarget(int x, int y)
        {
            long now;
            QueryPerformanceCounter(out now);
            TgtX[TgtAt] = x; TgtY[TgtAt] = y; TgtTick[TgtAt] = now;
            TgtAt = (TgtAt + 1) % TgtX.Length;
            if (TgtN < TgtX.Length) TgtN++;
        }

        static long Freq;

        // Marshalling the size of a struct is a reflection call, and it was
        // being made on every single click
        static readonly int InputSize = Marshal.SizeOf(typeof(INPUT));

        // How many recent clicks the achieved-rate meter averages over
        const int RateRing = 64;

        sealed class Slot
        {
            // The slot's CURRENT card index. Captured ids go stale the moment
            // cards are reordered, so the worker reads this at exit instead.
            public volatile int Id;
            public Thread Worker;
            public volatile bool Run;
            public SlotConfig Cfg;
            public long IntervalTicks, JitterTicks, PhaseTicks;
            public long Count, Limit;
            public long StopAtTick;             // 0 = no time limit
            public INPUT[] Buf = new INPUT[2];
            public int WaitVk;                  // key to watch for conversion
            // Snapshots taken at Start: the card's fields can be edited live
            // while the slot runs, and the veto accounting must stay paired
            public bool FixedSnap, Pinned;
            // The same two events every time, so they are marshalled into
            // native memory once at startup instead of on every click
            public IntPtr NativeBuf;
            // A scratch INPUT for pointer moves, so the click pair above is
            // never overwritten by one
            public Random Rng = new Random();
            public IntPtr Gate;                 // window the slot is tied to, or zero
            // Press-and-hold: how long each press stays down (0 = instant),
            // and the release event to fire if the run stops mid-press
            public long HoldTicks;
            public IntPtr HeldUp;               // non-zero while a press is open

            // When the last few clicks actually landed, for the achieved-rate
            // half of the meter
            public readonly long[] Stamps = new long[RateRing];
            public int StampAt, StampN;
            public readonly object RateLock = new object();

            // Macro playback. Non-null Macro means this slot replays a timeline
            // instead of pacing clicks; LoopGapTicks is the pause between runs.
            public Ev[] Macro;
            public long LoopGapTicks;
            // Window-relative playback: recorded positions shift by however far
            // the reference window has moved since the take. Recomputed at each
            // loop start, so dragging the window mid-run stays aligned.
            public bool Relative;
            public IntPtr RefWindow;            // resolved once at start
            public int RecWinX, RecWinY;        // where the take's window was
            public int RelDX, RelDY;            // this loop's shift
            // Buttons (0-4) and keys (vk+100) the take has pressed but not yet
            // released, so stopping mid-gesture can let go of them - a macro
            // stopped mid-drag would otherwise strand a button down.
            public readonly List<int> Held = new List<int>();
        }

        static readonly Dictionary<int, Slot> Running = new Dictionary<int, Slot>();

        // Every slot whose worker might still be holding a press down. Unlike
        // Running - which Stop empties the moment the signal goes out - a slot
        // stays listed here until its worker's finally has actually run, so
        // the exit and crash paths can see (and release) what a killed
        // background thread would have left held. See ReleaseAllHeld.
        static readonly List<Slot> Live = new List<Slot>();

        // No per-click event: the card only needs a reading five times a second
        // and polls for it, so nothing crosses a thread boundary per click.
        public static event Action<int> Stopped;           // slot id

        public static void Startup()
        {
            QueryPerformanceFrequency(out Freq);
            timeBeginPeriod(1);
            EnsureObserver();
        }

        public static void Shutdown()
        {
            StopAll();
            // Unlike every other stop, exit WAITS. Workers are background
            // threads, and the CLR kills those without running their finallys
            // - which are the only place a held press (HoldPercent, a macro
            // mid-drag) sends its up-event. A fast exit that leaves the
            // user's mouse button logically stuck is not a fast exit.
            List<Slot> live;
            lock (Live) live = new List<Slot>(Live);
            foreach (Slot s in live)
                if (s.Worker != null && s.Worker.IsAlive) s.Worker.Join(700);
            ReleaseAllHeld();               // backstop for any join that timed out
            timeEndPeriod(1);
        }

        // Send the up-event for anything still logically held down. The last
        // line of defence: called after Shutdown's joins for any straggler,
        // and from the crash handlers in Program, where workers die without
        // their finallys. A duplicate release is harmless (an unmatched up is
        // ignored); a missed one is a stuck mouse button on the desktop.
        public static void ReleaseAllHeld()
        {
            List<Slot> live;
            lock (Live) live = new List<Slot>(Live);
            if (live.Count == 0) return;
            IntPtr one = Marshal.AllocHGlobal(InputSize);
            try
            {
                foreach (Slot s in live)
                {
                    try
                    {
                        // HeldUp points into the slot's NativeBuf, which the
                        // worker frees under PointerLock - so it is only read
                        // under the same lock. TryEnter, not lock: if the
                        // crashing thread itself died holding PointerLock, a
                        // plain lock would hang the process open forever.
                        bool got = Monitor.TryEnter(PointerLock, 500);
                        try
                        {
                            if (s.HeldUp != IntPtr.Zero && s.NativeBuf != IntPtr.Zero)
                            {
                                SendInput(1, s.HeldUp, InputSize);
                                s.HeldUp = IntPtr.Zero;
                            }
                        }
                        finally { if (got) Monitor.Exit(PointerLock); }
                        // A macro's open presses - built here rather than via
                        // Emit, which would wait on PointerLock again
                        int[] held;
                        lock (s.Held) held = s.Held.ToArray();
                        foreach (int code in held) SendUpFor(one, code);
                        lock (s.Held) s.Held.Clear();
                    }
                    catch { }               // dying process: release what we can
                }
            }
            finally { Marshal.FreeHGlobal(one); }
        }

        // The up-event for one Held entry: buttons 0-4, keys at vk+100 - the
        // same coding ReleaseHeld replays through Emit.
        static void SendUpFor(IntPtr one, int code)
        {
            if (code >= 100)
            {
                var inp = new INPUT();
                inp.type = INPUT_KEYBOARD;
                inp.U.ki.wVk = (ushort)(code - 100);
                inp.U.ki.wScan = (ushort)MapVirtualKeyW((uint)(code - 100), 0);
                inp.U.ki.dwFlags = KEYEVENTF_KEYUP;
                inp.U.ki.dwExtraInfo = SelfMark;
                Marshal.StructureToPtr(inp, one, false);
                SendInput(1, one, InputSize);
            }
            else
            {
                uint flag, data;
                MouseFlag(code, false, out flag, out data);
                SendMouse(one, flag, data);
            }
        }

        public static bool IsRunning(int id)
        {
            lock (Running)
            {
                Slot s;
                if (!Running.TryGetValue(id, out s)) return false;
                // A dead worker whose entry lingered would make its card
                // permanently answer "already running" - every hotkey press a
                // no-op stop. Self-heal instead of trusting the bookkeeping.
                if (s.Worker != null && !s.Worker.IsAlive)
                {
                    Running.Remove(id);
                    return false;
                }
                return true;
            }
        }

        public static bool AnyRunning()
        {
            lock (Running) return Running.Count > 0;
        }

        public static int RunningCount()
        {
            lock (Running) return Running.Count;
        }

        // Which cards are replaying a take right now. Asked by the one-macro-
        // at-a-time rule, which has to interrupt whatever is playing before it
        // starts the next one. Answered from what the engine is actually doing
        // rather than from the cards' settings: a card switched away from Macro
        // while its take was still running would be invisible to that check.
        public static List<int> RunningMacros()
        {
            var ids = new List<int>();
            lock (Running)
                foreach (KeyValuePair<int, Slot> kv in Running)
                    if (kv.Value.Macro != null) ids.Add(kv.Key);
            return ids;
        }

        public static long ClickCount(int id)
        {
            lock (Running)
            {
                Slot s;
                return Running.TryGetValue(id, out s) ? s.Count : 0;
            }
        }

        // phaseMs staggers the first click. Cards sharing one hotkey are given
        // evenly spaced phases so their clicks arrive in turn instead of all on
        // the same tick - a game polling input once a frame only sees one of
        // them otherwise.
        public static void Start(int id, SlotConfig cfg, IntPtr gate, double phaseMs)
        {
            Stop(id);
            var s = new Slot();
            s.Id = id;
            s.Cfg = cfg;
            s.Gate = gate;
            long us = Math.Max(100, (long)cfg.Interval * 1000);        // 10k cps ceiling
            s.IntervalTicks = Math.Max(1, (long)(Freq * (us / 1000000.0)));
            s.JitterTicks = (long)(Freq * (cfg.JitterMs / 1000.0));
            // Hold is a share of the interval, so it follows the click rate.
            // A custom KEY never presses for zero time: down and up in one
            // batch arrive inside the same frame, and a game polling per frame
            // (Minecraft) never sees the key down at all. 1% forces them into
            // separate events with real time between.
            int holdPct = cfg.HoldPercent > 0 ? Math.Min(90, cfg.HoldPercent)
                        : cfg.IsCustomKey ? 1 : 0;
            s.HoldTicks = holdPct > 0 ? s.IntervalTicks * holdPct / 100 : 0;
            s.PhaseTicks = (long)(Freq * (phaseMs / 1000.0));
            s.Limit = cfg.StopClicks;
            if (cfg.StopSeconds > 0)
            {
                long now; QueryPerformanceCounter(out now);
                s.StopAtTick = now + (long)(Freq * cfg.StopSeconds);
            }
            BuildInput(s);
            s.FixedSnap = cfg.IsFixed;
            s.Pinned = cfg.IsFixed && !cfg.IsCustomKey;
            if (s.Pinned) Interlocked.Increment(ref fixedRunning);
            s.Run = true;
            // A fresh session - nothing else running - starts clean: no home
            // carried over from the last run, no stale targets to distrust
            bool fresh;
            lock (Running) fresh = Running.Count == 0;
            if (fresh) lock (PointerLock) { HomeValid = false; TgtN = 0; TgtAt = 0; }
            lock (Running) Running[id] = s;
            lock (Live) Live.Add(s);        // exit/crash paths can see it now
            // This slot's spot counts as ours from now until it stops
            RefreshSpots();

            s.Worker = new Thread(delegate() { Pace(s, id); });
            s.Worker.IsBackground = true;
            s.Worker.Priority = ThreadPriority.Highest;
            s.Worker.Start();
        }

        // A macro slot replays a recorded timeline; the interval doubles as the
        // pause between repeats rather than the gap between clicks. Returns
        // false when the file is missing or empty. warning comes back non-null
        // when playback will run but not the way the user probably expects -
        // the card shows it on its status line.
        public static bool StartMacro(int id, SlotConfig cfg, IntPtr gate, string path,
                                      out string warning)
        {
            warning = null;
            Stop(id);
            // Only one macro plays at a time - a new take takes over, the way
            // changing a station does. Clickers are deliberately untouched:
            // they run alongside whatever macro is playing.
            List<int> playing = null;
            lock (Running)
            {
                foreach (KeyValuePair<int, Slot> kv in Running)
                    if (kv.Value.Macro != null)
                        (playing ?? (playing = new List<int>())).Add(kv.Key);
            }
            if (playing != null) foreach (int p in playing) Stop(p);

            MacroFile.WindowInfo rec;
            Ev[] macro = MacroFile.Load(path, Freq, out rec);
            if (macro == null || macro.Length == 0) return false;

            var s = new Slot();
            s.Id = id;
            s.Cfg = cfg;
            s.Gate = gate;
            s.Macro = macro;

            // Window-relative playback: anchor the take to a live window. The
            // gate window when the card has one - that pairing is the point of
            // the feature - otherwise whatever is in front right now.
            if (cfg.MacroRelative)
            {
                if (!rec.Valid)
                {
                    // \u escapes, not literal glyphs: a raw non-ASCII byte
                    // in a BOM-less file compiles as whatever codepage the
                    // build machine uses, and ships as mojibake
                    warning = "\u26A0 This take has no window info - playing at the recorded positions.";
                }
                else
                {
                    IntPtr r = (gate != IntPtr.Zero && gate != new IntPtr(1))
                             ? gate : GetForegroundWindow();
                    if (r == IntPtr.Zero || !IsWindow(r))
                    {
                        warning = "\u26A0 No window to follow - playing at the recorded positions.";
                    }
                    else
                    {
                        s.Relative = true;
                        s.RefWindow = r;
                        s.RecWinX = rec.X;
                        s.RecWinY = rec.Y;
                        RECT cur;
                        if (GetWindowRect(r, out cur)
                            && (cur.R - cur.L != rec.W || cur.B - cur.T != rec.H))
                            warning = "\u26A0 Window is " + (cur.R - cur.L) + "\u00D7" + (cur.B - cur.T)
                                    + " but the take was recorded at " + rec.W + "\u00D7" + rec.H
                                    + " - positions may be off.";
                    }
                }
            }

            s.LoopGapTicks = (long)(Freq * (Math.Max(0, cfg.Interval) / 1000.0));
            s.JitterTicks = (long)(Freq * (cfg.JitterMs / 1000.0));
            s.Limit = cfg.StopClicks;
            if (cfg.StopSeconds > 0)
            {
                long now; QueryPerformanceCounter(out now);
                s.StopAtTick = now + (long)(Freq * cfg.StopSeconds);
            }
            s.Run = true;
            lock (Running) Running[id] = s;
            lock (Live) Live.Add(s);        // exit/crash paths can see it now
            ArmStopOnInput();

            s.Worker = new Thread(delegate() { PlayMacro(s, id); });
            s.Worker.IsBackground = true;
            s.Worker.Priority = ThreadPriority.Highest;
            s.Worker.Start();
            return true;
        }

        // Signal-only: no joining. The stop hotkey is a HARD stop - joining
        // each worker (up to its full interval) stacked sequentially on the
        // keyboard-hook thread, so several cards took the better part of a
        // second to obey. Workers notice within one 30 ms wait slice and
        // clean themselves up in their finally; anything still in flight is
        // sacrificed (post-stop stragglers are vetoed at conversion anyway).
        public static void Stop(int id)
        {
            Slot s = null;
            lock (Running)
            {
                if (Running.TryGetValue(id, out s)) Running.Remove(id);
            }
            if (s == null) return;
            s.Run = false;
            ReturnCursor(s);
        }

        // Stop, minus anything that can wait on PointerLock - for callers
        // inside a low-level hook callback. ReturnCursor can block behind a
        // pace thread's SendInput retry loop; from a hook that stalls every
        // event on the machine, so the restore is handed to the pool instead.
        public static void StopFromHook(int id)
        {
            Slot s = null;
            lock (Running)
            {
                if (Running.TryGetValue(id, out s)) Running.Remove(id);
            }
            if (s == null) return;
            s.Run = false;
            ThreadPool.QueueUserWorkItem(delegate { ReturnCursor(s); });
        }

        // Signal every slot, then restore each pointer. Stop() per slot,
        // each with its own 250 ms join, can hold the UI thread for over a
        // second with a few cards running, and the UI thread is where the
        // keyboard hook's callback runs: block it long enough while a key
        // arrives and Windows quietly removes the hook - every hotkey dead
        // after a profile switch.
        public static void StopAll()
        {
            var stopping = new List<Slot>();
            lock (Running)
            {
                foreach (KeyValuePair<int, Slot> kv in Running)
                {
                    kv.Value.Run = false;
                    stopping.Add(kv.Value);
                }
                Running.Clear();
            }
            foreach (Slot s in stopping) ReturnCursor(s);
            // No joins - see Stop(). Workers notice within a wait slice and
            // self-clean; a restarted slot never shares state with an old one.
        }

        // Cards were reordered: move the running slots' keys to match, so a
        // clicker keeps running under its card wherever the card lands.
        public static void MoveSlot(int from, int to)
        {
            if (from == to) return;
            lock (Running)
            {
                var moved = new Dictionary<int, Slot>();
                foreach (KeyValuePair<int, Slot> kv in Running)
                {
                    int k = kv.Key, nk = k;
                    if (k == from) nk = to;
                    else if (from < to && k > from && k <= to) nk = k - 1;
                    else if (to < from && k >= to && k < from) nk = k + 1;
                    kv.Value.Id = nk;
                    moved[nk] = kv.Value;
                }
                Running.Clear();
                foreach (KeyValuePair<int, Slot> kv in moved) Running[kv.Key] = kv.Value;
            }
        }

        // A card was removed or inserted: running slots at higher indices keep
        // clicking, but their ids must follow their cards or the running state
        // shows on - and Stop() reaches - the wrong card.
        public static void ShiftForRemoval(int removed) { Shift(removed, -1); }
        public static void ShiftForInsert(int at)       { Shift(at, +1); }

        static void Shift(int from, int by)
        {
            lock (Running)
            {
                var moved = new Dictionary<int, Slot>();
                foreach (KeyValuePair<int, Slot> kv in Running)
                {
                    int nk = (by < 0 ? kv.Key > from : kv.Key >= from) ? kv.Key + by : kv.Key;
                    kv.Value.Id = nk;
                    moved[nk] = kv.Value;
                }
                Running.Clear();
                foreach (KeyValuePair<int, Slot> kv in moved) Running[kv.Key] = kv.Value;
            }
        }

        // The gate can change while a slot runs - the window comes and goes as
        // the user alt-tabs - so it is updated in place rather than restarting.
        public static void SetGate(int id, IntPtr gate)
        {
            // The restore runs OUTSIDE the Running lock: ReturnCursor takes
            // PointerLock, and holding Running while waiting on it let the
            // hook thread's brief lock(Running) chain-block behind a pace
            // thread's whole SendInput retry loop.
            Slot closed = null;
            lock (Running)
            {
                Slot s;
                if (Running.TryGetValue(id, out s))
                {
                    bool wasOpen = MayFire(s);
                    s.Gate = gate;
                    if (wasOpen && !MayFire(s)) closed = s;
                }
            }
            if (closed != null) ReturnCursor(closed);
        }

        // Which window a running slot is currently tied to - IntPtr.Zero when
        // the slot isn't running (or has no gate). The gate timer reads this
        // to leave a FlickFocus slot's resolved handle alone.
        public static IntPtr GateOf(int id)
        {
            lock (Running)
            {
                Slot s;
                return Running.TryGetValue(id, out s) ? s.Gate : IntPtr.Zero;
            }
        }

        static bool MayFire(Slot s)
        {
            return s.Gate == IntPtr.Zero || GetForegroundWindow() == s.Gate;
        }

        static void BuildInput(Slot s)
        {
            INPUT[] b = s.Buf;
            if (s.Cfg.IsCustomKey)
            {
                ushort vk = HotkeyParser.KeyNameToVk(s.Cfg.CustomKey);
                ushort sc = (ushort)MapVirtualKeyW(vk, 0);
                b[0] = new INPUT(); b[0].type = INPUT_KEYBOARD;
                b[0].U.ki.wVk = vk; b[0].U.ki.wScan = sc; b[0].U.ki.dwExtraInfo = SelfMark;
                b[1] = new INPUT(); b[1].type = INPUT_KEYBOARD;
                b[1].U.ki.wVk = vk; b[1].U.ki.wScan = sc; b[1].U.ki.dwFlags = KEYEVENTF_KEYUP;
                b[1].U.ki.dwExtraInfo = SelfMark;
                s.WaitVk = vk;
                Pin(s);
                return;
            }
            uint down, up, data = 0;
            switch (s.Cfg.Input)
            {
                case "Right Click":  down = MOUSEEVENTF_RIGHTDOWN;  up = MOUSEEVENTF_RIGHTUP;  s.WaitVk = 0x02; break;
                case "Middle Click": down = MOUSEEVENTF_MIDDLEDOWN; up = MOUSEEVENTF_MIDDLEUP; s.WaitVk = 0x04; break;
                case "X1 Button":    down = MOUSEEVENTF_XDOWN; up = MOUSEEVENTF_XUP; data = 1; s.WaitVk = 0x05; break;
                case "X2 Button":    down = MOUSEEVENTF_XDOWN; up = MOUSEEVENTF_XUP; data = 2; s.WaitVk = 0x06; break;
                default:             down = MOUSEEVENTF_LEFTDOWN;   up = MOUSEEVENTF_LEFTUP;   s.WaitVk = 0x01; break;
            }
            // Fixed-position pairs carry the veto-eligible stamp; everything
            // else keeps the plain one and is never gated
            IntPtr mark = s.Cfg.IsFixed ? SelfMarkPinned : SelfMark;
            b[0] = new INPUT(); b[0].type = INPUT_MOUSE;
            b[0].U.mi.dwFlags = down; b[0].U.mi.mouseData = data; b[0].U.mi.dwExtraInfo = mark;
            b[1] = new INPUT(); b[1].type = INPUT_MOUSE;
            b[1].U.mi.dwFlags = up; b[1].U.mi.mouseData = data; b[1].U.mi.dwExtraInfo = mark;
            Pin(s);
        }

        // Copy the pair into unmanaged memory once. Passing the managed array to
        // SendInput made the marshaller copy both structs out on every click,
        // for two events that never change.
        //
        // Three slots, not two: [0] holds the per-cycle move so a fixed-position
        // slot can send move+down+up as ONE SendInput batch. Sent separately,
        // the input stack could coalesce the move away against a fast hand's
        // delta flood, and the orphaned click landed at the user's position.
        // A batch is never interspersed with other input, so the click can't
        // be separated from its move.
        static void Pin(Slot s)
        {
            if (s.NativeBuf == IntPtr.Zero) s.NativeBuf = Marshal.AllocHGlobal(InputSize * 3);
            Marshal.StructureToPtr(s.Buf[0], IntPtr.Add(s.NativeBuf, InputSize), false);
            Marshal.StructureToPtr(s.Buf[1], IntPtr.Add(s.NativeBuf, InputSize * 2), false);
        }

        static void ReturnCursor(Slot s)
        {
            // Under the pointer lock: Stop() calls this from the UI thread,
            // and racing an in-flight move+click cycle lets the restore land
            // BEFORE the click (misfiring it at the user's position) and
            // abandon the pointer at the fixed spot. The lock is reentrant, so the
            // per-click restore inside the pace loop is unaffected.
            lock (PointerLock)
            {
                if (!s.Cfg.RestoreCursor || !HomeValid) return;
                // Cleared BEFORE the move, not after: a conversion landing in
                // between is followed by this restore anyway, so the cursor
                // still ends up at home. Clearing afterwards would drop a
                // parked flag that had just become true again.
                Parked = false;
                // SetCursorPos, deliberately NOT a queued input event: a
                // queued move-to-home could still be PENDING when the next
                // cycle's click converts under a lagging queue, yanking that
                // click to the user's hand. An immediate restore never lingers
                // anywhere. The restore is for the user's hand, not the game -
                // their own next real movement re-syncs raw-input consumers.
                SetCursorPos(HomeX, HomeY);
            }
        }

        // Modifiers currently held down by a custom-key slot - a card whose
        // key IS Shift, mid-press. The hotkey hook reads the machine's live
        // modifier state, and without this a card holding Shift turned the
        // user's plain "2" into Shift+2: the stop hotkey stopped matching
        // exactly while the clicker ran, which is the one moment it matters.
        // Counters, not flags - two cards can hold the same modifier.
        static readonly int[] SynthMod = new int[4];       // shift, ctrl, alt, win

        static int ModIndex(int vk)
        {
            switch (vk)
            {
                case 0x10: return 0;
                case 0x11: return 1;
                case 0x12: return 2;
                case 0x5B: case 0x5C: return 3;
                default: return -1;
            }
        }

        public static bool SelfShift { get { return SynthMod[0] > 0; } }
        public static bool SelfCtrl  { get { return SynthMod[1] > 0; } }
        public static bool SelfAlt   { get { return SynthMod[2] > 0; } }
        public static bool SelfWin   { get { return SynthMod[3] > 0; } }

        // Sit on a press for its hold time, then release. Sliced so a stop is
        // still honoured within a wait slice; the release fires either way -
        // the finally would catch it, but leaving a button down even briefly
        // reads as a stuck mouse.
        static void HoldThenRelease(Slot s, IntPtr timer, IntPtr upEvent)
        {
            s.HeldUp = upEvent;
            // The press we're sitting on may BE a modifier - flag it for the
            // hotkey hook for as long as it's down
            int mb = s.Cfg.IsCustomKey ? ModIndex(s.WaitVk) : -1;
            if (mb >= 0) Interlocked.Increment(ref SynthMod[mb]);
            try
            {
                long start, now;
                QueryPerformanceCounter(out start);
                while (s.Run)
                {
                    QueryPerformanceCounter(out now);
                    long left = s.HoldTicks - (now - start);
                    if (left <= 0) break;
                    Wait(timer, Math.Min(left * 1000.0 / Freq, 30.0));
                }
                SendInput(1, upEvent, InputSize);
                s.HeldUp = IntPtr.Zero;
            }
            finally
            {
                if (mb >= 0) Interlocked.Decrement(ref SynthMod[mb]);
            }
        }

        static void MarkClick(Slot s, long tick)
        {
            lock (s.RateLock)
            {
                s.Stamps[s.StampAt] = tick;
                s.StampAt = (s.StampAt + 1) % RateRing;
                if (s.StampN < RateRing) s.StampN++;
            }
        }

        // Clicks per second this slot is actually managing, measured from when
        // its recent clicks really went out rather than from what it was asked
        // for. A slot that is gated out, throttled, or fighting for the pointer
        // reads below its target, which is the whole point of showing both.
        public static double ActualCps(int id)
        {
            Slot s;
            lock (Running) { if (!Running.TryGetValue(id, out s)) return 0; }

            long now; QueryPerformanceCounter(out now);
            lock (s.RateLock)
            {
                if (s.StampN < 2) return 0;
                // Only the last couple of seconds count, so the reading follows
                // the clicker instead of averaging in ancient history
                long cutoff = now - Freq * 2;
                long newest = 0, oldest = 0;
                int n = 0;
                for (int k = 0; k < s.StampN; k++)
                {
                    long t = s.Stamps[((s.StampAt - 1 - k) % RateRing + RateRing) % RateRing];
                    if (t < cutoff) break;
                    if (n == 0) newest = t;
                    oldest = t;
                    n++;
                }
                if (n < 2) return 0;

                double span = (newest - oldest) / (double)Freq;
                if (span <= 0) return 0;
                double rate = (n - 1) / span;
                // Nothing since the last one for a while? The rate is falling,
                // so count the silence in rather than reporting a stale figure
                double idle = (now - newest) / (double)Freq;
                if (idle > span / (n - 1) * 2.0) rate = (n - 1) / (span + idle);
                return rate;
            }
        }

        static long NextGap(Slot s)
        {
            long step = s.IntervalTicks;
            if (s.JitterTicks > 0)
                step += (long)((s.Rng.NextDouble() * 2.0 - 1.0) * s.JitterTicks);
            return step < 1 ? 1 : step;
        }

        static void Pace(Slot s, int id)
        {
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                                                  TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            try
            {
                long next;
                QueryPerformanceCounter(out next);
                next += s.PhaseTicks + NextGap(s);
                double msPerTick = 1000.0 / Freq;

                while (s.Run)
                {
                    long now;
                    QueryPerformanceCounter(out now);
                    if (s.StopAtTick != 0 && now >= s.StopAtTick) break;

                    long remain = next - now;
                    if (remain > 0)
                    {
                        // Sliced, never one long sleep: the loop re-checks
                        // s.Run every slice, so a stop takes effect within
                        // ~30 ms whatever the interval. The final sub-slice
                        // wait is still exact - pacing precision is untouched.
                        Wait(timer, Math.Min(remain * msPerTick, 30.0));
                        continue;
                    }

                    // The user is mid-press: a fixed-position beat would
                    // teleport the pointer out from under their finger and
                    // steal the button-up to the click spot - the thing they
                    // pressed never gets its click. Wait out the press
                    // (bounded, so a held-button playstyle keeps clicking).
                    // Two signals: the observer's userButtons AND a synchronous
                    // physical-key read. The observer lags the real event by a
                    // hook hop, so on its own a press could sneak through in
                    // that gap; GetAsyncKeyState reflects the hardware NOW and
                    // closes it.
                    if (s.FixedSnap && (userButtons != 0 || UserHolding()))
                    {
                        long defer0;
                        QueryPerformanceCounter(out defer0);
                        while ((userButtons != 0 || UserHolding()) && s.Run)
                        {
                            QueryPerformanceCounter(out now);
                            if (now - defer0 > Freq * 7 / 10) break;
                            Wait(timer, 3);
                        }
                        if (!s.Run) break;
                    }

                    bool fire = MayFire(s);
                    // Switched away, but this card keeps running: post the
                    // click into the window instead of synthesizing input.
                    // The messages carry their own client coordinates, so they
                    // land in the gate window from BEHIND - no focus theft at
                    // all, no pointer move, no restore needed. Focus-flicking
                    // per beat instead thrashes the user's window out of the
                    // foreground half the time with a fast card.
                    // The window just has to still exist.
                    if (!fire && s.Cfg.FlickFocus && s.Cfg.IsGated && s.FixedSnap
                        && !s.Cfg.IsCustomKey)
                    {
                        IntPtr g = s.Gate;
                        if (g != IntPtr.Zero && g != new IntPtr(1) && IsWindow(g))
                        {
                            int bx = s.Cfg.X, by = s.Cfg.Y;
                            if (s.Cfg.PosJitter > 0)
                            {
                                bx += s.Rng.Next(-s.Cfg.PosJitter, s.Cfg.PosJitter + 1);
                                by += s.Rng.Next(-s.Cfg.PosJitter, s.Cfg.PosJitter + 1);
                            }
                            if (PostClick(s, bx, by))
                            {
                                s.Count++;
                                MarkClick(s, now);
                                if (s.Limit > 0 && s.Count >= s.Limit) break;
                            }
                            next += NextGap(s);
                            QueryPerformanceCounter(out now);
                            if (next < now - s.IntervalTicks * 4) next = now + NextGap(s);
                            continue;
                        }
                    }
                    if (fire)
                    {
                        if (s.FixedSnap)   // snapshot: live edits apply next start
                        {
                            // Restore after EVERY click, whatever the pace.
                            // A floor here ("so the pointer isn't in flight
                            // constantly") buys something worse: at 100 ms the
                            // user's pointer sits captive at the click spot
                            // for the whole run.
                            // The save above is re-taken each cycle, so the
                            // restore target follows the user's hand instead of
                            // rubber-banding to where it was at the first click.
                            bool perClick = s.Cfg.RestoreCursor;
                            bool holdPending = false;   // press open, release owed
                            // Only a slot that moves the pointer needs the lock,
                            // and only for as long as the move and the click
                            // have to stay together. Slots clicking wherever the
                            // pointer already is were queueing behind each other
                            // for a resource none of them touched.
                            lock (PointerLock)
                            {
                                if (s.Cfg.RestoreCursor && !PointerIsOurs())
                                {
                                    // Refresh the shared home from the live
                                    // pointer. Not while the cursor is parked
                                    // on one of our spots, and not while it is
                                    // sitting on one either - between them, a
                                    // reading here is the user's hand.
                                    POINT cur;
                                    if (GetCursorPos(out cur)
                                        && !AtRunningSpot(cur.X, cur.Y))
                                    {
                                        HomeX = cur.X; HomeY = cur.Y;
                                        HomeValid = true;
                                    }
                                }
                                int px = s.Cfg.X, py = s.Cfg.Y;
                                if (s.Cfg.PosJitter > 0)
                                {
                                    px += s.Rng.Next(-s.Cfg.PosJitter, s.Cfg.PosJitter + 1);
                                    py += s.Rng.Next(-s.Cfg.PosJitter, s.Cfg.PosJitter + 1);
                                }
                                // The click CARRIES its coordinates: one event
                                // with MOVE|ABSOLUTE|button, indivisible by
                                // construction. A separate move - even in the
                                // same SendInput batch - was measured losing
                                // the race against a fast hand's delta flood:
                                // the down converted while the pointer was
                                // still at the user's position, and the click
                                // landed on their hand roughly once in ten
                                // under a frantic-speed flood.
                                // Backstop: park the real cursor on the spot
                                // FIRST (immediate, unqueued). Even in the
                                // rare case the OS drops the click's own move
                                // portion, the button then converts against a
                                // cursor that is already at the target.
                                // Into the ring BEFORE anything moves: both the
                                // gatekeeper and the virtualised-input fallback
                                // consult it from the hook thread, which can
                                // run before the next line of this one does
                                PushTarget(px, py);
                                SetCursorPos(px, py);
                                uint sent;
                                if (s.Cfg.IsCustomKey)
                                {
                                    // A keystroke can't carry a position, so
                                    // the move stays its own event, batched
                                    WriteMove(s.NativeBuf, px, py);
                                    if (s.HoldTicks > 0)
                                    {
                                        // move + keydown now, keyup after the hold
                                        sent = SendInput(2, s.NativeBuf, InputSize);
                                        holdPending = sent == 2;
                                    }
                                    else
                                    {
                                        sent = 0;
                                        for (int t = 0; t < 4 && sent == 0; t++)
                                        {
                                            if (t > 0) Thread.Sleep(0);
                                            sent = SendInput(3, s.NativeBuf, InputSize);
                                        }
                                        if (sent == 2)  // key left half-pressed
                                            SendInput(1, IntPtr.Add(s.NativeBuf, InputSize * 2), InputSize);
                                    }
                                }
                                else
                                {
                                    int nx, ny;
                                    Normalize(px, py, out nx, out ny);
                                    INPUT d = s.Buf[0], u = s.Buf[1];
                                    d.U.mi.dx = nx; d.U.mi.dy = ny;
                                    d.U.mi.dwFlags |= MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
                                    u.U.mi.dx = nx; u.U.mi.dy = ny;
                                    u.U.mi.dwFlags |= MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
                                    Marshal.StructureToPtr(d, s.NativeBuf, false);
                                    Marshal.StructureToPtr(u, IntPtr.Add(s.NativeBuf, InputSize), false);
                                    if (s.HoldTicks > 0)
                                    {
                                        // Press now, release after the hold.
                                        // Safe to leave the lock in between:
                                        // the release carries its own
                                        // coordinates, so another slot moving
                                        // the pointer can't misplace it.
                                        sent = SendInput(1, s.NativeBuf, InputSize);
                                        holdPending = sent == 1;
                                    }
                                    else
                                    {
                                        sent = 0;
                                        for (int t = 0; t < 4 && sent == 0; t++)
                                        {
                                            if (t > 0) Thread.Sleep(0);
                                            sent = SendInput(2, s.NativeBuf, InputSize);
                                        }
                                        // Half a click in: force the release so
                                        // a button can't be left stuck down
                                        if (sent == 1)
                                            SendInput(1, IntPtr.Add(s.NativeBuf, InputSize), InputSize);
                                    }
                                }
                                // Restore immediately. No conversion-waiting:
                                // a synchronized ledger's waits (spinning
                                // inside the pointer lock, in every event's
                                // path) back the whole input queue up -
                                // clicks arrive in a burst at the end and
                                // the stop hotkey lags seconds. The gatekeeper alone already
                                // makes strays impossible: clicks carry their
                                // coordinates, and any surfacing off-target
                                // is discarded at conversion.
                                // With a hold the release is still pending, so
                                // the pointer stays put until it lands - see
                                // just below, outside the lock.
                                if (sent >= 1 && perClick && s.HoldTicks == 0)
                                    ReturnCursor(s);
                            }
                            // The hold itself waits OUTSIDE the pointer lock -
                            // sitting on the lock for the press duration would
                            // stall every other slot. The release carries its
                            // own coordinates, so nothing can misplace it.
                            if (holdPending)
                            {
                                HoldThenRelease(s, timer, s.Cfg.IsCustomKey
                                    ? IntPtr.Add(s.NativeBuf, InputSize * 2)
                                    : IntPtr.Add(s.NativeBuf, InputSize));
                                if (s.Cfg.RestoreCursor) ReturnCursor(s);
                            }
                        }
                        else
                        {
                            // clicks live at slots [1] and [2]; [0] is the move
                            if (s.HoldTicks > 0)
                            {
                                if (SendInput(1, IntPtr.Add(s.NativeBuf, InputSize), InputSize) == 1)
                                    HoldThenRelease(s, timer, IntPtr.Add(s.NativeBuf, InputSize * 2));
                            }
                            else
                                SendInput(2, IntPtr.Add(s.NativeBuf, InputSize), InputSize);
                        }

                        s.Count++;
                        MarkClick(s, now);
                        if (s.Limit > 0 && s.Count >= s.Limit) break;
                    }

                    next += NextGap(s);
                    // If something stalled us badly, give up the debt rather
                    // than machine-gunning to catch up - that clumping is the
                    // exact defect this pacing exists to avoid.
                    QueryPerformanceCounter(out now);
                    if (next < now - s.IntervalTicks * 4) next = now + NextGap(s);
                }
            }
            finally
            {
                if (timer != IntPtr.Zero) CloseHandle(timer);
                s.Run = false;
                // Stopped mid-press: let go, or the button stays down for
                // real after the card is off
                if (s.HeldUp != IntPtr.Zero)
                {
                    SendInput(1, s.HeldUp, InputSize);
                    s.HeldUp = IntPtr.Zero;
                }
                // This pinned slot is done. Once the count hits zero the
                // gatekeeper discards any straggler still in the queue, so
                // the restore below is the LAST word on where the pointer
                // sits - without it, a straggler converting seconds later jumps
                // the pointer back to the click spot after everything was
                // "turned off".
                if (s.Pinned) Interlocked.Decrement(ref fixedRunning);
                // Restore first, then free under the pointer lock: a beat on
                // another thread can be inside the buffer right now, and a free
                // outside the lock would pull it out mid-send.
                ReturnCursor(s);
                lock (PointerLock)
                {
                    if (s.NativeBuf != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(s.NativeBuf);
                        s.NativeBuf = IntPtr.Zero;
                    }
                }
                int myId = s.Id;            // reorders move slots between ids
                lock (Running)
                {
                    Slot cur;
                    if (Running.TryGetValue(myId, out cur) && cur == s) Running.Remove(myId);
                }
                lock (Live) Live.Remove(s); // nothing held past this point
                // Its spot is ordinary screen again the moment it stops
                RefreshSpots();
                Action<int> st = Stopped;
                if (st != null) st(myId);
            }
        }

        // --- macro playback -------------------------------------------------

        static void PlayMacro(Slot s, int id)
        {
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero,
                                                  TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            IntPtr one = Marshal.AllocHGlobal(InputSize);
            try
            {
                Ev[] evs = s.Macro;
                double msPerTick = 1000.0 / Freq;

                while (s.Run)
                {
                    // Gating holds between loops rather than freezing
                    // mid-gesture, which would leave a drag half-finished
                    while (s.Run && !MayFire(s)) Thread.Sleep(20);
                    if (!s.Run) break;

                    long start;
                    QueryPerformanceCounter(out start);
                    if (s.StopAtTick != 0 && start >= s.StopAtTick) break;

                    // Where has the window gone since the take? Once per loop:
                    // dragging the window between repeats stays aligned without
                    // paying a rect query on every event. If the window has
                    // closed, the last known shift holds - snapping back to
                    // absolute mid-run would spray clicks somewhere unrelated.
                    if (s.Relative)
                    {
                        RECT cur;
                        if (IsWindow(s.RefWindow) && GetWindowRect(s.RefWindow, out cur))
                        {
                            s.RelDX = cur.L - s.RecWinX;
                            s.RelDY = cur.T - s.RecWinY;
                        }
                    }

                    bool aborted = false;
                    for (int i = 0; i < evs.Length && s.Run; i++)
                    {
                        long due = start + evs[i].Off;
                        while (s.Run)
                        {
                            long now;
                            QueryPerformanceCounter(out now);
                            long remain = due - now;
                            if (remain <= 0) break;
                            Wait(timer, Math.Min(remain * msPerTick, 30.0));   // sliced: stop within ~30 ms
                        }
                        if (!s.Run) break;
                        // Per event, not only between loops: this loop's own
                        // clicks activate the target window, so a check that
                        // waits for the loop to finish reads the gate as open
                        // again the instant our click re-fronts it - alt-tab
                        // away from a gated macro and it stole focus straight
                        // back. Aborting here leaves the user's window alone;
                        // ReleaseHeld below lets go of anything mid-gesture.
                        if (!MayFire(s)) { aborted = true; break; }
                        Emit(s, evs[i], one);
                    }
                    ReleaseHeld(s, one);
                    if (!s.Run) break;
                    // An interrupted run isn't a repeat - straight back to the
                    // gate wait at the top, resuming fresh when the window is
                    if (aborted) continue;

                    s.Count++;
                    long tick; QueryPerformanceCounter(out tick);
                    MarkClick(s, tick);
                    if (s.Limit > 0 && s.Count >= s.Limit) break;

                    long gap = s.LoopGapTicks;
                    if (s.JitterTicks > 0)
                        gap += (long)((s.Rng.NextDouble() * 2.0 - 1.0) * s.JitterTicks);
                    if (gap > 0)
                    {
                        long until;
                        QueryPerformanceCounter(out until);
                        until += gap;
                        while (s.Run)
                        {
                            long now;
                            QueryPerformanceCounter(out now);
                            long remain = until - now;
                            if (remain <= 0) break;
                            Wait(timer, Math.Min(remain * msPerTick, 30.0));   // sliced: stop within ~30 ms
                        }
                    }
                }
            }
            finally
            {
                ReleaseHeld(s, one);
                Marshal.FreeHGlobal(one);
                if (timer != IntPtr.Zero) CloseHandle(timer);
                s.Run = false;
                int myId = s.Id;            // reorders move slots between ids
                lock (Running)
                {
                    Slot cur;
                    if (Running.TryGetValue(myId, out cur) && cur == s) Running.Remove(myId);
                }
                lock (Live) Live.Remove(s); // nothing held past this point
                ArmStopOnInput();
                Action<int> st = Stopped;
                if (st != null) st(myId);
            }
        }

        static void Emit(Slot s, Ev e, IntPtr one)
        {
            lock (PointerLock)
            {
                switch (e.Type)
                {
                    case 0:
                        MoveTo(one, e.A + s.RelDX, e.B + s.RelDY);
                        break;
                    case 1:
                    case 2:
                    {
                        uint flag, data;
                        MouseFlag(e.A, e.Type == 1, out flag, out data);
                        SendMouse(one, flag, data);
                        if (e.Type == 1) { if (!s.Held.Contains(e.A)) s.Held.Add(e.A); }
                        else s.Held.Remove(e.A);
                        break;
                    }
                    case 3:
                    case 4:
                    {
                        var inp = new INPUT();
                        inp.type = INPUT_KEYBOARD;
                        inp.U.ki.wVk = (ushort)e.A;
                        // Scancode filled in as well: plenty of games read the
                        // scancode rather than the virtual key, and a zero
                        // there made replayed keys invisible to them
                        inp.U.ki.wScan = (ushort)MapVirtualKeyW((uint)e.A, 0);
                        inp.U.ki.dwFlags = (e.Type == 4 ? KEYEVENTF_KEYUP : 0)
                                         | (e.B != 0 ? KEYEVENTF_EXTENDEDKEY : 0);
                        inp.U.ki.dwExtraInfo = SelfMark;
                        Marshal.StructureToPtr(inp, one, false);
                        SendInput(1, one, InputSize);
                        if (e.Type == 3) { if (!s.Held.Contains(e.A + 100)) s.Held.Add(e.A + 100); }
                        else s.Held.Remove(e.A + 100);
                        break;
                    }
                    case 5:
                        SendMouse(one, MOUSEEVENTF_WHEEL, (uint)e.A);
                        break;
                }
            }
        }

        // Let go of anything the take pressed and didn't release - run after
        // every loop and again on stop, so neither a mid-take stop nor a
        // recording that simply never released a key can strand it down.
        static void ReleaseHeld(Slot s, IntPtr one)
        {
            for (int i = s.Held.Count - 1; i >= 0; i--)
            {
                int code = s.Held[i];
                var e = new Ev();
                if (code >= 100) { e.Type = 4; e.A = code - 100; }
                else { e.Type = 2; e.A = code; }
                Emit(s, e, one);
            }
            s.Held.Clear();
        }

        static void MouseFlag(int button, bool down, out uint flag, out uint data)
        {
            data = 0;
            switch (button)
            {
                case 1:  flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case 2:  flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
                case 3:  flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
                case 4:  flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
                default: flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            }
        }

        static uint SendMouse(IntPtr one, uint flags, uint data)
        {
            var inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.U.mi.dwFlags = flags;
            inp.U.mi.mouseData = data;
            inp.U.mi.dwExtraInfo = SelfMark;
            Marshal.StructureToPtr(inp, one, false);
            return SendInput(1, one, InputSize);
        }

        // Move the pointer as a real input event.
        //
        // SetCursorPos won't do here: it relocates the cursor without
        // generating any mouse input at all, so a macro looks like it is
        // working while every click lands in the same place in-game.
        // Anything reading Raw Input or DirectInput - which is most games -
        // therefore never learns the pointer moved, and keeps resolving clicks
        // against wherever it last saw it. Measured against a raw-input
        // listener: SetCursorPos produces 0 motion events while every click
        // comes through.
        //
        // SendInput goes through the ordinary input queue, so raw consumers
        // see it. Coordinates are absolute over the whole virtual desktop,
        // normalised to 0..65535, which keeps multi-monitor setups honest.
        // Screen pixels to the 0..65535 virtual-desktop grid SendInput wants.
        static void Normalize(int x, int y, out int nx, out int ny)
        {
            int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN) - 1);
            int vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN) - 1);
            nx = (int)(((long)(x - vx) * 65535 + vw / 2) / vw);
            ny = (int)(((long)(y - vy) * 65535 + vh / 2) / vh);
        }

        // Marshal an absolute move for (x, y) into `at` - shared between the
        // standalone MoveTo and the move+keystroke batch in the pace loop.
        static void WriteMove(IntPtr at, int x, int y)
        {
            int nx, ny;
            Normalize(x, y, out nx, out ny);
            var inp = new INPUT();
            inp.type = INPUT_MOUSE;
            inp.U.mi.dx = nx;
            inp.U.mi.dy = ny;
            inp.U.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
            inp.U.mi.dwExtraInfo = SelfMark;
            Marshal.StructureToPtr(inp, at, false);
        }

        static bool MoveTo(IntPtr one, int x, int y) { return MoveTo(one, x, y, true); }

        static bool MoveTo(IntPtr one, int x, int y, bool settle)
        {
            WriteMove(one, x, y);
            // A saturated input queue (a very fast hand) can REJECT the event;
            // the caller must know, because a click sent without its move
            // lands wherever the pointer happens to be
            bool sent = SendInput(1, one, InputSize) == 1;

            // The absolute move lands on a 65535-step grid, so it can round to
            // a neighbouring pixel. Settle the OS cursor exactly where asked -
            // the input event above is what games consume; this only makes the
            // visible cursor and GetCursorPos agree with the request.
            //
            // NOT for the restore-to-home move: SetCursorPos acts immediately,
            // leapfrogging the queued click that precedes it. With the queue
            // running behind a fast hand, the settle yanked the cursor home
            // BEFORE the click event processed - one stray click at the user's
            // position. The queued absolute above keeps its place in line, so
            // without the settle the click can only ever land at the spot; the
            // one-pixel grid rounding is invisible on a hand position.
            if (settle && sent) SetCursorPos(x, y);
            return sent;
        }

        // A waitable timer created with HIGH_RESOLUTION waits to ~0.1 ms while
        // costing nothing; only the last half millisecond is spun. The old
        // sleep/yield/spin ladder burned ~3 ms of core per click - 6.7% of a
        // core for a single 50 ms clicker, against 0.3% this way.
        static void Wait(IntPtr timer, double ms)
        {
            if (timer != IntPtr.Zero && ms > 0.5)
            {
                long due = -(long)((ms - 0.2) * 10000.0);   // 100 ns units, relative
                if (due > -1) due = -1;
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
                {
                    WaitForSingleObject(timer, (uint)(ms + 10.0));
                    return;
                }
            }
            if (ms > 3.0) Thread.Sleep(1);
            else if (ms > 1.0) Thread.Yield();
            else Thread.SpinWait(40);
        }
    }
}
