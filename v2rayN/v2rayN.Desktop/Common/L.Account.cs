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
        Add("Account_TopUpMethod", "Способ оплаты", "Payment method");
        Add("Account_TopUpVia", "Оплата · {0}", "Payment · {0}");
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
        Add("Account_AutoRenewOnDate", "Продлится {0}", "Renews {0}");
        Add("Account_AutoRenewOff", "Автопродление выключено", "Auto-renew is off");
        Add("Account_AutoRenewNudge",
            "Включите автопродление, чтобы не прерывать",
            "Turn on auto-renew so it doesn't lapse");
        Add("Account_RenewFromBalance", "С баланса · {0}", "From balance · {0}");
        Add("Account_RenewWithCard", "Оплатить картой", "Pay by card");
        Add("Account_RenewDone", "Подписка продлена", "Subscription renewed");
        Add("Account_PickPlan", "Выбрать тариф", "Pick a plan");

        // ── Overflow «Ещё»: докупка устройств + улучшение тарифа ──
        Add("Account_More", "Ещё", "More");
        Add("Account_AddDevices", "Докупить устройства", "Add devices");
        Add("Account_UpgradeTariff", "Улучшить тариф", "Upgrade plan");
        Add("Account_ExtraDevicesN", "+{0} к устройствам", "+{0} devices");
        // Client-side estimate («≈»): the final amount is computed by the backend at payment.
        Add("Account_DeviceEstimate", "≈ {0}", "≈ {0}");
        Add("Account_EstimateNote",
            "Примерная сумма — точную посчитаем при оплате",
            "Approximate — the exact amount is set at checkout");
        Add("Account_PayFromBalance", "С баланса", "From balance");
        Add("Account_PayWithCard", "Картой", "By card");
        Add("Account_DevicesAdded", "Устройства добавлены", "Devices added");
        Add("Account_UpgradeTo", "Улучшить до {0}", "Upgrade to {0}");
        // {0} = amount, {1} = effective days.
        Add("Account_UpgradeQuote", "{0} · +{1} дн.", "{0} · +{1} days");
        Add("Account_UpgradeDone", "Тариф улучшен", "Plan upgraded");
        Add("Account_NoUpgrades", "Вы на максимальном тарифе", "You're on the top plan");
        Add("Account_BackAction", "Назад", "Back");

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
            "Войдите по email и паролю — или через Telegram в один тап.",
            "Sign in with your email and password — or with Telegram in one tap.");
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

        // ── Start-page auth: sign-in ⇄ register segment, providers, passwordless links ──
        Add("Login_TabSignIn", "Вход", "Sign in");
        Add("Login_TabRegister", "Регистрация", "Register");
        Add("Login_TitleRegister", "Создайте аккаунт", "Create your account");
        Add("Login_SubtitleRegister",
            "Зарегистрируйтесь по email — или войдите через Telegram в один тап.",
            "Register with your email — or sign in with Telegram in one tap.");
        Add("Login_PasswordRegister", "Пароль (не менее 8 символов)", "Password (at least 8 characters)");
        Add("Login_PasswordHint", "Минимум 8 символов", "At least 8 characters");
        Add("Login_ConfirmPassword", "Повторите пароль", "Repeat password");
        Add("Login_PasswordMismatch", "Пароли не совпадают", "The passwords don't match");
        Add("Login_CreateAccount", "Создать аккаунт", "Create account");
        Add("Login_MagicLink", "Войти по ссылке", "Sign in with a link");
        Add("Login_ForgotPassword", "Забыли пароль?", "Forgot password?");
        Add("Login_ContinueGoogle", "Продолжить с Google", "Continue with Google");
        Add("Login_ComingSoon", "Скоро", "Soon");

        // Email/password form submit («Войти») — distinct from the browser handoff «Войти через сайт»
        // (Common_SignInWebsite) and the manual-code fallback below.
        Add("Login_SubmitSignIn", "Войти", "Sign in");
        // Manual browser→app handoff fallback: paste the code the site shows if the scheme callback misses.
        Add("Login_ByCode", "Войти по коду", "Sign in with a code");
        Add("Login_CodePaste", "Вставьте код из браузера", "Paste the code from your browser");
        // Transient step while the departamentvpn://auth handoff code is being redeemed.
        Add("Login_SiteHandoff", "Завершаем вход через сайт…", "Finishing sign-in via the website…");

        // Email-pending states (verify email · magic link sent · reset sent). {0} = the address.
        Add("Login_VerifyTitle", "Подтвердите почту", "Confirm your email");
        Add("Login_VerifyHint",
            "Мы отправили ссылку на {0}. Откройте её, чтобы подтвердить вход — остальное сделаем сами.",
            "We've sent a link to {0}. Open it to confirm your sign-in — we'll take care of the rest.");
        Add("Login_MagicSentTitle", "Ссылка отправлена", "Link sent");
        Add("Login_MagicSentHint",
            "Если аккаунт с {0} существует, мы отправили ссылку для входа. Откройте её в браузере.",
            "If an account for {0} exists, we've sent a sign-in link. Open it in your browser.");
        Add("Login_ResetSentTitle", "Письмо отправлено", "Email sent");
        Add("Login_ResetSentHint",
            "Если аккаунт с {0} существует, мы отправили ссылку для сброса пароля. Задайте новый пароль и вернитесь ко входу.",
            "If an account for {0} exists, we've sent a password-reset link. Set a new password, then return to sign in.");
        Add("Login_Resend", "Отправить снова", "Send again");
        Add("Login_BackToSignIn", "Вернуться ко входу", "Back to sign in");

        // Login error family (login-flow diagnostics; shown in the error line).
        Add("Login_ErrBadCreds", "Неверный email или пароль", "Incorrect email or password");
        Add("Login_ErrLinkExpired", "Ссылка устарела, начните заново", "The link has expired, start over");
        Add("Login_ErrUnavailable", "Вход недоступен", "Sign-in is unavailable");
        Add("Login_ErrEmailTaken",
            "Аккаунт с этой почтой уже существует",
            "An account with this email already exists");
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
