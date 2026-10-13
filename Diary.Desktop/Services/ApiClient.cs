using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Diary.Desktop.Models;

namespace Diary.Desktop.Services
{
    // Клиент для общения с нашим API на сервере.
    // Один экземпляр на всё приложение — хранит HttpClient с базовым адресом.
    internal class ApiClient
    {
        private readonly HttpClient _http;

        // Конструктор принимает базовый адрес сервера (например, "http://localhost:5092").
        // Все запросы будут автоматически добавлять его в начало URL.
        public ApiClient(string baseUrl)
        {
            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        }

        // === ЗАМЕТКИ ===

        // GET /notes — получить список всех заметок.
        public async Task<List<Note>> GetNotesAsync()
        {
            var notes = await _http.GetFromJsonAsync<List<Note>>("/notes");
            return notes ?? new List<Note>();
        }

        // POST /notes — создать заметку. Возвращает созданную (с сервера).
        public async Task<Note?> CreateNoteAsync(Note note)
        {
            var response = await _http.PostAsJsonAsync("/notes", note);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Note>();
        }

        // PUT /notes/{id} — обновить заметку. Возвращает обновлённую.
        public async Task<Note?> UpdateNoteAsync(Note note)
        {
            var response = await _http.PutAsJsonAsync($"/notes/{note.Id}", note);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Note>();
        }

        // DELETE /notes/{id} — удалить заметку по Id.
        public async Task DeleteNoteAsync(Guid id)
        {
            var response = await _http.DeleteAsync($"/notes/{id}");
            response.EnsureSuccessStatusCode();
        }

        // === ПАПКИ ===

        // GET /folders — получить список всех папок.
        public async Task<List<Folder>> GetFoldersAsync()
        {
            var folders = await _http.GetFromJsonAsync<List<Folder>>("/folders");
            return folders ?? new List<Folder>();
        }

        // POST /folders — создать папку. Возвращает созданную.
        public async Task<Folder?> CreateFolderAsync(Folder folder)
        {
            var response = await _http.PostAsJsonAsync("/folders", folder);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Folder>();
        }
        // PUT /folders/{id} — обновить папку.
        public async Task<Folder?> UpdateFolderAsync(Folder folder)
        {
            var response = await _http.PutAsJsonAsync($"/folders/{folder.Id}", folder);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<Folder>();
        }
        // DELETE /folders/{id} — удалить папку по Id.
        public async Task DeleteFolderAsync(Guid id)
        {
            var response = await _http.DeleteAsync($"/folders/{id}");
            response.EnsureSuccessStatusCode();
        }

        // === ВОССТАНОВЛЕНИЕ (UNDO УДАЛЕНИЯ) ===

        // POST /notes/{id}/restore — вернуть удалённую заметку.
        public async Task RestoreNoteAsync(Guid id)
        {
            var response = await _http.PostAsync($"/notes/{id}/restore", null);
            response.EnsureSuccessStatusCode();
        }

        // POST /folders/{id}/restore — вернуть удалённую папку.
        public async Task RestoreFolderAsync(Guid id)
        {
            var response = await _http.PostAsync($"/folders/{id}/restore", null);
            response.EnsureSuccessStatusCode();
        }
    }
}