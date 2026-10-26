using System;
using System.IO;
using System.Linq;
using Diary.Desktop.Services;

namespace Diary.Desktop.Tests;

// Тесты проверки орфографии: словари настоящие (ru + en из Dictionaries),
// тайм-аут увеличен — первый прогон загружает словари на диске.
public class SpellCheckServiceTests : IDisposable
{
    // Общий сервис для тестов, которые ничего не меняют.
    private static readonly Lazy<SpellCheckService> Shared = new(() =>
    {
        var service = CreateService();
        service.EnsureLoaded();
        return service;
    });

    // Временный файл «всегда игнорировать» для тестов с сохранением.
    private readonly string _tempUserWords =
        Path.Combine(Path.GetTempPath(), $"notare-test-{Guid.NewGuid():N}.dic");

    public void Dispose()
    {
        if (File.Exists(_tempUserWords)) File.Delete(_tempUserWords);
        GC.SuppressFinalize(this);
    }

    private static SpellCheckService CreateService(string? userWordsPath = null) =>
        new(
            Path.Combine(AppContext.BaseDirectory, "Dictionaries"),
            userWordsPath ?? Path.Combine(
                Path.GetTempPath(), $"notare-test-{Guid.NewGuid():N}.dic"));

    // === РАЗБОР ТЕКСТА НА СЛОВА ===

    [Fact]
    public void Tokenize_SplitsWordsAndKeepsApostrophes()
    {
        var words = SpellCheckService.Tokenize("Привет, world! Это don't test.");

        Assert.Equal(
            new[] { "Привет", "world", "Это", "don't", "test" },
            words.Select(w => w.Word).ToArray());
    }

    [Fact]
    public void Tokenize_ReportsStartAndLength()
    {
        var text = "раз два";
        var words = SpellCheckService.Tokenize(text);

        Assert.Equal((0, 3, "раз"), words[0]);
        Assert.Equal((4, 3, "два"), words[1]);
    }

    [Fact]
    public void Tokenize_SkipsNumbersAndPunctuation()
    {
        var words = SpellCheckService.Tokenize("в1234винда 42 !!!");

        Assert.Equal(new[] { "в", "винда" }, words.Select(w => w.Word).ToArray());
    }

    [Fact]
    public void Tokenize_EmptyText_GivesNothing()
    {
        Assert.Empty(SpellCheckService.Tokenize(""));
    }

    // === ПРОВЕРКА СЛОВЕЙ (настоящие словари) ===

    [Fact]
    public void IsCorrect_KnownRussianAndEnglishWords()
    {
        var service = Shared.Value;

        Assert.True(service.IsCorrect("привет"));
        Assert.True(service.IsCorrect("тетрадь"));
        Assert.True(service.IsCorrect("hello"));
        Assert.True(service.IsCorrect("world"));
    }

    [Fact]
    public void IsCorrect_UnknownWordsAreFlagged()
    {
        var service = Shared.Value;

        foreach (var word in new[] { "фываыва", "ъъъъъ", "qwertyuiop" })
        {
            Assert.False(service.IsCorrect(word), $"неожиданно верное слово: {word}");
        }
    }

    [Fact]
    public void IsCorrect_EmptyWord_IsAlwaysCorrect()
    {
        Assert.True(Shared.Value.IsCorrect(""));
    }

    [Fact]
    public void IsCorrect_BeforeLoad_NothingIsFlagged()
    {
        var service = CreateService();

        // Словари ещё не загружены — проверять нечем, ошибок нет.
        Assert.False(service.IsLoaded);
        Assert.True(service.IsCorrect("фываыва"));
    }

    // === ПРЕДЛОЖЕНИЯ ЗАМЕНЫ ===

    [Fact]
    public void Suggest_FindsTypoFixes()
    {
        var suggestions = Shared.Value.Suggest("helo");

        Assert.Contains("hello", suggestions);
    }

    [Fact]
    public void Suggest_RespectsMaxLimit()
    {
        var suggestions = Shared.Value.Suggest("helo", max: 3);

        Assert.True(suggestions.Count <= 3);
        Assert.NotEmpty(suggestions);
    }

    // === ИГНОРИРОВАНИЕ СЛОВ ===

    [Fact]
    public void IgnoreTemporarily_OnlyForThisInstance()
    {
        var service = CreateService(_tempUserWords);
        service.EnsureLoaded();
        service.IgnoreTemporarily("фываыва");

        Assert.True(service.IsCorrect("фываыва"));

        // Новый сервис — слово снова с ошибкой (ничего не сохранялось).
        var fresh = CreateService();
        fresh.EnsureLoaded();
        Assert.False(fresh.IsCorrect("фываыва"));
    }

    [Fact]
    public void IgnoreForever_PersistsToUserFile()
    {
        var service = CreateService(_tempUserWords);
        service.EnsureLoaded();
        service.IgnoreForever("фываыва");

        Assert.True(service.IsCorrect("фываыва"));
        Assert.True(File.Exists(_tempUserWords));
        Assert.Contains("фываыва", File.ReadAllLines(_tempUserWords));

        // Новый сервис с тем же файлом — слово по-прежнему игнорируется.
        var fresh = CreateService(_tempUserWords);
        fresh.EnsureLoaded();
        Assert.True(fresh.IsCorrect("фываыва"));
    }
}
