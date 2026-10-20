using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Diary.Desktop.Views;

// Маленькое окно с сообщением и одной кнопкой «Понятно».
public partial class InfoDialog : Window
{
    // Parameterless ctor — для XAML-загрузчика.
    public InfoDialog()
    {
        InitializeComponent();
    }

    public InfoDialog(string title, string message) : this()
    {
        TitleText.Text = title;
        MessageText.Text = message;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(true);
}
