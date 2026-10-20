using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Maui.Networking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Mobile.Models;
using Diary.Mobile.Services;

namespace Diary.Mobile.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private WebSocketClient _ws = null!;

    // Для смены адреса сервера из настроек.
    private readonly ApiClient _api;
    private readonly LocalDatabase _db;
    private readonly SyncService _sync;

    // Полные списки (до фильтра поиска) — из них строим видимые Folders/Notes.
    private readonly List<LocalFolder> _allFolders = new();
    private readonly List<LocalNote> _allNotes = new();
    private IDispatcherTimer? _syncTimer;
    private IDispatcherTimer? _noteTimer;
    private IDispatcherTimer? _folderTimer;

    private LocalNote? _previousNote;
    private LocalFolder? _previousFolder;

    public ObservableCollection<LocalFolder> Folders { get; } = new();
    public ObservableCollection<LocalNote> Notes { get; } = new();

    // Заметки, сгруппированные по датам (для списка на экране заметок).
    public ObservableCollection<NoteGroup> NoteGroups { get; } = new();

    [ObservableProperty]
    public partial Screen CurrentScreen { get; set; } = Screen.Folders;

    [ObservableProperty]
    public partial LocalFolder? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial LocalNote? SelectedNote { get; set; }

    [ObservableProperty]
    public partial string CurrentTitle { get; set; } = "Главная";

    // Сообщение баннера undo (пустая строка — баннер скрыт).
    [ObservableProperty]
    public partial string UndoMessage { get; set; } = "";

    public bool IsUndoVisible => !string.IsNullOrEmpty(UndoMessage);

    partial void OnUndoMessageChanged(string value) => OnPropertyChanged(nameof(IsUndoVisible));

    // Текст поиска: фильтрует папки на главной и заметки в папке.
    [ObservableProperty]
    public partial string SearchQuery { get; set; } = "";

    partial void OnSearchQueryChanged(string value) => ApplyFilter();

    public bool IsOnMainScreen => CurrentScreen == Screen.Folders;
    public bool IsOnFoldersScreen => CurrentScreen == Screen.Folders;
    public bool IsOnNotesScreen => CurrentScreen == Screen.Notes;
    public bool IsOnEditorScreen => CurrentScreen == Screen.Editor;
    public bool IsSearchVisible => CurrentScreen != Screen.Editor;

    partial void OnCurrentScreenChanged(Screen value)
    {
        OnPropertyChanged(nameof(IsOnMainScreen));
        OnPropertyChanged(nameof(IsOnFoldersScreen));
        OnPropertyChanged(nameof(IsOnNotesScreen));
        OnPropertyChanged(nameof(IsOnEditorScreen));
        OnPropertyChanged(nameof(IsSearchVisible));
    }

    // === ПОИСК ===

    // Пересобирает видимые списки Folders/Notes из полных данных с учётом запроса.
    private void ApplyFilter()
    {
        Folders.Clear();
        foreach (var folder in _allFolders.Where(f => Matches(f.Name)))
            Folders.Add(folder);

        Notes.Clear();
        foreach (var note in _allNotes.Where(n => Matches(n.Title) || Matches(n.Content)))
            Notes.Add(note);

        RebuildGroups();
    }

    // Пустой запрос пропускает всё; иначе — регистронезависимое вхождение.
    private bool Matches(string? text) =>
        string.IsNullOrWhiteSpace(SearchQuery)
        || (text?.Contains(SearchQuery.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    // === ГРУППЫ ПО ДАТАМ ===

    // Строит группы заметок по дате создания: новые группы сверху,
    // внутри группы — заметки по убыванию даты.
    private void RebuildGroups()
    {
        NoteGroups.Clear();

        foreach (var group in Notes
            .GroupBy(n => UtcToLocal(n.CreatedAt).Date)
            .OrderByDescending(g => g.Key))
        {
            NoteGroups.Add(new NoteGroup(
                DateHeader(group.Key),
                group.OrderByDescending(n => n.CreatedAt).ToList()));
        }
    }

    // CreatedAt хранится в UTC (Kind теряется при чтении из SQLite).
    private static DateTime UtcToLocal(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();

    // «Сегодня» / «Вчера» / «5 октября 2026».
    private static string DateHeader(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today) return "Сегодня";
        if (date == today.AddDays(-1)) return "Вчера";
        return date.ToString("d MMMM yyyy", RuCulture);
    }

    private static readonly CultureInfo RuCulture = new("ru-RU");

    public MainViewModel(LocalDatabase db, SyncService sync, ApiClient api)
    {
        _db = db;
        _sync = sync;
        _api = api;

        // WebSocket для мгновенного синка (адрес — из настроек).
        StartWebSocket();

        _ = InitializeAsync();
    }

    // (Пере)подключение WebSocket к текущему адресу сервера.
    private void StartWebSocket()
    {
        _ws = new WebSocketClient(
            ServerSettings.WsUrl,
            onChange: () =>
            {
                // Callback из WebSocket-потока. Переключаемся на UI-поток.
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    Console.WriteLine("### WebSocket: получен сигнал, синкаю");
                    await TrySyncAsync();
                });
            });
        _ws.Start();
        Console.WriteLine($"### WebSocket-клиент запущен (mobile): {ServerSettings.WsUrl}");
    }

    // === ИНИЦИАЛИЗАЦИЯ ===

    private async Task InitializeAsync()
    {
        try
        {
            Console.WriteLine("### Инициализация базы...");
            await _db.InitializeAsync();
            Console.WriteLine("### База готова");

            await LoadInitialDataAsync();

            // Синк при старте (если есть Wi-Fi).
            _ = TrySyncAsync();

            StartSyncTimer();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"### ОШИБКА инициализации: {ex.Message}");
        }
    }

    // Таймер автосинка — страховка на случай, если мгновенный синк не сработал.
    private void StartSyncTimer()
    {
        _syncTimer = Application.Current!.Dispatcher.CreateTimer();
        _syncTimer.Interval = TimeSpan.FromMinutes(5);
        _syncTimer.Tick += async (_, _) =>
        {
            await TrySyncAsync();
        };
        _syncTimer.Start();
        Console.WriteLine("### Таймер синка-страховки запущен (5 мин)");
    }

    // Тихий автосинк (старое имя для внутренних вызовов).
    private Task TrySyncAsync() => SyncNowAsync();

    // Настоящий синк с ПК (push+pull) + обновление UI.
    // Возвращает false, если Wi-Fi нет или сервер недоступен —
    // это нужно ручному «Обновить данные», чтобы показать ошибку.
    public async Task<bool> SyncNowAsync()
    {
        if (!Connectivity.Current.ConnectionProfiles.Contains(ConnectionProfile.WiFi))
        {
            Console.WriteLine("→ Wi-Fi нет, синк пропущен");
            return false;
        }

        Console.WriteLine("→ Запускаю синк...");
        var ok = await _sync.SyncAsync();

        if (ok)
        {
            Console.WriteLine("→ Синк успешен, обновляю UI");
            await LoadInitialDataAsync();
        }
        else
        {
            Console.WriteLine("→ Синк не удался");
        }

        return ok;
    }

    // Смена адреса сервера из меню: сохраняем, переподключаем WS, пробуем синк.
    // false — по новому адресу сервер не отвечает.
    public async Task<bool> ApplyServerUrlAsync(string url)
    {
        ServerSettings.Save(url);
        _api.SetBaseUrl(ServerSettings.HttpUrl);

        _ws.Stop();
        StartWebSocket();

        return await SyncNowAsync();
    }

    // === ЗАГРУЗКА ДАННЫХ ===

    public async Task LoadInitialDataAsync()
    {
        try
        {
            // Запоминаем ID до перезагрузки — чтобы вернуть выделение после.
            var keepNoteId = SelectedNote?.Id;
            var keepFolderId = SelectedFolder?.Id;

            // Папки загружаем всегда — они нужны на главном экране.
            Console.WriteLine("→ Загружаю папки из локальной базы");
            var folders = await _db.GetFoldersAsync();
            _allFolders.Clear();
            _allFolders.AddRange(folders);
            Console.WriteLine($"→ Загружено папок: {_allFolders.Count}");

            // Заметки загружаем ПО КОНТЕКСТУ экрана.
            if (CurrentScreen == Screen.Folders)
            {
                // На главной — все заметки (для превью, если понадобится).
                Console.WriteLine("→ Загружаю все заметки (экран папок)");
                var notes = await _db.GetNotesAsync();
                _allNotes.Clear();
                _allNotes.AddRange(notes);
                Console.WriteLine($"→ Загружено заметок: {_allNotes.Count}");
            }
            else if (CurrentScreen == Screen.Notes && SelectedFolder is not null)
            {
                // На экране заметок в папке — только заметки этой папки.
                Console.WriteLine($"→ Загружаю заметки папки {SelectedFolder.Name}");
                var notes = await _db.GetNotesByFolderAsync(SelectedFolder.Id);
                _allNotes.Clear();
                _allNotes.AddRange(notes);
                Console.WriteLine($"→ Заметок в папке: {_allNotes.Count}");
            }
            else if (CurrentScreen == Screen.Editor)
            {
                // На экране редактора список заметок не трогаем — он уже отфильтрован.
                Console.WriteLine("→ Экран редактора, список заметок не трогаю");
            }

            // Строим видимые списки с учётом поискового запроса.
            ApplyFilter();

            // Восстанавливаем выделение по Id (независимо от фильтра).
            if (keepFolderId.HasValue)
                SelectedFolder = _allFolders.FirstOrDefault(f => f.Id == keepFolderId.Value);

            if (keepNoteId.HasValue)
                SelectedNote = _allNotes.FirstOrDefault(n => n.Id == keepNoteId.Value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА загрузки: {ex.Message}");
        }
    }

    private async Task LoadNotesForFolderAsync(Guid folderId)
    {
        try
        {
            var notes = await _db.GetNotesByFolderAsync(folderId);
            _allNotes.Clear();
            _allNotes.AddRange(notes);
            ApplyFilter();
            Console.WriteLine($"→ Заметок в папке: {_allNotes.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА загрузки заметок папки: {ex.Message}");
        }
    }

    // === НАВИГАЦИЯ ===

    [RelayCommand]
    private async Task OpenFolderAsync(LocalFolder folder)
    {
        if (folder is null) return;

        SearchQuery = ""; // при входе в папку сбрасываем поиск
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
                SearchQuery = ""; // возврат на главную — сбрасываем поиск
                _allNotes.Clear();
                ApplyFilter();
                break;

            case Screen.Editor:
                CurrentScreen = Screen.Notes;
                SelectedNote = null;
                CurrentTitle = SelectedFolder?.Name ?? "Заметки";
                break;
        }
    }

    [RelayCommand]
    private void OpenNote(LocalNote note)
    {
        if (note is null) return;

        SelectedNote = note;
        CurrentTitle = note.Title;
        CurrentScreen = Screen.Editor;
    }

    // === СОЗДАНИЕ ===

    [RelayCommand]
    private async Task CreateNoteAsync()
    {
        var newNote = new LocalNote
        {
            Id = Guid.NewGuid(),
            Title = "Новая заметка",
            Content = "",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            FolderId = SelectedFolder?.Id,
            IsDeleted = false,
            IsSynced = false
        };

        try
        {
            await _db.SaveNoteAsync(newNote);
            _allNotes.Insert(0, newNote);
            ApplyFilter();

            SelectedNote = newNote;
            CurrentTitle = newNote.Title;
            CurrentScreen = Screen.Editor;
            Console.WriteLine("→ Заметка создана локально");

            // Мгновенный синк с сервером.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА создания заметки: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CreateFolderAsync()
    {
        var newFolder = new LocalFolder
        {
            Id = Guid.NewGuid(),
            Name = "Новая папка",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsDeleted = false,
            IsSynced = false
        };

        try
        {
            await _db.SaveFolderAsync(newFolder);
            _allFolders.Insert(0, newFolder);
            ApplyFilter();
            Console.WriteLine("→ Папка создана локально");

            // Мгновенный синк с сервером.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА создания папки: {ex.Message}");
        }
    }

    // === УДАЛЕНИЕ ===

    [RelayCommand]
    private async Task DeleteNoteAsync(LocalNote note)
    {
        if (note is null) return;

        try
        {
            await _db.DeleteNoteAsync(note.Id);

            if (SelectedNote == note)
            {
                SelectedNote = null;
                if (CurrentScreen == Screen.Editor)
                    GoBack();
            }

            _allNotes.Remove(note);
            ApplyFilter();
            Console.WriteLine("→ Заметка удалена (soft)");

            // Баннер undo: 5 секунд, чтобы передумать.
            StartUndo(new PendingUndo { Note = note });

            // Мгновенный синк.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА удаления заметки: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteFolderAsync(LocalFolder folder)
    {
        if (folder is null) return;

        try
        {
            // Заметки папки запрашиваем ДО удаления — при undo вернём их все.
            var childNoteIds = (await _db.GetNotesByFolderAsync(folder.Id))
                .Select(n => n.Id)
                .ToList();

            await _db.DeleteFolderAsync(folder.Id);

            if (SelectedFolder == folder)
                SelectedFolder = null;

            _allFolders.Remove(folder);
            ApplyFilter();
            Console.WriteLine("→ Папка удалена (soft)");

            StartUndo(new PendingUndo { Folder = folder, ChildNoteIds = childNoteIds });

            // Мгновенный синк.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА удаления папки: {ex.Message}");
        }
    }

    // === UNDO УДАЛЕНИЯ ===

    // Что можно отменить (всегда — только последнее удаление).
    private sealed class PendingUndo
    {
        public LocalNote? Note;
        public LocalFolder? Folder;
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
                await _db.RestoreNoteAsync(undo.Note.Id);
                Console.WriteLine("→ Удаление заметки отменено");
            }
            else if (undo.Folder is not null)
            {
                await _db.RestoreFolderAsync(undo.Folder.Id, undo.ChildNoteIds);
                Console.WriteLine("→ Удаление папки отменено");
            }

            // Перечитываем списки — удалённое возвращается.
            await LoadInitialDataAsync();

            // И сразу на сервер: восстановление тоже нужно синкнуть.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА отмены удаления: {ex.Message}");
        }
    }

    // === ОБНОВЛЕНИЕ ===

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Console.WriteLine("→ Обновляю данные и синкаю");
        await TrySyncAsync();
    }

    // === АВТОСОХРАНЕНИЕ ЗАМЕТОК ===

    partial void OnSelectedNoteChanged(LocalNote? value)
    {
        if (_previousNote is not null)
            _previousNote.PropertyChanged -= OnNotePropertyChanged;

        _previousNote = value;

        if (value is not null)
            value.PropertyChanged += OnNotePropertyChanged;
    }

    private void OnNotePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalNote.Title) && e.PropertyName != nameof(LocalNote.Content))
            return;
        ScheduleNoteSave();
    }

    private void ScheduleNoteSave()
    {
        var t = GetNoteTimer();
        t.Stop();
        t.Start();
    }

    private IDispatcherTimer GetNoteTimer()
    {
        if (_noteTimer is null)
        {
            _noteTimer = Application.Current!.Dispatcher.CreateTimer();
            _noteTimer.Interval = TimeSpan.FromSeconds(1);
            _noteTimer.Tick += async (_, _) =>
            {
                _noteTimer?.Stop();
                await SaveSelectedNoteAsync();
            };
        }
        return _noteTimer;
    }

    private async Task SaveSelectedNoteAsync()
    {
        if (SelectedNote is null) return;

        try
        {
            Console.WriteLine($"→ Сохраняю заметку: {SelectedNote.Title}");
            await _db.SaveNoteAsync(SelectedNote);
            Console.WriteLine("→ Заметка сохранена локально");

            // Мгновенный синк.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА сохранения заметки: {ex.Message}");
        }
    }

    // === АВТОСОХРАНЕНИЕ ПАПОК ===

    partial void OnSelectedFolderChanged(LocalFolder? value)
    {
        if (_previousFolder is not null)
            _previousFolder.PropertyChanged -= OnFolderPropertyChanged;

        _previousFolder = value;

        if (value is not null)
            value.PropertyChanged += OnFolderPropertyChanged;
    }

    private void OnFolderPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LocalFolder.Name))
            return;
        ScheduleFolderSave();
    }

    private void ScheduleFolderSave()
    {
        var t = GetFolderTimer();
        t.Stop();
        t.Start();
    }

    private IDispatcherTimer GetFolderTimer()
    {
        if (_folderTimer is null)
        {
            _folderTimer = Application.Current!.Dispatcher.CreateTimer();
            _folderTimer.Interval = TimeSpan.FromSeconds(1);
            _folderTimer.Tick += async (_, _) =>
            {
                _folderTimer?.Stop();
                await SaveSelectedFolderAsync();
            };
        }
        return _folderTimer;
    }

    private async Task SaveSelectedFolderAsync()
    {
        if (SelectedFolder is null) return;

        try
        {
            Console.WriteLine($"→ Сохраняю папку: {SelectedFolder.Name}");
            await _db.SaveFolderAsync(SelectedFolder);
            Console.WriteLine("→ Папка сохранена локально");

            // Мгновенный синк.
            _ = TrySyncAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"→ ОШИБКА сохранения папки: {ex.Message}");
        }
    }
}