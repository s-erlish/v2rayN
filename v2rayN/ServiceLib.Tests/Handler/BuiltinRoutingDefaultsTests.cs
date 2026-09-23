using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Handler;
using ServiceLib.Helper;
using ServiceLib.Manager;
using ServiceLib.Models;
using ServiceLib.Tests.CoreConfig;
using Xunit;

namespace ServiceLib.Tests.Handler;

/// <summary>
/// Набор маршрутов по умолчанию — «Белый список России», как одноимённый готовый набор на Android.
/// Свежая установка получает его первым и активным; установка прежней сборки переходит на него один
/// раз (ConfigHandler.MigrateBuiltinRoutingDefaults), и только если по умолчанию у неё стоит
/// нетронутый «Whitelist» апстрима.
///
/// Настоящая база SQLite в каталоге тестов: наборы живут в ней, а переход — это SQL-операции. В одной
/// коллекции с остальными тестами базы и файла настроек: переход пишет guiNConfig.json, который
/// читают тесты LoadConfig.
/// </summary>
[Collection("SqliteDb")]
public class BuiltinRoutingDefaultsTests : IDisposable
{
    private readonly Config _config;
    private readonly string _configPath = Utils.GetConfigPath(Global.ConfigFileName);
    private readonly byte[]? _configFileBefore;

    public BuiltinRoutingDefaultsTests()
    {
        _config = CoreConfigTestFactory.CreateConfig();
        CoreConfigTestFactory.BindAppManagerConfig(_config);
        SQLiteHelper.Instance.CreateTable<RoutingItem>();
        SQLiteHelper.Instance.DeleteAllAsync<RoutingItem>().GetAwaiter().GetResult();
        _configFileBefore = File.Exists(_configPath) ? File.ReadAllBytes(_configPath) : null;
    }

    public void Dispose()
    {
        SQLiteHelper.Instance.DeleteAllAsync<RoutingItem>().GetAwaiter().GetResult();
        if (_configFileBefore is null)
        {
            File.Delete(_configPath);
        }
        else
        {
            File.WriteAllBytes(_configPath, _configFileBefore);
        }
    }

    private static List<RulesItem> Rules(string? ruleSet) => JsonUtils.Deserialize<List<RulesItem>>(ruleSet) ?? [];

