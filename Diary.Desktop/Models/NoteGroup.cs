using System.Collections.Generic;

namespace Diary.Desktop.Models;

// Группа заметок для списка по датам: заголовок («Сегодня», «Вчера»,
// «5 октября 2026») и заметки этой даты.
public class NoteGroup
{
    public string Header { get; }
    public IReadOnlyList<Note> Notes { get; }

    public NoteGroup(string header, IReadOnlyList<Note> notes)
    {
        Header = header;
        Notes = notes;
    }
}
