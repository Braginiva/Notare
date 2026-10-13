using Diary.Mobile.Models;
using Diary.Mobile.ViewModels;

namespace Diary.Mobile;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel _vm;

    public MainPage(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        BindingContext = vm;
    }

    // Меню ⋮
    private async void OnMenuClicked(object? sender, EventArgs e)
    {
        var action = await DisplayActionSheetAsync("Меню", "Отмена", null, "Обновить данные");

        if (action == "Обновить данные")
        {
            await _vm.LoadInitialDataAsync();
        }
    }

    // Свайп «Удалить»: спрашиваем подтверждение, потом выполняем команду VM.
    private async void OnDeleteFolderClicked(object? sender, EventArgs e)
    {
        if (sender is not SwipeItem { BindingContext: LocalFolder folder }) return;

        var confirmed = await DisplayAlertAsync(
            "Удалить папку?",
            $"Папка «{folder.Name}» и все её заметки будут удалены. " +
            "В течение 5 секунд действие можно отменить.",
            "Удалить", "Отмена");

        if (confirmed)
            await _vm.DeleteFolderCommand.ExecuteAsync(folder);
    }

    private async void OnDeleteNoteClicked(object? sender, EventArgs e)
    {
        if (sender is not SwipeItem { BindingContext: LocalNote note }) return;

        var confirmed = await DisplayAlertAsync(
            "Удалить заметку?",
            $"Заметка «{note.Title}» будет удалена. " +
            "В течение 5 секунд действие можно отменить.",
            "Удалить", "Отмена");

        if (confirmed)
            await _vm.DeleteNoteCommand.ExecuteAsync(note);
    }
}
