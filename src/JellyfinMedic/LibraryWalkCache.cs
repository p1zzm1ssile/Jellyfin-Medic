namespace JellyfinMedic.Services;

/// <summary>
/// Keeps what a walk through the whole library found for 10 minutes, so opening a tab again doesn't walk a big library
/// again. The actions that change what a walk finds clear it. A walk that found nothing usable (null) isn't kept.
/// </summary>
internal sealed class LibraryWalkCache<T>
{
    internal static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private T? _value;
    private DateTimeOffset _walkedAt;
    private bool _kept;
    private long _clears; // a walk that a clear overtook found what was there before the change, so it isn't kept

    internal LibraryWalkCache(TimeProvider clock) => _clock = clock;

    internal T Get(Func<CancellationToken, T> walk, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        DateTimeOffset now;
        long clears;
        lock (_gate)
        {
            now = _clock.GetUtcNow();
            if (_kept && now - _walkedAt < KeepFor)
            {
                return _value!;
            }

            clears = _clears;
        }

        // Outside the lock: a walk can take minutes, and a request that's waiting couldn't be cancelled.
        T value = walk(ct);
        lock (_gate)
        {
            if (value is not null && clears == _clears)
            {
                _value = value;
                _walkedAt = now;
                _kept = true;
            }
        }

        return value;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _value = default;
            _kept = false;
            _clears++;
        }
    }
}
