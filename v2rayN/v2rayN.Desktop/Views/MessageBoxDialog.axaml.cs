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

        // Отступ 16 существует ровно для тени: она рисуется В НЁМ. Если композитор попиксельной
        // прозрачности не даёт, эти 16 px — не пустота, а закрашенный периметр окна, и тень в нём
        // читается вторым контуром вокруг карточки. Тогда карточка занимает окно целиком: заливка
        // окна (TransparencyBackgroundFallback) совпадает с заливкой карточки, и периметра нет.
        // Читается в конструкторе — окно уже имеет платформенную реализацию, поэтому решение
        // принимается ДО первой раскладки и окно не передёргивает размером.
        if (ActualTransparencyLevel != WindowTransparencyLevel.Transparent)
        {
            dialogCard.Margin = default;
            dialogCard.BoxShadow = default;
        }

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
