using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;

namespace MidiPlayer;

public partial class App : Application
{
    /// <summary>
    /// 启动时如果命令行带了文件路径（右键"用 ALambda Midi 打开"），
    /// 存到 Properties 里，供 MainWindow 读取。
    /// </summary>
    public const string StartupFileKey = "StartupFile";

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show(
                $"发生严重错误：\n{ex?.Message}\n\n程序即将退出。",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        if (e.Args.Length > 0)
        {
            var path = e.Args[0];
            if (File.Exists(path) && IsMidiFile(path))
                Current.Properties[StartupFileKey] = path;
        }
    }

    private static bool IsMidiFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mid", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".midi", StringComparison.OrdinalIgnoreCase);
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生错误：\n{e.Exception.Message}\n\n程序将继续运行。",
            "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}