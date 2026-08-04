using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Handler.Fmt;
using ServiceLib.Models.CoreConfigs;
using Xunit;

namespace ServiceLib.Tests.Fmt;

/// <summary>
/// <see cref="XrayJsonTemplateFmt.GetProxyOutbound"/> must name the outbound that actually carries
/// traffic.
///
/// Operator templates ship several proxy outbounds and choose one with a routing rule, so reading the
/// first proxy-protocol entry can name a decoy. Everything downstream then reads the wrong server: the
/// row shows the decoy's protocol and transport, the TCP ping probes a host that is not the server and
/// never answers — so the profile looks unpingable — and the delay test promotes the decoy and measures
/// it after stripping routing.
///
/// Port of Android's <c>ProxyOutboundResolutionTest</c>. The two configs below are the reduced but
/// faithful shapes of a template that worked and one that did not, which is what put this defect on
/// the list.
/// </summary>
public class ProxyOutboundResolutionTests
{
    private static V2rayConfig Parse(string json)
    {
        var config = JsonUtils.Deserialize<V2rayConfig>(json);
        config.Should().NotBeNull("the fixture must model cleanly, otherwise the test proves nothing");
        return config!;
    }

    /// <summary>Traffic goes to the tag the routing rule names, not to the first proxy outbound in the array.</summary>
    [Fact]
    public void RoutingRuleWinsOverTheFirstProxyOutbound()
    {
        var config = Parse(
            """
            {
              "routing": {
                "domainStrategy": "IPIfNonMatch",
                "rules": [
                  { "type": "field", "protocol": ["bittorrent"], "outboundTag": "block" },
                  { "type": "field", "ip": ["geoip:private"], "outboundTag": "direct" },
                  { "type": "field", "domain": ["geosite:private"], "outboundTag": "direct" },
                  { "type": "field", "network": "tcp,udp", "outboundTag": "proxy-rum1lk2" }
                ]
              },
              "outbounds": [
                { "tag": "proxy", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "web.max.ru", "port": 443,
                    "users": [ { "id": "fd9151b1-da58-493e-b830-e2037d7b66e6", "encryption": "none" } ] } ] },
                  "streamSettings": { "network": "tcp", "security": "reality" } },
                { "tag": "proxy-rum1lk2", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "185.91.54.248", "port": 44444,
                    "users": [ { "id": "4f69ad29-f49a-4032-b9bb-888663ff8296", "encryption": "none" } ] } ] },
                  "streamSettings": { "network": "grpc", "security": "reality" } },
                { "tag": "direct", "protocol": "freedom" },
                { "tag": "block", "protocol": "blackhole" }
              ]
            }
            """);

        var outbound = XrayJsonTemplateFmt.GetProxyOutbound(config);

        outbound.Should().NotBeNull("a routed proxy outbound must be resolved");
        outbound!.tag.Should().Be("proxy-rum1lk2");
        outbound.settings.vnext![0].address.Should().Be("185.91.54.248");
        outbound.settings.vnext![0].port.Should().Be(44444);
    }

    /// <summary>The row/ping/delay surfaces read the routed outbound's transport, not the decoy's.</summary>
    [Fact]
    public void IntrospectionReportsTheRoutedOutboundNotTheDecoy()
    {
        var info = XrayJsonTemplateFmt.IntrospectFromJson(
            """
            {
              "routing": {
                "rules": [
                  { "type": "field", "ip": ["geoip:private"], "outboundTag": "direct" },
                  { "type": "field", "network": "tcp,udp", "outboundTag": "proxy-rum1lk2" }
                ]
              },
              "outbounds": [
                { "tag": "proxy", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "web.max.ru", "port": 443,
                    "users": [ { "id": "fd9151b1-da58-493e-b830-e2037d7b66e6", "encryption": "none" } ] } ] },
                  "streamSettings": { "network": "tcp", "security": "reality" } },
                { "tag": "proxy-rum1lk2", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "185.91.54.248", "port": 44444,
                    "users": [ { "id": "4f69ad29-f49a-4032-b9bb-888663ff8296", "encryption": "none" } ] } ] },
                  "streamSettings": { "network": "grpc", "security": "reality" } },
                { "tag": "direct", "protocol": "freedom" }
              ]
            }
            """);

        info.Should().NotBeNull();
        info!.Address.Should().Be("185.91.54.248");
        info.Port.Should().Be(44444);
        info.Network.Should().Be("grpc");
    }

