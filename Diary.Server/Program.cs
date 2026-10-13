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

// GET /folders — все не удалённые папки.
app.MapGet("/folders", async (AppDbContext db) =>
    await db.Folders.Where(f => !f.IsDeleted).ToListAsync());

// GET /folders/{id} — одна папка по Id.
app.MapGet("/folders/{id}", async (Guid id, AppDbContext db) =>
{
    var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id && !f.IsDeleted);
    return folder is null ? Results.NotFound() : Results.Ok(folder);
});

// POST /folders — создать папку.
app.MapPost("/folders", async (Folder folder, AppDbContext db) =>
{
    folder.UpdatedAt = DateTime.UtcNow;
    folder.IsDeleted = false;
    db.Folders.Add(folder);
    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.Created($"/folders/{folder.Id}", folder);
});

// PUT /folders/{id} — обновить имя папки.
// Поле IsDeleted опционально — аналогично PUT /notes (восстановление через синк).
app.MapPut("/folders/{id}", async (Guid id, FolderUpdate updated, AppDbContext db) =>
{
    var existing = await db.Folders.FirstOrDefaultAsync(f => f.Id == id);
    if (existing is null) return Results.NotFound();

    existing.Name = updated.Name;
    if (updated.IsDeleted is not null)
    {
        existing.IsDeleted = updated.IsDeleted.Value;
    }
    existing.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();

    await WebSocketHub.BroadcastChangeAsync();  // ← сообщаем всем

    return Results.Ok(existing);
});

// DELETE /folders/{id} — soft delete папки + всех её заметок. ИДЕМПОТЕНТНО.
app.MapDelete("/folders/{id}", async (Guid id, AppDbContext db) =>
{
    var folder = await db.Folders.FirstOrDefaultAsync(f => f.Id == id);
    if (folder is null)
    {
        return Results.NoContent();
    }

    var now = DateTime.UtcNow;

    folder.IsDeleted = true;
    folder.UpdatedAt = now;

    var notes = await db.Notes.Where(n => n.FolderId == id && !n.IsDeleted).ToListAsync();
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
public class FolderUpdate
{
    public string Name { get; set; } = "";
    public bool? IsDeleted { get; set; }
}

// Маркер публичной точки входа для интеграционных тестов
// (WebApplicationFactory<Program> из Microsoft.AspNetCore.Mvc.Testing).
public partial class Program { }
