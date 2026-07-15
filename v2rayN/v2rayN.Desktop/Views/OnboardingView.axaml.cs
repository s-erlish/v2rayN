using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Первый запуск (нет подписок): приветствие на всю ширину окна под chrome — без рейла, без
/// списка серверов, без connect-щита. MainWindow показывает эту вью, пока
/// <see cref="HomeViewModel.IsEmpty"/> = true, и прячет её (открывая обычный шелл), как только
/// подписка добавлена. Разметка статична; здесь — только проводка CTA по DataContext.
///
/// «Добавить по QR-коду» / «из буфера обмена» бьют в реальный движок через HomeViewModel
/// (тот же путь, что онбординг-CTA в HomeView). «Войти через Telegram» / «через сайт» пока
/// открывают вкладку «Аккаунт» — реальный вход подключит агент AccountViewModel (Ф-D6/D7).
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

    // Вход через Telegram/сайт: экран/логику аккаунта строит другой агент. Пока — переход на
    // вкладку «Аккаунт». TODO: подключить реальный вход, когда появится AccountViewModel.
    private void OnLogin(object? sender, RoutedEventArgs e)
    {
        (TopLevel.GetTopLevel(this) as MainWindow)?.SelectAccountTab();
    }
}
