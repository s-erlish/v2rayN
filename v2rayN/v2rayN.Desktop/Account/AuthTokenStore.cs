using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using v2rayN.Desktop.Account.Dto;

namespace v2rayN.Desktop.Account;

/// <summary>
/// Encrypted-at-rest store for the app session JWT (7-day, no refresh), the cached user profile, a
/// stable device id (HWID), and the uuid-&gt;guid map of subscriptions we manage. Port of V2rayNG
/// auth/AuthTokenStore.kt (MMKV + Android Keystore) using an AES-encrypted file under guiConfigs.
///
/// The AES-256 key is derived from a machine-stable secret (the Windows MachineGuid, else the Linux
/// machine-id, else the machine/user name) so the blob decrypts across restarts on the same machine
/// but is opaque if copied elsewhere. Tokens and subscription URLs are never logged.
/// </summary>
public static class AuthTokenStore
{
    private const string FileName = "departament_auth.dat";
    private const string Tag = "AuthTokenStore";

    private static readonly object _lock = new();
    private static StoreData? _data;
    private static byte[]? _key;
    private static string? _machineSeed;

    /// <summary>
    /// The device id held for THIS PROCESS ONLY. It is what keeps an unwritable fallback out of the
    /// file: a guess made because the machine would not answer must not outlive the process that
    /// made it, or the next launch inherits it instead of deriving the real one.
    /// </summary>
    private static string? _cachedDeviceId;

    private sealed class StoreData
    {
        public string? Token { get; set; }
        public long ExpiresAt { get; set; }
        public string? UserJson { get; set; }
        public string? DeviceId { get; set; }
        public Dictionary<string, string> ManagedGuids { get; set; } = new();
    }

    #region public API

    /// <summary>
    /// Stable per-machine HWID (32 lowercase hex) that SURVIVES UNINSTALL/REINSTALL, computed once and
    /// reused. Port of the Android fix in <c>auth/AuthTokenStore.kt</c> (<c>1c1d890</c>); read this
    /// before changing it, because the phone shipped the wrong answer here once and the owner has said
    /// the same defect applies to this client — «это касается и пк версии».
    ///
    /// <para><b>What it is keyed on.</b> A value the OPERATING SYSTEM owns, never one this app mints:
    /// <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c> on Windows, <c>/etc/machine-id</c>
    /// (then <c>/var/lib/dbus/machine-id</c>) on Linux, <c>IOPlatformUUID</c> from
    /// <c>IOPlatformExpertDevice</c> on macOS. None of them lives in the app's own directory, so
    /// uninstalling — or wiping <c>guiConfigs</c> — does not touch it: a clean reinstall on the same
    /// machine derives the SAME HWID and the subscription keeps ONE device slot. Each of them dies
    /// with the OS install, which is correct: a reimaged machine IS a different device.</para>
    ///
    /// <para><b>Why the previous one was not stable.</b> The old code fell through to
    /// <c>Guid.NewGuid()</c> whenever the platform value read back blank AND PERSISTED IT. On macOS
    /// that was EVERY install — the reader only knew Windows and Linux — so every reinstall of the
    /// desktop client burned another slot against <c>hwidDeviceLimit</c>, exactly the nine-rows-for-one-
    /// device failure the owner hit on Android. A fallback is now in-memory only (see
    /// <see cref="_cachedDeviceId"/>) and is no longer random.</para>
    ///
    /// <para><b>Migration.</b> An id already on disk ALWAYS wins, whatever it was keyed on and before
    /// any derivation runs. An install that upgrades into this build keeps the identity the panel
    /// already knows and does NOT appear as yet another new device; the derivation below only runs when
    /// there is nothing to carry forward, i.e. on a genuinely fresh install.</para>
    ///
    /// <para><b>No elevation, no new permission.</b> All three sources are world-readable to a normal
    /// user process.</para>
    ///
    /// <para>The desktop legitimately registers as a SEPARATE device from the phone — same account,
    /// two machines — and the plan's device limit has to account for both. That is not a bug.</para>
    /// </summary>
    public static string DeviceId()
    {
        lock (_lock)
        {
            var data = Data();
            // MIGRATION FIRST, ALWAYS. Whatever this install has been telling the panel, it keeps
            // telling it. Re-deriving over a stored id would make every update a new device — the same
            // defect as a reinstall, just triggered by us instead of by the user.
            if (data.DeviceId.IsNotEmpty())
            {
                _cachedDeviceId = data.DeviceId;
                return data.DeviceId!;
            }
            // Held from earlier in this process: either a fallback (never written) or a derived id
            // whose write failed. Either way the process must not change identity mid-run.
            if (_cachedDeviceId.IsNotEmpty())
            {
                return _cachedDeviceId!;
            }
            var (id, derived) = ComputeDeviceId();
            _cachedDeviceId = id;
            // ONLY A DERIVED ID IS WRITTEN DOWN. A derived id is a pure function of the machine, so
            // persisting it is free — the same value comes back next launch anyway. A fallback is a
            // guess made because the machine would not answer, and persisting THAT is what burns a
            // device slot forever: one unlucky first launch and the install is pinned to a value no
            // later launch can correct.
            if (derived)
            {
                data.DeviceId = id;
                Persist();
            }
            return id;
        }
    }

