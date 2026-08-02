using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

public partial class MessageBoxDialog : Window
{
    public MessageBoxDialog()
        : this(string.Empty, string.Empty)
    {
    }

    /// <param name="destructive">
    /// True, когда «да» УДАЛЯЕТ. Тогда подтверждающая кнопка красная (Button.Destructive) и несёт
    /// глагол действия, а не «Подтвердить»: деструктивное действие обязано называть, что оно сделает,
    /// и читаться как ГЛАВНОЕ действие диалога — не как выключенный контрол рядом со сплошной
    /// «Отменой», каким его увидел владелец (D3).
    /// </param>
    /// <param name="confirmLabel">Подпись подтверждающей кнопки; null — «Подтвердить» из ResUI.</param>
    public MessageBoxDialog(string caption, string message, string? confirmLabel = null, bool destructive = false)
    {
        InitializeComponent();

        if (Design.IsDesignMode)
        {
            caption = "departament";
            message = "Удалить подписку?";
        }

        Title = caption;
        txtMessage.Text = message;

        if (destructive)
        {
            btnYes.Classes.Remove("Primary");
            btnYes.Classes.Add("Destructive");
        }
        if (confirmLabel.IsNotEmpty())
        {
            btnYes.Content = confirmLabel;
        }

        btnYes.Click += BtnYes_Click;
        btnNo.Click += BtnNo_Click;

        CanMinimize = false;
    }

    private void BtnYes_Click(object? sender, RoutedEventArgs e)
    {
        Close(ButtonResult.Yes);
    }

    private void BtnNo_Click(object? sender, RoutedEventArgs e)
    {
        Close(ButtonResult.No);
    }
}
