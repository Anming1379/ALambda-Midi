using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MidiPlayer.Services;

namespace MidiPlayer.Controls;

/// <summary>
/// MIDI 编辑器页面：工具栏 + 钢琴窗 / 鼓编辑器 + 状态栏。
/// 直接盖在主窗口可视化区域上方，不是独立窗口。
/// </summary>
public partial class MidiEditorView : UserControl
{
    private readonly DispatcherTimer _uiTimer;

    private AppConfig? _config;
    private Func<string?>? _portProvider;
    private Action<string>? _notify;
    private MidiDocument? _doc;
    private MidiEditorSession? _session;
    private string _sourceKey = "";
    private double _laneExpandedHeight = 112;

    private Brush _textPrimary = Brushes.White;
    private Brush _accentSoft = Brushes.Violet;

    public bool IsActive { get; private set; }
    public bool HasDocument => _doc != null;

    /// <summary>编辑器收起到后台时触发，主窗口用来恢复可视化区域的渲染。</summary>
    public event Action? Deactivated;

    public MidiEditorView()
    {
        InitializeComponent();

        _textPrimary = (Brush)FindResource("TextPrimaryBrush");
        _accentSoft = (Brush)FindResource("AccentSoftBrush");

        Roll.DocumentEdited  += () => { Lane.Refresh(); UpdateStatus(); };
        Roll.SelectionChanged += () => { Lane.Refresh(); UpdateStatus(); };
        Roll.ViewChanged += () => { Lane.Refresh(); UpdateSnapLabel(); };
        Roll.PreviewRequested += OnPreviewRequested;

        Lane.Roll = Roll;
        Lane.DocumentEdited += () => { Roll.Refresh(); UpdateStatus(); };
        Lane.SelectionChanged += () => { Roll.Refresh(); UpdateStatus(); };

        TabVelocity.Click += (_, _) => SetLaneMode(false);
        TabPedal.Click += (_, _) => SetLaneMode(true);
        BtnLaneCollapse.Click += (_, _) => ToggleLaneCollapsed();
        LaneHeader.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            ToggleLaneCollapsed();
            e.Handled = true;
        };

        // 兜底：万一主窗口没有转发按键（焦点在工具栏按钮 / 下拉框 / 编辑区上），
        // 编辑区自己也能收到快捷键。
        PreviewKeyDown += (_, e) =>
        {
            if (!e.Handled && HandleKey(e)) e.Handled = true;
        };

        foreach (var name in PianoRollControl.NoteNames)
            CmbScaleRoot.Items.Add(name);
        CmbScaleRoot.SelectedIndex = 0;

        foreach (var name in PianoRollControl.ScaleNames)
            CmbScaleKind.Items.Add(name);
        CmbScaleKind.SelectedIndex = 0;

        BtnBack.Click  += (_, _) => RequestExit();
        BtnNew.Click   += (_, _) => NewDocument();
        BtnPlay.Click  += (_, _) => TogglePlay();
        BtnStop.Click  += (_, _) => StopAndRewind();
        BtnUndo.Click  += (_, _) => Undo();
        BtnRedo.Click  += (_, _) => Redo();
        BtnSave.Click  += (_, _) => SaveWithDialog();

        ChkSnap.Click  += (_, _) =>
        {
            Roll.SnapEnabled = ChkSnap.IsChecked == true;
            UpdateSnapLabel();
            Roll.Refresh();
        };

