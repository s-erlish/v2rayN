using v2rayN.Desktop.Account;
using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Backs the «Устройства» sub-screen. Port of V2rayNG ui/DeviceManagementActivity.kt +
/// ui/adapter/DeviceAdapter.kt (lifecycleScope → ReactiveUI): lists the devices (HWIDs) bound to
/// the active subscription and removes one after an in-view confirmation. DATA-DRIVEN: everything
/// comes from GET /client/devices via <see cref="AccountRepository"/>; nothing is invented.
///
/// The subscription UUID may be supplied by the caller; otherwise it is resolved from the account:
/// logged-in profile's remnawaveUuid (no network) → GET /client/auth/me (the ONLY authoritative
/// source of the uuid, exactly what the Account tab uses) → first /subscription/all item with a
/// non-blank remnawaveUuid → «Активная подписка не найдена». The GetMe step is what makes the list
/// populate: the token-store profile can lack the uuid until the identity endpoint enriches it, and
/// /all items never carry a remnawaveUuid (SubscriptionDtos.cs), so without GetMe resolution dead-ends.
///
/// Re-resolves on login: <see cref="AccountSession.StateChanged"/> is observed so opening Devices
/// right after signing in (before the profile has a uuid) still populates once the session settles.
///
/// Cache-first: a fresh (&lt; 1h) list in <see cref="AccountCache"/> renders instantly without a
/// network call; a successful delete rewrites the cached list so the Account tab count stays true.
/// </summary>
public class DevicesViewModel : MyReactiveObject, IDisposable
{
    private readonly AccountRepository _repo;

    // Observes AccountSession so Devices re-resolves when the session becomes logged-in (or the
    // profile is refreshed with a uuid). Null in design mode. Detached in Dispose to avoid a leak,
    // since AccountSession.StateChanged is a long-lived static event.
    private readonly Action<AccountState>? _onAccountStateChanged;

    private string? _remnawaveUuid;

    // Coalesces overlapping loads: our own RefreshProfile() fires AccountSession.StateChanged, which
    // would otherwise kick off a second Load() mid-flight. True for the duration of one load.
    private volatile bool _loadInFlight;

    // True until the FIRST load result lands: gates the skeleton so the pre-load instant does not
    // flash the empty state (same trick as AccountViewModel._pendingFirstLoad).
    private bool _pendingFirstLoad = true;

    // Set when no subscription with a remnawaveUuid could be resolved (devices_error_no_subscription).
    private bool _noSubscription;

    // The device awaiting confirmation in the delete overlay.
    private DeviceDto? _pendingDelete;

    #region reactive state

    [Reactive] public List<DeviceRow> Devices { get; set; } = new();
    [Reactive] public bool IsLoading { get; set; }
    [Reactive] public ApiError? Error { get; set; }
    [Reactive] public string ErrorText { get; set; } = string.Empty;

    // The five mutually-exclusive content slots (skeletons / list / empty / no-sub / error).
    [Reactive] public bool ShowLoading { get; set; }
    [Reactive] public bool ShowList { get; set; }
    [Reactive] public bool ShowEmpty { get; set; }
    [Reactive] public bool ShowNoSub { get; set; }
    [Reactive] public bool ShowError { get; set; }

    // «Устройства, подключённые к вашей подписке» is list-chrome: Android hides it whenever the
    // empty/no-sub/error overlay is up because it would contradict «нет устройств».
    [Reactive] public bool ShowSubtitle { get; set; }

    // Device count shown next to the toolbar title, only while the real list is on screen.
    [Reactive] public string CountText { get; set; } = string.Empty;
    [Reactive] public bool HasCount { get; set; }

    // Delete confirmation overlay (in-view modal, port of the MaterialAlertDialog).
    [Reactive] public bool ShowDeleteConfirm { get; set; }
    [Reactive] public string DeleteConfirmText { get; set; } = string.Empty;
    [Reactive] public bool IsDeleting { get; set; }

    #endregion reactive state

    #region commands

    public ReactiveCommand<Unit, Unit> LoadCmd { get; }

    /// <summary>Trash button on a device card: opens the confirmation overlay for that device.</summary>
    public ReactiveCommand<DeviceDto, Unit> DeleteCmd { get; }

