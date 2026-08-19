// ===========================================================================
//  Macro - the recorded-timeline file format
// ---------------------------------------------------------------------------
//  A plain-text timeline:
//
//      # Polyclicker macro v1
//      # window L T W H              (where the take happened, optional)
//      offsetMs type a b             (0 move, 1 down, 2 up, 3 keydown,
//                                     4 keyup, 5 wheel)
//
//  The parser treats every # line except "# window" as commentary, so takes
//  carrying the older "# MultiAutoClicker macro v1" header load unchanged;
//  MigrateHeaders rewrites them to the current header at startup.
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Polyclicker
{
    // One recorded event. Off is in QPC ticks from the start of the take.
    struct Ev
    {
        public long Off;
        public byte Type;
        public int A, B;
    }

    static class MacroFile
    {
        public static void Save(string path, IList<Ev> evs, long freq,
                                bool winValid, int wx, int wy, int ww, int wh)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            using (var w = new StreamWriter(path, false, Encoding.UTF8))
            {
                w.WriteLine("# Polyclicker macro v1");
                w.WriteLine("# recorded " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                w.WriteLine("# fields: offsetMs type a b   (type 0 move,1 down,2 up,3 keydown,4 keyup,5 wheel)");
                if (winValid)
                    w.WriteLine("# window " + wx + " " + wy + " " + ww + " " + wh);
                for (int i = 0; i < evs.Count; i++)
                {
                    double ms = evs[i].Off * 1000.0 / freq;
                    w.WriteLine(ms.ToString("F3", CultureInfo.InvariantCulture) + " " +
                                evs[i].Type + " " + evs[i].A + " " + evs[i].B);
                }
            }
        }

        // Old takes carry "# MultiAutoClicker macro v1" as their first line.
        // The parser does not care - # lines are commentary - but the files
        // name an app this one no longer is, so startup rewrites the header
        // in place. Idempotent; a file that cannot be read is left alone.
        public static void MigrateHeaders(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir, "*.macro"))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(f);
                        if (lines.Length == 0 || lines[0] != "# MultiAutoClicker macro v1")
                            continue;
                        lines[0] = "# Polyclicker macro v1";
                        File.WriteAllLines(f, lines, Encoding.UTF8);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public static Ev[] Load(string path, long freq)
        {
            WindowInfo unused;
            return Load(path, freq, out unused);
        }

        // Where the take happened, from the "# window" header - the anchor for
        // window-relative playback. Absent on takes recorded before the header
        // existed, and on takes that never touched a window.
        public struct WindowInfo
        {
            public bool Valid;
            public int X, Y, W, H;
        }

        public static Ev[] Load(string path, long freq, out WindowInfo win)
        {
            win = new WindowInfo();
            try
            {
                if (!File.Exists(path)) return null;
                var list = new List<Ev>();
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("# window "))
                    {
                        string[] p = line.Substring(9).Split(' ');
                        if (p.Length >= 4)
                        {
                            win.X = int.Parse(p[0], CultureInfo.InvariantCulture);
                            win.Y = int.Parse(p[1], CultureInfo.InvariantCulture);
                            win.W = int.Parse(p[2], CultureInfo.InvariantCulture);
                            win.H = int.Parse(p[3], CultureInfo.InvariantCulture);
                            win.Valid = true;
                        }
                        continue;
                    }
                    if (line.Length == 0 || line[0] == '#') continue;
                    string[] f = line.Split(' ');
                    if (f.Length < 4) continue;
                    var e = new Ev();
                    double ms = double.Parse(f[0], CultureInfo.InvariantCulture);
                    e.Off = (long)(freq * (ms / 1000.0));
                    e.Type = byte.Parse(f[1], CultureInfo.InvariantCulture);
                    e.A = int.Parse(f[2], CultureInfo.InvariantCulture);
                    e.B = int.Parse(f[3], CultureInfo.InvariantCulture);
                    list.Add(e);
                }
                return list.ToArray();
            }
            catch { return null; }
        }

        // Strip anything Windows won't accept in a filename
        public static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in (s ?? "").Trim())
                sb.Append("\\/:*?\"<>|".IndexOf(c) >= 0 ? '-' : c);
            string outp = sb.ToString();
            while (outp.Contains("  ")) outp = outp.Replace("  ", " ");
            return outp.Trim(' ', '.');
        }

        // "Fishing.macro", then "Fishing 2.macro" - re-recording never destroys
        // the take you already have, which matters when it took effort to
        // perform.
        public static string UniquePath(string dir, string baseName)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "Recording";
            string path = Path.Combine(dir, baseName + ".macro");
            if (!File.Exists(path)) return path;
            int n = 2;
            while (File.Exists(Path.Combine(dir, baseName + " " + n + ".macro"))) n++;
            return Path.Combine(dir, baseName + " " + n + ".macro");
        }
    }
}