    /// <summary>Persists a new session. No refresh token in this backend.</summary>
    public static void SaveSession(string token, long? expiresAt = null, UserProfileDto? user = null)
    {
        lock (_lock)
        {
            var data = Data();
            data.Token = token;
            data.ExpiresAt = expiresAt ?? 0L;
            data.UserJson = user != null ? JsonSerializer.Serialize(user, ApiJson.Options) : null;
            Persist();
        }
    }

    /// <summary>Updates just the cached user profile (keeps the current token).</summary>
    public static void SaveUser(UserProfileDto user)
    {
        lock (_lock)
        {
            Data().UserJson = JsonSerializer.Serialize(user, ApiJson.Options);
            Persist();
        }
    }

    public static string? GetToken()
    {
        lock (_lock)
        {
            return Data().Token;
        }
    }

    public static long GetExpiresAt()
    {
        lock (_lock)
        {
            return Data().ExpiresAt;
        }
    }

    public static UserProfileDto? GetUser()
    {
        lock (_lock)
        {
            var json = Data().UserJson;
            if (json.IsNullOrEmpty())
            {
                return null;
            }
            try
            {
                return JsonSerializer.Deserialize<UserProfileDto>(json!, ApiJson.Options);
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool IsLoggedIn()
    {
        lock (_lock)
        {
            return Data().Token.IsNotEmpty();
        }
    }

    /// <summary>uuid -&gt; local subscription guid map of subscriptions owned by the auth flow.</summary>
    public static Dictionary<string, string> GetManagedGuids()
    {
        lock (_lock)
        {
            return new Dictionary<string, string>(Data().ManagedGuids);
        }
    }

    public static void SetManagedGuids(IDictionary<string, string> map)
    {
        lock (_lock)
        {
            Data().ManagedGuids = new Dictionary<string, string>(map);
            Persist();
        }
    }

    /// <summary>Clears the session (logout / 401). Keeps deviceId; drops managed-sub references.</summary>
    public static void Clear()
    {
        lock (_lock)
        {
            var data = Data();
            data.Token = null;
            data.ExpiresAt = 0L;
            data.UserJson = null;
            data.ManagedGuids = new Dictionary<string, string>();
            Persist();
        }
    }

    #endregion public API

    #region persistence

    private static StoreData Data() => _data ??= Load();

    private static StoreData Load()
    {
        try
        {
            var path = Utils.GetConfigPath(FileName);
            if (!File.Exists(path))
            {
                return new StoreData();
            }
            var blob = File.ReadAllBytes(path);
            var json = Decrypt(blob);
            if (json.IsNullOrEmpty())
            {
                return new StoreData();
            }
            return JsonSerializer.Deserialize<StoreData>(json!) ?? new StoreData();
        }
        catch
        {
            // Corrupt/undecryptable (e.g. machine changed): start fresh rather than crash.
            return new StoreData();
        }
    }

    private static void Persist()
    {
        try
        {
            var json = JsonSerializer.Serialize(_data ?? new StoreData());
            var blob = Encrypt(json);
            File.WriteAllBytes(Utils.GetConfigPath(FileName), blob);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(Tag, ex);
        }
    }

    #endregion persistence

    #region crypto + machine identity

    private static byte[] Key => _key ??= SHA256.HashData(Encoding.UTF8.GetBytes("departament-vpn|auth|v1|" + MachineSeed()));

    private static byte[] Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = Key;
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = enc.TransformFinalBlock(pt, 0, pt.Length);
        var result = new byte[aes.IV.Length + ct.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(ct, 0, result, aes.IV.Length, ct.Length);
        return result;
    }

    private static string? Decrypt(byte[] blob)
    {
        if (blob.Length <= 16)
        {
            return null;
        }
        using var aes = Aes.Create();
        aes.Key = Key;
        var iv = new byte[16];
        Buffer.BlockCopy(blob, 0, iv, 0, 16);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        var pt = dec.TransformFinalBlock(blob, 16, blob.Length - 16);
        return Encoding.UTF8.GetString(pt);
    }

    /// <summary>
    /// Namespaces the two derivations below so they can never collide on one machine, and matches the
    /// Android salt shape (<c>departament-hwid-v1|&lt;source&gt;|</c>) so both clients mint ids of the
    /// same 32-lowercase-hex form the panel stores.
    /// </summary>
    private const string IdSaltMachine = "departament-hwid-v1|machine_id|";

    private const string IdSaltAttrs = "departament-hwid-v1|attrs|";

    /// <summary>
    /// The OS-owned machine id first; a digest of stable machine attributes when the OS will not
    /// answer; a random uuid only when even MD5 is unavailable — and that last one is flagged
    /// <c>derived: false</c> so it is never written to disk.
    ///
    /// The middle tier matters more than it looks. It is not guaranteed unique between two machines,
    /// but it IS identical across reinstalls of the same machine — and since the panel scopes device
    /// entries to one subscription, "the same PC keeps one slot" is worth far more than "two machines
    /// with the same name and user would collide", which additionally requires them to share an
    /// account. A random GUID has neither property.
    /// </summary>
    private static (string Id, bool Derived) ComputeDeviceId()
    {
        var machineId = ReadPlatformMachineId();
        if (machineId.IsNotEmpty())
        {
            var hex = Md5Hex(IdSaltMachine + machineId);
            if (hex != null)
            {
                return (hex, true);
            }
        }
        var attrs = Md5Hex(IdSaltAttrs + MachineFingerprint());
        if (attrs != null)
        {
            return (attrs, true);
        }
        return (Guid.NewGuid().ToString("N"), false);
    }

    /// <summary>
    /// The machine's own unchanging description, used only when the OS id is unreadable. Every field
    /// survives an app reinstall and dies with the OS install — the same lifetime the primary source
    /// has — and not one of them is random, which is the whole point of this tier.
    /// </summary>
    private static string MachineFingerprint() => string.Join("|",
        Environment.MachineName,
        Environment.UserName,
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "other",
        RuntimeInformation.OSArchitecture.ToString(),
        Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));

    /// <summary>MD5 as 32 lowercase hex chars — the UUID-without-dashes shape the panel stores.</summary>
    private static string? Md5Hex(string input)
    {
        try
        {
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The OS-owned machine id: Windows MachineGuid, Linux machine-id, macOS IOPlatformUUID.
    ///
    /// Deliberately NOT the same function as <see cref="ReadMachineGuid"/>, which seeds the AES key and
    /// therefore must keep answering exactly what it answered when a given store was written — teaching
    /// THAT one about macOS would change the key on every Mac and make every existing store
    /// undecryptable, taking the stored device id down with it.
    /// </summary>
    private static string? ReadPlatformMachineId()
    {
        var guid = ReadMachineGuid();
        if (guid.IsNotEmpty())
        {
            return guid;
        }
        return OperatingSystem.IsMacOS() ? ReadMacPlatformUuid() : null;
    }

    /// <summary>
    /// <c>IOPlatformUUID</c> from the IOKit registry — the Mac's own hardware id, unchanged by an OS
    /// user wiping the app. Read through <c>ioreg</c> rather than a P/Invoke into IOKit so the call
    /// costs nothing on the other two platforms and cannot fail the build there.
    /// </summary>
    private static string? ReadMacPlatformUuid()
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/sbin/ioreg",
                Arguments = "-rd1 -c IOPlatformExpertDevice",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null)
            {
                return null;
            }
            var output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000))
            {
                return null;
            }
            // Line shape: `    "IOPlatformUUID" = "564D…-…"`
            const string marker = "\"IOPlatformUUID\"";
            var at = output.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                return null;
            }
            var open = output.IndexOf('"', output.IndexOf('=', at + marker.Length) + 1);
            var close = open < 0 ? -1 : output.IndexOf('"', open + 1);
            return close > open ? output[(open + 1)..close].Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string MachineSeed()
    {
        if (_machineSeed != null)
        {
            return _machineSeed;
        }
        var guid = ReadMachineGuid();
        _machineSeed = guid.IsNotEmpty() ? guid! : $"{Environment.MachineName}|{Environment.UserName}";
        return _machineSeed;
    }

    /// <summary>
    /// Raw machine identifier: Windows MachineGuid, then Linux machine-id. Null when unavailable.
    ///
    /// FROZEN — this is the AES key seed (<see cref="MachineSeed"/>). Widening it to a platform it did
    /// not previously cover changes the key on that platform and makes every store already written
    /// there undecryptable. New sources belong in <see cref="ReadPlatformMachineId"/>.
    /// </summary>
    private static string? ReadMachineGuid()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var sub = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                if (sub?.GetValue("MachineGuid") is string val && val.IsNotEmpty())
                {
                    return val;
                }
            }
            else
            {
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                {
                    if (File.Exists(path))
                    {
                        var id = File.ReadAllText(path).Trim();
                        if (id.IsNotEmpty())
                        {
                            return id;
                        }
                    }
                }
            }
        }
        catch
        {
            // ignore — fall back to the machine/user-name seed
        }
        return null;
    }

    #endregion crypto + machine identity
}
