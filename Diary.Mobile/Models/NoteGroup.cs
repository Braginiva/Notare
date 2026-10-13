using System.Collections.Generic;

namespace Diary.Mobile.Models;

// Группа заметок для CollectionView (IsGrouped): заголовок даты + заметки.
// MAUI требует, чтобы группа реализовывала IList — наследуемся от List<T>.
public class NoteGroup : List<LocalNote>
{
    public string Header { get; }

    public NoteGroup(string header, IEnumerable<LocalNote> notes) : base(notes)
    {
        Header = header;
    }
}
