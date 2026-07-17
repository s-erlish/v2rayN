namespace v2rayN.Desktop.Common;

// ─────────────────────────────────────────────────────────────────────────────
// OWNER: WP4 — Account / Login / Onboarding.  Keys: Account_*, Login_*, Onboarding_*
//        (+ Common_* references).
// Views: AccountView(.axaml/.cs), AccountViewModel, LoginView(.axaml/.cs),
//        OnboardingView(.axaml/.cs).
// Inventory: LOCALIZATION_PLAN.md §2.4. Add each key with Add("Account_X", "ru", "en").
// This is the ONLY L file WP4 edits.
// ─────────────────────────────────────────────────────────────────────────────

public sealed partial class L
{
    partial void RegisterAccount()
    {
        // ── Account screen (AccountView) ──
        Add("Account_Balance", "Баланс", "Balance");
        Add("Account_TopUp", "Пополнить", "Top up");
        Add("Account_TopUpTitle", "Пополнение баланса", "Top up balance");
        Add("Account_TopUpHint",
            "Введите сумму в рублях — откроется страница оплаты.",
            "Enter an amount in rubles — the payment page will open.");
        Add("Account_AmountRub", "Сумма, ₽", "Amount, ₽");
        Add("Account_Continue", "Продолжить", "Continue");
        Add("Account_CopyReferralCode", "Скопировать код", "Copy code");
        Add("Account_FirstSub", "Оформи первую подписку", "Set up your first subscription");
        Add("Account_NoSubHint",
            "Выбери тариф — оплата в рублях, подключение сразу.",
            "Pick a plan — pay in rubles, connect right away.");
        Add("Account_Devices", "Устройства", "Devices");
        Add("Account_SignOut", "Выйти", "Sign out");
        Add("Account_SignInTitle", "Войди в departament", "Sign in to departament");
        Add("Account_SignInHint",
            "Через Telegram — быстро, без пароля. Или войди по почте на сайте.",
            "With Telegram — fast, no password. Or sign in by email on the website.");

        // ── Account view-model (imperative / computed) ──
        Add("Account_AmountGtZero", "Введите сумму больше 0", "Enter an amount greater than 0");
        Add("Account_ReferralCode", "Реф-код {0}", "Referral code {0}");
        Add("Account_ReferralBenefit", "Код друга", "Referral code");
        Add("Account_MySubs", "Мои подписки", "My subscriptions");
        Add("Account_ValidUntil", "Действует до {0}", "Valid until {0}");
        Add("Account_DevicesCount", "Устройства: {0} / {1}", "Devices: {0} / {1}");

        // ── Subscription card (identity caption · health chip · expiry urgency · devices) ──
        Add("Account_TariffCaption", "Тариф · {0}", "Plan · {0}");
        Add("Account_TrialPeriod", "Пробный период", "Trial period");
        Add("Account_HealthActive", "Активна", "Active");
        Add("Account_HealthExpiring", "Истекает", "Expiring");
        Add("Account_HealthExpired", "Истекла", "Expired");
        Add("Account_ExpiresUntil", "До {0}", "Until {0}");
        Add("Account_ExpiresInDays", "Осталось {0} дн.", "{0} days left");
        Add("Account_ExpiredOn", "Истекла", "Expired");
        Add("Account_Perpetual", "Бессрочно", "No expiry");
        Add("Account_DevicesUsage", "{0} из {1} устройств", "{0} of {1} devices");
        Add("Account_DevicesTotal", "{0} устройств", "{0} devices");
        Add("Account_Renew", "Продлить", "Renew");
        Add("Account_PrevSub", "Предыдущая", "Previous");
        Add("Account_NextSub", "Следующая", "Next");

        // ── Subscription card (redesign: named sub · state-led meters · auto-renew · renew flow) ──
        Add("Account_YourSubscription", "Ваша подписка", "Your subscription");
        Add("Account_SubscriptionN", "Подписка {0}", "Subscription {0}");
        Add("Account_ActiveUntil", "Активна до {0}", "Active until {0}");
        Add("Account_ExpiredOnDate", "Истекла {0}", "Expired {0}");
        Add("Account_DevicesShort", "{0} / {1} устройств", "{0} / {1} devices");
        Add("Account_DevicesUnlimited", "Безлимит устройств", "Unlimited devices");
        Add("Account_TrafficUnlimited", "{0} · безлимит", "{0} · unlimited");
        Add("Account_AutoRenew", "Автопродление", "Auto-renew");
        Add("Account_AutoRenewNext", "Продлится {0} — спишем {1}", "Renews {0} — we'll charge {1}");
        Add("Account_AutoRenewOn", "Автопродление включено", "Auto-renew is on");
        Add("Account_AutoRenewOff", "Автопродление выключено", "Auto-renew is off");
        Add("Account_AutoRenewNudge",
            "Включите автопродление, чтобы не прерывать",
            "Turn on auto-renew so it doesn't lapse");
        Add("Account_RenewFromBalance", "С баланса · {0}", "From balance · {0}");
        Add("Account_RenewWithCard", "Оплатить картой", "Pay by card");
        Add("Account_RenewDone", "Подписка продлена", "Subscription renewed");
        Add("Account_PickPlan", "Выбрать тариф", "Pick a plan");

        // ── Linking block (Способы входа) ──
        Add("Account_LinkingTitle", "Способы входа", "Sign-in methods");
        Add("Account_LinkEmail", "Email и пароль", "Email & password");
        Add("Account_WebCabinet", "Веб-кабинет", "Web cabinet");
        Add("Account_Linked", "Привязан", "Linked");
        Add("Account_LinkAction", "Привязать", "Link");
        Add("Account_AddAction", "Добавить", "Add");
        Add("Account_OpenAction", "Открыть", "Open");
        Add("Account_SoonAction", "Скоро", "Soon");
        Add("Account_TgLinkCode", "Код: {0}", "Code: {0}");
        Add("Account_OpenBot", "Открыть бота", "Open the bot");
        Add("Account_TgLinkWaiting", "Ждём подтверждения в Telegram…", "Waiting for Telegram…");
        Add("Account_EmailLinkTitle", "Привязать почту", "Link an email");
        Add("Account_EmailLinkHint",
            "Пришлём ссылку для подтверждения на этот адрес.",
            "We'll email a confirmation link to this address.");
        Add("Account_EmailSent", "Письмо отправлено на {0}", "Email sent to {0}");
        Add("Account_Send", "Отправить", "Send");
        Add("Account_LinkDone", "Готово", "Done");

        // ── Login screen (LoginView) ──
        Add("Login_SignIn", "Вход", "Sign in");
        Add("Login_Title", "Вход в departament", "Sign in to departament");
        Add("Login_Subtitle",
            "Войдите через Telegram в один тап или по email и паролю.",
            "Sign in with Telegram in one tap, or with your email and password.");
        Add("Login_Or", "или", "or");
        Add("Login_Email", "Электронная почта", "Email");
        Add("Login_EmailInvalid",
            "Введите корректный email, например name@example.com",
            "Enter a valid email, for example name@example.com");
        Add("Login_Password", "Пароль", "Password");
        Add("Login_ShowPassword", "Показать пароль", "Show password");
        Add("Login_HidePassword", "Скрыть пароль", "Hide password");
        Add("Login_EnterCode",
            "Введите 6-значный код из приложения",
            "Enter the 6-digit code from your app");
        Add("Login_CodeIs6", "Код состоит из 6 цифр", "The code is 6 digits");
        Add("Login_Confirm", "Подтвердить", "Confirm");
        Add("Login_SignUp", "Регистрация на сайте", "Sign up on the website");
        Add("Login_WaitingConfirm",
            "Ожидаем подтверждения в Telegram",
            "Waiting for Telegram confirmation");
        Add("Login_TelegramConfirmHint",
            "Подтвердите вход в открывшемся приложении и вернитесь сюда — остальное сделаем сами.",
            "Confirm the sign-in in the app that opened, then come back here — we'll take care of the rest.");
        Add("Login_OpenTelegram", "Открыть Telegram", "Open Telegram");
        Add("Login_StartOver", "Начать заново", "Start over");
        Add("Login_ChooseAnother", "Другой способ входа", "Use another method");

        // Login error family (login-flow diagnostics; shown in the error line).
        Add("Login_ErrBadCreds", "Неверный email или пароль", "Incorrect email or password");
        Add("Login_ErrLinkExpired", "Ссылка устарела, начните заново", "The link has expired, start over");
        Add("Login_ErrUnavailable", "Вход недоступен", "Sign-in is unavailable");
        Add("Login_ErrRetry",
            "Что-то пошло не так, попробуйте снова",
            "Something went wrong, try again");

        // ── Onboarding (OnboardingView) ──
        // Welcome/hint dedup to WP1's Home_Welcome / Home_NoSubsHint (see plan §2.4).
        Add("Onboarding_OrSignIn", "Или войдите в свой аккаунт", "Or sign in to your account");

        // ── Account sync overlay (AccountSyncView) ──
        Add("Account_SyncTitle", "Добавляем аккаунт", "Adding your account");
        // Live stage line — tracks the real post-login phase (checking → subscriptions → servers).
        Add("Account_SyncStageAccount", "Проверяем аккаунт", "Checking your account");
        Add("Account_SyncSubtitle", "Загружаем подписки…", "Loading subscriptions…");
        Add("Account_SyncStageServers", "Обновляем серверы", "Refreshing servers");

        // Sync error surface (a failed import shows retry, not an eternal spinner).
        Add("Account_SyncErrorTitle", "Не удалось синхронизировать", "Sync didn't finish");
        Add("Account_SyncErrorHint",
            "Проверьте соединение и попробуйте снова.",
            "Check your connection and try again.");
        Add("Account_SyncRetry", "Повторить", "Try again");
        Add("Account_SyncReLogin", "Войти заново", "Sign in again");
    }
}
