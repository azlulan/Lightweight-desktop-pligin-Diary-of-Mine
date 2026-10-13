using System.IO;

namespace DiaryOfMine.Desktop.Services;

internal sealed class FolderPickerService
{
    public string? PickFolder(string description, string currentPath)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(currentPath) ? currentPath : string.Empty
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}
