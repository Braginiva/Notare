using System.Net;
using System.Text.Json;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

/// <summary>
/// Restore-поведение: undo удаления заметок и папок
/// + восстановление через PUT с полем isDeleted (синк-клиенты).
/// </summary>
public class RestoreEndpointsTests : ApiTestBase
{
    // ---------- POST /notes/{id}/restore ----------

    [Fact]
    public async Task RestoreNote_AfterDelete_Returns204_AndNoteIsAliveAgain()
    {
        var id = await CreateNoteAsync("Вернись");

        var del = await Client.DeleteAsync($"/notes/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var restore = await Client.PostAsync($"/notes/{id}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        // Заметка снова доступна по id…
        var get = await Client.GetAsync($"/notes/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        // …и снова попадает в общий список.
        var list = await ReadAsync(await Client.GetAsync("/notes"));
        Assert.Contains(list.EnumerateArray(), n => n.GetProperty("id").GetGuid() == id);
    }

    [Fact]
    public async Task RestoreNote_UnknownId_Returns404()
    {
        var response = await Client.PostAsync($"/notes/{Guid.NewGuid()}/restore", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RestoreNote_OnAlreadyLiveNote_Returns204_AndKeepsItLive()
    {
        var id = await CreateNoteAsync("И так живая");

        var restore = await Client.PostAsync($"/notes/{id}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        var get = await Client.GetAsync($"/notes/{id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    // ---------- POST /folders/{id}/restore ----------

    [Fact]
    public async Task RestoreFolder_AfterDelete_RestoresFolder_ButKeepsItsNotesDeleted()
    {
        var folderId = await CreateFolderAsync("Папка");
        var noteId = await CreateNoteAsync(folderId: folderId);

        var del = await Client.DeleteAsync($"/folders/{folderId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var restore = await Client.PostAsync($"/folders/{folderId}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        // Папка жива.
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/folders/{folderId}")).StatusCode);

        // Заметка остаётся удалённой: её восстанавливает клиент явно —
        // так не оживляются заметки, удалённые по отдельности.
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/notes/{noteId}")).StatusCode);
    }

    [Fact]
    public async Task RestoreFolder_UnknownId_Returns404()
    {
        var response = await Client.PostAsync($"/folders/{Guid.NewGuid()}/restore", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------- PUT с isDeleted (восстановление через синк) ----------

    [Fact]
    public async Task PutNote_WithIsDeletedFalse_RestoresSoftDeletedNote()
    {
        var id = await CreateNoteAsync("Воскреснет");

        var del = await Client.DeleteAsync($"/notes/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var put = await PutJsonAsync($"/notes/{id}", new
        {
            title = "Воскреснет",
            content = "Текст",
            folderId = (Guid?)null,
            isDeleted = false
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var body = await ReadAsync(put);
        Assert.False(body.GetProperty("isDeleted").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/notes/{id}")).StatusCode);
    }

    [Fact]
    public async Task PutNote_WithoutIsDeleted_KeepsSoftDeleteFlag()
    {
        var id = await CreateNoteAsync("Осталась удалённой");

        var del = await Client.DeleteAsync($"/notes/{id}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        // Обычное обновление контента без поля isDeleted.
        var put = await PutJsonAsync($"/notes/{id}", new
        {
            title = "Обновили",
            content = "Текст",
            folderId = (Guid?)null
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Флаг удаления не изменился — заметка по-прежнему скрыта.
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"/notes/{id}")).StatusCode);
    }

    [Fact]
    public async Task PutFolder_WithIsDeletedFalse_RestoresSoftDeletedFolder()
    {
        var folderId = await CreateFolderAsync("Папка-феникс");

        var del = await Client.DeleteAsync($"/folders/{folderId}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var put = await PutJsonAsync($"/folders/{folderId}", new
        {
            name = "Папка-феникс",
            isDeleted = false
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/folders/{folderId}")).StatusCode);
    }
}
