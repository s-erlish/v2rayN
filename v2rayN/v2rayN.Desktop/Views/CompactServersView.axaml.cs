namespace v2rayN.Desktop.Views;

/// <summary>
/// Compact «Сервера» tab (CA-4): the servers header (title + count + refresh/ping/add + search)
/// over a reused <see cref="ServerListView"/>. Purely a re-host — it binds to the same
/// <see cref="ViewModels.HomeViewModel"/> the host supplies via DataContext; no state of its own.
/// </summary>
public partial class CompactServersView : UserControl
{
    public CompactServersView()
    {
        InitializeComponent();

        // A7: живой фильтр. Поле TwoWay-связано с Profiles.ServerFilter, но VM обновляет список
        // только при пустом вводе (WPF-путь рефрешил по Enter). Гоним RefreshServers на каждый
        // ввод, чтобы список фильтровался по мере набора. VM не трогаем — зовём публичные члены.
        SearchBox.TextChanged += OnSearchTextChanged;
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is not ViewModels.HomeViewModel { Profiles: { } profiles })
        {
            return;
        }

        // Синхронизируем фильтр до рефреша, не полагаясь на порядок Binding vs TextChanged:
        // сеттер [Reactive] триггерит ServerFilterChanged, который синхронно кладёт _serverFilter,
        // а его читает RefreshServers().
        var text = (sender as TextBox)?.Text ?? string.Empty;
        if (profiles.ServerFilter != text)
        {
            profiles.ServerFilter = text;
        }

        await profiles.RefreshServers();
    }
}
