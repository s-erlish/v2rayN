using System.Net.Http;
using System.Text.Json;
using v2rayN.Desktop.Account.Dto;
using Endpoints = v2rayN.Desktop.Account.BackendConfig.Endpoints;

namespace v2rayN.Desktop.Account;

/// <summary>
/// HttpClient + System.Text.Json implementation of <see cref="IDepartamentApiClient"/>. Port of
/// V2rayNG auth/DepartamentApiClientImpl.kt (OkHttp + Gson).
///
/// A single <see cref="AuthMessageHandler"/> attaches Accept, User-Agent, the Bearer JWT (from
/// <see cref="AuthTokenStore"/>) and the HWID/device headers. Every failure maps to an
/// <see cref="ApiError"/>; tokens and subscription URLs are never logged. All calls throw
/// <see cref="ApiError.NotConfiguredError"/> when the base URL is blank.
/// </summary>
public sealed class DepartamentApiClient : IDepartamentApiClient
{
    private const string HeaderHwid = "X-HWID";
    private const string HeaderDeviceOs = "x-device-os";
    private const string HeaderVerOs = "x-ver-os";
    private const string HeaderDeviceModel = "x-device-model";

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new AuthMessageHandler(new HttpClientHandler()))
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        return client;
    }

    /// <summary>Injects Accept / User-Agent / Bearer JWT / HWID+device headers on every request.</summary>
    private sealed class AuthMessageHandler : DelegatingHandler
    {
        public AuthMessageHandler(HttpMessageHandler inner) : base(inner)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", BackendConfig.SubscriptionUserAgent);

            var token = AuthTokenStore.GetToken();
            if (token.IsNotEmpty())
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            }

            // Stable per-install HWID + OS/device so the panel keeps ONE device entry per machine.
            request.Headers.TryAddWithoutValidation(HeaderHwid, AuthTokenStore.DeviceId());
            request.Headers.TryAddWithoutValidation(HeaderDeviceOs, "windows");
            request.Headers.TryAddWithoutValidation(HeaderVerOs, Environment.OSVersion.Version.ToString());
            request.Headers.TryAddWithoutValidation(HeaderDeviceModel, Environment.MachineName);

            return await base.SendAsync(request, cancellationToken);
        }
    }

    #region public

    public Task<PublicConfigDto> GetPublicConfig() => GetJson<PublicConfigDto>(Endpoints.PublicConfig);

    public Task<TariffCatalogDto> GetPublicTariffs() => GetJson<TariffCatalogDto>(Endpoints.PublicTariffs);

    public Task<List<ServerStatusDto>> GetServerStatus() => GetJson<List<ServerStatusDto>>(Endpoints.ServerStatus);

    #endregion public

    #region auth

    public Task<TelegramTokenDto> CreateTelegramLoginToken() =>
        PostJson<TelegramTokenDto>(Endpoints.TelegramLoginToken, "{}");

    public async Task<TelegramCheckResult> CheckTelegramLogin(string token)
    {
        EnsureConfigured();
        var url = $"{UrlOf(Endpoints.TelegramLoginCheck)}?token={Uri.EscapeDataString(token)}";
        var resp = await Execute(new HttpRequestMessage(HttpMethod.Get, url));
        using (resp)
        {
            var code = (int)resp.StatusCode;
            if (code == 404)
            {
                return new TelegramCheckResult.NotYet();
            }
            if (code == 410)
            {
                return new TelegramCheckResult.Expired();
            }
            if (code is >= 200 and <= 299)
            {
                var raw = Parse<TelegramCheckResponseDto>(await resp.Content.ReadAsStringAsync());
                var jwt = raw.Token;
                var client = raw.Client;
                if (raw.Confirmed && jwt.IsNotEmpty() && client != null)
                {
                    return new TelegramCheckResult.Confirmed(jwt!, client, raw.JustCreated);
                }
                return new TelegramCheckResult.NotYet();
            }
            throw MapError(code);
        }
    }

    public async Task<LoginResult> Login(string email, string password)
    {
        var raw = await PostJson<LoginResponseDto>(Endpoints.Login, Serialize(new LoginRequestDto(email, password)));
        return MapLoginResponse(raw);
    }

    public Task<AuthResult> Login2Fa(string tempToken, string code) =>
        PostJson<AuthResult>(Endpoints.TwoFaLogin, Serialize(new TwoFaLoginRequestDto(tempToken, code)));

    public Task<AuthResult> LoginGoogle(string idToken, string? referralCode = null) =>
        PostJson<AuthResult>(Endpoints.GoogleLogin, Serialize(new GoogleLoginRequestDto(idToken, referralCode)));

    public Task<UserProfileDto> GetMe() => GetJson<UserProfileDto>(Endpoints.Me);

    public async Task<RegisterResult> Register(string email, string password, string? referralCode = null)
    {
        var raw = await PostJson<RegisterResponseDto>(Endpoints.Register, Serialize(new RegisterRequestDto(email, password, referralCode)));
        if (raw.Token.IsNotEmpty() && raw.Client != null)
        {
            return new RegisterResult.Success(raw.Token!, raw.Client);
        }
        // No token → the backend sent a verification email (requiresVerification).
        return new RegisterResult.RequiresVerification(raw.Message);
    }

    public async Task<LoginResult> VerifyEmail(string token)
    {
        var raw = await PostJson<LoginResponseDto>(Endpoints.VerifyEmail, Serialize(new TokenRequestDto(token)));
        return MapLoginResponse(raw);
    }

    public Task<MessageResponseDto> RequestMagicLink(string email) =>
        PostJson<MessageResponseDto>(Endpoints.MagicLinkRequest, Serialize(new EmailRequestDto(email)));

    public async Task<LoginResult> ConsumeMagicLink(string token, string? referralCode = null)
    {
        var raw = await PostJson<LoginResponseDto>(Endpoints.MagicLinkConsume, Serialize(new MagicLinkConsumeRequestDto(token, referralCode)));
        return MapLoginResponse(raw);
    }

    public Task<MessageResponseDto> RequestPasswordReset(string email) =>
        PostJson<MessageResponseDto>(Endpoints.PasswordResetRequest, Serialize(new EmailRequestDto(email)));

    public Task<MessageResponseDto> ConsumePasswordReset(string token, string newPassword) =>
        PostJson<MessageResponseDto>(Endpoints.PasswordResetConsume, Serialize(new PasswordResetConsumeRequestDto(token, newPassword)));

    public Task<AppHandoffDto> CreateAppHandoff() =>
        PostJson<AppHandoffDto>(Endpoints.AppHandoff, "{}");

    public Task<AuthResult> ConsumeAppHandoff(string code) =>
        PostJson<AuthResult>(Endpoints.AppHandoffConsume, Serialize(new CodeRequestDto(code)));

    /// <summary>Maps a login/verify/magic-link auth body (either {token,client} or {requires2FA,tempToken}).</summary>
    private static LoginResult MapLoginResponse(LoginResponseDto raw)
    {
        var tempToken = raw.TempToken;
        var token = raw.Token;
        var client = raw.Client;
        if (raw.Requires2Fa && tempToken.IsNotEmpty())
        {
            return new LoginResult.Requires2Fa(tempToken!);
        }
        if (token.IsNotEmpty() && client != null)
        {
            return new LoginResult.Success(token!, client);
        }
        throw new ApiError.Parse();
    }

    #endregion auth

    #region account linking

    public Task<LinkTelegramRequestDto> RequestLinkTelegram() =>
        PostJson<LinkTelegramRequestDto>(Endpoints.LinkTelegramRequest, "{}");

    public Task<MessageResponseDto> RequestLinkEmail(string email) =>
        PostJson<MessageResponseDto>(Endpoints.LinkEmailRequest, Serialize(new EmailRequestDto(email)));

    public Task<MessageResponseDto> SetPassword(string newPassword) =>
        PostJson<MessageResponseDto>(Endpoints.SetPassword, Serialize(new SetPasswordRequestDto(newPassword)));

    public Task<UserProfileDto> LinkGoogle(string idToken) =>
        PostJson<UserProfileDto>(Endpoints.LinkGoogle, Serialize(new LinkGoogleRequestDto(idToken)));

    #endregion account linking

    #region subscription

    public Task<PrimarySubscriptionDto> GetPrimarySubscription() =>
        GetJson<PrimarySubscriptionDto>(Endpoints.Subscription);

    public Task<SubscriptionAllDto> GetSubscriptionAll() =>
        GetJson<SubscriptionAllDto>(Endpoints.SubscriptionAll);

    public async Task RenameSubscription(string scope, string id, string name)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Patch, UrlOf(Endpoints.RenameSubscription(scope, id)))
        {
            Content = JsonBody(Serialize(new RenameRequestDto(name))),
        };
        await ExecuteVoid(req);
    }

    public async Task<byte[]> GetSubscriptionQr(string remnawaveUuid)
    {
        EnsureConfigured();
        var url = $"{UrlOf(Endpoints.SubscriptionQr)}?uuid={Uri.EscapeDataString(remnawaveUuid)}";
        var resp = await Execute(new HttpRequestMessage(HttpMethod.Get, url));
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw MapError((int)resp.StatusCode);
            }
            return await resp.Content.ReadAsByteArrayAsync();
        }
    }

    public Task<PaymentInitDto> AddDevices(string scope, string id, int extraDevices, string method, string? paymentMethod = null) =>
        PostJson<PaymentInitDto>(Endpoints.AddDevices(scope, id), Serialize(new AddDevicesRequestDto(extraDevices, method, paymentMethod)));

    public Task<AddDevicesResultDto> PurchaseDevices(string scope, string id, int extraDevices, string method, int? paymentMethod = null) =>
        PostJson<AddDevicesResultDto>(Endpoints.AddDevices(scope, id), Serialize(new AddDevicesPurchaseRequestDto(extraDevices, method, paymentMethod)));

    public async Task<UpgradeQuoteDto> GetUpgradeQuote(string targetTariffId)
    {
        EnsureConfigured();
        var url = $"{UrlOf(Endpoints.UpgradeQuote)}?targetTariffId={Uri.EscapeDataString(targetTariffId)}";
        return await Call<UpgradeQuoteDto>(new HttpRequestMessage(HttpMethod.Get, url));
    }

    public Task<PaymentInitDto> Upgrade(string targetTariffId, string method, string subscriptionUuid, string? paymentMethod = null) =>
        PostJson<PaymentInitDto>(Endpoints.Upgrade, Serialize(new UpgradeRequestDto(targetTariffId, method, paymentMethod, subscriptionUuid)));

    #endregion subscription

    #region devices

    public async Task<DevicesResult> GetDevices(string remnawaveUuid)
    {
        EnsureConfigured();
        var url = $"{UrlOf(Endpoints.Devices)}?uuid={Uri.EscapeDataString(remnawaveUuid)}";
        var resp = await Execute(new HttpRequestMessage(HttpMethod.Get, url));
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw MapError((int)resp.StatusCode, SanitizeBody(body));
            }
            var devices = Parse<DevicesDto>(body).Devices();
            return new DevicesResult(devices, (int)resp.StatusCode, SanitizeBody(body) ?? "");
        }
    }

    public async Task DeleteDevice(string hwid, string remnawaveUuid)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Post, UrlOf(Endpoints.DeleteDevice))
        {
            Content = JsonBody(Serialize(new DeleteDeviceRequestDto(hwid, remnawaveUuid))),
        };
        await ExecuteVoid(req);
    }

    #endregion devices

    #region payments

    public Task<PaymentInitDto> PayPlatega(PaymentRequestDto req) =>
        PostJson<PaymentInitDto>(Endpoints.PayPlatega, Serialize(req));

    public Task<PaymentInitDto> PayTariffPlatega(PaymentRequestDto req) =>
        PostJson<PaymentInitDto>(Endpoints.PayTariffPlatega, Serialize(req));

    public Task<PaymentResultDto> PayBalance(PaymentRequestDto req) =>
        PostJson<PaymentResultDto>(Endpoints.PayBalance, Serialize(req));

    public Task<PaymentsDto> GetPayments() => GetJson<PaymentsDto>(Endpoints.Payments);

    #endregion payments

    #region promo / trial / referral

    public Task<PromoDto> CheckPromo(string code) =>
        PostJson<PromoDto>(Endpoints.PromoCheck, Serialize(new PromoRequestDto(code)));

    public async Task ActivatePromo(string code)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Post, UrlOf(Endpoints.PromoActivate))
        {
            Content = JsonBody(Serialize(new PromoRequestDto(code))),
        };
        await ExecuteVoid(req);
    }

    public async Task ActivateTrial()
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Post, UrlOf(Endpoints.Trial))
        {
            Content = JsonBody("{}"),
        };
        await ExecuteVoid(req);
    }

    public async Task SetSecondaryAutoRenew(string id, bool autoRenew)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Patch, UrlOf(Endpoints.SecondaryAutoRenew(id)))
        {
            Content = JsonBody(Serialize(new AutoRenewRequestDto(autoRenew))),
        };
        await ExecuteVoid(req);
    }

    public async Task SetPrimaryAutoRenew(bool autoRenew)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Patch, UrlOf(Endpoints.PrimaryAutoRenew))
        {
            Content = JsonBody(Serialize(new AutoRenewRequestDto(autoRenew))),
        };
        await ExecuteVoid(req);
    }

    public Task<ReferralStatsDto> GetReferralStats() => GetJson<ReferralStatsDto>(Endpoints.ReferralStats);

    #endregion promo / trial / referral

    #region internals

    private static void EnsureConfigured()
    {
        if (!BackendConfig.IsConfigured())
        {
            throw new ApiError.NotConfiguredError();
        }
    }

    private static string UrlOf(string path) => BackendConfig.BaseUrl + path;

    private static HttpContent JsonBody(string json) => new StringContent(json, Encoding.UTF8, "application/json");

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ApiJson.Options);

    private async Task<T> GetJson<T>(string path)
    {
        EnsureConfigured();
        return await Call<T>(new HttpRequestMessage(HttpMethod.Get, UrlOf(path)));
    }

    private async Task<T> PostJson<T>(string path, string json)
    {
        EnsureConfigured();
        var req = new HttpRequestMessage(HttpMethod.Post, UrlOf(path)) { Content = JsonBody(json) };
        return await Call<T>(req);
    }

    private async Task<T> Call<T>(HttpRequestMessage request)
    {
        var resp = await Execute(request);
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                throw MapError((int)resp.StatusCode, SanitizeBody(body));
            }
            return Parse<T>(body);
        }
    }

    private async Task ExecuteVoid(HttpRequestMessage request)
    {
        var resp = await Execute(request);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                throw MapError((int)resp.StatusCode);
            }
        }
    }

    /// <summary>Executes the call, mapping transport failures to <see cref="ApiError"/>.</summary>
    private static async Task<HttpResponseMessage> Execute(HttpRequestMessage request)
    {
        try
        {
            return await _http.SendAsync(request);
        }
        catch (TaskCanceledException)
        {
            throw new ApiError.TimeoutError();
        }
        catch (OperationCanceledException)
        {
            throw new ApiError.TimeoutError();
        }
        catch (HttpRequestException e)
        {
            throw new ApiError.NetworkError(e);
        }
    }

    private static ApiError MapError(int code, string? detail = null) => code switch
    {
        // Only 401 means "authentication failed / token expired". 403 (Forbidden) is a permission
        // outcome on a valid session and must NOT be treated as Unauthorized (else callers wipe a
        // live session).
        401 => new ApiError.Unauthorized(detail),
        403 => new ApiError.Server(403, detail),
        404 => new ApiError.NotFoundError(),
        410 => new ApiError.GoneError(),
        429 => new ApiError.RateLimited(),
        502 or 503 => new ApiError.ServiceUnavailable(),
        _ => new ApiError.Server(code, detail),
    };

    /// <summary>
    /// Reduces an error body to a short, screenshot-safe snippet: drops any line mentioning a
    /// token/authorization header or an http(s) URL, then caps at 300 chars. Null when nothing remains.
    /// </summary>
    private static string? SanitizeBody(string body)
    {
        if (body.IsNullOrEmpty())
        {
            return null;
        }
        var cleaned = string.Join("\n", body.Split('\n').Where(line =>
        {
            var l = line.ToLowerInvariant();
            return !(l.Contains("token") || l.Contains("authorization") || l.Contains("http://") || l.Contains("https://"));
        })).Trim();
        if (cleaned.IsNullOrEmpty())
        {
            return null;
        }
        return cleaned.Length > 300 ? cleaned.Substring(0, 300) : cleaned;
    }

    private static T Parse<T>(string body)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(body, ApiJson.Options);
            if (value is null)
            {
                throw new ApiError.Parse();
            }
            return value;
        }
        catch (JsonException e)
        {
            throw new ApiError.Parse(e);
        }
    }

    #endregion internals
}
