using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>
/// Wrapper over <see cref="IDepartamentApiClient"/> that returns <see cref="ApiResult{T}"/> instead of
/// throwing, maps every failure to an <see cref="ApiError"/>, and performs the higher-level account
/// operations the UI needs. Port of V2rayNG auth/AccountRepository.kt.
///
/// The local session is wiped via <see cref="AccountSession.Wipe"/> ONLY when the identity endpoint
/// (<see cref="IDepartamentApiClient.GetMe"/>, via <see cref="RefreshProfile"/>) returns 401 — the
/// single reliable "the 7-day JWT is dead" signal. A 401/403 on any OTHER endpoint surfaces as a plain
/// error and NEVER touches the session; only an explicit user logout does.
/// </summary>
public sealed class AccountRepository
{
    private readonly IDepartamentApiClient _api;
    private readonly SubscriptionSyncManager _subs;

    public AccountRepository(IDepartamentApiClient? api = null, SubscriptionSyncManager? subs = null)
    {
        _api = api ?? new DepartamentApiClient();
        _subs = subs ?? new SubscriptionSyncManager();
    }

    /// <summary>
    /// Runs an authenticated API call and normalises failures to <see cref="ApiResult{T}"/>.
    /// Deliberately does NOT wipe the session on <see cref="ApiError.Unauthorized"/>: only
    /// <see cref="RefreshProfile"/> (the identity endpoint) is allowed to wipe.
    /// </summary>
    private static async Task<ApiResult<T>> Guard<T>(Func<Task<T>> block)
    {
        try
        {
            return ApiResult<T>.Success(await block());
        }
        catch (ApiError e)
        {
            return ApiResult<T>.Failure(e);
        }
        catch (Exception e)
        {
            return ApiResult<T>.Failure(new ApiError.NetworkError(e));
        }
    }

    // Public catalog / status
    public Task<ApiResult<PublicConfigDto>> LoadPublicConfig() => Guard(() => _api.GetPublicConfig());

    public Task<ApiResult<TariffCatalogDto>> LoadCatalog() => Guard(() => _api.GetPublicTariffs());

    public Task<ApiResult<List<ServerStatusDto>>> LoadServerStatus() => Guard(() => _api.GetServerStatus());

    // Profile
    //
    // GetMe() is the authoritative identity check: it is the ONLY endpoint whose 401 reliably means the
    // JWT is dead. A 401 here — and only here — wipes the local session so an expired 7-day token
    // self-heals into a logged-out state. Every other failure leaves the session intact.
    public async Task<ApiResult<UserProfileDto>> RefreshProfile()
    {
        try
        {
            var profile = await _api.GetMe();
            AccountSession.UpdateProfile(profile);
            return ApiResult<UserProfileDto>.Success(profile);
        }
        catch (ApiError.Unauthorized e)
        {
            await AccountSession.Wipe();
            return ApiResult<UserProfileDto>.Failure(e);
        }
        catch (ApiError e)
        {
            return ApiResult<UserProfileDto>.Failure(e);
        }
        catch (Exception e)
        {
            return ApiResult<UserProfileDto>.Failure(new ApiError.NetworkError(e));
        }
    }

    // Subscriptions
    /// <summary>The authoritative ACTIVE (root) subscription — /all often returns no root item.</summary>
    public Task<ApiResult<PrimarySubscriptionDto>> LoadPrimarySubscription() => Guard(() => _api.GetPrimarySubscription());

    public Task<ApiResult<SubscriptionAllDto>> LoadSubscriptions() => Guard(() => _api.GetSubscriptionAll());

    /// <summary>Fetches all subscriptions and imports them into the local plumbing; returns local guids.</summary>
    public Task<ApiResult<List<string>>> AutoImportSubscriptions() => Guard(async () =>
    {
        var all = await _api.GetSubscriptionAll();
        return await _subs.ImportAll(all.Items);
    });

    public Task<ApiResult<bool>> RenameSubscription(string scope, string id, string name) =>
        Guard(async () => { await _api.RenameSubscription(scope, id, name); return true; });

    public Task<ApiResult<byte[]>> GetQr(string remnawaveUuid) => Guard(() => _api.GetSubscriptionQr(remnawaveUuid));

    // Purchase / renew / upgrade / devices
    public Task<ApiResult<PaymentInitDto>> Buy(PaymentRequestDto req) => Guard(() => _api.PayPlatega(req));

    public Task<ApiResult<PaymentResultDto>> PayWithBalance(PaymentRequestDto req) => Guard(() => _api.PayBalance(req));

    /// <summary>Renew is a Platega purchase of the given tariff/price-option for an existing subscription.</summary>
    public Task<ApiResult<PaymentInitDto>> Renew(PaymentRequestDto req) => Guard(() => _api.PayPlatega(req));

    public Task<ApiResult<UpgradeQuoteDto>> UpgradeQuote(string targetTariffId) => Guard(() => _api.GetUpgradeQuote(targetTariffId));

    public Task<ApiResult<PaymentInitDto>> Upgrade(string targetTariffId, string method, string subscriptionUuid, string? paymentMethod = null) =>
        Guard(() => _api.Upgrade(targetTariffId, method, subscriptionUuid, paymentMethod));

    public Task<ApiResult<PaymentInitDto>> AddDevices(string scope, string id, int extraDevices, string method, string? paymentMethod = null) =>
        Guard(() => _api.AddDevices(scope, id, extraDevices, method, paymentMethod));

    public Task<ApiResult<DevicesResult>> GetDevices(string remnawaveUuid) => Guard(() => _api.GetDevices(remnawaveUuid));

    public Task<ApiResult<bool>> DeleteDevice(string hwid, string remnawaveUuid) =>
        Guard(async () => { await _api.DeleteDevice(hwid, remnawaveUuid); return true; });

    // Payments history
    public Task<ApiResult<PaymentsDto>> GetPayments() => Guard(() => _api.GetPayments());

    // Promo / trial / auto-renew / referral
    public Task<ApiResult<PromoDto>> CheckPromo(string code) => Guard(() => _api.CheckPromo(code));

    public Task<ApiResult<bool>> ActivatePromo(string code) =>
        Guard(async () => { await _api.ActivatePromo(code); return true; });

    public Task<ApiResult<bool>> ActivateTrial() =>
        Guard(async () => { await _api.ActivateTrial(); return true; });

    public Task<ApiResult<bool>> ToggleAutoRenew(string id, bool autoRenew) =>
        Guard(async () => { await _api.SetSecondaryAutoRenew(id, autoRenew); return true; });

    /// <summary>Auto-renew of the active (root/primary) subscription — targets the id-less endpoint.</summary>
    public Task<ApiResult<bool>> TogglePrimaryAutoRenew(bool autoRenew) =>
        Guard(async () => { await _api.SetPrimaryAutoRenew(autoRenew); return true; });

    public Task<ApiResult<ReferralStatsDto>> GetReferralStats() => Guard(() => _api.GetReferralStats());
}
