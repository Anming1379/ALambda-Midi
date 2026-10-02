using System.Collections.ObjectModel;

namespace MidiPlayer.Services;

public class LibraryItem
{
    public string Name     { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool   IsFolder { get; set; }
    public ObservableCollection<LibraryItem> Children { get; } = new();
}