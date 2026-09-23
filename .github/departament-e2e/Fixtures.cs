using System.Text.Json.Nodes;

namespace DpE2E;

/// <summary>
/// Тестовый VPN-сервер и то, что про него знает клиент. Ключи и UUID — те же, что у стенда на Linux
/// (/tmp/e2e/server.json): VLESS + REALITY + Vision, как у настоящих узлов Remnawave.
/// </summary>
internal static class Fixtures
{
    public const string Uuid = "986f25c1-b232-4d14-804b-cc6cd2575e8c";
    public const string PrivateKey = "KAwH_8DI220bNqpYx0HU4hpvLDiPSN8u3OxuZCFkf3U";
    public const string PublicKey = "8rVm_E6Qe7F0VdAGwmpxvo2SyTAC2RiaesL1Ze7mu1Y";
    public const string ShortId = "ab12";
    public const string RealitySni = "e2e.test";
    public const string VlessRemarks = "E2E VLESS";
    public const string CustomRemarks = "E2E Custom";

    /// <summary>Узел для подписки /sub: адрес — ДОМЕН, чтобы в режиме TUN было что разрешать мимо туннеля.</summary>
    public static string VlessLink(string host, int port) =>
        $"vless://{Uuid}@{host}:{port}?encryption=none&flow=xtls-rprx-vision&security=reality&sni={RealitySni}" +
        $"&fp=chrome&pbk={PublicKey}&sid={ShortId}&type=tcp#{Uri.EscapeDataString(VlessRemarks)}";

