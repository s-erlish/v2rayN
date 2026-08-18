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

        listComponents.ItemsSource = BuildRows(ViewModel);
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
            row.Model.IsSelected = row.Model.IsSelected != true;
            // IsSelected — обычное свойство модели движка, без уведомления: перечитываем строки,
            // чтобы тумблер показал новое состояние.
            listComponents.ItemsSource = BuildRows(ViewModel!);
        }
    }

    /// <summary>
    /// Запускает движковую команду. Пока она идёт, действия теряют акцент и не откликаются: акцентный
    /// текст читается как «нажми», а нажимать в этот момент нечего (то же правило, что в «Файлах
    /// ресурсов»).
    /// </summary>
    private void Run(bool check)
    {
        if (_busy || ViewModel is null)
        {
            return;
        }
        _busy = true;
        SetBusy(true);
        txtCheckState.Text = L.T("Update_Checking");

        var cmd = check ? ViewModel.CheckOnlyCmd : ViewModel.CheckUpdateCmd;
        cmd.Execute().Subscribe(
            _ => { },
            _ => Done(),
            Done);

        void Done() => Dispatcher.UIThread.Post(() =>
        {
            _busy = false;
            SetBusy(false);
            txtCheckState.Text = string.Empty;
        });
    }

    private void SetBusy(bool busy)
    {
        foreach (var (text, row) in new[] { (txtCheckOnly, RowCheckOnly), (txtInstall, RowInstall) })
        {
            text.Classes.Set("accent", !busy);
            row.Classes.Set("tap", !busy);
        }
    }

    private static List<ComponentRow> BuildRows(CheckUpdateViewModel vm) =>
        vm.CheckUpdateModels.Select((m, i) => new ComponentRow(m, i > 0)).ToList();

    /// <summary>
    /// Обёртка строки списка: несёт разделитель (он рисуется перед каждой строкой кроме первой) и
    /// человеческое имя компонента. Имена ядра и Geo-баз показываем так, как их зовут в этом
    /// приложении: «v2rayN» — это оно само, и апстримное имя в интерфейсе departament ни о чём не
    /// говорит пользователю.
    /// </summary>
    public sealed class ComponentRow
    {
        public ComponentRow(CheckUpdateModel model, bool showDivider)
        {
            Model = model;
            ShowDivider = showDivider;
        }

        public CheckUpdateModel Model { get; }

        public bool ShowDivider { get; }

        public string Title => Model.CoreTypeForStorage switch
        {
            "GeoFiles" => L.T("Update_GeoFiles"),
            "v2rayN" => L.T("Update_App"),
            "sing_box" => "sing-box",
            var other when other.IsNotEmpty() => other,
            _ => "—",
        };
    }
}
