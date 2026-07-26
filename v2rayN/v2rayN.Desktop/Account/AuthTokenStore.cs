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
    /// Stable per-device HWID (32 lowercase hex) that survives reinstall, computed once and reused.
    /// First run derives it from MD5(MachineGuid) to match the UUID-without-dashes format the backend
    /// expects, so the panel keeps a single device slot per physical machine. Random uuid fallback.
    /// </summary>
    public static string DeviceId()
    {
        lock (_lock)
        {
            var data = Data();
            if (data.DeviceId.IsNotEmpty())
            {
                return data.DeviceId!;
            }
            var generated = ComputeStableDeviceId();
            data.DeviceId = generated;
            Persist();
            return generated;
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

    private static string ComputeStableDeviceId()
    {
        var guid = ReadMachineGuid();
        if (guid.IsNullOrEmpty())
        {
            return Guid.NewGuid().ToString("N");
        }
        try
        {
            var bytes = MD5.HashData(Encoding.UTF8.GetBytes(guid!));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
        catch
        {
            return Guid.NewGuid().ToString("N");
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

    /// <summary>Raw machine identifier: Windows MachineGuid, then Linux machine-id. Null when unavailable.</summary>
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
