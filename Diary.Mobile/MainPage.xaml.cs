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
}