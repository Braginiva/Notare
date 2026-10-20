using Diary.Mobile.Services;
using Diary.Mobile.ViewModels;
using Microsoft.Extensions.Logging;
using SQLitePCL;

namespace Diary.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Инициализация нативного SQLite для Android.
        Batteries_V2.Init();

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // === Регистрация сервисов ===

        // Локальная база на телефоне.
        builder.Services.AddSingleton<LocalDatabase>();

        builder.Services.AddSingleton<SyncService>();

        // ApiClient — адрес берётся из настроек (меняется в меню приложения).
        builder.Services.AddSingleton<ApiClient>(sp => new ApiClient(ServerSettings.HttpUrl));

        // Главная VM — Singleton (общее состояние).
        builder.Services.AddSingleton<MainViewModel>();

        // Страницы — Transient (создаются по запросу).
        builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}