    /// <summary>The ordinary single-proxy template keeps resolving to its one outbound.</summary>
    [Fact]
    public void SingleProxyOutboundIsUnaffected()
    {
        var config = Parse(
            """
            {
              "routing": {
                "domainStrategy": "IPIfNonMatch",
                "rules": [
                  { "type": "field", "ip": ["geoip:private"], "outboundTag": "direct" },
                  { "type": "field", "network": "tcp,udp", "outboundTag": "proxy" }
                ]
              },
              "outbounds": [
                { "tag": "proxy", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "nl.departament.site", "port": 443,
                    "users": [ { "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "encryption": "none" } ] } ] },
                  "streamSettings": { "network": "tcp", "security": "reality" } },
                { "tag": "direct", "protocol": "freedom" },
                { "tag": "direct-fragment", "protocol": "freedom" },
                { "tag": "block", "protocol": "blackhole" }
              ]
            }
            """);

        var outbound = XrayJsonTemplateFmt.GetProxyOutbound(config);

        outbound.Should().NotBeNull();
        outbound!.tag.Should().Be("proxy");
        outbound.settings.vnext![0].address.Should().Be("nl.departament.site");
    }

    /// <summary>
    /// A rule narrowed to a destination describes a special case, not the default path, so it must not
    /// decide which server the profile is. Here a domain-scoped rule points at a second outbound while
    /// ordinary traffic still goes to the first.
    /// </summary>
    [Fact]
    public void NarrowedRulesDoNotDecideTheServer()
    {
        var config = Parse(
            """
            {
              "routing": {
                "domainStrategy": "IPIfNonMatch",
                "rules": [
                  { "type": "field", "domain": ["geosite:category-ads"], "outboundTag": "block" },
                  { "type": "field", "domain": ["example.com"], "outboundTag": "proxy-special" },
                  { "type": "field", "network": "tcp,udp", "outboundTag": "proxy-main" }
                ]
              },
              "outbounds": [
                { "tag": "proxy-special", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "special.example", "port": 443,
                    "users": [ { "id": "11111111-2222-3333-4444-555555555555", "encryption": "none" } ] } ] } },
                { "tag": "proxy-main", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "main.example", "port": 8443,
                    "users": [ { "id": "66666666-7777-8888-9999-000000000000", "encryption": "none" } ] } ] } },
                { "tag": "block", "protocol": "blackhole" }
              ]
            }
            """);

        XrayJsonTemplateFmt.GetProxyOutbound(config)!.tag.Should().Be("proxy-main");
    }

    /// <summary>
    /// The narrowing fields the app itself never emits — source, user, sourcePort, attrs — still make a
    /// rule a special case when a provider template uses them.
    /// </summary>
    [Fact]
    public void SourceAndUserScopedRulesAreNotTheDefaultPath()
    {
        var config = Parse(
            """
            {
              "routing": {
                "rules": [
                  { "type": "field", "source": ["10.0.0.0/8"], "outboundTag": "proxy-lan" },
                  { "type": "field", "user": ["ops@departament"], "outboundTag": "proxy-ops" },
                  { "type": "field", "sourcePort": "1000-2000", "outboundTag": "proxy-lowports" },
                  { "type": "field", "attrs": ":method", "outboundTag": "proxy-attrs" },
                  { "type": "field", "network": "tcp,udp", "outboundTag": "proxy-main" }
                ]
              },
              "outbounds": [
                { "tag": "proxy-lan", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "lan.example", "port": 443,
                    "users": [ { "id": "11111111-1111-1111-1111-111111111111" } ] } ] } },
                { "tag": "proxy-ops", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "ops.example", "port": 443,
                    "users": [ { "id": "22222222-2222-2222-2222-222222222222" } ] } ] } },
                { "tag": "proxy-lowports", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "low.example", "port": 443,
                    "users": [ { "id": "33333333-3333-3333-3333-333333333333" } ] } ] } },
                { "tag": "proxy-attrs", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "attrs.example", "port": 443,
                    "users": [ { "id": "44444444-4444-4444-4444-444444444444" } ] } ] } },
                { "tag": "proxy-main", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "main.example", "port": 8443,
                    "users": [ { "id": "55555555-5555-5555-5555-555555555555" } ] } ] } }
              ]
            }
            """);

        XrayJsonTemplateFmt.GetProxyOutbound(config)!.tag.Should().Be("proxy-main");
    }

    /// <summary>
    /// A catch-all sending everything to freedom is a bypass or a kill-switch, not this profile's
    /// server. Resolution keeps looking rather than reporting that the server is "freedom".
    /// </summary>
    [Fact]
    public void ACatchAllToFreedomIsNotTheServer()
    {
        var config = Parse(
            """
            {
              "routing": {
                "domainStrategy": "AsIs",
                "rules": [
                  { "type": "field", "network": "tcp,udp", "outboundTag": "direct" }
                ]
              },
              "outbounds": [
                { "tag": "direct", "protocol": "freedom" },
                { "tag": "proxy", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "fallback.example", "port": 443,
                    "users": [ { "id": "aaaaaaaa-0000-0000-0000-000000000000", "encryption": "none" } ] } ] } }
              ]
            }
            """);

        XrayJsonTemplateFmt.GetProxyOutbound(config)!.tag.Should().Be("proxy");
    }

