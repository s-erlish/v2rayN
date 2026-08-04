using Avalonia.Platform.Storage;
using v2rayN.Desktop.Manager;
using v2rayN.Desktop.Views;

namespace v2rayN.Desktop.Common;

internal class UI
{
    private static readonly string caption = Global.AppName;

    /// <summary>
    /// Вопрос «да/нет». <paramref name="destructive"/> красит подтверждающую кнопку в красный и
    /// требует ГЛАГОЛА в <paramref name="confirmLabel"/> («Удалить»), а не «Подтвердить»:
    /// деструктивное действие обязано называть, что оно сделает, и быть главным действием диалога.
    /// </summary>
    public static async Task<ButtonResult> ShowYesNo(string msg, string? confirmLabel = null, bool destructive = false)
    {
        var owner = WindowDialog.TryGetOwnerWindow();
        var box = new MessageBoxDialog(caption, msg, confirmLabel, destructive);
        var result = await box.ShowDialog<ButtonResult>(owner);
        return result == ButtonResult.Yes ? ButtonResult.Yes : ButtonResult.No;
    }

    public static async Task<string?> OpenFileDialog(FilePickerFileType? filter)
    {
        var sp = GetStorageProvider();
        if (sp is null)
        {
            return null;
        }

        // Start async operation to open the dialog.
        var files = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = filter is null ? [FilePickerFileTypes.All, FilePickerFileTypes.ImagePng] : [filter]
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public static async Task<string?> SaveFileDialog(string filter)
    {
        var sp = GetStorageProvider();
        if (sp is null)
        {
            return null;
        }

        // Start async operation to open the dialog.
        var files = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
        });

        return files?.TryGetLocalPath();
    }

    private static IStorageProvider? GetStorageProvider()
    {
        var owner = WindowDialog.TryGetOwnerWindow();
        var topLevel = TopLevel.GetTopLevel(owner);
        return topLevel?.StorageProvider;
    }
}
