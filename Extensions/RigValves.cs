// Per-rig valve scale, so a given "ms" gives the same drop on both ports.
// Every task valve pulse (2AFC and sequence: cue drops, rewards, Water buttons) is multiplied by
// the scale for that port. Set it in the Valve calibration window ("From log" fills it from the
// latest L and R rows with the same pulse ms). Stored in rig_valves.txt next to the workflow:
//   LeftScale=1
//   RightScale=0.8
// The file is re-read whenever it changes, so a running task picks up a new scale on its next drop.
// Calibration pulses themselves are NOT scaled (they measure the raw valve).
//
// Written for C# 5.

using System;
using System.Globalization;
using System.IO;

public static class RigValves
{
    public const string FileName = "rig_valves.txt";
    static readonly object gate = new object();
    static readonly double[] scale = { 1, 1 };
    static DateTime loadedStamp = DateTime.MinValue;

    public static string FilePath { get { return Path.GetFullPath(FileName); } }

    // Scale for port 0 (L) or 1 (R).
    public static double Scale(int port)
    {
        lock (gate) { Refresh(); return scale[port]; }
    }

    // Raw valve ms for a task value, at least 1 ms when the value is > 0.
    public static int Ms(int port, int ms)
    {
        if (ms <= 0) return 0;
        return Math.Max(1, Math.Min(65535, (int)Math.Round(ms * Scale(port))));
    }

    public static void Save(double left, double right)
    {
        lock (gate)
        {
            File.WriteAllText(FilePath, string.Format(CultureInfo.InvariantCulture,
                "# Valve scale per port: task ms x scale = valve open time. Set from valve calibration.\r\n" +
                "# Saved {0:yyyy-MM-dd HH:mm}\r\nLeftScale={1:0.###}\r\nRightScale={2:0.###}\r\n",
                DateTime.Now, left, right));
            loadedStamp = DateTime.MinValue;
            Refresh();
        }
    }

    static void Refresh()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) { scale[0] = scale[1] = 1; loadedStamp = DateTime.MinValue; return; }
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == loadedStamp) return;
            double l = 1, r = 1;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("#")) continue;
                var kv = line.Split('=');
                if (kv.Length != 2) continue;
                double v;
                if (!double.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v <= 0 || v > 5) continue;
                if (kv[0].Trim().Equals("LeftScale", StringComparison.OrdinalIgnoreCase)) l = v;
                if (kv[0].Trim().Equals("RightScale", StringComparison.OrdinalIgnoreCase)) r = v;
            }
            scale[0] = l; scale[1] = r;
            loadedStamp = stamp;
        }
        catch (IOException) { }
    }
}
