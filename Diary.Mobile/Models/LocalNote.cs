using System;
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

    public LocalNote()
    {
        Title = "";
        Content = "";
    }
}