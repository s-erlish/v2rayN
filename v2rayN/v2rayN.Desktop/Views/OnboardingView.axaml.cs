using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Первый запуск (нет подписок): приветствие на всю ширину окна под chrome — без рейла, без
/// списка серверов, без connect-щита. MainWindow показывает эту вью, пока
/// <see cref="HomeViewModel.IsEmpty"/> = true, и прячет её (открывая обычный шелл), как только
/// подписка добавлена. Разметка статична; здесь — только проводка CTA по DataContext.
///
/// «Добавить по QR-коду» / «из буфера обмена» бьют в реальный движок через HomeViewModel
/// (тот же путь, что онбординг-CTA в HomeView). «Войти через Telegram» / «через сайт» открывают
/// суб-страницу «Вход» (LoginView) поверх онбординга через MainWindow.OpenLogin (P0-2): шелл
/// скрыт, пока пусто, поэтому вход показываем оверлеем; «назад» возвращает к онбордингу.
/// </summary>
public partial class OnboardingView : UserControl
{
    public OnboardingView()
    {
        InitializeComponent();

        AddQrButton.Click += OnAddQr;
        AddClipboardButton.Click += OnAddClipboard;
        LoginTelegramButton.Click += OnLogin;
        LoginSiteButton.Click += OnLogin;
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

    // Вход через Telegram/сайт: открываем суб-страницу «Вход» поверх онбординга (P0-2).
    // Оба CTA ведут на один LoginView (в нём есть и Telegram, и сайт); «назад» вернёт к онбордингу.
    private void OnLogin(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenLogin();
    }
}
