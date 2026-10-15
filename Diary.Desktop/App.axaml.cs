using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Diary.Desktop.Services;
using Diary.Desktop.ViewModels;
using Diary.Desktop.Views;

namespace Diary.Desktop;

public partial class App : Application
{
    private TrayIcon? _trayIcon;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
            desktop.MainWindow = window;

            SetupTrayIcon(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // Иконка в трее: клик или «Открыть» — показать окно,
    // «Выход» — закрыть приложение (сервер продолжает работать для телефона).
    private void SetupTrayIcon(MainWindow window)
    {
        var openItem = new NativeMenuItem("Открыть");
        openItem.Click += (_, _) => ShowMainWindow(window);

        var exitItem = new NativeMenuItem("Выход");
        exitItem.Click += (_, _) =>
        {
            ServerLauncher.StopSupervising(); // сервер остаётся работать
            window.AllowExit = true;
            window.Close();
            _trayIcon?.Dispose();
        };

        var menu = new NativeMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(exitItem);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Diary.Desktop/Assets/notare.png"))),
            ToolTipText = "Notare",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow(window);

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private static void ShowMainWindow(Window window)
    {
        window.Show();
        window.Activate();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
    }
}
