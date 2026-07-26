namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP2, server list / provider meta bar.  Keys: Servers_*, Sub_* (+ Common_*).
// Views: ServerListView, CompactServersView, SubscriptionMetaView(.axaml/.cs).
// Inventory: LOCALIZATION_PLAN.md §2.2. Add each key with Add("Servers_X", "ru", "en").
// This is the ONLY L file WP2 edits.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterServers()
    {
        // ── Server list (ServerListView, CompactServersView) ──
        // «Серверы», not the colloquial «сервера»: every other string in both clients
        // («серверов», «Мои серверы», «Поиск серверов…») already uses this plural.
        Add("Servers_Title", "Серверы", "Servers");
        Add("Servers_MakeDefault", "Сделать основным", "Make default");
        Add("Servers_Duplicate", "Дублировать", "Duplicate");
        Add("Servers_ShareQr", "Поделиться · QR-код", "Share · QR code");
        Add("Servers_ShareLink", "Поделиться · ссылка", "Share · link");
        // Copy-law 9.5 «No servers» empty state, verbatim (same pair as Home_NoSubs/Home_NoSubsHint).
        Add("Servers_Empty", "Нет серверов", "No servers");
        Add("Servers_EmptyHint", "Добавьте провайдера или отсканируйте QR-код, чтобы появились серверы.", "Add a provider or scan a QR code to get servers.");
        Add("Servers_SearchPlaceholder", "Поиск серверов…", "Search servers…");

        // ── Provider meta bar (SubscriptionMetaView) ──
        Add("Sub_CollapseServers", "Свернуть серверы", "Collapse servers");
        Add("Sub_Pin", "Закрепить", "Pin");
        Add("Sub_Delete", "Удалить провайдера", "Delete provider");
        Add("Sub_DeleteConfirm", "Удалить провайдера и его серверы?", "Delete the provider and its servers?");
        Add("Sub_OpenSupport", "Открыть поддержку", "Open support");
        Add("Sub_Support", "Поддержка", "Support");
        // Same word as the account card (Account_HealthExpired), one term per state.
        Add("Sub_Expired", "Истекла", "Expired");
        Add("Sub_Until", "до {0:dd.MM.yyyy}", "until {0:dd.MM.yyyy}");
        Add("Sub_AutoUpdate", "Автообновление · {0}", "Auto-update · {0}");
    }
}
