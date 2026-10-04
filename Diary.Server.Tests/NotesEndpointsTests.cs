using System.Net;
using System.Text.Json;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

public class NotesEndpointsTests : ApiTestBase
{
    [Fact]
    public async Task GetNotes_EmptyDatabase_ReturnsEmptyArray()
    {
        var response = await Client.GetAsync("/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Empty(body.EnumerateArray());
    }

    [Fact]
    public async Task PostNote_Returns201_WithLocationHeader()
    {
        var id = Guid.NewGuid();

        var response = await PostJsonAsync("/notes", new
        {
            id,
            title = "Новая заметка",
            content = "Текст",
            createdAt = DateTime.UtcNow,
            folderId = (Guid?)null
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/notes/{id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task PostNote_SavesFields_AndMarksAsNotDeleted()
    {
        var id = Guid.NewGuid();

        var response = await PostJsonAsync("/notes", new
        {
            id,
            title = "Заголовок",
            content = "Содержимое",
            createdAt = DateTime.UtcNow,
            folderId = (Guid?)null
        });

        var body = await ReadAsync(response);

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Заголовок", body.GetProperty("title").GetString());
        Assert.Equal("Содержимое", body.GetProperty("content").GetString());
        Assert.False(body.GetProperty("isDeleted").GetBoolean());
        Assert.NotEqual(default, body.GetProperty("updatedAt").GetDateTime());
    }

    [Fact]
    public async Task GetNote_ById_ReturnsCreatedNote()
    {
        var id = await CreateNoteAsync("Моя заметка", "Текст заметки");

        var response = await Client.GetAsync($"/notes/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Моя заметка", body.GetProperty("title").GetString());
        Assert.Equal("Текст заметки", body.GetProperty("content").GetString());
    }

    [Fact]
    public async Task GetNote_UnknownId_Returns404()
    {
        var response = await Client.GetAsync($"/notes/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetNote_AfterDelete_Returns404()
    {
        var id = await CreateNoteAsync();

        var delete = await Client.DeleteAsync($"/notes/{id}");
        delete.EnsureSuccessStatusCode();

        var get = await Client.GetAsync($"/notes/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task GetNotes_ExcludesDeletedNote()
    {
        var deleted = await CreateNoteAsync("Удалённая");
        var alive = await CreateNoteAsync("Живая");

        await Client.DeleteAsync($"/notes/{deleted}");

        var response = await Client.GetAsync("/notes");
        var body = await ReadAsync(response);
        var ids = body.EnumerateArray()
            .Select(n => n.GetProperty("id").GetGuid())
            .ToList();

        Assert.Contains(alive, ids);
        Assert.DoesNotContain(deleted, ids);
    }

    [Fact]
    public async Task PutNote_UpdatesTitleContentAndFolderId()
    {
        var folderId = await CreateFolderAsync();
        var id = await CreateNoteAsync("Старый заголовок");

        var response = await PutJsonAsync($"/notes/{id}", new
        {
            id,
            title = "Новый заголовок",
            content = "Новое содержимое",
            createdAt = DateTime.UtcNow,
            folderId
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal("Новый заголовок", body.GetProperty("title").GetString());
        Assert.Equal("Новое содержимое", body.GetProperty("content").GetString());
        Assert.Equal(folderId, body.GetProperty("folderId").GetGuid());
    }

    [Fact]
    public async Task PutNote_UnknownId_Returns404()
    {
        var response = await PutJsonAsync($"/notes/{Guid.NewGuid()}", new
        {
            id = Guid.NewGuid(),
            title = "x",
            content = "y",
            createdAt = DateTime.UtcNow,
            folderId = (Guid?)null
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteNote_Returns204_AndIsIdempotent()
    {
        var id = await CreateNoteAsync();

        var first = await Client.DeleteAsync($"/notes/{id}");
        var second = await Client.DeleteAsync($"/notes/{id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }
}
