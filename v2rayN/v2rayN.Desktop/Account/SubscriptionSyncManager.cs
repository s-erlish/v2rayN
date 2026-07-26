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
/// KEY FACT (why the previous import fetched nothing): the connect URL (<c>subscriptionUrl</c>) is
/// only GUARANTEED on GET /client/subscription — the authoritative ACTIVE/primary subscription. The
/// deployed backend also copies the same Remnawave payload onto each /client/subscription/all item
/// (client.routes.ts builds `subscription: rootResult.data ?? null` per item), but it is `null`
/// whenever the upstream panel is unreachable, so /all alone is not a dependable source of URLs. This
/// manager fetches the PRIMARY summary for the real URL, merges any /all item that exposes its own,
/// sets it as the created <see cref="SubItem.Url"/>, and lets the normal subscription update download
/// the servers.
///
/// PRUNING IS NOT DRIVEN BY "WE GOT NOTHING BACK". Both endpoints can answer HTTP 200 with a null
/// subscription during an upstream outage, and both can fail outright when the machine is offline;
/// neither means the account lost its subscriptions. <see cref="ImportAll"/> therefore only authorises
/// the prune when both calls succeeded AND the remote answer was self-consistent — see the
/// <c>canPrune</c> parameter of <see cref="Import"/>.
///
/// The uuid-&gt;guid mapping is owned by <see cref="AuthTokenStore"/>.
/// </summary>
public sealed class SubscriptionSyncManager
{
    /// <summary>
    /// v2rayNG-family User-Agent stamped on every account-imported subscription. The departament
    /// panel keys its managed server list off a recognised v2rayNG client, so this must stay a
    /// v2rayNG UA — it is passed through verbatim by <see cref="SubscriptionHandler"/>. Single source
    /// of truth: <see cref="Global.SubscriptionUserAgent"/>, the SAME literal the manual-add path
    /// forces, so the account and manual fetches send byte-identical User-Agents.
    /// </summary>
    private const string AccountSubscriptionUserAgent = Global.SubscriptionUserAgent;

    private readonly IDepartamentApiClient _api;

    public SubscriptionSyncManager(IDepartamentApiClient? api = null)
    {
        _api = api ?? new DepartamentApiClient();
    }

    /// <summary>
    /// GETs the account's subscriptions (primary summary + /all), imports/updates each into the local
    /// plumbing, removes any managed subscription that the account AUTHORITATIVELY no longer has, and
    /// returns the local guids of the current managed set (so the caller can reload its server list).
    /// A fetch that failed is rethrown AFTER the import so the caller's
    /// <c>ApiResult.OnFailure(Report)</c> shows the user an honest error instead of a silent no-op —
    /// but the local data is left intact either way.
    /// </summary>
    public async Task<List<string>> ImportAll()
    {
        // The PRIMARY summary is the authoritative source of the real connect URL. A "no active
        // subscription" account returns a 200 with an empty subscription (not an error), so a failure
        // here is always a genuine transient one. It is REMEMBERED (not swallowed): a fetch that did
        // not answer must not be read as "this subscription is gone".
        ApiError? fetchError = null;
        PrimarySubscriptionDto? primary = null;
        var primaryOk = false;
        try
        {
            primary = await _api.GetPrimarySubscription();
            primaryOk = true;
        }
        catch (ApiError e)
        {
            fetchError ??= e;   // still import anything /all exposes, but forbid the prune
        }

        List<SubInfoDto> all;
        var allOk = false;
        try
        {
            all = (await _api.GetSubscriptionAll()).Items;
            allOk = true;
        }
        catch (ApiError e)
        {
            fetchError ??= e;
            all = new List<SubInfoDto>();
        }

        var profile = AuthTokenStore.GetUser();
        // Offline / DNS / TLS / timeout / 429 / 502 / 503 / parse all land here with canPrune=false, so
        // the launch-time sync of an offline user can no longer delete the servers he is offline with.
        var guids = await Import(primary, all, profile, canPrune: primaryOk && allOk);
        if (fetchError is not null)
        {
            throw fetchError;
        }
        return guids;
    }

    /// <param name="canPrune">
    /// True only when the remote subscription set was actually determined. False means "could not
    /// determine" — the managed set is then preserved verbatim and nothing is deleted.
    /// </param>
    private async Task<List<string>> Import(PrimarySubscriptionDto? primary, List<SubInfoDto> all, UserProfileDto? profile, bool canPrune)
    {
        var config = AppManager.Instance.Config;
        var managed = AuthTokenStore.GetManagedGuids();
        var existing = await AppManager.Instance.SubItems() ?? new List<SubItem>();
        var newMap = new Dictionary<string, string>();
        var resultGuids = new List<string>();

        var candidates = BuildCandidates(primary, all, profile).ToList();

        // Second guard, for the failure that throws NOTHING: when the upstream Remnawave panel is down
        // the backend still answers 200 on both endpoints with `subscription: null`, so every item
        // loses its URL and BuildCandidates returns empty even though the account still owns the
        // subscriptions. Distinguish that from a genuinely emptied account: only an account that
        // reports no /all items AND no active primary is allowed to prune down to nothing.
        var remoteReportsNothing = all.Count == 0 && primary?.HasActiveSubscription() != true;
        if (candidates.Count == 0 && !remoteReportsNothing)
        {
            canPrune = false;
        }

        foreach (var candidate in candidates)
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

        if (!canPrune)
        {
            // Not authoritative. Keep every mapping we could not re-confirm and delete NOTHING, so the
            // user still has his subscriptions (and their servers) the next time the network works.
            foreach (var kv in managed)
            {
                if (kv.Value.IsNotEmpty() && !newMap.ContainsKey(kv.Key))
                {
                    newMap[kv.Key] = kv.Value;
                }
            }
            AuthTokenStore.SetManagedGuids(newMap);
            return resultGuids;
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
    /// Builds the ordered import set: the ACTIVE/primary subscription first (the most reliable source
    /// of a real connect URL), then any /all item that exposes its OWN url — the deployed backend does
    /// put one on both root and secondary items, but leaves it null when the upstream panel is down,
    /// which is why an empty result here is treated as "unknown", not "empty". De-duplicated by url.
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
                // Per-item guard: DeleteSubItem does raw SQLite work with no try/catch of its own, and a
                // single failing row must not abort the logout half-way — the session still has to be
                // cleared, and the caller (LogoutCmd) must not fault.
                try
                {
                    await ConfigHandler.DeleteSubItem(config, kv.Value);
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("RemoveAllManaged", ex);
                }
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
