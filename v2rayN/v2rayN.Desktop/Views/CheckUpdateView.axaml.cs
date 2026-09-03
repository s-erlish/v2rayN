using Avalonia.Controls.Shapes;
using Avalonia.Data.Converters;
using ServiceLib.Models.Dto;
using v2rayN.Desktop.Common;

namespace v2rayN.Desktop.Views;

/// <summary>
/// «Проверить обновление» — подэкран настроек по единому лекалу (screens.md «Подэкраны»):
/// тумблер предварительных выпусков → действия → компоненты → сноска с версией.
///
/// Раньше это было легаси-представление v2rayN: англоязычный ResUI, <c>ListBox</c> с тумблером,
/// типом ядра и текстом в трёх колонках и две кнопки в ряд. Разметка переписана, ЛОГИКА НЕ
/// ДУБЛИРУЕТСЯ — под экраном тот же движковый <see cref="CheckUpdateViewModel"/>: он хранит выбор
/// компонентов в конфиге, качает релизы и пишет ответ в <c>Remarks</c> каждой строки.
///
/// Класс остался <see cref="ReactiveUserControl{T}"/> — на нём висит регистрация в
/// <c>SimpleViewLocator</c>; добавлен <see cref="ISubPage"/>, поэтому экран кладётся на общий стек
/// «назад» оболочки, как остальные подэкраны, и из него есть выход.
/// </summary>
public partial class CheckUpdateView : ReactiveUserControl<CheckUpdateViewModel>, ISubPage
{
    private bool _busy;

    public event EventHandler? BackRequested;

    public CheckUpdateView()
    {
        InitializeComponent();

        // Раньше модель приходила только через DataContext локатора. Подэкран создаётся оболочкой
        // напрямую (new CheckUpdateView()), поэтому модель заводим сами — иначе экран пустой.
        ViewModel ??= new CheckUpdateViewModel();

        // Строки строятся ОДИН раз: у обёртки собственное реактивное IsOn, а Remarks движка уже
        // [Reactive] — значит и тумблер, и живой ответ проверки доезжают до экрана сами.
        // Пересборка списка на каждый тап (как было) моргала всей карточкой.
        // Безымянный компонент СТРОКОЙ НЕ СТАНОВИТСЯ. Раньше такая строка получала подпись «—»:
        // тумблер без названия, который непонятно что включает. Имя берётся из CoreTypeForStorage,
        // и оно же — ключ, под которым выбор сохраняется в конфиг; без имени строка не может ни
        // назваться, ни запомниться.
        listComponents.ItemsSource = ViewModel.CheckUpdateModels
            .Where(m => m.CoreTypeForStorage.IsNotEmpty())
            .Select((m, i) => new ComponentRow(m, i > 0))
            .ToList();
        txtFoot.Text = L.F("Update_Foot", Utils.GetVersionInfo());

        btnBack.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);

        // Тап по строке-тумблеру переключает тумблер — но не когда источником тапа был он сам.
        RowPreRelease.Tapped += (_, e) =>
        {
            if (!SubPageUtil.OriginatedIn<ToggleSwitch>(e.Source))
            {
                togEnableCheckPreReleaseUpdate.IsChecked = togEnableCheckPreReleaseUpdate.IsChecked != true;
            }
        };

