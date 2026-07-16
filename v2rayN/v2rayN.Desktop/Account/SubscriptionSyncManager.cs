using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>
/// Bridges the backend subscription payloads into the app's EXISTING subscription plumbing. Port of
/// V2rayNG auth/SubscriptionSyncManager.kt — it REUSES the engine rather than duplicating it:
///  - <see cref="ConfigHandler.AddSubItem(Config, SubItem)"/> persists each subscription row;
///  - <see cref="SubscriptionHandler.UpdateProcess"/> fetches + parses + imports the servers, FORCING
///    a v2rayNG-family User-Agent (the panel keys the "real server list vs app-not-supported" response
///    off it — the branding UA gets the wrong content);
///  - <see cref="ConfigHandler.DeleteSubItem(Config, string)"/> drops a managed sub gone remotely.
///
/// KEY FACT (why the previous import fetched nothing): the connect URL (<c>subscriptionUrl</c>) lives
/// ONLY on GET /client/subscription — the authoritative ACTIVE/primary subscription. The
/// /client/subscription/all items arrive WITHOUT a <c>subscription</c>/<c>remnawaveUuid</c> block, so
/// importing from /all alone yields no URL and therefore no servers. This manager fetches the PRIMARY
/// summary for the real URL (and merges any /all item that happens to expose its own URL), sets it as
/// the created <see cref="SubItem.Url"/>, and lets the normal subscription update download the servers.
///
/// The uuid-&gt;guid mapping is owned by <see cref="AuthTokenStore"/>.
/// </summary>
public sealed class SubscriptionSyncManager
{
    /// <summary>
    /// v2rayNG-family User-Agent stamped on every account-imported subscription. The departament
    /// panel keys its managed server list off a recognised v2rayNG client, so this must stay a
    /// v2rayNG UA — it is passed through verbatim by <see cref="SubscriptionHandler"/>.
    /// </summary>
    private const string AccountSubscriptionUserAgent = "v2rayNG/1.10.6";

    private readonly IDepartamentApiClient _api;

    public SubscriptionSyncManager(IDepartamentApiClient? api = null)
    {
        _api = api ?? new DepartamentApiClient();
    }

    /// <summary>
    /// GETs the account's subscriptions (primary summary + /all), imports/updates each into the local
    /// plumbing, removes any managed subscription no longer present remotely, and returns the local
    /// guids of the current managed set (so the caller can reload its server list).
    /// </summary>
    public async Task<List<string>> ImportAll()
    {
        // The PRIMARY summary is the authoritative source of the real connect URL. A "no active
        // subscription" account returns a 200 with an empty subscription (not an error), so the
        // best-effort try/catch only swallows genuine transient failures — the subsequent
        // AccountViewModel.FetchAndApplySubscriptions surfaces those to the UI.
        PrimarySubscriptionDto? primary = null;
        try
        {
            primary = await _api.GetPrimarySubscription();
        }
        catch (ApiError)
        {
            // fall through — still import anything /all exposes
        }

        List<SubInfoDto> all;
        try
        {
            all = (await _api.GetSubscriptionAll()).Items;
        }
        catch (ApiError)
        {
            all = new List<SubInfoDto>();
        }

        var profile = AuthTokenStore.GetUser();
        return await Import(primary, all, profile);
    }