    /// <summary>
    /// Подписка /subjson — XRAY_JSON, как отдаёт Remnawave: массив полных конфигов Xray. Приложение
    /// кладёт такой конфиг в guiConfigs/&lt;guid&gt;.json и заводит узел Custom, у которого Address — имя
    /// этого файла. Сервер в нём — тот же домен.
    /// </summary>
    public static string CustomSubscription(string host, int port)
    {
        var config = new JsonObject
        {
            ["dns"] = new JsonObject { ["queryStrategy"] = "UseIPv4", ["servers"] = new JsonArray("1.1.1.1", "1.0.0.1") },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["listen"] = "127.0.0.1", ["port"] = 10808, ["protocol"] = "socks", ["tag"] = "socks",
                    ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                    ["sniffing"] = new JsonObject { ["destOverride"] = new JsonArray("http", "tls", "quic"), ["enabled"] = true, ["routeOnly"] = false },
                },
                new JsonObject
                {
                    ["listen"] = "127.0.0.1", ["port"] = 10809, ["protocol"] = "http", ["tag"] = "http",
                    ["settings"] = new JsonObject { ["allowTransparent"] = false },
                    ["sniffing"] = new JsonObject { ["destOverride"] = new JsonArray("http", "tls", "quic"), ["enabled"] = true, ["routeOnly"] = false },
                }),
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["protocol"] = "vless", ["tag"] = "proxy",
                    ["settings"] = new JsonObject
                    {
                        ["vnext"] = new JsonArray(new JsonObject
                        {
                            ["address"] = host, ["port"] = port,
                            ["users"] = new JsonArray(new JsonObject { ["id"] = Uuid, ["encryption"] = "none", ["flow"] = "xtls-rprx-vision" }),
                        }),
                    },
                    ["streamSettings"] = new JsonObject
                    {
                        ["network"] = "tcp", ["security"] = "reality",
                        ["realitySettings"] = new JsonObject
                        {
                            ["fingerprint"] = "chrome", ["publicKey"] = PublicKey, ["serverName"] = RealitySni,
                            ["shortId"] = ShortId, ["spiderX"] = "",
                        },
                        ["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "none" } },
                    },
                },
                new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" },
                new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" }),
            ["remarks"] = CustomRemarks,
            ["routing"] = new JsonObject
            {
                ["domainMatcher"] = "hybrid", ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray(
                    new JsonObject { ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" },
                    new JsonObject { ["domain"] = new JsonArray("geosite:private"), ["outboundTag"] = "direct" },
                    new JsonObject { ["protocol"] = new JsonArray("bittorrent"), ["outboundTag"] = "direct" }),
            },
        };
        return new JsonArray(config).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Конфиг тестового сервера (Xray 25.9.11, как на стенде Linux: 26-я ветка глушит во freedom частные
    /// и петлевые адреса). Цель REALITY — второй вход того же сервера с TLS на 127.0.0.1: петлю туннель
    /// не захватывает, а внешняя цель тянула бы собственные соединения сервера в TUN, то есть по кругу.
    /// По той же причине исходящее freedom привязано к физическому интерфейсу (sockopt.interface):
    /// в режиме «весь трафик» сервер живёт на той же машине, и без привязки его ответы в интернет
    /// ушли бы в туннель к клиенту. DNS сервера — DoH по IP через тот же freedom: порт 53 вне туннеля
    /// strict_route на Windows закрывает для всех, кроме sing-box.
    /// </summary>
    public static JsonObject ServerConfig(int serverPort, int destPort, string accessLog, string errorLog,
        JsonObject destCertificate, string? bindInterface, IReadOnlyDictionary<string, string> hosts, bool ipv6Loopback)
    {
        var freedom = new JsonObject
        {
            ["tag"] = "direct", ["protocol"] = "freedom",
            ["settings"] = new JsonObject { ["domainStrategy"] = "UseIPv4" },
        };
        if (!string.IsNullOrEmpty(bindInterface))
        {
            freedom["streamSettings"] = new JsonObject { ["sockopt"] = new JsonObject { ["interface"] = bindInterface } };
        }
        var hostsNode = new JsonObject();
        foreach (var (name, ip) in hosts)
        {
            hostsNode[name] = ip;
        }

        JsonObject Vless(string tag, string listen) => new()
        {
            ["tag"] = tag, ["listen"] = listen, ["port"] = serverPort, ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["clients"] = new JsonArray(new JsonObject { ["id"] = Uuid, ["flow"] = "xtls-rprx-vision" }),
                ["decryption"] = "none",
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "raw", ["security"] = "reality",
                ["realitySettings"] = new JsonObject
                {
                    ["target"] = $"127.0.0.1:{destPort}",
                    ["serverNames"] = new JsonArray(RealitySni),
                    ["privateKey"] = PrivateKey,
                    ["shortIds"] = new JsonArray(ShortId),
                },
            },
            ["sniffing"] = new JsonObject { ["enabled"] = false },
        };

        //  Домен узла (localtest.me) разрешается и в ::1: клиент, начавший с IPv6, должен попасть туда же.
        var inbounds = new JsonArray(Vless("vless-in", "127.0.0.1"));
        if (ipv6Loopback)
        {
            inbounds.Add(Vless("vless-in6", "::1"));
        }
        inbounds.Add(new JsonObject
        {
            //  Сайт-«маска» для REALITY: настоящий TLS 1.3 с самоподписанным сертификатом.
            ["tag"] = "reality-target", ["listen"] = "127.0.0.1", ["port"] = destPort, ["protocol"] = "http",
            ["settings"] = new JsonObject(),
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "raw", ["security"] = "tls",
                ["tlsSettings"] = new JsonObject
                {
                    ["alpn"] = new JsonArray("h2", "http/1.1"),
                    ["certificates"] = new JsonArray(destCertificate),
                },
            },
        });

        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "info", ["access"] = accessLog, ["error"] = errorLog },
            ["dns"] = new JsonObject
            {
                ["hosts"] = hostsNode,
                ["servers"] = new JsonArray("https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"),
                ["queryStrategy"] = "UseIPv4",
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = new JsonArray(
                freedom,
                new JsonObject { ["tag"] = "dns-out", ["protocol"] = "dns" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }),
            ["routing"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject { ["inboundTag"] = new JsonArray("reality-target"), ["outboundTag"] = "block" },
                    new JsonObject { ["port"] = "53", ["outboundTag"] = "dns-out" }),
            },
        };
    }

    /// <summary>Ручной клиент для самопроверки сервера до приложения: SOCKS → VLESS/REALITY.</summary>
    public static JsonObject ManualClientConfig(int socksPort, int serverPort) => new()
    {
        ["log"] = new JsonObject { ["loglevel"] = "warning" },
        ["inbounds"] = new JsonArray(new JsonObject
        {
            ["listen"] = "127.0.0.1", ["port"] = socksPort, ["protocol"] = "socks",
            ["settings"] = new JsonObject { ["udp"] = true },
        }),
        ["outbounds"] = new JsonArray(new JsonObject
        {
            ["protocol"] = "vless",
            ["settings"] = new JsonObject
            {
                ["vnext"] = new JsonArray(new JsonObject
                {
                    ["address"] = "127.0.0.1", ["port"] = serverPort,
                    ["users"] = new JsonArray(new JsonObject { ["id"] = Uuid, ["encryption"] = "none", ["flow"] = "xtls-rprx-vision" }),
                }),
            },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "raw", ["security"] = "reality",
                ["realitySettings"] = new JsonObject
                {
                    ["serverName"] = RealitySni, ["fingerprint"] = "chrome", ["publicKey"] = PublicKey, ["shortId"] = ShortId,
                },
            },
        }),
    };
}
