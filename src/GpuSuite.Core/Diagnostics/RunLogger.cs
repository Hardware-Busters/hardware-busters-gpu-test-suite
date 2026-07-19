namespace GpuSuite.Core.Diagnostics;

public enum LogLevel { Trace, Info, Warn, Error }

/// <summary>
/// A timeline logger. Writes timestamped, leveled events to a per-run (or per-suite)
/// log file AND mirrors them to an in-memory timeline + an optional console sink.
/// Every orchestrator transition, process launch, capture start/stop and validation
/// decision is recorded here — diagnostics are first-class, not an afterthought.
/// </summary>
public sealed class RunLogger : IDisposable
{
    private readonly StreamWriter? _writer;
    private readonly object _lock = new();
    private readonly bool _echo;
    private readonly LogLevel _minimumFileLevel;
    public string? Path { get; }
    public List<TimelineEntry> Timeline { get; } = new();
    /// <summary>Cap on the in-memory timeline tail (the log FILE keeps everything). Prevents 24h+ accumulation.</summary>
    private const int MaxTimelineEntries = 10_000;
    private const int TimelineTrimBatch = 2_000;

    /// <summary>Raised for every logged entry (after it is recorded), so a live consumer — e.g. the
    /// WPF Run console — can stream the timeline as it happens. Fired outside the write lock; handlers
    /// run on the logging thread and must marshal to their own UI thread.</summary>
    public event Action<TimelineEntry>? EntryLogged;

    public RunLogger(string? filePath, bool echoToConsole = true, LogLevel minimumFileLevel = LogLevel.Trace)
    {
        Path = filePath;
        _echo = echoToConsole;
        _minimumFileLevel = minimumFileLevel;
        if (filePath is not null)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath)!);
            _writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true
            };
        }
    }

    public void Log(LogLevel level, string stage, string message)
    {
        var entry = new TimelineEntry(DateTime.Now, level, stage, message);
        lock (_lock)
        {
            Timeline.Add(entry);
            // Cap the in-memory timeline so a 24h+ unattended run can't accumulate it unbounded. The durable
            // record is the log FILE (every line is written there); this list is only a recent in-memory tail
            // for live consumers. Batch-trim so we drop the oldest chunk occasionally, not on every add.
            if (Timeline.Count > MaxTimelineEntries)
                Timeline.RemoveRange(0, TimelineTrimBatch);
            string line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{stage}] {message}";
            if (level >= _minimumFileLevel) _writer?.WriteLine(line);
            if (_echo)
            {
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = level switch
                {
                    LogLevel.Error => ConsoleColor.Red,
                    LogLevel.Warn => ConsoleColor.Yellow,
                    LogLevel.Trace => ConsoleColor.DarkGray,
                    _ => prev
                };
                Console.WriteLine(line);
                Console.ForegroundColor = prev;
            }
        }
        EntryLogged?.Invoke(entry);   // outside the lock — a handler must not be able to deadlock the writer
    }

    public void Trace(string stage, string m) => Log(LogLevel.Trace, stage, m);
    public void Info(string stage, string m) => Log(LogLevel.Info, stage, m);
    public void Warn(string stage, string m) => Log(LogLevel.Warn, stage, m);
    public void Error(string stage, string m) => Log(LogLevel.Error, stage, m);

    public void Dispose()
    {
        lock (_lock) { _writer?.Flush(); _writer?.Dispose(); }
    }
}

public readonly record struct TimelineEntry(DateTime Time, LogLevel Level, string Stage, string Message);
