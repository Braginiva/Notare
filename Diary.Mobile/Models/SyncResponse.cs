using System;
using System.Collections.Generic;

namespace Diary.Mobile.Models;

public class SyncResponse
{
    public DateTime ServerTime { get; set; }
    public List<Note> Notes { get; set; } = new();
    public List<Folder> Folders { get; set; } = new();
}