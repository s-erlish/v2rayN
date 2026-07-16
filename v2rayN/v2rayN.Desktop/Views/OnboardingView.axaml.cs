using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Первый запуск (нет подписок): приветствие на всю ширину окна под chrome — без рейла, без
/// списка серверов, без connect-щита. MainWindow показывает эту вью, пока
/// <see cref="HomeViewModel.IsEmpty"/> = true, и прячет её (открывая обычный шелл), как только
/// подписка добавлена. Разметка статична; здесь — только проводка CTA по DataContext.
///
/// «Добавить по QR-коду» / «из буфера обмена» бьют в реальный движок через HomeViewModel
/// (тот же путь, что онбординг-CTA в HomeView). «Войти через Telegram» / «через сайт» СРАЗУ
/// стартуют соответствующую авторизацию (без промежуточного выбора метода): Telegram открывает
/// deep link и переходит в ожидание, сайт открывает форму email/пароля. Шелл скрыт, пока пусто,
/// поэтому вход показываем оверлеем; «назад» возвращает к онбордингу.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();

        AddQrButton.Click += OnAddQr;
        AddClipboardButton.Click += OnAddClipboard;
        LoginTelegramButton.Click += OnLoginTelegram;
        LoginSiteButton.Click += OnLoginSite;
    }

    // Добавить по QR-коду → скан экрана (MainWindowViewModel.AddServerViaScanAsync).
    private void OnAddQr(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            _ = vm.AddViaQr();
        }
    }

    // Добавить из буфера обмена → импорт из clipboard (MainWindowViewModel.AddServerViaClipboardAsync).
    private void OnAddClipboard(object? sender, RoutedEventArgs e)
    {
        if (DataContext is HomeViewModel vm)
        {
            _ = vm.AddViaClipboard();
        }
    }

    // Войти через Telegram → сразу стартуем Telegram-авторизацию (открывает Telegram), LoginView
    // показывает состояние ожидания подтверждения — без повторного выбора метода.
    private void OnLoginTelegram(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenLoginTelegram();
    }

    // Войти через сайт → открываем LoginView прямо на форме входа по email/паролю.
    private void OnLoginSite(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenLoginSite();
    }
}
