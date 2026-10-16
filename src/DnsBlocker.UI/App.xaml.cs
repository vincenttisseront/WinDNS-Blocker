using System.Windows;
using System.Windows.Threading;

namespace DnsBlocker.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatal("Erreur inattendue", e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowFatal("Erreur fatale", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        ShowFatal("Erreur asynchrone", e.Exception);
    }

    private static void ShowFatal(string title, Exception ex)
    {
        try
        {
            MessageBox.Show(
                "WinDNS Blocker a rencontré une erreur mais reste ouvert.\n\n" +
                title + ":\n" + ex.Message +
                "\n\nSi le service DnsBlocker n'est pas démarré, utilisez le bouton « Démarrer le service ».",
                "WinDNS Blocker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // last resort — never rethrow from the handler
        }
    }
}
