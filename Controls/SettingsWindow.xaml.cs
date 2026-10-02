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
        BtnAssociate.Click += OnAssociateClicked;
        RefreshAssocButton();

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

    // ---------------- 文件关联 ----------------

    private void OnAssociateClicked(object sender, RoutedEventArgs e)
    {
        if (FileAssociationService.IsRegistered())
        {
            var r = MessageBox.Show(
                "要取消 .mid 文件与本程序的关联吗？",
                "确认", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;

            FileAssociationService.Unregister();
            RefreshAssocButton();
            return;
        }

        var ok = FileAssociationService.Register();
        RefreshAssocButton();

        if (ok)
        {
            var r = MessageBox.Show(
                "注册成功！\n\n" +
                "如果双击 .mid 文件仍然用其他程序打开，\n" +
                "请右键 → 打开方式 → 选择其他应用，\n" +
                "找到 ALambda Midi 并勾选\"始终使用\"。\n\n" +
                "是否现在打开 Windows 的默认应用设置页？",
                "注册完成",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (r == MessageBoxResult.Yes)
                FileAssociationService.OpenDefaultAppsSettings();
        }
        else
        {
            MessageBox.Show("注册失败。可能是权限或系统限制。", "错误",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshAssocButton()
    {
        if (FileAssociationService.IsRegistered())
        {
            BtnAssociate.Content = "取消关联";
            LblAssocHint.Text = "已关联到本程序。双击 .mid 将用 ALambda Midi 打开";
        }
        else
        {
            BtnAssociate.Content = "注册";
            LblAssocHint.Text = "注册后可在右键菜单中看到 ALambda Midi";
        }
    }

    // ---------------- 音乐库 ----------------

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

    // ---------------- 关于页链接 ----------------

    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    // ---------------- 颜色选择 ----------------

    private MediaColor? Pick(MediaColor current, string title)
    {
        var dlg = new ColorPickerWindow(current) { Owner = this };
        if (dlg.ShowDialog() == true) return dlg.SelectedColor;
        return null;
    }
}