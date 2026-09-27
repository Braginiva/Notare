using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Diary.Desktop.Models;

public partial class Folder : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial DateTime CreatedAt { get; set; }

    public Folder() { }

    public Folder(Guid id, string name, DateTime createdAt)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }
}