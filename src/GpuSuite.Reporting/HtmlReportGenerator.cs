using System.Globalization;
using System.Text;
using GpuSuite.Core.Models;

namespace GpuSuite.Reporting;

/// <summary>
/// (10) Report Generator. Produces a single self-contained HTML file (inline CSS + inline
/// SVG charts, no external assets) in the spirit of a TechPowerUp GPU review: per-game and
/// per-resolution tables and charts for FPS + lows, frame-time percentiles, real GPU power
/// (avg/peak/energy-per-frame/perf-per-watt), temperatures/clocks/fans, run-to-run variance,
/// valid/invalid status, and an overall performance index.
/// </summary>
public sealed class HtmlReportGenerator
{
    public IReadOnlyDictionary<string, string>? GameNames { get; init; }
    public IReadOnlyDictionary<string, string>? SceneNames { get; init; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string F(double v, string fmt = "0.0") => v.ToString(fmt, Inv);
    private static string F(double? v, string fmt = "0.0", string dash = "—") => v is double d ? d.ToString(fmt, Inv) : dash;

    public void Save(string path, SuiteResult suite)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Generate(suite), new UTF8Encoding(false));
    }

    public string Generate(SuiteResult suite)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"/>");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
        sb.Append($"<title>GPU Test Suite — {SvgCharts.Esc(suite.GpuName)}</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");

        Header(sb, suite);
        Overview(sb, suite);
        RosterSummary(sb, suite);

        // group aggregates by game, then scene, then graphics variant ("extra model"), preserving
        // first-seen order. Each variant becomes its own table so resolution columns stay unique.
        foreach (var gameGroup in GroupOrdered(suite.Aggregates, a => a.GameId))
        {
            sb.Append($"<h2 class=\"game\">{SvgCharts.Esc(GameName(gameGroup.Key))}</h2>");
            VariantComparison(sb, gameGroup.Value);
            foreach (var sceneGroup in GroupOrdered(gameGroup.Value, a => a.SceneId))
                foreach (var variantGroup in GroupOrdered(sceneGroup.Value, a => a.VariantId))
                    SceneSection(sb, gameGroup.Key, sceneGroup.Key, variantGroup.Value);
        }

        Methodology(sb, suite);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private void Header(StringBuilder sb, SuiteResult s)
    {
        sb.Append("<header><div class=\"wrap\">");
        sb.Append($"<h1>GPU Test Suite Report</h1>");
        sb.Append($"<div class=\"gpu\">{SvgCharts.Esc(s.GpuName)}</div>");
        sb.Append("<div class=\"meta\">");
        sb.Append($"<span>CPU: {SvgCharts.Esc(s.System.CpuName)}</span>");
        sb.Append($"<span>OS: {SvgCharts.Esc(s.System.OsVersion ?? "—")}</span>");
        sb.Append($"<span>Suite v{SvgCharts.Esc(s.System.SuiteVersion)}</span>");
        if (!string.IsNullOrWhiteSpace(s.VisionCompute))
            sb.Append($"<span>Vision: {SvgCharts.Esc(s.VisionCompute)}</span>");
        sb.Append($"<span>Generated {s.GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm}</span>");
        sb.Append("</div>");

        // data-source badges (Live vs Synthetic) derived from the runs
        var (fLive, pLive, tLive) = SourceModes(s);
        var (frameProviders, powerProviders, telemetryProviders) = ProviderNames(s);
        sb.Append("<div class=\"badges\">");
        sb.Append(Badge("Frames / " + frameProviders, fLive));
        sb.Append(Badge("Power / " + powerProviders + " / " + PowerLabels(s), pLive));
        sb.Append(Badge("Telemetry / " + telemetryProviders, tLive));
        // A configured-but-silent Powenetics PMD is the recurring bench failure (wedged MCU). Surface it
        // in the header so a fallback-power report can never be skimmed as a verified PMD measurement.
        if (!string.IsNullOrWhiteSpace(s.System.PoweneticsNote))
            sb.Append($"<span class=\"badge warn\" title=\"{SvgCharts.Esc(s.System.PoweneticsNote)}\">PMD NOT STREAMING — power fell back</span>");
        sb.Append("</div></div></header>");
    }

    private static string Badge(string label, bool live) =>
        $"<span class=\"badge {(live ? "live" : "synth")}\">{SvgCharts.Esc(label)}: {(live ? "LIVE" : "SYNTHETIC")}</span>";

    private void Overview(StringBuilder sb, SuiteResult s)
    {
        sb.Append("<section class=\"wrap cards\">");
        sb.Append(Card("Overall performance index", F(s.OverallPerformanceIndex, "0.0"), "geo-mean avg FPS"));
        sb.Append(Card("Overall perf / watt", F(HasEligibleEfficiency(s) ? s.OverallPerfPerWatt : null, "0.000"), "geo-mean FPS per W"));
        sb.Append(Card("Scenes measured", s.Aggregates.Count.ToString(), $"{s.Aggregates.Count(a => a.ValidRuns > 0)} with valid runs"));
        int totalRuns = s.Aggregates.Sum(a => a.TotalRuns), validRuns = s.Aggregates.Sum(a => a.ValidRuns);
        sb.Append(Card("Runs", $"{validRuns}/{totalRuns}", "valid / total"));
        sb.Append("</section>");
    }

    private static string Card(string title, string value, string sub) =>
        $"<div class=\"card\"><div class=\"ct\">{SvgCharts.Esc(title)}</div><div class=\"cval\">{SvgCharts.Esc(value)}</div><div class=\"csub\">{SvgCharts.Esc(sub)}</div></div>";

    /// <summary>
    /// The per-game roster outcome table (passed / failed / skipped + failure class + reason) — so a report
    /// from an unattended run shows, up front, exactly what survived and why each failed game failed. Only
    /// rendered when the orchestrator recorded outcomes.
    /// </summary>
    private void RosterSummary(StringBuilder sb, SuiteResult s)
    {
        if (s.GameOutcomes is null || s.GameOutcomes.Count == 0) return;
        int passed = s.GameOutcomes.Count(o => o.Status == GameStatus.Passed);
        int failed = s.GameOutcomes.Count(o => o.Status == GameStatus.Failed);
        int skipped = s.GameOutcomes.Count(o => o.Status == GameStatus.Skipped);

        sb.Append("<section class=\"wrap\">");
        sb.Append("<h2 class=\"game\">Roster summary");
        if (s.Unattended) sb.Append(" <span class=\"badge live\">UNATTENDED</span>");
        sb.Append("</h2>");
        sb.Append($"<p style=\"margin:.2rem 0 1rem;color:#9aa7b4\">Full roster completed: <strong>{(s.RosterCompleted ? "YES" : "NO (aborted early)")}</strong> — {passed} passed, {failed} failed, {skipped} skipped. No numbers were faked; failed games carry a recorded reason.</p>");
        bool anyAdvisory = s.GameOutcomes.Any(o => !string.IsNullOrWhiteSpace(o.AdvisoryCause));
        sb.Append("<table class=\"data\"><thead><tr><th>Game</th><th>Status</th><th>Failure class</th><th>Reason</th><th>Valid / total</th>");
        if (anyAdvisory) sb.Append("<th>Custom AI (advisory)</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var o in s.GameOutcomes)
        {
            string color = o.Status switch { GameStatus.Passed => "#54d18c", GameStatus.Failed => "#e06666", _ => "#d6b656" };
            sb.Append("<tr>");
            sb.Append($"<td>{SvgCharts.Esc(string.IsNullOrEmpty(o.Name) ? o.GameId : o.Name)}</td>");
            sb.Append($"<td style=\"color:{color};font-weight:600\">{o.Status}</td>");
            sb.Append($"<td>{SvgCharts.Esc(o.Status == GameStatus.Passed ? "—" : ClassLabel(o.FailureClass))}</td>");
            sb.Append($"<td>{SvgCharts.Esc(o.Reason)}</td>");
            sb.Append($"<td>{o.ValidRuns}/{o.TotalRuns}</td>");
            if (anyAdvisory)
                sb.Append($"<td style=\"color:#9aa7b4\">{(string.IsNullOrWhiteSpace(o.AdvisoryCause) ? "—" : SvgCharts.Esc(o.AdvisoryCause) + (string.IsNullOrWhiteSpace(o.AdvisoryConfidence) ? "" : $" <em>[{SvgCharts.Esc(o.AdvisoryConfidence!)}]</em>"))}</td>");
            sb.Append("</tr>");
        }
        if (anyAdvisory)
            sb.Append("<p style=\"margin:.4rem 0;color:#7f8c99;font-size:.85em\">Custom AI (advisory): an AI hypothesis for human review — it does not change the deterministic failure class/reason.</p>");
        sb.Append("</tbody></table></section>");
    }

    private static string ClassLabel(FailureClass c) => c switch
    {
        FailureClass.None => "—",
        FailureClass.LaunchFailure => "launch failure",
        FailureClass.Crash => "crash",
        FailureClass.FrozenCapture => "frozen capture",
        FailureClass.NoFrames => "no frames",
        FailureClass.InvalidFps => "invalid FPS",
        FailureClass.MenuNavFailure => "menu navigation failure",
        FailureClass.GameplayGateFailure => "gameplay gate failure",
        FailureClass.ShaderHitch => "shader/hitch instability",
        FailureClass.LauncherNotReady => "launcher not ready",
        FailureClass.HardwarePrecheck => "hardware validation failed",
        FailureClass.RuntimeHealth => "runtime health failure",
        _ => "other"
    };

    /// <summary>
    /// Side-by-side settings-set (variant) comparison — rendered only when a game ran 2+ variants in
    /// the same scene, which is the whole point of a Run Plan A/B. One table per scene × resolution:
    /// rows = variants, with Δfps vs the baseline (the 'as-set' variant when present, else the first),
    /// power and efficiency. Rows whose runs had frame generation active are flagged: a presented-fps
    /// number must never be read as (or compared against) a rendered-fps number.
    /// </summary>
    private void VariantComparison(StringBuilder sb, IReadOnlyList<SceneResolutionAggregate> gameAggs)
    {
        foreach (var sceneGroup in GroupOrdered(gameAggs, a => a.SceneId))
        {
            if (sceneGroup.Value.Select(a => a.VariantId).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
            foreach (var resGroup in GroupOrdered(sceneGroup.Value, a => a.ResolutionName))
            {
                var rows = resGroup.Value.Where(a => a.ValidRuns > 0).ToList();
                if (rows.Count < 2) continue;
                var baseline = rows.FirstOrDefault(r => string.Equals(r.VariantId, "as-set", StringComparison.OrdinalIgnoreCase)) ?? rows[0];
                bool mixedFg = rows.Select(r => r.Runs.Any(x => x.FrameGenActive == true)).Distinct().Count() > 1;

                sb.Append("<section class=\"wrap scene\">");
                sb.Append($"<h3>Settings-set comparison — {SvgCharts.Esc(SceneName(sceneGroup.Key))} @ {SvgCharts.Esc(resGroup.Key)}</h3>");
                if (mixedFg)
                    sb.Append("<p class=\"fingerprint\"><strong class=\"fg-on\">⚠ Mixed frame-gen states below — an FG-on fps counts generated presents and is NOT comparable to a rendered-fps row.</strong></p>");

                var bars = rows.Select((a, i) => new SvgCharts.Bar(
                    VariantLabel(a), a.AvgFps, $"{F(a.AvgFps)} (1% {F(a.P1LowFps)})", SvgCharts.ColorFor(i))).ToList();
                sb.Append($"<figure><figcaption>Average FPS by settings-set (1% low in parentheses)</figcaption>{SvgCharts.HBars(bars, unit: " fps")}</figure>");

                bool compatiblePower = PowerProvenance.AreCompatible(rows.Select(a => a.PowerMeasurement));
                bool displayEfficiency = compatiblePower && rows.All(a => PowerProvenance.IsDirectEfficiencyEligible(a.PowerMeasurement));
                sb.Append("<table><thead><tr><th>Settings-set</th><th>Avg FPS</th><th>Δ vs baseline</th><th>1% low</th><th>GPU W</th><th>FPS/W</th><th>Frame gen</th><th>Valid</th></tr></thead><tbody>");
                foreach (var a in rows)
                {
                    bool isBase = ReferenceEquals(a, baseline);
                    bool fg = a.Runs.Any(x => x.FrameGenActive == true);
                    // FG PARITY CHECK: a really-engaged frame generator meaningfully multiplies presents, so an
                    // FG-on row that is NOT well above (>= +15%) some FG-off row of the SAME scene/res means the
                    // game accepted the config value but never generated frames (live 2026-07-03: CP rt-dlss-fg
                    // 50.4 ≡ rt-dlss-q 50.6 — menu-mirror field; MSFS normalized FG away at boot; F1 frame_gen
                    // mode=1 measured BELOW its FG-off twin, 53.7 vs 62.0 — overhead without generation). Flag it
                    // so the row can't be read as a real FG number.
                    var fgOffPeer = fg
                        ? rows.Where(r => !ReferenceEquals(r, a) && !r.Runs.Any(x => x.FrameGenActive == true) && r.AvgFps > 0)
                              .FirstOrDefault(r => a.AvgFps < r.AvgFps * 1.15)
                        : null;
                    double? delta = isBase || baseline.AvgFps <= 0 ? null : (a.AvgFps - baseline.AvgFps) / baseline.AvgFps * 100.0;
                    string deltaCell = isBase ? "baseline" : delta is double d ? $"{(d >= 0 ? "+" : "")}{F(d, "0.0")}%" : "—";
                    string deltaCls = delta is double dd ? (dd >= 0 ? "ok" : "bad") : "";
                    string fgCell = !fg ? "off"
                        : fgOffPeer is not null
                            ? $"<span class=\"fg-on\">ON ⚠ but fps not above {SvgCharts.Esc(VariantLabel(fgOffPeer))} (FG-off) — FG likely did NOT engage</span>"
                            : "<span class=\"fg-on\">ON ⚠</span>";
                    sb.Append($"<tr><td class=\"ml\">{SvgCharts.Esc(VariantLabel(a))}{(isBase ? " <strong>(baseline)</strong>" : "")}</td>");
                    sb.Append($"<td>{F(a.AvgFps)}</td><td class=\"{deltaCls}\">{SvgCharts.Esc(deltaCell)}</td><td>{F(a.P1LowFps)}</td>");
                    sb.Append($"<td>{(compatiblePower ? F(a.AvgGpuPowerW, "0") : "—")}<br/><small>{SvgCharts.Esc(PowerProvenance.Label(a.PowerMeasurement))}</small></td><td>{(displayEfficiency ? F(a.FpsPerWatt, "0.000") : "—")}</td>");
                    sb.Append($"<td>{fgCell}</td><td>{a.ValidRuns}/{a.TotalRuns}</td></tr>");
                }
                sb.Append("</tbody></table>");
                if (!compatiblePower) sb.Append("<p class=\"warn-note\">Power and efficiency comparison suppressed: rows use incompatible power measurement kinds.</p>");
                sb.Append("</section>");
            }
        }
    }

    private static string VariantLabel(SceneResolutionAggregate a)
        => string.IsNullOrWhiteSpace(a.VariantId) || a.VariantId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? "default"
            : string.IsNullOrWhiteSpace(a.VariantName) ? a.VariantId : a.VariantName;

    private void SceneSection(StringBuilder sb, string gameId, string sceneId, IReadOnlyList<SceneResolutionAggregate> aggs)
    {
        sb.Append("<section class=\"wrap scene\">");
        // A non-default graphics variant ("extra model") is shown as a tag next to the scene name.
        var variant = aggs.Count > 0 ? aggs[0].VariantName : "";
        var variantId = aggs.Count > 0 ? aggs[0].VariantId : "";
        var vtag = !string.IsNullOrWhiteSpace(variantId) && !variantId.Equals("default", StringComparison.OrdinalIgnoreCase)
            ? $" <span class=\"vtag\">{SvgCharts.Esc(string.IsNullOrWhiteSpace(variant) ? variantId : variant)}</span>"
            : "";
        sb.Append($"<h3>{SvgCharts.Esc(SceneName(sceneId))}{vtag}</h3>");

        // As-set render-settings fingerprint (read off the game's own config at run time): what the numbers
        // below ACTUALLY measured. FG-on means AvgFps counts generated presents (2-4× the rendered rate) —
        // it must never be read as a render-fps number or compared against a native benchmark.
        var fpRun = aggs.SelectMany(a => a.Runs).FirstOrDefault(r => r.SettingsFingerprint is { Count: > 0 });
        if (fpRun is not null)
        {
            string fp = string.Join(" · ", fpRun.SettingsFingerprint!.Select(p => $"{p.Key}={p.Value}"));
            string fgBadge = fpRun.FrameGenActive == true
                ? "<strong class=\"fg-on\"> ⚠ FRAME GEN ON — fps counts generated presents, not rendered frames</strong>"
                : "";
            sb.Append($"<p class=\"fingerprint\">As-set render settings: {SvgCharts.Esc(fp)}{fgBadge}</p>");
        }

        // Charts: avg FPS (+1% low) and avg GPU power by resolution
        var fpsBars = aggs.Select((a, i) => new SvgCharts.Bar(
            a.ResolutionName, a.AvgFps, $"{F(a.AvgFps)} (1% {F(a.P1LowFps)})", SvgCharts.ColorFor(i))).ToList();
        var pwrBars = aggs.Select((a, i) => new SvgCharts.Bar(
            a.ResolutionName, a.AvgGpuPowerW ?? 0, F(a.AvgGpuPowerW, "0"), SvgCharts.ColorFor(i))).ToList();

        sb.Append("<div class=\"charts\">");
        sb.Append($"<figure><figcaption>Average FPS by resolution (1% low in parentheses)</figcaption>{SvgCharts.HBars(fpsBars, unit: " fps")}</figure>");
        sb.Append($"<figure><figcaption>Average GPU power by resolution (provenance-labelled)</figcaption>{SvgCharts.HBars(pwrBars, unit: " W")}</figure>");
        sb.Append("</div>");

        // Detailed metric table (rows = metric, columns = resolution)
        sb.Append("<table><thead><tr><th>Metric</th>");
        foreach (var a in aggs) sb.Append($"<th>{SvgCharts.Esc(a.ResolutionName)}</th>");
        sb.Append("</tr></thead><tbody>");

        Row(sb, "Average FPS", aggs, a => F(a.AvgFps));
        Row(sb, "1% low FPS", aggs, a => F(a.P1LowFps));
        Row(sb, "0.1% low FPS", aggs, a => F(a.P01LowFps));
        Row(sb, "Avg frame time (ms)", aggs, a => F(a.AvgFrameTimeMs, "0.00"));
        Row(sb, "99th %ile frame time (ms)", aggs, a => F(a.P99FrameTimeMs, "0.00"));
        Row(sb, "Stutter (%)", aggs, a => F(a.StutterPct, "0.00"));
        Row(sb, "Power provenance", aggs, a => PowerProvenance.Label(a.PowerMeasurement));
        Row(sb, "Avg GPU power (W)", aggs, a => F(a.AvgGpuPowerW, "0.0"));
        Row(sb, "Peak GPU power (W)", aggs, a => F(a.PeakGpuPowerW, "0.0"));
        Row(sb, "Energy / frame (J)", aggs, a => F(PowerProvenance.IsDirectEfficiencyEligible(a.PowerMeasurement) ? a.EnergyPerFrameJ : null, "0.000"));
        Row(sb, "Performance / watt (FPS/W)", aggs, a => F(PowerProvenance.IsDirectEfficiencyEligible(a.PowerMeasurement) ? a.FpsPerWatt : null, "0.000"));
        Row(sb, "GPU temp avg (°C)", aggs, a => F(a.GpuTempAvgC));
        Row(sb, "GPU hotspot max (°C)", aggs, a => F(a.GpuHotspotMaxC));
        Row(sb, "VRAM temp max (°C)", aggs, a => F(a.GpuVramTempMaxC));
        Row(sb, "GPU core clock avg (MHz)", aggs, a => F(a.GpuCoreClockAvgMhz, "0"));
        Row(sb, "Fan speed avg (RPM)", aggs, a => F(a.FanRpmAvg, "0"));
        Row(sb, "CPU temp avg (°C)", aggs, a => F(a.CpuTempAvgC));   // LHM SMU — populated only when run elevated
        Row(sb, "CPU load avg (%)", aggs, a => F(a.CpuLoadAvgPct, "0"));
        Row(sb, "Run-to-run variance (%)", aggs, a => F(a.FpsVariancePct, "0.00"));
        Row(sb, "Valid runs", aggs, a => $"{a.ValidRuns}/{a.TotalRuns}", a => a.ValidRuns == 0 ? "bad" : a.ValidRuns < a.TotalRuns ? "warn" : "ok");

        sb.Append("</tbody></table>");

        // Per-run verdicts (transparency: never hide bad runs)
        sb.Append("<details class=\"runs\"><summary>Per-run detail</summary><table class=\"mini\"><thead><tr><th>Res</th><th>Run</th><th>Avg FPS</th><th>1% low</th><th>GPU W</th><th title=\"Capture-card motion over the measured window (scene-static sensor): mean/min across N probes — proof the measured scene was genuinely moving. '—' = sensor not armed (self-sensing bot / no capture device).\">Motion</th><th>Detect</th><th>Game FPS</th><th>Verdict</th><th>Issues</th></tr></thead><tbody>");
        foreach (var a in aggs)
            foreach (var r in a.Runs)
            {
                string motion = r.MeasuredMotion is { } m
                    ? $"{m.MeanScore:0.00}/{m.MinScore:0.00} ×{m.Probes}{(m.BelowFloor > 0 ? $" ({m.BelowFloor} low)" : "")}"
                    : "—";
                sb.Append($"<tr class=\"v-{r.Verdict.ToString().ToLowerInvariant()}\"><td>{SvgCharts.Esc(a.ResolutionName)}</td><td>{r.RepeatIndex}</td><td>{F(r.Frames.AvgFps)}{(r.FrameGenActive == true ? "<sup class=\"fg-on\" title=\"Frame generation ON: this fps counts generated presents, not rendered frames.\">FG</sup>" : "")}</td><td>{F(r.Frames.P1LowFps)}</td><td>{F(r.Power.AvgGpuPowerW, "0")}<br/><small>{SvgCharts.Esc(PowerProvenance.Label(r.Power.Measurement))}</small></td><td>{SvgCharts.Esc(motion)}</td><td>{SvgCharts.Esc(r.CompletionMode ?? "—")}</td><td>{F(r.GameReportedFps)}</td><td>{r.Verdict}</td><td class=\"issues\">{SvgCharts.Esc(string.Join("; ", r.ValidationIssues))}</td></tr>");
            }
        sb.Append("</tbody></table></details>");

        sb.Append("</section>");
    }

    private static void Row(StringBuilder sb, string label, IReadOnlyList<SceneResolutionAggregate> aggs,
        Func<SceneResolutionAggregate, string> cell, Func<SceneResolutionAggregate, string>? cls = null)
    {
        sb.Append($"<tr><td class=\"ml\">{SvgCharts.Esc(label)}</td>");
        foreach (var a in aggs)
        {
            string c = cls?.Invoke(a) ?? "";
            sb.Append($"<td class=\"{c}\">{SvgCharts.Esc(cell(a))}</td>");
        }
        sb.Append("</tr>");
    }

    private void Methodology(StringBuilder sb, SuiteResult s)
    {
        sb.Append("<footer class=\"wrap\"><h3>Methodology</h3><ul>");
        var (frameProviders, powerProviders, telemetryProviders) = ProviderNames(s);
        sb.Append($"<li>FPS provider(s): <b>{SvgCharts.Esc(frameProviders)}</b>. <b>1% / 0.1% lows</b> are time-weighted (CapFrameX-style): the average FPS over the slowest 1% / 0.1% of total capture time. Percentile-of-frame-time lows are also recorded per run.</li>");
        sb.Append($"<li>Power provider(s): <b>{SvgCharts.Esc(powerProviders)}</b>. Direct Powenetics power covers PCIe slot + auxiliary GPU rails and is eligible for Hardware Busters power-efficiency metrics. LHM is APPROXIMATE · GPU TELEMETRY; synthetic, replay, unknown, and legacy-inferred power are NOT A HARDWARE RESULT. Energy/frame and FPS/W are withheld unless direct eligible.</li>");
        sb.Append($"<li>Telemetry provider(s): <b>{SvgCharts.Esc(telemetryProviders)}</b>. Temperatures, clocks, load, and fans are attributed to this exact backend.</li>");
        sb.Append("<li><b>Temperatures, clocks and fan speed</b> come from LibreHardwareMonitor sampled continuously through each run.</li>");
        sb.Append("<li>The measured window is <b>marker-driven</b>, not a fixed timer: built-in benchmarks detect start/finish from the game's result/log file (and cross-check the game's own reported FPS in the <i>Game FPS</i> column), falling back to frame-provider activity. The <i>Detect</i> column shows which method bounded each run.</li>");
        sb.Append("<li>Each scene/resolution runs at least 3 repeats. Only <b>valid</b> runs are averaged; invalid runs are rejected and shown, never silently averaged. Outliers are flagged and auto-repeated within budget. Graphics settings are applied and verified before capture; a failed verification marks the run invalid.</li>");
        var (f, p, t) = SourceModes(s);
        if (!f || !p || !t)
            sb.Append($"<li class=\"warn-note\">Degraded mode: some sources were synthetic for this report (Frames {(f ? "live" : "synthetic")}, Power {(p ? "live" : "synthetic")}, Telemetry {(t ? "live" : "synthetic")}). Numbers from synthetic sources are for pipeline validation, not hardware conclusions.</li>");
        if (!string.IsNullOrWhiteSpace(s.System.PoweneticsNote))
            sb.Append($"<li class=\"warn-note\">{SvgCharts.Esc(s.System.PoweneticsNote)}</li>");
        // CPU temperature/package power need the process elevated (LHM's SMU/Ring0). When no aggregate has
        // one, say so explicitly instead of leaving silent zeros/dashes in the table above.
        bool anyCpuTemp = s.Aggregates.Any(a => a.CpuTempAvgC is not null);
        if (s.Aggregates.Count > 0 && !anyCpuTemp)
            sb.Append("<li class=\"warn-note\">CPU temperature was not captured in any run — it requires the suite to run as Administrator (LibreHardwareMonitor's SMU/Ring0 access). The CPU rows above are absent, not zero.</li>");
        sb.Append("</ul></footer>");
    }

    private static (bool frames, bool power, bool tel) SourceModes(SuiteResult s)
    {
        var runs = s.Aggregates.SelectMany(a => a.Runs).ToList();
        if (runs.Count == 0) return (s.System.PresentMonAvailable, s.System.PoweneticsConnected, s.System.LhmAvailable);
        return (runs.All(r => r.FrameSource == DataSourceMode.Live),
                runs.All(r => r.PowerSource == DataSourceMode.Live),
                runs.All(r => r.TelemetrySource == DataSourceMode.Live));
    }

    private static (string frames, string power, string telemetry) ProviderNames(SuiteResult s)
    {
        var runs = s.Aggregates.SelectMany(a => a.Runs).ToList();
        static string Join(IEnumerable<string> values, string fallback)
        {
            var names = values.Where(v => !string.IsNullOrWhiteSpace(v))
                              .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v).ToList();
            return names.Count == 0 ? fallback : string.Join(" + ", names);
        }
        return (Join(runs.Select(r => r.FrameProviderName), "unknown"),
                Join(runs.Select(r => r.PowerProviderName), "unknown"),
                Join(runs.Select(r => r.TelemetryProviderName), "unknown"));
    }

    private static string PowerLabels(SuiteResult s)
    {
        var labels = s.Aggregates.SelectMany(a => a.Runs)
            .Select(r => PowerProvenance.Label(r.Power.Measurement))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return labels.Count == 0 ? "NOT A HARDWARE RESULT" : string.Join(" + ", labels);
    }

    private static bool HasEligibleEfficiency(SuiteResult suite) => suite.Aggregates
        .Any(aggregate => PowerProvenance.IsDirectEfficiencyEligible(aggregate.PowerMeasurement));

    private static IEnumerable<KeyValuePair<string, List<T>>> GroupOrdered<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var order = new List<string>();
        var map = new Dictionary<string, List<T>>();
        foreach (var it in items)
        {
            var k = key(it);
            if (!map.TryGetValue(k, out var list)) { list = new List<T>(); map[k] = list; order.Add(k); }
            list.Add(it);
        }
        foreach (var k in order) yield return new(k, map[k]);
    }

    private string GameName(string id) => GameNames is not null && GameNames.TryGetValue(id, out var n) ? n : Prettify(id);
    private string SceneName(string id) => SceneNames is not null && SceneNames.TryGetValue(id, out var n) ? n : Prettify(id);
    private static string Prettify(string id) => string.Join(' ', id.Split('-', '_').Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w[1..]));

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
.badge.warn{background:rgba(224,102,58,.14);color:#f0a06a;border-color:#c62;cursor:help}
.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:14px;margin:22px auto}
.card{background:var(--panel);border:1px solid var(--line);border-radius:12px;padding:16px}
.ct{color:var(--mut);font-size:12px;text-transform:uppercase;letter-spacing:.4px}
.cval{font-size:28px;font-weight:700;margin:6px 0 2px}.csub{color:var(--mut);font-size:12px}
h2.game{max-width:1100px;margin:34px auto 4px;padding:0 20px;font-size:22px;border-left:4px solid var(--accent);}
.scene{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px 20px;margin:16px auto}
.scene h3{margin:0 0 12px;font-size:17px}
.vtag{font-size:12px;font-weight:600;color:var(--accent);background:rgba(91,141,239,.12);border:1px solid var(--accent);border-radius:999px;padding:2px 9px;margin-left:8px;vertical-align:middle}
.charts{display:grid;grid-template-columns:1fr 1fr;gap:18px;margin-bottom:14px}
figure{margin:0;background:var(--panel2);border:1px solid var(--line);border-radius:10px;padding:12px}
figcaption{color:var(--mut);font-size:12px;margin-bottom:8px}
.cl{fill:var(--mut);font-size:12px}.cv{fill:var(--txt);font-size:12px;font-weight:600}
table{width:100%;border-collapse:collapse;margin-top:6px;font-variant-numeric:tabular-nums}
th,td{padding:7px 10px;text-align:right;border-bottom:1px solid var(--line)}
th:first-child,td.ml{text-align:left;color:var(--mut)}
thead th{font-size:12px;color:var(--mut);text-transform:uppercase;letter-spacing:.3px}
td.ok{color:#7be0b0}td.warn{color:#e9c07a}td.bad{color:#e88}
.runs{margin-top:12px}.runs summary{cursor:pointer;color:var(--mut)}
table.mini th,table.mini td{font-size:12px;padding:4px 8px}
.v-valid td{color:var(--txt)}.v-outlier td{color:#e9c07a}.v-invalid td{color:#e88}
td.issues{text-align:left;color:var(--mut);font-size:11px;max-width:380px}
p.fingerprint{color:var(--mut);font-size:12px;margin:2px 0 8px}
.fg-on{color:#e8a03c;font-weight:600}
@media(max-width:860px){.cards{grid-template-columns:repeat(2,1fr)}.charts{grid-template-columns:1fr}}
footer{margin:30px auto 60px}footer ul{color:var(--mut);font-size:13px}.warn-note{color:#e9c07a}
";
}
