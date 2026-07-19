using GpuSuite.Core.Io;
using GpuSuite.Calibration.Schema;

namespace GpuSuite.Calibration.Persistence;

/// <summary>
/// The JSON-file implementation of <see cref="ICalibrationDatabase"/> — the store we ship with. Lays out
/// exactly the structure in docs/ai-calibration-architecture.md:
///
///   &lt;root&gt;/&lt;Game&gt;/game.json
///   &lt;root&gt;/&lt;Game&gt;/menus/graph_&lt;ver&gt;.json
///   &lt;root&gt;/&lt;Game&gt;/layouts/&lt;screen&gt;_&lt;ver&gt;.json
///   &lt;root&gt;/&lt;Game&gt;/routes/&lt;goal&gt;_&lt;ver&gt;.json
///   &lt;root&gt;/&lt;Game&gt;/bots/&lt;goal&gt;_draft_&lt;stamp&gt;.json   (DRAFTS only — never executed)
///   &lt;root&gt;/&lt;Game&gt;/screenshots/&lt;name&gt;.png
///   &lt;root&gt;/&lt;Game&gt;/history/{validation,drift,calibration_report}_&lt;stamp&gt;.*
///   &lt;root&gt;/Shared/{OCR,Templates,LLM}/
///
/// "Latest" reads pick the most recently written file. Files are the export format even after a future
/// SQLite store backs the live data; callers only ever see this interface.
/// </summary>
public sealed class JsonCalibrationDatabase : ICalibrationDatabase
{
    public string Root { get; }

    public JsonCalibrationDatabase(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? "Calibration" : root!;
        // Seed the shared, game-agnostic asset folders so they exist for the cross-game library (P4).
        foreach (var sub in new[] { "OCR", "Templates", "LLM" })
            Directory.CreateDirectory(Path.Combine(Root, "Shared", sub));
    }

    private static string Slug(string s)
    {
        var cleaned = new string((s ?? "").Trim().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
        cleaned = cleaned.Trim('-');
        return cleaned.Length > 0 ? cleaned : "unnamed";
    }

    private static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    public string GameDirectory(string game) => Path.Combine(Root, Slug(game));
    public string HistoryDirectory(string game) => EnsureDir(Path.Combine(GameDirectory(game), "history"));
    public string ScreenshotsDirectory(string game) => EnsureDir(Path.Combine(GameDirectory(game), "screenshots"));
    private string MenusDirectory(string game) => EnsureDir(Path.Combine(GameDirectory(game), "menus"));
    private string LayoutsDirectory(string game) => EnsureDir(Path.Combine(GameDirectory(game), "layouts"));
    private string RoutesDirectory(string game) => EnsureDir(Path.Combine(GameDirectory(game), "routes"));
    private string BotsDirectory(string game) => EnsureDir(Path.Combine(GameDirectory(game), "bots"));
    private string SharedTemplatesDirectory() => EnsureDir(Path.Combine(Root, "Shared", "Templates"));
    private string ApprovalsDirectory() => EnsureDir(Path.Combine(Root, "Approvals"));

    private static string EnsureDir(string dir) { Directory.CreateDirectory(dir); return dir; }

    // ── game.json ──
    public Task<GameCalibration?> GetGameAsync(string game, CancellationToken ct)
        => Task.FromResult(Json.Load<GameCalibration>(Path.Combine(GameDirectory(game), "game.json")));

    public Task SaveGameAsync(GameCalibration game, CancellationToken ct)
    {
        Json.Save(Path.Combine(GameDirectory(game.Game), "game.json"), game);
        return Task.CompletedTask;
    }

    // ── menus/graph_<ver>.json ──
    public Task SaveGraphAsync(string game, MenuGraph graph, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(graph.Version)) graph.Version = graph.Env.Key();
        Json.Save(Path.Combine(MenusDirectory(game), $"graph_{Slug(graph.Version)}.json"), graph);
        return Task.CompletedTask;
    }

    public Task<MenuGraph?> GetBaselineGraphAsync(string game, CancellationToken ct)
        => Task.FromResult(LoadLatest<MenuGraph>(MenusDirectory(game), "graph_*.json"));

    // ── layouts/<screen>_<ver>.json ──
    public Task SaveLayoutAsync(string game, LayoutSnapshot snapshot, CancellationToken ct)
    {
        var ver = snapshot.Env.Key();
        Json.Save(Path.Combine(LayoutsDirectory(game), $"{Slug(snapshot.Screen)}_{Slug(ver)}.json"), snapshot);
        return Task.CompletedTask;
    }

    public Task<LayoutSnapshot?> GetLayoutAsync(string game, string screen, CancellationToken ct)
        => Task.FromResult(LoadLatest<LayoutSnapshot>(LayoutsDirectory(game), $"{Slug(screen)}_*.json"));

    // ── routes/<goal>_<ver>.json ──
    public Task SaveRouteAsync(string game, RouteRecord route, CancellationToken ct)
    {
        var ver = route.Env.Key();
        Json.Save(Path.Combine(RoutesDirectory(game), $"{Slug(route.Goal)}_{Slug(ver)}.json"), route);
        return Task.CompletedTask;
    }

