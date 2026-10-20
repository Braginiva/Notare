using System;

namespace Diary.Mobile.Services;

// Адрес сервера на ПК. Хранится в Preferences, чтобы можно было поменять IP
// из меню приложения, а не пересобирать его под каждый новый адрес.
public static class ServerSettings
{
    private const string Key = "server_url";

    // По умолчанию — прежний адрес (когда его не меняли).
    public const string DefaultUrl = "http://192.168.1.175:5092";

    public static string HttpUrl =>
        Preferences.Default.Get(Key, DefaultUrl);

    // ws://.../ws, собранный из http://...
    public static string WsUrl
    {
        get
        {
            var url = HttpUrl;
            if (url.StartsWith("https://", StringComparison.Ordinal))
                url = "wss://" + url["https://".Length..];
            else if (url.StartsWith("http://", StringComparison.Ordinal))
                url = "ws://" + url["http://".Length..];

            return url.TrimEnd('/') + "/ws";
        }
    }

    public static void Save(string url)
    {
        Preferences.Default.Set(Key, url.Trim().TrimEnd('/'));
    }
}
