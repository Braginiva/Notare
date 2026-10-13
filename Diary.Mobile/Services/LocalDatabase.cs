using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Diary.Mobile.Models;
using SQLite;

namespace Diary.Mobile.Services;

// Обёртка над SQLite. Все операции с локальной базой — здесь.
public class LocalDatabase
{
    private SQLiteAsyncConnection? _connection;

    // Путь к файлу базы: в личной папке приложения (доступна только этому приложению).
    private static string DbPath =>
        Path.Combine(FileSystem.AppDataDirectory, "diary.db3");

    // Ленивая инициализация: создаём соединение и таблицы при первом обращении.
    private async Task<SQLiteAsyncConnection> GetConnectionAsync()
    {
        if (_connection is not null) return _connection;

        _connection = new SQLiteAsyncConnection(
            DbPath,
            SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);

        // Создаёт таблицы, если их нет.
        await _connection.CreateTableAsync<LocalNote>();
        await _connection.CreateTableAsync<LocalFolder>();

        return _connection;
    }

    // === ИНИЦИАЛИЗАЦИЯ ===

    public async Task InitializeAsync()
    {
        await GetConnectionAsync();
    }

    // === ЗАМЕТКИ ===

    public async Task<List<LocalNote>> GetNotesAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalNote>()
            .Where(n => !n.IsDeleted)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    public async Task<List<LocalNote>> GetNotesByFolderAsync(Guid folderId)
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalNote>()
            .Where(n => n.FolderId == folderId && !n.IsDeleted)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    public async Task<LocalNote?> GetNoteAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalNote>()
            .Where(n => n.Id == id)
            .FirstOrDefaultAsync();
    }

    public async Task SaveNoteAsync(LocalNote note)
    {
        var db = await GetConnectionAsync();
        note.UpdatedAt = DateTime.UtcNow;
        note.IsSynced = false;  // ждёт отправки на сервер
        await db.InsertOrReplaceAsync(note);
    }

    // Soft delete — не удаляем физически, помечаем.
    public async Task DeleteNoteAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        var note = await GetNoteAsync(id);
        if (note is null) return;

        note.IsDeleted = true;
        note.IsSynced = false;
        note.UpdatedAt = DateTime.UtcNow;
        await db.InsertOrReplaceAsync(note);
    }

    // === ПАПКИ ===

    public async Task<List<LocalFolder>> GetFoldersAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalFolder>()
            .Where(f => !f.IsDeleted)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();
    }

    public async Task<LocalFolder?> GetFolderAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalFolder>()
            .Where(f => f.Id == id)
            .FirstOrDefaultAsync();
    }

    public async Task SaveFolderAsync(LocalFolder folder)
    {
        var db = await GetConnectionAsync();
        folder.UpdatedAt = DateTime.UtcNow;
        folder.IsSynced = false;
        await db.InsertOrReplaceAsync(folder);
    }

    public async Task DeleteFolderAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        var folder = await GetFolderAsync(id);
        if (folder is null) return;

        folder.IsDeleted = true;
        folder.IsSynced = false;
        folder.UpdatedAt = DateTime.UtcNow;
        await db.InsertOrReplaceAsync(folder);

        // Помечаем все заметки в этой папке как удалённые.
        var notes = await GetNotesByFolderAsync(id);
        foreach (var note in notes)
        {
            note.IsDeleted = true;
            note.IsSynced = false;
            note.UpdatedAt = DateTime.UtcNow;
            await db.InsertOrReplaceAsync(note);
        }
    }

    // === ВОССТАНОВЛЕНИЕ (UNDO УДАЛЕНИЯ) ===

    // Снимает пометку удаления с заметки — изменение уйдёт на сервер при синке.
    public async Task RestoreNoteAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        var note = await GetNoteAsync(id);
        if (note is null) return;

        note.IsDeleted = false;
        note.IsSynced = false;
        note.UpdatedAt = DateTime.UtcNow;
        await db.InsertOrReplaceAsync(note);
    }

    // Восстанавливает папку и явно перечисленные заметки — те, что были в ней
    // на момент удаления (сервер сам их не возвращает).
    public async Task RestoreFolderAsync(Guid id, IEnumerable<Guid> noteIds)
    {
        var db = await GetConnectionAsync();

        var folder = await GetFolderAsync(id);
        if (folder is not null)
        {
            folder.IsDeleted = false;
            folder.IsSynced = false;
            folder.UpdatedAt = DateTime.UtcNow;
            await db.InsertOrReplaceAsync(folder);
        }

        foreach (var noteId in noteIds)
        {
            var note = await GetNoteAsync(noteId);
            if (note is null) continue;

            note.IsDeleted = false;
            note.IsSynced = false;
            note.UpdatedAt = DateTime.UtcNow;
            await db.InsertOrReplaceAsync(note);
        }
    }

    // === СИНК (пока не используется) ===

    // Всё, что не отправлено на сервер.
    public async Task<List<LocalNote>> GetUnsyncedNotesAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalNote>()
            .Where(n => !n.IsSynced)
            .ToListAsync();
    }

    public async Task<List<LocalFolder>> GetUnsyncedFoldersAsync()
    {
        var db = await GetConnectionAsync();
        return await db.Table<LocalFolder>()
            .Where(f => !f.IsSynced)
            .ToListAsync();
    }

    public async Task MarkNoteSyncedAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        var note = await GetNoteAsync(id);
        if (note is null) return;
        note.IsSynced = true;
        await db.InsertOrReplaceAsync(note);
    }

    public async Task MarkFolderSyncedAsync(Guid id)
    {
        var db = await GetConnectionAsync();
        var folder = await GetFolderAsync(id);
        if (folder is null) return;
        folder.IsSynced = true;
        await db.InsertOrReplaceAsync(folder);
    }
    // Сохраняет заметку, пришедшую с сервера (БЕЗ пометки IsSynced = false).
    public async Task SaveRemoteNoteAsync(LocalNote note)
    {
        var db = await GetConnectionAsync();
        await db.InsertOrReplaceAsync(note);
    }

    // То же для папки.
    public async Task SaveRemoteFolderAsync(LocalFolder folder)
    {
        var db = await GetConnectionAsync();
        await db.InsertOrReplaceAsync(folder);
    }
}