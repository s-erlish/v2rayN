using System.Text.Json.Serialization;

namespace v2rayN.Desktop.Account.Dto;

// Auth endpoints of the Departament backend (JWT, 7-day, NO refresh).
//
//  POST /client/auth/telegram-login-token  -> TelegramTokenDto
//  GET  /client/auth/telegram-login-check  -> 404 NotYet / 200 Confirmed / 410 Expired (TelegramCheckResult)
//  POST /client/auth/login                 -> LoginResult (Success | Requires2FA)
//  POST /client/auth/2fa-login             -> AuthResult
//  POST /client/auth/google                -> AuthResult
//  GET  /client/auth/me                    -> UserProfileDto
//
// Ported 1:1 from V2rayNG auth/dto/AuthDtos.kt. Gson @SerializedName(alternate=[...]) is preserved
// via extra set-only properties that funnel each alternate spelling into the canonical field.

#region request bodies

public sealed class LoginRequestDto
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";

    public LoginRequestDto()
    {
    }

    public LoginRequestDto(string email, string password)
    {
        Email = email;
        Password = password;
    }
}

public sealed class TwoFaLoginRequestDto
{
    public string TempToken { get; set; } = "";
    public string Code { get; set; } = "";

    public TwoFaLoginRequestDto()
    {
    }

    public TwoFaLoginRequestDto(string tempToken, string code)
    {
        TempToken = tempToken;
        Code = code;
    }
}

public sealed class GoogleLoginRequestDto
{
    public string IdToken { get; set; } = "";
    public string? ReferralCode { get; set; }

    public GoogleLoginRequestDto()
    {
    }

    public GoogleLoginRequestDto(string idToken, string? referralCode = null)
    {
        IdToken = idToken;
        ReferralCode = referralCode;
    }
}

#endregion request bodies

#region raw responses (parsed then mapped to the result types)

/// <summary>POST /client/auth/telegram-login-token</summary>
public sealed class TelegramTokenDto
{
    public string Token { get; set; } = "";
}

/// <summary>Raw 200 body of GET /client/auth/telegram-login-check.</summary>
public sealed class TelegramCheckResponseDto
{
    public bool Confirmed { get; set; }
    public string? Token { get; set; }
    public UserProfileDto? Client { get; set; }
    public bool JustCreated { get; set; }
}

/// <summary>Raw body of POST /client/auth/login (either shape).</summary>
public sealed class LoginResponseDto
{
    public string? Token { get; set; }
    public UserProfileDto? Client { get; set; }

    [JsonPropertyName("requires2FA")]
    public bool Requires2Fa { get; set; }

    public string? TempToken { get; set; }
}

#endregion raw responses

#region result types consumed by the UI/session layer

/// <summary>Outcome of polling GET /client/auth/telegram-login-check.</summary>
public abstract record TelegramCheckResult
{
    /// <summary>404 — not confirmed yet, keep polling.</summary>
    public sealed record NotYet : TelegramCheckResult;

    /// <summary>410 — the login token expired.</summary>
    public sealed record Expired : TelegramCheckResult;

    /// <summary>200 — the user confirmed in Telegram; session is ready.</summary>
    public sealed record Confirmed(string Token, UserProfileDto Client, bool JustCreated) : TelegramCheckResult;
}

/// <summary>Outcome of POST /client/auth/login.</summary>
public abstract record LoginResult
{
    /// <summary>Password accepted, session issued.</summary>
    public sealed record Success(string Token, UserProfileDto Client) : LoginResult;

    /// <summary>Password accepted but a TOTP code is required; call 2fa-login with TempToken.</summary>
    public sealed record Requires2Fa(string TempToken) : LoginResult;
}

/// <summary>A successful authentication carrying the JWT and profile.</summary>
public sealed class AuthResult
{
    public string Token { get; set; } = "";
    public UserProfileDto Client { get; set; } = new();
}

#endregion result types

/// <summary>The authenticated user's profile (GET /client/auth/me and embedded in auth responses).</summary>
public sealed class UserProfileDto
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public double Balance { get; set; }

    // The backend exposes the profile currency as `preferredCurrency`; accept `currency` too so
    // either spelling maps. Stays blank when absent (then defaults to the ruble sign).
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "";

    [JsonPropertyName("preferredCurrency")]
    public string PreferredCurrency
    {
        set
        {
            if (Currency.IsNullOrEmpty() && value.IsNotEmpty())
            {
                Currency = value;
            }
        }
    }

    public bool TelegramLinked { get; set; }
    public long? TelegramId { get; set; }
    public string? TelegramUsername { get; set; }

    // Telegram display / first name, when the backend exposes it. Preferred for the primary account
    // line; the key name varies across backends so accept the common spellings.
    [JsonPropertyName("telegramName")]
    public string? TelegramName { get; set; }

    [JsonPropertyName("telegramFirstName")]
    public string? TelegramNameAlt1 { set => SetTelegramName(value); }

    [JsonPropertyName("firstName")]
    public string? TelegramNameAlt2 { set => SetTelegramName(value); }

    [JsonPropertyName("first_name")]
    public string? TelegramNameAlt3 { set => SetTelegramName(value); }

    [JsonPropertyName("name")]
    public string? TelegramNameAlt4 { set => SetTelegramName(value); }

    [JsonPropertyName("displayName")]
    public string? TelegramNameAlt5 { set => SetTelegramName(value); }

    [JsonPropertyName("tgName")]
    public string? TelegramNameAlt6 { set => SetTelegramName(value); }

    public string ReferralCode { get; set; } = "";
    public string RemnawaveUuid { get; set; } = "";
    public bool TrialUsed { get; set; }
    public bool AutoRenewEnabled { get; set; }
    public bool TotpEnabled { get; set; }

    // Telegram profile photo, if the backend exposes one. Key name varies across backends.
    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("photoUrl")]
    public string? AvatarUrlAlt1 { set => SetAvatarUrl(value); }

    [JsonPropertyName("photo_url")]
    public string? AvatarUrlAlt2 { set => SetAvatarUrl(value); }

    [JsonPropertyName("telegramPhotoUrl")]
    public string? AvatarUrlAlt3 { set => SetAvatarUrl(value); }

    [JsonPropertyName("tgPhotoUrl")]
    public string? AvatarUrlAlt4 { set => SetAvatarUrl(value); }

    [JsonPropertyName("telegramAvatarUrl")]
    public string? AvatarUrlAlt5 { set => SetAvatarUrl(value); }

    [JsonPropertyName("avatar")]
    public string? AvatarUrlAlt6 { set => SetAvatarUrl(value); }

    [JsonPropertyName("photo")]
    public string? AvatarUrlAlt7 { set => SetAvatarUrl(value); }

    private void SetTelegramName(string? value)
    {
        if (TelegramName.IsNullOrEmpty() && value.IsNotEmpty())
        {
            TelegramName = value;
        }
    }

    private void SetAvatarUrl(string? value)
    {
        if (AvatarUrl.IsNullOrEmpty() && value.IsNotEmpty())
        {
            AvatarUrl = value;
        }
    }
}
