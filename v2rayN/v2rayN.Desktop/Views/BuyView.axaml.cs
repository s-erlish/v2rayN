using Avalonia.Animation;
using v2rayN.Desktop.Common;
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

    private IDisposable? _totalSub;
    private IDisposable? _contentSub;
    private bool _firstTotal = true;

    public BuyView()
    {
        InitializeComponent();
        DataContext = Design.IsDesignMode ? BuyViewModel.CreateDesign() : new BuyViewModel();

        // Esc закрывает шит «Способ оплаты» (клавиатурный путь к «тап-вне»).
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        if (Vm is { } vm)
        {
            // «Итого» набрано как ДЕНЬГИ (сумма + приглушённый ₽) и КРОССФЕЙДИТСЯ при пересчёте
            // (степпер устройств / смена срока) — та же грамматика денег, что и hero-баланс.
            _totalSub = vm.WhenAnyValue(v => v.TotalText).Subscribe(SetTotal);
            // Тарифы показались → стаггер входа карт (по одной, +40мс), один раз.
            _contentSub = vm.WhenAnyValue(v => v.ShowContent).Subscribe(OnContentShown);
        }
    }

    private BuyViewModel? Vm => DataContext as BuyViewModel;

    // ── «Итого»: набор денег + кроссфейд суммы на реальное изменение ──

    private void SetTotal(string? text)
    {
        var (amount, currency) = SplitMoney(text);
        TotalAmount.Text = amount;
        TotalCurrency.Text = currency;

        if (_firstTotal)
        {
            _firstTotal = false;
            return;
        }
        if (MotionState.IsLite)
        {
            return;
        }
        var anim = new Animation
        {
            Duration = Motion.Dur.State,
            Easing = Motion.Ease.Standard,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(OpacityProperty, 0.25d), new Setter(TranslateTransform.YProperty, -6d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) },
                },
            },
        };
        _ = anim.RunAsync(TotalAmount);
    }

    // «150 ₽» → ("150", "₽"); бланк-валюта («150») → ("150", ""). Разбиваем по ПОСЛЕДНЕМУ пробелу
    // (число в InvariantCulture без разделителей тысяч, поэтому пробел ровно один — перед символом).
    private static (string amount, string currency) SplitMoney(string? text)
    {
        var s = text?.Trim() ?? string.Empty;
        if (s.Length == 0)
        {
            return (string.Empty, string.Empty);
        }
        var idx = s.LastIndexOf(' ');
        return idx <= 0 ? (s, string.Empty) : (s[..idx], s[(idx + 1)..]);
    }

    // ── Стаггер входа карт тарифов (одна легитимная list-стаггер-анимация) ──

    private void OnContentShown(bool shown)
    {
        if (!shown || MotionState.IsLite)
        {
            return;
        }
        // Даём ItemsControl разложиться (реализовать контейнеры), затем стаггерим их вход.
        Dispatcher.UIThread.Post(StaggerTariffs, DispatcherPriority.Background);
    }

    private void StaggerTariffs()
    {
        try
        {
            var i = 0;
            foreach (var container in TariffList.GetRealizedContainers())
            {
                // Ограничиваем задержку (карта 6+ не «опаздывает») — весь стаггер ≤ ~240мс.
                var delay = TimeSpan.FromMilliseconds(Math.Min(i, 5) * Motion.Dur.Stagger.TotalMilliseconds);
                RunCardEntrance(container, delay);
                i++;
            }
        }
        catch
        {
            // Контейнеры ещё не реализованы / гонка показа — карты всё равно проявит родительский reveal-fade.
        }
    }

    private static void RunCardEntrance(Control card, TimeSpan delay)
    {
        var anim = new Animation
        {
            Duration = Motion.Dur.Reveal,
            Delay = delay,
            Easing = Motion.Ease.OutQuint,
            FillMode = FillMode.Both,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, 8d) },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) },
                },
            },
        };
        _ = anim.RunAsync(card);
    }

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
