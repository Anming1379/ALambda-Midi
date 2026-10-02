using System;
using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox  = System.Windows.MessageBox;

namespace MidiPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show(
                $"发生严重错误：\n{ex?.Message}\n\n程序即将退出。",
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    private void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"发生错误：\n{e.Exception.Message}\n\n程序将继续运行。",
            "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}