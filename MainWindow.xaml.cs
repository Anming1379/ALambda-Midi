using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    private const double SidebarWidth = 220;

    private readonly MidiPlayerService _player = new();
    private readonly DispatcherTimer _uiTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly Random _rng = new();

    private AppConfig _config = new();
    private bool _horizontal;
    private string _loopMode = "None";   // None / List / Shuffle
    private bool _suppressLibraryEvent;
    private LibraryItem? _libraryRoot;
    private bool _sidebarOpen;

    /// <summary>当前 MIDI 所在文件夹里的曲目列表（循环只在这个范围内）</summary>
    private readonly List<string> _playlist = new();
    private string? _playlistDir;


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
        BtnMode.Click     += (_, _) => { ToggleMode(); ShowToast(_horizontal ? "横版卷轴" : "竖版瀑布"); };
        BtnLoop.Click     += (_, _) => ToggleLoopMode();
        BtnPiano.Click    += (_, _) => OpenPianoKeyboard();
        BtnEditor.Click   += (_, _) => OpenEditor();
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

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HideToast();
        };

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop     += OnDrop;

        Closing += OnClosing;

        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += (_, _) => PlayIntroAnimation();

        Closed += (_, _) =>
        {
            SaveConfig();
            EditorView.Shutdown();
            _player.Dispose();
        };

        // 扫描库
        RefreshLibrary();
        UpdateLoopIcon();

        // 加载文件的优先级：
        //   1. 右键"用 ALambda Midi 打开"传来的文件（立即播放）
        //   2. 上次关闭时的文件（只加载，不自动播放）
        string? toLoad = null;
        bool autoPlay = false;

        if (Application.Current.Properties[App.StartupFileKey] is string cmdFile
            && File.Exists(cmdFile))
        {
            toLoad = cmdFile;
            autoPlay = true;
            Application.Current.Properties.Remove(App.StartupFileKey);
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
                TryLoadFile(toLoad, autoPlay: autoPlay);
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
        ApplyModeVisual(animate: false);
        SetSidebar(cfg.SidebarOpen, animate: false);
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
    //
    // 设计原则：不再用「单按 Ctrl / Shift」这类一碰就触发的键位，
    // 全部改成明确的组合键；容易误触的按键加上长按屏蔽（e.IsRepeat）。
    //
    //   空格          播放 / 暂停
    //   Esc           停止并回到开头
    //   ← / →         快退 / 快进 5 秒
    //   Home          回到开头
    //   ↑ / ↓         升调 / 降调（可长按连续调整）
    //   Ctrl+← / →    上一首 / 下一首（同一文件夹）
    //   Ctrl+O        打开文件
    //   Ctrl+H        横版 / 竖版切换
    //   Ctrl+L        循环模式切换
    //   Ctrl+B        显示 / 隐藏 MIDI 库
    //   Ctrl+E        MIDI 编辑器
    //   Ctrl+P        钢琴键盘
    //   Ctrl+,        设置
    //   [ / ]         BPM -5 / +5

    private static bool IsTextInputFocused()
    {
        return Keyboard.FocusedElement is TextBoxBase ||
               Keyboard.FocusedElement is PasswordBox ||
               Keyboard.FocusedElement is ComboBox { IsEditable: true };
    }

    private static bool IsLibraryTreeFocused()
    {
        var node = Keyboard.FocusedElement as DependencyObject;
        while (node != null)
        {
            if (node is TreeView) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 编辑器页面拥有自己的快捷键
        if (EditorView.IsActive)
        {
            if (EditorView.HandleKey(e)) e.Handled = true;
            return;
        }

        if (IsTextInputFocused()) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Alt 组合留给系统（Alt+F4 之类）
        if ((mods & ModifierKeys.Alt) != 0) return;

        bool ctrl  = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        // 侧边栏树获得焦点时，方向键让给它做导航
        if (!ctrl && IsLibraryTreeFocused()
            && key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home)
            return;

        if (ctrl)
        {
            if (shift) return;   // Ctrl+Shift+... 暂不占用

            switch (key)
            {
                case Key.O:            OnOpenClick(this, new RoutedEventArgs()); break;
                case Key.Left:         PlayPrev();       break;
                case Key.Right:        PlayNext();       break;
                case Key.H:            ToggleMode();     break;
                case Key.L:            ToggleLoopMode(); break;
                case Key.B:            SetSidebar(!_sidebarOpen); break;
                case Key.E:            OpenEditor();      break;
                case Key.N:
                    if (!EditorView.IsActive) OpenEditor();
                    if (EditorView.IsActive) EditorView.NewDocument();
                    break;
                case Key.P:            OpenPianoKeyboard(); break;
                case Key.OemComma:     OpenSettings();   break;
                case Key.D:            StopAndReset();   break;
                case Key.OemMinus:     ChangeBpm(-5);    break;
                case Key.OemPlus:      ChangeBpm(+5);    break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        switch (key)
        {
            case Key.Space:
                if (e.IsRepeat) { e.Handled = true; return; }   // 长按不连发
                TogglePlayPause();
                break;

            case Key.Escape:
                if (e.IsRepeat) { e.Handled = true; return; }
                StopAndReset();
                break;

            case Key.Left:
                if (e.IsRepeat) { e.Handled = true; return; }
                SeekRelative(-5);
                break;

            case Key.Right:
                if (e.IsRepeat) { e.Handled = true; return; }
                SeekRelative(+5);
                break;

            case Key.Home:
                if (e.IsRepeat) { e.Handled = true; return; }
                OnSeek(TimeSpan.Zero);
                break;

            case Key.Up:
                ChangeTranspose(+1);   // 允许长按连续移调
                break;

            case Key.Down:
                ChangeTranspose(-1);
                break;

            case Key.OemOpenBrackets:
                ChangeBpm(-5);
                break;

            case Key.OemCloseBrackets:
                ChangeBpm(+5);
                break;

            default:
                return;
        }
        e.Handled = true;
    }

    // ---------------- 播放列表（当前文件夹） ----------------

    private void UpdatePlaylistFor(string? filePath)
    {
        _playlist.Clear();
        _playlistDir = null;

        if (!string.IsNullOrWhiteSpace(filePath))
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    _playlistDir = dir;
                    var files = new List<string>();
                    files.AddRange(Directory.EnumerateFiles(dir, "*.mid",  SearchOption.TopDirectoryOnly));
                    files.AddRange(Directory.EnumerateFiles(dir, "*.midi", SearchOption.TopDirectoryOnly));
                    _playlist.AddRange(files
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase));
                }
            }
            catch { /* 文件夹不可读时退化成单曲播放 */ }
        }

        UpdatePlaylistBadge();
    }

    private int CurrentPlaylistIndex()
    {
        var current = _player.CurrentFilePath;
        if (current == null) return -1;
        return _playlist.FindIndex(p => string.Equals(p, current, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdatePlaylistBadge()
    {
        int idx = CurrentPlaylistIndex();
        if (idx >= 0 && _playlist.Count > 1)
        {
            LblPlaylist.Text = $"{idx + 1} / {_playlist.Count} 首";
            PlaylistBadge.Visibility = Visibility.Visible;
            PlaylistBadge.ToolTip = $"循环只在这个文件夹里：{_playlistDir}";
        }
        else
        {
            PlaylistBadge.Visibility = Visibility.Collapsed;
        }
    }

    // ---------------- 音乐库 ----------------

    private void RefreshLibrary()
    {
        _suppressLibraryEvent = true;
        LibraryTree.Items.Clear();
        _libraryRoot = null;

        var folder = _config.LibraryFolder;
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            _libraryRoot = ScanFolder(folder, 0);
            if (_libraryRoot != null)
            {
                foreach (var child in _libraryRoot.Children)
                    LibraryTree.Items.Add(child);
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

    private void OnLibrarySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (EditorView.IsActive) return;
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
        if (EditorView.IsActive)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

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
        if (EditorView.IsActive) return;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files.Length == 0) return;
        TryLoadFile(files[0], autoPlay: true);
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
            LblFile.ToolTip = path;
            LblBpm.Text = $"{_player.CurrentBpm} BPM";
            LblTranspose.Text = "0";
            RefreshTimeUI();
            UpdatePlayButton();

            UpdatePlaylistFor(path);
            AnimateFileLabel();

            string folderInfo = _playlist.Count > 1
                ? $" · 文件夹内 {_playlist.Count} 首"
                : "";
            ShowToast($"已载入 {_player.CurrentFileName}{folderInfo}");

            if (autoPlay) StartPlayback(silent: true);
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
        TryLoadFile(dlg.FileName, autoPlay: true);
    }

    // ---------------- 播放控制 ----------------

    private void TogglePlayPause()
    {
        switch (_player.Status)
        {
            case PlayerStatus.Playing:
                _player.Pause();
                UpdatePlayButton();
                ShowToast("已暂停");
                Pulse(BtnPlay);
                return;

            case PlayerStatus.Paused:
                _player.Resume();
                UpdatePlayButton();
                ShowToast("继续播放");
                Pulse(BtnPlay);
                return;
        }

        StartPlayback(silent: false);
    }

    private void StartPlayback(bool silent)
    {
        if (_player.CurrentFilePath == null)
        {
            if (!silent) MessageBox.Show("请先打开一个 MIDI 文件", "提示");
            else ShowToast("请先打开一个 MIDI 文件");
            return;
        }
        if (CmbPort.SelectedItem is not string portName || portName.StartsWith("("))
        {
            if (!silent) MessageBox.Show("请先选择有效的 MIDI 输出端口。", "提示");
            else ShowToast("未找到可用的 MIDI 输出端口");
            return;
        }
        try
        {
            _player.Play(portName);
            UpdatePlayButton();
            Pulse(BtnPlay);
            UpdatePlaylistBadge();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"播放失败: {ex.Message}", "错误");
        }
    }

    private void StopAndReset()
    {
        bool wasActive = _player.Status != PlayerStatus.Stopped;
        _player.Stop();
        UpdatePlayButton();
        RefreshTimeUI();
        if (_player.CurrentFilePath != null)
            LblBpm.Text = $"{_player.CurrentBpm} BPM";
        if (wasActive) ShowToast("已停止");
    }

    private void SeekRelative(int seconds)
    {
        if (_player.CurrentFilePath == null) return;
        var target = _player.GetCurrentTime() + TimeSpan.FromSeconds(seconds);
        OnSeek(target);
    }

    private void PlayPrev()
    {
        if (_playlist.Count == 0) return;
        int idx = CurrentPlaylistIndex();
        int target = idx < 0 ? _playlist.Count - 1 : (idx - 1 + _playlist.Count) % _playlist.Count;
        TryLoadFile(_playlist[target], autoPlay: true);
    }

    private void PlayNext()
    {
        if (_playlist.Count == 0) return;
        int idx = CurrentPlaylistIndex();

        int next;
        if (_loopMode == "Shuffle" && _playlist.Count > 1)
        {
            do { next = _rng.Next(_playlist.Count); }
            while (next == idx);
        }
        else
        {
            next = idx < 0 ? 0 : (idx + 1) % _playlist.Count;
        }

        TryLoadFile(_playlist[next], autoPlay: true);
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
            if (_player.CurrentFilePath != null)
                LblBpm.Text = $"{_player.CurrentBpm} BPM";

            if ((_loopMode == "List" || _loopMode == "Shuffle") && _playlist.Count > 0)
            {
                PlayNext();
            }
            else
            {
                ShowToast("播放结束");
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
        bool playing = _player.Status == PlayerStatus.Playing;
        BtnPlay.Content = playing ? "\ue034" : "\ue037";

        BtnPlay.Foreground = playing
            ? (Brush)FindResource("AccentSoftBrush")
            : (Brush)FindResource("TextPrimaryBrush");
    }

    // ---------------- 模式 / 循环 / 侧边栏 ----------------

    private void ToggleMode()
    {
        _horizontal = !_horizontal;
        ApplyModeVisual(animate: true);
    }

    private void ApplyModeVisual(bool animate)
    {
        var show = _horizontal ? (FrameworkElement)HorizontalRoll : VerticalRoll;
        var hide = _horizontal ? (FrameworkElement)VerticalRoll : HorizontalRoll;

        show.BeginAnimation(OpacityProperty, null);
        hide.BeginAnimation(OpacityProperty, null);
        show.Opacity = 1;
        hide.Opacity = 1;
        show.Visibility = Visibility.Visible;

        if (!animate)
        {
            hide.Visibility = Visibility.Collapsed;
        }
        else
        {
            show.Opacity = 0;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            show.BeginAnimation(OpacityProperty, fadeIn);

            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fadeOut.Completed += (_, _) =>
            {
                hide.Visibility = Visibility.Collapsed;
                hide.BeginAnimation(OpacityProperty, null);
                hide.Opacity = 1;
            };
            hide.BeginAnimation(OpacityProperty, fadeOut);
        }

        BtnMode.Content = _horizontal ? "\ue8d4" : "\ue8d5";
    }

    private void SetSidebar(bool open, bool animate = true)
    {
        _sidebarOpen = open;

        double from = double.IsNaN(SidebarPanel.Width) ? 0 : SidebarPanel.Width;
        double to   = open ? SidebarWidth : 0;

        SidebarPanel.BeginAnimation(WidthProperty, null);
        SidebarPanel.Width = from;
        SidebarContent.BeginAnimation(OpacityProperty, null);

        if (open) SidebarPanel.Visibility = Visibility.Visible;

        if (!animate)
        {
            SidebarPanel.Width = to;
            SidebarContent.Opacity = open ? 1 : 0;
            SidebarPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var widthAnim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(open ? 260 : 190))
        {
            EasingFunction = open
                ? new CubicEase { EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        widthAnim.Completed += (_, _) =>
        {
            if (_sidebarOpen) return;
            SidebarPanel.BeginAnimation(WidthProperty, null);
            SidebarPanel.Width = 0;
            SidebarPanel.Visibility = Visibility.Collapsed;
        };
        SidebarPanel.BeginAnimation(WidthProperty, widthAnim);

        SidebarContent.BeginAnimation(OpacityProperty,
            new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(open ? 220 : 140)));
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

        Pulse(BtnLoop);
        ShowToast(_loopMode switch
        {
            "List"    => $"列表循环 · 仅当前文件夹（{_playlist.Count} 首）",
            "Shuffle" => $"随机播放 · 仅当前文件夹（{_playlist.Count} 首）",
            _         => "不循环"
        });
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
            : (Brush)FindResource("AccentSoftBrush");
    }

    // ---------------- BPM / 移调 ----------------

    private void ChangeBpm(int delta)
    {
        if (_player.CurrentFilePath == null) return;
        var newBpm = Math.Clamp(_player.CurrentBpm + delta, 20, 400);
        if (newBpm == _player.CurrentBpm) return;
        _player.SetBpm(newBpm);
        LblBpm.Text = $"{_player.CurrentBpm} BPM";
        Flash(LblBpm);
    }

    private void ChangeTranspose(int delta)
    {
        if (_player.CurrentFilePath == null) return;
        var newVal = Math.Clamp(_player.Transpose + delta, -12, 12);
        if (newVal == _player.Transpose) return;
        _player.SetTranspose(newVal);
        LblTranspose.Text = newVal > 0 ? $"+{newVal}" : newVal.ToString();
        Flash(LblTranspose);
        UpdatePlayButton();
    }

    // ---------------- 设置 / 钢琴 / 编辑器 ----------------

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

    private void OpenEditor()
    {
        if (EditorView.IsActive) return;

        // 先把主窗口的播放停下来，避免和编辑器同时占用输出端口
        if (_player.Status != PlayerStatus.Stopped)
        {
            _player.Stop();
            UpdatePlayButton();
            RefreshTimeUI();
        }

        if (!EditorView.Open(_config,
                             _player.CurrentFilePath,
                             () => CmbPort.SelectedItem as string,
                             ShowToast))
        {
            return;
        }

        EditorView.Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 关闭程序时也会询问是否保存编辑器里的修改
        if (!EditorView.ConfirmSaveIfNeeded())
            e.Cancel = true;
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

    // ---------------- 动画小工具 ----------------

    private void PlayIntroAnimation()
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Shell.BeginAnimation(OpacityProperty, fade);

        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(380))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ShellShift.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Toast.BeginAnimation(OpacityProperty, fadeIn);

        var rise = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        ToastShift.BeginAnimation(TranslateTransform.YProperty, rise);

        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) => Toast.Visibility = Visibility.Collapsed;
        Toast.BeginAnimation(OpacityProperty, fadeOut);

        var sink = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        ToastShift.BeginAnimation(TranslateTransform.YProperty, sink);
    }

    private void AnimateFileLabel()
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(280))));
        LblFile.BeginAnimation(OpacityProperty, anim);
    }

    private static void Flash(FrameworkElement el)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
        el.BeginAnimation(OpacityProperty, anim);
    }

    private static void Pulse(FrameworkElement? el)
    {
        if (el == null) return;

        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        el.RenderTransform = scale;

        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(
            1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(320)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }
}
