using ServiceLib.Services;
using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>
/// Bridges the backend subscription payloads into the app's EXISTING subscription plumbing. Port of
/// V2rayNG auth/SubscriptionSyncManager.kt — it REUSES the engine rather than duplicating it:
///  - <see cref="ConfigHandler.AddSubItem(Config, SubItem)"/> persists each subscription row;
///  - <see cref="DownloadService.TryDownloadString(string, bool, string)"/> fetches the servers;
///  - <see cref="ConfigHandler.AddBatchServers(Config, string, string, bool)"/> parses + imports them;
///  - <see cref="ConfigHandler.DeleteSubItem(Config, string)"/> drops a managed sub gone remotely.
///
/// The uuid-&gt;guid mapping is owned by <see cref="AuthTokenStore"/>.
/// </summary>
public sealed class SubscriptionSyncManager
{
    /// <summary>
    /// Imports/updates every subscription in <paramref name="items"/>, removes locally any managed
    /// subscription no longer present remotely, and returns the local guids of the current managed set.
    /// </summary>
    public async Task<List<string>> ImportAll(List<SubInfoDto> items)
    {
        var config = AppManager.Instance.Config;
        var managed = AuthTokenStore.GetManagedGuids();
        var existing = await AppManager.Instance.SubItems() ?? new List<SubItem>();
        var newMap = new Dictionary<string, string>();
        var resultGuids = new List<string>();

        foreach (var info in items)
        {
            var raw = info.Subscription?.Raw();
            if (raw == null)
            {
                continue;
            }
            var url = raw.SubscriptionUrl;
            if (url.IsNullOrEmpty())
            {
                continue;
            }

            var uuid = FirstNonBlank(info.RemnawaveUuid, info.Id, url);

            // Reuse the guid we already manage for this uuid, else an existing SubItem with the same
            // URL, else create a new row (AddSubItem assigns the guid).
            SubItem? item = null;
            if (managed.TryGetValue(uuid, out var mappedGuid) && mappedGuid.IsNotEmpty())
            {
                item = await AppManager.Instance.GetSubItem(mappedGuid);
            }
            item ??= existing.FirstOrDefault(s => s.Url == url);
            item ??= new SubItem { Id = string.Empty, Url = url };

            item.Remarks = FirstNonBlank(info.DisplayName, info.TariffDisplayName, "Departament VPN");
            item.Url = url;
            item.Enabled = true;
            item.UserAgent = BackendConfig.SubscriptionUserAgent;

            await ConfigHandler.AddSubItem(config, item);
            var guid = item.Id;
            if (guid.IsNullOrEmpty())
            {
                continue;
            }

            // Fetch + import the servers behind the subscription URL (reuses the whole parser chain).
            var content = await new DownloadService().TryDownloadString(url, false, BackendConfig.SubscriptionUserAgent);
            if (content.IsNotEmpty())
            {
                await ConfigHandler.AddBatchServers(config, content!, guid, true);
            }

            newMap[uuid] = guid;
            resultGuids.Add(guid);
        }

        // Drop any previously managed subscription that is gone remotely.
        foreach (var kv in managed)
        {
            if (!newMap.ContainsKey(kv.Key) && kv.Value.IsNotEmpty())
            {
                await ConfigHandler.DeleteSubItem(config, kv.Value);
            }
        }

        AuthTokenStore.SetManagedGuids(newMap);
        return resultGuids;
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
}
