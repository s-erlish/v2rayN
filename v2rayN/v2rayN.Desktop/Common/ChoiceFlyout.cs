using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
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
/// строки.
///
/// СПИСОК ОТКРЫВАЕТСЯ ВНИЗ И ОСТАЁТСЯ ВНИЗУ. Владелец: «чтобы они ниже открывались, а не сверху».
/// По умолчанию позиционер Avalonia ПЕРЕВОРАЧИВАЕТ поповер вверх, как только снизу не хватает места,
/// — и список накрывал ту самую строку, по которой только что нажали, читаясь как совсем другой
/// контрол. <see cref="PopupPositionerConstraintAdjustment"/> без <c>FlipY</c> запрещает переворот:
/// список только СДВИГАЕТСЯ в пределах экрана. Чтобы сдвигать было куда, он и должен быть маленьким —
/// см. <see cref="MaxHeight"/> и правило «только варианты, ничего кроме».
///
/// ТОЛЬКО ВАРИАНТЫ. Ни полей, ни подвала, ни «применить»: «как бы как доп меню просто где выбрать
/// можно, маленькое». Всё, что относится к настройке, но выбором НЕ является, на этой поверхности не
/// живёт — иначе она перестаёт быть меню и снова становится окном, только без рамки.
/// </summary>
public static class ChoiceFlyout
{
    // Галочка выбранного варианта (ic_check_24dp) — тот же путь, что у строк выбора в разметке.
    private static readonly Geometry _check = Geometry.Parse("M9,16.17L4.83,12l-1.42,1.41L9,19 21,7l-1.41,-1.41z");

    /// <summary>
    /// Ширину задаёт САМЫЙ ДЛИННЫЙ ВАРИАНТ, а не константа. Здесь стояла фиксированная 264: список
    /// масштаба («100 %», «125 %») выходил втрое шире своего содержимого и читался плитой, а не меню —
    /// «слишком как-то широко и огромно выглядит». Теперь колонка меряется по контенту и лишь
    /// зажимается: <see cref="MinWidth"/>, чтобы двухсимвольное значение не давало обрубок, и
    /// <see cref="MaxWidth"/>, чтобы вариант с пояснением переносился, а не растил меню в панель.
    /// Обе величины на шкале 8.
    /// </summary>
    private const double MinWidth = 176d;

    private const double MaxWidth = 300d;

    /// <summary>
    /// Потолок высоты списка. Пять компактных вариантов помещаются целиком; всё, что длиннее,
    /// прокручивается ВНУТРИ списка, а не растит его до высоты окна. Это и есть то, что делает запрет
    /// переворота безопасным: низкому списку почти всегда хватает места под строкой, и правило
    /// «открывается вниз» перестаёт зависеть от того, где именно на экране оказалась строка.
    /// </summary>
    private const double MaxHeight = 280d;

    /// <summary>Зазор между вариантами. 4 по единой шкале: список — меню, а не набор карточек.</summary>
    private const double Gap = 4d;

    /// <summary>
    /// Раскрывает список вариантов у <paramref name="anchor"/>. Флайаут собирается на КАЖДЫЙ показ:
    /// и набор вариантов, и текущий выбор к этому моменту уже другие, а живой перевод подписей ловится
    /// тем же способом — строки берутся в момент открытия.
    /// </summary>
    /// <param name="anchor">Строка настройки. Она же цель показа и якорь позиционирования.</param>
    /// <param name="items">Варианты, ровно один из которых <see cref="ChoiceItem.Selected"/>.</param>
    public static void Show(Control anchor, IReadOnlyList<ChoiceItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var column = new StackPanel { MinWidth = MinWidth, MaxWidth = MaxWidth, Spacing = Gap };
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedRight,
            // БЕЗ FlipY: позиционеру разрешено только сдвигать список, но не переворачивать его над
            // строкой. SlideX/SlideY держат его в пределах экрана у правого и нижнего края.
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.SlideX
                | PopupPositionerConstraintAdjustment.SlideY,
            FlyoutPresenterTheme = anchor.FindResource("IncyChoiceFlyoutTheme") as ControlTheme,
            Content = new ScrollViewer
            {
                MaxHeight = MaxHeight,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = column,
            },
        };

        foreach (var item in items)
        {
            column.Children.Add(BuildOption(item, flyout));
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
        // Компактная мера той же строки выбора (48 вместо 56, паддинг 12,8): меню обязано быть
        // низким, иначе запрет переворота вверх нечем обеспечить у нижнего края экрана.
        option.Classes.Add("compact");
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
