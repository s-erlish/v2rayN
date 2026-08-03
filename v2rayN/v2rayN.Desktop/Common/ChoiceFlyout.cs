using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;

namespace v2rayN.Desktop.Common;

/// <summary>Один вариант в списке выбора: подпись, необязательное пояснение, выбран ли он и что сделать.</summary>
/// <param name="Label">Подпись варианта — то же слово, что строка настройки покажет как значение.</param>
/// <param name="Hint">Вторая строка (когда без неё вариант непонятен). null — обычный однострочный вариант.</param>
/// <param name="Selected">Текущий выбор — ровно один в списке.</param>
/// <param name="Choose">Применить вариант. Выполняется до закрытия флайаута.</param>
public sealed record ChoiceItem(string Label, string? Hint, bool Selected, Action Choose);

/// <summary>
/// Список вариантов, раскрывающийся У САМОЙ строки настройки.
///
/// ЗАЧЕМ. Владелец: «надо, чтобы просто варианты пинга при нажатии сбоку у кнопки высвечивались, и это
/// касается многих таких настроек, да и сами окна эти не проработаны никак». Настройка вида «выбери
/// одно из N» занимала целую суб-страницу с тулбаром и кнопкой «назад» — четыре жеста ради одного
/// выбора; а там, где страницы не было, значение приходилось ПЕРЕЩЁЛКИВАТЬ по кругу, не видя, какие
/// значения вообще есть. Здесь оба случая сходятся к одному: нажал строку — увидел все варианты рядом
/// с ней, выбрал — флайаут закрылся, значение в строке обновилось.
///
/// ЭТО НЕ НОВЫЙ ЯЗЫК. Порядок поверхностей — закон (00-rules §7.6): инлайн → раскрывающаяся строка →
/// ФЛАЙАУТ → диалог. Поверхность даёт <c>IncyFlyoutTheme</c> (GlobalStyles), сами варианты —
/// <c>Border.Selectable</c> + <c>PathIcon.Check</c>, тот же компонент выбора, что в списке тарифов:
/// слот галочки зарезервирован всегда, поэтому выбор ничего не переразмечает.
///
/// РАЗМЕЩЕНИЕ. Якорь — вся строка, выравнивание по её ПРАВОМУ краю (<c>BottomEdgeAlignedRight</c>):
/// список раскрывается ровно там, где стоит значение и шеврон, то есть у той части строки, которую
/// человек и нажимает. Привязка к самому значению с <c>Placement=Right</c> уводила бы список за правый
/// край окна (карточка настроек прижата к нему на 16), и позиционер отражал бы его ПОВЕРХ подписи
/// строки. По вертикали позиционер сам переворачивает список вверх, когда снизу места нет.
/// </summary>
public static class ChoiceFlyout
{
    // Галочка выбранного варианта (ic_check_24dp) — тот же путь, что у строк выбора в разметке.
    private static readonly Geometry _check = Geometry.Parse("M9,16.17L4.83,12l-1.42,1.41L9,19 21,7l-1.41,-1.41z");

    /// <summary>Ширина списка: помещает и «Реальная задержка», и «Каждые 24 часа», не растягиваясь в плиту.</summary>
    private const double Width = 264d;

    /// <summary>
    /// Раскрывает список вариантов у <paramref name="anchor"/>. Флайаут собирается на КАЖДЫЙ показ:
    /// и набор вариантов, и текущий выбор к этому моменту уже другие, а живой перевод подписей ловится
    /// тем же способом — строки берутся в момент открытия.
    /// </summary>
    /// <param name="anchor">Строка настройки. Она же цель показа и якорь позиционирования.</param>
    /// <param name="items">Варианты, ровно один из которых <see cref="ChoiceItem.Selected"/>.</param>
    /// <param name="footer">
    /// Необязательный «подвал» — параметры, которые относятся к выбору, но выбором не являются
    /// (адрес и тайм-аут проверки у «Пинга»). Отделяется волоском; null — список без подвала.
    /// </param>
    /// <param name="onClosed">Вызывается при закрытии — сюда вешается коммит полей подвала.</param>
    public static void Show(Control anchor, IReadOnlyList<ChoiceItem> items, Control? footer = null, Action? onClosed = null)
    {
        if (items.Count == 0)
        {
            return;
        }

        var column = new StackPanel { Width = Width, Spacing = 8 };
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterTheme = anchor.FindResource("IncyFlyoutTheme") as ControlTheme,
            Content = column,
        };

        foreach (var item in items)
        {
            column.Children.Add(BuildOption(item, flyout));
        }

        if (footer is not null)
        {
            // Волосок отделяет параметры от выбора. Кисть — ДИНАМИЧЕСКИЙ ресурс, а не разовое чтение:
            // тема (в т.ч. монохром) может смениться при открытом флайауте, а замороженное значение
            // ровно это и ломает — то же правило, что «{DynamicResource Brush.*} only» в разметке.
            var divider = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
            divider.Bind(Border.BackgroundProperty, new DynamicResourceExtension("Brush.OutlineVariant"));
            column.Children.Add(divider);
            column.Children.Add(footer);
        }

        if (onClosed is not null)
        {
            flyout.Closed += (_, _) => onClosed();
        }

        FlyoutBase.SetAttachedFlyout(anchor, flyout);
        FlyoutBase.ShowAttachedFlyout(anchor);
    }

    /// <summary>
    /// Один вариант: <c>Border.Selectable</c> (покой/ховер/выбран/фокус — всё из GlobalStyles) с
    /// зарезервированным слотом галочки. Клавиатура равноправна с мышью: вариант — таб-стоп,
    /// Enter/Space выбирают. Выбор применяется и СРАЗУ закрывает список — подтверждать нечего.
    /// </summary>
    private static Border BuildOption(ChoiceItem item, Flyout flyout)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = item.Label, TextWrapping = TextWrapping.Wrap };
        label.Classes.Add("Body");
        text.Children.Add(label);
        if (item.Hint.IsNotEmpty())
        {
            var hint = new TextBlock { Text = item.Hint, TextWrapping = TextWrapping.Wrap };
            hint.Classes.Add("Subtitle");
            text.Children.Add(hint);
        }

        var check = new PathIcon { Data = _check, VerticalAlignment = VerticalAlignment.Center };
        check.Classes.Add("Check");

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        grid.Children.Add(text);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);

        var option = new Border
        {
            Child = grid,
            Focusable = true,
            IsTabStop = true,
        };
        option.Classes.Add("Selectable");
        if (item.Selected)
        {
            option.Classes.Add("selected");
        }
        // Вспомогательным технологиям вариант объявляется своей подписью, а не склейкой детей.
        // Состояние выбора им не передаётся: у Avalonia 12 нет присоединённого IsSelected для
        // элемента, не входящего в селектор, а выдумывать для этого строку копирайта нельзя.
        AutomationProperties.SetName(option, item.Label);

        void Activate()
        {
            item.Choose();
            flyout.Hide();
        }

        option.Tapped += (_, _) => Activate();
        option.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                Activate();
                e.Handled = true;
            }
        };
        return option;
    }
}
