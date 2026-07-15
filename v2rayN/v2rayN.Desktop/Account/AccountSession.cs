using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>Logged-in/out account state, observed by the UI. Port of V2rayNG auth/AccountSession.kt.</summary>
public abstract record AccountState
{
    public sealed record LoggedOut : AccountState;

    public sealed record LoggedIn(UserProfileDto Profile) : AccountState;
}

/// <summary>
/// Single source of truth for the logged-in/out account state. Seeded from <see cref="AuthTokenStore"/>
/// on first access so a returning user is already "logged in". Mutations keep the persisted session
/// and the in-memory <see cref="State"/> consistent and raise <see cref="StateChanged"/>.
/// </summary>
public static class AccountSession
{
    private static readonly object _lock = new();
    private static readonly SubscriptionSyncManager _subs = new();
    private static AccountState _state = Seed();

    /// <summary>Raised (on the caller's thread) whenever the account state changes.</summary>
    public static event Action<AccountState>? StateChanged;

    public static AccountState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    private static AccountState Seed() =>
        AuthTokenStore.IsLoggedIn()
            ? new AccountState.LoggedIn(AuthTokenStore.GetUser() ?? new UserProfileDto())
            : new AccountState.LoggedOut();

    public static bool IsLoggedIn() => AuthTokenStore.IsLoggedIn();

    /// <summary>Persist a freshly issued session and flip to LoggedIn.</summary>
    public static void OnAuthenticated(string jwt, UserProfileDto profile)
    {
        AuthTokenStore.SaveSession(jwt, user: profile);
        SetState(new AccountState.LoggedIn(profile));
    }

    /// <summary>Refresh the cached profile (e.g. after GET /client/auth/me).</summary>
    public static void UpdateProfile(UserProfileDto profile)
    {
        AuthTokenStore.SaveUser(profile);
        if (IsLoggedIn())
        {
            SetState(new AccountState.LoggedIn(profile));
        }
    }

    /// <summary>
    /// Clear session + managed subscriptions and flip to LoggedOut. Called ONLY on an explicit user
    /// logout, or when the identity endpoint (getMe) confirms the JWT is dead with a 401. It must never
    /// be triggered by a 403 or by a 401 on any other endpoint.
    /// </summary>
    public static async Task Wipe()
    {
        await _subs.RemoveAllManaged();
        AuthTokenStore.Clear();
        SetState(new AccountState.LoggedOut());
    }

    private static void SetState(AccountState state)
    {
        lock (_lock)
        {
            _state = state;
        }
        StateChanged?.Invoke(state);
    }
}
