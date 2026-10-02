using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MidiPlayer.Controls;
using MidiPlayer.Services;
using MediaColor = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace MidiPlayer;

internal static class DwmHelper
{
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyDarkTitleBar(IntPtr hwnd, int captionBgr, int textBgr)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 35, ref captionBgr, sizeof(int));
            DwmSetWindowAttribute(hwnd, 36, ref textBgr, sizeof(int));
        }
        catch { }
    }
}

public partial class MainWindow : Window
{
    private readonly MidiPlayerService _player = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly Random _rng = new();

    private AppConfig _config = new();
    private bool _horizontal;
    private string _loopMode = "None";   // None / List / Shuffle
    private bool _suppressLibraryEvent;
    private readonly List<LibraryItem> _flatFiles = new();
    private LibraryItem? _libraryRoot;
    private bool _sidebarOpen;

    public MainWindow()
    {
        InitializeComponent();

        // 深色标题栏
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            DwmHelper.ApplyDarkTitleBar(hwnd, 0x1B1818, 0xFAFAFA);
        };

        _config = AppConfig.Load();
        _loopMode = _config.LoopMode ?? "None";
        ApplyConfig(_config);

        LoadPorts();
        SelectPort(_config.LastPort);

        VerticalRoll.CurrentTimeProvider   = () => _player.GetCurrentTime();
        HorizontalRoll.CurrentTimeProvider = () => _player.GetCurrentTime();

        _player.PlaybackFinished += OnPlaybackFinished;

        BtnSidebar.Click  += (_, _) => SetSidebar(!_sidebarOpen);
        BtnOpen.Click     += OnOpenClick;
        BtnPlay.Click     += (_, _) => TogglePlayPause();
        BtnStop.Click     += (_, _) => StopAndReset();
        BtnPrev.Click     += (_, _) => PlayPrev();
        BtnNext.Click     += (_, _) => PlayNext();
        BtnMode.Click     += (_, _) => ToggleMode();
        BtnLoop.Click     += (_, _) => ToggleLoopMode();
        BtnPiano.Click    += (_, _) => OpenPianoKeyboard();
        BtnSettings.Click += (_, _) => OpenSettings();

        BtnBpmDown.Click   += (_, _) => ChangeBpm(-5);
        BtnBpmUp.Click     += (_, _) => ChangeBpm(+5);
        BtnTransDown.Click += (_, _) => ChangeTranspose(-1);
        BtnTransUp.Click   += (_, _) => ChangeTranspose(+1);

        LibraryTree.SelectedItemChanged += OnLibrarySelected;

        ProgressBar.SeekRequested += OnSeek;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _uiTimer.Tick += (_, _) => RefreshTimeUI();
        _uiTimer.Start();

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop     += OnDrop;

        PreviewKeyDown += OnPreviewKeyDown;

        Closed += (_, _) =>
        {
            SaveConfig();
            _player.Dispose();
        };

        // 扫描库
        RefreshLibrary();
        UpdateLoopIcon();

        // 加载文件的优先级：
        //   1. 右键"用 ALambda Midi 打开"传来的文件
        //   2. 上次关闭时的文件
        string? toLoad = null;
        if (Application.Current.Properties[App.StartupFileKey] is string cmdFile
            && File.Exists(cmdFile))
        {
            toLoad = cmdFile;
        }
        else if (!string.IsNullOrWhiteSpace(_config.LastFile)
                 && File.Exists(_config.LastFile))
        {
            toLoad = _config.LastFile;
        }

