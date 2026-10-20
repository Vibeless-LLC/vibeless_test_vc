using System.Diagnostics;
using System.Globalization;
using Gridline.IO;

// P-7 probe (§12): loads a .gridline file in a fresh process and reports load times and private memory.
// Usage: dotnet Gridline.PerfProbe.dll <file> [runs]
// Output (one line, invariant culture): "<ms1>,<ms2>,...;<privateMB>;<cellCount>"
// Run as its own process by tests/Gridline.IO.Tests (P-7) so the measurement contains only the engine and the loaded
// workbook, not the test host, xunit, or other tests' garbage (DECISIONS D-30).
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2) { Console.Error.WriteLine("usage: Gridline.PerfProbe <file> [runs]"); return 1; }
        int runs = args.Length == 2 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 5;
        string path = args[0];

        GridlineFile.LoadFromPath(path); // warm-up, discarded
        var times = new List<double>();
        Gridline.Core.Workbook? loaded = null;
        for (int i = 0; i < runs; i++)
        {
            loaded = null; // release the previous load before the next one
            long t0 = Stopwatch.GetTimestamp();
            loaded = GridlineFile.LoadFromPath(path);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        double mb = process.PrivateMemorySize64 / (1024.0 * 1024.0);
        int cells = loaded!.GetUsedAddresses().Count; // keeps the workbook alive past the measurement
        Console.WriteLine(string.Join(",", times.Select(t => t.ToString("F1", CultureInfo.InvariantCulture)))
            + ";" + mb.ToString("F1", CultureInfo.InvariantCulture) + ";" + cells.ToString(CultureInfo.InvariantCulture));
        return 0;
    }
}
