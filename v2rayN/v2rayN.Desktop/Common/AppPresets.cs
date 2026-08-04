using System.Text.Json;

namespace v2rayN.Desktop.Common;

/// <summary>
/// A NAMED, REVERSIBLE preset for «Прокси по приложениям»: a list of Windows process names that the
/// user can send around the tunnel with one switch, and take back with the same switch.
/// </summary>
/// <param name="Key">Stable id used to remember what the preset added. Never shown.</param>
/// <param name="TitleKey">Localisation key for the row title.</param>
/// <param name="HintKey">Localisation key for the row's second line.</param>
/// <param name="Processes">The executable names, exactly as Windows reports them.</param>
public sealed record AppPreset(string Key, string TitleKey, string HintKey, IReadOnlyList<string> Processes);

/// <summary>
/// The shipped presets for «Прокси по приложениям», and the record of what each one actually added.
///
/// Port of the Android client's <c>handler/RussianAppsPreset.kt</c> — the BEHAVIOUR, not the
/// contents: that preset is about Russian apps refusing a foreign exit address, this one is about
/// latency. Three properties are load-bearing and all three come from there:
///
/// 1. <b>It is a preset, not a hidden list.</b> It has a name, one switch applies and un-applies it,
///    and every process it contains is visible in the list it acts on and individually un-tickable
///    afterwards. A hard-coded list that silently edited the user's routing is indistinguishable
///    from a bug.
/// 2. <b>Un-applying gives back exactly what applying took.</b> <see cref="Apply"/> records the
///    entries it ACTUALLY ADDED — not the whole preset — and <see cref="Owned"/> is what
///    <see cref="Release"/> hands back. A process the user ticked by hand before ever touching the
///    preset survives un-applying it, because the preset never claimed it.
/// 3. <b>It never restarts the tunnel by itself.</b> Applying only edits the in-memory selection on
///    the page; the page's existing save path decides whether anything changed and only then writes
///    and reloads. Toggling a preset and toggling it back is zero writes and zero restarts.
///
/// <b>OFF BY DEFAULT</b>, unlike the Russian-apps preset. Nobody asked for games to leave the tunnel
/// out of the box, and silently doing it would surprise a user who does not play.
///
/// <para>ON THE PROCESS NAMES. Every one below was verified against a source — the vendor's own
/// support pages where they exist (Facepunch for Rust, Riot for the client), the store/anti-cheat
/// firewall guidance, and the process databases that catalogue the canonical executable of each
/// title. A wrong name is SILENT: it matches no process and simply does not route, so guessing one
/// is worse than omitting it. A process that is never running costs nothing — the routing rule just
/// never matches.</para>
///
/// <para>WHAT IS DELIBERATELY ABSENT. No generic runtime, ever: Minecraft runs as <c>javaw.exe</c>,
/// Warface's own binary is literally <c>Game.exe</c>, and Blizzard's updater is <c>Agent.exe</c> —
/// bypassing any of those would route unrelated software around the VPN, which is the one failure
/// this file must not have. Anti-cheat services (<c>vgc.exe</c>/<c>vgm.exe</c>, <c>BEService.exe</c>,
/// <c>EasyAntiCheat.exe</c>) are absent too: they are drivers and services whose traffic is licence
/// and telemetry, not the latency-bound game stream, and splitting an anti-cheat's route from its
/// game's is a difference worth not creating.</para>
/// </summary>
public static class AppPresets
{
    /// <summary>
    /// THE GAMES. Competitive titles where latency is the product, plus <c>RiotClientServices.exe</c>,
    /// which is not a store front but a hard dependency: VALORANT and League do not start without it.
    /// </summary>
    public static readonly AppPreset Games = new(
        "games",
        "PerApp_PresetGames",
        "PerApp_PresetGamesHint",
        [
            "cs2.exe",                          // Counter-Strike 2
            "dota2.exe",                        // Dota 2
            "VALORANT-Win64-Shipping.exe",      // VALORANT (the game)
            "VALORANT.exe",                     // VALORANT (Riot's launcher shim)
            "RiotClientServices.exe",           // Riot client — required by VALORANT and League
            "League of Legends.exe",            // League of Legends (the name really does contain spaces)
            "LeagueClient.exe",                 // League client
            "LeagueClientUx.exe",               // League client UX host
            "FortniteClient-Win64-Shipping.exe",// Fortnite
            "r5apex.exe",                       // Apex Legends
            "EscapeFromTarkov.exe",             // Escape from Tarkov
            "GTA5.exe",                         // GTA V / GTA Online
            "TslGame.exe",                      // PUBG: BATTLEGROUNDS
            "RustClient.exe",                   // Rust (Facepunch's own name for the game process)
            "Overwatch.exe",                    // Overwatch 2
            "WorldOfTanks.exe",                 // World of Tanks
            "RainbowSix.exe",                   // Rainbow Six Siege
            "destiny2.exe",                     // Destiny 2
            "BF2042.exe",                       // Battlefield 2042
            "cod.exe",                          // Call of Duty (the shared HQ launcher of the modern titles)
        ]);