        TxtBpmEdit.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyBpmFromEditor();
            e.Handled = true;
        };
        TxtBpmEdit.LostFocus += (_, _) => ApplyBpmFromEditor();
        TxtBpmEdit.PreviewTextInput += (_, e) =>
        {
            foreach (char c in e.Text)
            {
                if (char.IsDigit(c)) continue;
                e.Handled = true;
                return;
            }
        };

        ChkScale.Click += (_, _) =>
        {
            Roll.ScaleAssist = ChkScale.IsChecked == true && !Roll.DrumMode;
            UpdateScaleControls();
            Roll.Refresh();
        };

        CmbScaleRoot.SelectionChanged += (_, _) =>
        {
            Roll.ScaleRoot = Math.Max(0, CmbScaleRoot.SelectedIndex);
            Roll.Refresh();
        };

        CmbScaleKind.SelectionChanged += (_, _) =>
        {
            Roll.ScaleKind = Math.Max(0, CmbScaleKind.SelectedIndex);
            Roll.Refresh();
        };

        BtnDrum.Click += (_, _) => ToggleDrumMode();

        BtnUndo.IsEnabled = false;
        BtnRedo.IsEnabled = false;

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _uiTimer.Tick += (_, _) => UpdatePlaybackUi();
        _uiTimer.Start();

        UpdateStatus();
    }

    // ---------------- 打开 / 关闭 ----------------

    /// <summary>打开编辑器（当前文件或新建）。返回 false 表示用户取消了切换。</summary>
    public bool Open(AppConfig config, string? filePath,
                     Func<string?> portProvider, Action<string> notify)
    {
        _config = config;
        _portProvider = portProvider;
        _notify = notify;

        if (_doc != null) ApplyBpmFromEditor(restartPlayback: false);

        // 同一个文件（或都是新建文档）时保留当前编辑内容
        if (_doc != null &&
            string.Equals(_doc.SourcePath, filePath, StringComparison.OrdinalIgnoreCase))
        {
            IsActive = true;
            Visibility = Visibility.Visible;
            Roll.ResetView();
            UpdateScaleControls();
            UpdateStatus();
            UpdatePlaybackUi();
            FocusRoll();
            return true;
        }

        if (!ConfirmSaveIfNeeded()) return false;
        if (!LoadDocument(filePath)) return false;

        IsActive = true;
        Visibility = Visibility.Visible;
        UpdateScaleControls();
        UpdateStatus();
        UpdatePlaybackUi();
        FocusRoll();
        return true;
    }

    private void FocusRoll()
    {
        // 把焦点从主窗口的控件上拿回来，保证方向键等快捷键直接生效
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Roll.Focus();
            Keyboard.Focus(Roll);
        }), DispatcherPriority.Input);
    }

    private bool LoadDocument(string? filePath)
    {
        _session?.Dispose();
        _session = null;
        _doc = null;
        Lane.Document = null;
        Lane.Session = null;

        try
        {
            _doc = filePath != null ? MidiDocument.FromFile(filePath) : MidiDocument.CreateNew();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this),
                "读取 MIDI 失败：\n" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _sourceKey = "";
        if (filePath != null)
        {
            try { _sourceKey = Path.GetFullPath(filePath).ToLowerInvariant(); }
            catch { _sourceKey = filePath.ToLowerInvariant(); }
        }

        _doc.SaveName = _doc.Title;
        _doc.SaveFolder = AppConfig.DefaultEditorFolder;

        if (_config != null && _sourceKey.Length > 0 &&
            _config.EditorSaves.TryGetValue(_sourceKey, out var info))
        {
            _doc.SavePath = info.Path;
            if (!string.IsNullOrWhiteSpace(info.MidiName)) _doc.SaveName = info.MidiName;
            if (!string.IsNullOrWhiteSpace(info.Folder))   _doc.SaveFolder = info.Folder;

            // 记住上次保存时用的 BPM / 拍号，重新打开源 MIDI 时恢复
            _doc.Bpm         = Math.Clamp(info.Bpm, 20, 400);
            _doc.Numerator   = Math.Clamp(info.Numerator, 1, 32);
            _doc.Denominator = Math.Clamp(info.Denominator, 1, 32);
        }

        _session = new MidiEditorSession(_doc);
        Lane.Document = _doc;
        Lane.Session = _session;
        FocusRoll();

        Roll.Document = _doc;
        Roll.Session = _session;
        Roll.DrumMode = false;
        Roll.SnapEnabled = ChkSnap.IsChecked == true;
        Roll.ScaleAssist = ChkScale.IsChecked == true;
        Roll.ScaleRoot = Math.Max(0, CmbScaleRoot.SelectedIndex);
        Roll.ScaleKind = Math.Max(0, CmbScaleKind.SelectedIndex);
        BtnDrum.Content = "鼓编辑器";
        Roll.ResetView();
        UpdateScaleControls();
        return true;
    }

    /// <summary>新建一个空 MIDI（会先询问是否保存当前修改）。</summary>
    public bool NewDocument()
    {
        ApplyBpmFromEditor(restartPlayback: false);
        if (!ConfirmSaveIfNeeded()) return false;
        if (!LoadDocument(null)) return false;

        IsActive = true;
        Visibility = Visibility.Visible;
        SetLaneMode(false);
        UpdateScaleControls();
        UpdateStatus();
        UpdatePlaybackUi();
        FocusRoll();
        _notify?.Invoke("已新建空 MIDI");
        return true;
    }

    /// <summary>返回可视化界面（会询问保存）。</summary>
    public void RequestExit()
    {
        if (!IsActive) return;
        ApplyBpmFromEditor(restartPlayback: false);
        if (!ConfirmSaveIfNeeded()) return;

        _session?.Stop();
        _session?.ReleaseOutput();
        IsActive = false;
        Visibility = Visibility.Collapsed;
        Deactivated?.Invoke();
    }

    /// <summary>关闭窗口时询问是否保存。返回 false 表示取消关闭。</summary>
    public bool ConfirmSaveIfNeeded()
    {
        // 关闭窗口 / 切换文档前，把 BPM 输入框里还没提交的值也一起提交
        ApplyBpmFromEditor(restartPlayback: false);

        if (_doc == null || !_doc.IsDirty) return true;

        var owner = Window.GetWindow(this);
        var result = owner != null
            ? MessageBox.Show(owner, "是否保存对 MIDI 的修改？", "ALambda Midi",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question)
            : MessageBox.Show("是否保存对 MIDI 的修改？", "ALambda Midi",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.No) return true;

        return _doc.SavePath != null ? SaveToExisting() : SaveWithDialog();
    }

    public void Shutdown()
    {
        _uiTimer.Stop();
        _session?.Stop();
        _session?.Dispose();
        _session = null;
        IsActive = false;
    }

    // ---------------- 播放 ----------------

    private void TogglePlay()
    {
        if (_session == null || _doc == null) return;

        if (_session.IsPlaying)
        {
            _session.Pause();
        }
        else
        {
            var port = _portProvider?.Invoke();
            if (!_session.Play(port))
            {
                _notify?.Invoke(_doc.Notes.Count == 0
                    ? "还没有音符，先在钢琴窗里点一下放一个吧"
                    : "请先在主界面选择有效的 MIDI 输出端口");
            }
            else
            {
                Roll.ScrollPlayheadIntoView();
            }
        }

        UpdatePlaybackUi();
    }

    private void StopAndRewind()
    {
        _session?.Stop();
        Roll.ScrollPlayheadIntoView();
        Roll.Refresh();
        UpdatePlaybackUi();
    }

    private void SeekByBar(int direction)
    {
        if (_doc == null) return;
        long bar = _doc.BarTicks;
        long pos = _session?.PositionTicks ?? 0;
        long target = Math.Clamp((pos / bar + direction) * bar, 0, _doc.ContentEndTicks);
        _session?.SeekTicks(target);
        Roll.ScrollPlayheadIntoView();
        Roll.Refresh();
        UpdatePlaybackUi();
    }

    private void OnPreviewRequested(int note, int channel)
    {
        _session?.PreviewNote(note, channel, _portProvider?.Invoke());
    }

    // ---------------- 编辑 ----------------

    private void Undo()
    {
        if (_doc == null) return;
        _doc.Undo();
        Roll.ClearSelection();
        Roll.Refresh();
        UpdateStatus();
    }

    private void Redo()
    {
        if (_doc == null) return;
        _doc.Redo();
        Roll.ClearSelection();
        Roll.Refresh();
        UpdateStatus();
    }

    private void ToggleDrumMode()
    {
        if (_doc == null) return;

        Roll.DrumMode = !Roll.DrumMode;
        BtnDrum.Content = Roll.DrumMode ? "钢琴窗" : "鼓编辑器";

        Roll.ScaleAssist = !Roll.DrumMode && ChkScale.IsChecked == true;
        UpdateScaleControls();

        Roll.ResetView();
        Lane.Refresh();
        UpdateStatus();
    }

    // ---------------- 力度 / 踏板编辑区 ----------------

    private void SetLaneMode(bool pedal)
    {
        TabVelocity.IsChecked = !pedal;
        TabPedal.IsChecked = pedal;
        Lane.PedalMode = pedal;
        LblLaneHint.Text = pedal
            ? "左键拖动添加踏板，右键拖动删除"
            : "拖动竖条修改力度（Shift 多选可一起改）";
        Lane.Refresh();
    }

    private void ToggleLaneCollapsed()
    {
        if (LaneRow.ActualHeight > 40)
        {
            _laneExpandedHeight = LaneRow.ActualHeight;
            LaneRow.Height = new GridLength(30);
            BtnLaneCollapse.Content = "\ue313";
            BtnLaneCollapse.ToolTip = "展开力度 / 踏板编辑区";
        }
        else
        {
            LaneRow.Height = new GridLength(Math.Max(70, _laneExpandedHeight));
            BtnLaneCollapse.Content = "\ue316";
            BtnLaneCollapse.ToolTip = "收起力度 / 踏板编辑区";
        }
        Lane.Refresh();
    }

    private void UpdateScaleControls()
    {
        bool piano = _doc != null && !Roll.DrumMode;
        bool assistOn = piano && ChkScale.IsChecked == true;
        ChkScale.IsEnabled = piano;
        CmbScaleRoot.IsEnabled = CmbScaleKind.IsEnabled = assistOn;
    }

    // ---------------- 保存 ----------------

    private bool SaveWithDialog()
    {
        ApplyBpmFromEditor();
        if (_doc == null) return false;

        var dlg = new QuickSaveDialog(
            _doc.SaveName ?? _doc.Title,
            _doc.SaveFolder ?? AppConfig.DefaultEditorFolder,
            _doc.Bpm, _doc.Numerator, _doc.Denominator)
        {
            Owner = Window.GetWindow(this)
        };

        if (dlg.ShowDialog() != true) return false;
        return SaveTo(dlg.FilePath, dlg.MidiName, dlg.Folder,
                      dlg.Bpm, dlg.Numerator, dlg.Denominator);
    }

    private bool SaveToExisting()
    {
        ApplyBpmFromEditor();
        if (_doc?.SavePath == null) return false;

        var name = _doc.SaveName ?? Path.GetFileNameWithoutExtension(_doc.SavePath);
        var folder = _doc.SaveFolder
                     ?? Path.GetDirectoryName(_doc.SavePath)
                     ?? AppConfig.DefaultEditorFolder;
        return SaveTo(_doc.SavePath, name, folder,
                      _doc.Bpm, _doc.Numerator, _doc.Denominator);
    }

    private bool SaveTo(string path, string name, string folder,
                        int bpm, int numerator, int denominator)
    {
        if (_doc == null) return false;

        try
        {
            int oldBpm = _doc.Bpm;
            _doc.Bpm = Math.Clamp(bpm, 20, 400);
            _doc.Numerator = Math.Clamp(numerator, 1, 32);
            _doc.Denominator = Math.Clamp(denominator, 1, 32);

            _doc.SaveTo(path);
            _doc.MarkSaved(path, name, folder);

            RememberSave();

            // 在保存窗口里改了 BPM 时，正在播放的会话也要立即用新速度重新开始
            if (oldBpm != _doc.Bpm && _session?.IsPlaying == true)
            {
                long position = _session.PositionTicks;
                _session.Stop();
                if (_session.Play(_portProvider?.Invoke()))
                    _session.SeekTicks(position);
            }

            // 保存窗口里的 BPM 是最终值，直接把工具栏输入框同步过去
            TxtBpmEdit.Text = _doc.Bpm.ToString();
            Roll.Refresh();
            Lane.Refresh();
            UpdateStatus();
            _notify?.Invoke("已保存：" + Path.GetFileName(path));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this),
                "保存失败：\n" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void RememberSave()
    {
        if (_config == null || _doc?.SavePath == null || _sourceKey.Length == 0) return;

        _config.EditorSaves[_sourceKey] = new EditorSaveInfo
        {
            MidiName = _doc.SaveName,
            Folder = _doc.SaveFolder,
            Path = _doc.SavePath,
            Bpm = _doc.Bpm,
            Numerator = _doc.Numerator,
            Denominator = _doc.Denominator
        };
        _config.Save();
    }

    // ---------------- 快捷键 ----------------

    /// <summary>编辑器获得焦点时，由主窗口转发按键。</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (!IsActive || _doc == null) return false;

        bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.S:
                    if (shift) return false;
                    if (_doc.SavePath != null) SaveToExisting();
                    else SaveWithDialog();
                    return true;

                case Key.N:
                    if (shift) return false;
                    NewDocument();
                    return true;

                case Key.E:
                    if (shift) return false;
                    RequestExit();
                    return true;

                case Key.Z:
                    if (Keyboard.FocusedElement is TextBoxBase) return false;
                    if (shift) Redo(); else Undo();
                    return true;

                case Key.Y:
                    if (Keyboard.FocusedElement is TextBoxBase) return false;
                    Redo();
                    return true;

                case Key.C:
                    if (shift) return false;
                    if (Keyboard.FocusedElement is TextBoxBase) return false;
                    int copied = Roll.CopySelectedNotes();
                    _notify?.Invoke(copied > 0
                        ? $"已复制 {copied} 个音符"
                        : "没有选中音符");
                    return true;

                case Key.X:
                    if (shift) return false;
                    if (Keyboard.FocusedElement is TextBoxBase) return false;
                    int cut = Roll.CutSelectedNotes();
                    _notify?.Invoke(cut > 0
                        ? $"已剪切 {cut} 个音符"
                        : "没有选中音符");
                    return true;

                case Key.V:
                    if (shift) return false;
                    if (Keyboard.FocusedElement is TextBoxBase) return false;
                    int pasted = Roll.PasteNotes();
                    _notify?.Invoke(pasted > 0
                        ? $"已粘贴 {pasted} 个音符到播放头"
                        : "剪贴板里没有音符");
                    return true;

                default:
                    return false;
            }
        }

        // 空格 / Esc 永远优先：即使焦点落在工具栏按钮或下拉框上也要生效
        if (!ctrl && e.Key == Key.Space)
        {
            if (!e.IsRepeat) TogglePlay();
            return true;
        }

        if (!ctrl && e.Key == Key.Escape)
        {
            if (!e.IsRepeat) RequestExit();
            return true;
        }

        // Tab：在力度 / 踏板编辑页之间切换
        if (!ctrl && e.Key == Key.Tab)
        {
            if (!e.IsRepeat) SetLaneMode(!Lane.PedalMode);
            return true;
        }

        // BPM 微调：即使焦点在 BPM 输入框里也生效
        if (!ctrl && e.Key == Key.OemOpenBrackets)
        {
            if (!e.IsRepeat) NudgeBpm(-5);
            return true;
        }

        if (!ctrl && e.Key == Key.OemCloseBrackets)
        {
            if (!e.IsRepeat) NudgeBpm(+5);
            return true;
        }

        // 下拉框获得焦点时把方向键让给它自己
        if (Keyboard.FocusedElement is ComboBox || Keyboard.FocusedElement is TextBoxBase)
            return false;

        switch (e.Key)
        {
            case Key.Up:
                Roll.MoveSelectedNotes(shift ? 12 : 1);
                return true;

            case Key.Down:
                Roll.MoveSelectedNotes(shift ? -12 : -1);
                return true;

            case Key.Left:
                SeekByBar(-1);
                return true;

            case Key.Right:
                SeekByBar(1);
                return true;

            case Key.Home:
                _session?.SeekTicks(0);
                Roll.ScrollPlayheadIntoView();
                Roll.Refresh();
                UpdatePlaybackUi();
                return true;

            case Key.Delete:
            case Key.Back:
                int deleted = Roll.DeleteSelectedNotes();
                if (deleted > 0) _notify?.Invoke($"已删除 {deleted} 个音符");
                return true;

            default:
                return false;
        }
    }

    // ---------------- 状态 ----------------

    private void UpdateSnapLabel()
    {
        LblSnap.Text = Roll.SnapEnabled ? $"吸附 {Roll.SnapLabel}" : "吸附 关闭";
    }

    private void NudgeBpm(int delta)
    {
        if (_doc == null) return;
        TxtBpmEdit.Text = Math.Clamp(_doc.Bpm + delta, 20, 400).ToString();
        ApplyBpmFromEditor();
    }

    private void ApplyBpmFromEditor(bool restartPlayback = true)
    {
        if (_doc == null) return;

        if (!int.TryParse((TxtBpmEdit.Text ?? "").Trim(), out int bpm))
        {
            TxtBpmEdit.Text = _doc.Bpm.ToString();
            return;
        }

        bpm = Math.Clamp(bpm, 20, 400);
        if (bpm == _doc.Bpm)
        {
            TxtBpmEdit.Text = bpm.ToString();
            return;
        }

        bool wasPlaying = _session?.IsPlaying == true;
        long position = _session?.PositionTicks ?? 0;

        _doc.Bpm = bpm;
        _doc.MarkDirty();
        TxtBpmEdit.Text = bpm.ToString();

        if (wasPlaying && restartPlayback && _session != null)
        {
            _session.Stop();
            if (_session.Play(_portProvider?.Invoke()))
                _session.SeekTicks(position);
            else
                _notify?.Invoke("BPM 已修改，按空格重新播放");
        }

        Roll.Refresh();
        Lane.Refresh();
        UpdateStatus();
        UpdatePlaybackUi();

        if (restartPlayback) _notify?.Invoke($"BPM 已改为 {bpm}");
    }

    private void UpdateStatus()
    {
        UpdateSnapLabel();

        if (_doc == null)
        {
            LblTitle.Text = "未打开 MIDI";
            LblDirty.Text = "";
            LblSaveState.Text = "";
            LblSelection.Text = "";
            LblSourceInfo.Text = "";
            TxtBpmEdit.Text = "";
            return;
        }

        if (!TxtBpmEdit.IsKeyboardFocusWithin)
            TxtBpmEdit.Text = _doc.Bpm.ToString();

        LblTitle.Text = _doc.SourcePath == null
            ? "新建 MIDI"
            : Path.GetFileName(_doc.SourcePath);
        LblDirty.Text = _doc.IsDirty ? "*" : "";

        LblSaveState.Text = _doc.SavePath == null
            ? "尚未保存 · Ctrl+S 打开保存窗口"
            : (_doc.IsDirty ? "有未保存的修改 · " : "已保存 · ") + _doc.SavePath;
        LblSaveState.ToolTip = _doc.SavePath;

        LblSourceInfo.Text =
            $"BPM {_doc.Bpm} · {_doc.Numerator}/{_doc.Denominator} · PPQ {_doc.Ppq} · 音符 {_doc.Notes.Count}";
        LblSelection.Text = $"选中 {Roll.SelectedCount()} / {_doc.Notes.Count}";

        BtnUndo.IsEnabled = _doc.CanUndo;
        BtnRedo.IsEnabled = _doc.CanRedo;

        Lane.Refresh();
        UpdateTimeline();
    }

    private void UpdatePlaybackUi()
    {
        if (!IsActive || _doc == null) return;

        bool playing = _session?.IsPlaying == true;
        BtnPlay.Content = playing ? "\ue034" : "\ue037";
        BtnPlay.Foreground = playing ? _accentSoft : _textPrimary;

        UpdateTimeline();
    }

    private void UpdateTimeline()
    {
        if (_doc == null) return;
        long pos = _session?.PositionTicks ?? 0;
        LblTime.Text =
            $"{MidiDocument.FormatSeconds(_doc.TicksToSeconds(pos))} / " +
            $"{MidiDocument.FormatSeconds(_doc.TicksToSeconds(_doc.ContentEndTicks))}";
    }
}
