using System.Net;
using System.Text.Json;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

public class FoldersEndpointsTests : ApiTestBase
{
    private static List<Guid> ExtractIds(JsonElement array) =>
        array.EnumerateArray().Select(f => f.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task PostFolder_Returns201_WithName()
    {
        var id = Guid.NewGuid();

        var response = await PostJsonAsync("/folders", new
        {
            id,
            name = "Рабочие",
            createdAt = DateTime.UtcNow
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Рабочие", body.GetProperty("name").GetString());
        Assert.False(body.GetProperty("isDeleted").GetBoolean());
    }

    [Fact]
    public async Task GetFolder_ById_ReturnsFolder()
    {
        var id = await CreateFolderAsync("Личные");

        var response = await Client.GetAsync($"/folders/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal("Личные", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetFolder_UnknownId_Returns404()
    {
        var response = await Client.GetAsync($"/folders/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PutFolder_UpdatesName()
    {
        var id = await CreateFolderAsync("Старое имя");

        var response = await PutJsonAsync($"/folders/{id}", new
        {
            id,
            name = "Новое имя",
            createdAt = DateTime.UtcNow
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal("Новое имя", body.GetProperty("name").GetString());

        var get = await Client.GetAsync($"/folders/{id}");
        var persisted = await ReadAsync(get);
        Assert.Equal("Новое имя", persisted.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetFolders_ExcludesDeletedFolder()
    {
        var deleted = await CreateFolderAsync("Удалённая");
        var alive = await CreateFolderAsync("Живая");

        await Client.DeleteAsync($"/folders/{deleted}");

        var response = await Client.GetAsync("/folders");
        var ids = ExtractIds(await ReadAsync(response));

        Assert.Contains(alive, ids);
        Assert.DoesNotContain(deleted, ids);
    }

    [Fact]
    public async Task DeleteFolder_Returns204_AndIsIdempotent()
    {
        var id = await CreateFolderAsync();

        var first = await Client.DeleteAsync($"/folders/{id}");
        var second = await Client.DeleteAsync($"/folders/{id}");

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task DeleteFolder_SoftDeletesItsNotes_ButKeepsForeignNotes()
    {
        var folderId = await CreateFolderAsync("Папка с заметками");
        var noteIn1 = await CreateNoteAsync("В папке 1", folderId: folderId);
        var noteIn2 = await CreateNoteAsync("В папке 2", folderId: folderId);
        var noteOutside = await CreateNoteAsync("Сирота без папки");

        var delete = await Client.DeleteAsync($"/folders/{folderId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Папка скрыта.
        var folderGet = await Client.GetAsync($"/folders/{folderId}");
        Assert.Equal(HttpStatusCode.NotFound, folderGet.StatusCode);

        // Её заметки скрыты, чужая — живёт.
        var notes = ExtractIds(await ReadAsync(await Client.GetAsync("/notes")));
        Assert.DoesNotContain(noteIn1, notes);
        Assert.DoesNotContain(noteIn2, notes);
        Assert.Contains(noteOutside, notes);
    }

    [Fact]
    public async Task GetFolderNotes_ReturnsOnlyLiveNotesOfFolder()
    {
        var folderId = await CreateFolderAsync();
        var inFolder = await CreateNoteAsync("Живая в папке", folderId: folderId);
        var hiddenInFolder = await CreateNoteAsync("Удалённая в папке", folderId: folderId);
        await CreateNoteAsync("Чужая заметка");

        await Client.DeleteAsync($"/notes/{hiddenInFolder}");

        var response = await Client.GetAsync($"/folders/{folderId}/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ids = ExtractIds(await ReadAsync(response));
        Assert.Single(ids);
        Assert.Equal(inFolder, ids[0]);
    }

    [Fact]
    public async Task GetFolderNotes_UnknownFolder_Returns404()
    {
        var response = await Client.GetAsync($"/folders/{Guid.NewGuid()}/notes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetFolderNotes_DeletedFolder_Returns404()
    {
        var folderId = await CreateFolderAsync();
        await Client.DeleteAsync($"/folders/{folderId}");

        var response = await Client.GetAsync($"/folders/{folderId}/notes");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
