using System.Net;
using System.Text;
using GpuSuite.Core.Io;
using GpuSuite.Calibration.Schema;
using GpuSuite.Calibration.Decisions;

namespace GpuSuite.Calibration.Reporting;

/// <summary>The data a calibration report renders — the single artifact a human reads to approve/reject.</summary>
public sealed class CalibrationReportModel
{
    public string Game { get; set; } = "";
    public string Name { get; set; } = "";
    public string SuiteVersion { get; set; } = "";
    public string GeneratedAtIso { get; set; } = "";
    public EnvFingerprint Env { get; set; } = new();
    public MenuGraph Graph { get; set; } = new();
    public DriftAnalysis? Drift { get; set; }
    /// <summary>The full decision audit trail — why every branch (OCR/escalate/consult/recommend/approve) was taken.</summary>
    public List<CalibrationDecision> Decisions { get; set; } = new();
    /// <summary>Advisory proposals; each is inert until a human approves it.</summary>
    public List<Recommendation> Recommendations { get; set; } = new();
    public GameplayVerdict? Gameplay { get; set; }
    public FailureClassification? Failure { get; set; }
    public RecoveryPlan? Recovery { get; set; }
    public Phase5Analysis? Phase5 { get; set; }
}

/// <summary>Where the generated report landed.</summary>
public sealed class ReportRefs
{
    public string HtmlPath { get; set; } = "";
    public string JsonPath { get; set; } = "";
}

/// <summary>Produces the human review report (module 12).</summary>
public interface ICalibrationReportGenerator
{
    Task<ReportRefs> GenerateAsync(CalibrationReportModel model, string outDir, string fileStem, CancellationToken ct);
}

