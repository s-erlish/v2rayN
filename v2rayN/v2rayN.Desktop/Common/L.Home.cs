namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP1 — Home / shield.  Keys: Home_* (+ Common_* / Status_* references).
// Views: ConnectHeroView(.axaml/.cs), HomeView, CompactHomeView, HomeViewModel.
// Inventory: LOCALIZATION_PLAN.md §2.1. Add each key with Add("Home_X", "ru", "en").
// This is the ONLY L file WP1 edits.
//
// Cross-WP references used by the Home code-behind/VM (NOT defined here):
//   • Common_* (L.Common.cs, WP0): Common_CouldntConnect, Common_AddSubscription,
//     Common_AddFromClipboard, Common_AddViaQr, Common_ServersPlural,
//     Common_ProvidersPlural.
//   • Status_* (L.Shell.cs, WP6): Status_Connecting / Status_Connected — the shield's
//     connecting/connected caption is the SAME shared status string the status bar uses,
//     so Home reuses WP6's keys rather than redefining them (per plan §2.1/§2.6).
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterHome()
    {
        // ── Shield status captions (idle / no-server) ──
        Add("Home_NotConnected", "Не подключено", "Not connected");
        Add("Home_ChooseServer", "Выберите сервер", "Choose a server");

        // Error-shield retry affordance (ConnectHeroView RetryHint) — not in the §2.1 table
        // but a user-facing literal in an owned Home view, so localized here.
        Add("Home_RetryHint", "Нажмите, чтобы повторить", "Tap to retry");

        // ── Empty / onboarding (no subscriptions) ──
        Add("Home_Welcome", "Приветствуем!", "Welcome!");
        Add("Home_NoSubs", "Пока нет подписок", "No subscriptions yet");
        Add("Home_NoSubsHint", "Добавьте подписку, чтобы начать пользоваться", "Add a subscription to get started");

        // First-run onboarding hero (OnboardingView): active-verb title names the exact job, subtitle
        // tells the concrete "how" and promises immediacy. Home_Welcome/Home_NoSubsHint kept above for
        // any other consumer; the onboarding screen uses these. Divider is the short sentence-case form
        // of Onboarding_OrSignIn (L.Account.cs), inside the two-hairline "или ..." rule.
        Add("Onboarding_Title", "Добавьте подписку", "Add a subscription");
        Add("Onboarding_Subtitle", "Отсканируйте QR-код или вставьте ссылку из буфера — доступ появится сразу.", "Scan a QR code or paste a link from the clipboard — access appears right away.");
        Add("Onboarding_OrSignInShort", "или войдите в аккаунт", "or sign in to your account");

        // ── Server-list grouping / meta (HomeViewModel) ──
        // Fallback group name for servers without a subscription remark.
        Add("Home_MyServers", "Мои серверы", "My servers");
        // Composed meta line: {0} = "{n} servers" plural, {1} = "{n} providers" plural.
        // The middle dot is locale-neutral and stays in the template.
        Add("Home_ServersProvidersMeta", "{0} · {1}", "{0} · {1}");

        // ── Routing-mode banner (HomeView / CompactHomeView) ──
        // Shown when "all traffic" (TUN) was requested but the app lacks the rights, so traffic
        // actually flows through the system proxy. Not in the §2.1 table but user-facing literals
        // in owned Home views (the mode name itself comes from StatusBar.RoutingModeDisplay).
        Add("Home_TunUnavailable", "Режим «весь трафик» недоступен без прав администратора", "Whole-traffic mode isn't available without administrator rights");
        Add("Home_RestartElevated", "Перезапустить с правами", "Restart as administrator");

        // ── Account chip (HomeAccountChip) ──
        // Registered per plan §2.1 for reuse; HomeAccountChip.axaml is outside WP1's file scope,
        // so its literal is not converted here — this key is available for whoever owns that view.
        Add("Home_ManageAccount", "Управление аккаунтом", "Manage account");
    }
}
