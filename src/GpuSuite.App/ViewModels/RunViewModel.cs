using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GpuSuite.App.Services;
using GpuSuite.Core;
using GpuSuite.Core.Config;
using GpuSuite.Core.Diagnostics;
using GpuSuite.Core.Models;
using GpuSuite.Engine.Automation;
using GpuSuite.Engine.Orchestration;

namespace GpuSuite.App.ViewModels;

/// <summary>A selectable graphics variant (settings-set) of a game in the Run console.</summary>
public partial class RunVariantItem : ObservableObject
{
    private readonly Action? _onChanged;
    public string Id { get; }
    public string Label { get; }
    /// <summary>The variant's profile Enabled default — what an unconfigured sweep would run.</summary>
    public bool EnabledDefault { get; }

    [ObservableProperty] private bool selected;

    public RunVariantItem(GameVariant v, Action? onChanged)
    {
        Id = v.Id;
        Label = string.IsNullOrWhiteSpace(v.Name) || string.Equals(v.Name, v.Id, StringComparison.OrdinalIgnoreCase)
            ? v.Id : $"{v.Id} — {v.Name}";
        EnabledDefault = v.Enabled;
        selected = v.Enabled;
        _onChanged = onChanged;
    }

    partial void OnSelectedChanged(bool value) => _onChanged?.Invoke();
}

/// <summary>A selectable runnable game in the Run console, with its per-run variant picks.</summary>
public partial class RunGameItem : ObservableObject
{
    private readonly Action? _onChanged;
    public GameProfile Game { get; }
    public string Id => Game.Id;
    public string Name => string.IsNullOrWhiteSpace(Game.Name) ? Game.Id : Game.Name;
    public string StoreLabel => string.IsNullOrWhiteSpace(Game.Launch.Store) ? "—" : Game.Launch.Store;

    /// <summary>The game's defined variants (settings-sets), pre-ticked to their profile Enabled defaults.
    /// Ticking others here selects them for THIS run only — the profile is never modified.</summary>
    public ObservableCollection<RunVariantItem> Variants { get; } = new();
    public bool HasVariants => Variants.Count > 0;

    [ObservableProperty] private bool selected;

    public RunGameItem(GameProfile game, bool selected, Action? onChanged)
    {
        Game = game;
        this.selected = selected;
        _onChanged = onChanged;
        // Calibration proofs and state-restore helpers are intentionally CLI-only. Showing them as models made
        // the Full preset select them and produced mislabeled data when a one-knob proof inherited other state.
        foreach (var v in game.Variants.Where(v => v.BenchmarkEligible))
            Variants.Add(new RunVariantItem(v, onChanged));
    }

    /// <summary>The variant ids ticked for this run (empty when the game defines none).</summary>
    public List<string> SelectedVariantIds() => Variants.Where(v => v.Selected).Select(v => v.Id).ToList();

    /// <summary>Re-tick to an explicit id set (plan load), or back to the profile Enabled defaults (empty/null).</summary>
    public void ApplyVariantSelection(IReadOnlyList<string>? ids)
    {
        foreach (var v in Variants)
            v.Selected = ids is { Count: > 0 }
                ? ids.Contains(v.Id, StringComparer.OrdinalIgnoreCase)
                : v.EnabledDefault;
    }

    partial void OnSelectedChanged(bool value) => _onChanged?.Invoke();
}

/// <summary>A selectable resolution in the Run console.</summary>
public partial class RunResolutionItem : ObservableObject
{
    private readonly Action? _onChanged;
    public string Name { get; }

    [ObservableProperty] private bool selected;

    public RunResolutionItem(string name, bool selected, Action? onChanged)
    {
        Name = name;
        this.selected = selected;
        _onChanged = onChanged;
    }

    partial void OnSelectedChanged(bool value) => _onChanged?.Invoke();
}

/// <summary>
/// Run console: pick which runnable games / per-game variants / resolutions / repeats to sweep, then
/// Start. A named RUN PLAN saves the whole selection (games + variant picks + resolution subset) to
/// plans.json for one-click reuse — profiles are never modified. Streams the engine log live, supports
/// Cancel, and offers the HTML report on completion. NOTE: Start actually launches the selected games —
/// intended to run on the bench (Test PC).
/// </summary>
public partial class RunViewModel : ObservableObject
{
    private readonly Workspace _ws;
    private readonly ProfileService _profiles;
    private readonly DiscoveryService _discovery;
    private readonly RunService _run;
    private readonly RosterSmokeService _smoke;
    private readonly FullPreflightService _preflight;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _liveTimer;
    private BenchmarkProgress? _lastProgress;
    private bool _loadedOnce;
    private DateTime _runStartedUtc;

