using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using MidiPlayer.Services;
using MediaColor = System.Windows.Media.Color;

namespace MidiPlayer.Controls;

public partial class SettingsWindow : Window
{
    public MediaColor Bg           { get; private set; }
    public MediaColor Playhead     { get; private set; }
    public MediaColor GridWeak     { get; private set; }
    public MediaColor GridStrong   { get; private set; }
    public MediaColor NoteInactive { get; private set; }
    public MediaColor NoteActive   { get; private set; }
    public bool       ShowGrid     { get; private set; }
    public string?    LibraryFolder { get; private set; }

    public SettingsWindow(
        MediaColor bg, MediaColor playhead,
        MediaColor gridWeak, MediaColor gridStrong,
        MediaColor noteInactive, MediaColor noteActive,
        bool showGrid,
        string? libraryFolder)
    {
        InitializeComponent();

        Bg = bg;
        Playhead = playhead;
        GridWeak = gridWeak;
        GridStrong = gridStrong;
        NoteInactive = noteInactive;
        NoteActive = noteActive;
        ShowGrid = showGrid;
        LibraryFolder = libraryFolder;

        LblVersion.Text = AppInfo.Version;
        ChkShowGrid.IsChecked = showGrid;
        TxtLibraryFolder.Text = libraryFolder ?? "";

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        ChkShowGrid.Click += (_, _) => ShowGrid = ChkShowGrid.IsChecked == true;
        BtnBrowseLibrary.Click += OnBrowseLibrary;

        BtnBg.Click           += (_, _) => { var c = Pick(Bg,           "背景色");         if (c.HasValue) { Bg = c.Value;           SwBg.Background           = new SolidColorBrush(c.Value); } };
        BtnPlayhead.Click     += (_, _) => { var c = Pick(Playhead,     "判定线");         if (c.HasValue) { Playhead = c.Value;     SwPlayhead.Background     = new SolidColorBrush(c.Value); } };
        BtnGridWeak.Click     += (_, _) => { var c = Pick(GridWeak,     "弱拍网格线");     if (c.HasValue) { GridWeak = c.Value;     SwGridWeak.Background     = new SolidColorBrush(c.Value); } };
        BtnGridStrong.Click   += (_, _) => { var c = Pick(GridStrong,   "小节线");         if (c.HasValue) { GridStrong = c.Value;   SwGridStrong.Background   = new SolidColorBrush(c.Value); } };
        BtnNoteInactive.Click += (_, _) => { var c = Pick(NoteInactive, "音符未激活色");   if (c.HasValue) { NoteInactive = c.Value; SwNoteInactive.Background = new SolidColorBrush(c.Value); } };
        BtnNoteActive.Click   += (_, _) => { var c = Pick(NoteActive,   "音符激活色");     if (c.HasValue) { NoteActive = c.Value;   SwNoteActive.Background   = new SolidColorBrush(c.Value); } };

        BtnClose.Click  += (_, _) => { DialogResult = false; Close(); };
        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnOk.Click     += (_, _) =>
        {
            var t = TxtLibraryFolder.Text.Trim();
            LibraryFolder = string.IsNullOrWhiteSpace(t) ? null : t;
            DialogResult = true;
            Close();
        };

        LinkAuthor.NavigateUri = new System.Uri(AppInfo.AuthorUrl);
        LinkAuthor.RequestNavigate += OnNavigate;

        SwBg.Background           = new SolidColorBrush(Bg);
        SwPlayhead.Background     = new SolidColorBrush(Playhead);
        SwGridWeak.Background     = new SolidColorBrush(GridWeak);
        SwGridStrong.Background   = new SolidColorBrush(GridStrong);
        SwNoteInactive.Background = new SolidColorBrush(NoteInactive);
        SwNoteActive.Background   = new SolidColorBrush(NoteActive);
    }

    private void OnBrowseLibrary(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 MIDI 库文件夹",
            InitialDirectory = TxtLibraryFolder.Text
        };
        if (dlg.ShowDialog(this) == true)
            TxtLibraryFolder.Text = dlg.FolderName;
    }

    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    private MediaColor? Pick(MediaColor current, string title)
    {
        var dlg = new ColorPickerWindow(current) { Owner = this };
        if (dlg.ShowDialog() == true) return dlg.SelectedColor;
        return null;
    }
}