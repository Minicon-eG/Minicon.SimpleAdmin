using System.Text.Json;
using Microsoft.Extensions.Logging;
using Minicon.SimpleAdmin.Models.State;

namespace Minicon.SimpleAdmin.Services;

/// <summary>
/// First-seen times of the cross-server scheduled task group problems ("none active" / "several active").
/// The worker cannot track them (it sees only its own server), so every central reader keeps its own small file
/// <see cref="FileName"/> (WebUI: ConfigPath directory next to acknowledges.json; notifier: next to
/// notify-state.json). Per problem ID it stores <see cref="Entry.Since"/> and <see cref="Entry.LastSeen"/>.
/// </summary>
/// <remarks>
/// <para>
/// The start of a problem is carried forward only while its entry was seen within <c>keepResolvedFor</c>
/// (<see cref="ScheduledTaskGroupEvaluator.GetStaleAfter"/>): a short gap — one read where a server timed out —
/// keeps the start, a longer one (also a reader that was not running) starts it anew. Entries not seen within
/// <c>keepResolvedFor</c> are removed.
/// </para>
/// <para>
/// The content is kept in memory; the file is read once and again only when its write time changes (another
/// process, e.g. the overlapping IIS recycle). It is written atomically (unique temp name) and only when the content
/// changes: a new/removed entry or a changed start, or when a persisted <see cref="Entry.LastSeen"/> is older than
/// half of <c>keepResolvedFor</c> — so a restart sees a reasonably fresh value without a write per read.
/// File IO happens outside the in-memory lock. Best effort: read/write errors fall back to first = last = observed
/// time; a write error is logged once as warning, then at debug level.
/// </para>
/// </remarks>
public sealed class ScheduledTaskGroupSinceStore
{
    /// <summary>File name of the store inside the reader's state directory.</summary>
    public const string FileName = "schtask-groups-since.json";

    /// <summary>Persisted state of one group problem.</summary>
    public sealed record Entry(DateTime Since, DateTime LastSeen);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string? _path;
    private readonly ILogger? _logger;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Entry> _persisted = new(StringComparer.OrdinalIgnoreCase);
    private DateTime? _loadedWriteTime;
    private bool _writeErrorLogged;

