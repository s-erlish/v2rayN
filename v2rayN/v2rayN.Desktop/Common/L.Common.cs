namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP0 (Foundation). Shared Common_* keys reused across every screen.
// WP1-WP6 only *reference* these keys via {loc:T Common_*} / L.T("Common_*");
// they must NOT edit this file. Add screen-specific keys to your own L.<Area>.cs.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterCommon()
    {
        // ── Actions / buttons ──
        Add("Common_Back", "Назад", "Back");
        Add("Common_Retry", "Повторить", "Retry");
        Add("Common_Cancel", "Отмена", "Cancel");
        Add("Common_Delete", "Удалить", "Delete");
        Add("Common_Edit", "Изменить", "Edit");
        Add("Common_Add", "Добавить", "Add");
        Add("Common_Copy", "Копировать", "Copy");
        Add("Common_Open", "Открыть", "Open");
        Add("Common_Refresh", "Обновить", "Refresh");
        Add("Common_Manage", "Управление", "Manage");

        // ── Provider / server actions ──
        // Terminology lock 9.3: a subscription URL that yields servers is a «провайдер».
        // «Подписка» is reserved for the paid Departament service (Account_*/Buy_*).
        Add("Common_AddSubscription", "Добавить провайдера", "Add provider");
        Add("Common_AddFromClipboard", "Добавить из буфера обмена", "Add from clipboard");
        Add("Common_AddViaQr", "Добавить по QR-коду", "Add via QR code");
        Add("Common_UpdateSubscription", "Обновить провайдера", "Update provider");
        Add("Common_TestLatency", "Проверить задержку", "Test latency");

        // ── Auth / commerce ──
        Add("Common_SignInTelegram", "Войти через Telegram", "Sign in with Telegram");
        Add("Common_SignInWebsite", "Войти через сайт", "Sign in via website");
        Add("Common_BuySubscription", "Купить подписку", "Buy subscription");
        Add("Common_PaymentHistory", "История платежей", "Payment history");
        Add("Common_Copied", "Скопировано", "Copied");

        // ── Field / value tokens ──
        Add("Common_Default", "По умолчанию", "Default");
        Add("Common_Custom", "Свой", "Custom");
        Add("Common_On", "Вкл", "On");
        Add("Common_Off", "Выкл", "Off");
        Add("Common_SearchPlaceholder", "Поиск…", "Search…");

        // ── Error / status family (API + connection) ──
        Add("Common_CouldntConnect", "Не удалось подключиться", "Couldn't connect");
        Add("Common_CouldntLoad", "Не удалось загрузить", "Couldn't load");
        Add("Common_CouldntOpenPayment", "Не удалось открыть страницу оплаты", "Couldn't open the payment page");
        Add("Common_CompletePaymentInBrowser", "Завершите оплату в браузере", "Complete the payment in your browser");
        Add("Common_ServiceUnavailable", "Сервис временно недоступен", "Service is temporarily unavailable");
        // Copy law 9.4 gives this string verbatim for the no-network case.
        Add("Common_NetworkError", "Нет подключения к интернету. Проверьте сеть и повторите.", "No internet connection. Check your network and try again.");
        Add("Common_SignInRequired", "Требуется вход в аккаунт", "Sign-in required");
        Add("Common_TooManyRequests", "Слишком много запросов. Попробуйте позже", "Too many requests. Try again later");
        Add("Common_Timeout", "Превышено время ожидания", "Request timed out");
        // Copy law 9.4, last-resort string. Verbatim, including the closing full stop.
        Add("Common_SomethingWrong", "Что-то пошло не так. Повторите попытку.", "Something went wrong. Try again.");

        // ── Units / formats (positional templates → use with L.F; arrays → split on ',') ──
        // Byte-unit ladder: split on ',' at the call site. WP2 uses all 6; WP5 uses the first 5.
        Add("Common_ByteUnits", "Б,КБ,МБ,ГБ,ТБ,ПБ", "B,KB,MB,GB,TB,PB");
        Add("Common_ZeroBytes", "0 Б", "0 B");
        Add("Common_HoursShort", "{0} ч.", "{0} h");
        Add("Common_MinutesShort", "{0} мин", "{0} min");
        Add("Common_DaysShort", "{0} дн.", "{0} days");

        // ── Plurals (locale-aware, via L.Plural). RU = {one, few, many}; EN = {one, other}. ──
        AddPlural(
            "Common_ServersPlural",
            new[] { "сервер", "сервера", "серверов" },
            new[] { "server", "servers" });
        AddPlural(
            "Common_ProvidersPlural",
            new[] { "провайдер", "провайдера", "провайдеров" },
            new[] { "provider", "providers" });
    }
}
