using System;
using System.IO;
using Avalonia;
using Avalonia.Styling;

namespace Diary.Desktop.Services;

// Тема приложения: светлая / тёмная / системная.
// Хранится в %AppData%\Notare\theme.txt, применяется при старте.
public static class DesktopTheme
{
    public const string Light = "light";
    public const string Dark = "dark";
    public const string System = "system";

    private static string ThemeFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Notare", "theme.txt");

    // Текущая тема (для галочек в меню).
    public static string Current { get; private set; } = System;

    // Применяет сохранённую тему при старте приложения.
    public static void ApplyStored() => Apply(ReadStored());

    // Применяет выбранную тему и запоминает выбор.
    public static void Apply(string theme)
    {
        if (theme is not (Light or Dark or System))
            theme = System;

        Current = theme;

        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = theme switch
            {
                Light => ThemeVariant.Light,
                Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ThemeFilePath)!);
            File.WriteAllText(ThemeFilePath, theme);
        }
        catch
        {
            // Не смогли сохранить — не критично, тема просто не запомнится.
        }
    }

    private static string ReadStored()
    {
        try
        {
            return File.Exists(ThemeFilePath)
                ? File.ReadAllText(ThemeFilePath).Trim()
                : System;
        }
        catch
        {
            return System;
        }
    }
}
