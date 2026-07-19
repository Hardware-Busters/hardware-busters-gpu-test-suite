using System.Diagnostics;

namespace GpuSuite.Load;

/// <summary>
/// Best-effort NVIDIA GPU clock locking via <c>nvidia-smi</c>. Locking the graphics clock to a fixed
/// value removes the boost-clock hysteresis that otherwise makes power a bistable function of the load
/// (the same workload can sit at low or boosted power), which is what lets the closed-loop power
/// controller hold a precise, steady wattage. Requires Administrator (the bench runs elevated); it is a
/// no-op returning false when nvidia-smi is absent or permission is denied. Always pair Lock with Reset.
/// </summary>
public static class NvidiaClockLock
{
    /// <summary>Lock the graphics clock to a fixed MHz (e.g. 1500). Returns false (with detail) if
    /// nvidia-smi is missing or the user lacks permission.</summary>
    public static bool TryLock(int graphicsMhz, out string detail)
        => Run($"-lgc {graphicsMhz},{graphicsMhz}", out detail);

    /// <summary>Reset the locked graphics clock to default (best-effort).</summary>
    public static void Reset() => Run("-rgc", out _);

    private static bool Run(string args, out string detail)
    {
        detail = "";
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null) { detail = "nvidia-smi not found"; return false; }
            string outp = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(8000);
            detail = outp;
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }
}