    public ObservableCollection<RunGameItem> Games { get; } = new();
    public ObservableCollection<RunResolutionItem> Resolutions { get; } = new();
    public ObservableCollection<string> PlanNames { get; } = new();
    /// <summary>Complete in-app timeline. Filtering only changes <see cref="VisibleLog"/>; entries are never
    /// removed from this collection or from the persisted engine log.</summary>
    public ObservableCollection<string> Log { get; } = new();
    public ICollectionView VisibleLog { get; }
    public IReadOnlyList<string> LogFilterOptions { get; } = ["All entries", "Warnings", "Errors"];

    /// <summary>Pre-flight readiness rows (one per game) from the last "Check readiness" run. Each is a fixed
    /// snapshot of the game's checks — bound directly (no wrapper VM needed).</summary>
    public ObservableCollection<GpuSuite.Engine.Diagnostics.GameReadiness> Readiness { get; } = new();
    public ObservableCollection<PreflightGroup> PreflightGroups { get; } = new();

    [ObservableProperty] private int repeats = 3;
    /// <summary>Keep the run-console repeats at the ≥3 reviewer minimum (a game can still request MORE per-game).</summary>
    partial void OnRepeatsChanged(int value)
    {
        if (value < 3) Repeats = 3;
        UpdateSelection();
    }
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "Idle";
    [ObservableProperty] private string selectionText = "";
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private string logFilter = "All entries";

    partial void OnLogFilterChanged(string value) => VisibleLog.Refresh();

    /// <summary>Planning estimate for the selected measured windows.</summary>
    public string EstimatedDurationText { get; private set; } = "Select games and resolutions";
    /// <summary>Explains what the estimate includes and excludes so capture time is not confused with total wall-clock time.</summary>
    public string EstimatedDurationDetail { get; private set; } = "The estimate appears after a game and resolution are selected.";

    [ObservableProperty] private bool hasProgress;
    [ObservableProperty] private double progressPercent;
    [ObservableProperty] private string progressHeadline = "Waiting to start";
    [ObservableProperty] private string progressDetail = "";
    [ObservableProperty] private string etaText = "";

    /// <summary>Running the read-only pre-flight checks (disables the button + guards Start).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckReadinessCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartQualificationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SmokeTestCommand))]
    private bool isChecking;

    [ObservableProperty] private string preflightButtonText = "Run full pre-flight";

    /// <summary>One-line roll-up of the last readiness pass (e.g. "10 ready · 2 warning(s) · 1 blocked").</summary>
    [ObservableProperty] private string readinessSummary = "";
    /// <summary>Worst verdict across the roster — colours the readiness banner (Ok/Warn/Blocker).</summary>
    [ObservableProperty] private GpuSuite.Engine.Diagnostics.CheckStatus readinessVerdict;
    [ObservableProperty] private bool hasReadiness;
    [ObservableProperty] private string lastCheckedText = "";
    [ObservableProperty] private GpuSuite.Engine.Diagnostics.CheckStatus machineVerdict;
    [ObservableProperty] private string machineSummary = "Not checked";
    [ObservableProperty] private GpuSuite.Engine.Diagnostics.CheckStatus benchVerdict;
    [ObservableProperty] private string benchSummary = "Not checked";
    [ObservableProperty] private GpuSuite.Engine.Diagnostics.CheckStatus gamesVerdict;
    [ObservableProperty] private string gamesSummary = "Not checked";

