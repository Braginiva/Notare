using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Diary.Mobile.Models;

// Используется ТОЛЬКО для сериализации/десериализации JSON при синке.
public class Note
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public Guid? FolderId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsDeleted { get; set; }
}