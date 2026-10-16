using System.IO;
using System.Windows;
using System.Windows.Threading;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            BuildUserMessage(e.Exception),
            "WinDNS Blocker — erreur",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogCrash("UnhandledException", ex);
            try
            {
                MessageBox.Show(
                    BuildUserMessage(ex),
                    "WinDNS Blocker — erreur fatale",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // UI may already be gone
            }
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static string BuildUserMessage(Exception ex) =>
        "L'interface s'est arrêtée avec une erreur.\n\n" +
        ex.Message +
        "\n\nVérifiez que le service Windows « DnsBlocker » est démarré, puis exécutez :\n" +
        @"  powershell -ExecutionPolicy Bypass -File ""C:\Program Files\DnsBlocker\doctor.ps1""" +
        "\n\nJournal : " + Path.Combine(AppPaths.LogDirectory, "ui-crash.log");

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var path = Path.Combine(AppPaths.LogDirectory, "ui-crash.log");
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:u} [{kind}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