/// <summary>
/// Self-contained HTML report generator (no external assets), matching the suite's existing report style.
/// Renders the menu graph (screens + controls), the drift analysis, the recommendations (each with its
/// confidence and whether it requires human approval), and — front and center — the decision audit trail.
/// Also writes a machine-readable .json companion so approvals can be scripted later.
/// </summary>
public sealed class HtmlCalibrationReportGenerator : ICalibrationReportGenerator
{
    public async Task<ReportRefs> GenerateAsync(CalibrationReportModel model, string outDir, string fileStem, CancellationToken ct)
    {
        Directory.CreateDirectory(outDir);
        var htmlPath = Path.Combine(outDir, fileStem + ".html");
        var jsonPath = Path.Combine(outDir, fileStem + ".json");

        Json.Save(jsonPath, model);
        await File.WriteAllTextAsync(htmlPath, RenderHtml(model), Encoding.UTF8, ct).ConfigureAwait(false);

        return new ReportRefs { HtmlPath = htmlPath, JsonPath = jsonPath };
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string RenderHtml(CalibrationReportModel m)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Calibration — ")
          .Append(H(m.Name)).Append("</title><style>")
          .Append("body{font:14px/1.5 Segoe UI,system-ui,sans-serif;margin:24px;color:#1a1a1a;background:#fafafa}")
          .Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;border-bottom:1px solid #ddd;padding-bottom:4px;margin-top:28px}")
          .Append(".muted{color:#666}.tag{display:inline-block;padding:1px 7px;border-radius:10px;font-size:12px}")
          .Append(".hi{background:#0b7a3b;color:#fff}.med{background:#b8860b;color:#fff}.lo{background:#b03030;color:#fff}.ab{background:#555;color:#fff}")
          .Append(".approve{background:#b03030;color:#fff}.auto{background:#0b7a3b;color:#fff}")
          .Append("table{border-collapse:collapse;width:100%;margin:8px 0;background:#fff}td,th{border:1px solid #e2e2e2;padding:6px 8px;text-align:left;vertical-align:top}")
          .Append("th{background:#f0f0f0}code{background:#eee;padding:1px 4px;border-radius:3px}.banner{background:#eef4ff;border:1px solid #bcd;padding:8px 12px;border-radius:6px}")
          .Append("</style></head><body>");

        sb.Append("<h1>AI Calibration Report — ").Append(H(m.Name)).Append("</h1>");
        sb.Append("<div class=\"muted\">game <code>").Append(H(m.Game)).Append("</code> · suite ").Append(H(m.SuiteVersion))
          .Append(" · generated ").Append(H(m.GeneratedAtIso)).Append("</div>");
        sb.Append("<div class=\"banner\" style=\"margin-top:10px\">Advisory only. The AI Calibration Engineer observed and reasoned; ")
          .Append("it did not change anything. Every item below is a recommendation for you to approve or reject.</div>");

        // Environment fingerprint
        sb.Append("<h2>Environment</h2><table>");
        void Row(string k, string? v) => sb.Append("<tr><th style=\"width:200px\">").Append(H(k)).Append("</th><td>").Append(H(v)).Append("</td></tr>");
        Row("Game version", m.Env.GameVersion); Row("Suite version", m.Env.SuiteVersion);
        Row("GPU", m.Env.Gpu); Row("Driver", m.Env.DriverVersion); Row("Windows", m.Env.WindowsVersion);
        Row("Resolution", m.Env.Resolution); Row("Preset", m.Env.Preset); Row("Language", m.Env.Language);
        sb.Append("</table>");

        // Menu graph
        sb.Append("<h2>Menu graph — ").Append(m.Graph.Nodes.Count).Append(" screen(s), ").Append(m.Graph.Edges.Count).Append(" transition(s)</h2>");
        foreach (var n in m.Graph.Nodes)
        {
            sb.Append("<h3 style=\"margin:14px 0 4px\"><code>").Append(H(n.Id)).Append("</code> ").Append(Band(n.Confidence)).Append("</h3>");
            if (!string.IsNullOrWhiteSpace(n.SemanticDescription))
                sb.Append("<div class=\"muted\">").Append(H(n.SemanticDescription)).Append("</div>");
            if (n.Controls.Count > 0)
            {
                sb.Append("<table><tr><th>Control</th><th>Type</th><th>Value</th><th>Sources</th><th>Confidence</th></tr>");
                foreach (var c in n.Controls)
                    sb.Append("<tr><td>").Append(H(c.Label)).Append("</td><td>").Append(H(c.Type)).Append("</td><td>")
                      .Append(H(c.Value)).Append("</td><td>").Append(H(string.Join(", ", c.Sources))).Append("</td><td>")
                      .Append(Band(c.Confidence)).Append("</td></tr>");
                sb.Append("</table>");
            }
        }

        // Drift
        if (m.Drift is not null && (m.Drift.Changes.Count > 0 || m.Drift.Abstained))
        {
            sb.Append("<h2>Drift vs. baseline</h2>");
            if (m.Drift.Abstained) sb.Append("<div class=\"muted\">").Append(H(m.Drift.Rationale)).Append("</div>");
            if (m.Drift.Changes.Count > 0)
            {
                sb.Append("<table><tr><th>Screen</th><th>Change</th><th>Impact</th><th>Confidence</th></tr>");
                foreach (var ch in m.Drift.Changes)
                    sb.Append("<tr><td>").Append(H(ch.Screen)).Append("</td><td>").Append(H(ch.Change)).Append("</td><td>")
                      .Append(H(ch.Impact)).Append("</td><td>").Append(Band(ch.Confidence)).Append("</td></tr>");
                sb.Append("</table>");
            }
        }

        // Recommendations
        if (m.Gameplay is not null)
        {
            sb.Append("<h2>Phase-3 gameplay validation</h2><table>");
            void GRow(string key, string value) => sb.Append("<tr><th style=\"width:200px\">").Append(H(key)).Append("</th><td>").Append(H(value)).Append("</td></tr>");
            GRow("Gameplay", m.Gameplay.IsGameplay ? "pass" : "fail");
            GRow("Scene identity", m.Gameplay.SceneMatch ? "match" : "mismatch");
            GRow("Spawn", m.Gameplay.SpawnMatch ? "match" : "mismatch");
            GRow("HUD", m.Gameplay.HudMatch ? "match" : "mismatch");
            GRow("Shader hitches", m.Gameplay.ShaderHitchesSettled ? "settled / none" : "not settled");
            GRow("Visual evidence", m.Gameplay.VisualEvidenceUsed ? "used" : "metadata only");
            GRow("Rationale", m.Gameplay.Rationale);
            if (m.Failure is not null) GRow("Failure class", $"{m.Failure.FailureClass} (transient={m.Failure.Transient})");
            if (m.Recovery is not null) GRow("Recovery", string.Join(" → ", m.Recovery.Steps.Select(s => s.Kind)));
            sb.Append("</table>");
        }

        if (m.Phase5 is not null)
        {
            sb.Append("<h2>Phase 5 — self-improving calibration</h2><table>");
            void PRow(string key, string value) => sb.Append("<tr><th style=\"width:240px\">").Append(H(key)).Append("</th><td>").Append(H(value)).Append("</td></tr>");
            PRow("Shared cross-game patterns", m.Phase5.SharedPatterns.Count.ToString());
            PRow("Threshold proposals", m.Phase5.ThresholdRecommendations.Count.ToString());
            PRow("Approval requests", m.Phase5.ApprovalRequests.Count.ToString());
            if (m.Phase5.Regression is not null) PRow("Regression analysis", m.Phase5.Regression.Summary);
            sb.Append("</table>");

            if (m.Phase5.SharedPatterns.Count > 0)
            {
                sb.Append("<h3>Shared pattern library (inert evidence)</h3><table><tr><th>Kind</th><th>Signature</th><th>Source games</th><th>Confidence</th></tr>");
                foreach (var p in m.Phase5.SharedPatterns)
                    sb.Append("<tr><td>").Append(H(p.Kind.ToString())).Append("</td><td><code>").Append(H(p.Signature))
                      .Append("</code></td><td>").Append(H(string.Join(", ", p.SourceGames))).Append("</td><td>").Append(Band(p.Confidence)).Append("</td></tr>");
                sb.Append("</table>");
            }
            if (m.Phase5.ThresholdRecommendations.Count > 0)
            {
                sb.Append("<h3>Data-driven threshold proposals</h3><table><tr><th>Key</th><th>Current → proposed</th><th>Samples</th><th>Observed</th><th>Confidence</th></tr>");
                foreach (var t in m.Phase5.ThresholdRecommendations)
                    sb.Append("<tr><td><code>").Append(H(t.Key)).Append("</code></td><td>").Append(H(t.CurrentValue)).Append(" → ").Append(H(t.ProposedValue))
                      .Append("</td><td>").Append(t.SampleCount).Append("</td><td>").Append(H(t.ObservedRange)).Append("</td><td>").Append(Band(t.Confidence)).Append("</td></tr>");
                sb.Append("</table>");
            }
            if (m.Phase5.Regression is { } regression && regression.Findings.Count > 0)
            {
                sb.Append("<h3>Automatic regression findings</h3><table><tr><th>Cell</th><th>Metric</th><th>Baseline → current</th><th>Severity</th><th>Why</th></tr>");
                foreach (var f in regression.Findings)
                    sb.Append("<tr><td><code>").Append(H(f.CellKey)).Append("</code></td><td>").Append(H(f.Metric)).Append("</td><td>")
                      .Append(H(f.BaselineValue?.ToString("0.##"))).Append(" → ").Append(H(f.CurrentValue?.ToString("0.##")))
                      .Append("</td><td>").Append(H(f.Severity.ToString())).Append("</td><td>").Append(H(f.Rationale)).Append("</td></tr>");
                sb.Append("</table>");
            }
        }

        // Recommendations
        sb.Append("<h2>Recommendations — ").Append(m.Recommendations.Count).Append("</h2>");
        if (m.Recommendations.Count == 0) sb.Append("<div class=\"muted\">None.</div>");
        else
        {
            sb.Append("<table><tr><th>Kind</th><th>Summary</th><th>Proposed change</th><th>Approval</th><th>Confidence</th></tr>");
            foreach (var r in m.Recommendations)
            {
                var gate = r.RequiresHumanApproval ? "<span class=\"tag approve\">human approval</span>" : "<span class=\"tag auto\">auto-eligible</span>";
                sb.Append("<tr><td>").Append(H(r.Kind.ToString())).Append("</td><td>").Append(H(r.Summary)).Append("</td><td><code>")
                  .Append(H(r.ProposedChange)).Append("</code></td><td>").Append(gate).Append("</td><td>").Append(Band(r.Confidence)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        // Decision audit trail — the heart of the report
        sb.Append("<h2>Decision audit trail — ").Append(m.Decisions.Count).Append(" decision(s)</h2>");
        sb.Append("<div class=\"muted\">Every consequential branch, with its reason: why OCR was accepted, why vision escalation was triggered, why the GX10 was consulted, why a recommendation was generated, why human approval was required.</div>");
        sb.Append("<table><tr><th style=\"width:160px\">Time</th><th>Kind</th><th>Screen</th><th>Why</th><th>Confidence</th></tr>");
        foreach (var d in m.Decisions)
            sb.Append("<tr><td>").Append(H(d.Time.ToString("HH:mm:ss.fff"))).Append("</td><td>").Append(H(d.Kind.ToString()))
              .Append("</td><td>").Append(H(d.Screen)).Append("</td><td>").Append(H(d.Why)).Append("</td><td>")
              .Append(d.Confidence is null ? "" : Band(d.Confidence)).Append("</td></tr>");
        sb.Append("</table>");

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string Band(Confidence c)
    {
        var cls = c.Band switch
        {
            ConfidenceBand.High => "hi",
            ConfidenceBand.Medium => "med",
            ConfidenceBand.Low => "lo",
            _ => "ab"
        };
        return $"<span class=\"tag {cls}\">{c.Band} {c.Value:0.00}</span>";
    }
}
