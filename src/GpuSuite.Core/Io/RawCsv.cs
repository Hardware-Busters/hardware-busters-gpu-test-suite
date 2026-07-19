using System.Globalization;
using System.Text;
using GpuSuite.Core.Models;

namespace GpuSuite.Core.Io;

/// <summary>
/// Writers/readers for the three raw per-run sample files. Explicit columns (no
/// reflection) so the schema is stable, deterministic and self-documenting.
/// Invariant culture throughout — never locale-dependent decimal separators.
/// </summary>
public static class RawCsv
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double? v) => v is double d && !double.IsNaN(d) ? d.ToString("0.####", Inv) : "";
    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.####", Inv);

    // ---------- Frames ----------
    public const string FrameHeader = "TimeSec,FrameTimeMs,Fps,DisplayedTimeMs,GpuBusyMs";

    public static void WriteFrames(string path, IEnumerable<FrameSample> frames)
    {
        using var w = NewWriter(path);
        w.WriteLine(FrameHeader);
        foreach (var f in frames)
            w.WriteLine($"{F(f.TimeSec)},{F(f.FrameTimeMs)},{F(f.Fps)},{F(f.DisplayedTimeMs)},{F(f.GpuBusyMs)}");
    }

    public static List<FrameSample> ReadFrames(string path)
    {
        var list = new List<FrameSample>();
        foreach (var cols in ReadRows(path))
        {
            if (cols.Length < 2) continue;
            list.Add(new FrameSample
            {
                TimeSec = D(cols, 0),
                FrameTimeMs = D(cols, 1),
                DisplayedTimeMs = N(cols, 3),
                GpuBusyMs = N(cols, 4)
            });
        }
        return list;
    }

    // ---------- Power ----------
    public const string PowerHeader =
        "TimeSec,GpuTotalW,PcieSlot12vW,PcieSlot3v3W,Pcie8pin1W,Pcie8pin2W,Pcie8pin3W,CpuTotalW,Eps1W,Eps2W,Atx12vW,SystemTotalW";

    public static void WritePower(string path, IEnumerable<PowerSample> samples)
    {
        using var w = NewWriter(path);
        w.WriteLine(PowerHeader);
        foreach (var s in samples)
            w.WriteLine(string.Join(',',
                F(s.TimeSec), F(s.GpuTotalW), F(s.PcieSlot12vW), F(s.PcieSlot3v3W),
                F(s.Pcie8pin1W), F(s.Pcie8pin2W), F(s.Pcie8pin3W), F(s.CpuTotalW),
                F(s.Eps1W), F(s.Eps2W), F(s.Atx12vW), F(s.SystemTotalW)));
    }

    public static List<PowerSample> ReadPower(string path)
    {
        var list = new List<PowerSample>();
        foreach (var c in ReadRows(path))
        {
            if (c.Length < 2) continue;
            list.Add(new PowerSample
            {
                TimeSec = D(c, 0),
                GpuTotalW = N(c, 1),
                PcieSlot12vW = N(c, 2),
                PcieSlot3v3W = N(c, 3),
                Pcie8pin1W = N(c, 4),
                Pcie8pin2W = N(c, 5),
                Pcie8pin3W = N(c, 6),
                CpuTotalW = N(c, 7),
                Eps1W = N(c, 8),
                Eps2W = N(c, 9),
                Atx12vW = N(c, 10),
                SystemTotalW = N(c, 11)
            });
        }
        return list;
    }

    // ---------- Telemetry ----------
    public const string TelemetryHeader =
        "TimeSec,GpuTempC,GpuHotspotC,GpuVramTempC,GpuCoreClockMhz,GpuMemClockMhz,GpuLoadPct,GpuVoltageV,GpuBoardPowerW,VramUsedMb,FanRpm1,FanRpm2,FanRpm3,FanRpm4,FanPct1,CpuTempC,CpuLoadPct,CpuPowerW,CpuClockMhz";

    public static void WriteTelemetry(string path, IEnumerable<TelemetrySample> samples)
    {
        using var w = NewWriter(path);
        w.WriteLine(TelemetryHeader);
        foreach (var s in samples)
            w.WriteLine(string.Join(',',
                F(s.TimeSec), F(s.GpuTempC), F(s.GpuHotspotC), F(s.GpuVramTempC),
                F(s.GpuCoreClockMhz), F(s.GpuMemClockMhz), F(s.GpuLoadPct), F(s.GpuVoltageV),
                F(s.GpuBoardPowerW), F(s.VramUsedMb), F(s.FanRpm1), F(s.FanRpm2), F(s.FanRpm3),
                F(s.FanRpm4), F(s.FanPct1), F(s.CpuTempC), F(s.CpuLoadPct), F(s.CpuPowerW), F(s.CpuClockMhz)));
    }

    public static List<TelemetrySample> ReadTelemetry(string path)
    {
        var list = new List<TelemetrySample>();
        foreach (var c in ReadRows(path))
        {
            if (c.Length < 2) continue;
            list.Add(new TelemetrySample
            {
                TimeSec = D(c, 0),
                GpuTempC = N(c, 1),
                GpuHotspotC = N(c, 2),
                GpuVramTempC = N(c, 3),
                GpuCoreClockMhz = N(c, 4),
                GpuMemClockMhz = N(c, 5),
                GpuLoadPct = N(c, 6),
                GpuVoltageV = N(c, 7),
                GpuBoardPowerW = N(c, 8),
                VramUsedMb = N(c, 9),
                FanRpm1 = N(c, 10),
                FanRpm2 = N(c, 11),
                FanRpm3 = N(c, 12),
                FanRpm4 = N(c, 13),
                FanPct1 = N(c, 14),
                CpuTempC = N(c, 15),
                CpuLoadPct = N(c, 16),
                CpuPowerW = N(c, 17),
                CpuClockMhz = N(c, 18)
            });
        }
        return list;
    }

    // ---------- helpers ----------
    private static StreamWriter NewWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
    }

    private static IEnumerable<string[]> ReadRows(string path)
    {
        if (!File.Exists(path)) yield break;
        bool header = true;
        foreach (var line in File.ReadLines(path))
        {
            if (header) { header = false; continue; }
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return line.Split(',');
        }
    }

    private static double D(string[] c, int i) =>
        i < c.Length && double.TryParse(c[i], NumberStyles.Float, Inv, out var v) ? v : 0;

    private static double? N(string[] c, int i) =>
        i < c.Length && double.TryParse(c[i], NumberStyles.Float, Inv, out var v) ? v : null;
}
