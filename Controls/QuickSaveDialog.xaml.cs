using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using MidiPlayer.Services;

namespace MidiPlayer.Controls;

public partial class QuickSaveDialog : Window
{
    private static readonly (string Text, int Num, int Den)[] Signatures =
    {
        ("4/4", 4, 4), ("3/4", 3, 4), ("2/4", 2, 4),
        ("6/8", 6, 8), ("5/4", 5, 4), ("7/8", 7, 8), ("12/8", 12, 8)
    };

    public string MidiName { get; private set; } = "未命名";
    public string Folder   { get; private set; } = "";
    public int    Bpm      { get; private set; } = 120;
    public int    Numerator   { get; private set; } = 4;
    public int    Denominator { get; private set; } = 4;

    public string FilePath => Path.Combine(Folder, MidiName + ".mid");

    public QuickSaveDialog(string name, string folder, int bpm, int numerator, int denominator)
    {
        InitializeComponent();

        MidiName = NormalizeName(name);
        Folder = string.IsNullOrWhiteSpace(folder) ? AppConfig.DefaultEditorFolder : folder.Trim();
        Bpm = Math.Clamp(bpm, 20, 400);

        TxtName.Text = MidiName;
        TxtFolder.Text = Folder;
        TxtBpm.Text = Bpm.ToString();

        foreach (var sig in Signatures) CmbSignature.Items.Add(sig.Text);
        int index = Array.FindIndex(Signatures, s => s.Num == numerator && s.Den == denominator);
        CmbSignature.SelectedIndex = index >= 0 ? index : 0;

        TitleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        BtnClose.Click  += (_, _) => { DialogResult = false; Close(); };
        BtnCancel.Click += (_, _) => { DialogResult = false; Close(); };
        BtnOk.Click     += (_, _) => TryAccept();
        BtnBrowse.Click += OnBrowse;

        TxtName.TextChanged   += (_, _) => UpdatePathPreview();
        TxtFolder.TextChanged += (_, _) => UpdatePathPreview();

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
            else if (e.Key == Key.Enter) TryAccept();
        };

        Loaded += (_, _) =>
        {
            UpdatePathPreview();
            TxtName.Focus();
            TxtName.SelectAll();
        };
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择保存文件夹",
            InitialDirectory = Directory.Exists(TxtFolder.Text) ? TxtFolder.Text : AppConfig.DefaultEditorFolder
        };
        if (dlg.ShowDialog(this) == true)
            TxtFolder.Text = dlg.FolderName;
    }

    private void UpdatePathPreview()
    {
        var name = NormalizeName(TxtName.Text);
        var folder = (TxtFolder.Text ?? "").Trim();
        LblPath.Text = string.IsNullOrEmpty(folder)
            ? "将保存到默认目录：" + AppConfig.DefaultEditorFolder
            : "将保存为：" + Path.Combine(folder, name + ".mid");
    }

    private void TryAccept()
    {
        var name = NormalizeName(TxtName.Text);
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, "请填写 MIDI 名称。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            TxtName.Focus();
            return;
        }

        var folder = (TxtFolder.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(folder)) folder = AppConfig.DefaultEditorFolder;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法创建保存目录：\n" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse((TxtBpm.Text ?? "").Trim(), out int bpm)) bpm = Bpm;
        bpm = Math.Clamp(bpm, 20, 400);

        var sig = Signatures[Math.Clamp(CmbSignature.SelectedIndex, 0, Signatures.Length - 1)];
        var path = Path.Combine(folder, name + ".mid");

        if (File.Exists(path))
        {
            var result = MessageBox.Show(this,
                "这个文件已经存在，要覆盖吗？\n\n" + path, "确认覆盖",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
        }

        MidiName = name;
        Folder = folder;
        Bpm = bpm;
        Numerator = sig.Num;
        Denominator = sig.Den;

        DialogResult = true;
        Close();
    }

    private static string NormalizeName(string? input)
    {
        var name = (input ?? "").Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }
}
