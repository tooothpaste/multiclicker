// ===========================================================================
//  Log - a small append-only session log
// ---------------------------------------------------------------------------
//  Answers how a session ended when the process can't say. A hang or a hard
//  kill leaves no crash event and no error report; a line at startup, a line
//  at exit, a heartbeat in between, and a line from the crash handler leave
//  a trail either way.
//
//  Plain text in the data folder, next to the INI. Capped: past half a
//  megabyte the older half is dropped on the next write.
// ===========================================================================

using System;
using System.IO;
using System.Linq;

namespace Polyclicker
{
    static class Log
    {
        static readonly object Gate = new object();
        static string _path;
        const long MaxBytes = 512 * 1024;

        public static string File_
        {
            get { return _path ?? (_path = Path.Combine(AppConfig.Dir, "log.txt")); }
        }

        public static void Line(string msg)
        {
            // The log must never be the thing that takes the app down
            try
            {
                lock (Gate)
                {
                    var fi = new FileInfo(File_);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string[] all = File.ReadAllLines(File_);
                        File.WriteAllLines(File_, all.Skip(all.Length / 2));
                    }
                    File.AppendAllText(File_,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg
                        + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
