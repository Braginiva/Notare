using System;
using System.Threading.Tasks;
using Diary.Mobile.Models;

namespace Diary.Mobile.Services;

public class SyncService
{
    private readonly LocalDatabase _db;
    private readonly ApiClient _api;

    private const string LastSyncKey = "last_sync_time";
    private bool _isSyncing;

    public SyncService(LocalDatabase db, ApiClient api)
    {
        _db = db;
        _api = api;
    }

    public async Task<bool> SyncAsync()
    {
        if (_isSyncing)
        {
            Console.WriteLine("→ Синк уже идёт, пропускаю");
            return false;
        }

        _isSyncing = true;
        try
        {
            Console.WriteLine("### СИНК НАЧАЛСЯ");
            await PushLocalChangesAsync();
            await PullRemoteChangesAsync();
            Console.WriteLine("### СИНК ЗАВЕРШЁН");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"### ОШИБКА СИНКА: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    // === ОТПРАВКА ЛОКАЛЬНЫХ ИЗМЕНЕНИЙ ===

    private async Task PushLocalChangesAsync()
    {
        // Папки
        var unsyncedFolders = await _db.GetUnsyncedFoldersAsync();
        Console.WriteLine($"→ Папок на отправку: {unsyncedFolders.Count}");

        foreach (var folder in unsyncedFolders)
        {
            try
            {
                if (folder.IsDeleted)
                {
                    await _api.DeleteFolderAsync(folder.Id);
                }
                else
                {
                    var existing = await _api.GetFolderAsync(folder.Id);
                    var payload = new Folder
                    {
                        Id = folder.Id,
                        Name = folder.Name,
                        CreatedAt = folder.CreatedAt
                    };

                    if (existing is null)
                        await _api.CreateFolderAsync(payload);
                    else
                        await _api.UpdateFolderAsync(payload);
                }

                await _db.MarkFolderSyncedAsync(folder.Id);
                Console.WriteLine($"→ Папка отправлена: {folder.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"→ Не удалось отправить папку {folder.Name}: {ex.Message}");
            }
        }

        // Заметки
        var unsyncedNotes = await _db.GetUnsyncedNotesAsync();
        Console.WriteLine($"→ Заметок на отправку: {unsyncedNotes.Count}");

        foreach (var note in unsyncedNotes)
        {
            try
            {
                if (note.IsDeleted)
                {
                    await _api.DeleteNoteAsync(note.Id);
                }
                else
                {
                    var existing = await _api.GetNoteAsync(note.Id);
                    var payload = new Note
                    {
                        Id = note.Id,
                        Title = note.Title,
                        Content = note.Content,
                        CreatedAt = note.CreatedAt,
                        FolderId = note.FolderId
                    };

                    if (existing is null)
                        await _api.CreateNoteAsync(payload);
                    else
                        await _api.UpdateNoteAsync(payload);
                }

                await _db.MarkNoteSyncedAsync(note.Id);
                Console.WriteLine($"→ Заметка отправлена: {note.Title}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"→ Не удалось отправить заметку {note.Title}: {ex.Message}");
            }
        }
    }

    // === ЗАБОР ИЗМЕНЕНИЙ С СЕРВЕРА ===

    private async Task PullRemoteChangesAsync()
    {
        DateTime? lastSync = null;
        var lastSyncStr = Preferences.Default.Get<string?>(LastSyncKey, null);
        if (!string.IsNullOrEmpty(lastSyncStr) && DateTime.TryParse(lastSyncStr, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            lastSync = parsed;
        }

        Console.WriteLine($"→ Забираю с {lastSync?.ToString("O") ?? "начала времён"}");

        var response = await _api.SyncAsync(lastSync);
        if (response is null)
        {
            Console.WriteLine("→ Сервер вернул null");
            return;
        }

        Console.WriteLine($"→ Пришло: заметок {response.Notes.Count}, папок {response.Folders.Count}");

        // Применяем папки.
        foreach (var remote in response.Folders)
        {
            var local = await _db.GetFolderAsync(remote.Id);

            if (local is null)
            {
                await _db.SaveRemoteFolderAsync(new LocalFolder
                {
                    Id = remote.Id,
                    Name = remote.Name,
                    CreatedAt = remote.CreatedAt,
                    UpdatedAt = remote.UpdatedAt,
                    IsDeleted = remote.IsDeleted,
                    IsSynced = true
                });
            }
            else if (remote.UpdatedAt > local.UpdatedAt)
            {
                local.Name = remote.Name;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
                local.IsSynced = true;
                await _db.SaveRemoteFolderAsync(local);
            }
        }

        // Применяем заметки.
        foreach (var remote in response.Notes)
        {
            var local = await _db.GetNoteAsync(remote.Id);

            if (local is null)
            {
                await _db.SaveRemoteNoteAsync(new LocalNote
                {
                    Id = remote.Id,
                    Title = remote.Title,
                    Content = remote.Content,
                    CreatedAt = remote.CreatedAt,
                    FolderId = remote.FolderId,
                    UpdatedAt = remote.UpdatedAt,
                    IsDeleted = remote.IsDeleted,
                    IsSynced = true
                });
            }
            else if (remote.UpdatedAt > local.UpdatedAt)
            {
                local.Title = remote.Title;
                local.Content = remote.Content;
                local.FolderId = remote.FolderId;
                local.UpdatedAt = remote.UpdatedAt;
                local.IsDeleted = remote.IsDeleted;
                local.IsSynced = true;
                await _db.SaveRemoteNoteAsync(local);
            }
        }

        Preferences.Default.Set(LastSyncKey, response.ServerTime.ToString("O"));
        Console.WriteLine($"→ serverTime: {response.ServerTime:O}");
    }
}
