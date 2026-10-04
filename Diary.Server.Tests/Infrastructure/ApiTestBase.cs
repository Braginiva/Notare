using System.Net;
using System.Text;
using System.Text.Json;

namespace Diary.Server.Tests.Infrastructure;

/// <summary>
/// Базовый класс интеграционных тестов: свой экземпляр сервера
/// и своя временная БД на каждый тест (полная изоляция).
/// </summary>
public abstract class ApiTestBase : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected TestServerFactory Factory { get; }
    protected HttpClient Client { get; }

    protected ApiTestBase()
    {
        Factory = new TestServerFactory();
        Factory.EnsureSchema();
        Client = Factory.CreateClient();
    }

    // ---------- HTTP-хелперы ----------

    protected Task<HttpResponseMessage> PostJsonAsync(string url, object payload) =>
        SendJsonAsync(HttpMethod.Post, url, payload);

    protected Task<HttpResponseMessage> PutJsonAsync(string url, object payload) =>
        SendJsonAsync(HttpMethod.Put, url, payload);

    private async Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string url, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await Client.SendAsync(new HttpRequestMessage(method, url) { Content = content });
    }

    /// <summary>Читает тело ответа как JsonElement (клон — документ закрывается).</summary>
    protected static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    // ---------- Создание тестовых данных ----------

    protected async Task<Guid> CreateNoteAsync(
        string title = "Тестовая заметка",
        string content = "Содержимое",
        Guid? folderId = null)
    {
        var id = Guid.NewGuid();
        var response = await PostJsonAsync("/notes", new
        {
            id,
            title,
            content,
            createdAt = DateTime.UtcNow,
            folderId
        });
        response.EnsureSuccessStatusCode();
        return id;
    }

    protected async Task<Guid> CreateFolderAsync(string name = "Тестовая папка")
    {
        var id = Guid.NewGuid();
        var response = await PostJsonAsync("/folders", new
        {
            id,
            name,
            createdAt = DateTime.UtcNow
        });
        response.EnsureSuccessStatusCode();
        return id;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }
}
