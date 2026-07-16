using Avalonia;
using Avalonia.Controls;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Экран синхронизации аккаунта (E3): полноэкранный оверлей «Добавляем аккаунт / Загружаем
/// подписки…», который держится, пока идёт пост-логин импорт (AccountViewModel.IsImportingAccount).
/// Видимостью управляет MainWindow.ApplyShellVisibility (3-way gate). DataContext не требуется —
/// разметка статична. Дуга-спиннер крутится ТОЛЬКО пока оверлей виден (класс .spinning навешивается
/// по IsVisible), чтобы не тикать бесконечную анимацию за кадром.
/// </summary>
public partial class AccountSyncView : UserControl
{
    public AccountSyncView()
    {
        InitializeComponent();
        UpdateSpinner();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateSpinner();
        }
    }

    private void UpdateSpinner()
    {
        if (IsVisible)
        {
            if (!SyncSpinner.Classes.Contains("spinning"))
            {
                SyncSpinner.Classes.Add("spinning");
            }
        }
        else
        {
            SyncSpinner.Classes.Remove("spinning");
        }
    }
}
