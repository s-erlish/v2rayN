namespace v2rayN.Desktop.Account;

/// <summary>
/// Central configuration for the Departament VPN backend + Telegram bot. Ported from V2rayNG
/// auth/BackendConfig.kt (values baked from build.gradle.kts buildConfigField defaults, since the
/// desktop build has no BuildConfig).
///
/// Base URL is `https://web.departament.site/api`; every path in <see cref="Endpoints"/> is relative
/// to it. JWT auth uses `Authorization: Bearer &lt;token&gt;` (7-day token, NO refresh endpoint).
/// </summary>
public static class BackendConfig
{
    /// <summary>Backend base URL (no trailing slash).</summary>
    public static string BaseUrl => "https://web.departament.site/api".TrimEnd('/');

    /// <summary>Telegram bot username without the leading '@'.</summary>
    public static string BotUsername => "departamentvpnbot";

    /// <summary>User-Agent used for API + subscription requests (negotiates the response format).</summary>
    public static string SubscriptionUserAgent => "DepartamentVPN/1.0";

    /// <summary>True only when a backend base URL has been provided.</summary>
    public static bool IsConfigured() => BaseUrl.IsNotEmpty();

    /// <summary>Relative paths (appended to <see cref="BaseUrl"/>). Parameterized paths use helpers.</summary>
    public static class Endpoints
    {
        // Public
        public const string PublicConfig = "/public/config";
        public const string PublicTariffs = "/public/tariffs";
        public const string ServerStatus = "/public/server-status";

        // Auth
        public const string TelegramLoginToken = "/client/auth/telegram-login-token";
        public const string TelegramLoginCheck = "/client/auth/telegram-login-check";
        public const string Login = "/client/auth/login";
        public const string TwoFaLogin = "/client/auth/2fa-login";
        public const string GoogleLogin = "/client/auth/google";
        public const string Me = "/client/auth/me";

        // Subscription
        /// <summary>The authoritative ACTIVE (root) subscription summary — richer than the /all root item.</summary>
        public const string Subscription = "/client/subscription";
        public const string SubscriptionAll = "/client/subscription/all";
        public const string SubscriptionQr = "/client/subscription/qr";
        public const string UpgradeQuote = "/client/subscriptions/upgrade-quote";
        public const string Upgrade = "/client/subscriptions/upgrade";

        public static string RenameSubscription(string scope, string id) => $"/client/subscription/{scope}/{id}/name";

        public static string AddDevices(string scope, string id) => $"/client/subscription/{scope}/{id}/add-devices";

        // Devices
        public const string Devices = "/client/devices";
        public const string DeleteDevice = "/client/devices/delete";

        // Payments
        public const string PayPlatega = "/client/payments/platega";
        public const string PayBalance = "/client/payments/balance";
        public const string Payments = "/client/payments";

        // Promo / trial / referral
        public const string PromoCheck = "/client/promo-code/check";
        public const string PromoActivate = "/client/promo-code/activate";
        public const string Trial = "/client/trial";
        public const string ReferralStats = "/client/referral-stats";

        public static string SecondaryAutoRenew(string id) => $"/client/secondary-subscriptions/{id}/auto-renew";

        /// <summary>Auto-renew of the ACTIVE (root/primary) subscription — no id in the path.</summary>
        public const string PrimaryAutoRenew = "/client/subscription/auto-renew";
    }
}
