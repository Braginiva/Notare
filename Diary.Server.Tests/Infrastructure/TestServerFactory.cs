using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Diary.Server.Tests.Infrastructure;

/// <summary>
/// Поднимает настоящий сервер (Program.cs) на временной SQLite-базе в %TEMP%.
/// Рабочий diary.db проекта тестами не открывается и не изменяется.
/// </summary>
public sealed class TestServerFactory : WebApplicationFactory<Program>
{
    public string DbPath { get; } =
        Path.Combine(Path.GetTempPath(), $"notare-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Заменяем подключение к БД с рабочей diary.db на временную.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={DbPath}"));
        });
    }

    /// <summary>Применяет миграции к временной базе.</summary>
    public void EnsureSchema()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        try
        {
            SqliteConnection.ClearAllPools();
            File.Delete(DbPath);
        }
        catch
        {
            // Временный файл в %TEMP% — его удаление не критично.
        }
    }
}
