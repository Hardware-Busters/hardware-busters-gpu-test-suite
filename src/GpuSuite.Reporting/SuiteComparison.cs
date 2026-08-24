using System.Globalization;
using System.Text;
using GpuSuite.Core.Models;

namespace GpuSuite.Reporting;

/// <summary>One joined row of a two-suite comparison, keyed by (game, scene, variant, resolution).</summary>
public sealed class ComparisonRow
{
    public required string GameId { get; init; }
    public required string SceneId { get; init; }
    public required string VariantId { get; init; }
    public required string ResolutionName { get; init; }
    public SceneResolutionAggregate? Baseline { get; init; }
    public SceneResolutionAggregate? Target { get; init; }

    public bool Matched => Baseline is not null && Target is not null;

    public double? BaselineFps => Baseline?.AvgFps;
    public double? TargetFps => Target?.AvgFps;

    /// <summary>Relative avg-FPS change from baseline to target (%). Null unless both sides have a
    /// positive avg — an absent side must never read as ±100%.</summary>
    public double? DeltaPct => Baseline is { AvgFps: > 0 } b && Target is { AvgFps: > 0 } t
        ? (t.AvgFps - b.AvgFps) / b.AvgFps * 100.0
        : null;

    /// <summary>Power may be compared only when BOTH sides carry the same full provenance contract
    /// (same kind, scope, eligibility). Otherwise the watts columns are suppressed — an LHM board-power
    /// number must never be differenced against a Powenetics rail measurement as if equivalent.</summary>
    public bool PowerComparable => PowerProvenance.AreCompatible(Baseline?.PowerMeasurement, Target?.PowerMeasurement);

    public bool FrameGenInvolved =>
        Baseline?.Runs.Any(r => r.FrameGenActive == true) == true ||
        Target?.Runs.Any(r => r.FrameGenActive == true) == true;
}

/// <summary>
/// Pure builder for a two-suite (typically cross-GPU) comparison. Joins the two suites' scene/resolution/
/// variant aggregates on identity, keeps honest unmatched sides instead of dropping them, and derives the
/// delta math. No I/O, no formatting — fully unit-testable.
/// </summary>
public static class SuiteComparison
{
    public sealed record Result(
        IReadOnlyList<ComparisonRow> Rows,
        SuiteResult BaselineSuite,
        SuiteResult TargetSuite)
    {
        public IEnumerable<ComparisonRow> Matched => Rows.Where(r => r.Matched);
        public IEnumerable<ComparisonRow> OnlyInBaseline => Rows.Where(r => r.Baseline is not null && r.Target is null);
        public IEnumerable<ComparisonRow> OnlyInTarget => Rows.Where(r => r.Target is not null && r.Baseline is null);

        /// <summary>Geo-mean index ratio (target/baseline − 1, %) over MATCHED rows with positive fps on
        /// both sides — computed over identical work, unlike differencing each suite's own overall index
        /// (whose cell sets may differ).</summary>
        public double? MatchedIndexDeltaPct
        {
            get
            {
                double logSum = 0; int n = 0;
                foreach (var row in Matched)
                {
                    var d = row.DeltaPct;
                    if (d is null || row.BaselineFps is not { } bf || bf <= 0) continue;
                    logSum += Math.Log((row.TargetFps ?? 0) / bf);
                    n++;
                }
                return n > 0 ? (Math.Exp(logSum / n) - 1.0) * 100.0 : null;
            }
        }

        public int ImprovedCount => Matched.Count(r => (r.DeltaPct ?? 0) > 1.0);
        public int RegressedCount => Matched.Count(r => (r.DeltaPct ?? 0) < -1.0);
    }

    public static Result Build(SuiteResult baselineSuite, SuiteResult targetSuite)
    {
        static string Key(SceneResolutionAggregate a) =>
            $"{a.GameId}|{a.SceneId}|{a.VariantId}|{a.ResolutionName}".ToLowerInvariant();

        var baseline = baselineSuite.Aggregates.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
        var target = targetSuite.Aggregates.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);

        var keys = new List<string>();
        foreach (var k in baselineSuite.Aggregates.Select(Key)) if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);
        foreach (var k in targetSuite.Aggregates.Select(Key)) if (!keys.Contains(k, StringComparer.OrdinalIgnoreCase)) keys.Add(k);

        var rows = new List<ComparisonRow>(keys.Count);
        foreach (var k in keys)
        {
            baseline.TryGetValue(k, out var b);
            target.TryGetValue(k, out var t);
            var any = b ?? t!;
            rows.Add(new ComparisonRow
            {
                GameId = any.GameId,
                SceneId = any.SceneId,
                VariantId = any.VariantId,
                ResolutionName = any.ResolutionName,
                Baseline = b,
                Target = t
            });
        }
        return new Result(rows, baselineSuite, targetSuite);
    }
}

