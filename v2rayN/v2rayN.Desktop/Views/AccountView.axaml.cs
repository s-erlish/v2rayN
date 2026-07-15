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
        LoginCtaButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
    }
}
