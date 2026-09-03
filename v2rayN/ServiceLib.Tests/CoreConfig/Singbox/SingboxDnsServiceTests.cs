using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Models.CoreConfigs;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig.Singbox;

/// <summary>
/// The DNS block the generator produces must be one sing-box will actually start on — old cores
/// and 1.14+ alike. The tests below do not need a sing-box binary: they re-implement the acceptance
/// rule the core applies at startup and assert the generated shape against it.
/// </summary>
public class SingboxDnsServiceTests
{
    /// <summary>
    /// Builds the config the app hands to the sing-box that owns the TUN device: default DNS
    /// settings as shipped, no custom DNS, TUN on.
    /// </summary>
    private static SingboxConfig Generate(Action<SimpleDNSItem>? tweakDns = null)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.sing_box);
        config.SimpleDNSItem = ConfigHandler.InitBuiltinSimpleDNS();
        tweakDns?.Invoke(config.SimpleDNSItem);
        config.TunModeItem.EnableTun = true;
        CoreConfigTestFactory.BindAppManagerConfig(config);

        var node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
        node.Address = Global.Loopback;
        node.Port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);

        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = true,
            SimpleDnsItem = config.SimpleDNSItem,
            RawDnsItem = null,
        };

        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }

    // --- the rule sing-box applies at startup, mirrored -------------------------------------

    /// <summary>
    /// Legacy Address Filter Fields — a rule that decides by looking at the ANSWER. Without
    /// <c>match_response</c> (which no core before 1.14 knows) these are only legal while sing-box
    /// keeps legacy DNS mode on.
    /// </summary>
    private static bool HasLegacyAddressFilter(Rule4Sbox rule) =>
        rule.ip_accept_any == true
        || rule.ip_is_private == true
        || rule.ip_cidr is { Count: > 0 }
        || rule.rule_set_ip_cidr_accept_empty == true;

    /// <summary>
    /// Anything here turns legacy DNS mode OFF — for the whole rule list at once, whichever rule
    /// or sub-rule it sits on, and no matter what order the rules are in.
    /// </summary>
    private static bool DisablesLegacyDnsMode(Rule4Sbox rule) =>
        rule.query_type is { Count: > 0 }
        || rule.action is "evaluate" or "respond";

    private static IEnumerable<Rule4Sbox> Flatten(IEnumerable<Rule4Sbox>? rules)
    {
        foreach (var rule in rules ?? [])
        {
            yield return rule;
            foreach (var sub in Flatten(rule.rules))
            {
                yield return sub;
            }
        }
    }

    /// <summary>
    /// Reproduces sing-box's startup verdict: with legacy DNS mode off, a legacy address filter or
    /// a legacy rule-action <c>strategy</c> is fatal, and the core exits before the tunnel is up.
    /// </summary>
    private static string? DescribeSingboxRejection(Dns4Sbox? dns)
    {
        var rules = Flatten(dns?.rules).ToList();
        if (!rules.Any(DisablesLegacyDnsMode))
        {
            return null;
        }

        var addressFilter = rules.FindIndex(HasLegacyAddressFilter);
        if (addressFilter >= 0)
        {
            return $"dns rule[{addressFilter}]: Response Match Fields require match_response to be enabled";
        }

        var strategy = rules.FindIndex(t => t.strategy is { Length: > 0 } and not Global.AsIs);
        if (strategy >= 0)
        {
            return $"dns rule[{strategy}]: legacy `strategy` DNS rule action option";
        }

        return null;
    }

    // --- tests -----------------------------------------------------------------------------

    [Theory]
    // fakeIp, globalFakeIp, blockBindingQuery — every combination the shipped settings can reach
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public void GenDns_DnsRules_ShouldBeAcceptedBySingbox114(bool fakeIp, bool globalFakeIp, bool blockBindingQuery)
    {
        var cfg = Generate(dns =>
        {
            dns.FakeIP = fakeIp;
            dns.GlobalFakeIp = globalFakeIp;
            dns.BlockBindingQuery = blockBindingQuery;
        });

        DescribeSingboxRejection(cfg.dns).Should().BeNull();
    }

    [Fact]
    public void GenDns_HostsRule_ShouldMatchByDomainInsteadOfIpAcceptAny()
    {
        var cfg = Generate();

        var predefined = cfg.dns!.servers
            .Single(t => t.tag == Global.SingboxHostsDNSTag)
            .predefined!;
        predefined.Should().NotBeEmpty();

        var hostsRule = cfg.dns.rules.Single(t => t.server == Global.SingboxHostsDNSTag);
        hostsRule.ip_accept_any.Should().BeNull("ip_accept_any is fatal on sing-box 1.14");
        hostsRule.domain.Should().BeEquivalentTo(predefined.Keys);
        // the hosts server answers A and AAAA only; everything else must fall through as before
        hostsRule.query_type.Should().BeEquivalentTo(new[] { 1, 28 });
    }

    [Fact]
    public void GenDns_WithoutPredefinedHosts_ShouldOmitHostsRule()
    {
        var cfg = Generate(dns =>
        {
            dns.AddCommonHosts = false;
            dns.UseSystemHosts = false;
            dns.Hosts = string.Empty;
        });

        // a hosts rule with nothing to match on would swallow every query and NXDOMAIN it
        cfg.dns!.rules.Should().NotContain(t => t.server == Global.SingboxHostsDNSTag);
    }

    [Fact]
    public void GenDns_ShouldNotEmitIndependentCache()
    {
        // deprecated in sing-box 1.14, removed in 1.16 — where an unknown field aborts the core
        Generate().dns!.independent_cache.Should().BeNull();
    }
}
