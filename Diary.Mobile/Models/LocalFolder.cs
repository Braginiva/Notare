using System;
using CommunityToolkit.Mvvm.ComponentModel;
using SQLite;

namespace Diary.Mobile.Models;

// Модель папки для ЛОКАЛЬНОЙ базы.
[Table("Folders")]
public partial class LocalFolder : ObservableObject
{
    [PrimaryKey]
    public Guid Id { get; set; }

    [ObservableProperty]
    [property: MaxLength(200)]
    public partial string Name { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
    public bool IsSynced { get; set; }

    public LocalFolder()
    {
        Name = "";
    }
}