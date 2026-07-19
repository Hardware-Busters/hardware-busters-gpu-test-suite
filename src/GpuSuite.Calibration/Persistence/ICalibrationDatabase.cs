using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Persistence;

/// <summary>
/// The calibration store — the single abstraction seam (module 11). We start with human-readable JSON
/// files on disk (git-diffable, human-approvable); a SQLite-backed implementation can drop in LATER
/// behind this same interface WITHOUT changing any caller (principle P3). Callers never see files or SQL.
/// </summary>
public interface ICalibrationDatabase
{
    /// <summary>Root directory of the store (e.g. "Calibration").</summary>
    string Root { get; }

    Task<GameCalibration?> GetGameAsync(string game, CancellationToken ct);
    Task SaveGameAsync(GameCalibration game, CancellationToken ct);

    /// <summary>Persist a menu graph under the game's menus/, versioned by its env fingerprint.</summary>
    Task SaveGraphAsync(string game, MenuGraph graph, CancellationToken ct);
    /// <summary>The most recently saved graph for the game (the drift baseline), or null if none yet.</summary>
    Task<MenuGraph?> GetBaselineGraphAsync(string game, CancellationToken ct);

    Task SaveLayoutAsync(string game, LayoutSnapshot snapshot, CancellationToken ct);
    Task<LayoutSnapshot?> GetLayoutAsync(string game, string screen, CancellationToken ct);

    Task SaveRouteAsync(string game, RouteRecord route, CancellationToken ct);
    Task<RouteRecord?> GetRouteAsync(string game, string goal, CancellationToken ct);

    /// <summary>Persist a bot DRAFT (never a live route — promotion to profiles/bots/ is a human action).</summary>
    Task SaveBotDraftAsync(string game, BotDraft draft, CancellationToken ct);

    Task AppendValidationAsync(string game, ValidationEvent ev, CancellationToken ct);
    Task AppendDriftAsync(string game, DriftEvent ev, CancellationToken ct);

    // ── Phase 5: shared knowledge, regression history, and the hard human gate ──
    Task<IReadOnlyDictionary<string, MenuGraph>> GetAllBaselineGraphsAsync(CancellationToken ct);
    Task SaveSharedPatternsAsync(IReadOnlyList<SharedCalibrationPattern> patterns, CancellationToken ct);
    Task<IReadOnlyList<SharedCalibrationPattern>> GetSharedPatternsAsync(CancellationToken ct);
    Task AppendRegressionAsync(string game, RegressionAnalysis analysis, CancellationToken ct);
    Task QueueApprovalAsync(ApprovalRequest request, CancellationToken ct);
    Task<IReadOnlyList<ApprovalRequest>> GetApprovalsAsync(ApprovalStatus? status, CancellationToken ct);
    /// <summary>Records a human decision only. It deliberately does not apply the proposal.</summary>
    Task<ApprovalRequest?> DecideApprovalAsync(string id, ApprovalStatus decision, string? note, CancellationToken ct);

    /// <summary>Save an evidence screenshot; returns the path RELATIVE to the game's screenshots/ folder.</summary>
    Task<string> SaveScreenshotAsync(string game, string name, byte[] png, CancellationToken ct);

    // ── Directory helpers (so the report generator can write into the right place) ──
    string GameDirectory(string game);
    string HistoryDirectory(string game);
    string ScreenshotsDirectory(string game);
}
