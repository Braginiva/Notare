using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Diary.Desktop.Models;
using Diary.Desktop.Services;
using Diary.Desktop.Views;

namespace Diary.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly WebSocketClient _ws;

    // Полные списки (до фильтра поиска) — из них строим видимые Folders/Notes.
    private readonly List<Folder> _allFolders = new();
    private readonly List<Note> _allNotes = new();

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

    // Заметки, сгруппированные по датам (для списка на экране заметок).
    public ObservableCollection<NoteGroup> NoteGroups { get; } = new();

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

    // Сообщение ошибки «сервер не запустился» (пустая строка — баннер скрыт).
    [ObservableProperty]
    public partial string ServerError { get; set; } = "";

    public bool IsServerErrorVisible => !string.IsNullOrEmpty(ServerError);

    partial void OnServerErrorChanged(string value) => OnPropertyChanged(nameof(IsServerErrorVisible));

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
            .GroupBy(n => n.CreatedAt.ToLocalTime().Date)
            .OrderByDescending(g => g.Key))
        {
            NoteGroups.Add(new NoteGroup(
                DateHeader(group.Key),
                group.OrderByDescending(n => n.CreatedAt).ToList()));
        }
    }

    // «Сегодня» / «Вчера» / «5 октября 2026».
    private static string DateHeader(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today) return "Сегодня";
        if (date == today.AddDays(-1)) return "Вчера";
        return date.ToString("d MMMM yyyy", RuCulture);
    }

    private static readonly CultureInfo RuCulture = new("ru-RU");

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
        // WebSocket для мгновенного синка (стартует после подъёма сервера).
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

        // Автосинк раз в 5 минут как страховка на случай обрыва WebSocket.
        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _autoSyncTimer.Tick += async (_, _) =>
        {
            await AutoSyncAsync();
        };
        _autoSyncTimer.Start();
        Debug.WriteLine("### Автосинк-страховка запущен (5 мин)");

        _ = StartServicesAsync();
    }

    // === СТАРТ СЕРВИСОВ ===

    // Порядок важен: сначала поднимаем сервер (если нужно), потом WS и загрузка.
    // Вызывается из конструктора как fire-and-forget — окно показывается сразу,
    // ожидание сервера идёт в фоне.
    private async Task StartServicesAsync()
    {
        // Ярлыки: миграция старого имени + ярлык на рабочем столе при первом запуске.
        EnsureShortcutsOnStartup();

        var started = await ServerLauncher.EnsureStartedAsync();

        if (!started)
        {
            ServerError = "Сервер синхронизации не запустился. Заметки будут только на этом " +
                          "компьютере, телефон их не увидит. Нажмите «Повторить».";
        }

        // WS сам переподключается каждые 5 секунд — запускаем в любом случае.
        _ws.Start();
        Debug.WriteLine("### WebSocket-клиент запущен");

        await LoadInitialDataAsync();
    }

    // Кнопка «Повторить» на баннере ошибки сервера.
    [RelayCommand]
    private async Task RetryServerAsync()
    {
        if (await ServerLauncher.EnsureStartedAsync())
        {
            ServerError = "";
            await LoadInitialDataAsync();
        }
    }

    // === ЯРЛЫКИ (автозапуск с Windows и рабочий стол) ===

    private static string AutoStartShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Notare.lnk");

    private static string DesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "Notare.lnk");

    // Старое имя ярлыка (когда приложение называлось «Дневник») — для миграции.
    private static string OldDesktopShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "Дневник.lnk");

    // Маркер первого запуска: ярлык на рабочем столе создаём только один раз,
    // дальше пользователь сам управляет пунктом меню.
    private static string FirstRunMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Notare", "first-run.marker");

    // Состояния пунктов меню ⋮: есть ли соответствующие ярлыки.
    public bool IsAutoStart => File.Exists(AutoStartShortcutPath);
    public bool IsDesktopShortcut => File.Exists(DesktopShortcutPath);

    // Заголовки пунктов меню с визуальной галочкой.
    public string AutoStartHeader => IsAutoStart ? "✓ Запускать с Windows" : "Запускать с Windows";
    public string DesktopShortcutHeader => IsDesktopShortcut ? "✓ Ярлык на рабочем столе" : "Ярлык на рабочем столе";

    [RelayCommand]
    private void ToggleAutoStart() => ToggleShortcutFile(
        AutoStartShortcutPath, "Notare — персональный дневник");

    [RelayCommand]
    private void ToggleDesktopShortcut() => ToggleShortcutFile(
        DesktopShortcutPath, "Notare — персональный дневник");

    // Общий переключатель: есть ярлык — удаляем, нет — создаём.
    private void ToggleShortcutFile(string shortcutPath, string description)
    {
        try
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                Debug.WriteLine($"→ Ярлык удалён: {shortcutPath}");
            }
            else
            {
                CreateShortcut(shortcutPath, description);
                Debug.WriteLine($"→ Ярлык создан: {shortcutPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА ярлыка: {ex.Message}");
        }

        // Оба пункта зависят от наличия файлов — обновляем оба заголовка.
        OnPropertyChanged(nameof(IsAutoStart));
        OnPropertyChanged(nameof(AutoStartHeader));
        OnPropertyChanged(nameof(IsDesktopShortcut));
        OnPropertyChanged(nameof(DesktopShortcutHeader));
    }

    // При старте: мигрируем ярлыки под текущее имя и exe, плюс при самом первом
    // запуске кладём ярлык на рабочий стол (маркер в %AppData% — чтобы после
    // удаления через меню он не появлялся снова).
    public void EnsureShortcutsOnStartup()
    {
        try
        {
            // Старый ярлык «Дневник.lnk» переименовываем в «Notare.lnk».
            if (File.Exists(OldDesktopShortcutPath) && !File.Exists(DesktopShortcutPath))
            {
                File.Move(OldDesktopShortcutPath, DesktopShortcutPath);
                Debug.WriteLine("→ Ярлык «Дневник» переименован в «Notare»");
            }

            // Ярлыки перепривязываем на текущий exe
            // (важно после переименования Diary.Desktop.exe → Notare.exe).
            if (File.Exists(AutoStartShortcutPath))
                RefreshShortcutTarget(AutoStartShortcutPath);

            if (File.Exists(DesktopShortcutPath))
                RefreshShortcutTarget(DesktopShortcutPath);

            // Первый запуск: ярлык на рабочем столе, чтобы было откуда запускать.
            if (File.Exists(FirstRunMarkerPath)) return;

            Directory.CreateDirectory(Path.GetDirectoryName(FirstRunMarkerPath)!);
            File.WriteAllText(FirstRunMarkerPath, DateTime.UtcNow.ToString("O"));

            if (!File.Exists(DesktopShortcutPath))
            {
                CreateShortcut(DesktopShortcutPath, "Notare — персональный дневник");
                Debug.WriteLine("→ Первый запуск: создан ярлык на рабочем столе");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА ярлыков при старте: {ex.Message}");
        }
    }

    // Перепривязывает существующий ярлык на текущий exe, если тот сменился.
    private static void RefreshShortcutTarget(string shortcutPath)
    {
        if (!OperatingSystem.IsWindows()) return;

        var exePath = Environment.ProcessPath;
        if (exePath is null) return;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null) return;

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);

        if ((string?)shortcut.TargetPath == exePath) return;

        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
        shortcut.Save();
        Debug.WriteLine($"→ Ярлык перепривязан на {exePath}: {shortcutPath}");
    }

    // Ярлык через WScript.Shell (без прав администратора).
    private static void CreateShortcut(string shortcutPath, string description)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Ярлыки поддерживаются только на Windows");

        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не удалось определить путь к exe");

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = exePath;
        shortcut.WorkingDirectory = Path.GetDirectoryName(exePath);
        shortcut.Description = description;
        shortcut.Save();
    }

    // === ТЕМА И ЭКСПОРТ ===

    // Галочки в подменю темы.
    public string ThemeLightHeader => DesktopTheme.Current == DesktopTheme.Light ? "✓ Светлая" : "Светлая";
    public string ThemeDarkHeader => DesktopTheme.Current == DesktopTheme.Dark ? "✓ Тёмная" : "Тёмная";
    public string ThemeSystemHeader => DesktopTheme.Current == DesktopTheme.System ? "✓ Системная" : "Системная";

    [RelayCommand]
    private void SetTheme(string theme)
    {
        DesktopTheme.Apply(theme);

        OnPropertyChanged(nameof(ThemeLightHeader));
        OnPropertyChanged(nameof(ThemeDarkHeader));
        OnPropertyChanged(nameof(ThemeSystemHeader));
    }

    // Экспорт всех папок и заметок в JSON-файл (бэкап).
    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            var folders = await _api.GetFoldersAsync();
            var notes = await _api.GetNotesAsync();

            var payload = new
            {
                app = "Notare",
                exportedAt = DateTime.Now,
                folders,
                notes,
            };
            var json = JsonSerializer.Serialize(
                payload, new JsonSerializerOptions { WriteIndented = true });

            if (Application.Current?.ApplicationLifetime
                    is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } window)
                return;

            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт данных",
                SuggestedFileName = $"notare-backup-{DateTime.Now:yyyy-MM-dd}",
                DefaultExtension = "json",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                },
            });

            if (file is null) return;

            await using (var stream = await file.OpenWriteAsync())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json);
            }

            var dialog = new InfoDialog(
                "Экспорт завершён",
                $"Сохранено папок: {folders.Count}, заметок: {notes.Count}.");
            await dialog.ShowDialog<bool?>(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"→ ОШИБКА экспорта: {ex.Message}");
        }
    }

    // === НАВИГАЦИЯ ===

    [RelayCommand]
    private async Task OpenFolderAsync(Folder folder)
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
            _allFolders.Clear();
            _allFolders.AddRange(folders);
            Debug.WriteLine($"→ Загружено папок: {_allFolders.Count}");

            // Заметки загружаем ПО КОНТЕКСТУ экрана.
            if (CurrentScreen == Screen.Folders)
            {
                // На главной — все заметки (для превью, если понадобится).
                Debug.WriteLine("→ Загружаю все заметки (экран папок)");
                var notes = await _api.GetNotesAsync();
                _allNotes.Clear();
                _allNotes.AddRange(notes);
                Debug.WriteLine($"→ Загружено заметок: {_allNotes.Count}");
            }
            else if (CurrentScreen == Screen.Notes && SelectedFolder is not null)
            {
                // На экране заметок в папке — только заметки этой папки.
                Debug.WriteLine($"→ Загружаю заметки папки {SelectedFolder.Name}");
                var notes = await _api.GetNotesAsync();
                _allNotes.Clear();
                _allNotes.AddRange(notes.Where(n => n.FolderId == SelectedFolder.Id));
                Debug.WriteLine($"→ Заметок в папке: {_allNotes.Count}");
            }
            else if (CurrentScreen == Screen.Editor)
            {
                // На экране редактора список заметок не трогаем — он уже отфильтрован.
                Debug.WriteLine("→ Экран редактора, список заметок не трогаю");
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
            Debug.WriteLine($"→ ОШИБКА загрузки: {ex.Message}");
        }
    }

    private async Task LoadNotesForFolderAsync(Guid folderId)
    {
        try
        {
            var notes = await _api.GetNotesAsync();

            _allNotes.Clear();
            _allNotes.AddRange(notes.Where(n => n.FolderId == folderId));

            ApplyFilter();

            Debug.WriteLine($"→ Заметок в папке: {_allNotes.Count}");
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

            _allNotes.Insert(0, created);
            ApplyFilter();
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

            _allFolders.Insert(0, created);
            ApplyFilter();
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

            _allNotes.Remove(note);
            ApplyFilter();
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

            _allFolders.Remove(folder);
            ApplyFilter();
            Debug.WriteLine("→ Папка удалена");

            // Запоминаем заметки папки: при undo восстановим и их тоже
            // (сервер папке их не возвращает — только сам клиент явно).
            var childNoteIds = _allNotes
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