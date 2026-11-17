using Diary.Server;
using Microsoft.EntityFrameworkCore;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source = diary.db"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseWebSockets();

// Проверка, что сервер жив.
app.MapGet("/ping", () => new { status = "ok" });

// ============================================================
// ЭНДПОИНТЫ ДЛЯ ЗАМЕТОК
// ============================================================

// GET /notes — все не удалённые заметки.
app.MapGet("/notes", async (AppDbContext db) =>
    await db.Notes.Where(n => !n.IsDeleted).ToListAsync());

// GET /notes/{id} — одна заметка по Id (если не удалена).
app.MapGet("/notes/{id}", async (Guid id, AppDbContext db) =>
{
    var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id && !n.IsDeleted);
    return note is null ? Results.NotFound() : Results.Ok(note);
});

// POST /notes — создать заметку.
app.MapPost("/notes", async (Note note, AppDbContext db) =>
{
    note.UpdatedAt = DateTime.UtcNow;
    note.IsDeleted = false;
    db.Notes.Add(note);
    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.Created($"/notes/{note.Id}", note);
});

// PUT /notes/{id} — обновить заметку.
// Поле IsDeleted опционально: если передано — синк-клиент может восстановить
// удалённую заметку; если нет — флаг удаления не трогаем (как раньше).
app.MapPut("/notes/{id}", async (Guid id, NoteUpdate updated, AppDbContext db) =>
{
    var existing = await db.Notes.FirstOrDefaultAsync(n => n.Id == id);
    if (existing is null) return Results.NotFound();

    existing.Title = updated.Title;
    existing.Content = updated.Content;
    existing.FolderId = updated.FolderId;
    if (updated.IsDeleted is not null)
    {
        existing.IsDeleted = updated.IsDeleted.Value;
    }
    existing.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.Ok(existing);
});

// DELETE /notes/{id} — soft delete. ИДЕМПОТЕНТНО.
app.MapDelete("/notes/{id}", async (Guid id, AppDbContext db) =>
{
    var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id);
    if (note is null)
    {
        return Results.NoContent();
    }

    note.IsDeleted = true;
    note.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.NoContent();
});

// POST /notes/{id}/restore — восстановить мягкую заметку (undo удаления).
// ИДЕМПОТЕНТНО: 204 для существующей записи, 404 — только если записи нет.
app.MapPost("/notes/{id}/restore", async (Guid id, AppDbContext db) =>
{
    var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id);
    if (note is null) return Results.NotFound();

    if (note.IsDeleted)
    {
        note.IsDeleted = false;
        note.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем
    }

    return Results.NoContent();
});

// ============================================================
// ЭНДПОИНТЫ ДЛЯ ПАПОК
// ============================================================

// Проверка родительской папки при создании/перемещении.
// Возвращает текст ошибки или null, если всё в порядке.
// Ловит две грубые ошибки: ссылку на несуществующую/удалённую папку
// и цикл (папку внутрь самой себя или внутрь одного из её потомков).
static async Task<string?> ValidateParentAsync(AppDbContext db, Guid folderId, Guid? parentId)
{
    if (parentId is null) return null;

    if (parentId == folderId)
        return "Папку нельзя перенести внутрь самой себя";

    var parent = await db.Folders
        .FirstOrDefaultAsync(f => f.Id == parentId && !f.IsDeleted);
    if (parent is null)
        return "Родительская папка не найдена";

    // Идём вверх по цепочке родителей. Если встретили саму папку —
    // значит, мы лезем в её же потомка и получился бы цикл.
    var current = parent;
    var guard = 0;
    while (current?.ParentId is not null)
    {
        if (current.ParentId == folderId)
            return "Папку нельзя перенести внутрь её же потомка";

        var parentIdOfParent = current.ParentId;
        current = await db.Folders
            .FirstOrDefaultAsync(f => f.Id == parentIdOfParent && !f.IsDeleted);

        if (++guard > 1000) return "Обнаружен цикл в дереве папок";
    }

    return null;
}

// Id самой папки + всех её потомков (на любую глубину).
static async Task<List<Guid>> GetSubtreeIdsAsync(AppDbContext db, Guid rootId)
{
    var all = await db.Folders
        .Where(f => !f.IsDeleted)
        .Select(f => new { f.Id, f.ParentId })
        .ToListAsync();

    var children = all
        .Where(f => f.ParentId is not null)
        .GroupBy(f => f.ParentId!.Value)
        .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

    var result = new List<Guid> { rootId };
    var queue = new Queue<Guid>();
    queue.Enqueue(rootId);

    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        if (!children.TryGetValue(current, out var childIds)) continue;

        foreach (var childId in childIds)
        {
            if (result.Contains(childId)) continue;  // защита от цикла в данных
            result.Add(childId);
            queue.Enqueue(childId);
        }
    }

    return result;
}

// GET /folders — все не удалённые папки.
app.MapGet("/folders", async (AppDbContext db) =>
    await db.Folders.Where(f => !f.IsDeleted).ToListAsync());

// GET /folders/{id} — одна папка по Id.
app.MapGet("/folders/{id}", async (Guid id, AppDbContext db) =>
{
    var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id && !f.IsDeleted);
    return folder is null ? Results.NotFound() : Results.Ok(folder);
});

// POST /folders — создать папку (ParentId опционален: null = корневая).
app.MapPost("/folders", async (Folder folder, AppDbContext db) =>
{
    var error = await ValidateParentAsync(db, folder.Id, folder.ParentId);
    if (error is not null) return Results.BadRequest(new { error });

    folder.UpdatedAt = DateTime.UtcNow;
    folder.IsDeleted = false;
    db.Folders.Add(folder);
    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.Created($"/folders/{folder.Id}", folder);
});

