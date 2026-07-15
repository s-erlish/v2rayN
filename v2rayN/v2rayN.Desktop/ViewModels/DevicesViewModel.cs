using v2rayN.Desktop.Account;
using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.ViewModels;

/// <summary>
/// Backs the «Устройства» sub-screen. Port of V2rayNG ui/DeviceManagementActivity.kt +
/// ui/adapter/DeviceAdapter.kt (lifecycleScope → ReactiveUI): lists the devices (HWIDs) bound to
/// the active subscription and removes one after an in-view confirmation. DATA-DRIVEN: everything
/// comes from GET /client/devices via <see cref="AccountRepository"/>; nothing is invented.
///
/// The subscription UUID may be supplied by the caller; otherwise it is resolved like Android does:
/// logged-in profile's remnawaveUuid (no network) → first /subscription/all item with a non-blank
/// remnawaveUuid → profile again → «Активная подписка не найдена».
///
/// Cache-first: a fresh (&lt; 1h) list in <see cref="AccountCache"/> renders instantly without a
/// network call; a successful delete rewrites the cached list so the Account tab count stays true.
/// </summary>
public class DevicesViewModel : MyReactiveObject
{
    private readonly AccountRepository _repo;

    private string? _remnawaveUuid;

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

        LoadCmd = ReactiveCommand.CreateFromTask(() => Load());
        DeleteCmd = ReactiveCommand.Create<DeviceDto>(AskDelete);
        ConfirmDeleteCmd = ReactiveCommand.CreateFromTask(ConfirmDelete);
        CancelDeleteCmd = ReactiveCommand.Create(CancelDelete);

        Recompute();
        _ = Load();
    }

    /// <summary>Design-time constructor: the list state from android_devices.jpg for the previewer.</summary>
    private DevicesViewModel(bool design)
    {
        _repo = null!;
        _pendingFirstLoad = false;
        Devices = new List<DeviceRow>
        {
            new(new DeviceDto { Hwid = "0210da79ff83470092941af8b390692d", Platform = "android", DeviceModel = "Xiaomi 2203129G", LastActiveAt = "2026-07-10T09:03:00Z" }),
            new(new DeviceDto { Hwid = "bdc968c12cd646848b9a814b6556f1a3", LastActiveAt = "2026-07-10T08:41:00Z" }),
            new(new DeviceDto { Hwid = "6ec3b80558194dabaf769c0f9351206f", Platform = "windows", LastActiveAt = "2026-07-09T19:12:00Z" }),
        };

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
    /// immediately with no network call.
    /// </summary>
    private async Task Load(bool forceRefresh = false)
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

        var uuid = _remnawaveUuid;
        if (uuid.IsNullOrEmpty())
        {
            // First /subscription/all item with a non-blank remnawaveUuid = the active sub.
            var all = await _repo.LoadSubscriptions();
            uuid = all.GetOrNull()?.Items?.FirstOrDefault(it => it.RemnawaveUuid.IsNotEmpty())?.RemnawaveUuid;
        }
        if (uuid.IsNullOrEmpty())
        {
            // /all is empty for primary-only accounts — fall back to the profile's own uuid.
            uuid = LoggedInProfileUuid();
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
        Devices = devices.Select(d => new DeviceRow(d)).ToList();
        Error = null;
        _pendingFirstLoad = false;
        Recompute();
    }

    /// <summary>remnawaveUuid of the logged-in profile, if any (covers primary-only accounts).</summary>
    private static string? LoggedInProfileUuid() =>
        (AccountSession.State as AccountState.LoggedIn)?.Profile.RemnawaveUuid.NullIfEmpty();

    #endregion load

    #region delete

    /// <summary>Opens the in-view confirmation overlay (port of confirmDelete).</summary>
    private void AskDelete(DeviceDto device)
    {
        _pendingDelete = device;
        DeleteConfirmText = $"Устройство «{DeviceRow.DisplayNameOf(device)}» будет отключено от подписки.";
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
                AppEvents.SendSnackMsgRequested.Publish("Не удалось удалить устройство. Попробуйте позже.");
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
                    AppEvents.SendSnackMsgRequested.Publish("Устройство удалено");
                })
                .OnFailure(_ => AppEvents.SendSnackMsgRequested.Publish("Не удалось удалить устройство. Попробуйте позже."));
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
        ErrorText = Error != null ? MessageFor(Error) : string.Empty;
    }

    /// <summary>Human error reason; the fallback is devices_error_generic verbatim.</summary>
    private static string MessageFor(ApiError error) => error switch
    {
        ApiError.ServiceUnavailable => "Сервис временно недоступен",
        ApiError.NetworkError => "Ошибка сети. Проверьте подключение",
        ApiError.Unauthorized => "Требуется вход в аккаунт",
        ApiError.RateLimited => "Слишком много запросов. Попробуйте позже",
        ApiError.TimeoutError => "Превышено время ожидания",
        _ => "Не удалось загрузить устройства. Попробуйте позже.",
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

    public DeviceRow(DeviceDto dto)
    {
        Dto = dto;
        Name = DisplayNameOf(dto);

        var platform = dto.Platform.NullIfEmpty();
        var lastActive = FormatIsoDate(dto.LastActiveAt);
        if (lastActive.IsNotEmpty() && platform != null)
        {
            Meta = $"{platform} · Активно: {lastActive}";
        }
        else if (lastActive.IsNotEmpty())
        {
            Meta = $"Активно: {lastActive}";
        }
        else
        {
            Meta = platform ?? dto.AppVersion.NullIfEmpty() ?? string.Empty;
        }
        HasMeta = Meta.IsNotEmpty();

        HwidText = $"ID: {dto.Hwid}";
        HasHwid = dto.Hwid.IsNotEmpty();
    }

    public static string DisplayNameOf(DeviceDto dto) =>
        dto.DeviceModel.NullIfEmpty() ?? dto.Platform.NullIfEmpty() ?? "Неизвестное устройство";

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
