using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace Diary.Mobile.Models;

// Модель заметки для ЛОКАЛЬНОЙ базы на телефоне.
// Наследует ObservableObject — чтобы UI обновлялся при изменении свойств.
[Table("Notes")]
public partial class LocalNote : ObservableObject
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [ObservableProperty]
    [property: MaxLength(500)]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; }

    // Эти поля не меняются через UI — обычные свойства.
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? FolderId { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsSynced { get; set; }

    // Подпись даты на карточке: только время для сегодняшних заметок,
    // иначе — дата и время («5 окт 14:30»).
    // CreatedAt хранится в UTC (так его пишут клиенты); Kind после чтения из
    // SQLite теряется — восстанавливаем перед переводом в локальное время.
    [Ignore]
    public string DateLabel
    {
        get
        {
            var local = DateTime.SpecifyKind(CreatedAt, DateTimeKind.Utc).ToLocalTime();
            var format = local.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm";
            return local.ToString(format, new CultureInfo("ru-RU"));
        }
    }

    public LocalNote()
    {
        Title = "";
        Content = "";
    }
}