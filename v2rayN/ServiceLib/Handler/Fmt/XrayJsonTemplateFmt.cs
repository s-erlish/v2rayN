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
    /// The outbound that actually carries this config's traffic.
    ///
    /// Taking the first proxy-protocol outbound is wrong for operator templates, which routinely ship
    /// several proxy outbounds and pick one with a routing rule. A template can, for instance, place a
    /// decoy outbound tagged "proxy" first and send all tcp/udp traffic to a differently-tagged one:
    /// reading the first entry then reports the wrong host, so the row shows the wrong protocol, the
    /// TCP ping probes a host that is not the server and never answers, and the delay test measures the
    /// wrong outbound entirely.
    ///
    /// So follow the routing the way the core does — the first rule that matches ordinary outbound
    /// traffic wins — and fall back to the old first-match behaviour only when routing says nothing.
    /// Port of Android's <c>V2rayConfig.getProxyOutbound()</c>.
    /// </summary>
    public static Outbounds4Ray? GetProxyOutbound(V2rayConfig? config)
    {
        if (config?.outbounds is not { Count: > 0 })
        {
            return null;
        }
        return ResolveRoutedOutbound(config) ?? FirstProxyOutbound(config);
    }

    /// <summary>First outbound whose protocol names a real proxy, ignoring routing.</summary>
    private static Outbounds4Ray? FirstProxyOutbound(V2rayConfig config)
        => config.outbounds.FirstOrDefault(o => o != null && IsProxyProtocol(o.protocol));

    private static Outbounds4Ray? OutboundByTag(V2rayConfig config, string? tag)
        => tag.IsNullOrEmpty()
            ? null
            : config.outbounds.FirstOrDefault(o => string.Equals(o?.tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Walks <c>routing</c> in core order and returns the proxy outbound ordinary traffic reaches, or
    /// <c>null</c> when no rule applies to it.
    ///
    /// Only rules that can match GENERIC traffic are considered: a rule constrained by ip, domain,
    /// port, source, user, inbound tag, process or application protocol describes a special case
    /// (private ranges, bittorrent, a bypass list), not the default path. Of the rest, the first one
    /// wins, mirroring the core's top-down evaluation.
    /// </summary>
    private static Outbounds4Ray? ResolveRoutedOutbound(V2rayConfig config)
    {
        var rules = config.routing?.rules;
        if (rules is null)
        {
            return null;
        }

        foreach (var rule in rules)
        {
            if (rule is null || !MatchesGenericTraffic(rule))
            {
                continue;
            }

            var direct = OutboundByTag(config, rule.outboundTag);
            if (direct != null)
            {
                // A rule sending everything to freedom/blackhole is a kill-switch or a bypass, not this
                // profile's server — keep looking rather than reporting "the server is freedom".
                if (!IsProxyProtocol(direct.protocol))
                {
                    continue;
                }
                return direct;
            }

            // A balancer names its members by tag prefix; any member is representative enough for the
            // display name, the ping target and the delay probe.
            var balancer = rule.balancerTag.IsNullOrEmpty()
                ? null
                : config.routing?.balancers?.FirstOrDefault(b =>
                    string.Equals(b?.tag, rule.balancerTag, StringComparison.OrdinalIgnoreCase));
            if (balancer is null)
            {
                continue;
            }

            Outbounds4Ray? member = null;
            foreach (var prefix in balancer.selector ?? [])
            {
                if (prefix.IsNullOrEmpty())
                {
                    continue;
                }
                member = config.outbounds.FirstOrDefault(o =>
                    o?.tag != null
                    && o.tag.StartsWith(prefix!, StringComparison.OrdinalIgnoreCase)
                    && IsProxyProtocol(o.protocol));
                if (member != null)
                {
                    break;
                }
            }
            if (member is null)
            {
                var fallback = OutboundByTag(config, balancer.fallbackTag);
                member = fallback != null && IsProxyProtocol(fallback.protocol) ? fallback : null;
            }
            if (member != null)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>
    /// True when this rule is not narrowed to a particular destination, source or protocol, so it
    /// applies to ordinary outbound traffic. A bare <c>network: "tcp,udp"</c> still counts — that is
    /// how templates spell "everything else".
    /// </summary>
    private static bool MatchesGenericTraffic(RulesItem4Ray rule)
    {
        if (rule.outboundTag.IsNullOrEmpty() && rule.balancerTag.IsNullOrEmpty())
        {
            return false;
        }

        return IsEmpty(rule.ip)
            && IsEmpty(rule.domain)
            && IsEmpty(rule.process)
            && IsEmpty(rule.protocol)
            && IsEmpty(rule.source)
            && IsEmpty(rule.user)
            && IsEmpty(rule.inboundTag)
            && rule.port.IsNullOrEmpty()
            && rule.sourcePort.IsNullOrEmpty()
            && rule.attrs is null;
    }

    private static bool IsEmpty(List<string>? values) => values is not { Count: > 0 };

    /// <summary>
    /// Raw-JSON twin of <see cref="GetProxyOutbound(V2rayConfig)"/>, for the call sites that work on a
    /// parsed config root rather than the typed model (the hot-swap outbound lift, the running-proxy
    /// tag capture, the batch-speedtest outbound graft). Resolves the routed outbound through the same
    /// rules and returns the ORIGINAL node, so callers keep every as-authored field. Falls back to the
    /// first proxy-protocol outbound when the config cannot be modelled or routing names nothing.
    /// </summary>
    public static JsonObject? ResolveProxyOutbound(JsonObject? root)
    {
        if (root?["outbounds"] is not JsonArray outbounds)
        {
            return null;
        }

        var candidates = outbounds
            .OfType<JsonObject>()
            .Where(o => IsProxyProtocol(GetJsonString(o, "protocol")))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var resolvedTag = GetProxyOutbound(JsonUtils.Deserialize<V2rayConfig>(root.ToJsonString()))?.tag;
        if (resolvedTag.IsNotEmpty())
        {
            var match = candidates.FirstOrDefault(o =>
                string.Equals(GetJsonString(o, "tag"), resolvedTag, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                return match;
            }
        }

        return candidates[0];
    }

    private static string? GetJsonString(JsonObject obj, string key)
        => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

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