// PUT /folders/{id} — обновить имя папки и/или родителя (перемещение).
// Поле IsDeleted опционально — аналогично PUT /notes (восстановление через синк).
app.MapPut("/folders/{id}", async (Guid id, FolderUpdate updated, AppDbContext db) =>
{
    var existing = await db.Folders.FirstOrDefaultAsync(f => f.Id == id);
    if (existing is null) return Results.NotFound();

    var error = await ValidateParentAsync(db, id, updated.ParentId);
    if (error is not null) return Results.BadRequest(new { error });

    existing.Name = updated.Name;
    existing.ParentId = updated.ParentId;
    if (updated.IsDeleted is not null)
    {
        existing.IsDeleted = updated.IsDeleted.Value;
    }
    existing.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.Ok(existing);
});

// DELETE /folders/{id} — soft delete папки, всех её потомков и их заметок.
// ИДЕМПОТЕНТНО. Заметки и вложенные папки восстанавливает клиент отдельно
// (только то, что реально было в дереве), чтобы не оживить удалённое по отдельности.
app.MapDelete("/folders/{id}", async (Guid id, AppDbContext db) =>
{
    var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id);
    if (folder is null)
    {
        return Results.NoContent();
    }

    var subtreeIds = await GetSubtreeIdsAsync(db, id);
    var now = DateTime.UtcNow;

    var folders = await db.Folders
        .Where(f => subtreeIds.Contains(f.Id) && !f.IsDeleted)
        .ToListAsync();
    foreach (var f in folders)
    {
        f.IsDeleted = true;
        f.UpdatedAt = now;
    }

    var notes = await db.Notes
        .Where(n => n.FolderId != null && subtreeIds.Contains(n.FolderId.Value) && !n.IsDeleted)
        .ToListAsync();
    foreach (var note in notes)
    {
        note.IsDeleted = true;
        note.UpdatedAt = now;
    }

    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.NoContent();
});

// POST /folders/{id}/restore — восстановить папку (undo удаления).
// ИДЕМПОТЕНТНО. Заметки папки НЕ восстанавливаются автоматически: их
// восстанавливает клиент отдельно (только те, что реально были в папке),
// чтобы не оживить заметки, удалённые по отдельности.
app.MapPost("/folders/{id}/restore", async (Guid id, AppDbContext db) =>
{
    var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id);
    if (folder is null) return Results.NotFound();

    if (folder.IsDeleted)
    {
        folder.IsDeleted = false;
        folder.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем
    }

    return Results.NoContent();
});

// GET /folders/{id}/notes — заметки в конкретной папке.
app.MapGet("/folders/{id}/notes", async (Guid id, AppDbContext db) =>
{
    var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id && !f.IsDeleted);
    if (folder is null) return Results.NotFound();

    var notes = await db.Notes
        .Where(n => n.FolderId == id && !n.IsDeleted)
        .ToListAsync();

    return Results.Ok(notes);
});

// ============================================================
// СИНХРОНИЗАЦИЯ
// ============================================================

app.MapGet("/sync", async (string? since, AppDbContext db) =>
{
    DateTime sinceDate = DateTime.MinValue;
    if (!string.IsNullOrWhiteSpace(since))
    {
        if (DateTime.TryParse(since, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            sinceDate = parsed;
        }
    }

    var notes = await db.Notes
        .Where(n => n.UpdatedAt > sinceDate)
        .ToListAsync();

    var folders = await db.Folders
        .Where(f => f.UpdatedAt > sinceDate)
        .ToListAsync();

    return Results.Ok(new
    {
        serverTime = DateTime.UtcNow,
        notes,
        folders
    });
});

// ВРЕМЕННЫЙ — обновить старые записи. После применения — удалить.
app.MapPost("/admin/fix-updated-at", async (AppDbContext db) =>
{
    var now = DateTime.UtcNow;
    var notes = await db.Notes.ToListAsync();
    var folders = await db.Folders.ToListAsync();

    foreach (var n in notes) n.UpdatedAt = now;
    foreach (var f in folders) f.UpdatedAt = now;

    await db.SaveChangesAsync();
    return Results.Ok(new { fixedNotes = notes.Count, fixedFolders = folders.Count });
});

// ============================================================
// WEBSOCKET
// ============================================================

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();
    Console.WriteLine("→ WebSocket-клиент подключён");
    WebSocketHub.Add(socket);

    var buffer = new byte[1024];

    try
    {
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Bye",
                    CancellationToken.None);
                break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"→ WebSocket error: {ex.Message}");
    }
    finally
    {
        WebSocketHub.Remove(socket);
        Console.WriteLine("→ WebSocket-клиент отключён");
    }
});

app.Run();

// ============================================================
// МОДЕЛИ
// ============================================================

public class Note
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Guid? FolderId { get; set; }

    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

public class Folder
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }

    // Родительская папка: null = корневая. Вложенность любая по глубине.
    public Guid? ParentId { get; set; }

    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}

// Тело PUT /notes/{id}: IsDeleted опционален, чтобы «отсутствие поля»
// означало «не менять», а не «снять удаление».
public class NoteUpdate
{
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public Guid? FolderId { get; set; }
    public bool? IsDeleted { get; set; }
}

// Тело PUT /folders/{id}: та же идея с опциональным IsDeleted.
// ParentId опционален по смыслу: null = папка корневая (перемещение в корень).
public class FolderUpdate
{
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public bool? IsDeleted { get; set; }
}

// Маркер публичной точки входа для интеграционных тестов
// (WebApplicationFactory<Program> из Microsoft.AspNetCore.Mvc.Testing).
public partial class Program { }
