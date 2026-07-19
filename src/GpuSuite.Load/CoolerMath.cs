namespace GpuSuite.Load;

/// <summary>
/// Pure numeric helpers behind the cooler sweep — the power axis, the settle-test slope, and the
/// reference-power interpolation. Extracted from <see cref="CoolerTestRunner"/> so they can be unit-tested
/// offline (see the CLI's <c>selftest-cooler</c>) without any GPU or hardware.
/// </summary>
public static class CoolerMath
{
    /// <summary>Inclusive power axis from <paramref name="from"/> to <paramref name="to"/> by |step|
    /// (ascending or descending); a non-positive step falls back to 25 W.</summary>
    public static List<double> PowerAxis(double from, double to, double step)
    {
        var list = new List<double>();
        double s = Math.Abs(step) < 1 ? 25 : Math.Abs(step);
        if (to >= from)
            for (double w = from; w <= to + 1e-6; w += s) list.Add(w);
        else
            for (double w = from; w >= to - 1e-6; w -= s) list.Add(w);

        // A non-divisible step must still test the requested endpoint.  For example, 80→250 W in
        // 25 W increments formerly stopped at 230 W and silently omitted the requested 250 W point.
        if (list.Count == 0 || Math.Abs(list[^1] - to) > 1e-6)
            list.Add(to);
        return list;
    }

    /// <summary>Least-squares slope of y vs t in units per MINUTE (null if fewer than 3 points or t is flat).</summary>
    public static double? SlopePerMinute(IReadOnlyList<(double T, double Y)> pts)
    {
        int n = pts.Count;
        if (n < 3) return null;
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (t, y) in pts) { sx += t; sy += y; sxx += t * t; sxy += t * y; }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return null;
        double slopePerSec = (n * sxy - sx * sy) / denom;
        return slopePerSec * 60.0;
    }

    /// <summary>Linear interpolation of y at <paramref name="x"/> over (x,y) points (unsorted ok); clamps to
    /// the end values outside the data range. Null if there are no points.</summary>
    public static double? InterpolateAt(IReadOnlyList<(double X, double Y)> points, double x)
    {
        if (points.Count == 0) return null;
        var pts = points.OrderBy(p => p.X).ToList();
        if (x <= pts[0].X) return pts[0].Y;
        if (x >= pts[^1].X) return pts[^1].Y;
        for (int i = 1; i < pts.Count; i++)
            if (x <= pts[i].X)
            {
                var (x0, y0) = pts[i - 1];
                var (x1, y1) = pts[i];
                double t = (x - x0) / (x1 - x0);
                return y0 + t * (y1 - y0);
            }
        return pts[^1].Y;
    }
}