    /// <summary>A balancer names its members by tag prefix; any proxy member represents the profile.</summary>
    [Fact]
    public void ABalancerResolvesThroughItsSelector()
    {
        var config = Parse(
            """
            {
              "routing": {
                "domainStrategy": "IPIfNonMatch",
                "rules": [
                  { "type": "field", "network": "tcp,udp", "balancerTag": "balancer-eu" }
                ],
                "balancers": [
                  { "tag": "balancer-eu", "selector": ["node-eu"] }
                ]
              },
              "outbounds": [
                { "tag": "direct", "protocol": "freedom" },
                { "tag": "node-eu-1", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "eu1.example", "port": 443,
                    "users": [ { "id": "aaaaaaaa-1111-1111-1111-111111111111" } ] } ] } }
              ]
            }
            """);

        XrayJsonTemplateFmt.GetProxyOutbound(config)!.tag.Should().Be("node-eu-1");
    }

    /// <summary>A balancer whose selector matches nothing falls back to its fallbackTag.</summary>
    [Fact]
    public void ABalancerFallsBackToItsFallbackTag()
    {
        var config = Parse(
            """
            {
              "routing": {
                "rules": [
                  { "type": "field", "network": "tcp,udp", "balancerTag": "balancer-eu" }
                ],
                "balancers": [
                  { "tag": "balancer-eu", "selector": ["node-eu"], "fallbackTag": "proxy-backup" }
                ]
              },
              "outbounds": [
                { "tag": "direct", "protocol": "freedom" },
                { "tag": "proxy-backup", "protocol": "trojan",
                  "settings": { "servers": [ { "address": "backup.example", "port": 8443, "password": "pw" } ] } }
              ]
            }
            """);

        XrayJsonTemplateFmt.GetProxyOutbound(config)!.tag.Should().Be("proxy-backup");
    }

    /// <summary>With no routing section at all, the first proxy outbound is still the answer.</summary>
    [Fact]
    public void NoRoutingFallsBackToTheFirstProxyOutbound()
    {
        var config = Parse(
            """
            {
              "outbounds": [
                { "tag": "proxy", "protocol": "vmess",
                  "settings": { "vnext": [ { "address": "plain.example", "port": 12345,
                    "users": [ { "id": "cccccccc-dddd-eeee-ffff-000000000000" } ] } ] } },
                { "tag": "direct", "protocol": "freedom" }
              ]
            }
            """);

        XrayJsonTemplateFmt.GetProxyOutbound(config)!.tag.Should().Be("proxy");
    }

    /// <summary>
    /// The raw-JSON twin used by the hot-swap lift, the running-proxy-tag capture and the batch
    /// speedtest graft resolves the SAME outbound and hands back the as-authored node.
    /// </summary>
    [Fact]
    public void RawResolutionMatchesTheTypedOneAndKeepsTheAuthoredNode()
    {
        var root = JsonUtils.ParseJson(
            """
            {
              "routing": {
                "rules": [
                  { "type": "field", "ip": ["geoip:private"], "outboundTag": "direct" },
                  { "type": "field", "network": "tcp,udp", "outboundTag": "proxy-rum1lk2" }
                ]
              },
              "outbounds": [
                { "tag": "proxy", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "web.max.ru", "port": 443,
                    "users": [ { "id": "fd9151b1-da58-493e-b830-e2037d7b66e6", "encryption": "none" } ] } ] } },
                { "tag": "proxy-rum1lk2", "protocol": "vless",
                  "settings": { "vnext": [ { "address": "185.91.54.248", "port": 44444,
                    "users": [ { "id": "4f69ad29-f49a-4032-b9bb-888663ff8296", "encryption": "none" } ] } ] },
                  "mux": { "enabled": false } },
                { "tag": "direct", "protocol": "freedom" }
              ]
            }
            """) as JsonObject;

        root.Should().NotBeNull();

        var resolved = XrayJsonTemplateFmt.ResolveProxyOutbound(root);

        resolved.Should().NotBeNull();
        resolved!["tag"]!.GetValue<string>().Should().Be("proxy-rum1lk2");
        resolved["mux"].Should().NotBeNull("the as-authored node is returned, not a rebuilt one");
    }

    /// <summary>A config with no proxy outbound at all resolves to nothing on both paths.</summary>
    [Fact]
    public void NoProxyOutboundResolvesToNull()
    {
        const string json =
            """
            {
              "outbounds": [
                { "tag": "direct", "protocol": "freedom" },
                { "tag": "block", "protocol": "blackhole" }
              ]
            }
            """;

        XrayJsonTemplateFmt.GetProxyOutbound(Parse(json)).Should().BeNull();
        XrayJsonTemplateFmt.ResolveProxyOutbound(JsonUtils.ParseJson(json) as JsonObject).Should().BeNull();
    }
}
