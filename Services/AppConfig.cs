using System;
using System.IO;
using System.Text.Json;
using MediaColor = System.Windows.Media.Color;

namespace MidiPlayer.Services;

public class ColorsConfig
{
    public string Background     { get; set; } = "#FF0A0E14";
    public string Playhead       { get; set; } = "#FFFFFFFF";
    public string GridWeak       { get; set; } = "#28FFFFFF";
    public string GridStrong     { get; set; } = "#5AFFFFFF";
    public string NoteInactive   { get; set; } = "#3CFFFFFF";
    public string NoteActive     { get; set; } = "#FFFFFFFF";
}

public class AppConfig
{

public bool SidebarOpen { get; set; } = false;
    public string? LibraryFolder { get; set; }
public string  LoopMode      { get; set; } = "None";   // None / List / Shuffle

    public string? LastPort          { get; set; }
    public string? LastFile          { get; set; }
    public bool    HorizontalMode    { get; set; } = false;
    public bool    ShowGrid          { get; set; } = true;
    public ColorsConfig Colors       { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 配置文件路径：与可执行文件同目录下的 config.json。
    /// 打包后用户可以手工编辑。
    /// </summary>
public static string ConfigPath
{
    get
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ALambda Midi");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "config.json");
    }
}

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new AppConfig();
            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<AppConfig>(json);
            return cfg ?? new AppConfig();
        }
        catch
        {
            // 配置文件损坏时静默回退到默认值
            return new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch { /* 保存失败不弹窗，避免打断用户 */ }
    }

    // ---- hex ↔ color ----
    // 支持 #RRGGBB（不透明）和 #AARRGGBB（带 alpha）

    public static MediaColor ParseColor(string hex, MediaColor fallback)
    {
        try
        {
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                return MediaColor.FromRgb(r, g, b);
            }
            if (hex.Length == 8)
            {
                byte a = Convert.ToByte(hex.Substring(0, 2), 16);
                byte r = Convert.ToByte(hex.Substring(2, 2), 16);
                byte g = Convert.ToByte(hex.Substring(4, 2), 16);
                byte b = Convert.ToByte(hex.Substring(6, 2), 16);
                return MediaColor.FromArgb(a, r, g, b);
            }
        }
        catch { }
        return fallback;
    }

    public static string FormatColor(MediaColor c) =>
        $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}