    public Task<RouteRecord?> GetRouteAsync(string game, string goal, CancellationToken ct)
        => Task.FromResult(LoadLatest<RouteRecord>(RoutesDirectory(game), $"{Slug(goal)}_*.json"));

    // ── bots/<goal>_draft_<stamp>.json (DRAFT — never executed) ──
    public Task SaveBotDraftAsync(string game, BotDraft draft, CancellationToken ct)
    {
        Json.Save(Path.Combine(BotsDirectory(game), $"{Slug(draft.Goal)}_draft_{Stamp()}.json"), draft);
        return Task.CompletedTask;
    }

    // ── history/ ──
    public Task AppendValidationAsync(string game, ValidationEvent ev, CancellationToken ct)
    {
        Json.Save(Path.Combine(HistoryDirectory(game), $"validation_{Stamp()}.json"), ev);
        return Task.CompletedTask;
    }

    public Task AppendDriftAsync(string game, DriftEvent ev, CancellationToken ct)
    {
        Json.Save(Path.Combine(HistoryDirectory(game), $"drift_{Stamp()}.json"), ev);
        return Task.CompletedTask;
    }

    // ── Phase 5 ──
    public Task<IReadOnlyDictionary<string, MenuGraph>> GetAllBaselineGraphsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new Dictionary<string, MenuGraph>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(Root)) return Task.FromResult<IReadOnlyDictionary<string, MenuGraph>>(result);
        foreach (var dir in new DirectoryInfo(Root).GetDirectories()
                     .Where(d => !string.Equals(d.Name, "Shared", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(d.Name, "Approvals", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            var graph = LoadLatest<MenuGraph>(Path.Combine(dir.FullName, "menus"), "graph_*.json");
            if (graph is not null) result[dir.Name] = graph;
        }
        return Task.FromResult<IReadOnlyDictionary<string, MenuGraph>>(result);
    }

    public Task SaveSharedPatternsAsync(IReadOnlyList<SharedCalibrationPattern> patterns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Json.Save(Path.Combine(SharedTemplatesDirectory(), "patterns.json"), patterns);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SharedCalibrationPattern>> GetSharedPatternsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var patterns = Json.Load<List<SharedCalibrationPattern>>(Path.Combine(SharedTemplatesDirectory(), "patterns.json"))
                       ?? new List<SharedCalibrationPattern>();
        return Task.FromResult<IReadOnlyList<SharedCalibrationPattern>>(patterns);
    }

    public Task AppendRegressionAsync(string game, RegressionAnalysis analysis, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Json.Save(Path.Combine(HistoryDirectory(game), $"regression_{Stamp()}.json"), analysis);
        return Task.CompletedTask;
    }

    public Task QueueApprovalAsync(ApprovalRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Id)) request.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(request.CreatedAtIso)) request.CreatedAtIso = DateTime.UtcNow.ToString("o");
        request.Status = ApprovalStatus.Pending;
        Json.Save(Path.Combine(ApprovalsDirectory(), $"{Slug(request.Id)}.json"), request);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ApprovalRequest>> GetApprovalsAsync(ApprovalStatus? status, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var items = new List<ApprovalRequest>();
        var dir = ApprovalsDirectory();
        foreach (var file in new DirectoryInfo(dir).GetFiles("*.json").OrderBy(f => f.CreationTimeUtc))
        {
            ct.ThrowIfCancellationRequested();
            var item = Json.Load<ApprovalRequest>(file.FullName);
            if (item is not null && (status is null || item.Status == status)) items.Add(item);
        }
        return Task.FromResult<IReadOnlyList<ApprovalRequest>>(items);
    }

    public Task<ApprovalRequest?> DecideApprovalAsync(string id, ApprovalStatus decision, string? note, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (decision == ApprovalStatus.Pending) throw new ArgumentException("A decision must be Approved or Rejected.", nameof(decision));
        var path = Path.Combine(ApprovalsDirectory(), $"{Slug(id)}.json");
        var item = Json.Load<ApprovalRequest>(path);
        if (item is null) return Task.FromResult<ApprovalRequest?>(null);
        item.Status = decision;
        item.DecidedAtIso = DateTime.UtcNow.ToString("o");
        item.DecisionNote = note;
        Json.Save(path, item);
        return Task.FromResult<ApprovalRequest?>(item);
    }

    public async Task<string> SaveScreenshotAsync(string game, string name, byte[] png, CancellationToken ct)
    {
        var file = $"{Slug(name)}.png";
        await File.WriteAllBytesAsync(Path.Combine(ScreenshotsDirectory(game), file), png, ct).ConfigureAwait(false);
        return file;   // relative to screenshots/
    }

    private static T? LoadLatest<T>(string dir, string pattern) where T : class
    {
        if (!Directory.Exists(dir)) return null;
        var newest = new DirectoryInfo(dir).GetFiles(pattern)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest is null ? null : Json.Load<T>(newest.FullName);
    }
}
