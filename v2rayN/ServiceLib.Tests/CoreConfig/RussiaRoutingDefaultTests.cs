using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Enums;
using ServiceLib.Handler;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Services.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.CoreConfig;

/// <summary>
/// «Белый список России» — набор маршрутов по умолчанию, правило в правило и в том же порядке, что
/// одноимённый готовый набор Android (assets/custom_routing_white_russia): торренты напрямую, QUIC
/// блокируется, локальная сеть напрямую, домены и IP РФ напрямую, остальное через proxy. И то, как
/// его видят оба ядра во всех режимах: системный прокси, TUN, sing-box перед узлом Custom.
/// </summary>
public class RussiaRoutingDefaultTests
{
    private const string Yandex = "77.88.8.8";

    /// <summary>Набор Android, записанный словами: порядок, исходящий, условие и подпись.</summary>
    private static readonly (string Remarks, string Outbound, string Kind, string Value)[] AndroidRussia =
    [
        ("Обход BitTorrent", Global.DirectTag, "protocol", "bittorrent"),
        ("Блокировать QUIC (UDP 443)", Global.BlockTag, "port/network", "443/udp"),
        ("Обход локальной сети (IP)", Global.DirectTag, "ip", "geoip:private"),
        ("Обход локальной сети (домены)", Global.DirectTag, "domain", "geosite:private"),
        ("Обход доменов РФ", Global.DirectTag, "domain", "geosite:category-ru"),
        ("Обход IP-адресов РФ", Global.DirectTag, "ip", "geoip:ru"),
    ];

