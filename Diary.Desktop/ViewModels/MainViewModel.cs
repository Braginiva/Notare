using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Desktop.Models;
using Diary.Desktop.Services;

namespace Diary.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WebSocketClient _ws;

    // Таймеры автосохранения (debounce): один на заметки, один на папки.
    private DispatcherTimer? _noteTimer;
    private DispatcherTimer? _folderTimer;

    // Таймер автосинка: раз в 30 секунд тянет данные с сервера.
    private DispatcherTimer? _autoSyncTimer;

    // Флаг: идёт ли прямо сейчас сохранение. Пока true — автосинк пропускаем.
    private bool _isSaving;

    // Предыдущие выделенные объекты — чтобы отписываться от их PropertyChanged.
    private Note? _previousNote;
    private Folder? _previousFolder;

    public ObservableCollection<Folder> Folders { get; } = new();
    public ObservableCollection<Note> Notes { get; } = new();

    [ObservableProperty]
    public partial Screen CurrentScreen { get; set; } = Screen.Folders;

    [ObservableProperty]
    public partial Folder? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial Note? SelectedNote { get; set; }

    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = "Главная";

    // Сообщение баннера undo (пустая строка — баннер скрыт).
    [ObservableProperty]
    public partial string UndoMessage { get; set; } = "";

    public bool IsUndoVisible => !string.IsNullOrEmpty(UndoMessage);

    partial void OnUndoMessageChanged(string value) => OnPropertyChanged(nameof(IsUndoVisible));

    public bool IsOnMainScreen => CurrentScreen == Screen.Folders;
    public bool IsOnFoldersScreen => CurrentScreen == Screen.Folders;
    public bool IsOnNotesScreen => CurrentScreen == Screen.Notes;
    public bool IsOnEditorScreen => CurrentScreen == Screen.Editor;

    partial void OnCurrentScreenChanged(Screen value)
    {
        OnPropertyChanged(nameof(IsOnMainScreen));
        OnPropertyChanged(nameof(IsOnFoldersScreen));
        OnPropertyChanged(nameof(IsOnNotesScreen));
        OnPropertyChanged(nameof(IsOnEditorScreen));
    }

    public MainViewModel()
    {
        _api = new ApiClient("http://localhost:5092");

        // Таймер сохранения заметок (debounce 1 сек).
        _noteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _noteTimer.Tick += async (_, _) =>
        {
            _noteTimer?.Stop();
            await SaveSelectedNoteAsync();
        };

        // Таймер сохранения папок (debounce 1 сек).
        _folderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _folderTimer.Tick += async (_, _) =>
        {
            _folderTimer?.Stop();
            await SaveSelectedFolderAsync();
        };

        // Таймер автосинка: раз в 30 сек.
        // WebSocket для мгновенного синка.
        _ws = new WebSocketClient(
            "ws://localhost:5092/ws",
            onChange: () =>
            {
                // Callback вызывается из WebSocket-потока. Чтобы UI обновился,
                // переключаемся на UI-поток.
                Dispatcher.UIThread.Post(async () =>
                {
                    Debug.WriteLine("### WebSocket: получен сигнал, обновляю данные");
                    await LoadInitialDataAsync();
                });
            });
        _ws.Start();
        Debug.WriteLine("### WebSocket-клиент запущен");

        // Автосинк раз в 5 минут как страховка на случай обрыва WebSocket.
        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _autoSyncTimer.Tick += async (_, _) =>
        {
            await AutoSyncAsync();
        };
        _autoSyncTimer.Start();
        Debug.WriteLine("### Автосинк-страховка запущен (5 мин)");

        _ = LoadInitialDataAsync();
    }

    // === НАВИГАЦИЯ ===

    [RelayCommand]
    private async Task OpenFolderAsync(Folder folder)
    {
        if (folder is null) return;

        SelectedFolder = folder;
        CurrentTitle = folder.Name;
        CurrentScreen = Screen.Notes;
        await LoadNotesForFolderAsync(folder.Id);
    }

    [RelayCommand]
    private void GoBack()
    {
        switch (CurrentScreen)
        {
            case Screen.Notes:
                CurrentScreen = Screen.Folders;
                SelectedFolder = null;
                CurrentTitle = "Главная";
                Notes.Clear();
                break;

            case Screen.Editor:
                CurrentScreen = Screen.Notes;
                SelectedNote = null;
                CurrentTitle = SelectedFolder?.Name ?? "Заметки";
                break;
        }
    }

    [RelayCommand]
    private void OpenNote(Note note)
    {
        if (note is null) return;

        SelectedNote = note;
        CurrentTitle = note.Title;
        CurrentScreen = Screen.Editor;
    }

    // === ЗАГРУЗКА ===

    public async Task LoadInitialDataAsync()
    {
        try
        {
            // Запоминаем ID до перезагрузки — чтобы вернуть выделение после.
            var keepNoteId = SelectedNote?.Id;
            var keepFolderId = SelectedFolder?.Id;

            // Папки загружаем всегда — они нужны на главном экране.
            Debug.WriteLine("→ Загружаю папки с сервера");
            var folders = await _api.GetFoldersAsync();
            Folders.Clear();
            foreach (var folder in folders)
                Folders.Add(folder);
            Debug.WriteLine($"→ Загружено папок: {Folders.Count}");

            // Заметки загружаем ПО КОНТЕКСТУ экрана.
            if (CurrentScreen == Screen.Folders)
            {
                // На главной — все заметки (для превью, если понадобится).
                Debug.WriteLine("→ Загружаю все заметки (экран папок)");
                var notes = await _api.GetNotesAsync();
                Notes.Clear();
                foreach (var note in notes)
                    Notes.Add(note);
                Debug.WriteLine($"→ Загружено заметок: {Notes.Count}");
            }
            else if (CurrentScreen == Screen.Notes && SelectedFolder is not null)
            {
                // На экране заметок в папке — только заметки этой папки.
                Debug.WriteLine($"→ Загружаю заметки папки {SelectedFolder.Name}");
                var notes = await _api.GetNotesAsync();
                var filtered = notes.Where(n => n.FolderId == SelectedFolder.Id).ToList();
                Notes.Clear();
                foreach (var note in filtered)
                    Notes.Add(note);
                Debug.WriteLine($"→ Заметок в папке: {Notes.Count}");
            }
            else if (CurrentScreen == Screen.Editor)
            {
                // На экране редактора список заметок не трогаем — он уже отфильтрован.
                Debug.WriteLine("→ Экран редактора, список заметок не трогаю");
            }

            // Восстанавливаем выделение по Id.
            if (keepFolderId.HasValue)
                SelectedFolder = Folders.FirstOrDefault(f => f.Id == keepFolderId.Value);

            if (keepNoteId.HasValue)
                SelectedNote = Notes.FirstOrDefault(n => n.Id == keepNoteId.Value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА загрузки: {ex.Message}");
        }
    }

    private async Task LoadNotesForFolderAsync(Guid folderId)
    {
        try
        {
            var notes = await _api.GetNotesAsync();
            var filtered = notes.Where(n => n.FolderId == folderId).ToList();

            Notes.Clear();
            foreach (var note in filtered)
                Notes.Add(note);

            Debug.WriteLine($"→ Заметок в папке: {Notes.Count}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА загрузки заметок папки: {ex.Message}");
        }
    }

    // === АВТОСИНК ===

    // Раз в 30 сек обновляет данные, если пользователь не редактирует прямо сейчас.
    private async Task AutoSyncAsync()
    {
        // Если идёт сохранение (пользователь печатает) — пропускаем, чтобы не сбить.
        if (_isSaving)
        {
            Debug.WriteLine("→ Автосинк: пропущен (идёт сохранение)");
            return;
        }

        Debug.WriteLine("→ Автосинк: обновляю данные");
        await LoadInitialDataAsync();
    }

    // === СОЗДАНИЕ ===

    [RelayCommand]
    private async Task CreateNoteAsync()
    {
        var newNote = new Note
        {
            Id = Guid.NewGuid(),
            Title = "Новая заметка",
            Content = "",
            CreatedAt = DateTime.UtcNow,
            FolderId = SelectedFolder?.Id
        };

        try
        {
            var created = await _api.CreateNoteAsync(newNote);
            if (created is null) return;

            Notes.Insert(0, created);
            SelectedNote = created;
            CurrentTitle = created.Title;
            CurrentScreen = Screen.Editor;
            Debug.WriteLine("→ Заметка создана");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА создания заметки: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        var newFolder = new Folder
        {
            Id = Guid.NewGuid(),
            Name = "Новая папка",
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            var created = await _api.CreateFolderAsync(newFolder);
            if (created is null) return;

            Folders.Insert(0, created);
            Debug.WriteLine("→ Папка создана");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА создания папки: {ex.Message}");
        }
    }

    // === ОБНОВЛЕНИЕ ===

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Debug.WriteLine("→ Обновляю данные вручную");
        await LoadInitialDataAsync();
    }

    // === УДАЛЕНИЕ ===

    [RelayCommand]
    private async Task DeleteNoteAsync(Note note)
    {
        if (note is null) return;

        try
        {
            await _api.DeleteNoteAsync(note.Id);

            if (SelectedNote == note)
            {
                SelectedNote = null;
                if (CurrentScreen == Screen.Editor)
                    GoBack();
            }

            Notes.Remove(note);
            Debug.WriteLine("→ Заметка удалена");

            // Даём возможность отменить удаление (баннер на 5 секунд).
            StartUndo(new PendingUndo { Note = note });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА удаления заметки: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(Folder folder)
    {
        if (folder is null) return;

        try
        {
            await _api.DeleteFolderAsync(folder.Id);

            if (SelectedFolder == folder)
                SelectedFolder = null;

            Folders.Remove(folder);
            Debug.WriteLine("→ Папка удалена");

            // Запоминаем заметки папки: при undo восстановим и их тоже
            // (сервер папке их не возвращает — только сам клиент явно).
            var childNoteIds = Notes
                .Where(n => n.FolderId == folder.Id)
                .Select(n => n.Id)
                .ToList();

            StartUndo(new PendingUndo { Folder = folder, ChildNoteIds = childNoteIds });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА удаления папки: {ex.Message}");
        }
    }

    // === UNDO УДАЛЕНИЯ ===

    // Что можно отменить (всегда — только последнее удаление).
    private sealed class PendingUndo
    {
        public Note? Note;
        public Folder? Folder;
        public List<Guid> ChildNoteIds = new();
    }

    private PendingUndo? _pendingUndo;
    private int _undoToken;

    // Показывает баннер «Удалено — отменить» и запускает таймер автоскрытия.
    private void StartUndo(PendingUndo undo)
    {
        _pendingUndo = undo;
        UndoMessage = undo.Note is not null
            ? $"Заметка «{undo.Note.Title}» удалена"
            : $"Папка «{undo.Folder!.Name}» удалена";

        // Новый баннер делает таймер предыдущего «просроченным».
        var token = ++_undoToken;
        _ = HideUndoLaterAsync(token);
    }

    private async Task HideUndoLaterAsync(int token)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Пока ждали, показали новый баннер — этот уже не наш.
        if (token != _undoToken) return;

        _pendingUndo = null;
        UndoMessage = "";
    }

    [RelayCommand]
    private async Task UndoDeleteAsync()
    {
        if (_pendingUndo is null) return;

        var undo = _pendingUndo;
        _pendingUndo = null;
        _undoToken++; // таймер старого баннера больше не сработает
        UndoMessage = "";

        try
        {
            if (undo.Note is not null)
            {
                await _api.RestoreNoteAsync(undo.Note.Id);
                Debug.WriteLine("→ Удаление заметки отменено");
            }
            else if (undo.Folder is not null)
            {
                await _api.RestoreFolderAsync(undo.Folder.Id);

                // Заметки папки восстанавливаем по-отдельности.
                foreach (var noteId in undo.ChildNoteIds)
                    await _api.RestoreNoteAsync(noteId);

                Debug.WriteLine("→ Удаление папки отменено");
            }

            // Перечитываем данные: удалённое вернётся в списки.
            await LoadInitialDataAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА отмены удаления: {ex.Message}");
        }
    }

    // === АВТОСОХРАНЕНИЕ ЗАМЕТОК ===

    partial void OnSelectedNoteChanged(Note? value)
    {
        if (_previousNote is not null)
            _previousNote.PropertyChanged -= OnNotePropertyChanged;

        _previousNote = value;

        if (value is not null)
            value.PropertyChanged += OnNotePropertyChanged;
    }

    private void OnNotePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Note.Title) && e.PropertyName != nameof(Note.Content))
            return;
        ScheduleNoteSave();
    }

    private void ScheduleNoteSave()
    {
        _isSaving = true;   // пользователь редактирует — автосинк подождёт
        _noteTimer?.Stop();
        _noteTimer?.Start();
    }

    private async Task SaveSelectedNoteAsync()
    {
        if (SelectedNote is null)
        {
            _isSaving = false;
            return;
        }

        try
        {
            Debug.WriteLine($"→ Сохраняю заметку: {SelectedNote.Title}");
            await _api.UpdateNoteAsync(SelectedNote);
            Debug.WriteLine("→ Заметка сохранена");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА сохранения заметки: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
        }
    }

    // === АВТОСОХРАНЕНИЕ ПАПОК ===

    partial void OnSelectedFolderChanged(Folder? value)
    {
        if (_previousFolder is not null)
            _previousFolder.PropertyChanged -= OnFolderPropertyChanged;

        _previousFolder = value;

        if (value is not null)
            value.PropertyChanged += OnFolderPropertyChanged;
    }

    private void OnFolderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Folder.Name))
            return;
        ScheduleFolderSave();
    }

    private void ScheduleFolderSave()
    {
        _isSaving = true;
        _folderTimer?.Stop();
        _folderTimer?.Start();
    }

    private async Task SaveSelectedFolderAsync()
    {
        if (SelectedFolder is null)
        {
            _isSaving = false;
            return;
        }

        try
        {
            Debug.WriteLine($"→ Сохраняю папку: {SelectedFolder.Name}");
            await _api.UpdateFolderAsync(SelectedFolder);
            Debug.WriteLine("→ Папка сохранена");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА сохранения папки: {ex.Message}");
        }
        finally
        {
            _isSaving = false;
        }
    }
}