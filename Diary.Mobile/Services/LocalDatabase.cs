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

        // Миграция старых баз: у папок появилась колонка ParentId.
        // CreateTableAsync новые колонки к уже существующей таблице не дописывает.
        var folderIdType = await GetDeclaredColumnTypeAsync(_connection, "Notes", "FolderId");
        await EnsureColumnAsync(_connection, "Folders", "ParentId", folderIdType);

        return _connection;
    }

    // Тип колонки из PRAGMA table_info — чтобы ALTER создал колонку
    // ровно такой же, как её создал бы CreateTableAsync.
    private sealed class ColumnInfo
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
    }

    private static async Task<string> GetDeclaredColumnTypeAsync(
        SQLiteAsyncConnection db, string table, string column)
    {
        var columns = await db.QueryAsync<ColumnInfo>($"PRAGMA table_info(\"{table}\")");
        return columns.FirstOrDefault(c => c.Name == column)?.Type ?? "GUID";
    }

    // Добавляет колонку в существующую таблицу, если её ещё нет.
    private static async Task EnsureColumnAsync(
        SQLiteAsyncConnection db, string table, string column, string type)
    {
        var columns = await db.QueryAsync<ColumnInfo>($"PRAGMA table_info(\"{table}\")");
        if (columns.Any(c => c.Name == column)) return;

        await db.ExecuteAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type}");
        Console.WriteLine($"→ Миграция базы: добавлена колонка {table}.{column}");
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

        // Всё дерево: сама папка + вложенные потомки любого уровня.
        var all = await db.Table<LocalFolder>().Where(f => !f.IsDeleted).ToListAsync();
        var subtree = new List<LocalFolder> { folder };

        for (var i = 0; i < subtree.Count; i++)
        {
            var parentId = subtree[i].Id;
            foreach (var child in all.Where(f => f.ParentId == parentId))
            {
                if (subtree.Any(s => s.Id == child.Id)) continue;  // цикл в данных
                subtree.Add(child);
            }
        }

        foreach (var f in subtree)
        {
            f.IsDeleted = true;
            f.IsSynced = false;
            f.UpdatedAt = DateTime.UtcNow;
            await db.InsertOrReplaceAsync(f);
        }

        // Помечаем заметки всего дерева как удалённые.
        var subtreeIds = subtree.Select(f => f.Id).ToHashSet();
        var notes = await db.Table<LocalNote>().Where(n => !n.IsDeleted).ToListAsync();

        foreach (var note in notes.Where(n =>
                     n.FolderId is not null && subtreeIds.Contains(n.FolderId.Value)))
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

    // Восстанавливает папку, её подпапки и явно перечисленные заметки —
    // то, что реально было в дереве на момент удаления
    // (сервер сам ничего, кроме папки, не возвращает).
    public async Task RestoreFolderAsync(
        Guid id, IEnumerable<Guid> folderIds, IEnumerable<Guid> noteIds)
    {
        var db = await GetConnectionAsync();

        foreach (var folderId in new[] { id }.Concat(folderIds).Distinct())
        {
            var folder = await GetFolderAsync(folderId);
            if (folder is null) continue;

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