        RowCheckOnly.Tapped += (_, _) => Run(check: true);
        RowInstall.Tapped += (_, _) => Run(check: false);

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.EnableCheckPreReleaseUpdate, v => v.togEnableCheckPreReleaseUpdate.IsChecked).DisposeWith(disposables);
        });
    }

    /// <summary>Тап по строке компонента переключает его тумблер (кроме тапа по самому тумблеру).</summary>
    private void OnComponentRowTapped(object? sender, TappedEventArgs e)
    {
        if (SubPageUtil.OriginatedIn<ToggleSwitch>(e.Source))
        {
            return;
        }
        if (sender is Border { DataContext: ComponentRow row })
        {
            row.IsOn = !row.IsOn;
        }
    }

    /// <summary>
    /// Запускает движковую команду. Пока она идёт, действия теряют акцент и не откликаются: акцентный
    /// текст читается как «нажми», а нажимать в этот момент нечего (то же правило, что в «Файлах
    /// ресурсов»). В плитке ЗАПУЩЕННОЙ строки глиф подменяется вращающимся кругом — motion.md
    /// («Пинг и обновление подписки»): «на месте иконки вращается круг».
    /// </summary>
    private void Run(bool check)
    {
        if (_busy || ViewModel is null)
        {
            return;
        }
        _busy = true;
        SetBusy(true, check);

        var cmd = check ? ViewModel.CheckOnlyCmd : ViewModel.CheckUpdateCmd;
        cmd.Execute().Subscribe(
            _ => { },
            _ => Done(),
            Done);

        void Done() => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            SetBusy(false, check);
        });
    }

    /// <param name="busy">идёт ли команда.</param>
    /// <param name="check">какая именно строка её запустила — только у неё крутится круг.</param>
    private void SetBusy(bool busy, bool check)
    {
        foreach (var (text, row) in new[] { (txtCheckOnly, RowCheckOnly), (txtInstall, RowInstall) })
        {
            text.Classes.Set("accent", !busy);
            row.Classes.Set("tap", !busy);
        }

        Spin(icoCheckOnly, spinCheckOnly, busy && check);
        Spin(icoInstall, spinInstall, busy && !check);
    }

    /// <summary>Подмена глифа кругом в том же слоте: ничего вокруг не сдвигается.</summary>
    private static void Spin(PathIcon glyph, Ellipse ring, bool on)
    {
        glyph.IsVisible = !on;
        ring.IsVisible = on;
        ring.Classes.Set("spinning", on);
    }

    /// <summary>
    /// Обёртка строки списка: несёт разделитель (он рисуется перед каждой строкой кроме первой),
    /// человеческое имя компонента и реактивный переключатель. Имена ядра и Geo-баз показываем так,
    /// как их зовут в этом приложении: «v2rayN» — это оно само, и апстримное имя в интерфейсе
    /// departament ни о чём не говорит пользователю.
    ///
    /// <c>IsOn</c> нужен потому, что <see cref="CheckUpdateModel.IsSelected"/> — обычное свойство без
    /// уведомления: биндиться прямо к нему нельзя, тумблер не узнал бы о смене.
    /// </summary>
    public sealed class ComponentRow : ReactiveObject
    {
        private bool _isOn;

        public ComponentRow(CheckUpdateModel model, bool showDivider)
        {
            Model = model;
            ShowDivider = showDivider;
            _isOn = model.IsSelected == true;
        }

        public CheckUpdateModel Model { get; }

        public bool ShowDivider { get; }

        public bool IsOn
        {
            get => _isOn;
            set
            {
                this.RaiseAndSetIfChanged(ref _isOn, value);
                Model.IsSelected = value;
            }
        }

        public string Title => Model.CoreTypeForStorage switch
        {
            "GeoFiles" => L.T("Update_GeoFiles"),
            "v2rayN" => L.T("Update_App"),
            "sing_box" => "sing-box",
            // Пустого варианта здесь больше нет: безымянные строки отсеиваются при сборке списка.
            var other => other,
        };
    }
}

/// <summary>
/// Есть ли у компонента ЖИВОЙ ответ проверки. До первой проверки движок кладёт в <c>Remarks</c> свою
/// менюшную надпись «Обновить» (<c>ResUI.menuCheckUpdate</c>) — как подзаголовок строки она обещает
/// действие, которого в строке нет, поэтому такая подпись не показывается вовсе. Всё остальное —
/// настоящий ответ («… уже последней версии», «не поддерживается», ход проверки) — показывается.
/// Локален для этого экрана, в GlobalResources не выносится.
/// </summary>
public sealed class UpdateAnswerVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value?.ToString();
        return !string.IsNullOrWhiteSpace(s) && !string.Equals(s, ResUI.menuCheckUpdate, StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
