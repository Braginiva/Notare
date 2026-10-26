using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WeCantSpell.Hunspell;

namespace Diary.Desktop.Services;

// Проверка орфографии на основе Hunspell-словарей (рус + англ).
// Слово считается верным, если оно есть хотя бы в одном словаре —
// так можно писать смешанный текст.
public sealed class SpellCheckService
{
    private readonly List<WordList> _dictionaries = new();
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _dictionariesDir;
    private readonly string _userWordsPath;
    private bool _loaded;

    // Уже загружены ли словари (проверка без словарей ничего не подчёркивает).
    public bool IsLoaded => _loaded;

    // Пути по умолчанию: словари — рядом с exe, слова пользователя — в %AppData%.
    // Аргументы нужны тестам.
    public SpellCheckService(string? dictionariesDir = null, string? userWordsPath = null)
    {
        _dictionariesDir = dictionariesDir ?? Path.Combine(AppContext.BaseDirectory, "Dictionaries");
        _userWordsPath = userWordsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Notare", "user.dic");
    }

    // Ленивая загрузка словарей из папки Dictionaries рядом с exe
    // (при первом обращении — чтобы не тормозить старт).
    public void EnsureLoaded()
    {
        if (_loaded) return;

        foreach (var name in new[] { "ru_RU", "en_US" })
        {
            var affPath = Path.Combine(_dictionariesDir, name + ".aff");
            var dicPath = Path.Combine(_dictionariesDir, name + ".dic");
            if (!File.Exists(affPath) || !File.Exists(dicPath)) continue;

            try
            {
                // Порядок важен: первый аргумент — словарь (.dic), второй — аффиксы (.aff).
                using var dicStream = File.OpenRead(dicPath);
                using var affStream = File.OpenRead(affPath);
                _dictionaries.Add(WordList.CreateFromStreams(dicStream, affStream));
            }
            catch (Exception ex)
            {
                // Битый файл словаря не должен ронять проверку целиком.
                System.Diagnostics.Debug.WriteLine(
                    $"→ Словарь {name} не загрузился: {ex.Message}");
            }
        }

        // Пользовательские «всегда игнорировать».
        if (File.Exists(_userWordsPath))
        {
            foreach (var line in File.ReadAllLines(_userWordsPath))
            {
                if (line.Trim().Length > 0) _ignored.Add(line.Trim());
            }
        }

        _loaded = true;
    }

    // Верное ли слово (есть в словаре или в игнорируемых).
    public bool IsCorrect(string word)
    {
        if (word.Length == 0) return true;
        if (_ignored.Contains(word)) return true;

        // Словари ещё не загружены (или не нашлись) — подчёркивать нечего.
        if (_dictionaries.Count == 0) return true;

        foreach (var dict in _dictionaries)
        {
            if (dict.Check(word)) return true;
        }

        return false;
    }

    // Варианты замены (сначала из русного словаря, потом английские).
    public List<string> Suggest(string word, int max = 8)
    {
        var result = new List<string>(max);

        foreach (var dict in _dictionaries)
        {
            foreach (var suggestion in dict.Suggest(word))
            {
                if (result.Contains(suggestion, StringComparer.OrdinalIgnoreCase)) continue;

                result.Add(suggestion);
                if (result.Count >= max) return result;
            }
        }

        return result;
    }

    // Навсегда игнорировать слово — дописываем в user.dic.
    public void IgnoreForever(string word)
    {
        _ignored.Add(word);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_userWordsPath)!);
            File.AppendAllText(_userWordsPath, word + Environment.NewLine);
        }
        catch
        {
            // Не дописали в файл — не критично, игнорирование работает до перезапуска.
        }
    }

    // Игнорировать до перезапуска приложения (без записи в файл).
    public void IgnoreTemporarily(string word)
    {
        _ignored.Add(word);
    }

    // Разбор текста на слова: (начало, длина, слово).
    // Апостроф внутри слова не разрывает его: don't, can't.
    public static List<(int Start, int Length, string Word)> Tokenize(string text)
    {
        var words = new List<(int Start, int Length, string Word)>();
        var i = 0;

        while (i < text.Length)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsLetter(c))
                {
                    i++;
                    continue;
                }

                // Апостроф внутри слова (между буквами) — часть слова.
                if ((c == '\'' || c == '’') &&
                    i > start && i + 1 < text.Length && char.IsLetter(text[i + 1]))
                {
                    i++;
                    continue;
                }

                break;
            }

            words.Add((start, i - start, text.Substring(start, i - start)));
        }

        return words;
    }
}