    private static List<RulesItem> Sample(string name) => Rules(EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + name));

    /// <summary>Правила без Id: он случайный у каждой установки.</summary>
    private static string Shape(IEnumerable<RulesItem> rules) =>
        JsonUtils.Serialize(rules.Select(r =>
        {
            var copy = JsonUtils.DeepCopy(r)!;
            copy.Id = string.Empty;
            return copy;
        }).ToList(), false);

    private static async Task<List<RoutingItem>> All() => await AppManager.Instance.RoutingItems() ?? [];

    private static async Task<RoutingItem> Get(string id) => (await AppManager.Instance.GetRoutingItem(id))!;

    /// <summary>Набор ровно так, как его заводил InitBuiltinRouting прежних сборок.</summary>
    private static async Task<RoutingItem> AddUpstream(string sample, string remarks, int sort)
    {
        var item = new RoutingItem { Remarks = remarks, Url = string.Empty, Sort = sort };
        (await ConfigHandler.AddBatchRoutingRules(item, EmbedUtils.GetEmbedText(Global.CustomRoutingFileName + sample))).Should().Be(0);
        return item;
    }

    /// <summary>База установки прежней сборки: три набора апстрима, по умолчанию «Whitelist», отметки перехода нет.</summary>
    private async Task<(RoutingItem White, RoutingItem Black, RoutingItem Global)> OldInstall()
    {
        var white = await AddUpstream("white", Global.BuiltinRoutingWhitelist, 1);
        var black = await AddUpstream("black", Global.BuiltinRoutingBlacklist, 2);
        var global = await AddUpstream("global", Global.BuiltinRoutingGlobal, 3);
        await ConfigHandler.SetDefaultRouting(_config, white);
        _config.RoutingBasicItem.DefaultsVersion.Should().BeNull();
        return (white, black, global);
    }

    private static RulesItem PerAppRule(string marker, string outbound, List<string>? apps = null) => new()
    {
        Id = Utils.GetGuid(false),
        Remarks = marker,
        OutboundTag = outbound,
        Process = apps,
        Network = apps is null ? "tcp,udp" : null,
        Enabled = true,
    };

    /// <summary>Правка набора, как её делает редактор: свежая запись из базы, изменение, запись обратно.
    /// Свежая — потому что отметку «активен» SetDefaultRouting ставит в базе, а не в прежних копиях.</summary>
    private static async Task Edit(string id, Action<RoutingItem, List<RulesItem>> change)
    {
        var item = await Get(id);
        var rules = Rules(item.RuleSet);
        change(item, rules);
        item.RuleSet = JsonUtils.Serialize(rules, false);
        item.RuleNum = rules.Count;
        await SQLiteHelper.Instance.ReplaceAsync(item);
    }

    [Fact]
    public async Task FreshInstall_RussiaIsFirstActiveAndAlreadyMigrated()
    {
        await ConfigHandler.InitBuiltinRouting(_config);

        var items = await All();
        items.Select(t => t.Remarks).Should().Equal(
            Global.BuiltinRoutingRussia, Global.BuiltinRoutingWhitelist, Global.BuiltinRoutingBlacklist, Global.BuiltinRoutingGlobal);
        items.Where(t => t.IsActive).Select(t => t.Remarks).Should().Equal(Global.BuiltinRoutingRussia);
        (await ConfigHandler.GetDefaultRouting(_config)).Remarks.Should().Be(Global.BuiltinRoutingRussia);

        var russia = items[0];
        russia.RuleNum.Should().Be(6);
        Shape(Rules(russia.RuleSet)).Should().Be(Shape(Sample("white_russia")));

        // Наборы заведены по нынешним умолчаниям: переходу делать нечего.
        _config.RoutingBasicItem.DefaultsVersion.Should().Be(1);
        (await ConfigHandler.MigrateBuiltinRoutingDefaults(_config)).Should().BeFalse();
    }

    [Fact]
    public async Task FreshInstall_ConcurrentInits_CreateTheSetsOnce()
    {
        // Старт (StatusBarViewModel) и экран маршрутизации (RoutingSettingViewModel) зовут одно и то же.
        await Task.WhenAll(ConfigHandler.InitBuiltinRouting(_config), ConfigHandler.InitBuiltinRouting(_config));
        await ConfigHandler.InitBuiltinRouting(_config);

        var items = await All();
        items.Should().HaveCount(4);
        items.Count(t => t.Remarks == Global.BuiltinRoutingRussia).Should().Be(1);
        items.Count(t => t.IsActive).Should().Be(1);
    }

    [Fact]
    public async Task OldInstall_UntouchedWhitelist_SwitchesToRussiaAndKeepsTheOtherSets()
    {
        var (white, black, global) = await OldInstall();

        await ConfigHandler.InitBuiltinRouting(_config);

        var items = await All();
        items.Select(t => t.Remarks).Should().Equal(
            Global.BuiltinRoutingRussia, Global.BuiltinRoutingWhitelist, Global.BuiltinRoutingBlacklist, Global.BuiltinRoutingGlobal);
        var russia = items[0];
        russia.IsActive.Should().BeTrue();
        Shape(Rules(russia.RuleSet)).Should().Be(Shape(Sample("white_russia")));
        (await ConfigHandler.GetDefaultRouting(_config)).Id.Should().Be(russia.Id);

        // Прежние наборы на месте и не тронуты: те же Id, порядок и правила.
        foreach (var before in new[] { white, black, global })
        {
            var after = await Get(before.Id);
            after.IsActive.Should().BeFalse();
            after.Sort.Should().Be(before.Sort);
            after.RuleSet.Should().Be(before.RuleSet);
        }
        _config.RoutingBasicItem.DefaultsVersion.Should().Be(1);
    }

    [Fact]
    public async Task OldInstall_Migration_IsIdempotent()
    {
        await OldInstall();
        await ConfigHandler.InitBuiltinRouting(_config);
        var first = (await All()).Select(t => (t.Id, t.Remarks, t.IsActive, t.Sort, t.RuleSet)).ToList();

        // Отметка на диск не попала (аварийное завершение) — повтор ничего не меняет.
        _config.RoutingBasicItem.DefaultsVersion = null;
        (await ConfigHandler.MigrateBuiltinRoutingDefaults(_config)).Should().BeTrue();
        // С отметкой — не запускается вовсе.
        (await ConfigHandler.MigrateBuiltinRoutingDefaults(_config)).Should().BeFalse();
        await ConfigHandler.InitBuiltinRouting(_config);

        (await All()).Select(t => (t.Id, t.Remarks, t.IsActive, t.Sort, t.RuleSet)).Should().Equal(first);
    }

    [Fact]
    public async Task OldInstall_MarkerReachesTheConfigFileRightAway()
    {
        await OldInstall();

        await ConfigHandler.InitBuiltinRouting(_config);

        var saved = JsonUtils.Deserialize<Config>(await File.ReadAllTextAsync(_configPath, TestContext.Current.CancellationToken))!;
        saved.RoutingBasicItem.DefaultsVersion.Should().Be(1);
    }

    public static TheoryData<string> Edits => new()
    {
        "remark", "removed", "added", "outbound", "disabled", "reordered", "emptied",
        "strategy", "singboxStrategy", "singboxRuleset", "renamed",
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public async Task OldInstall_EditedWhitelist_StaysTheDefault(string edit)
    {
        var (white, _, _) = await OldInstall();
        await Edit(white.Id, (item, rules) =>
        {
            switch (edit)
            {
                case "remark": rules[0].Remarks = "Мой QUIC"; break;
                case "removed": rules.RemoveAt(rules.Count - 1); break;
                case "added": rules.Add(new RulesItem { Id = Utils.GetGuid(false), OutboundTag = Global.DirectTag, Domain = ["domain:example.org"] }); break;
                case "outbound": rules.Single(r => r.Domain?.Contains("geosite:cn") == true).OutboundTag = Global.ProxyTag; break;
                case "disabled": rules[0].Enabled = false; break;
                case "reordered": (rules[2], rules[3]) = (rules[3], rules[2]); break;
                case "emptied": rules.Clear(); break;
                case "strategy": item.DomainStrategy = Global.IPIfNonMatch; break;
                case "singboxStrategy": item.DomainStrategy4Singbox = "prefer_ipv4"; break;
                case "singboxRuleset": item.CustomRulesetPath4Singbox = "my_rulesets.json"; break;
                case "renamed": item.Remarks = "Мой набор"; break;
            }
        });
        var whiteBefore = await Get(white.Id);
        whiteBefore.IsActive.Should().BeTrue();

        await ConfigHandler.InitBuiltinRouting(_config);

        var active = await ConfigHandler.GetDefaultRouting(_config);
        active.Id.Should().Be(white.Id, $"правка «{edit}» — выбор человека");
        (await Get(white.Id)).RuleSet.Should().Be(whiteBefore.RuleSet);
        // Новый набор всё равно в списке, первым, но не по умолчанию.
        var items = await All();
        items[0].Remarks.Should().Be(Global.BuiltinRoutingRussia);
        items[0].IsActive.Should().BeFalse();
        _config.RoutingBasicItem.DefaultsVersion.Should().Be(1);
    }

    [Fact]
    public async Task OldInstall_EmptyListsAndStringsCountAsAbsent()
    {
        // Оба генератора понимают пустой список и пустую строку как отсутствие поля.
        var (white, _, _) = await OldInstall();
        await Edit(white.Id, (_, rules) =>
        {
            foreach (var rule in rules)
            {
                rule.InboundTag ??= [];
                rule.Protocol ??= [];
                rule.Process ??= [];
                rule.Port ??= string.Empty;
                rule.Network ??= string.Empty;
            }
        });

        await ConfigHandler.InitBuiltinRouting(_config);

        (await ConfigHandler.GetDefaultRouting(_config)).Remarks.Should().Be(Global.BuiltinRoutingRussia);
    }

    [Theory]
    [InlineData("black")]
    [InlineData("global")]
    [InlineData("own")]
    public async Task OldInstall_ChosenSet_StaysTheDefault(string chosen)
    {
        var (_, black, global) = await OldInstall();
        var target = chosen switch
        {
            "black" => black,
            "global" => global,
            _ => await AddUpstream("white", "Мои правила", 4),
        };
        await ConfigHandler.SetDefaultRouting(_config, target);

        await ConfigHandler.InitBuiltinRouting(_config);

        (await ConfigHandler.GetDefaultRouting(_config)).Id.Should().Be(target.Id);
        (await All()).Count(t => t.Remarks == Global.BuiltinRoutingRussia).Should().Be(1);
    }

    [Fact]
    public async Task OldInstall_PerAppBypass_MovesToTheNewDefault()
    {
        var (white, _, _) = await OldInstall();
        var bypass = PerAppRule("__departament_perapp_bypass", Global.DirectTag, ["Telegram.exe", "C:/Games/game.exe"]);
        await Edit(white.Id, (_, rules) => rules.Insert(0, bypass));

        await ConfigHandler.InitBuiltinRouting(_config);

        var russia = await ConfigHandler.GetDefaultRouting(_config);
        russia.Remarks.Should().Be(Global.BuiltinRoutingRussia);
        var rules = Rules(russia.RuleSet);
        rules.Select(r => r.Remarks).Should().Equal(
            new[] { bypass.Remarks }.Concat(Sample("white_russia").Select(r => r.Remarks)));
        rules[0].Process.Should().Equal("Telegram.exe", "C:/Games/game.exe");
        rules[0].Id.Should().Be(bypass.Id);
        russia.RuleNum.Should().Be(7);

        // В прежнем наборе правил программ больше нет: они живут в одном месте, в активном наборе.
        var whiteAfter = await Get(white.Id);
        Shape(Rules(whiteAfter.RuleSet)).Should().Be(Shape(Sample("white")));
        whiteAfter.RuleNum.Should().Be(Sample("white").Count);
    }

    [Fact]
    public async Task OldInstall_PerAppOnlyListed_KeepsHeadAndTailPositions()
    {
        var (white, _, _) = await OldInstall();
        var include = PerAppRule("__departament_perapp_include", Global.ProxyTag, ["firefox.exe"]);
        var catchAll = PerAppRule("__departament_perapp_catchall", Global.DirectTag);
        await Edit(white.Id, (_, rules) =>
        {
            rules.Insert(0, include);
            rules.Add(catchAll);
        });

        await ConfigHandler.InitBuiltinRouting(_config);

        var rules = Rules((await ConfigHandler.GetDefaultRouting(_config)).RuleSet);
        rules.First().Remarks.Should().Be(include.Remarks);
        rules.Last().Remarks.Should().Be(catchAll.Remarks);
        Shape(rules.Skip(1).SkipLast(1)).Should().Be(Shape(Sample("white_russia")));
    }

    [Fact]
    public async Task Migration_RunsOnce_ReturningToTheBasicSetSurvivesRestarts()
    {
        var (white, _, _) = await OldInstall();
        await ConfigHandler.InitBuiltinRouting(_config);

        // Человек сам вернул себе «Базовый набор».
        await ConfigHandler.SetDefaultRouting(_config, await Get(white.Id));
        await ConfigHandler.InitBuiltinRouting(_config);
        await ConfigHandler.InitBuiltinRouting(_config);

        (await ConfigHandler.GetDefaultRouting(_config)).Id.Should().Be(white.Id);
    }

    [Fact]
    public async Task DefaultRules_RebuildMakesRussiaTheDefaultAgain()
    {
        // «Стандартные правила» (RoutingSubView.ResetRulesAsync): убрать встроенные «V4-» и завести заново.
        await OldInstall();
        await ConfigHandler.InitBuiltinRouting(_config);
        var own = await AddUpstream("global", "Мои правила", 10);
        await ConfigHandler.SetDefaultRouting(_config, own);

        foreach (var item in (await All()).Where(t => t.Remarks.StartsWith(Global.BuiltinRoutingPrefix)))
        {
            await ConfigHandler.RemoveRoutingItem(item);
        }
        await ConfigHandler.InitRouting(_config);

        var items = await All();
        items.Should().HaveCount(5);
        items.Count(t => t.Remarks == Global.BuiltinRoutingRussia).Should().Be(1);
        (await ConfigHandler.GetDefaultRouting(_config)).Remarks.Should().Be(Global.BuiltinRoutingRussia);
        (await Get(own.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task FreshInstall_DefaultRouting_SendsRussiaDirectInTheGeneratedConfig()
    {
        // Сквозной путь: набор по умолчанию из базы (как его берёт CoreConfigContextBuilder) в конфиг Xray.
        await ConfigHandler.InitBuiltinRouting(_config);
        var node = CoreConfigTestFactory.CreateVmessNode(ECoreType.Xray);
        var context = CoreConfigTestFactory.CreateContext(_config, node, ECoreType.Xray) with
        {
            RoutingItem = await ConfigHandler.GetDefaultRouting(_config),
        };

        var result = new CoreConfigV2rayService(context).GenerateClientConfigContent();

        result.Success.Should().BeTrue(result.Msg);
        var rules = JsonUtils.Deserialize<V2rayConfig>(result.Data!.ToString())!.routing.rules;
        rules.Should().Contain(r => r.outboundTag == Global.DirectTag && r.domain != null && r.domain.SequenceEqual(new[] { "geosite:category-ru" }));
        rules.Should().Contain(r => r.outboundTag == Global.DirectTag && r.ip != null && r.ip.SequenceEqual(new[] { "geoip:ru" }));
        rules.Should().NotContain(r => r.domain != null && r.domain.Contains("geosite:cn"));
        rules.Should().NotContain(r => r.ip != null && r.ip.Contains("geoip:cn"));
    }

    [Fact]
    public async Task FreshInstall_CustomNodeInTun_PreServiceSendsRussiaDirect()
    {
        //  Узел Custom (Remnawave XRAY_JSON) в режиме TUN: туннель держит sing-box перед Xray, и маршрут
        //  у него — набор по умолчанию из базы (CoreConfigContextBuilder.BuildAll, BuildPreSocksIfNeeded).
        //  Свой конфиг Xray узла маршрутизирует по-своему; набор приложения действует здесь.
        SQLiteHelper.Instance.CreateTable<DNSItem>();
        SQLiteHelper.Instance.CreateTable<FullConfigTemplateItem>();
        _config.TunModeItem.EnableTun = true;
        _config.TunModeItem.Mtu = 9000;
        _config.SimpleDNSItem = ConfigHandler.InitBuiltinSimpleDNS();
        await ConfigHandler.InitBuiltinRouting(_config);
        var file = Utils.GetConfigPath($"routing-custom-{Utils.GetGuid(false)}.json");
        await File.WriteAllTextAsync(file, """
            { "outbounds": [
                { "tag": "proxy", "protocol": "vless", "settings": { "vnext": [ { "address": "vpn.example.com", "port": 443,
                  "users": [ { "id": "986f25c1-b232-4d14-804b-cc6cd2575e8c", "encryption": "none" } ] } ] } },
                { "tag": "direct", "protocol": "freedom" } ] }
            """, TestContext.Current.CancellationToken);
        try
        {
            var node = new ProfileItem
            {
                IndexId = "routing-custom",
                ConfigType = EConfigType.Custom,
                CoreType = ECoreType.Xray,
                Remarks = "custom",
                Address = file,
                Subid = string.Empty,
            };

            var all = await CoreConfigContextBuilder.BuildAll(_config, node);

            all.Success.Should().BeTrue();
            var pre = all.PreSocksResult!.Context;
            pre.RoutingItem!.Remarks.Should().Be(Global.BuiltinRoutingRussia);
            var result = new CoreConfigSingboxService(pre).GenerateClientConfigContent();
            result.Success.Should().BeTrue($"ret msg: {result.Msg}");
            var route = JsonUtils.Deserialize<SingboxConfig>(result.Data!.ToString())!.route;
            route.rules.Should().Contain(r => r.outbound == Global.DirectTag && r.rule_set != null && r.rule_set.SequenceEqual(new[] { "geosite-category-ru" }));
            route.rules.Should().Contain(r => r.outbound == Global.DirectTag && r.rule_set != null && r.rule_set.SequenceEqual(new[] { "geoip-ru" }));
            route.final.Should().Be(Global.ProxyTag);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Marker_RoundTripsThroughTheConfigFile_AndIsAbsentInOlderFiles()
    {
        var older = JsonUtils.Deserialize<Config>("""{ "RoutingBasicItem": { "DomainStrategy": "AsIs", "RoutingIndexId": "" } }""")!;
        older.RoutingBasicItem.DefaultsVersion.Should().BeNull();

        older.RoutingBasicItem.DefaultsVersion = 1;
        JsonUtils.Deserialize<Config>(JsonUtils.Serialize(older))!.RoutingBasicItem.DefaultsVersion.Should().Be(1);
    }
}
