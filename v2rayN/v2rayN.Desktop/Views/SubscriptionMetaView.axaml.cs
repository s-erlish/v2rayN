namespace v2rayN.Desktop.Views;

/// <summary>
/// Мета-бар подписки (под-план 2): заголовок + подзаголовок, действия
/// (пинг/обновить/пин), трафик-пилюля + expiry, announce, поддержка + Telegram.
/// Поля x:Name-нуты под привязку к SubItem/SubscriptionHandler (агент B, §(b) плана);
/// пока показывает дизайн-временные образцы. Реальная карусель подписок — фаза 2.
/// </summary>
public partial class SubscriptionMetaView : UserControl
{
    public SubscriptionMetaView()
    {
        InitializeComponent();
    }
}
