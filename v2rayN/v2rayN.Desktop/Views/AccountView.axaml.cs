using v2rayN.Desktop.ViewModels;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Аккаунт»: профиль-карта (аватар / имя / баланс / реф-код), карточка подписки с четырьмя
/// состояниями (скелет → активная / пусто / ошибка) и секция «Управление». Порт Android
/// activity_account.xml + AccountFragment.kt. DATA-DRIVEN: всё биндится к <see cref="AccountViewModel"/>
/// (departament-API), значения-заглушки удалены — пусто до входа и реального ответа API.
///
/// Самодостаточен: DataContext ставит сам (MainWindow создаёт вьюху без DataContext), design-time —
/// пример активного состояния для превьювера.
/// </summary>
public partial class AccountView : UserControl
{
    public AccountView()
    {
        InitializeComponent();
        DataContext = Design.IsDesignMode ? AccountViewModel.CreateDesign() : new AccountViewModel();
    }
}