    public ReactiveCommand<Unit, Unit> ConfirmDeleteCmd { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteCmd { get; }

    #endregion commands

    /// <summary>
    /// Runtime constructor. <paramref name="remnawaveUuid"/> mirrors Android's
    /// EXTRA_REMNAWAVE_UUID: when the caller already knows the active subscription's UUID it skips
    /// the resolution round-trip and hits the cache fast path directly.
    /// </summary>
    public DevicesViewModel(string? remnawaveUuid = null)
    {
        _repo = new AccountRepository();
        _remnawaveUuid = remnawaveUuid.NullIfEmpty() ?? LoggedInProfileUuid();

        // «Повторить» refetches, bypassing any stale cache and re-resolving the uuid via GetMe.
        LoadCmd = ReactiveCommand.CreateFromTask(() => Load(forceRefresh: true));
        DeleteCmd = ReactiveCommand.Create<DeviceDto>(AskDelete);
        ConfirmDeleteCmd = ReactiveCommand.CreateFromTask(ConfirmDelete);
        CancelDeleteCmd = ReactiveCommand.Create(CancelDelete);

        _onAccountStateChanged = OnAccountStateChanged;
        AccountSession.StateChanged += _onAccountStateChanged;

        Recompute();
        _ = Load();
    }

    /// <summary>Design-time constructor: the list state from android_devices.jpg for the previewer.</summary>
    private DevicesViewModel(bool design)
    {
        _repo = null!;
        _pendingFirstLoad = false;
        const string here = "6ec3b80558194dabaf769c0f9351206f"; // «это устройство» in the preview
        var sample = new List<DeviceDto>
        {
            new() { Hwid = "0210da79ff83470092941af8b390692d", Platform = "android", DeviceModel = "Xiaomi 2203129G", LastActiveAt = "2026-07-10T09:03:00Z" },
            new() { Hwid = "bdc968c12cd646848b9a814b6556f1a3", Platform = "ios", DeviceModel = "iPhone 15 Pro", LastActiveAt = "2026-07-10T08:41:00Z" },
            new() { Hwid = here, Platform = "windows", DeviceModel = "PC", LastActiveAt = "2026-07-09T19:12:00Z" },
        };
        Devices = sample.Select((d, i) => new DeviceRow(d, here, showDivider: i > 0)).ToList();

        LoadCmd = ReactiveCommand.Create(() => { });
        DeleteCmd = ReactiveCommand.Create<DeviceDto>(AskDelete);
        ConfirmDeleteCmd = ReactiveCommand.Create(() => { });
        CancelDeleteCmd = ReactiveCommand.Create(CancelDelete);

        Recompute();
    }

    public static DevicesViewModel CreateDesign() => new(true);

    #region load

    /// <summary>
    /// Loads the device list (port of DeviceManagementActivity.loadDevices). Unless
    /// <paramref name="forceRefresh"/>, a fresh cached list for the resolved UUID renders
    /// immediately with no network call. Re-entrant calls (e.g. a StateChanged raised by our own
    /// RefreshProfile) are coalesced so only one load runs at a time.
    /// </summary>
    private async Task Load(bool forceRefresh = false)
    {
        if (_loadInFlight)
        {
            return;
        }
        _loadInFlight = true;
        try
        {
            await LoadCore(forceRefresh);
        }
        finally
        {
            _loadInFlight = false;
        }
    }

    private async Task LoadCore(bool forceRefresh)
    {
        RunOnUi(() =>
        {
            _noSubscription = false;
            Error = null;
        });

        // Fast path: UUID already known and a fresh cached list exists — render from memory.
        if (!forceRefresh && _remnawaveUuid.IsNotEmpty())
        {
            var cached = AccountCache.GetDevices(_remnawaveUuid!);
            if (cached != null)
            {
                RunOnUi(() => Render(cached));
                return;
            }
        }

        RunOnUi(() =>
        {
            IsLoading = true;
            Recompute();
        });

        // Re-read the profile: the session may have settled since the ctor ran (e.g. login just
        // completed) and now carries the uuid.
        var uuid = _remnawaveUuid.NullIfEmpty() ?? LoggedInProfileUuid();

        if (uuid.IsNullOrEmpty())
        {
            // AUTHORITATIVE step: GET /client/auth/me returns the profile that actually carries the
            // remnawaveUuid — the same source the Account tab uses to fetch the device count. Without
            // this the list dead-ends (the stored profile can lack the uuid; /all items never carry it).
            var me = await _repo.RefreshProfile();
            uuid = me.GetOrNull()?.RemnawaveUuid.NullIfEmpty() ?? LoggedInProfileUuid();

            // A real transport/server failure while resolving identity is an ERROR (retryable), not a
            // «нет подписки». Unauthorized means the session is gone → treat as no-subscription below.
            if (uuid.IsNullOrEmpty() && me.IsFailure && me.Error is { } identityErr and not ApiError.Unauthorized)
            {
                RunOnUi(() =>
                {
                    Error = identityErr;
                    _pendingFirstLoad = false;
                    IsLoading = false;
                    Recompute();
                });
                return;
            }
        }
        if (uuid.IsNullOrEmpty())
        {
            // Last resort: a non-blank remnawaveUuid on any /subscription/all item (rare — the DTO
            // documents these are usually blank, which is exactly why GetMe above is the real fix).
            var all = await _repo.LoadSubscriptions();
            uuid = all.GetOrNull()?.Items?.FirstOrDefault(it => it.RemnawaveUuid.IsNotEmpty())?.RemnawaveUuid;
        }
        if (uuid.IsNullOrEmpty())
        {
            RunOnUi(() =>
            {
                _noSubscription = true;
                _pendingFirstLoad = false;
                IsLoading = false;
                Recompute();
            });
            return;
        }
        _remnawaveUuid = uuid;

        // UUID was resolved via the network above; re-check the cache before fetching devices.
        if (!forceRefresh)
        {
            var cached = AccountCache.GetDevices(uuid!);
            if (cached != null)
            {
                RunOnUi(() =>
                {
                    Render(cached);
                    IsLoading = false;
                    Recompute();
                });
                return;
            }
        }

        var result = await _repo.GetDevices(uuid!);
        RunOnUi(() =>
        {
            result
                .OnSuccess(r =>
                {
                    AccountCache.PutDevices(uuid!, r.Devices);
                    Render(r.Devices);
                })
                .OnFailure(e =>
                {
                    Error = e;
                    _pendingFirstLoad = false;
                });
            IsLoading = false;
            Recompute();
        });
    }

    /// <summary>Publishes the fetched list as display rows and re-derives the content slot.</summary>
    private void Render(List<DeviceDto> devices)
    {
        // This machine's stable HWID → the row for THIS device is highlighted subtly.
        var currentHwid = CurrentDeviceHwid();
        Devices = devices
            .Select((d, i) => new DeviceRow(d, currentHwid, showDivider: i > 0))
            .ToList();
        Error = null;
        _pendingFirstLoad = false;
        Recompute();
    }

    /// <summary>The stable per-machine HWID, used to flag the current device. Blank in design mode.</summary>
    private static string CurrentDeviceHwid()
    {
        try
        {
            return AuthTokenStore.DeviceId();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>remnawaveUuid of the logged-in profile, if any (covers primary-only accounts).</summary>
    private static string? LoggedInProfileUuid() =>
        (AccountSession.State as AccountState.LoggedIn)?.Profile.RemnawaveUuid.NullIfEmpty();

    /// <summary>
    /// Re-resolves + reloads when the session becomes logged-in (or its profile gains a uuid), so
    /// opening Devices immediately after login populates without a manual refresh; clears to
    /// «нет подписки» on logout. Raised on the caller's thread — Load()/RunOnUi marshal to the UI.
    /// </summary>
    private void OnAccountStateChanged(AccountState state)
    {
        if (state is AccountState.LoggedIn logged)
        {
            var uuid = logged.Profile.RemnawaveUuid.NullIfEmpty();
            if (uuid.IsNotEmpty() && !string.Equals(uuid, _remnawaveUuid, StringComparison.OrdinalIgnoreCase))
            {
                _remnawaveUuid = uuid;
                _ = Load();
            }
            else if (_remnawaveUuid.IsNullOrEmpty())
            {
                // Session is logged-in but the uuid is not on the profile yet — let Load() resolve it.
                _ = Load();
            }
        }
        else if (state is AccountState.LoggedOut)
        {
            RunOnUi(() =>
            {
                _remnawaveUuid = null;
                _noSubscription = true;
                _pendingFirstLoad = false;
                IsLoading = false;
                Devices = new List<DeviceRow>();
                Error = null;
                Recompute();
            });
        }
    }

    #endregion load

    #region delete

    /// <summary>Opens the in-view confirmation overlay (port of confirmDelete).</summary>
    private void AskDelete(DeviceDto device)
    {
        _pendingDelete = device;
        DeleteConfirmText = Common.L.F("Devices_UnlinkBody", DeviceRow.DisplayNameOf(device));
        ShowDeleteConfirm = true;
    }

    /// <summary>Overlay «Удалить»: POST /client/devices/delete, then drop the row on success.</summary>
    private async Task ConfirmDelete()
    {
        var device = _pendingDelete;
        if (device == null)
        {
            return;
        }
        var uuid = _remnawaveUuid;
        if (uuid.IsNullOrEmpty() || device.Hwid.IsNullOrEmpty())
        {
            RunOnUi(() =>
            {
                CloseConfirm();
                AppEvents.SendSnackMsgRequested.Publish(Common.L.T("Devices_UnlinkFailed"));
            });
            return;
        }

        RunOnUi(() => IsDeleting = true);
        var result = await _repo.DeleteDevice(device.Hwid, uuid!);
        RunOnUi(() =>
        {
            result
                .OnSuccess(_ =>
                {
                    // Remove the device locally and rewrite the cache, so both this list and the
                    // Account tab's «N / ∞» counter reflect the deletion without a refetch.
                    var updated = Devices.Select(r => r.Dto).Where(d => d.Hwid != device.Hwid).ToList();
                    AccountCache.PutDevices(uuid!, updated);
                    Render(updated);
                    AppEvents.SendSnackMsgRequested.Publish(Common.L.T("Devices_Unlinked"));
                })
                .OnFailure(_ => AppEvents.SendSnackMsgRequested.Publish(Common.L.T("Devices_UnlinkFailed")));
            IsDeleting = false;
            CloseConfirm();
            Recompute();
        });
    }

    /// <summary>Overlay «Отмена» / scrim click / Escape. Ignored while the delete is in flight.</summary>
    public void CancelDelete()
    {
        if (IsDeleting)
        {
            return;
        }
        CloseConfirm();
    }

    private void CloseConfirm()
    {
        _pendingDelete = null;
        ShowDeleteConfirm = false;
    }

    #endregion delete

    #region derive state

    /// <summary>Recomputes the mutually-exclusive content slot + derived texts.</summary>
    private void Recompute()
    {
        bool list = false, loading = false, empty = false, noSub = false, error = false;
        if (Devices.Count > 0)
        {
            list = true;
        }
        else if (_noSubscription)
        {
            noSub = true;
        }
        else if (IsLoading || _pendingFirstLoad)
        {
            loading = true;
        }
        else if (Error != null)
        {
            error = true;
        }
        else
        {
            empty = true;
        }

        ShowList = list;
        ShowLoading = loading;
        ShowEmpty = empty;
        ShowNoSub = noSub;
        ShowError = error;
        ShowSubtitle = list || loading;
        HasCount = list && Devices.Count > 0;
        CountText = HasCount ? Devices.Count.ToString() : string.Empty;
        ErrorText = Error != null ? MessageFor(Error) : string.Empty;
    }

    /// <summary>Human error reason; the fallback is devices_error_generic verbatim.</summary>
    private static string MessageFor(ApiError error) => error switch
    {
        ApiError.ServiceUnavailable => Common.L.T("Common_ServiceUnavailable"),
        ApiError.NetworkError => Common.L.T("Common_NetworkError"),
        ApiError.Unauthorized => Common.L.T("Common_SignInRequired"),
        ApiError.RateLimited => Common.L.T("Common_TooManyRequests"),
        ApiError.TimeoutError => Common.L.T("Common_Timeout"),
        _ => Common.L.T("Devices_ErrLoad"),
    };

    private static void RunOnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }

    /// <summary>Detaches the static AccountSession subscription. Called by the view on unload.</summary>
    public void Dispose()
    {
        if (_onAccountStateChanged != null)
        {
            AccountSession.StateChanged -= _onAccountStateChanged;
        }
    }

    #endregion derive state
}

/// <summary>
/// One device card, display-ready (port of DeviceAdapter.onBindViewHolder): name = model →
/// platform → «Неизвестное устройство»; meta = «platform · Активно: dd.MM.yyyy» with graceful
/// degradation; id line = «ID: hwid». Immutable — the list is replaced wholesale on every render.
/// </summary>
public sealed class DeviceRow
{
    public DeviceDto Dto { get; }
    public string Name { get; }
    public string Meta { get; }
    public bool HasMeta { get; }
    public string HwidText { get; }
    public bool HasHwid { get; }

    // One inset divider BETWEEN rows: every row except the first draws its top hairline.
    public bool ShowDivider { get; }

    // THIS machine: the row is tinted and carries an «Это устройство» marker.
    public bool IsCurrent { get; }

    // Platform tile glyph — exactly one is true (the rest drive collapsed PathIcons in the row).
    public bool IsAndroid { get; }
    public bool IsApple { get; }
    public bool IsWindows { get; }
    public bool IsRouter { get; }
    public bool IsGenericPlatform { get; }

    public DeviceRow(DeviceDto dto, string? currentHwid = null, bool showDivider = false)
    {
        Dto = dto;
        Name = DisplayNameOf(dto);
        ShowDivider = showDivider;
        IsCurrent = currentHwid.IsNotEmpty() && dto.Hwid.IsNotEmpty()
            && string.Equals(dto.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase);

        // Map the free-text platform to one tile glyph. "win" is checked AFTER apple so "darwin"
        // (macOS) does not fall through to the Windows glyph.
        var p = (dto.Platform ?? string.Empty).ToLowerInvariant();
        if (p.Contains("android"))
        {
            IsAndroid = true;
        }
        else if (p.Contains("ios") || p.Contains("iphone") || p.Contains("ipad")
            || p.Contains("mac") || p.Contains("darwin") || p.Contains("apple"))
        {
            IsApple = true;
        }
        else if (p.Contains("win"))
        {
            IsWindows = true;
        }
        else if (p.Contains("router") || p.Contains("openwrt") || p.Contains("keenetic") || p.Contains("gl-"))
        {
            IsRouter = true;
        }
        else
        {
            IsGenericPlatform = true;
        }

        var platform = dto.Platform.NullIfEmpty();
        var lastActive = FormatIsoDate(dto.LastActiveAt);
        if (lastActive.IsNotEmpty() && platform != null)
        {
            Meta = Common.L.F("Devices_PlatformActive", platform, lastActive);
        }
        else if (lastActive.IsNotEmpty())
        {
            Meta = Common.L.F("Devices_Active", lastActive);
        }
        else
        {
            Meta = platform ?? dto.AppVersion.NullIfEmpty() ?? string.Empty;
        }
        HasMeta = Meta.IsNotEmpty();

        HwidText = Common.L.F("Devices_Id", dto.Hwid);
        HasHwid = dto.Hwid.IsNotEmpty();
    }

    public static string DisplayNameOf(DeviceDto dto) =>
        dto.DeviceModel.NullIfEmpty() ?? dto.Platform.NullIfEmpty() ?? Common.L.T("Devices_Unknown");

    /// <summary>ISO-8601 (or date-only) → dd.MM.yyyy; "" for blank/unparseable input.</summary>
    private static string FormatIsoDate(string? iso)
    {
        if (iso.IsNullOrEmpty())
        {
            return string.Empty;
        }
        var datePart = iso!.Split('T')[0];
        var parts = datePart.Split('-');
        return parts.Length == 3 ? $"{parts[2]}.{parts[1]}.{parts[0]}" : datePart;
    }
}
