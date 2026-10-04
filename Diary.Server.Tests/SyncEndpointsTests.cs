using System.Net;
using System.Text.Json;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

public class SyncEndpointsTests : ApiTestBase
{
    private static List<Guid> ExtractIds(JsonElement array) =>
        array.EnumerateArray().Select(n => n.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task Sync_WithoutSince_ReturnsAllNotesAndFolders()
    {
        var folderId = await CreateFolderAsync("Синк-папка");
        var noteId = await CreateNoteAsync("Синк-заметка", folderId: folderId);

        var response = await Client.GetAsync("/sync");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);

        Assert.Contains(noteId, ExtractIds(body.GetProperty("notes")));
        Assert.Contains(folderId, ExtractIds(body.GetProperty("folders")));
        Assert.True(body.GetProperty("serverTime").GetDateTime() > DateTime.MinValue);
    }

    [Fact]
    public async Task Sync_SinceBeforeUpdate_IncludesNote()
    {
        var noteId = await CreateNoteAsync();

        var since = Uri.EscapeDataString(DateTime.UtcNow.AddHours(-1).ToString("o"));
        var response = await Client.GetAsync($"/sync?since={since}");
        var body = await ReadAsync(response);

        Assert.Contains(noteId, ExtractIds(body.GetProperty("notes")));
    }

    [Fact]
    public async Task Sync_SinceInFuture_ReturnsNothing()
    {
        await CreateNoteAsync();
        await CreateFolderAsync();

        var since = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("o"));
        var response = await Client.GetAsync($"/sync?since={since}");
        var body = await ReadAsync(response);

        Assert.Empty(ExtractIds(body.GetProperty("notes")));
        Assert.Empty(ExtractIds(body.GetProperty("folders")));
    }

    [Fact]
    public async Task Sync_IncludesSoftDeletedNotes_ForClientRecovery()
    {
        var id = await CreateNoteAsync();
        await Client.DeleteAsync($"/notes/{id}");

        var response = await Client.GetAsync("/sync");
        var body = await ReadAsync(response);

        var deleted = body.GetProperty("notes")
            .EnumerateArray()
            .First(n => n.GetProperty("id").GetGuid() == id);

        Assert.True(deleted.GetProperty("isDeleted").GetBoolean());
    }
}
