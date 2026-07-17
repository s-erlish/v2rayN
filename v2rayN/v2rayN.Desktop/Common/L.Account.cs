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
        Add("Account_FirstSub", "Оформите первую подписку", "Set up your first subscription");
        Add("Account_NoSubHint",
            "Пока нет активной подписки. Оформите её, чтобы подключиться.",
            "You don't have an active subscription yet. Set one up to connect.");
        Add("Account_Devices", "Устройства", "Devices");
        Add("Account_SignOut", "Выйти из аккаунта", "Sign out");
        Add("Account_SignInTitle", "Войдите в аккаунт", "Sign in to your account");
        Add("Account_SignInHint",
            "Войдите, чтобы увидеть подписку, устройства и историю платежей.",
            "Sign in to see your subscription, devices, and payment history.");

        // ── Account view-model (imperative / computed) ──
        Add("Account_AmountGtZero", "Введите сумму больше 0", "Enter an amount greater than 0");
        Add("Account_ReferralCode", "Реф-код {0}", "Referral code {0}");
        Add("Account_MySubs", "Мои подписки", "My subscriptions");
        Add("Account_ValidUntil", "Действует до {0}", "Valid until {0}");
        Add("Account_DevicesCount", "Устройства: {0} / {1}", "Devices: {0} / {1}");

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
        Add("Account_SyncSubtitle", "Загружаем подписки…", "Loading subscriptions…");
    }
}
