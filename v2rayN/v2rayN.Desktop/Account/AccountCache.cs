using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>
/// Process-lifetime, in-memory cache for recently fetched account data, so re-entering a screen
/// renders instantly from memory instead of re-hitting the network. Entries expire after a TTL
/// (default 1 hour). Tied to the logged-in session: every read first checks
/// <see cref="AccountSession.IsLoggedIn"/>; logging out drops the whole cache on the next access.
/// Port of V2rayNG auth/AccountCache.kt (monotonic time via Environment.TickCount64).
/// </summary>
public static class AccountCache
{
    /// <summary>Default freshness window: 1 hour.</summary>
    public const long DefaultTtlMs = 3_600_000L;

    private sealed record Entry(object? Value, long TimestampMs);

    private static readonly Dictionary<string, Entry> _entries = new();
    private static readonly object _lock = new();

    public static void Put(string key, object? value)
    {
        lock (_lock)
        {
            _entries[key] = new Entry(value, Environment.TickCount64);
        }
    }

    public static T? Get<T>(string key, long ttlMs = DefaultTtlMs) where T : class
    {
        lock (_lock)
        {
            if (!AccountSession.IsLoggedIn())
            {
                _entries.Clear();
                return null;
            }
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }
            var ageMs = Environment.TickCount64 - entry.TimestampMs;
            if (ageMs < 0 || ageMs > ttlMs)
            {
                _entries.Remove(key);
                return null;
            }
            return entry.Value as T;
        }
    }

    public static void Invalidate(string key)
    {
        lock (_lock)
        {
            _entries.Remove(key);
        }
    }

    public static void InvalidateAll()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    #region typed helpers

    private const string KeyPayments = "payments";

    private static string DevicesKey(string remnawaveUuid) => $"devices:{remnawaveUuid}";

    public static List<DeviceDto>? GetDevices(string remnawaveUuid, long ttlMs = DefaultTtlMs) =>
        Get<List<DeviceDto>>(DevicesKey(remnawaveUuid), ttlMs);

    public static void PutDevices(string remnawaveUuid, List<DeviceDto> devices) =>
        Put(DevicesKey(remnawaveUuid), devices);

    public static void InvalidateDevices(string remnawaveUuid) => Invalidate(DevicesKey(remnawaveUuid));

    public static List<PaymentDto>? GetPayments(long ttlMs = DefaultTtlMs) =>
        Get<List<PaymentDto>>(KeyPayments, ttlMs);

    public static void PutPayments(List<PaymentDto> payments) => Put(KeyPayments, payments);

    #endregion typed helpers
}
