using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MidiPlayer.Services;

public static class FileAssociationService
{
    private const string ProgId   = "ALambdaMidi.Document";
    private const string Friendly = "MIDI 文件";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST      = 0x0000;

    private static string ExePath =>
        Process.GetCurrentProcess().MainModule?.FileName
        ?? Path.Combine(AppContext.BaseDirectory, "ALambdaMidi.exe");

    /// <summary>检查当前用户是否已把 .mid 关联到本程序</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\.mid");
            var val = key?.GetValue(null) as string;
            return string.Equals(val, ProgId, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>注册 .mid / .midi 关联（HKCU，不需要管理员）</summary>
    public static bool Register()
    {
        try
        {
            // ProgID + 打开命令
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog?.SetValue(null, Friendly);
            }
            using (var icon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\DefaultIcon"))
            {
                icon?.SetValue(null, $"\"{ExePath}\",0");
            }
            using (var cmd = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}\shell\open\command"))
            {
                cmd?.SetValue(null, $"\"{ExePath}\" \"%1\"");
            }

            // 把 ProgID 放进 .mid / .midi 的 OpenWithProgids
            AssociateExtension(".mid");
            AssociateExtension(".midi");

            NotifyShell();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AssociateExtension(string ext)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
        key?.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.Binary);
    }

    /// <summary>取消关联（删除当前用户下的注册项）</summary>
    public static bool Unregister()
    {
        try
        {
            TryDelete($@"Software\Classes\{ProgId}");
            UnassociateExtension(".mid");
            UnassociateExtension(".midi");

            NotifyShell();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void UnassociateExtension(string ext)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            key?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        catch { }
    }

    private static void TryDelete(string subKey)
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { }
    }

    private static void NotifyShell()
    {
        try { SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }

    /// <summary>打开 Windows 的"默认应用"设置页面</summary>
    public static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch { }
    }
}