    private async Task<List<string>> Import(PrimarySubscriptionDto? primary, List<SubInfoDto> all, UserProfileDto? profile)
    {
        var config = AppManager.Instance.Config;
        var managed = AuthTokenStore.GetManagedGuids();
        var existing = await AppManager.Instance.SubItems() ?? new List<SubItem>();
        var newMap = new Dictionary<string, string>();
        var resultGuids = new List<string>();

        foreach (var candidate in BuildCandidates(primary, all, profile))
        {
            // Reuse the guid we already manage for this uuid, else an existing SubItem with the same
            // URL, else create a new row (AddSubItem assigns the guid).
            SubItem? item = null;
            if (managed.TryGetValue(candidate.Uuid, out var mappedGuid) && mappedGuid.IsNotEmpty())
            {
                item = await AppManager.Instance.GetSubItem(mappedGuid);
            }
            item ??= existing.FirstOrDefault(s => s.Url == candidate.Url);
            item ??= new SubItem { Id = string.Empty, Url = candidate.Url };

            item.Remarks = candidate.Remarks;
            item.Url = candidate.Url;                              // the ACCOUNT subscription URL
            item.Enabled = true;
            // Stamp an explicit v2rayNG-family UA: the departament/Remnawave panel serves its managed
            // server list only for a recognised v2rayNG client. SubscriptionHandler.ResolveSubUserAgent
            // honours this verbatim. (Manually-added subs carry no UA and correctly fall back to the
            // standard "v2rayN/<version>" desktop UA instead — see that method.)
            item.UserAgent = AccountSubscriptionUserAgent;

            await ConfigHandler.AddSubItem(config, item);
            var guid = item.Id;
            if (guid.IsNullOrEmpty())
            {
                continue;
            }

            // Download + import the servers behind the subscription URL via the shared updater, which
            // FORCES the correct v2rayNG-family User-Agent. This is the whole reason a plain
            // DownloadService call with the branding UA fetched an "app not supported" placeholder.
            await SubscriptionHandler.UpdateProcess(config, guid, false, static (_, _) => Task.CompletedTask);

            if (!newMap.ContainsKey(candidate.Uuid))
            {
                newMap[candidate.Uuid] = guid;
                resultGuids.Add(guid);
            }
        }

        // Drop any previously managed subscription whose guid is not in the freshly imported set.
        foreach (var kv in managed)
        {
            if (kv.Value.IsNotEmpty() && !newMap.Values.Contains(kv.Value))
            {
                await ConfigHandler.DeleteSubItem(config, kv.Value);
            }
        }

        AuthTokenStore.SetManagedGuids(newMap);
        return resultGuids;
    }

    /// <summary>
    /// Builds the ordered import set: the ACTIVE/primary subscription first (the only reliable source
    /// of a real connect URL), then any /all item that happens to expose its OWN url (future-proof —
    /// today /all never does). De-duplicated by url.
    /// </summary>
    private static IEnumerable<Candidate> BuildCandidates(PrimarySubscriptionDto? primary, List<SubInfoDto> all, UserProfileDto? profile)
    {
        var result = new List<Candidate>();
        var seenUrls = new HashSet<string>();
        var rootFromAll = all.FirstOrDefault(it => string.Equals(it.Type, "root", StringComparison.OrdinalIgnoreCase));

        var primaryUrl = primary?.Raw()?.SubscriptionUrl;
        if (primary?.HasActiveSubscription() == true && primaryUrl.IsNotEmpty() && seenUrls.Add(primaryUrl!))
        {
            var uuid = FirstNonBlank(profile?.RemnawaveUuid, rootFromAll?.RemnawaveUuid, rootFromAll?.Id, primaryUrl);
            var remarks = FirstNonBlank(rootFromAll?.DisplayName, primary.TariffDisplayName, rootFromAll?.TariffDisplayName, "Departament VPN");
            result.Add(new Candidate(uuid, primaryUrl!, remarks));
        }

        foreach (var info in all)
        {
            var url = info.Subscription?.Raw()?.SubscriptionUrl;
            if (url.IsNullOrEmpty() || !seenUrls.Add(url!))
            {
                continue;
            }
            var uuid = FirstNonBlank(info.RemnawaveUuid, info.Id, url);
            var remarks = FirstNonBlank(info.DisplayName, info.TariffDisplayName, "Departament VPN");
            result.Add(new Candidate(uuid, url!, remarks));
        }

        return result;
    }

    /// <summary>
    /// Removes every managed subscription and its servers. Invoked only from
    /// <see cref="AccountSession.Wipe"/> (explicit logout, or a confirmed-dead JWT on the identity
    /// endpoint).
    /// </summary>
    public async Task RemoveAllManaged()
    {
        var config = AppManager.Instance.Config;
        var managed = AuthTokenStore.GetManagedGuids();
        foreach (var kv in managed)
        {
            if (kv.Value.IsNotEmpty())
            {
                await ConfigHandler.DeleteSubItem(config, kv.Value);
            }
        }
        AuthTokenStore.SetManagedGuids(new Dictionary<string, string>());
    }

    private static string FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
        {
            if (v.IsNotEmpty())
            {
                return v!;
            }
        }
        return string.Empty;
    }

    private readonly record struct Candidate(string Uuid, string Url, string Remarks);
}