    private static List<RulesItem> Sample() =>
        JsonUtils.Deserialize<List<RulesItem>>(EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + "white_russia"))!;

    private static (string Remarks, string Outbound, string Kind, string Value) Describe(RulesItem r)
    {
        var kind = r.Protocol?.Count > 0 ? "protocol"
            : r.Ip?.Count > 0 ? "ip"
            : r.Domain?.Count > 0 ? "domain"
            : "port/network";
        var value = kind switch
        {
            "protocol" => string.Join(",", r.Protocol!),
            "ip" => string.Join(",", r.Ip!),
            "domain" => string.Join(",", r.Domain!),
            _ => $"{r.Port}/{r.Network}",
        };
        return (r.Remarks ?? string.Empty, r.OutboundTag ?? string.Empty, kind, value);
    }

    [Fact]
    public void Sample_IsTheAndroidRussiaSet_RuleByRule()
    {
        var rules = Sample();

        rules.Select(Describe).Should().Equal(AndroidRussia);
        rules.Should().OnlyContain(r => r.Enabled);
        // Каждое правило — одно условие, ни одного правила в proxy: «остальное» решает исходящий по
        // умолчанию, как на Android.
        rules.Should().NotContain(r => r.OutboundTag == Global.ProxyTag);
        rules.Should().OnlyContain(r => r.InboundTag == null && r.Process == null);
    }

    [Fact]
    public void Sample_MatchesTheAndroidAsset()
    {
        //  Живая сверка с ассетом Android, когда его репозиторий рядом (DP_ANDROID_ROOT или /home/user/dp).
        //  Сам ассет в этот репозиторий не входит, поэтому без него тест пропускается, а порядок и
        //  состав стережёт Sample_IsTheAndroidRussiaSet_RuleByRule.
        var root = Environment.GetEnvironmentVariable("DP_ANDROID_ROOT") ?? "/home/user/dp";
        var asset = Path.Combine(root, "V2rayNG", "app", "src", "main", "assets", "custom_routing_white_russia");
        Assert.SkipUnless(File.Exists(asset), $"ассет Android не найден: {asset}");

        var android = JsonUtils.Deserialize<List<RulesItem>>(File.ReadAllText(asset))!;
        var pc = Sample();

        pc.Should().HaveCount(android.Count);
        pc.Select(Describe).Should().Equal(android.Select(Describe));
        JsonUtils.Serialize(pc, false).Should().Be(JsonUtils.Serialize(android, false));
    }

    private static Config NewConfig(bool tun)
    {
        var config = CoreConfigTestFactory.CreateConfig(ECoreType.Xray);
        config.SimpleDNSItem = ConfigHandler.InitBuiltinSimpleDNS();
        config.TunModeItem.EnableTun = tun;
        CoreConfigTestFactory.BindAppManagerConfig(config);
        return config;
    }

    private static V2rayConfig Xray(bool tun)
    {
        var config = NewConfig(tun);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        node.Address = "vpn.example.com";
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.Xray) with
        {
            IsTunEnabled = tun,
            RoutingItem = CoreConfigTestFactory.CreateBuiltinRouting("white_russia"),
            ProtectDomainList = ["vpn.example.com"],
        };
        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        return JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!;
    }

    internal static SingboxConfig Singbox(bool tun, bool preService)
    {
        var config = NewConfig(tun);
        ProfileItem node;
        if (preService)
        {
            //  sing-box, который держит туннель для узла Custom (BuildPreSocksIfNeeded): SOCKS в Xray.
            node = CoreConfigTestFactory.CreateSocksNode(ECoreType.sing_box);
            node.Address = Global.Loopback;
            node.Port = AppManager.Instance.GetLocalPort(EInboundProtocol.socks);
            node.Username = null;
            node.Password = null;
        }
        else
        {
            node = CoreConfigTestFactory.CreateVmessNode(ECoreType.sing_box);
            node.Address = "vpn.example.com";
        }
        var context = CoreConfigTestFactory.CreateContext(config, node, ECoreType.sing_box) with
        {
            IsTunEnabled = tun,
            RoutingItem = CoreConfigTestFactory.CreateBuiltinRouting("white_russia"),
            ProtectDomainList = ["vpn.example.com"],
        };
        var result = new CoreConfigSingboxService(context).GenerateClientConfigContent();
        result.Success.Should().BeTrue($"ret msg: {result.Msg}");
        return JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!;
    }

    /// <summary>Индексы правил набора в конфиге: идут подряд и в порядке Android.</summary>
    private static void ShouldBeConsecutiveInOrder(IReadOnlyList<int> indexes)
    {
        indexes.Should().NotContain(-1);
        for (var k = 1; k < indexes.Count; k++)
        {
            indexes[k].Should().Be(indexes[k - 1] + 1, "правила набора идут подряд, в порядке Android");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xray_RulesKeepTheAndroidOrder_AndTheRestGoesToProxy(bool tun)
    {
        var cfg = Xray(tun);
        var rules = cfg.routing.rules;

        static bool Is(RulesItem4Ray r, string outbound) => r.outboundTag == outbound && r.inboundTag == null && r.process == null;
        ShouldBeConsecutiveInOrder(
        [
            rules.FindIndex(r => Is(r, Global.DirectTag) && r.protocol?.SequenceEqual(["bittorrent"]) == true),
            rules.FindIndex(r => Is(r, Global.BlockTag) && r.port == "443" && r.network == "udp"),
            rules.FindIndex(r => Is(r, Global.DirectTag) && r.ip?.SequenceEqual(["geoip:private"]) == true),
            rules.FindIndex(r => Is(r, Global.DirectTag) && r.domain?.SequenceEqual(["geosite:private"]) == true),
            rules.FindIndex(r => Is(r, Global.DirectTag) && r.domain?.SequenceEqual(["geosite:category-ru"]) == true),
            rules.FindIndex(r => Is(r, Global.DirectTag) && r.ip?.SequenceEqual(["geoip:ru"]) == true),
        ]);
        // Не совпавшее ни с одним правилом уходит в первый исходящий — proxy.
        cfg.outbounds.First().tag.Should().Be(Global.ProxyTag);
        rules.Should().NotContain(r => r.domain != null && r.domain.Any(d => d.EndsWith(":cn")));
        rules.Should().NotContain(r => r.ip != null && r.ip.Any(d => d.EndsWith(":cn")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Singbox_RulesKeepTheAndroidOrder_AndTheRestGoesToProxy(bool tun, bool preService)
    {
        var cfg = Singbox(tun, preService);
        var rules = cfg.route.rules;

        ShouldBeConsecutiveInOrder(
        [
            rules.FindIndex(r => r.outbound == Global.DirectTag && r.protocol?.SequenceEqual(["bittorrent"]) == true),
            rules.FindIndex(r => r.action == "reject" && r.port?.SequenceEqual([443]) == true && r.network?.SequenceEqual(["udp"]) == true),
            rules.FindIndex(r => r.outbound == Global.DirectTag && r.ip_is_private == true),
            rules.FindIndex(r => r.outbound == Global.DirectTag && r.rule_set?.SequenceEqual(["geosite-private"]) == true),
            rules.FindIndex(r => r.outbound == Global.DirectTag && r.rule_set?.SequenceEqual(["geosite-category-ru"]) == true),
            rules.FindIndex(r => r.outbound == Global.DirectTag && r.rule_set?.SequenceEqual(["geoip-ru"]) == true),
        ]);
        cfg.route.final.Should().Be(Global.ProxyTag);

        // Наборов правил ядру нужно ровно столько: ни одного китайского.
        cfg.route.rule_set!.Select(t => t.tag).Should().BeEquivalentTo("geosite-private", "geosite-category-ru", "geoip-ru");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Xray_RussianDomainsResolveThroughTheDirectDns(bool tun)
    {
        var servers = CoreConfigTestFactory.XrayDnsServers(Xray(tun));

        var ru = servers.Single(s => s.domains != null && s.domains.Contains("geosite:category-ru"));
        ru.address.Should().Be(Yandex);
        ru.tag.Should().StartWith(Global.DirectDnsTag);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Singbox_RussianDomainsResolveThroughTheDirectDns(bool tun, bool preService)
    {
        var cfg = Singbox(tun, preService);

        cfg.dns!.rules.Should().Contain(r => r.server == Global.SingboxDirectDNSTag
                                             && r.rule_set != null && r.rule_set.Contains("geosite-category-ru"));
        cfg.dns.servers.Single(s => s.tag == Global.SingboxDirectDNSTag).server.Should().Be(Yandex);
    }

    [Fact]
    public void Singbox_RussianRuleSets_AreLocalFilesWhenBundled_RemoteOnlyWithoutThem()
    {
        //  Первое подключение не должно зависеть от GitHub: набор правил, который лежит в bin/srss,
        //  ядро читает с диска (SingboxRulesetService). Без файла — удалённый набор через proxy, и не
        //  скачавшийся при старте набор роняет sing-box. Поэтому geoip-ru.srs и geosite-category-ru.srs
        //  обязаны быть в сборке.
        string[] tags = ["geoip-ru", "geosite-category-ru"];
        var dir = Utils.GetBinPath("srss");
        Directory.CreateDirectory(dir);
        var kept = tags.ToDictionary(t => t, t =>
        {
            var path = Path.Combine(dir, $"{t}.srs");
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        });
        try
        {
            foreach (var t in tags)
            {
                File.Delete(Path.Combine(dir, $"{t}.srs"));
            }
            var remote = Singbox(true, true).route.rule_set!;
            remote.Single(t => t.tag == "geoip-ru").url.Should().Be("https://raw.githubusercontent.com/2dust/sing-box-rules/rule-set-geoip/geoip-ru.srs");
            remote.Single(t => t.tag == "geosite-category-ru").url.Should().Be("https://raw.githubusercontent.com/2dust/sing-box-rules/rule-set-geosite/geosite-category-ru.srs");
            remote.Where(t => tags.Contains(t.tag)).Should().OnlyContain(t => t.type == "remote" && t.http_client!.detour == Global.ProxyTag);

            foreach (var t in tags)
            {
                File.WriteAllBytes(Path.Combine(dir, $"{t}.srs"), [0]);
            }
            var local = Singbox(true, true).route.rule_set!;
            foreach (var t in tags)
            {
                var set = local.Single(r => r.tag == t);
                set.type.Should().Be("local");
                set.format.Should().Be("binary");
                set.path.Should().Be(Path.Combine(dir, $"{t}.srs"));
                set.url.Should().BeNull();
            }
        }
        finally
        {
            foreach (var (t, bytes) in kept)
            {
                var path = Path.Combine(dir, $"{t}.srs");
                if (bytes is null)
                {
                    File.Delete(path);
                }
                else
                {
                    File.WriteAllBytes(path, bytes);
                }
            }
        }
    }
}
