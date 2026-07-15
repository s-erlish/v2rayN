namespace ServiceLib.Handler.Fmt;

/// <summary>
/// Typed importer for Xray JSON templates (the "XRAY_JSON" body a Remnawave / departament panel
/// returns for a subscription — an array of full Xray configs, or a single config object).
///
/// Instead of storing each element as an opaque <see cref="EConfigType.Custom"/> blob (which breaks
/// protocol display, ping and traffic routing on Desktop), this extracts the single proxy OUTBOUND
/// from every element and builds a real typed <see cref="ProfileItem"/> (VLESS / VMess / Trojan /
/// Shadowsocks / SOCKS / HTTP). The result behaves exactly like a <c>vless://</c> link import: the
/// app injects its own inbound + system-proxy coordination at connect time, ping works, and the row
/// shows the real protocol/transport.
///
/// Mirrors Android's <c>getProxyOutbound()</c> + <c>CustomFmt.parse</c> semantics
/// (V2rayNG: handler/AngConfigManager.parseCustomConfigServer, dto/V2rayConfig.getProxyOutbound).
/// </summary>
public class XrayJsonTemplateFmt : BaseFmt
{
    // Proxy outbound protocol (lower-case) -> typed EConfigType. Non-proxy outbounds
    // (freedom / blackhole / dns / direct / loopback ...) are intentionally excluded.
    private static readonly Dictionary<string, EConfigType> _protocolMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "vless", EConfigType.VLESS },
        { "vmess", EConfigType.VMess },
        { "trojan", EConfigType.Trojan },
        { "shadowsocks", EConfigType.Shadowsocks },
        { "socks", EConfigType.SOCKS },
        { "http", EConfigType.HTTP },
    };

    /// <summary>
    /// Parse an Xray JSON template into typed profiles.
    /// Returns <c>null</c> when the body is not an Xray JSON template with at least one identifiable
    /// proxy outbound (so callers fall through to the sing-box / clash / custom parsers). When it is,
    /// every element is returned as a typed <see cref="ProfileItem"/>; an element with no identifiable
    /// proxy outbound is returned as a <see cref="EConfigType.Custom"/> fallback (its raw JSON already
    /// written to a temp file at <see cref="ProfileItem.Address"/>).
    /// </summary>
    public static List<ProfileItem>? ResolveTypedProfiles(string strData, string? subRemarks)
    {
        if (strData.IsNullOrEmpty())
        {
            return null;
        }

        var trimmed = strData.TrimEx();
        if (!(trimmed.StartsWith('{') || trimmed.StartsWith('['))
            || !strData.Contains("outbounds"))
        {
            return null;
        }

        // Split into per-config element JSON strings (array of configs, or a single config object).
        List<string> elements = [];
        if (trimmed.StartsWith('['))
        {
            var arr = JsonUtils.Deserialize<object[]>(strData);
            if (arr is not { Length: > 0 })
            {
                return null;
            }
            elements.AddRange(arr.Select(o => JsonUtils.Serialize(o)));
        }
        else
        {
            elements.Add(strData);
        }

        // First pass: try typed extraction per element (no side effects yet).
        var typedItems = new List<ProfileItem?>(elements.Count);
        var anyTyped = false;
        var index = 0;
        foreach (var element in elements)
        {
            index++;
            ProfileItem? typed = null;
            try
            {
                typed = ResolveElement(element, subRemarks, index);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("XrayJsonTemplateFmt", ex);
            }
            if (typed != null)
            {
                anyTyped = true;
            }
            typedItems.Add(typed);
        }

        // Not an Xray typed template (e.g. a sing-box config, which uses "type" not "protocol"):
        // return null so the sing-box / clash parsers get their turn. No temp files are written.
        if (!anyTyped)
        {
            return null;
        }

        // Second pass: materialize. Elements with no identifiable proxy outbound are kept as a Custom
        // fallback so the server is not silently lost — WriteAllText stores the raw element to a temp
        // file that AddCustomServer later copies into the app config directory.
        var results = new List<ProfileItem>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            results.Add(typedItems[i] ?? new ProfileItem
            {
                ConfigType = EConfigType.Custom,
                CoreType = ECoreType.Xray,
                Address = WriteAllText(elements[i]),
                Remarks = subRemarks ?? "custom",
            });
        }
        return results;
    }

    private static ProfileItem? ResolveElement(string element, string? subRemarks, int index)
    {
        var config = JsonUtils.Deserialize<V2rayConfig>(element);
        var outbound = GetProxyOutbound(config);
        if (outbound == null || !_protocolMap.TryGetValue(outbound.protocol ?? string.Empty, out var configType))
        {
            return null;
        }

        var item = new ProfileItem
        {
            ConfigType = configType,
            CoreType = ECoreType.Xray,
            Remarks = FirstNonEmpty(config?.remarks, subRemarks, outbound.tag, $"custom_{index}"),
        };

        var settings = outbound.settings;
        switch (configType)
        {
            case EConfigType.VLESS:
            case EConfigType.VMess:
                {
                    var vnext = settings?.vnext?.FirstOrDefault();
                    if (vnext == null)
                    {
                        return null;
                    }
                    item.Address = vnext.address;
                    item.Port = vnext.port;
                    var user = vnext.users?.FirstOrDefault();
                    item.Password = user?.id ?? string.Empty;
                    if (configType == EConfigType.VLESS)
                    {
                        var encryption = user?.encryption;
                        item.SetProtocolExtra(item.GetProtocolExtra() with
                        {
                            VlessEncryption = encryption.IsNullOrEmpty() ? Global.None : encryption,
                            Flow = user?.flow ?? string.Empty,
                        });
                    }
                    else
                    {
                        var security = user?.security;
                        item.SetProtocolExtra(item.GetProtocolExtra() with
                        {
                            VmessSecurity = security.IsNullOrEmpty() ? Global.DefaultSecurity : security,
                            AlterId = (user?.alterId ?? 0).ToString(),
                        });
                    }
                    break;
                }
            case EConfigType.Trojan:
                {
                    var srv = settings?.servers?.FirstOrDefault();
                    if (srv == null)
                    {
                        return null;
                    }
                    item.Address = srv.address;
                    item.Port = srv.port;
                    item.Password = srv.password ?? string.Empty;
                    if (srv.flow.IsNotEmpty())
                    {
                        item.SetProtocolExtra(item.GetProtocolExtra() with { Flow = srv.flow });
                    }
                    break;
                }
            case EConfigType.Shadowsocks:
                {
                    var srv = settings?.servers?.FirstOrDefault();
                    if (srv == null)
                    {
                        return null;
                    }
                    item.Address = srv.address;
                    item.Port = srv.port;
                    item.Password = srv.password ?? string.Empty;
                    item.SetProtocolExtra(item.GetProtocolExtra() with { SsMethod = srv.method });
                    break;
                }
            case EConfigType.SOCKS:
                {
                    var srv = settings?.servers?.FirstOrDefault();
                    if (srv == null)
                    {
                        return null;
                    }
                    item.Address = srv.address;
                    item.Port = srv.port;
                    var user = srv.users?.FirstOrDefault();
                    item.Username = user?.user ?? string.Empty;
                    item.Password = user?.pass ?? string.Empty;
                    break;
                }
            case EConfigType.HTTP:
                {
                    item.Address = settings?.address?.ToString() ?? string.Empty;
                    item.Port = settings?.port ?? 0;
                    item.Username = settings?.user ?? string.Empty;
                    item.Password = settings?.pass ?? string.Empty;
                    break;
                }
        }

        if (item.Address.IsNullOrEmpty() || item.Port <= 0)
        {
            return null;
        }

        ApplyStreamSettings(item, outbound.streamSettings);
        return item;
    }

    private static void ApplyStreamSettings(ProfileItem item, StreamSettings4Ray? stream)
    {
        if (stream == null)
        {
            item.Network = Global.DefaultNetwork;
            return;
        }

        // Network: xray's classic "tcp" is the app's "raw"; unknown networks are sanitized to the
        // default by AddServerCommon downstream.
        var network = stream.network.IsNullOrEmpty() ? Global.DefaultNetwork : stream.network;
        if (network == Global.RawNetworkAlias)
        {
            network = nameof(ETransport.raw);
        }
        item.Network = network;

        // Security ("tls" / "reality" / "none"). AddServerCommon clears anything that isn't
        // tls/reality, so "none"/empty collapses to no security.
        item.StreamSecurity = stream.security ?? string.Empty;

        var tls = stream.tlsSettings;
        if (tls != null)
        {
            item.Sni = tls.serverName ?? string.Empty;
            item.Alpn = tls.alpn is { Count: > 0 } ? string.Join(",", tls.alpn) : string.Empty;
            item.Fingerprint = tls.fingerprint ?? string.Empty;
            item.EchConfigList = tls.echConfigList ?? string.Empty;
            item.VerifyPeerCertByName = tls.verifyPeerCertByName ?? string.Empty;
            item.CertSha = tls.pinnedPeerCertSha256 ?? string.Empty;
            item.AllowInsecure = tls.allowInsecure switch
            {
                true => Global.StringTrue,
                false => Global.StringFalse,
                _ => string.Empty,
            };
        }

        var reality = stream.realitySettings;
        if (reality != null)
        {
            item.Sni = reality.serverName ?? string.Empty;
            item.Fingerprint = reality.fingerprint ?? string.Empty;
            item.PublicKey = reality.publicKey ?? string.Empty;
            item.ShortId = reality.shortId ?? string.Empty;
            item.SpiderX = reality.spiderX ?? string.Empty;
            item.Mldsa65Verify = reality.mldsa65Verify ?? string.Empty;
        }

        var transport = item.GetTransportExtra();
        switch (item.Network)
        {
            case nameof(ETransport.ws):
                transport = transport with
                {
                    Host = stream.wsSettings?.host ?? string.Empty,
                    Path = stream.wsSettings?.path ?? string.Empty,
                };
                break;

            case nameof(ETransport.httpupgrade):
                transport = transport with
                {
                    Host = stream.httpupgradeSettings?.host ?? string.Empty,
                    Path = stream.httpupgradeSettings?.path ?? string.Empty,
                };
                break;

            case nameof(ETransport.xhttp):
                transport = transport with
                {
                    Host = stream.xhttpSettings?.host ?? string.Empty,
                    Path = stream.xhttpSettings?.path ?? string.Empty,
                    XhttpMode = stream.xhttpSettings?.mode ?? string.Empty,
                };
                break;

            case nameof(ETransport.grpc):
                transport = transport with
                {
                    GrpcAuthority = stream.grpcSettings?.authority ?? string.Empty,
                    GrpcServiceName = stream.grpcSettings?.serviceName ?? string.Empty,
                    GrpcMode = stream.grpcSettings?.multiMode == true ? Global.GrpcMultiMode : Global.GrpcGunMode,
                };
                break;

            case nameof(ETransport.kcp):
                transport = transport with
                {
                    KcpHeaderType = stream.kcpSettings != null ? Global.None : transport.KcpHeaderType,
                };
                break;

            case nameof(ETransport.raw):
                transport = transport with
                {
                    RawHeaderType = stream.rawSettings?.header?.type ?? Global.None,
                };
                break;
        }
        item.SetTransportExtra(transport);
    }

    private static Outbounds4Ray? GetProxyOutbound(V2rayConfig? config)
    {
        if (config?.outbounds is not { Count: > 0 })
        {
            return null;
        }
        return config.outbounds.FirstOrDefault(o => o.protocol.IsNotEmpty() && _protocolMap.ContainsKey(o.protocol));
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (value.IsNotEmpty())
            {
                return value!;
            }
        }
        return "custom";
    }
}
