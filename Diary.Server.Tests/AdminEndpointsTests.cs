using System.Net;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

public class AdminEndpointsTests : ApiTestBase
{
    [Fact]
    public async Task FixUpdatedAt_RefreshesAllRecords()
    {
        var noteId = await CreateNoteAsync();
        var folderId = await CreateFolderAsync();

        var response = await Client.PostAsync("/admin/fix-updated-at", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.True(body.GetProperty("fixedNotes").GetInt32() >= 1);
        Assert.True(body.GetProperty("fixedFolders").GetInt32() >= 1);

        // Заметка и папка по-прежнему доступны после обновления.
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/notes/{noteId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"/folders/{folderId}")).StatusCode);
    }
}
