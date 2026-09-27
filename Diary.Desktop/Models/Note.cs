using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Diary.Desktop.Models;

public partial class Note : ObservableObject
{
    [ObservableProperty]
    public partial Guid Id { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial string Content { get; set; }

    [ObservableProperty]
    public partial DateTime CreatedAt { get; set; }

    [ObservableProperty]
    public partial Guid? FolderId { get; set; }

    public Note() { }

    public Note(Guid id, string title, string content, DateTime createdAt, Guid? folderId)
    {
        Id = id;
        Title = title;
        Content = content;
        CreatedAt = createdAt;
        FolderId = folderId;
    }
}