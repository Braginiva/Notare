using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Diary.Mobile.Models;

namespace Diary.Mobile.Services
{
    public class ApiClient
    {
        private readonly HttpClient _http;

        public ApiClient(string baseUrl)
        {
            // HttpClient требует, чтобы BaseAddress заканчивался на "/".
            // Иначе относительные URL склеиваются неправильно.
            if (!baseUrl.EndsWith("/"))
                baseUrl += "/";

            _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        }

        // === ЗАМЕТКИ ===

        public async Task<List<Note>> GetNotesAsync()
        {
            var notes = await _http.GetFromJsonAsync<List<Note>>("notes");
            return notes ?? new List<Note>();
        }

        public async Task<Note?> GetNoteAsync(Guid id)
        {
            var response = await _http.GetAsync($"notes/{id}");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<Note>();
        }

        public async Task<Note?> CreateNoteAsync(Note note)
        {
            var response = await _http.PostAsJsonAsync("notes", note);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"### POST notes FAILED: {(int)response.StatusCode} {response.StatusCode}, body: {body}");
                response.EnsureSuccessStatusCode();
            }
            return await response.Content.ReadFromJsonAsync<Note>();
        }

        public async Task<Note?> UpdateNoteAsync(Note note)
        {
            var response = await _http.PutAsJsonAsync($"notes/{note.Id}", note);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"### PUT notes/{note.Id} FAILED: {(int)response.StatusCode} {response.StatusCode}, body: {body}");
                response.EnsureSuccessStatusCode();
            }
            return await response.Content.ReadFromJsonAsync<Note>();
        }

        public async Task DeleteNoteAsync(Guid id)
        {
            var response = await _http.DeleteAsync($"notes/{id}");
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"### DELETE notes/{id} FAILED: {(int)response.StatusCode} {response.StatusCode}, body: {body}");
                response.EnsureSuccessStatusCode();
            }
        }

        // === ПАПКИ ===

        public async Task<List<Folder>> GetFoldersAsync()
        {
            var folders = await _http.GetFromJsonAsync<List<Folder>>("folders");
            return folders ?? new List<Folder>();
        }

        public async Task<Folder?> GetFolderAsync(Guid id)
        {
            var response = await _http.GetAsync($"folders/{id}");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<Folder>();
        }

        public async Task<Folder?> CreateFolderAsync(Folder folder)
        {
            var response = await _http.PostAsJsonAsync("folders", folder);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"### POST folders FAILED: {(int)response.StatusCode} {response.StatusCode}, body: {body}");
                response.EnsureSuccessStatusCode();
            }
            return await response.Content.ReadFromJsonAsync<Folder>();
        }

        public async Task<Folder?> UpdateFolderAsync(Folder folder)
        {
            var response = await _http.PutAsJsonAsync($"folders/{folder.Id}", folder);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"### PUT folders/{folder.Id} FAILED: {(int)response.StatusCode} {response.StatusCode}, body: {body}");
                response.EnsureSuccessStatusCode();
            }
            return await response.Content.ReadFromJsonAsync<Folder>();
        }

        public async Task DeleteFolderAsync(Guid id)
        {
            var response = await _http.DeleteAsync($"folders/{id}");
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"### DELETE folders/{id} FAILED: {(int)response.StatusCode} {response.StatusCode}, body: {body}");
                response.EnsureSuccessStatusCode();
            }
        }

        // === СИНК ===

        public async Task<SyncResponse?> SyncAsync(DateTime? since)
        {
            var url = since.HasValue
                ? $"sync?since={Uri.EscapeDataString(since.Value.ToString("O"))}"
                : "sync";

            return await _http.GetFromJsonAsync<SyncResponse>(url);
        }
    }
}