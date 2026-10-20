using Diary.Mobile.Models;
using Diary.Mobile.Services;
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
        var action = await DisplayActionSheetAsync("Меню", "Отмена", null,
            "Обновить данные", "Адрес сервера");

        if (action == "Обновить данные")
        {
            var ok = await _vm.SyncNowAsync();
            if (!ok)
            {
                await DisplayAlertAsync("Нет связи с ПК",
                    "Не удалось синхронизироваться с компьютером. Проверьте, что сервер " +
                    "запущен на ПК и телефон в той же Wi-Fi сети. Текущий адрес сервера " +
                    "можно поменять в меню «Адрес сервера».",
                    "ОК");
            }
        }
        else if (action == "Адрес сервера")
        {
            var input = await DisplayPromptAsync(
                "Адрес сервера",
                "Например, http://192.168.1.175:5092",
                "Сохранить", "Отмена",
                initialValue: ServerSettings.HttpUrl,
                keyboard: Keyboard.Url);

            if (string.IsNullOrWhiteSpace(input)) return;

            var ok = await _vm.ApplyServerUrlAsync(input);
            if (!ok)
            {
                await DisplayAlertAsync("Адрес сохранён",
                    "Но сервер по этому адресу пока не отвечает. Он заработает, " +
                    "когда ПК будет доступен в сети.",
                    "ОК");
            }
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
