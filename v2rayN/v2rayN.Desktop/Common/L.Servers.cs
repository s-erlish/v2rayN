namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP2 — Server list / subscription meta.  Keys: Servers_*, Sub_* (+ Common_*).
// Views: ServerListView, CompactServersView, SubscriptionMetaView(.axaml/.cs).
// Inventory: LOCALIZATION_PLAN.md §2.2. Add each key with Add("Servers_X", "ru", "en").
// This is the ONLY L file WP2 edits.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterServers()
    {
        // ── Server list (ServerListView, CompactServersView) ──
        Add("Servers_Title", "Сервера", "Servers");
        Add("Servers_MakeDefault", "Сделать основным", "Make default");
        Add("Servers_Duplicate", "Дублировать", "Duplicate");
        Add("Servers_ShareQr", "Поделиться · QR-код", "Share · QR code");
        Add("Servers_ShareLink", "Поделиться · ссылка", "Share · link");
        Add("Servers_Empty", "Список пуст", "No servers yet");
        Add("Servers_EmptyHint", "Добавьте подписку, чтобы увидеть серверы", "Add a subscription to see your servers");
        Add("Servers_SearchPlaceholder", "Поиск серверов…", "Search servers…");

        // ── Subscription meta-bar (SubscriptionMetaView) ──
        Add("Sub_CollapseServers", "Свернуть серверы", "Collapse servers");
        Add("Sub_Pin", "Закрепить", "Pin");
        Add("Sub_Delete", "Удалить подписку", "Delete subscription");
        Add("Sub_DeleteConfirm", "Удалить подписку?", "Delete subscription?");
        Add("Sub_OpenSupport", "Открыть поддержку", "Open support");
        Add("Sub_Support", "Поддержка", "Support");
        Add("Sub_Expired", "Просрочено", "Expired");
        Add("Sub_Until", "до {0:dd.MM.yyyy}", "until {0:dd.MM.yyyy}");
        Add("Sub_AutoUpdate", "Автообновление — {0}", "Auto-update — {0}");
    }
}