    /// <summary>
    /// THE STORE FRONTS, AS THEIR OWN SWITCH, and that separation is the point.
    ///
    /// Bypassing <c>steam.exe</c> takes downloads and the store out of the tunnel. The downloads are
    /// usually what a user wants — they are the biggest transfer the machine does and the tunnel only
    /// slows them. But the STORE then sees the real connection, so its region, its prices and its
    /// availability follow the real address rather than the exit. That is a decision about money and
    /// catalogue, not about latency, and it has no business riding on the switch that fixes ping —
    /// so it is a second switch, and the row says what it does.
    /// </summary>
    public static readonly AppPreset Launchers = new(
        "launchers",
        "PerApp_PresetLaunchers",
        "PerApp_PresetLaunchersHint",
        [
            "steam.exe",                // Steam
            "Battle.net.exe",           // Battle.net
            "EpicGamesLauncher.exe",    // Epic Games Launcher
            "EADesktop.exe",            // EA app
            "upc.exe",                  // Ubisoft Connect
        ]);

    public static readonly IReadOnlyList<AppPreset> All = [Games, Launchers];

    #region ownership

    private const string FileName = "departament_presets.json";
    private const string Tag = "AppPresets";

    private static readonly object _lock = new();
    private static Dictionary<string, List<string>>? _owned;

    /// <summary>
    /// What <see cref="Apply"/> added for this preset and has not handed back yet. Empty means the
    /// preset is not applied.
    /// </summary>
    public static IReadOnlyList<string> Owned(AppPreset preset)
    {
        lock (_lock)
        {
            return Load().TryGetValue(preset.Key, out var list) ? list : [];
        }
    }

    /// <summary>True when the preset currently owns at least one entry — i.e. its switch is on.</summary>
    public static bool IsApplied(AppPreset preset) => Owned(preset).Count > 0;

    /// <summary>
    /// Records the entries this preset just added, so <see cref="Release"/> can return exactly those.
    /// </summary>
    /// <param name="preset">The preset being switched on.</param>
    /// <param name="current">The selection as it stands, case-insensitive.</param>
    /// <returns>The entries to ADD — possibly empty, when everything was already selected.</returns>
    public static IReadOnlyList<string> Apply(AppPreset preset, ISet<string> current)
    {
        var added = preset.Processes.Where(p => !current.Contains(p)).ToList();
        lock (_lock)
        {
            var owned = Load();
            owned[preset.Key] = added;
            Persist();
        }
        return added;
    }

    /// <summary>
    /// The entries to REMOVE when the preset is switched off: only what <see cref="Apply"/> added.
    ///
    /// A process the user chose before applying the preset is not in the owned set, so it stays —
    /// un-applying a preset must never take away a decision the user made himself.
    /// </summary>
    public static IReadOnlyList<string> Release(AppPreset preset)
    {
        lock (_lock)
        {
            var owned = Load();
            var list = owned.TryGetValue(preset.Key, out var v) ? v : [];
            owned.Remove(preset.Key);
            Persist();
            return list;
        }
    }

    private static Dictionary<string, List<string>> Load()
    {
        if (_owned is not null)
        {
            return _owned;
        }
        try
        {
            var path = Utils.GetConfigPath(FileName);
            if (File.Exists(path))
            {
                _owned = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
            }
        }
        catch (Exception ex)
        {
            // An unreadable record reads as "no preset applied", which is the safe answer: the switch
            // shows off, and turning it on re-records what it adds. Nothing in the user's selection
            // is touched either way.
            Logging.SaveLog(Tag, ex);
        }
        return _owned ??= new Dictionary<string, List<string>>();
    }

    private static void Persist()
    {
        try
        {
            File.WriteAllText(Utils.GetConfigPath(FileName), JsonSerializer.Serialize(_owned ?? []));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    #endregion ownership
}
