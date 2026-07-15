using ServiceLib.Enums;
using ServiceLib.Models.Dto;

namespace v2rayN.Desktop.Common;

/// <summary>
/// Presentation helpers that turn a real <see cref="ProfileItemModel"/> into the Incy row/hero
/// strings (protocol chip + transport line). Single source of truth shared by the server-list
/// converters and the connect-hero code-behind — so protocol/transport are derived from live
/// engine data, never hardcoded.
/// </summary>
public static class ProfileDisplay
{
    /// <summary>Protocol chip text: <see cref="EConfigType"/> → upper-case token (VLESS, SHADOWSOCKS, …).</summary>
    public static string Protocol(EConfigType configType) => configType.ToString().ToUpperInvariant();

    /// <summary>Transport line: "TCP · REALITY" from Network + StreamSecurity (empty security → NONE).</summary>
    public static string Transport(string? network, string? streamSecurity)
    {
        var net = string.IsNullOrWhiteSpace(network) ? "TCP" : network.Trim().ToUpperInvariant();
        var sec = string.IsNullOrWhiteSpace(streamSecurity) ? "NONE" : streamSecurity.Trim().ToUpperInvariant();
        return $"{net} · {sec}";
    }

    /// <summary>Transport line for a whole profile row.</summary>
    public static string Transport(ProfileItemModel? item) => item == null ? string.Empty : Transport(item.Network, item.StreamSecurity);
}
