// ===========================================================================
//  Polyclicker - the entry point
// ---------------------------------------------------------------------------
//  Single instance, crash handling, and the one promise that must survive
//  any exit: a dying app never leaves a button held. The crash handler and
//  the run's finally send the releases a killed worker never got to.
//
//  Build (no SDK needed - this compiler ships with Windows):
//    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo
//        /optimize+ /target:winexe /out:Polyclicker.exe *.cs
//        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll
// ===========================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Forms;

namespace Polyclicker
{
    static class Program
    {
        // The pending-save flush the crash path runs; MainForm installs it.
        // Saves are debounced 400 ms, and a dying process never fires that
        // timer - without this, the edit someone made just before a crash
        // silently reverts on the next launch.
        public static Action SaveOnCrash;

        static Mutex mutex;
        static EventWaitHandle exitSignal;      // held so the wait stays registered

        [STAThread]
        static void Main()
        {
            // One instance, #SingleInstance Force semantics like the script:
            // the NEW launch wins. Two copies would fight over the same INI
            // and both hook the keyboard - but "already running" is the wrong
            // answer to a relaunch, because relaunching is what people try
            // when the running instance has wedged.
            bool fresh;
            mutex = new Mutex(true, "PolyclickerSingleInstance", out fresh);
            if (!fresh && !Displace()) return;

            // An unhandled exception on ANY background thread kills the
            // process without running the workers' finallys - the only place
            // a held press is released. Let go of everything, save, then die.
            AppDomain.CurrentDomain.UnhandledException += delegate(object sndr, UnhandledExceptionEventArgs ue)
            {
                Log.Line("CRASH: " + ue.ExceptionObject);
                try { Engine.ReleaseAllHeld(); } catch { }
                Action flush = SaveOnCrash;
                if (flush != null) { try { flush(); } catch { } }
            };

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // The shared double-buffer context only CACHES its buffer for
            // controls up to MaximumBuffer; anything larger gets a fresh
            // DIB allocated and torn down on every paint. The card surface
            // is far past the small default, so raise the ceiling to the
            // monitor and let one cached buffer serve every repaint.
            System.Drawing.BufferedGraphicsManager.Current.MaximumBuffer =
                SystemInformation.PrimaryMonitorSize;

            Log.Line("started, pid " + Process.GetCurrentProcess().Id + ", exe of "
                     + System.IO.File.GetLastWriteTime(
                           Process.GetCurrentProcess().MainModule.FileName)
                       .ToString("yyyy-MM-dd HH:mm"));

            var form = new MainForm();
            ListenForDisplacement(form);
            try
            {
                Application.Run(form);
            }
            finally
            {
                // Normal exits released everything in Engine.Shutdown; a
                // second pass costs nothing. Abnormal ones land here with
                // presses still open.
                try { Engine.ReleaseAllHeld(); } catch { }
                Log.Line("exited");
            }
        }

        // A newer launch asked us to leave. Close through the form so the
        // ordinary save-and-shutdown path runs; the newer instance only
        // resorts to Kill when this pump is too wedged to hear it.
        static void ListenForDisplacement(Form form)
        {
            try
            {
                exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset,
                                                 "PolyclickerExit");
                exitSignal.WaitOne(0);      // drain a stale signal nobody heard
                ThreadPool.RegisterWaitForSingleObject(exitSignal, delegate
                {
                    Log.Line("displacement requested by a newer launch - closing");
                    try { form.BeginInvoke((MethodInvoker)delegate { form.Close(); }); }
                    catch { }
                }, null, -1, true);
            }
            catch { }
        }

        // Returns true holding the mutex.
        static bool Displace()
        {
            try
            {
                using (var bye = new EventWaitHandle(false, EventResetMode.AutoReset,
                                                     "PolyclickerExit"))
                    bye.Set();
            }
            catch { }
            if (Acquire(2000)) return true;
            // Deaf: hung UI, dead pump, possibly a worker still clicking.
            // Its held input can't be rescued from out here, but the healthy
            // instance replacing it can be stopped normally. The log line
            // matters: a wedged instance never writes its own exit, so this
            // is the only record of how it ended.
            Log.Line("running instance did not answer the exit signal - killing it");
            KillOthers();
            return Acquire(1500);
        }

        static bool Acquire(int ms)
        {
            try { return mutex.WaitOne(ms); }
            catch (AbandonedMutexException) { return true; }   // holder died - ours now
        }

        static void KillOthers()
        {
            try
            {
                Process me = Process.GetCurrentProcess();
                string myExe = me.MainModule.FileName;
                foreach (Process p in Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id) { p.Dispose(); continue; }
                    try
                    {
                        if (string.Equals(p.MainModule.FileName, myExe,
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            p.Kill();
                            p.WaitForExit(1000);
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }
    }
}
