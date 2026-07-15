using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>UI-facing state of a login attempt. Port of V2rayNG auth/AuthManager.LoginState.</summary>
public abstract record LoginState
{
    public sealed record Idle : LoginState;

    /// <summary>Deep link is ready; the UI should open Telegram with it.</summary>
    public sealed record AwaitingTelegram(string DeepLink) : LoginState;

    /// <summary>Polling the backend for confirmation. Carries the deep link so the UI can reopen it.</summary>
    public sealed record Polling(string DeepLink) : LoginState;

    /// <summary>A site email/password or 2FA request is in flight.</summary>
    public sealed record SiteLoading : LoginState;

    /// <summary>Confirmed — session persisted; carries the profile.</summary>
    public sealed record Success(UserProfileDto Profile) : LoginState;

    public sealed record Error(ApiError ErrorValue) : LoginState;
}

/// <summary>
/// Orchestrates the auth flows only (Telegram deep-link login, site email/password, TOTP 2FA). There
/// is NO refresh/logout here — the JWT is 7-day and non-refreshable; session persistence is delegated
/// to <see cref="AccountSession"/>/<see cref="AuthTokenStore"/>. Port of V2rayNG auth/AuthManager.kt.
/// </summary>
public sealed class AuthManager
{
    private readonly IDepartamentApiClient _api;

    public AuthManager(IDepartamentApiClient? api = null)
    {
        _api = api ?? new DepartamentApiClient();
    }

    public bool IsLoggedIn() => AuthTokenStore.IsLoggedIn();

    /// <summary>
    /// Telegram login: create a login token, emit <see cref="LoginState.AwaitingTelegram"/> (so the UI
    /// opens the deep link), then poll every ~2s (capped at ~3 min) until the user confirms in Telegram.
    /// On confirmation the session is persisted before <see cref="LoginState.Success"/>.
    /// </summary>
    public async Task BeginTelegramLogin(Action<LoginState> emit, CancellationToken cancellationToken)
    {
        if (!BackendConfig.IsConfigured())
        {
            emit(new LoginState.Error(new ApiError.NotConfiguredError()));
            return;
        }

        TelegramTokenDto tokenDto;
        try
        {
            tokenDto = await _api.CreateTelegramLoginToken();
        }
        catch (ApiError e)
        {
            emit(new LoginState.Error(e));
            return;
        }
        if (tokenDto.Token.IsNullOrEmpty())
        {
            emit(new LoginState.Error(new ApiError.Parse()));
            return;
        }

        var deepLink = $"https://t.me/{BackendConfig.BotUsername}?start=auth_{tokenDto.Token}";
        emit(new LoginState.AwaitingTelegram(deepLink));
        emit(new LoginState.Polling(deepLink));

        var pollInterval = TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow.AddMinutes(3);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await Task.Delay(pollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TelegramCheckResult result;
            try
            {
                result = await _api.CheckTelegramLogin(tokenDto.Token);
            }
            catch (ApiError e)
            {
                emit(new LoginState.Error(e));
                return;
            }

            switch (result)
            {
                case TelegramCheckResult.Confirmed confirmed:
                    AccountSession.OnAuthenticated(confirmed.Token, confirmed.Client);
                    emit(new LoginState.Success(confirmed.Client));
                    return;
                case TelegramCheckResult.Expired:
                    emit(new LoginState.Error(new ApiError.GoneError()));
                    return;
                default:
                    // NotYet — keep polling
                    break;
            }
        }

        emit(new LoginState.Error(new ApiError.TimeoutError()));
    }

    /// <summary>
    /// Site login with email/password. On <see cref="LoginResult.Success"/> the session is persisted;
    /// on <see cref="LoginResult.Requires2Fa"/> the caller must follow up with <see cref="Submit2Fa"/>.
    /// Throws <see cref="ApiError"/> on failure.
    /// </summary>
    public async Task<LoginResult> LoginSite(string email, string password)
    {
        if (!BackendConfig.IsConfigured())
        {
            throw new ApiError.NotConfiguredError();
        }
        var result = await _api.Login(email, password);
        if (result is LoginResult.Success success)
        {
            AccountSession.OnAuthenticated(success.Token, success.Client);
        }
        return result;
    }

    /// <summary>Completes a 2FA login; persists the session and returns the profile. Throws ApiError.</summary>
    public async Task<UserProfileDto> Submit2Fa(string tempToken, string code)
    {
        if (!BackendConfig.IsConfigured())
        {
            throw new ApiError.NotConfiguredError();
        }
        var auth = await _api.Login2Fa(tempToken, code);
        AccountSession.OnAuthenticated(auth.Token, auth.Client);
        return auth.Client;
    }
}
