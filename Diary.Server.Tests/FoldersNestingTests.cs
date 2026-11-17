using System.Net;
using System.Text.Json;
using Diary.Server.Tests.Infrastructure;

namespace Diary.Server.Tests;

/// <summary>
/// Вложенные папки: ParentId, защита от циклов, каскадное удаление.
/// </summary>
public class FoldersNestingTests : ApiTestBase
{
    private static List<Guid> ExtractIds(JsonElement array) =>
        array.EnumerateArray().Select(f => f.GetProperty("id").GetGuid()).ToList();

    // ---------- СОЗДАНИЕ ----------

    [Fact]
    public async Task PostFolder_WithParent_Returns201_AndKeepsParentId()
    {
        var parentId = await CreateFolderAsync("Родитель");
        var id = Guid.NewGuid();

        var response = await PostJsonAsync("/folders", new
        {
            id,
            name = "Дочка",
            createdAt = DateTime.UtcNow,
            parentId
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(parentId, body.GetProperty("parentId").GetGuid());

        // И в базе тоже.
        var get = await Client.GetAsync($"/folders/{id}");
        var persisted = await ReadAsync(get);
        Assert.Equal(parentId, persisted.GetProperty("parentId").GetGuid());
    }

    [Fact]
    public async Task PostFolder_WithoutParent_IsRoot()
    {
        var id = Guid.NewGuid();

        var response = await PostJsonAsync("/folders", new
        {
            id,
            name = "Корневая",
            createdAt = DateTime.UtcNow
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await ReadAsync(response);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task PostFolder_UnknownParent_Returns400()
    {
        var response = await PostJsonAsync("/folders", new
        {
            id = Guid.NewGuid(),
            name = "Сирота",
            createdAt = DateTime.UtcNow,
            parentId = Guid.NewGuid()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostFolder_DeletedParent_Returns400()
    {
        var parentId = await CreateFolderAsync("Удалённый родитель");
        await Client.DeleteAsync($"/folders/{parentId}");

        var response = await PostJsonAsync("/folders", new
        {
            id = Guid.NewGuid(),
            name = "В мёртвую папку",
            createdAt = DateTime.UtcNow,
            parentId
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---------- ПЕРЕМЕЩЕНИЕ ----------

    [Fact]
    public async Task PutFolder_MoveToAnotherParent_Returns200()
    {
        var oldParent = await CreateFolderAsync("Старый родитель");
        var newParent = await CreateFolderAsync("Новый родитель");
        var id = await CreateFolderAsync("Переезжает", oldParent);

        var response = await PutJsonAsync($"/folders/{id}", new
        {
            id,
            name = "Переезжает",
            parentId = newParent
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var get = await Client.GetAsync($"/folders/{id}");
        var persisted = await ReadAsync(get);
        Assert.Equal(newParent, persisted.GetProperty("parentId").GetGuid());
    }

    [Fact]
    public async Task PutFolder_MoveToRoot_Returns200_WithNullParent()
    {
        var parent = await CreateFolderAsync("Родитель");
        var id = await CreateFolderAsync("Подпапка", parent);

        var response = await PutJsonAsync($"/folders/{id}", new
        {
            id,
            name = "Подпапка",
            parentId = (Guid?)null
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var get = await Client.GetAsync($"/folders/{id}");
        var persisted = await ReadAsync(get);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task PutFolder_MoveIntoItself_Returns400()
    {
        var id = await CreateFolderAsync("Сама в себя");

        var response = await PutJsonAsync($"/folders/{id}", new
        {
            id,
            name = "Сама в себя",
            parentId = id
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PutFolder_MoveIntoOwnDescendant_Returns400_AndKeepsOldParent()
    {
        var root = await CreateFolderAsync("Корень");
        var child = await CreateFolderAsync("Дочка", root);
        var grandchild = await CreateFolderAsync("Внучка", child);

        // Корень лезет в собственную внучку — цикл.
        var response = await PutJsonAsync($"/folders/{root}", new
        {
            id = root,
            name = "Корень",
            parentId = grandchild
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Старая структура не тронута.
        var get = await Client.GetAsync($"/folders/{root}");
        var persisted = await ReadAsync(get);
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("parentId").ValueKind);
    }

    [Fact]
    public async Task PutFolder_DeepChain_IsAllowed()
    {
        var a = await CreateFolderAsync("A");
        var b = await CreateFolderAsync("B", a);
        var c = await CreateFolderAsync("C", b);
        var d = await CreateFolderAsync("D", c);

        // Перемещаем D под A — глубокая, но валидная цепочка.
        var response = await PutJsonAsync($"/folders/{d}", new
        {
            id = d,
            name = "D",
            parentId = a
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---------- УДАЛЕНИЕ ----------

    [Fact]
    public async Task DeleteFolder_CascadesToSubfoldersAndTheirNotes()
    {
        var root = await CreateFolderAsync("Корень");
        var child = await CreateFolderAsync("Дочка", root);
        var grandchild = await CreateFolderAsync("Внучка", child);

        var noteRoot = await CreateNoteAsync("В корне", folderId: root);
        var noteChild = await CreateNoteAsync("В дочке", folderId: child);
        var noteGrandchild = await CreateNoteAsync("Внучке", folderId: grandchild);
        var outsideFolder = await CreateFolderAsync("Чужая");
        var noteOutside = await CreateNoteAsync("Чужая", folderId: outsideFolder);

        var delete = await Client.DeleteAsync($"/folders/{root}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        // Всё дерево скрыто.
        foreach (var folderId in new[] { root, child, grandchild })
        {
            var get = await Client.GetAsync($"/folders/{folderId}");
            Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        }

        var folders = ExtractIds(await ReadAsync(await Client.GetAsync("/folders")));
        Assert.DoesNotContain(root, folders);
        Assert.DoesNotContain(child, folders);
        Assert.DoesNotContain(grandchild, folders);
        Assert.Contains(outsideFolder, folders);

        // Заметки дерева скрыты, чужая живёт.
        var notes = ExtractIds(await ReadAsync(await Client.GetAsync("/notes")));
        Assert.DoesNotContain(noteRoot, notes);
        Assert.DoesNotContain(noteChild, notes);
        Assert.DoesNotContain(noteGrandchild, notes);
        Assert.Contains(noteOutside, notes);
    }

    [Fact]
    public async Task DeleteFolder_DoesNotTouchSiblings()
    {
        var root = await CreateFolderAsync("Корень");
        var deletedChild = await CreateFolderAsync("Удаляемая", root);
        var liveChild = await CreateFolderAsync("Живая", root);
        var liveNote = await CreateNoteAsync("Живая заметка", folderId: liveChild);

        await Client.DeleteAsync($"/folders/{deletedChild}");

        var folders = ExtractIds(await ReadAsync(await Client.GetAsync("/folders")));
        Assert.Contains(root, folders);
        Assert.Contains(liveChild, folders);
        Assert.DoesNotContain(deletedChild, folders);

        var notes = ExtractIds(await ReadAsync(await Client.GetAsync("/notes")));
        Assert.Contains(liveNote, notes);
    }

    [Fact]
    public async Task RestoreFolder_ReturnsOnlyTheDeletedFolder_NotItsSubtree()
    {
        // Сервер папку восстанавливает, но дерево возвращает клиент:
        // он знает, что реально было в папке на момент удаления.
        var root = await CreateFolderAsync("Корень");
        var child = await CreateFolderAsync("Дочка", root);

        await Client.DeleteAsync($"/folders/{root}");

        var restore = await Client.PostAsync($"/folders/{root}/restore", null);
        Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);

        var rootGet = await Client.GetAsync($"/folders/{root}");
        Assert.Equal(HttpStatusCode.OK, rootGet.StatusCode);

        var childGet = await Client.GetAsync($"/folders/{child}");
        Assert.Equal(HttpStatusCode.NotFound, childGet.StatusCode);
    }

    // ---------- СПИСКИ ----------

    [Fact]
    public async Task GetFolders_ReturnsAllLiveFoldersWithParentId()
    {
        var parent = await CreateFolderAsync("Родитель");
        var child = await CreateFolderAsync("Дочка", parent);
        var deleted = await CreateFolderAsync("Мёртвая", parent);
        await Client.DeleteAsync($"/folders/{deleted}");

        var response = await Client.GetAsync("/folders");
        var body = await ReadAsync(response);

        var byId = body.EnumerateArray()
            .ToDictionary(f => f.GetProperty("id").GetGuid(), f => f);

        Assert.Equal(2, byId.Count);
        Assert.Equal(JsonValueKind.Null, byId[parent].GetProperty("parentId").ValueKind);
        Assert.Equal(parent, byId[child].GetProperty("parentId").GetGuid());
    }

    [Fact]
    public async Task GetFolderNotes_WorksForNestedFolder()
    {
        var parent = await CreateFolderAsync("Родитель");
        var child = await CreateFolderAsync("Дочка", parent);
        var note = await CreateNoteAsync("В дочке", folderId: child);

        var response = await Client.GetAsync($"/folders/{child}/notes");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(note, ExtractIds(await ReadAsync(response)).Single());
    }
}
