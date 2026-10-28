using System.Diagnostics;
using GpuSuite.Measurement.Monitoring;

namespace GpuSuite.Cli;

internal static partial class Program
{
    // This path runs before configuration/provider construction. Discovery never opens hardware.
    private static int Monitor(string[] args)
    {
        int index = Array.IndexOf(args, "--pid");
        if (index >= 0)
        {
            if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int pid) || pid <= 0)
            { Console.Error.WriteLine("monitor --pid requires a positive process ID."); return 2; }
            try
            {
                var json = MonitoringPool.ReadJson(pid);
                if (json is null) { Console.Error.WriteLine("Pool is busy, incomplete, or has an unsupported schema."); return 1; }
                Console.WriteLine(json);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine($"Pool unavailable: {ex.Message}"); return 1; }
        }

        Console.WriteLine("PID\tPOOL (read-only, current Windows session)");
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.ProcessName is not ("gpusuite" or "GpuSuite.App")) continue;
                    if (MonitoringPool.ReadJson(process.Id) is not null)
                        Console.WriteLine($"{process.Id}\t{MonitoringPool.Name(process.Id)}");
                }
                catch { /* Process closed, is inaccessible, or has no pool. */ }
            }
        }
        return 0;
    }
}
