using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Аккаунт»: профиль-карта (аватар / имя / баланс / реф-код), карточка подписки с четырьмя
/// состояниями (скелет → активная / пусто / ошибка) и секция «Управление». Порт Android
/// activity_account.xml + AccountFragment.kt. DATA-DRIVEN: всё биндится к <see cref="AccountViewModel"/>
/// (departament-API), значения-заглушки удалены — пусто до входа и реального ответа API.
///
/// В рантайме DataContext ставит MainWindow (ОБЩИЙ <see cref="AccountViewModel"/>, тот же, что у
/// суб-страницы «Вход»), чтобы состояние входа было единым. Design-time — пример активного состояния.
/// Навигацию отдаёт наружу событиями: «Управление»-строки и CTA входа поднимают
/// <see cref="BuyRequested"/> / <see cref="DevicesRequested"/> / <see cref="HistoryRequested"/> /
/// <see cref="LoginRequested"/>; куда вести — решает хост (MainWindow).
/// </summary>
public partial class AccountView : UserControl
{
    /// <summary>Строка «Купить подписку» — хост открывает Buy.</summary>
    public event EventHandler? BuyRequested;

    /// <summary>Строка «Устройства» — хост открывает Devices.</summary>
    public event EventHandler? DevicesRequested;

    /// <summary>Строка «История платежей» — хост открывает History.</summary>
    public event EventHandler? HistoryRequested;

    /// <summary>CTA входа (logged-out) — хост открывает суб-страницу «Вход».</summary>
    public event EventHandler? LoginRequested;

    public AccountView()
    {
        InitializeComponent();

        // Только для превьювера: в рантайме общий VM приходит от MainWindow.
        if (Design.IsDesignMode)
        {
            DataContext = AccountViewModel.CreateDesign();
        }

        BuyRow.Tapped += (_, _) => BuyRequested?.Invoke(this, EventArgs.Empty);
        DevicesRow.Tapped += (_, _) => DevicesRequested?.Invoke(this, EventArgs.Empty);
        HistoryRow.Tapped += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
        // «Войти через сайт» → полная форма входа (email/пароль/2FA) на хосте.
        // «Войти через Telegram» биндится прямо на LoginTelegramCmd в разметке (deep-link).
        LoginSiteButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);

        // Копирование реф-кода: кнопка IconButton40 справа копирует код в буфер + тост «Скопировано»
        // (clipboard живёт на TopLevel, поэтому это чисто view-действие; сам код берём из VM).
        CopyReferralButton.Click += OnCopyReferral;

        // «Выйти из аккаунта» → LogoutCmd (AccountSession.Wipe → возврат к гейту входа).
        LogoutRow.Tapped += (_, _) => (DataContext as AccountViewModel)?.LogoutCmd.Execute().Subscribe();

        // Пополнение: работу делает TopUpCmd (биндинг кнопки во флайауте). Закрываем флайаут после
        // выполнения команды — ReactiveCommand это IObservable и эмитит по завершении исполнения.
        DataContextChanged += (_, _) => HookTopUp();
        HookTopUp();
    }

    private IDisposable? _topUpSub;

    private void HookTopUp()
    {
        _topUpSub?.Dispose();
        if (DataContext is AccountViewModel vm)
        {
            _topUpSub = vm.TopUpCmd.Subscribe(_ => TopUpButton.Flyout?.Hide());
        }
    }

    private async void OnCopyReferral(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var code = (DataContext as AccountViewModel)?.ReferralCode;
        if (code.IsNullOrEmpty())
        {
            return;
        }
        await AvaUtils.SetClipboardData(this, code);
        AppEvents.SendSnackMsgRequested.Publish("Скопировано");
    }
}