/// <summary>
/// Self-contained HTML report for a two-suite comparison (inline CSS/SVG, no external assets — same house
/// style as the main report). Honest by construction: every row shows each side's power PROVENANCE label,
/// watt deltas are suppressed across incompatible provenance, frame-gen-inflated rows are flagged, and
/// aggregates missing on one side are listed as "not measured" rather than silently dropped.
/// </summary>
public sealed class SuiteComparisonReportGenerator
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string fmt = "0.0") => v.ToString(fmt, Inv);
    private static string F(double? v, string fmt = "0.0", string dash = "—") => v is double d ? d.ToString(fmt, Inv) : dash;

    public void Save(string path, SuiteComparison.Result result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Generate(result), new UTF8Encoding(false));
    }

    public string Generate(SuiteComparison.Result cmp)
    {
        var sb = new StringBuilder();
        var b = cmp.BaselineSuite;
        var t = cmp.TargetSuite;
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.Append($"<title>Suite comparison — {SvgCharts.Esc(b.GpuName)} vs {SvgCharts.Esc(t.GpuName)}</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        sb.Append("<header><div class=\"wrap\">");
        sb.Append("<h1>GPU Test Suite — Comparison</h1>");
        sb.Append($"<div class=\"gpu\">{SvgCharts.Esc(b.GpuName)} <span class=\"vs\">vs</span> {SvgCharts.Esc(t.GpuName)}</div>");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<span>Baseline: generated {b.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · suite v{SvgCharts.Esc(b.System.SuiteVersion)}</span>");
        sb.Append($"<span>Target: generated {t.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · suite v{SvgCharts.Esc(t.System.SuiteVersion)}</span>");
        sb.Append("</div>");
        sb.Append($"<p class=\"note\">Only aggregates for the SAME (game, scene, model, resolution) are compared. Positive % means the target card was faster. Watt deltas appear only where both sides share the same power provenance.</p>");
        sb.Append("</div></header>");

        Overview(sb, cmp);
        DeltaTable(sb, cmp);
        Unmatched(sb, "Only in the baseline (not measured on the target)", cmp.OnlyInBaseline, cmp);
        Unmatched(sb, "Only in the target (not in the baseline)", cmp.OnlyInTarget, cmp);
        RosterDelta(sb, cmp);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Overview(StringBuilder sb, SuiteComparison.Result cmp)
    {
        sb.Append("<section class=\"wrap cards\">");
        sb.Append(Card("Matched cells", cmp.Matched.Count().ToString(),
            $"{cmp.ImprovedCount} faster · {cmp.RegressedCount} slower · {cmp.Matched.Count() - cmp.ImprovedCount - cmp.RegressedCount} within ±1%"));
        var idx = cmp.MatchedIndexDeltaPct;
        sb.Append(Card("Matched geo-mean Δ", idx is null ? "—" : $"{(idx >= 0 ? "+" : "")}{F(idx.Value)}%",
            "target vs baseline over identical cells"));
        sb.Append(Card("Baseline index", F(cmp.BaselineSuite.OverallPerformanceIndex), "geo-mean avg FPS"));
        sb.Append(Card("Target index", F(cmp.TargetSuite.OverallPerformanceIndex),
            $"whole-suite geo-mean ({cmp.OnlyInBaseline.Count()}+{cmp.OnlyInTarget.Count()} unmatched cells excluded above)"));
        sb.Append("</section>");
    }

    private static string Card(string title, string value, string sub) =>
        $"<div class=\"card\"><div class=\"ct\">{SvgCharts.Esc(title)}</div><div class=\"cval\">{SvgCharts.Esc(value)}</div><div class=\"csub\">{SvgCharts.Esc(sub)}</div></div>";

    private static void DeltaTable(StringBuilder sb, SuiteComparison.Result cmp)
    {
        sb.Append("<section class=\"wrap\"><h2>Per-cell deltas</h2>");
        sb.Append("<table><thead><tr><th>Game</th><th>Scene</th><th>Model</th><th>Res</th>" +
                  "<th>Baseline FPS</th><th>Target FPS</th><th>Δ FPS</th><th>Δ %</th>" +
                  "<th>Baseline W</th><th>Target W</th><th>Δ W</th><th>FPS/W Δ%</th></tr></thead><tbody>");
        foreach (var r in cmp.Matched)
        {
            string fgFlag = r.FrameGenInvolved ? " <sup class=\"fg\" title=\"Frame generation active on at least one side: presented-fps numbers count generated presents.\">FG⚠</sup>" : "";
            double? dFps = r.Baseline is { } bb && r.Target is { } tt ? tt.AvgFps - bb.AvgFps : null;
            double? dPct = r.DeltaPct;
            string pctCls = dPct is null ? "" : dPct > 1 ? "ok" : dPct < -1 ? "bad" : "";
            string pctCell = dPct is null
                ? "—"
                : $"<span class=\"{pctCls}\">{(dPct >= 0 ? "+" : "")}{F(dPct)}%</span>";

            // Watts only when the provenance contracts match; otherwise dashes + the two labels below.
            bool pw = r.PowerComparable;
            double? bw = r.Baseline?.AvgGpuPowerW, tw = r.Target?.AvgGpuPowerW;
            double? dW = pw && bw is { } b2 && tw is { } t2 ? t2 - b2 : null;

            // FPS/W delta only when both sides are DIRECT eligible (the only publishable efficiency kind).
            bool effEligible = PowerProvenance.IsDirectEfficiencyEligible(r.Baseline?.PowerMeasurement)
                            && PowerProvenance.IsDirectEfficiencyEligible(r.Target?.PowerMeasurement);
            double? bE = effEligible && bw is { } bwv && bwv > 0 ? r.Baseline!.AvgFps / bwv : null;
            double? tE = effEligible && tw is { } twv && twv > 0 ? r.Target!.AvgFps / twv : null;
            double? dEff = bE is { } bEv && tE is { } tEv && bEv > 0 ? (tEv - bEv) / bEv * 100.0 : null;

            sb.Append("<tr>");
            sb.Append($"<td class=\"ml\">{SvgCharts.Esc(GameLabel(r.GameId, cmp))}</td>");
            sb.Append($"<td>{SvgCharts.Esc(r.SceneId)}{fgFlag}</td>");
            sb.Append($"<td>{SvgCharts.Esc(string.IsNullOrWhiteSpace(r.VariantId) || r.VariantId == "default" ? "default" : r.VariantId)}</td>");
            sb.Append($"<td>{SvgCharts.Esc(r.ResolutionName)}</td>");
            sb.Append($"<td>{F(r.BaselineFps)}</td><td>{F(r.TargetFps)}</td>");
            sb.Append($"<td>{(dFps is null ? "—" : (dFps >= 0 ? "+" : "") + F(dFps))}</td>");
            sb.Append($"<td>{pctCell}</td>");
            sb.Append($"<td>{(pw ? F(bw, "0") : "—")}<br/><small>{SvgCharts.Esc(PowerProvenance.Label(r.Baseline?.PowerMeasurement))}</small></td>");
            sb.Append($"<td>{(pw ? F(tw, "0") : "—")}<br/><small>{SvgCharts.Esc(PowerProvenance.Label(r.Target?.PowerMeasurement))}</small></td>");
            string wCls = !pw || dW is null ? "" : dW >= 0 ? "warn-c" : "ok";
            sb.Append($"<td class=\"{wCls}\">{(dW is null ? (pw ? "" : "n/a") : (dW >= 0 ? "+" : "") + F(dW, "0"))}</td>");
            sb.Append($"<td>{(effEligible && dEff is not null ? (dEff >= 0 ? "+" : "") + F(dEff) + "%" : "—")}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></section>");
    }

    private static void Unmatched(StringBuilder sb, string title, IEnumerable<ComparisonRow> rows, SuiteComparison.Result cmp)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        sb.Append($"<section class=\"wrap\"><h2>{SvgCharts.Esc(title)}</h2><table><thead><tr><th>Game</th><th>Scene</th><th>Model</th><th>Res</th><th>Avg FPS</th><th>Power provenance</th></tr></thead><tbody>");
        foreach (var r in list)
        {
            var agg = r.Baseline ?? r.Target!;
            sb.Append("<tr>");
            sb.Append($"<td class=\"ml\">{SvgCharts.Esc(GameLabel(r.GameId, cmp))}</td>");
            sb.Append($"<td>{SvgCharts.Esc(r.SceneId)}</td>");
            sb.Append($"<td>{SvgCharts.Esc(string.IsNullOrWhiteSpace(r.VariantId) || r.VariantId == "default" ? "default" : r.VariantId)}</td>");
            sb.Append($"<td>{SvgCharts.Esc(r.ResolutionName)}</td>");
            sb.Append($"<td>{F(agg.AvgFps)} ({agg.ValidRuns}/{agg.TotalRuns} valid)</td>");
            sb.Append($"<td>{SvgCharts.Esc(PowerProvenance.Label(agg.PowerMeasurement))}</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></section>");
    }

    private static void RosterDelta(StringBuilder sb, SuiteComparison.Result cmp)
    {
        if ((cmp.BaselineSuite.GameOutcomes.Count == 0) && (cmp.TargetSuite.GameOutcomes.Count == 0)) return;
        var targetById = cmp.TargetSuite.GameOutcomes.ToDictionary(o => o.GameId, StringComparer.OrdinalIgnoreCase);
        sb.Append("<section class=\"wrap\"><h2>Roster outcomes</h2><table><thead><tr><th>Game</th><th>Baseline</th><th>Target</th></tr></thead><tbody>");
        foreach (var o in cmp.BaselineSuite.GameOutcomes)
        {
            targetById.TryGetValue(o.GameId, out var t2);
            sb.Append($"<tr><td class=\"ml\">{SvgCharts.Esc(string.IsNullOrEmpty(o.Name) ? o.GameId : o.Name)}</td>");
            sb.Append($"<td class=\"{Cls(o.Status)}\">{o.Status}{Detail(o)}</td>");
            sb.Append($"<td class=\"{(t2 is null ? "" : Cls(t2.Status))}\">{(t2?.Status.ToString() ?? "—")}{(t2 is null ? "" : Detail(t2))}</td></tr>");
        }
        foreach (var o in cmp.TargetSuite.GameOutcomes.Where(o => !cmp.BaselineSuite.GameOutcomes.Any(b => b.GameId.Equals(o.GameId, StringComparison.OrdinalIgnoreCase))))
            sb.Append($"<tr><td class=\"ml\">{SvgCharts.Esc(string.IsNullOrEmpty(o.Name) ? o.GameId : o.Name)}</td><td>—</td><td class=\"{Cls(o.Status)}\">{o.Status}{Detail(o)}</td></tr>");
        sb.Append("</tbody></table></section>");
    }

    private static string Detail(GameOutcome o)
        => o.Status == GameStatus.Passed ? "" : $" — {SvgCharts.Esc(o.FailureClass.ToString())}";

    private static string Cls(GameStatus s) => s switch
    {
        GameStatus.Passed => "ok",
        GameStatus.Failed => "bad",
        _ => "warn-c"
    };

    private static string GameLabel(string gameId, SuiteComparison.Result cmp)
    {
        var names = cmp.BaselineSuite.GameOutcomes.FirstOrDefault(o => o.GameId.Equals(gameId, StringComparison.OrdinalIgnoreCase))?.Name;
        return string.IsNullOrWhiteSpace(names) ? gameId : names!;
    }

    private const string Css = """
:root{--bg:#0f1216;--panel:#161b22;--line:#2a323d;--txt:#e6edf3;--mut:#9aa7b4;--accent:#5b8def}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--txt);font:14px/1.5 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}
.wrap{max-width:1240px;margin:0 auto;padding:0 20px}
header{background:linear-gradient(180deg,#1b2330,#0f1216);border-bottom:1px solid var(--line);padding:28px 0 22px}
header h1{margin:0;font-size:20px;color:var(--mut);font-weight:600}
.gpu{font-size:26px;font-weight:700;margin:4px 0 8px}.vs{color:var(--accent);font-size:16px}
.meta{display:flex;flex-wrap:wrap;gap:16px;color:var(--mut);font-size:13px}
.note{color:#9aa7b4;font-size:12.5px;margin:10px 0 0}
.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:22px auto}
.card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:16px}
.ct{color:var(--mut);font-size:12px;text-transform:uppercase;letter-spacing:.4px}
.cval{font-size:24px;font-weight:700;margin:6px 0 2px}.csub{color:var(--mut);font-size:12px}
h2{max-width:1240px;margin:30px auto 6px;padding:0 20px;font-size:18px;border-left:4px solid var(--accent)}
table{width:calc(100% - 40px);margin:6px auto 18px;border-collapse:collapse;font-variant-numeric:tabular-nums}
th,td{padding:7px 10px;text-align:right;border-bottom:1px solid var(--line)}
th:first-child,td.ml{text-align:left;color:var(--mut)}
thead th{font-size:12px;color:var(--mut);text-transform:uppercase}
td.ok{color:#7be0b0}td.bad{color:#e88}td.warn-c{color:#e9c07a}
span.ok{color:#7be0b0;font-weight:600}span.bad{color:#e88;font-weight:600}
small{color:var(--mut);font-size:10.5px}
sup.fg{color:#e8a03c;font-weight:700;cursor:help}
""";
}
