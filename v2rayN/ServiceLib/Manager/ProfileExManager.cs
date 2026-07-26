//using System.Reactive.Linq;

namespace ServiceLib.Manager;

/// <summary>
/// Owns the per-profile "extra" row (ping, speed, sort, message) and its write-behind flush.
///
/// THREAD SAFETY IS LOAD-BEARING HERE, not decorative. Every field below is mutated from at least
/// four uncoordinated contexts: up to <see cref="Global.SpeedTestPageSize"/> parallel speedtest
/// continuations, the subscription auto-update background loop (<c>TaskManager</c> →
/// <c>ConfigHandler.AddServerCommon</c> → <see cref="SetSort"/>), the UI thread (move/sort a server),
/// and the flush itself — which has three independent callers (a finished speedtest, the 20-minute
/// TaskManager tick, and app exit) and awaits a DB read half-way through, so two flushes can interleave
/// on a single thread. The previous plain <c>Queue&lt;string&gt;</c> + <c>ConcurrentBag</c> lost whole
/// batches of results and, with them, the user's server order.
/// </summary>
public class ProfileExManager
{
    private static readonly Lazy<ProfileExManager> _instance = new(() => new());

    /// <summary>
    /// Keyed by <c>IndexId</c> (the table's primary key) so "find it or create it" is one atomic
    /// <c>GetOrAdd</c>. The old <c>FirstOrDefault(...) ?? Add(...)</c> over a bag could produce two live
    /// objects for one id, of which one silently lost its values.
    /// </summary>
    private ConcurrentDictionary<string, ProfileExItem> _lstProfileEx = new();

    /// <summary>Ids waiting to be flushed.</summary>
    private readonly ConcurrentQueue<string> _queIndexIds = new();

    /// <summary>
    /// Membership companion for <see cref="_queIndexIds"/>. <c>ConcurrentQueue.Contains</c> is an O(n)
    /// snapshot walk, not an atomic test, so the dedup cannot live on the queue itself.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _queued = new();

    /// <summary>One drain at a time — the drain spans an await, so its three callers would otherwise
    /// interleave and the loser would dequeue from an emptied queue.</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public static ProfileExManager Instance => _instance.Value;
    private static readonly string _tag = "ProfileExHandler";

    public ProfileExManager()
    {
        //Init();
    }

    public async Task Init()
    {
        await InitData();
    }

    public async Task<IReadOnlyList<ProfileExItem>> GetProfileExs()
    {
        return await Task.FromResult<IReadOnlyList<ProfileExItem>>(_lstProfileEx.Values.ToList());
    }

    private async Task InitData()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileExItem where indexId not in ( select indexId from ProfileItem )");

        var rows = await SQLiteHelper.Instance.TableAsync<ProfileExItem>().ToListAsync();
        var map = new ConcurrentDictionary<string, ProfileExItem>();
        foreach (var row in rows)
        {
            if (row.IndexId.IsNotEmpty())
            {
                map[row.IndexId] = row;
            }
        }
        _lstProfileEx = map;
    }

    private void IndexIdEnqueue(string? indexId)
    {
        // TryAdd is the atomic "was it already pending?" test, so a duplicate can never be enqueued and
        // a genuinely new id can never be dropped by a check-then-act race.
        if (indexId.IsNotEmpty() && _queued.TryAdd(indexId!, 0))
        {
            _queIndexIds.Enqueue(indexId!);
        }
    }

    private async Task SaveQueueIndexIds()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_queIndexIds.IsEmpty)
            {
                return;
            }

            var lstExists = await SQLiteHelper.Instance.TableAsync<ProfileExItem>().ToListAsync();
            var existingIds = lstExists.Select(t => t.IndexId).ToHashSet();
            List<ProfileExItem> lstInserts = [];
            List<ProfileExItem> lstUpdates = [];
            List<string> drained = [];

            // TryDequeue instead of a captured Count: the old `for (i < cnt) Dequeue()` re-read a count
            // taken BEFORE the await above, so an interleaved drain made it dequeue from an empty queue
            // and throw — discarding every result it had already collected.
            while (_queIndexIds.TryDequeue(out var id))
            {
                _queued.TryRemove(id, out _);
                drained.Add(id);
                if (!_lstProfileEx.TryGetValue(id, out var itemNew))
                {
                    continue;
                }

                if (existingIds.Contains(id))
                {
                    lstUpdates.Add(itemNew);
                }
                else
                {
                    lstInserts.Add(itemNew);
                }
            }

            try
            {
                if (lstInserts.Count > 0)
                {
                    await SQLiteHelper.Instance.InsertAllAsync(lstInserts);
                }

                if (lstUpdates.Count > 0)
                {
                    await SQLiteHelper.Instance.UpdateAllAsync(lstUpdates);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
                // Both writes run in a transaction, so a failure means NOTHING was persisted. Put the
                // batch back so the next flush retries it, instead of consuming the pending work and
                // losing the ping/speed/sort results for good.
                foreach (var id in drained)
                {
                    IndexIdEnqueue(id);
                }
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static ProfileExItem NewProfileEx(string indexId) => new()
    {
        IndexId = indexId,
        Delay = 0,
        Speed = 0,
        Sort = 0,
        Message = string.Empty
    };

    private ProfileExItem GetProfileExItem(string? indexId)
    {
        var key = indexId ?? string.Empty;
        var added = false;
        var item = _lstProfileEx.GetOrAdd(key, id =>
        {
            added = true;
            return NewProfileEx(id);
        });
        if (added)
        {
            IndexIdEnqueue(key);
        }
        return item;
    }

    public async Task ClearAll()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileExItem ");
        _lstProfileEx = new();
        // Drop the pending flush too: writing those ids back would resurrect rows we just deleted.
        while (_queIndexIds.TryDequeue(out var id))
        {
            _queued.TryRemove(id, out _);
        }
    }

    public async Task SaveTo()
    {
        try
        {
            await SaveQueueIndexIds();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    public void SetTestDelay(string indexId, int delay)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Delay = delay;
        IndexIdEnqueue(indexId);
    }

    public void SetTestSpeed(string indexId, decimal speed)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Speed = speed;
        IndexIdEnqueue(indexId);
    }

    public void SetTestMessage(string indexId, string message)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Message = message;
        IndexIdEnqueue(indexId);
    }

    public void SetTestIpInfo(string indexId, string ipInfo)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.IpInfo = ipInfo;
        IndexIdEnqueue(indexId);
    }

    public void SetSort(string indexId, int sort)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Sort = sort;
        IndexIdEnqueue(indexId);
    }

    public int GetSort(string indexId)
    {
        return _lstProfileEx.TryGetValue(indexId ?? string.Empty, out var profileEx) ? profileEx.Sort : 0;
    }

    public int GetMaxSort()
    {
        if (_lstProfileEx.IsEmpty)
        {
            return 0;
        }
        return _lstProfileEx.Values.Max(t => t?.Sort ?? 0);
    }
}