        if (toLoad != null)
        {
            // 等窗口加载完成再加载文件，避免 UI 还没准备好
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TryLoadFile(toLoad, autoPlay: false);
            }), DispatcherPriority.Loaded);
        }
    }

    // ---------------- 配置 ----------------

    private void ApplyConfig(AppConfig cfg)
    {
        var c = cfg.Colors;
        var bg = AppConfig.ParseColor(c.Background,   MediaColor.FromRgb(0x0A, 0x0E, 0x14));
        var ph = AppConfig.ParseColor(c.Playhead,     MediaColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        var gw = AppConfig.ParseColor(c.GridWeak,     MediaColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        var gs = AppConfig.ParseColor(c.GridStrong,   MediaColor.FromArgb(0x5A, 0xFF, 0xFF, 0xFF));
        var ni = AppConfig.ParseColor(c.NoteInactive, MediaColor.FromArgb(0x3C, 0xFF, 0xFF, 0xFF));
        var na = AppConfig.ParseColor(c.NoteActive,   MediaColor.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        foreach (var roll in new RollControlBase[] { VerticalRoll, HorizontalRoll })
        {
            roll.BackgroundColor   = bg;
            roll.PlayheadColor     = ph;
            roll.GridWeakColor     = gw;
            roll.GridStrongColor   = gs;
            roll.NoteInactiveColor = ni;
            roll.NoteActiveColor   = na;
            roll.ShowGrid          = cfg.ShowGrid;
        }

        _horizontal = cfg.HorizontalMode;
        VerticalRoll.Visibility   = _horizontal ? Visibility.Collapsed : Visibility.Visible;
        HorizontalRoll.Visibility = _horizontal ? Visibility.Visible   : Visibility.Collapsed;

        SetSidebar(cfg.SidebarOpen);
    }

    private void SaveConfig()
    {
        _config.HorizontalMode = _horizontal;
        _config.LastPort       = CmbPort.SelectedItem as string;
        _config.ShowGrid       = VerticalRoll.ShowGrid;
        _config.LoopMode       = _loopMode;
        _config.SidebarOpen    = _sidebarOpen;

        var c = _config.Colors;
        c.Background   = AppConfig.FormatColor(VerticalRoll.BackgroundColor);
        c.Playhead     = AppConfig.FormatColor(VerticalRoll.PlayheadColor);
        c.GridWeak     = AppConfig.FormatColor(VerticalRoll.GridWeakColor);
        c.GridStrong   = AppConfig.FormatColor(VerticalRoll.GridStrongColor);
        c.NoteInactive = AppConfig.FormatColor(VerticalRoll.NoteInactiveColor);
        c.NoteActive   = AppConfig.FormatColor(VerticalRoll.NoteActiveColor);

        _config.Save();
    }

    private void LoadPorts()
    {
        CmbPort.Items.Clear();
        var names = MidiPlayerService.GetOutputPortNames().ToList();
        if (names.Count == 0)
        {
            CmbPort.Items.Add("(未找到 MIDI 端口)");
            CmbPort.SelectedIndex = 0;
            CmbPort.IsEnabled = false;
            return;
        }
        foreach (var name in names)
            CmbPort.Items.Add(name);
        CmbPort.SelectedIndex = 0;
        CmbPort.IsEnabled = true;
    }

    private void SelectPort(string? portName)
    {
        if (string.IsNullOrEmpty(portName)) return;
        for (int i = 0; i < CmbPort.Items.Count; i++)
        {
            if (CmbPort.Items[i] as string == portName)
            {
                CmbPort.SelectedIndex = i;
                return;
            }
        }
    }

    // ---------------- 快捷键 ----------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;

        if (e.Key == Key.System && e.SystemKey == Key.Space && mods == ModifierKeys.Alt)
        {
            StopAndReset();
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
            && mods == ModifierKeys.Control)
        {
            OnOpenClick(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        if ((e.Key == Key.LeftShift || e.Key == Key.RightShift)
            && mods == ModifierKeys.Shift)
        {
            ToggleMode();
            e.Handled = true;
            return;
        }

        if (mods != ModifierKeys.None) return;

        switch (e.Key)
        {
            case Key.Left:  PlayPrev();          e.Handled = true; break;
            case Key.Right: PlayNext();          e.Handled = true; break;
            case Key.Up:    ChangeTranspose(+1); e.Handled = true; break;
            case Key.Down:  ChangeTranspose(-1); e.Handled = true; break;
            case Key.Space: TogglePlayPause();   e.Handled = true; break;
            case Key.Escape:OpenSettings();      e.Handled = true; break;
        }
    }

    // ---------------- 音乐库 ----------------

    private void RefreshLibrary()
    {
        _suppressLibraryEvent = true;
        LibraryTree.Items.Clear();
        _flatFiles.Clear();
        _libraryRoot = null;

        var folder = _config.LibraryFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            _libraryRoot = ScanFolder(folder, 0);
            if (_libraryRoot != null)
            {
                foreach (var child in _libraryRoot.Children)
                    LibraryTree.Items.Add(child);

                CollectFiles(_libraryRoot, _flatFiles);
            }
        }

        _suppressLibraryEvent = false;
    }

    private static LibraryItem? ScanFolder(string path, int depth)
    {
        if (depth > 8) return null;
        DirectoryInfo dir;
        try { dir = new DirectoryInfo(path); }
        catch { return null; }
        if (!dir.Exists) return null;

        var item = new LibraryItem
        {
            Name = dir.Name,
            FullPath = path,
            IsFolder = true
        };

        try
        {
            foreach (var sub in dir.GetDirectories())
            {
                if (sub.Name.StartsWith(".")) continue;
                var child = ScanFolder(sub.FullName, depth + 1);
                if (child != null && child.Children.Count > 0)
                    item.Children.Add(child);
            }
        }
        catch { }

        try
        {
            foreach (var f in dir.GetFiles("*.*"))
            {
                var ext = f.Extension.ToLowerInvariant();
                if (ext != ".mid" && ext != ".midi") continue;
                item.Children.Add(new LibraryItem
                {
                    Name = Path.GetFileNameWithoutExtension(f.Name),
                    FullPath = f.FullName,
                    IsFolder = false
                });
            }
        }
        catch { }

        var ordered = item.Children
            .OrderBy(c => c.IsFolder ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        item.Children.Clear();
        foreach (var c in ordered) item.Children.Add(c);

        return item;
    }

    private static void CollectFiles(LibraryItem item, List<LibraryItem> list)
    {
        foreach (var child in item.Children)
        {
            if (child.IsFolder) CollectFiles(child, list);
            else list.Add(child);
        }
    }

    private void OnLibrarySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressLibraryEvent) return;
        if (e.NewValue is not LibraryItem item) return;
        if (item.IsFolder) return;
        if (item.FullPath == _player.CurrentFilePath) return;

        TryLoadFile(item.FullPath, autoPlay: true);
    }

    // ---------------- 拖拽 ----------------

    private static bool IsMidiFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mid", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".midi", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0 && IsMidiFile(files[0]))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;
        TryLoadFile(files[0], autoPlay: false);
    }

    // ---------------- 加载 ----------------

    private void TryLoadFile(string path, bool autoPlay)
    {
        try
        {
            _player.Load(path);

            VerticalRoll.Notes       = _player.Notes;
            HorizontalRoll.Notes     = _player.Notes;
            VerticalRoll.BeatLines   = _player.BeatLines;
            HorizontalRoll.BeatLines = _player.BeatLines;

            _config.LastFile = path;
            LblFile.Text = _player.CurrentFileName;
            LblBpm.Text = $"{_player.CurrentBpm} BPM";
            LblTranspose.Text = "0";
            RefreshTimeUI();
            UpdatePlayButton();

            if (autoPlay) StartPlayback();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取失败: {ex.Message}", "错误");
        }
    }

    private void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "MIDI 文件|*.mid;*.midi|所有文件|*.*",
            InitialDirectory = _config.LibraryFolder
                ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Midi")
        };
        if (dlg.ShowDialog() != true) return;
        TryLoadFile(dlg.FileName, autoPlay: false);
    }

    // ---------------- 播放控制 ----------------

    private void TogglePlayPause()
    {
        switch (_player.Status)
        {
            case PlayerStatus.Playing:
                _player.Pause();
                UpdatePlayButton();
                return;

            case PlayerStatus.Paused:
                _player.Resume();
                UpdatePlayButton();
                return;
        }

        StartPlayback();
    }

    private void StartPlayback()
    {
        if (_player.CurrentFilePath == null)
        {
            MessageBox.Show("请先打开一个 MIDI 文件", "提示");
            return;
        }
        if (CmbPort.SelectedItem is not string portName || portName.StartsWith("("))
        {
            MessageBox.Show("请先选择有效的 MIDI 输出端口。", "提示");
            return;
        }
        try
        {
            _player.Play(portName);
            UpdatePlayButton();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"播放失败: {ex.Message}", "错误");
        }
    }

    private void StopAndReset()
    {
        _player.Stop();
        UpdatePlayButton();
        RefreshTimeUI();
        if (_player.CurrentFilePath != null)
            LblBpm.Text = $"{_player.CurrentBpm} BPM";
    }

    private void PlayPrev()
    {
        if (_flatFiles.Count == 0) return;
        int idx = _flatFiles.FindIndex(f => f.FullPath == _player.CurrentFilePath);
        if (idx < 0) idx = 0;
        idx = (idx - 1 + _flatFiles.Count) % _flatFiles.Count;
        TryLoadFile(_flatFiles[idx].FullPath, autoPlay: true);
    }

    private void PlayNext()
    {
        if (_flatFiles.Count == 0) return;
        int idx = _flatFiles.FindIndex(f => f.FullPath == _player.CurrentFilePath);

        int next;
        if (_loopMode == "Shuffle")
        {
            if (_flatFiles.Count == 1) next = 0;
            else
            {
                do { next = _rng.Next(_flatFiles.Count); }
                while (next == idx);
            }
        }
        else
        {
            next = ((idx < 0 ? -1 : idx) + 1) % _flatFiles.Count;
        }
        TryLoadFile(_flatFiles[next].FullPath, autoPlay: true);
    }

    private void OnSeek(TimeSpan target)
    {
        _player.Seek(target);
        RefreshTimeUI();
    }

    private void OnPlaybackFinished()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _player.Stop();
            UpdatePlayButton();
            RefreshTimeUI();

            if ((_loopMode == "List" || _loopMode == "Shuffle")
                && _flatFiles.Count > 0)
            {
                PlayNext();
            }
        });
    }

    private void RefreshTimeUI()
    {
        var total   = _player.GetDuration();
        var current = _player.GetCurrentTime();
        if (current > total) current = total;

        LblTime.Text = $"{Format(current)} / {Format(total)}";
        ProgressBar.SetProgress(current, total);
    }

    private static string Format(TimeSpan t) => $"{(int)t.TotalMinutes:D2}:{t.Seconds:D2}";

    private void UpdatePlayButton()
    {
        switch (_player.Status)
        {
            case PlayerStatus.Playing:
                BtnPlay.Content = "\ue034";
                break;
            default:
                BtnPlay.Content = "\ue037";
                break;
        }
    }

    // ---------------- 模式 / 循环 ----------------

    private void ToggleMode()
    {
        _horizontal = !_horizontal;
        VerticalRoll.Visibility   = _horizontal ? Visibility.Collapsed : Visibility.Visible;
        HorizontalRoll.Visibility = _horizontal ? Visibility.Visible   : Visibility.Collapsed;
        BtnMode.Content = _horizontal ? "\ue8d4" : "\ue8d5";
    }

    private void SetSidebar(bool open)
    {
        _sidebarOpen = open;
        SidebarColumn.Width = open ? new GridLength(220) : new GridLength(0);
        SidebarPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ToggleLoopMode()
    {
        _loopMode = _loopMode switch
        {
            "None"    => "List",
            "List"    => "Shuffle",
            _         => "None"
        };
        UpdateLoopIcon();
    }

    private void UpdateLoopIcon()
    {
        BtnLoop.Content = _loopMode switch
        {
            "List"    => "\ue040",
            "Shuffle" => "\ue043",
            _         => "\ue040"
        };
        BtnLoop.Foreground = _loopMode == "None"
            ? (Brush)FindResource("TextSecondaryBrush")
            : (Brush)FindResource("TextPrimaryBrush");
    }

    // ---------------- BPM / 移调 ----------------

    private void ChangeBpm(int delta)
    {
        if (_player.CurrentFilePath == null) return;
        var newBpm = Math.Clamp(_player.CurrentBpm + delta, 20, 400);
        _player.SetBpm(newBpm);
        LblBpm.Text = $"{_player.CurrentBpm} BPM";
    }

    private void ChangeTranspose(int delta)
    {
        if (_player.CurrentFilePath == null) return;
        var newVal = Math.Clamp(_player.Transpose + delta, -12, 12);
        _player.SetTranspose(newVal);
        LblTranspose.Text = newVal > 0 ? $"+{newVal}" : newVal.ToString();
        UpdatePlayButton();
    }

    // ---------------- 设置 / 钢琴 ----------------

    private void OpenPianoKeyboard()
    {
        var portName = CmbPort.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(portName) || portName.StartsWith("("))
        {
            MessageBox.Show("请先在主窗口选择有效的 MIDI 输出端口。", "提示");
            return;
        }

        var win = new PianoKeyboardWindow(portName) { Owner = this };
        win.Show();
    }

    private void OpenSettings()
    {
        var dlg = new SettingsWindow(
            bg:           VerticalRoll.BackgroundColor,
            playhead:     VerticalRoll.PlayheadColor,
            gridWeak:     VerticalRoll.GridWeakColor,
            gridStrong:   VerticalRoll.GridStrongColor,
            noteInactive: VerticalRoll.NoteInactiveColor,
            noteActive:   VerticalRoll.NoteActiveColor,
            showGrid:     VerticalRoll.ShowGrid,
            libraryFolder: _config.LibraryFolder)
        { Owner = this };

        if (dlg.ShowDialog() != true) return;

        foreach (var roll in new RollControlBase[] { VerticalRoll, HorizontalRoll })
        {
            roll.BackgroundColor   = dlg.Bg;
            roll.PlayheadColor     = dlg.Playhead;
            roll.GridWeakColor     = dlg.GridWeak;
            roll.GridStrongColor   = dlg.GridStrong;
            roll.NoteInactiveColor = dlg.NoteInactive;
            roll.NoteActiveColor   = dlg.NoteActive;
            roll.ShowGrid          = dlg.ShowGrid;
        }

        if (dlg.LibraryFolder != _config.LibraryFolder)
        {
            _config.LibraryFolder = dlg.LibraryFolder;
            RefreshLibrary();
        }
    }
}