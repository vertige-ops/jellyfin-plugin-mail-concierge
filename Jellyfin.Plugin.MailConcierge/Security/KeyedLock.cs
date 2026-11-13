using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MailConcierge.Security;

/// <summary>
/// An asynchronous lock per key: callers using the same key run one at a time, callers using different keys never wait for each other.
/// An entry exists only while a caller holds or awaits its key.
/// </summary>
/// <typeparam name="TKey">The key type.</typeparam>
public sealed class KeyedLock<TKey>
    where TKey : notnull
{
    private readonly object _sync = new();
    private readonly Dictionary<TKey, Entry> _entries = new();

    /// <summary>
    /// Gets the number of keys currently held or awaited.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits until the key is free and takes it.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A handle that releases the key when disposed.</returns>
    public async Task<IDisposable> AcquireAsync(TKey key, CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Leave(key, entry, release: false);
            throw;
        }

        return new Handle(this, key, entry);
    }

    private void Leave(TKey key, Entry entry, bool release)
    {
        lock (_sync)
        {
            if (release)
            {
                entry.Semaphore.Release();
            }

            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }
    }

    private sealed class Handle : IDisposable
    {
        private readonly KeyedLock<TKey> _owner;
        private readonly TKey _key;
        private readonly Entry _entry;
        private int _disposed;

        public Handle(KeyedLock<TKey> owner, TKey key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Leave(_key, _entry, release: true);
            }
        }
    }
}
