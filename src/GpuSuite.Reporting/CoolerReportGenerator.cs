using System.Globalization;
using System.Text;
using GpuSuite.Core.Models;

namespace GpuSuite.Reporting;

/// <summary>
/// Renders a <see cref="CoolerSweepResult"/> as a single self-contained HTML report (inline CSS + inline SVG),
/// in the spirit of TechPowerUp's "Cooler Performance" pages: a family of temperature-vs-heat-load curves (one
/// line per noise-normalized fan speed) plus single-point bars at the card's reference power (lower = better).
/// </summary>
public sealed class CoolerReportGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string fmt = "0.0") => v.ToString(fmt, Inv);
    private static string F(double? v, string fmt = "0.0", string dash = "—") => v is double d ? d.ToString(fmt, Inv) : dash;

    public void Save(string path, CoolerSweepResult r)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Generate(r), new UTF8Encoding(false));
    }

    public string Generate(CoolerSweepResult r)
    {
        bool anyMem = r.Levels.Any(l => l.Steps.Any(s => s.MemTempC is not null));

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.Append($"<title>GPU Cooler Evaluation — {SvgCharts.Esc(r.GpuName)}</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        Header(sb, r);
        Charts(sb, r, anyMem);
        RefBars(sb, r, anyMem);
        Tables(sb, r, anyMem);
        Methodology(sb, r);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private void Header(StringBuilder sb, CoolerSweepResult r)
    {
        sb.Append("<header><div class=\"wrap\">");
        sb.Append("<h1>GPU Cooler Evaluation</h1>");
        sb.Append($"<div class=\"gpu\">{SvgCharts.Esc(r.GpuName)}</div>");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<span>Vendor: {SvgCharts.Esc(r.Vendor)}</span>");
        sb.Append($"<span>Reference power: {F(r.ReferencePowerW, "0")} W</span>");
        if (r.AmbientC > 0) sb.Append($"<span>Ambient: {F(r.AmbientC)} °C</span>");
        if (r.ClockLockMhz is int mhz) sb.Append($"<span>Clock-locked: {mhz} MHz</span>");
        sb.Append($"<span>Generated {r.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm}</span>");
        sb.Append("</div>");

        bool powerLive = !r.PowerSource.Contains("synthetic", StringComparison.OrdinalIgnoreCase);
        sb.Append("<div class=\"badges\">");
        sb.Append(Badge($"Power / {r.PowerSource}", powerLive));
        sb.Append(Badge(r.FanAutoControlled ? "Fan / auto-set" : "Fan / manual", r.FanAutoControlled));
        sb.Append("</div>");
        if (!string.IsNullOrWhiteSpace(r.FanNote))
            sb.Append($"<div class=\"note\">Noise calibration: {SvgCharts.Esc(r.FanNote)}</div>");
        sb.Append("</div></header>");
    }

    private static string Badge(string label, bool good) =>
        $"<span class=\"badge {(good ? "live" : "synth")}\">{SvgCharts.Esc(label)}</span>";

    private void Charts(StringBuilder sb, CoolerSweepResult r, bool anyMem)
    {
        sb.Append("<section class=\"wrap\">");
        sb.Append("<div class=\"charts\">");

        var gpuSeries = r.Levels.Select((lvl, i) => new SvgCharts.XySeries(
            $"{F(lvl.TargetDba, "0")} dBA",
            lvl.Steps.Where(s => s.GpuTempC is not null)
                     .Select(s => (s.AchievedW ?? s.TargetW, s.GpuTempC!.Value)).ToList(),
            SvgCharts.ColorFor(i))).Where(s => s.Points.Count > 0).ToList();

        sb.Append("<figure class=\"wide\"><figcaption>GPU temperature vs heat load — one curve per noise-normalized fan speed (lower is better)</figcaption>");
        sb.Append(SvgCharts.MultiLineXY(gpuSeries, "Heat load (W)", "GPU temperature (°C)"));
        sb.Append("</figure>");

        if (anyMem)
        {
            var memSeries = r.Levels.Select((lvl, i) => new SvgCharts.XySeries(
                $"{F(lvl.TargetDba, "0")} dBA",
                lvl.Steps.Where(s => s.MemTempC is not null)
                         .Select(s => (s.AchievedW ?? s.TargetW, s.MemTempC!.Value)).ToList(),
                SvgCharts.ColorFor(i))).Where(s => s.Points.Count > 0).ToList();
            sb.Append("<figure class=\"wide\"><figcaption>Memory temperature vs heat load</figcaption>");
            sb.Append(SvgCharts.MultiLineXY(memSeries, "Heat load (W)", "Memory temperature (°C)"));
            sb.Append("</figure>");
        }

        sb.Append("</div></section>");
    }

    private void RefBars(StringBuilder sb, CoolerSweepResult r, bool anyMem)
    {
        sb.Append("<section class=\"wrap\">");
        sb.Append($"<h2>Cooler Performance @ {F(r.ReferencePowerW, "0")} W</h2>");
        sb.Append("<div class=\"charts\">");

        var gpuBars = r.Levels.Select((lvl, i) => new SvgCharts.Bar(
            $"{F(lvl.TargetDba, "0")} dBA", lvl.RefGpuTempC ?? 0, F(lvl.RefGpuTempC, "0.0"), SvgCharts.ColorFor(i))).ToList();
        sb.Append($"<figure><figcaption>GPU temperature at reference power (lower is better)</figcaption>{SvgCharts.HBars(gpuBars, unit: " °C")}</figure>");

        if (anyMem)
        {
            var memBars = r.Levels.Select((lvl, i) => new SvgCharts.Bar(
                $"{F(lvl.TargetDba, "0")} dBA", lvl.RefMemTempC ?? 0, F(lvl.RefMemTempC, "0"), SvgCharts.ColorFor(i))).ToList();
            sb.Append($"<figure><figcaption>Memory temperature at reference power</figcaption>{SvgCharts.HBars(memBars, unit: " °C")}</figure>");
        }

        sb.Append("</div></section>");
    }

    private void Tables(StringBuilder sb, CoolerSweepResult r, bool anyMem)
    {
        foreach (var lvl in r.Levels)
        {
            sb.Append("<section class=\"wrap scene\">");
            sb.Append($"<h3>{F(lvl.TargetDba, "0")} dBA <span class=\"vtag\">fan {F(lvl.FanPercent, "0")}% · {F(lvl.FanRpm, "0")} RPM</span></h3>");
            sb.Append("<table><thead><tr><th>Target W</th><th>Achieved W</th><th>GPU °C</th><th>Hotspot °C</th>");
            if (anyMem) sb.Append("<th>Mem °C</th>");
            sb.Append("<th>Clock MHz</th><th>Fan RPM</th><th>Soak s</th><th>Settled</th></tr></thead><tbody>");
            foreach (var s in lvl.Steps)
            {
                sb.Append("<tr>");
                sb.Append($"<td>{F(s.TargetW, "0")}</td><td>{F(s.AchievedW, "0")}</td>");
                sb.Append($"<td>{F(s.GpuTempC)}</td><td>{F(s.GpuHotspotC)}</td>");
                if (anyMem) sb.Append($"<td>{F(s.MemTempC, "0")}</td>");
                sb.Append($"<td>{F(s.GpuClockMhz, "0")}</td><td>{F(s.FanRpm, "0")}</td>");
                sb.Append($"<td>{F(s.SoakSeconds, "0")}</td>");
                sb.Append($"<td class=\"{(s.Settled ? "ok" : "warn")}\">{(s.Settled ? "yes" : "max-soak")}</td>");
                sb.Append("</tr>");
            }
            sb.Append("</tbody></table></section>");
        }
    }

    private void Methodology(StringBuilder sb, CoolerSweepResult r)
    {
        sb.Append("<footer class=\"wrap\"><h3>Methodology</h3><ul>");
        sb.Append("<li>The cooler is characterized <b>noise- and power-normalized</b> so cards are comparable independent of stock fan curves and heat output. Each curve holds the fan at a calibrated speed that emits a fixed noise level (dBA), and the heat load (GPU power) is swept along the X axis.</li>");
        sb.Append("<li><b>Heat load</b> is an in-suite controllable GPU compute load driven by a closed-loop controller to each target wattage, measured by " + SvgCharts.Esc(r.PowerSource) + ". Each step is soaked to <b>thermal equilibrium</b> (on-die GPU-temperature slope below threshold) before the steady temperatures are recorded; a step marked <i>max-soak</i> hit the time cap before fully settling.</li>");
        sb.Append("<li><b>Temperatures</b> (GPU on-die, hotspot, memory) and fan RPM come from LibreHardwareMonitor, averaged over the settled window.</li>");
        sb.Append($"<li>The bars summarize each fan speed at the reference power ({F(r.ReferencePowerW, "0")} W), interpolated from the swept steps — typically the card's reference TDP. Lower temperature at the same noise + power means a better cooler.</li>");
        if (r.ClockLockMhz is null)
            sb.Append("<li class=\"warn-note\">No clock-lock was applied: on consumer GPUs boost-clock hysteresis can make the held power less precise. Pair the sweep with an NVIDIA clock-lock and Powenetics on an elevated bench for the tightest heat-load control.</li>");
        sb.Append("</ul></footer>");
    }

    private const string Css = @"
:root{--bg:#0f1216;--panel:#161b22;--panel2:#1c232c;--line:#2a323d;--txt:#e6edf3;--mut:#9aa7b4;--accent:#5b8def;}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}
.wrap{max-width:1100px;margin:0 auto;padding:0 20px}
header{background:linear-gradient(180deg,#1b2330,#0f1216);border-bottom:1px solid var(--line);padding:28px 0 22px}
header h1{margin:0;font-size:20px;letter-spacing:.3px;color:var(--mut);font-weight:600}
.gpu{font-size:30px;font-weight:700;margin:4px 0 10px}
.meta{display:flex;flex-wrap:wrap;gap:16px;color:var(--mut);font-size:13px}
.badges{margin-top:14px;display:flex;flex-wrap:wrap;gap:8px}
.badge{font-size:12px;padding:4px 10px;border-radius:999px;border:1px solid var(--line)}
.badge.live{background:rgba(70,192,138,.12);color:#7be0b0;border-color:#2c5}
.badge.synth{background:rgba(224,161,58,.12);color:#e9c07a;border-color:#a83}
.note{margin-top:10px;color:var(--mut);font-size:13px}
h2{max-width:1100px;margin:30px auto 4px;padding:0 20px;font-size:20px;border-left:4px solid var(--accent)}
.charts{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin:18px 0}
figure{margin:0;background:var(--panel2);border:1px solid var(--line);border-radius:10px;padding:12px}
figure.wide{grid-column:1 / -1}
figcaption{color:var(--mut);font-size:12px;margin-bottom:8px}
.cl{fill:var(--mut);font-size:12px}.cv{fill:var(--txt);font-size:12px;font-weight:600}
.scene{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin:16px auto}
.scene h3{margin:0 0 12px;font-size:16px}
.vtag{font-size:12px;font-weight:600;color:var(--accent);background:rgba(91,141,239,.12);border:1px solid var(--accent);border-radius:999px;padding:2px 9px;margin-left:8px;vertical-align:middle}
table{width:100%;border-collapse:collapse;margin-top:6px;font-variant-numeric:tabular-nums}
th,td{padding:7px 10px;text-align:right;border-bottom:1px solid var(--line)}
thead th{font-size:12px;color:var(--mut);text-transform:uppercase;letter-spacing:.3px}
td.ok{color:#7be0b0}td.warn{color:#e9c07a}
footer{margin:30px auto 60px}footer ul{color:var(--mut);font-size:13px}.warn-note{color:#e9c07a}
@media(max-width:860px){.charts{grid-template-columns:1fr}}
";
}
