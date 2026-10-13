using System;
using System.Globalization;
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

    // Подпись даты на карточке: только время для сегодняшних заметок,
    // иначе — дата и время («5 окт 14:30»).
    public string DateLabel
    {
        get
        {
            var local = CreatedAt.ToLocalTime();
            var format = local.Date == DateTime.Today ? "HH:mm" : "d MMM HH:mm";
            return local.ToString(format, new CultureInfo("ru-RU"));
        }
    }

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