    /// <param name="directory">Directory of the file; null/empty = in memory only.</param>
    /// <param name="logger">Optional logger.</param>
    public ScheduledTaskGroupSinceStore(string? directory, ILogger? logger = null)
    {
        _path = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, FileName);
        _logger = logger;
    }

    /// <summary>
    /// Maintenance + apply: reloads the file if it changed, carries the start of every current problem forward
    /// (new ones start at their observation time), drops resolved entries after <paramref name="keepResolvedFor"/>,
    /// writes the file when needed and sets <see cref="ActiveProblem.Since"/> / <see cref="ActiveProblem.LastOccurrence"/>.
    /// <paramref name="pendingIds"/> are problems that cannot be decided right now (see
    /// <see cref="ScheduledTaskGroupEvaluator.DerivePendingGroupProblemIds"/>): an existing entry keeps its start and
    /// counts as seen, so the start survives an outage of any length; no entry is created for them.
    /// </summary>
    public void Track(
        IReadOnlyCollection<(ActiveProblem Problem, DateTime ObservedAt)> problems,
        DateTime nowUtc,
        TimeSpan keepResolvedFor,
        IEnumerable<string>? pendingIds = null)
    {
        ReloadIfChanged();

        Dictionary<string, Entry>? toWrite = null;
        lock (_gate)
        {
            var next = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (var (problem, observedAt) in problems)
            {
                if (next.ContainsKey(problem.Id))
                    continue;
                var since = _entries.TryGetValue(problem.Id, out var known)
                            && nowUtc - known.LastSeen <= keepResolvedFor
                            && known.Since <= observedAt
                    ? known.Since
                    : observedAt;
                next[problem.Id] = new Entry(since, nowUtc);
            }

            foreach (var id in pendingIds ?? Enumerable.Empty<string>())
            {
                if (!next.ContainsKey(id) && _entries.TryGetValue(id, out var pending) && nowUtc - pending.LastSeen <= keepResolvedFor)
                    next[id] = pending with { LastSeen = nowUtc };
            }

            // Resolved (not seen now): keep through short gaps so a single failed read does not reset the start.
            foreach (var (id, entry) in _entries)
            {
                if (!next.ContainsKey(id) && nowUtc - entry.LastSeen <= keepResolvedFor)
                    next[id] = entry;
            }

            _entries = next;
            if (NeedsWrite(next, keepResolvedFor, nowUtc))
                toWrite = new Dictionary<string, Entry>(next, StringComparer.OrdinalIgnoreCase);

            ApplyLocked(problems);
        }

        if (toWrite != null)
            Write(toWrite);
    }

    /// <summary>IDs currently tracked (reloads the file first if it changed).</summary>
    public IReadOnlyCollection<string> TrackedIds()
    {
        ReloadIfChanged();
        lock (_gate)
            return _entries.Keys.ToList();
    }

    /// <summary>Read-only apply from memory (no IO, no maintenance); unknown problems start at their observation time.</summary>
    public void Apply(IEnumerable<(ActiveProblem Problem, DateTime ObservedAt)> problems)
    {
        lock (_gate)
            ApplyLocked(problems);
    }

    private void ApplyLocked(IEnumerable<(ActiveProblem Problem, DateTime ObservedAt)> problems)
    {
        foreach (var (problem, observedAt) in problems)
        {
            problem.LastOccurrence = observedAt;
            problem.Since = _entries.TryGetValue(problem.Id, out var e) && e.Since <= observedAt ? e.Since : observedAt;
        }
    }

    private bool NeedsWrite(Dictionary<string, Entry> next, TimeSpan keepResolvedFor, DateTime nowUtc)
    {
        if (_path == null)
            return false;
        if (next.Count != _persisted.Count)
            return true;

        var refreshAfter = TimeSpan.FromTicks(keepResolvedFor.Ticks / 2);
        foreach (var (id, entry) in next)
        {
            if (!_persisted.TryGetValue(id, out var stored) || stored.Since != entry.Since)
                return true;
            if (entry.LastSeen - stored.LastSeen > refreshAfter)
                return true;
        }
        return false;
    }

    private void ReloadIfChanged()
    {
        if (_path == null)
            return;

        try
        {
            var info = new FileInfo(_path);
            if (!info.Exists)
                return;
            var writeTime = info.LastWriteTimeUtc;
            lock (_gate)
            {
                if (_loadedWriteTime == writeTime)
                    return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path), JsonOptions);
            var entries = loaded == null
                ? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Entry>(loaded, StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                _entries = entries;
                _persisted = new Dictionary<string, Entry>(entries, StringComparer.OrdinalIgnoreCase);
                _loadedWriteTime = writeTime;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("Scheduled task group first-seen file {Path} unreadable, keeping in-memory state: {Error}", _path, ex.Message);
        }
    }

    private void Write(Dictionary<string, Entry> content)
    {
        var temp = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path!)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(content, JsonOptions));
            File.Move(temp, _path!, overwrite: true);
            var writeTime = File.GetLastWriteTimeUtc(_path!);
            lock (_gate)
            {
                _persisted = content;
                _loadedWriteTime = writeTime;
            }
        }
        catch (Exception ex)
        {
            if (!_writeErrorLogged)
            {
                _writeErrorLogged = true;
                _logger?.LogWarning("Scheduled task group first-seen file {Path} could not be written (further errors at debug level): {Error}", _path, ex.Message);
            }
            else
            {
                _logger?.LogDebug("Scheduled task group first-seen file {Path} could not be written: {Error}", _path, ex.Message);
            }
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }
}
