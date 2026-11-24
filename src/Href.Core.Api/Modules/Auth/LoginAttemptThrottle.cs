using System.Collections.Concurrent;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// In-memory limit of login attempts per account (the normalized email), across every application and end-user IP, so a
/// password guessing attack spread over many IPs is slowed down too. Each attempt takes one slot of a fixed window of
/// <see cref="Window"/> before the password is checked; a correct password releases the account (<see cref="Reset"/>).
/// Once <see cref="AttemptLimit"/> slots are taken, further attempts are rejected until the window ends, for existing
/// and unknown emails alike. Taking a slot is atomic, so simultaneous requests cannot pass the limit. Single instance only
/// (Railway): several replicas would each keep their own counters. Expired windows are dropped (at most once per
/// <see cref="PruneInterval"/>) while the table holds more than <see cref="PruneThreshold"/> accounts.
/// </summary>
public sealed class LoginAttemptThrottle(TimeProvider timeProvider)
{
    public const int AttemptLimit = 20;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    internal const int PruneThreshold = 10_000;

    internal static readonly TimeSpan PruneInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, AttemptWindow> _windows = new(StringComparer.Ordinal);

    private readonly object _pruneLock = new();

    private DateTimeOffset _nextPruneAt = DateTimeOffset.MinValue;

    internal int TrackedAccounts => _windows.Count;

    /// <summary>
    /// Takes one attempt slot for <paramref name="email"/> (already normalized). Returns <c>null</c> when the attempt may
    /// go on, or the time left until the window ends when the account has no slot left.
    /// </summary>
    public TimeSpan? TryAcquire(string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        var now = timeProvider.GetUtcNow();

        if (_windows.Count > PruneThreshold && now >= NextPruneAt)
        {
            Prune(now);
        }

        while (true)
        {
            var window = _windows.GetOrAdd(email, static (_, start) => new AttemptWindow(start), now);

            lock (window)
            {
                if (window.Removed)
                {
                    continue;
                }

                if (now >= window.Start + Window)
                {
                    window.Start = now;
                    window.Attempts = 0;
                }

                if (window.Attempts >= AttemptLimit)
                {
                    return window.Start + Window - now;
                }

                window.Attempts++;
                return null;
            }
        }
    }

    /// <summary>
    /// Releases the account after a correct password.
    /// </summary>
    public void Reset(string email)
    {
        ArgumentException.ThrowIfNullOrEmpty(email);

        if (_windows.TryRemove(email, out var window))
        {
            lock (window)
            {
                window.Removed = true;
            }
        }
    }

    private DateTimeOffset NextPruneAt
    {
        get
        {
            lock (_pruneLock)
            {
                return _nextPruneAt;
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        lock (_pruneLock)
        {
            if (now < _nextPruneAt)
            {
                return;
            }

            _nextPruneAt = now + PruneInterval;
        }

        foreach (var (email, window) in _windows)
        {
            lock (window)
            {
                if (now >= window.Start + Window && _windows.TryRemove(new KeyValuePair<string, AttemptWindow>(email, window)))
                {
                    window.Removed = true;
                }
            }
        }
    }

    private sealed class AttemptWindow(DateTimeOffset start)
    {
        public DateTimeOffset Start { get; set; } = start;

        public int Attempts { get; set; }

        public bool Removed { get; set; }
    }
}
