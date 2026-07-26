namespace ServiceLib.Handler.Fmt;

/// <summary>
/// Introspection helper for CUSTOM (raw xray-json / Remnawave "XRAY_JSON" template) profiles.
///
/// A departament/Remnawave subscription element is a FULL Xray config (its own routing rules + dns +
/// outbounds). We deliberately store it AS-IS as an <see cref="EConfigType.Custom"/> node so the
/// provider's routing/ad-block/geo rules are preserved and applied at connect time (the faithful
/// Android way). To still show the real protocol/transport on the row and to make ping work, this
/// helper peeks at the stored JSON and reports the wrapped proxy outbound's protocol, transport
/// network, security and real server address/port.
///
/// Mirrors Android's <c>V2rayConfig.getProxyOutbound()</c> + <c>MainRecyclerAdapter.customProtoInfo</c>.
/// </summary>
public class XrayJsonTemplateFmt : BaseFmt
{
    // Real proxy outbound protocols (lower-case). Helper outbounds (freedom / blackhole / dns /
    // direct / loopback / block) are intentionally excluded so we always report the actual server.
    private static readonly HashSet<string> _proxyProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "vmess", "trojan", "shadowsocks", "socks", "http",
    };

    /// <summary>Protocol / transport / server parsed from a CUSTOM node's wrapped proxy outbound.</summary>
    public record CustomProtoInfo(string Protocol, string? Network, string? Security, string? Address, int Port);

    // Parsing the stored raw config on every list rebuild would be wasteful, so cache per file path.
    // A stored custom config is written once to a unique file, so its content is immutable per path;
    // a null result is cached too (config has no identifiable proxy outbound → show "CUSTOM").
    private static readonly ConcurrentDictionary<string, CustomProtoInfo?> _cache = new();

    /// <summary>
    /// Introspect a CUSTOM node by reading its stored JSON file (<see cref="ProfileItem.Address"/>).
    /// Returns <c>null</c> for non-custom nodes or when no proxy outbound can be identified.
    /// </summary>
    public static CustomProtoInfo? Introspect(ProfileItem? node)
        => node == null ? null : IntrospectByAddress(node.Address, node.ConfigType);

    /// <summary>Introspect a CUSTOM row model (server-list build path).</summary>
    public static CustomProtoInfo? Introspect(ProfileItemModel? node)
        => node == null ? null : IntrospectByAddress(node.Address, node.ConfigType);

    private static CustomProtoInfo? IntrospectByAddress(string? address, EConfigType configType)
    {
        if (configType != EConfigType.Custom)
        {
            return null;
        }

        var path = ResolveAddressPath(address);
        if (path == null)
        {
            return null;
        }

        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        CustomProtoInfo? info = null;
        try
        {
            info = IntrospectFromJson(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Logging.SaveLog("XrayJsonTemplateFmt", ex);
        }
        _cache[path] = info;
        return info;
    }

    /// <summary>
    /// Introspect a raw Xray-json string. Returns <c>null</c> when it is not a JSON object with an
    /// identifiable proxy outbound.
    /// </summary>
    public static CustomProtoInfo? IntrospectFromJson(string? raw)
    {
        if (raw.IsNullOrEmpty())
        {
            return null;
        }

        V2rayConfig? config;
        try
        {
            config = JsonUtils.Deserialize<V2rayConfig>(raw);
        }
        catch
        {
            return null;
        }

        var outbound = GetProxyOutbound(config);
        if (outbound == null)
        {
            return null;
        }

        // xray's classic "tcp" network is the app's "raw" (typed nodes store "raw" too), so map it
        // for a consistent transport chip. Everything else is passed through as-authored.
        var network = outbound.streamSettings?.network;
        if (network == Global.RawNetworkAlias)
        {
            network = Global.DefaultNetwork;
        }

        var (address, port) = GetOutboundServer(outbound);
        return new CustomProtoInfo(
            outbound.protocol ?? string.Empty,
            network,
            outbound.streamSettings?.security,
            address,
            port);
    }

    /// <summary>
    /// The first real proxy outbound of a config (skips freedom / blackhole / dns / direct helpers),
    /// mirroring Android's <c>getProxyOutbound()</c>.
    /// </summary>
    public static Outbounds4Ray? GetProxyOutbound(V2rayConfig? config)
    {
        if (config?.outbounds is not { Count: > 0 })
        {
            return null;
        }
        return config.outbounds.FirstOrDefault(o => o.protocol.IsNotEmpty() && _proxyProtocols.Contains(o.protocol));
    }

    /// <summary>True when the protocol names a real proxy outbound (vless/vmess/trojan/ss/socks/http).</summary>
    public static bool IsProxyProtocol(string? protocol) => protocol.IsNotEmpty() && _proxyProtocols.Contains(protocol!);

    private static (string? address, int port) GetOutboundServer(Outbounds4Ray outbound)
    {
        var settings = outbound.settings;
        if (settings == null)
        {
            return (null, 0);
        }

        var vnext = settings.vnext?.FirstOrDefault();
        if (vnext != null)
        {
            return (vnext.address, vnext.port);
        }

        var server = settings.servers?.FirstOrDefault();
        if (server != null)
        {
            return (server.address, server.port);
        }

        if (settings.address != null)
        {
            return (settings.address.ToString(), settings.port ?? 0);
        }

        return (null, 0);
    }

    private static string? ResolveAddressPath(string? address)
    {
        if (address.IsNullOrEmpty())
        {
            return null;
        }
        if (File.Exists(address))
        {
            return address;
        }
        var p = Utils.GetConfigPath(address);
        return File.Exists(p) ? p : null;
    }
}
