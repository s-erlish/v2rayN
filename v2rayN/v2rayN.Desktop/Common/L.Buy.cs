namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP5, buy / devices / payment history.  Keys: Buy_*, Devices_*, History_*
//        (+ Common_* references).
// Views: BuyView, BuyViewModel, DevicesView, DevicesViewModel, PaymentHistoryView,
//        PaymentHistoryViewModel.
// Inventory: LOCALIZATION_PLAN.md §2.5. Add each key with Add("Buy_X", "ru", "en").
// This is the ONLY L file WP5 edits.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterBuy()
    {
        // ── Buy («Купить подписку») ──
        Add("Buy_Paid", "Подписка оплачена", "Subscription paid");
        Add("Buy_PaidSubtitle", "Серверы уже добавлены, можно подключаться", "Servers are already added, you can connect");
        Add("Buy_ChoosePlan", "Выберите тариф", "Choose a plan");
        Add("Buy_AdditionalDevices", "Дополнительные устройства", "Additional devices");
        Add("Buy_RemoveDevice", "Убрать устройство", "Remove device");
        Add("Buy_AddDevice", "Добавить устройство", "Add device");
        Add("Buy_Total", "Итого", "Total");
        // Terminology lock 9.3: buying is «Купить». «Оплатить» is reserved for an invoice that
        // already exists (Account_RenewWithCard).
        Add("Buy_Pay", "Купить", "Buy");
        Add("Buy_PaymentMethod", "Способ оплаты", "Payment method");
        Add("Buy_Processing", "Платёж обрабатывается…", "Processing payment…");
        // Verbatim match with Android buy_error.
        Add("Buy_ErrLoadPlans", "Не удалось загрузить тарифы. Проверьте подключение и повторите.", "Couldn't load plans. Check your connection and try again.");
        Add("Buy_NoPlans", "Тарифы недоступны", "No plans available");
        Add("Buy_ChoosePeriod", "Выберите срок подписки", "Choose a subscription period");
        Add("Buy_NoPaymentMethods", "Способы оплаты недоступны", "No payment methods available");
        // Matches Android pay_method_from_balance_fmt and desktop Account_RenewFromBalance.
        Add("Buy_FromBalance", "С баланса: {0}", "From balance: {0}");
        Add("Buy_PaymentError", "Ошибка оплаты", "Payment error");
        Add("Buy_DevicesTraffic", "Устройства: {0} · Трафик: {1}", "Devices: {0} · Traffic: {1}");

        // ── Devices («Устройства») ──
        Add("Devices_Subtitle", "Устройства, подключённые к вашей подписке", "Devices connected to your subscription");
        Add("Devices_ThisDevice", "Это устройство", "This device");
        Add("Devices_Unlink", "Отвязать устройство", "Unlink device");
        // Copy-law 9.5 «No devices» empty state, verbatim.
        Add("Devices_Empty", "Устройств пока нет", "No devices yet");
        Add("Devices_EmptyHint", "Устройства появятся после первого подключения.", "Devices appear after your first connection.");
        // Title + line together read as Android devices_error_no_subscription, word for word.
        Add("Devices_NoSub", "Активной подписки нет", "No active subscription");
        Add("Devices_NoSubHint", "Купите тариф, чтобы управлять устройствами.", "Buy a plan to manage your devices.");
        Add("Devices_GoToAccount", "Перейти в аккаунт", "Go to account");
        Add("Devices_UnlinkConfirm", "Отвязать устройство?", "Unlink device?");
        Add("Devices_UnlinkShort", "Отвязать", "Unlink");
        Add("Devices_UnlinkBody", "Устройство «{0}» будет отключено от подписки.", "Device \"{0}\" will be disconnected from your subscription.");
        Add("Devices_UnlinkFailed", "Не удалось отвязать устройство. Повторите попытку позже.", "Couldn't unlink the device. Try again later.");
        Add("Devices_Unlinked", "Устройство отвязано", "Device unlinked");
        // Verbatim match with Android devices_error_generic.
        Add("Devices_ErrLoad", "Не удалось загрузить устройства. Проверьте подключение и повторите.", "Couldn't load devices. Check your connection and try again.");
        Add("Devices_PlatformActive", "{0} · Активно: {1}", "{0} · Active: {1}");
        Add("Devices_Active", "Активно: {0}", "Active: {0}");
        Add("Devices_Id", "ID: {0}", "ID: {0}");
        Add("Devices_Unknown", "Неизвестное устройство", "Unknown device");

        // ── Payment history («История платежей») ──
        // Copy-law 9.5 «No payments»: title + one line, no action. The line has no consumer yet;
        // PaymentHistoryView.axaml:302 renders only the title (owner of that view adds the line).
        Add("History_Empty", "Платежей пока нет", "No payments yet");
        Add("History_EmptyHint", "Здесь появится история покупок и продлений.", "Your purchases and renewals will appear here.");
        Add("History_ErrLoad", "Не удалось загрузить историю платежей", "Couldn't load payment history");
        Add("History_StatusPaid", "Оплачено", "Paid");
        Add("History_StatusProcessing", "В обработке", "Processing");
        Add("History_StatusFailed", "Ошибка", "Failed");
        Add("History_StatusCanceled", "Отменён", "Canceled");

        // Design-time sample rows (previewer only; never shipped to users).
        Add("History_SampleRenewal", "Продление подписки", "Subscription renewal");
        Add("History_SampleTopUp", "Пополнение баланса", "Balance top-up");
        Add("History_SamplePlan", "Тариф Base", "Base plan");
    }
}
