using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace Diary.Desktop.Services;

// Поднимает сервер синхронизации и следит за ним.
// Если сервер уже работает (запущен вручную) — просто подключаемся.
public static class ServerLauncher
{
    private const string PingUrl = "http://localhost:5092/ping";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static Process? _process;
    private static DateTime _startedAtUtc;
    private static volatile bool _supervised = true;

    // Быстрая проверка: отвечает ли сервер.
    public static async Task<bool> IsAliveAsync()
    {
        try
        {
            var response = await Http.GetAsync(PingUrl);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // Гарантирует работающий сервер. true — сервер отвечает.
    public static async Task<bool> EnsureStartedAsync()
    {
        if (await IsAliveAsync()) return true;

        // Уже мы его запускали — просто ждём, пока поднимется.
        var alreadyStarting = _process is { HasExited: false };

        if (!alreadyStarting)
        {
            var exe = FindServerExecutable();
            if (exe is null)
            {
                Debug.WriteLine("→ Сервер: exe не найден, работаем автономно");
                return false;
            }

            if (!Start(exe)) return false;
        }

        // Ждём до ~15 секунд, пока поднимется.
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(500);

            if (await IsAliveAsync())
            {
                Debug.WriteLine("→ Сервер отвечает");
                return true;
            }

            if (_process is { HasExited: true })
            {
                Debug.WriteLine("→ Сервер упал сразу после старта");
                return false;
            }
        }

        Debug.WriteLine("→ Сервер не поднялся за 15 секунд");
        return false;
    }

    // Сервер упал — перезапускаем, если проработал нормально (>30 с).
    // Быстрое падение означает ошибку конфигурации — не крутим бесконечный цикл.
    private static void OnServerExited(object? sender, EventArgs e)
    {
        if (!_supervised) return;

        var uptime = DateTime.UtcNow - _startedAtUtc;
        Debug.WriteLine($"→ Сервер завершился (прожил {uptime.TotalSeconds:F0} с)");

        if (uptime < TimeSpan.FromSeconds(30)) return;

        // Уже кто-то другой поднял сервер — не мешаем.
        if (IsAliveAsync().GetAwaiter().GetResult()) return;

        var exe = FindServerExecutable();
        if (exe is not null) Start(exe);
    }

    // Перестаём следить за сервером (при выходе из приложения).
    // Сам сервер при этом продолжает работать — телефон остаётся в синке.
    public static void StopSupervising()
    {
        _supervised = false;
        if (_process is not null)
            _process.Exited -= OnServerExited;
    }

    private static bool Start(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                // Без консольного окна; настройки — как у launchSettings.
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = FindDataDirectory(exePath),
            };
            psi.Environment["ASPNETCORE_URLS"] = "http://0.0.0.0:5092";
            psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

            var process = Process.Start(psi);
            if (process is null) return false;

            process.EnableRaisingEvents = true;
            process.Exited += OnServerExited;

            _process = process;
            _startedAtUtc = DateTime.UtcNow;

            Debug.WriteLine($"→ Сервер запущен (pid {process.Id}), CWD = {psi.WorkingDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА запуска сервера: {ex.Message}");
            return false;
        }
    }

    // Ищем exe сервера: рядом с десктопом (прод-раскладка)
    // или в dev-структуре репозитория (bin/Debug/net10.0).
    private static string? FindServerExecutable()
    {
        var baseDir = AppContext.BaseDirectory;

        var sibling = Path.Combine(baseDir, "Notare.Server.exe");
        if (File.Exists(sibling)) return sibling;

        // Поднимаемся вверх до корня репозитория (в нём лежит Diary.slnx).
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Diary.slnx")))
            {
                var dev = Path.Combine(dir.FullName, "Diary.Server", "bin", "Debug", "net10.0", "Notare.Server.exe");
                return File.Exists(dev) ? dev : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // Рабочая папка = где лежит настоящая diary.db,
    // иначе сервер создаст пустую базу рядом с exe.
    private static string FindDataDirectory(string exePath)
    {
        var exeDir = Path.GetDirectoryName(exePath)!;

        var dir = new DirectoryInfo(exeDir);
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "diary.db"))) return dir.FullName;
            dir = dir.Parent;
        }

        return exeDir;
    }
}
