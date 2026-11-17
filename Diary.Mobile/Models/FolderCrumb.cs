using System;

namespace Diary.Mobile.Models;

// Один сегмент крошек над списком: «Главная» или папка на пути к текущей.
public sealed class FolderCrumb
{
    public FolderCrumb(Guid? folderId, string name, bool hasSeparator)
    {
        FolderId = folderId;
        Name = name;
        HasSeparator = hasSeparator;
    }

    // null — корень («Главная»).
    public Guid? FolderId { get; }

    public string Name { get; }

    // Показывать разделитель «›» после сегмента (не показываем у последнего).
    public bool HasSeparator { get; }
}
