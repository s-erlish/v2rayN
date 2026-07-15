namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран «Аккаунт»: профиль-карта (аватар / @ник / баланс / реф-код / «Пополнить»),
/// карточка подписки (имя + тариф-бейдж + срок + устройства) и секция «Управление».
/// Порт Android activity_account.xml + AccountFragment.kt. Разметка/данные — в AccountView.axaml
/// (x:Name-поля привязываются к AccountViewModel-порту departament-API на слое данных).
/// </summary>
public partial class AccountView : UserControl
{
    public AccountView()
    {
        InitializeComponent();
    }
}
