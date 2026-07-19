using System.Globalization;
using System.Text;

namespace GpuSuite.Reporting;

/// <summary>Minimal, dependency-free inline-SVG chart helpers for the HTML report.</summary>
public static class SvgCharts
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double v) => v.ToString("0.###", Inv);

    public record Bar(string Label, double Value, string ValueText, string Color);

    /// <summary>Horizontal bar chart scaled to the max value. Good for FPS / power by resolution.</summary>
    public static string HBars(IReadOnlyList<Bar> bars, int width = 560, string unit = "")
    {
        if (bars.Count == 0) return "<svg/>";
        double max = bars.Max(b => b.Value);
        if (max <= 0) max = 1;
        int rowH = 30, gap = 10, left = 78, right = 70, top = 8;
        int chartW = width - left - right;
        int height = top * 2 + bars.Count * rowH + (bars.Count - 1) * gap;

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" preserveAspectRatio=\"xMinYMin meet\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\">");
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            int y = top + i * (rowH + gap);
            double w = chartW * (b.Value / max);
            sb.Append($"<text x=\"{left - 8}\" y=\"{y + rowH * 0.68}\" text-anchor=\"end\" class=\"cl\">{Esc(b.Label)}</text>");
            sb.Append($"<rect x=\"{left}\" y=\"{y}\" width=\"{N(w)}\" height=\"{rowH}\" rx=\"4\" fill=\"{b.Color}\"/>");
            sb.Append($"<text x=\"{left + w + 6}\" y=\"{y + rowH * 0.68}\" class=\"cv\">{Esc(b.ValueText)}{Esc(unit)}</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>A small sparkline-style line chart (e.g. a frame-time trace).</summary>
    public static string Line(IReadOnlyList<double> values, int width = 560, int height = 120, string color = "#5b8def", string fill = "rgba(91,141,239,0.15)")
    {
        if (values.Count < 2) return "<svg/>";
        double min = values.Min(), max = values.Max();
        if (max - min < 1e-9) max = min + 1;
        int pad = 6;
        double xs = (double)(width - 2 * pad) / (values.Count - 1);
        double Y(double v) => pad + (height - 2 * pad) * (1 - (v - min) / (max - min));

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" preserveAspectRatio=\"none\" xmlns=\"http://www.w3.org/2000/svg\">");
        var path = new StringBuilder();
        var area = new StringBuilder($"M {pad},{N(height - pad)} ");
        for (int i = 0; i < values.Count; i++)
        {
            double x = pad + i * xs, y = Y(values[i]);
            path.Append(i == 0 ? $"M {N(x)},{N(y)} " : $"L {N(x)},{N(y)} ");
            area.Append($"L {N(x)},{N(y)} ");
        }
        area.Append($"L {N(width - pad)},{N(height - pad)} Z");
        sb.Append($"<path d=\"{area}\" fill=\"{fill}\" stroke=\"none\"/>");
        sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.6\"/>");
        sb.Append("</svg>");
        return sb.ToString();
    }

    public record XySeries(string Label, IReadOnlyList<(double X, double Y)> Points, string Color);

    /// <summary>
    /// Multi-series XY line chart with axes, gridlines, tick labels, point markers and a legend — for the
    /// cooler eval's "temperature vs heat load" family of curves (one line per noise-normalized fan speed).
    /// Self-contained: all colors are inlined so it renders without external CSS.
    /// </summary>
    public static string MultiLineXY(IReadOnlyList<XySeries> series, string xLabel, string yLabel,
        int width = 640, int height = 380, double? yFloor = null)
    {
        var pts = series.SelectMany(s => s.Points).ToList();
        if (pts.Count < 2) return "<svg/>";

        double xMinD = pts.Min(p => p.X), xMaxD = pts.Max(p => p.X);
        double yMinD = pts.Min(p => p.Y), yMaxD = pts.Max(p => p.Y);
        if (yFloor is double yf) yMinD = Math.Min(yMinD, yf);
        var (xlo, xhi, xstep) = NiceAxis(xMinD, xMaxD);
        var (ylo, yhi, ystep) = NiceAxis(yMinD, yMaxD);

        int left = 56, right = 18, top = 16, bottom = 64;
        double px0 = left, px1 = width - right, py0 = top, py1 = height - bottom;
        double X(double v) => px0 + (px1 - px0) * (v - xlo) / (xhi - xlo);
        double Y(double v) => py1 - (py1 - py0) * (v - ylo) / (yhi - ylo);

        var sb = new StringBuilder();
        sb.Append($"<svg viewBox=\"0 0 {width} {height}\" width=\"100%\" preserveAspectRatio=\"xMinYMin meet\" xmlns=\"http://www.w3.org/2000/svg\" role=\"img\">");

        // gridlines + tick labels
        for (double v = ylo; v <= yhi + 1e-6; v += ystep)
        {
            double y = Y(v);
            sb.Append($"<line x1=\"{N(px0)}\" y1=\"{N(y)}\" x2=\"{N(px1)}\" y2=\"{N(y)}\" stroke=\"#2a323d\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{N(px0 - 8)}\" y=\"{N(y + 4)}\" text-anchor=\"end\" fill=\"#9aa7b4\" font-size=\"11\">{N(v)}</text>");
        }
        for (double v = xlo; v <= xhi + 1e-6; v += xstep)
        {
            double x = X(v);
            sb.Append($"<line x1=\"{N(x)}\" y1=\"{N(py0)}\" x2=\"{N(x)}\" y2=\"{N(py1)}\" stroke=\"#222a33\" stroke-width=\"1\"/>");
            sb.Append($"<text x=\"{N(x)}\" y=\"{N(py1 + 16)}\" text-anchor=\"middle\" fill=\"#9aa7b4\" font-size=\"11\">{N(v)}</text>");
        }
        // axis frame + titles
        sb.Append($"<line x1=\"{N(px0)}\" y1=\"{N(py1)}\" x2=\"{N(px1)}\" y2=\"{N(py1)}\" stroke=\"#3a434f\" stroke-width=\"1.5\"/>");
        sb.Append($"<line x1=\"{N(px0)}\" y1=\"{N(py0)}\" x2=\"{N(px0)}\" y2=\"{N(py1)}\" stroke=\"#3a434f\" stroke-width=\"1.5\"/>");
        sb.Append($"<text x=\"{N((px0 + px1) / 2)}\" y=\"{N(py1 + 34)}\" text-anchor=\"middle\" fill=\"#9aa7b4\" font-size=\"12\">{Esc(xLabel)}</text>");
        sb.Append($"<text x=\"16\" y=\"{N((py0 + py1) / 2)}\" text-anchor=\"middle\" fill=\"#9aa7b4\" font-size=\"12\" transform=\"rotate(-90 16 {N((py0 + py1) / 2)})\">{Esc(yLabel)}</text>");

        // series
        foreach (var s in series)
        {
            var ordered = s.Points.OrderBy(p => p.X).ToList();
            if (ordered.Count == 0) continue;
            var path = new StringBuilder();
            for (int i = 0; i < ordered.Count; i++)
                path.Append(i == 0 ? $"M {N(X(ordered[i].X))},{N(Y(ordered[i].Y))} " : $"L {N(X(ordered[i].X))},{N(Y(ordered[i].Y))} ");
            sb.Append($"<path d=\"{path}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"2\"/>");
            foreach (var p in ordered)
                sb.Append($"<circle cx=\"{N(X(p.X))}\" cy=\"{N(Y(p.Y))}\" r=\"3\" fill=\"{s.Color}\"/>");
        }

        // legend (row under the x label)
        double lx = px0, ly = height - 14;
        foreach (var s in series)
        {
            sb.Append($"<rect x=\"{N(lx)}\" y=\"{N(ly - 9)}\" width=\"12\" height=\"12\" rx=\"2\" fill=\"{s.Color}\"/>");
            sb.Append($"<text x=\"{N(lx + 17)}\" y=\"{N(ly + 1)}\" fill=\"#e6edf3\" font-size=\"12\">{Esc(s.Label)}</text>");
            lx += 19 + s.Label.Length * 7.2 + 16;
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>"Nice" axis bounds + tick step covering [dataMin,dataMax] with ~5 ticks.</summary>
    private static (double lo, double hi, double step) NiceAxis(double dataMin, double dataMax, int maxTicks = 6)
    {
        if (dataMax - dataMin < 1e-9) dataMax = dataMin + 1;
        double range = NiceNum(dataMax - dataMin, false);
        double step = NiceNum(range / Math.Max(1, maxTicks - 1), true);
        double lo = Math.Floor(dataMin / step) * step;
        double hi = Math.Ceiling(dataMax / step) * step;
        return (lo, hi, step);
    }

    private static double NiceNum(double range, bool round)
    {
        double exp = Math.Floor(Math.Log10(range <= 0 ? 1 : range));
        double f = range / Math.Pow(10, exp);
        double nf = round
            ? (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10)
            : (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10);
        return nf * Math.Pow(10, exp);
    }

    public static string Esc(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // A small categorical palette used across charts.
    public static readonly string[] Palette = { "#5b8def", "#46c08a", "#e0a13a", "#d36b6b", "#9a6bd3", "#3aa0c0" };
    public static string ColorFor(int i) => Palette[i % Palette.Length];
}
