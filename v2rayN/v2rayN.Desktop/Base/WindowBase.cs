namespace v2rayN.Desktop.Base;

public class WindowBase<TViewModel> : ReactiveWindow<TViewModel> where TViewModel : class
{
    public WindowBase()
    {
        Loaded += OnLoaded;
        Loaded += (s, e) =>
        {
            if (Owner != null && !ShowInTaskbar)
            {
                CanMinimize = false;
            }
        };
    }

    private void ReactiveWindowBase_Closed(object? sender, EventArgs e)
    {
        throw new NotImplementedException();
    }

    protected virtual void OnLoaded(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sizeItem = ConfigHandler.GetWindowSizeItem(AppManager.Instance.Config, GetType().Name);
            if (sizeItem is null)
            {
                return;
            }

            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
            var workingArea = screen.WorkingArea;

            // Верхняя граница = проектный дефолт окна (1120×760), а не вся рабочая область:
            // на большом мониторе сохранённый (в т.ч. когда-то развёрнутый) размер не должен
            // раздувать окно. На малых экранах всё равно ужимаемся под workingArea.
            var maxWidth = Math.Min(1120.0, workingArea.Width / scaling);
            var maxHeight = Math.Min(760.0, workingArea.Height / scaling);
            var width = Math.Min(sizeItem.Width, maxWidth);
            var height = Math.Min(sizeItem.Height, maxHeight);
            var x = workingArea.X + ((workingArea.Width - (width * scaling)) / 2);
            var y = workingArea.Y + ((workingArea.Height - (height * scaling)) / 2);

            Width = width;
            Height = height;
            Position = new PixelPoint((int)x, (int)y);
        }
        catch { }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            // Не персистим развёрнутый/свёрнутый размер — только обычное состояние окна,
            // иначе следующий запуск восстанавливается «на весь экран».
            if (WindowState != WindowState.Normal)
            {
                return;
            }
            ConfigHandler.SaveWindowSizeItem(AppManager.Instance.Config, GetType().Name, Width, Height);
        }
        catch { }
    }
}
