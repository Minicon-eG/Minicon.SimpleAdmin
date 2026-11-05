namespace Minicon.SimpleAdmin.WebUI.Services.ScheduledTasks;

/// <summary>
/// One async mutex per key. Entries are reference-counted and removed when the last holder/waiter leaves,
/// so the dictionary does not grow with every task path ever used.
/// </summary>
internal sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int References;
    }

    /// <summary>Number of keys currently held or waited for (tests).</summary>
    internal int Count
    {
        get { lock (_entries) return _entries.Count; }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the key. Returns the releaser, or null on timeout or
    /// cancellation (the caller distinguishes via <see cref="CancellationToken.IsCancellationRequested"/>).
    /// </summary>
    public async Task<IDisposable?> TryAcquireAsync(string key, TimeSpan timeout, CancellationToken ct)
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries[key] = entry = new Entry();
            entry.References++;
        }

        var acquired = false;
        try
        {
            acquired = await entry.Gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // reported as null
        }

        if (!acquired)
        {
            Leave(key, entry, holdsGate: false);
            return null;
        }
        return new Releaser(this, key, entry);
    }

    private void Leave(string key, Entry entry, bool holdsGate)
    {
        if (holdsGate) entry.Gate.Release();
        lock (_entries)
        {
            if (--entry.References == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Releaser(KeyedAsyncLock owner, string key, Entry entry) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Leave(key, entry, holdsGate: true);
        }
    }
}
