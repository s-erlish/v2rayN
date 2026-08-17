using Avalonia.Animation;
using v2rayN.Desktop.Common;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Вкладка «Аккаунт»: семь полос из screens.md — профиль, кольцо трафика с блоком тарифа, пара
/// кнопок «Пополнить»/«Продлить», автопродление, «Управление», «Способы входа» и «Выйти из аккаунта».
///
/// Code-behind держит ровно три вещи: (1) навигацию наружу событиями, чтобы MainWindow не менялся;
/// (2) чип баланса, который открывает ТОТ ЖЕ флайаут пополнения, что и кнопка «Пополнить» (одна форма,
/// два якоря); (3) появление полосы 2 при активации вкладки. Всё остальное — биндинги к
/// <see cref="AccountViewModel"/>.
///
/// Прогиба строк здесь СОЗНАТЕЛЬНО нет: строки живут на общем лекале (Border.SubRow), из которого
/// press-scale был убран после реального дефекта — строка уезжала из-под курсора и жест Tapped
/// отменялся, тап срабатывал через раз (коммит «Settings rows: remove press-scale»).
/// </summary>
public partial class AccountView : UserControl
{
    /// <summary>Строка «Купить подписку» / CTA карточки — хост открывает Buy.</summary>
    public event EventHandler? BuyRequested;

    /// <summary>Строка «Устройства» — хост открывает Devices.</summary>
    public event EventHandler? DevicesRequested;

    /// <summary>Строка «История платежей» — хост открывает History.</summary>
    public event EventHandler? HistoryRequested;

    /// <summary>CTA входа (logged-out) — хост открывает суб-страницу «Вход».</summary>
    public event EventHandler? LoginRequested;

    private AccountViewModel? _vm;

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
        LoginSiteButton.Click += (_, _) => LoginRequested?.Invoke(this, EventArgs.Empty);
        LogoutRow.Tapped += (_, _) => (DataContext as AccountViewModel)?.LogoutCmd.Execute().Subscribe();

        // Чип баланса — второй якорь того же флайаута пополнения. Второй Flyout здесь завёл бы
        // вторую форму (и второй набор состояний валидации) для одного и того же действия.
        BalanceChip.Click += (_, _) => TopUpButton.Flyout?.ShowAt(BalanceChip);

        DataContextChanged += (_, _) => HookVm();
        HookVm();
    }

    // ==================== VM wiring ====================

    private void HookVm()
    {
        if (_vm != null)
        {
            _vm.BuyIntentRequested -= OnBuyIntent;
            _vm.DevicesIntentRequested -= OnDevicesIntent;
            _vm.TopUpCheckoutOpened -= OnTopUpCheckoutOpened;
        }

        _vm = DataContext as AccountViewModel;
        if (_vm is null)
        {
            return;
        }

        // Флайаут пополнения закрывается ТОЛЬКО при успехе (чекаут открылся): невалидная сумма
        // оставляет его открытым, чтобы показать инлайн-ошибку.
        _vm.TopUpCheckoutOpened += OnTopUpCheckoutOpened;
        // Карточка подписки может сама попроситься в Buy/Devices (например «Продлить» без тарифа).
        _vm.BuyIntentRequested += OnBuyIntent;
        _vm.DevicesIntentRequested += OnDevicesIntent;
    }

    private void OnTopUpCheckoutOpened(object? sender, EventArgs e) => TopUpButton.Flyout?.Hide();

    private void OnBuyIntent(object? sender, EventArgs e) => BuyRequested?.Invoke(this, EventArgs.Empty);

    private void OnDevicesIntent(object? sender, EventArgs e) => DevicesRequested?.Invoke(this, EventArgs.Empty);

    // ==================== появление полосы 2 ====================

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // MainWindow помечает ровно одну keep-alive вкладку hit-test'абельной при свопе: переход
        // false→true = «эта вкладка стала активной» → проигрываем появление кольца и блока тарифа.
        if (change.Property == IsHitTestVisibleProperty
            && change.GetNewValue<bool>()
            && !change.GetOldValue<bool>())
        {
            PlayEntrance();
        }
    }

    // Кольцо «распускается» из 0.94 (bloomIn прототипа), блок тарифа приезжает снизу на +8 с
    // задержкой в один стаггер-шаг. Под «Облегчённым режимом» не запускаем вовсе — полоса просто есть.
    private void PlayEntrance()
    {
        if (MotionState.IsLite)
        {
            return;
        }

        if (TrafficRing is not null)
        {
            var bloom = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(560),
                Easing = Motion.Ease.OutQuint,
                FillMode = FillMode.Both,
                Children =
                {
                    new KeyFrame
                    {
                        Cue = new Cue(0d),
                        Setters =
                        {
                            new Setter(OpacityProperty, 0d),
                            new Setter(ScaleTransform.ScaleXProperty, 0.94d),
                            new Setter(ScaleTransform.ScaleYProperty, 0.94d),
                        },
                    },
                    new KeyFrame
                    {
                        Cue = new Cue(1d),
                        Setters =
                        {
                            new Setter(OpacityProperty, 1d),
                            new Setter(ScaleTransform.ScaleXProperty, 1d),
                            new Setter(ScaleTransform.ScaleYProperty, 1d),
                        },
                    },
                },
            };
            _ = bloom.RunAsync(TrafficRing);
        }

        if (PlanBlock is not null)
        {
            var lift = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(460),
                Delay = Motion.Dur.Stagger * 2,
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
            _ = lift.RunAsync(PlanBlock);
        }
    }
}