    public bool HasBlockingPreflight => HasReadiness && ReadinessVerdict == GpuSuite.Engine.Diagnostics.CheckStatus.Blocker;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool allowBlockedStart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoadPlanCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePlanCommand))]
    private string planName = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartQualificationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SmokeTestCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyCanExecuteChangedFor(nameof(CheckReadinessCommand))]
    private bool isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReportCommand))]
    private string reportPath = "";

    public RunViewModel(Workspace ws, ProfileService profiles, DiscoveryService discovery, RunService run,
        RosterSmokeService smoke, FullPreflightService preflight)
    {
        _ws = ws;
        _profiles = profiles;
        _discovery = discovery;
        _run = run;
        _smoke = smoke;
        _preflight = preflight;
        VisibleLog = CollectionViewSource.GetDefaultView(Log);
        VisibleLog.Filter = MatchesLogFilter;
    }

    private bool MatchesLogFilter(object item)
    {
        if (item is not string line) return false;
        return LogFilter switch
        {
            "Warnings" => line.Contains("[Warn", StringComparison.OrdinalIgnoreCase) ||
                          line.Contains(" WARNING", StringComparison.OrdinalIgnoreCase),
            "Errors" => line.Contains("[Error", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    [RelayCommand]
    private void CopyAllLog()
    {
        if (Log.Count == 0)
        {
            StatusText = "Engine log is empty.";
            return;
        }

        try
        {
            Clipboard.SetDataObject(string.Join(Environment.NewLine, Log), copy: true);
            StatusText = $"Copied all {Log.Count} engine-log entries to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText = "Could not copy the engine log: " + ex.Message;
        }
    }

    private string PlansPath => Path.Combine(_ws.Root, RunPlanFile.DefaultFileName);

    public async Task EnsureLoadedAsync()
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Repeats = Math.Max(3, _ws.Config.RepeatsPerScene);

            var profiles = await Task.Run(() => _profiles.LoadAll());
            var catalog = await _discovery.DiscoverAsync();
            var plan = _discovery.Resolve(profiles, catalog);

            Games.Clear();
            foreach (var e in plan.Entries.Where(e => e.Runnable))
                Games.Add(new RunGameItem(e.Game, selected: true, UpdateSelection));

            Resolutions.Clear();
            foreach (var rn in _ws.Config.Resolutions)
                Resolutions.Add(new RunResolutionItem(rn, selected: true, UpdateSelection));

            RefreshPlanNames();
            UpdateSelection();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshPlanNames()
    {
        PlanNames.Clear();
        foreach (var p in RunPlanFile.Load(PlansPath).Plans)
            PlanNames.Add(p.Name);
    }

    private void UpdateSelection()
    {
        int g = Games.Count(x => x.Selected);
        int r = Resolutions.Count(x => x.Selected);
        int models = Games.Where(x => x.Selected && x.HasVariants).Sum(x => x.Variants.Count(v => v.Selected));
        SelectionText = $"{g} game(s) × {r} resolution(s)" + (models > 0 ? $" · {models} model pick(s)" : "");
        UpdateDurationEstimate();
        StartCommand.NotifyCanExecuteChanged();
        StartQualificationCommand.NotifyCanExecuteChanged();
        SmokeTestCommand.NotifyCanExecuteChanged();
        SavePlanCommand.NotifyCanExecuteChanged();
    }

    private void UpdateDurationEstimate()
    {
        var games = Games.Where(x => x.Selected).ToList();
        var resolutions = Resolutions.Where(x => x.Selected).ToList();
        if (games.Count == 0 || resolutions.Count == 0)
        {
            EstimatedDurationText = "Select games and resolutions";
            EstimatedDurationDetail = "The estimate appears after a game and resolution are selected.";
            OnPropertyChanged(nameof(EstimatedDurationText));
            OnPropertyChanged(nameof(EstimatedDurationDetail));
            return;
        }

        double measuredSeconds = 0;
        double budgetSeconds = 0;
        int runs = 0;
        foreach (var item in games)
        {
            int variants = item.HasVariants
                ? item.Variants.Count(v => v.Selected)
                : 1;
            // An all-cleared variant list means “use the profile defaults” at run time.
            if (variants == 0) variants = Math.Max(1, item.Game.Variants.Count(v => v.Enabled));

            int repeats = Math.Max(3, Math.Max(Repeats, item.Game.Repeats));
            int supportedResolutionCount = item.Game.SupportedResolutions.Count == 0
                ? resolutions.Count
                : resolutions.Count(r => item.Game.SupportedResolutions.Contains(r.Name, StringComparer.OrdinalIgnoreCase));
            if (supportedResolutionCount == 0) continue;
            IEnumerable<SceneProfile> scenes = item.Game.Scenes.Count > 0
                ? item.Game.Scenes
                : new[] { new SceneProfile { CaptureSeconds = 30 } };
            foreach (var scene in scenes)
            {
                int repetitions = repeats * variants * supportedResolutionCount;
                measuredSeconds += EstimateMeasuredWindowSeconds(scene) * repetitions;
                budgetSeconds += EstimateRunBudgetSeconds(scene) * repetitions;
                runs += repetitions;
            }
        }

        EstimatedDurationText = $"Measured: {FormatDuration(measuredSeconds)}";
        EstimatedDurationDetail = $"{runs:N0} planned run(s) · configured scene budgets up to {FormatDuration(budgetSeconds)}. Marker-driven bots use their MarkStart→MarkEnd route (TLOU: 35s); their longer scene limit is a safety deadline, not reported GPU time.";
        OnPropertyChanged(nameof(EstimatedDurationText));
        OnPropertyChanged(nameof(EstimatedDurationDetail));
    }

    private static double EstimateMeasuredWindowSeconds(SceneProfile scene)
    {
        foreach (var botId in new[] { scene.BotScript, scene.StartBotScript, scene.ReRunBotScript }
                     .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var script = BotScriptLibrary.Resolve(botId);
            if (script is null) continue;
            int start = script.Actions.FindIndex(a => a.Type == BotActionType.MarkStart);
            int end = start < 0 ? -1 : script.Actions.FindIndex(start + 1, a => a.Type == BotActionType.MarkEnd);
            if (start < 0 || end <= start) continue;
            double seconds = script.Actions.Skip(start + 1).Take(end - start - 1)
                .Sum(a => Math.Max(0, a.DurationMs) / 1000.0);
            if (seconds > 0) return seconds;
        }
        return Math.Max(1, scene.CaptureSeconds);
    }

    private static double EstimateRunBudgetSeconds(SceneProfile scene)
    {
        double warmup = Math.Max(0, scene.WarmupSeconds);
        if (scene.Warmup is { Enabled: true } configured) warmup += Math.Max(0, configured.DurationSeconds);
        return Math.Max(1, scene.CaptureSeconds) + warmup;
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds:00}s";
        return $"{Math.Max(1, span.Seconds)}s";
    }

    // ---- Selection presets ----

    [RelayCommand]
    private void ApplyQuickPreset()
    {
        ApplyPreset(selectAllVariants: false, repeats: 3, highestResolutionOnly: true);
        StatusText = "Quick preset: all runnable games, highest resolution, profile-default models, 3 repeats.";
    }

    [RelayCommand]
    private void ApplyStandardPreset()
    {
        ApplyPreset(selectAllVariants: false, repeats: 3, highestResolutionOnly: false);
        StatusText = "Standard preset: all runnable games and resolutions, profile-default models, 3 repeats.";
    }

    [RelayCommand]
    private void ApplyFullPreset()
    {
        ApplyPreset(selectAllVariants: true, repeats: 5, highestResolutionOnly: false);
        StatusText = "Full preset: all runnable games, resolutions, and models, 5 repeats.";
    }

    private void ApplyPreset(bool selectAllVariants, int repeats, bool highestResolutionOnly)
    {
        foreach (var game in Games)
        {
            game.Selected = true;
            if (selectAllVariants)
                foreach (var variant in game.Variants) variant.Selected = true;
            else
                game.ApplyVariantSelection(null);
        }

        var preferred = Resolutions.FirstOrDefault(r => string.Equals(r.Name, "4K", StringComparison.OrdinalIgnoreCase))
                        ?? Resolutions.LastOrDefault();
        foreach (var resolution in Resolutions)
            resolution.Selected = !highestResolutionOnly || ReferenceEquals(resolution, preferred);
        Repeats = repeats;
        UpdateSelection();
    }

    // ---- Run plans (saved setting groups) ----

    private bool CanSavePlan() => !string.IsNullOrWhiteSpace(PlanName) && Games.Any(x => x.Selected);

    /// <summary>Save the CURRENT selection (games + ticked variants + resolution subset) as a named plan.</summary>
    [RelayCommand(CanExecute = nameof(CanSavePlan))]
    private void SavePlan()
    {
        var file = RunPlanFile.Load(PlansPath);
        var plan = new RunPlan { Name = PlanName.Trim() };
        foreach (var g in Games.Where(x => x.Selected))
            plan.Games[g.Id] = g.SelectedVariantIds();
        plan.Resolutions = Resolutions.Where(r => r.Selected).Select(r => r.Name).ToList();
        file.Upsert(plan);
        file.Save(PlansPath);
        RefreshPlanNames();
        PlanName = plan.Name;
        StatusText = $"Plan '{plan.Name}' saved ({plan.Games.Count} game(s), {plan.Resolutions.Count} resolution(s)).";
    }

    private bool CanLoadPlan() => RunPlanFile.Load(PlansPath).Find(PlanName ?? "") is not null;

    /// <summary>Apply a saved plan to the checkboxes: its games + variant picks + resolution subset.</summary>
    [RelayCommand(CanExecute = nameof(CanLoadPlan))]
    private void LoadPlan()
    {
        var plan = RunPlanFile.Load(PlansPath).Find(PlanName ?? "");
        if (plan is null) return;
        foreach (var g in Games)
        {
            var entry = plan.Games.FirstOrDefault(kv => string.Equals(kv.Key, g.Id, StringComparison.OrdinalIgnoreCase));
            bool inPlan = entry.Key is not null;
            g.Selected = inPlan;
            g.ApplyVariantSelection(inPlan ? entry.Value : null);
        }
        foreach (var r in Resolutions)
            r.Selected = plan.Resolutions.Count == 0 || plan.Resolutions.Contains(r.Name, StringComparer.OrdinalIgnoreCase);
        UpdateSelection();
        StatusText = $"Plan '{plan.Name}' loaded.";
    }

    [RelayCommand(CanExecute = nameof(CanLoadPlan))]
    private void DeletePlan()
    {
        var file = RunPlanFile.Load(PlansPath);
        if (!file.Remove(PlanName ?? "")) return;
        file.Save(PlansPath);
        RefreshPlanNames();
        StatusText = $"Plan '{PlanName}' deleted.";
    }

    // ---- Sweep ----

    private bool CanStart() => !IsRunning && !IsChecking && Games.Any(x => x.Selected) && Resolutions.Any(x => x.Selected)
                               && (!HasBlockingPreflight || AllowBlockedStart);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
        => await StartSelectedAsync(exactRepeatsOverride: null);

    /// <summary>Fast real-game qualification: all runnable games, highest resolution and profile-default
    /// model selection, exactly one repeat. This is diagnostic only and does not replace publishable ≥3-repeat data.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartQualificationAsync()
    {
        ApplyPreset(selectAllVariants: false, repeats: 3, highestResolutionOnly: true);
        StatusText = "Qualification sweep: one real repeat per scene at the highest resolution.";
        await StartSelectedAsync(exactRepeatsOverride: 1);
    }

    private async Task StartSelectedAsync(int? exactRepeatsOverride)
    {
        var games = Games.Where(x => x.Selected).Select(x => x.Game).ToList();
        var resolutions = Resolutions.Where(x => x.Selected).Select(x => x.Name)
            .Select(Resolution.FromName).Where(x => x is not null).Cast<Resolution>().ToList();
        if (games.Count == 0 || resolutions.Count == 0) return;

        // A long roster can otherwise discover a logout or queued update hours into the campaign. Start always
        // refreshes the whole-machine/whole-roster clearance first; a failed probe fails closed. Warnings stay
        // visible but do not fabricate a blocker, while a known blocker requires the existing explicit override.
        bool overrideRequested = AllowBlockedStart;
        bool preflightCompleted = await RunReadinessAsync(resetOverride: false);
        if (!preflightCompleted)
        {
            StatusText = "Start cancelled: full pre-flight did not complete.";
            return;
        }
        if (HasBlockingPreflight && !overrideRequested)
        {
            StatusText = "Start blocked: review the full pre-flight failures below.";
            return;
        }

        // Per-game variant picks for THIS run (exactly the ticked boxes). A game whose ticks equal its
        // profile Enabled defaults still gets an explicit entry — what you see is what runs. Zero ticks
        // on a variant game ⇒ no entry ⇒ the engine falls back to its enabled set.
        var variantSelections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in Games.Where(x => x.Selected && x.HasVariants))
        {
            var ids = g.SelectedVariantIds();
            if (ids.Count > 0) variantSelections[g.Id] = ids;
        }

        Log.Clear();
        ReportPath = "";
        Summary = "";
        IsRunning = true;
        HasProgress = true;
        ProgressPercent = 0;
        ProgressHeadline = "Preparing benchmark sweep";
        ProgressDetail = "Probing the machine and initializing measurement providers.";
        EtaText = "ETA calculating…";
        _runStartedUtc = DateTime.UtcNow;
        _lastProgress = null;
        StatusText = "Running…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var dispatcher = Application.Current.Dispatcher;
        StartLiveTimer(dispatcher);
        PhysicalInputGuard? inputGuard = null;

        void OnLog(TimelineEntry e)
        {
            var line = $"{e.Time:HH:mm:ss} [{e.Stage}] {e.Message}";
            dispatcher.BeginInvoke(() =>
            {
                Log.Add(line);
                if (Log.Count > 5000) Log.RemoveAt(0);
            });
        }

        void OnProgress(BenchmarkProgress progress)
        {
            dispatcher.BeginInvoke(() => UpdateLiveProgress(progress));
        }

        try
        {
            if (!_ws.Config.SimulateLaunch)
            {
                int hangMs = Math.Max(0, _ws.Config.InputHangReleaseSeconds) * 1000;
                int lastResortHangMs = hangMs > 0 ? hangMs * 3 : 0;
                RunHeartbeat.Begin();
                inputGuard = PhysicalInputGuard.Arm(
                    onAbort: () => _cts?.Cancel(),
                    log: message => dispatcher.BeginInvoke(() =>
                    {
                        Log.Add($"{DateTime.Now:HH:mm:ss} [InputLock] {message}");
                        if (Log.Count > 5000) Log.RemoveAt(0);
                    }),
                    isHung: lastResortHangMs > 0 ? () => RunHeartbeat.IsStale(lastResortHangMs) : null);

                if (inputGuard.IsArmed)
                    Log.Add($"{DateTime.Now:HH:mm:ss} [InputLock] ARMED — physical input blocked; ESC aborts, ESC×3 force-unlocks.");
                else
                {
                    inputGuard.Dispose();
                    inputGuard = null;
                    Log.Add($"{DateTime.Now:HH:mm:ss} [InputLock] WARNING — hooks could not be installed; continuing unlocked.");
                }
            }

            var outcome = await Task.Run(() => _run.RunAsync(games, resolutions, Repeats, OnLog, OnProgress, ct,
                variantSelections, exactRepeatsOverride), ct);
            if (outcome.Cancelled)
            {
                StatusText = "Cancelled";
            }
            else
            {
                ReportPath = outcome.ReportPath;
                Summary = outcome.Suite is { } s
                    ? $"Overall index {s.OverallPerformanceIndex:0.0} · {s.Aggregates.Count} aggregate(s)"
                    : "Completed";
                StatusText = "Completed";
                ProgressPercent = 100;
                ProgressHeadline = "Sweep completed";
                ProgressDetail = Summary;
                EtaText = $"Elapsed {FormatDuration((DateTime.UtcNow - _runStartedUtc).TotalSeconds)}";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            StatusText = "Failed: " + ex.Message;
        }
        finally
        {
            RunHeartbeat.End();
            StopLiveTimer();
            if (inputGuard is not null)
            {
                inputGuard.Dispose();
                Log.Add($"{DateTime.Now:HH:mm:ss} [InputLock] RELEASED — physical input restored " +
                        $"(blocked keys={inputGuard.BlockedKeys}, mouse={inputGuard.BlockedMouse}).");
            }
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanSmokeTest() => !IsRunning && !IsChecking && Games.Any(x => x.Selected);

    /// <summary>Launch-only roster proof. It does not drive benchmark bots or collect performance data.</summary>
    [RelayCommand(CanExecute = nameof(CanSmokeTest))]
    private async Task SmokeTestAsync()
    {
        var games = Games.Where(x => x.Selected).Select(x => x.Game).ToList();
        if (games.Count == 0) return;

        if (!await RunReadinessAsync(resetOverride: false)) return;

        Log.Clear();
        ReportPath = "";
        Summary = "";
        IsRunning = true;
        HasProgress = true;
        ProgressPercent = 0;
        ProgressHeadline = "Preparing roster smoke test";
        ProgressDetail = "Each game will be launched, captured, checked, and closed.";
        EtaText = "Launch-only diagnostic — no benchmark data is collected";
        StatusText = "Smoke-testing roster…";
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var dispatcher = Application.Current.Dispatcher;

        void OnLog(TimelineEntry e) => dispatcher.BeginInvoke(() =>
        {
            Log.Add($"{e.Time:HH:mm:ss} [{e.Stage}] {e.Message}");
            if (Log.Count > 5000) Log.RemoveAt(0);
        });
        void OnProgress(RosterSmokeProgress p) => dispatcher.BeginInvoke(() =>
        {
            ProgressPercent = 100.0 * (p.Index - 1) / Math.Max(1, p.Total);
            ProgressHeadline = $"{p.GameName} · {p.Index}/{p.Total}";
            ProgressDetail = p.Phase;
        });

        try
        {
            var outcome = await _smoke.RunAsync(games, OnLog, OnProgress, ct);
            ReportPath = outcome.ReportPath;
            Summary = $"{outcome.Report.ClearCount} clear · {outcome.Report.WarningCount} warning · {outcome.Report.BlockedCount} blocked";
            ProgressPercent = 100;
            ProgressHeadline = "Roster smoke test completed";
            ProgressDetail = Summary;
            StatusText = outcome.Report.BlockedCount > 0 ? "Smoke test found blockers — inspect the saved evidence."
                : outcome.Report.WarningCount > 0 ? "Smoke test completed with warnings."
                : "Roster smoke test clear.";
        }
        catch (OperationCanceledException) { StatusText = "Smoke test cancelled"; }
        catch (Exception ex) { StatusText = "Smoke test failed: " + ex.Message; }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void UpdateLiveProgress(BenchmarkProgress progress)
    {
        _lastProgress = progress;
        int completedCells = progress.CellCompleted ? progress.CellIndex : progress.CellIndex - 1;
        ProgressPercent = Math.Clamp(100.0 * completedCells / Math.Max(1, progress.TotalCells), 0, 100);
        string model = string.Equals(progress.VariantId, "default", StringComparison.OrdinalIgnoreCase)
            ? "default" : progress.VariantId;
        string repeat = progress.RepeatIndex <= 0 ? progress.Phase : $"repeat {progress.RepeatIndex}/{progress.RepeatTarget}";
        ProgressHeadline = $"{progress.GameName} · {progress.SceneName}";
        ProgressDetail = $"{progress.ResolutionName} · {model} · {repeat} · cell {progress.CellIndex}/{progress.TotalCells}";

        RefreshLiveTiming();
    }

    private void StartLiveTimer(Dispatcher dispatcher)
    {
        StopLiveTimer();
        _liveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _liveTimer.Tick += OnLiveTimerTick;
        _liveTimer.Start();
    }

    private void StopLiveTimer()
    {
        if (_liveTimer is null) return;
        _liveTimer.Tick -= OnLiveTimerTick;
        _liveTimer.Stop();
        _liveTimer = null;
    }

    private void OnLiveTimerTick(object? sender, EventArgs e)
    {
        if (IsRunning) RefreshLiveTiming();
    }

    private void RefreshLiveTiming()
    {
        var elapsed = DateTime.UtcNow - _runStartedUtc;
        if (_lastProgress is { } progress)
        {
            // A cell contains several repeats. Counting only fully finished cells leaves ETA stuck at
            // "calculating" throughout a one-cell/three-repeat sweep, precisely when the operator
            // needs it most. Treat completed repeats as completed work while retaining cell-based
            // progress-bar semantics above.
            int repeatsPerCell = Math.Max(1, progress.RepeatTarget);
            int completedRepeatsInCell = progress.CellCompleted
                ? repeatsPerCell
                : Math.Clamp(progress.RepeatIndex - 1, 0, repeatsPerCell);
            int completedRuns = Math.Max(0, (progress.CellIndex - 1) * repeatsPerCell + completedRepeatsInCell);
            int totalRuns = Math.Max(1, progress.TotalCells * repeatsPerCell);
            if (completedRuns > 0)
            {
                double remainingSeconds = elapsed.TotalSeconds / completedRuns * (totalRuns - completedRuns);
                EtaText = $"Elapsed {FormatDuration(elapsed.TotalSeconds)} · about {FormatDuration(remainingSeconds)} remaining";
                return;
            }
        }

        EtaText = $"Elapsed {FormatDuration(elapsed.TotalSeconds)} · ETA calculating…";
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    private bool HasReport() => !string.IsNullOrEmpty(ReportPath);

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void OpenReport()
    {
        if (!string.IsNullOrEmpty(ReportPath) && File.Exists(ReportPath))
            Process.Start(new ProcessStartInfo(ReportPath) { UseShellExecute = true });
    }

    // ---- Pre-flight readiness ----

    private bool CanCheckReadiness() => !IsChecking && !IsRunning;

    /// <summary>
    /// Run the FULL pre-flight over the machine, live measurement bench, and WHOLE benchmark list (not just the
    /// runnable subset — the point is to catch games that are not installed, mid-update, or missing automation).
    /// Launches no games and edits nothing. The probe runs off the UI thread.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCheckReadiness))]
    private async Task CheckReadinessAsync()
        => await RunReadinessAsync(resetOverride: true);

    private async Task<bool> RunReadinessAsync(bool resetOverride)
    {
        IsChecking = true;
        HasReadiness = false;
        HasProgress = true;
        ProgressPercent = 0;
        ProgressHeadline = "Preparing full pre-flight";
        ProgressDetail = "Loading the complete benchmark profile inventory.";
        EtaText = "Starting…";
        PreflightButtonText = "Pre-flight running…";
        DateTime preflightStartedUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(HasBlockingPreflight));
        StatusText = "Running full pre-flight…";
        try
        {
            var progress = new Progress<FullPreflightProgress>(stage =>
            {
                ProgressPercent = Math.Clamp(100.0 * stage.Step / Math.Max(1, stage.TotalSteps), 0, 100);
                ProgressHeadline = stage.Headline;
                ProgressDetail = stage.Detail;
                EtaText = stage.Step >= stage.TotalSteps
                    ? "Finalizing…"
                    : $"Step {Math.Min(stage.Step + 1, stage.TotalSteps)} of {stage.TotalSteps}";
                StatusText = stage.Headline + "…";
            });
            var result = await _preflight.RunAsync(progress);
            var report = result.Games;

            Readiness.Clear();
            foreach (var g in report.Games) Readiness.Add(g);
            PreflightGroups.Clear();
            foreach (var group in result.Groups) PreflightGroups.Add(group);
            HasReadiness = true;
            LastCheckedText = $"Checked {DateTime.Now:HH:mm:ss}";

            var machine = result.Groups.First(g => g.Id == "machine");
            var bench = result.Groups.First(g => g.Id == "bench");
            MachineVerdict = machine.Overall;
            MachineSummary = machine.Summary;
            BenchVerdict = bench.Overall;
            BenchSummary = bench.Summary;
            GamesVerdict = report.BlockerCount > 0 ? GpuSuite.Engine.Diagnostics.CheckStatus.Blocker
                         : report.WarnCount > 0 ? GpuSuite.Engine.Diagnostics.CheckStatus.Warn
                         : GpuSuite.Engine.Diagnostics.CheckStatus.Ok;
            GamesSummary = $"{report.ReadyCount} ready · {report.WarnCount} warning(s) · {report.BlockerCount} blocked";

            ReadinessSummary = $"{result.WarningCount} warning(s) · {result.BlockerCount} blocked"
                               + (report.SkippedCount > 0 ? $" · {report.SkippedCount} disabled" : "");
            ReadinessVerdict = result.Overall;
            ProgressPercent = 100;
            ProgressHeadline = result.BlockerCount > 0 ? "Full pre-flight found blockers"
                : result.WarningCount > 0 ? "Full pre-flight completed with warnings"
                : "Full pre-flight clear";
            ProgressDetail = ReadinessSummary;
            EtaText = $"Completed in {FormatDuration((DateTime.UtcNow - preflightStartedUtc).TotalSeconds)}";
            if (resetOverride) AllowBlockedStart = false;
            OnPropertyChanged(nameof(HasBlockingPreflight));
            StartCommand.NotifyCanExecuteChanged();
            StatusText = result.BlockerCount > 0
                ? $"Pre-flight blocked by {result.BlockerCount} item(s)."
                : result.WarningCount > 0
                    ? $"Ready with {result.WarningCount} warning(s) to review."
                    : "Full pre-flight clear — machine, bench, and games are ready.";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "Readiness check failed: " + ex.Message;
            ProgressHeadline = "Full pre-flight failed";
            ProgressDetail = ex.Message;
            EtaText = $"Stopped after {FormatDuration((DateTime.UtcNow - preflightStartedUtc).TotalSeconds)}";
            return false;
        }
        finally
        {
            IsChecking = false;
            PreflightButtonText = "Run full pre-flight";
        }
    }
}
