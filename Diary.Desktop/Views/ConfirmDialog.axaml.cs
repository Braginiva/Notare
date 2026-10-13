using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Diary.Desktop.Views;

// Маленькое окно подтверждения «Удалить?».
// Показывается из MainWindow; Close(true) — пользователь подтвердил.
public partial class ConfirmDialog : Window
{
    // Parameterless ctor — для XAML-загрузчика (и цепочки конструкторов).
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(string title, string message) : this()
    {
        Title = "Подтверждение";
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
