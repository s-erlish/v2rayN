using Avalonia.VisualTree;
using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Устройства»: seamless-тулбар «← Устройства», список устройств подписки (HWID) с
/// удалением через in-view подтверждение. Порт Android activity_devices.xml +
/// DeviceManagementActivity.kt. DATA-DRIVEN: всё биндится к <see cref="DevicesViewModel"/>
/// (GET /client/devices departament-API), пусто до реального ответа.
///
/// Самодостаточен: DataContext ставит сам (design-time — образец списка для превьювера).
/// Хост подписывается на <see cref="BackRequested"/>, чтобы закрыть суб-страницу.
/// </summary>
public partial class DevicesView : UserControl
{
    private readonly DevicesViewModel _viewModel;

    /// <summary>Стрелка «назад» тулбара: хост убирает суб-страницу и возвращает «Аккаунт».</summary>
    public event EventHandler? BackRequested;

    public DevicesView()
    {
        InitializeComponent();
        _viewModel = Design.IsDesignMode ? DevicesViewModel.CreateDesign() : new DevicesViewModel();
        DataContext = _viewModel;

        // Открытие подтверждения — фокус на «Отмена»: Escape/Tab работают сразу с клавиатуры.
        _viewModel.WhenAnyValue(vm => vm.ShowDeleteConfirm)
            .Subscribe(show =>
            {
                if (show)
                {
                    Dispatcher.UIThread.Post(() => DeleteCancelButton.Focus());
                }
            });
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Escape закрывает подтверждение удаления (пока запрос не в полёте).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _viewModel.ShowDeleteConfirm)
        {
            _viewModel.CancelDelete();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Клик по самому скриму (не по карте) — отмена, стандартная модальная афорданса.</summary>
    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            _viewModel.CancelDelete();
            e.Handled = true;
        }
    }

    // Press-scale 0.96 карточки устройства (Android press_scale): класс .pressed на время
    // нажатия. Нажатие на корзину внутри карточки карточку НЕ сжимает — у кнопки свой отклик.

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border card)
        {
            return;
        }
        if (e.Source is Visual origin && origin.FindAncestorOfType<Button>(includeSelf: true) != null)
        {
            return;
        }
        card.Classes.Add("pressed");
    }

    private void OnCardReleased(object? sender, PointerReleasedEventArgs e)
    {
        (sender as Border)?.Classes.Remove("pressed");
    }

    private void OnCardExited(object? sender, PointerEventArgs e)
    {
        (sender as Border)?.Classes.Remove("pressed");
    }

    private void OnCardCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        (sender as Border)?.Classes.Remove("pressed");
    }
}
