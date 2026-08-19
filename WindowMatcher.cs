// ===========================================================================
//  WindowMatcher - resolve a saved gate string to a live window
// ---------------------------------------------------------------------------
//  A gate takes two forms: "ahk_exe name.exe" matches the owning process
//  (the prefix is a fixed format token saved in configs), anything else
//  matches the start of the window title.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Polyclicker
{
    static class WindowMatcher
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        delegate bool EnumProc(IntPtr h, IntPtr l);

        public static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder exe, ref int size);

        // Asked once a second for every open window (the presence scan), so it
        // must stay cheap: one QUERY_LIMITED handle per call, not the full
        // system process table that Process.GetProcessById snapshots.
        public static string ExeOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return "";
            IntPtr p = OpenProcess(0x1000, false, pid);    // QUERY_LIMITED_INFORMATION
            if (p == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int n = sb.Capacity;
                if (!QueryFullProcessImageNameW(p, 0, sb, ref n)) return "";
                string path = sb.ToString(0, n);
                int cut = path.LastIndexOf('\\');
                return cut >= 0 ? path.Substring(cut + 1) : path;
            }
            finally { CloseHandle(p); }
        }

        public static bool Matches(string spec, IntPtr h)
        {
            spec = (spec ?? "").Trim();
            if (spec.Length == 0 || h == IntPtr.Zero) return false;
            if (spec.StartsWith("ahk_exe ", StringComparison.OrdinalIgnoreCase))
            {
                string want = spec.Substring(8).Trim();
                return string.Equals(ExeOf(h), want, StringComparison.OrdinalIgnoreCase);
            }
            return TitleOf(h).StartsWith(spec, StringComparison.OrdinalIgnoreCase);
        }

        // Is the gate's window the one in front right now?
        public static bool IsActive(string spec)
        {
            return Matches(spec, GetForegroundWindow());
        }

        public static IntPtr Foreground() { return GetForegroundWindow(); }

        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);

        // A real, still-open window - not null, not the engine's gated-out
        // sentinel, not a handle whose window has since closed.
        public static bool IsLive(IntPtr h)
        {
            return h != IntPtr.Zero && h != new IntPtr(1) && IsWindow(h);
        }

        public static IntPtr Find(string spec)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (IsWindowVisible(h) && Matches(spec, h)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        // Match a gate against an OpenWindows snapshot (title, exe) instead of
        // enumerating again - one scan serves every gated card.
        public static bool AnyOpen(string spec, List<KeyValuePair<string, string>> wins)
        {
            spec = (spec ?? "").Trim();
            if (spec.Length == 0) return false;
            if (spec.StartsWith("ahk_exe ", StringComparison.OrdinalIgnoreCase))
            {
                string want = spec.Substring(8).Trim();
                foreach (var w in wins)
                    if (string.Equals(w.Value, want, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }
            foreach (var w in wins)
                if (w.Key.StartsWith(spec, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public static void Activate(IntPtr h)
        {
            if (h != IntPtr.Zero) SetForegroundWindow(h);
        }

        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);

        public static IntPtr RootOf(IntPtr h)
        {
            return h == IntPtr.Zero ? h : GetAncestor(h, 2);     // GA_ROOT
        }

        // Every window a person would recognise as open: visible, titled, and
        // not ours - gating a clicker to this app's own dialogs is never what
        // anyone means. For the "Pick..." list in the advanced options dialog.
        public static List<KeyValuePair<string, string>> OpenWindows(IntPtr except)
        {
            uint self = (uint)Process.GetCurrentProcess().Id;
            var outp = new List<KeyValuePair<string, string>>();
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h) || h == except) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == self) return true;
                string title = TitleOf(h);
                if (title.Length == 0) return true;
                outp.Add(new KeyValuePair<string, string>(title, ExeOf(h)));
                return true;
            }, IntPtr.Zero);
            return outp;
        }
    }

    // One-shot "click the window you want" capture: a temporary low-level
    // mouse hook takes the very next left-click anywhere on screen, resolves
    // the top-level window under it, and swallows the click so the target app
    // never sees it. Right-click (or clicking this app) cancels.
    static class WindowPick
    {
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT { public int X, Y; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookExW(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point p);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;

        static IntPtr _hook;
        static HookProc _proc;
        static System.Windows.Forms.Control _marshal;
        static Action<IntPtr> _done;

        public static void Once(System.Windows.Forms.Control marshal, Action<IntPtr> done)
        {
            if (_hook != IntPtr.Zero) return;
            _marshal = marshal;
            _done = done;
            _proc = Callback;
            _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero) Finish(IntPtr.Zero);
        }

        static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int m = wParam.ToInt32();
                if (m == WM_LBUTTONDOWN || m == WM_RBUTTONDOWN)
                {
                    IntPtr picked = IntPtr.Zero;
                    if (m == WM_LBUTTONDOWN)
                    {
                        var info = (MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal
                            .PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                        IntPtr root = WindowMatcher.RootOf(
                            WindowFromPoint(new System.Drawing.Point(info.X, info.Y)));
                        uint pid;
                        GetWindowThreadProcessId(root, out pid);
                        // clicking our own window reads as "never mind"
                        if (pid != (uint)System.Diagnostics.Process.GetCurrentProcess().Id)
                            picked = root;
                    }
                    Finish(picked);
                    return new IntPtr(1);       // the click never reaches anyone
                }
            }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        static void Finish(IntPtr picked)
        {
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            Action<IntPtr> cb = _done;
            System.Windows.Forms.Control m = _marshal;
            _done = null; _marshal = null;
            if (cb == null) return;
            if (m != null && m.IsHandleCreated)
                try { m.BeginInvoke(cb, picked); return; } catch { }
            cb(picked);
        }
    }
}
