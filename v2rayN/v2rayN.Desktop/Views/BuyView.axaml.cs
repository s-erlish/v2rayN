using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Купить подписку»: бесшовный тулбар (back-шеврон + Headline), состояния
/// скелет/ошибка/пусто/успех, post-checkout hint, карты тарифов с опциями срока, карта чекаута
/// (степпер доп-устройств + «Итого» + «Оплатить») и оверлей-шит «Способ оплаты». Порт Android
/// activity_buy_tariff.xml + BuyTariffActivity.kt + PaymentMethodSheet.kt. DATA-DRIVEN: всё биндится
/// к <see cref="BuyViewModel"/> (departament-API), никаких зашитых тарифов/цен.
///
/// Самодостаточен: DataContext ставит сам (как AccountView), design-time — пример каталога для
/// превьювера. Навигация назад отдана наружу через <see cref="BackRequested"/> — хостинг подключается
/// централизованно позже.
/// </summary>
public partial class BuyView : UserControl
{
    /// <summary>Возникает по back-шеврону тулбара; обработку (закрыть суб-страницу) вешает хост.</summary>
    public event EventHandler? BackRequested;

    public BuyView()
    {
        InitializeComponent();
        DataContext = Design.IsDesignMode ? BuyViewModel.CreateDesign() : new BuyViewModel();

        // Esc закрывает шит «Способ оплаты» (клавиатурный путь к «тап-вне»).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private BuyViewModel? Vm => DataContext as BuyViewModel;

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnScrimTapped(object? sender, TappedEventArgs e)
    {
        Vm?.CloseSheet();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm?.IsSheetOpen == true)
        {
            Vm.CloseSheet();
            e.Handled = true;
        }
    }
}
