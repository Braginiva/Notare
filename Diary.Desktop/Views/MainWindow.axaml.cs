using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Diary.Desktop.Models;
using Diary.Desktop.ViewModels;

namespace Diary.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // Правый клик → «Удалить»: спрашиваем подтверждение, потом выполняем команду VM.
    private async void OnDeleteFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Folder folder }) return;
        if (ViewModel is not { } vm) return;

        var dialog = new ConfirmDialog(
            "Удалить папку?",
            $"Папка «{folder.Name}» и все её заметки будут удалены. " +
            "В течение 5 секунд действие можно отменить.");

        if (await dialog.ShowDialog<bool?>(this) == true)
            await vm.DeleteFolderCommand.ExecuteAsync(folder);
    }

    private async void OnDeleteNoteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Note note }) return;
        if (ViewModel is not { } vm) return;

        var dialog = new ConfirmDialog(
            "Удалить заметку?",
            $"Заметка «{note.Title}» будет удалена. " +
            "В течение 5 секунд действие можно отменить.");

        if (await dialog.ShowDialog<bool?>(this) == true)
            await vm.DeleteNoteCommand.ExecuteAsync(note);
    }
}
