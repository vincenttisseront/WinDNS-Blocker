using System.IO;
using System.Windows;
using System.Windows.Threading;
using DnsBlocker.Core.Diagnostics;
using DnsBlocker.Core.Paths;
using DnsBlocker.UI.Localization;

namespace DnsBlocker.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Register handlers before culture/resources so startup failures still get a dialog + log.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            // Culture + string dictionaries before any Window InitializeComponent.
            UiCulture.ApplyFromConfig();
        }
        catch (Exception ex)
        {
            LogCrash("UiCulture.ApplyFromConfig", ex);
            // Continue with default Loc language / merged dictionary from App.xaml.
        }

        base.OnStartup(e);

        var args = e.Args ?? [];
        var setup = args.Any(a => string.Equals(a, FirstRunHandoff.ArgSetup, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(a, "--setup", StringComparison.OrdinalIgnoreCase));
        var firstRunArg = args.Any(a => string.Equals(a, FirstRunHandoff.ArgFirstRun, StringComparison.OrdinalIgnoreCase)
                                        || string.Equals(a, "--firstrun", StringComparison.OrdinalIgnoreCase));

        if (setup)
        {
            MainWindow = new SetupWindow();
            MainWindow.Show();
            return;
        }

        var main = new MainWindow();
        // /firstrun from Setup assistant forces the config wizard; otherwise SetupWizardGate decides.
        if (firstRunArg)
            main.EnableFirstRunMode();
        MainWindow = main;
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("DispatcherUnhandledException", e.Exception);
        e.Handled = true;
        ShowFatal(Loc.T("Err.Unexpected"), e.Exception);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogCrash("UnhandledException", ex);
            ShowFatal(Loc.T("Err.Fatal"), ex);
        }
        else
        {
            LogCrashRaw("UnhandledException", e.ExceptionObject?.ToString() ?? "(null ExceptionObject)");
            try
            {
                MessageBox.Show(
                    Loc.F("Err.CrashBody", Loc.T("Err.Fatal"),
                        e.ExceptionObject?.GetType().FullName ?? "(non-Exception)",
                        Path.Combine(AppPaths.LogDirectory, "ui-crash.log")),
                    Loc.T("AppName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // last resort
            }
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTaskException", e.Exception);
        e.SetObserved();
        ShowFatal(Loc.T("Err.Async"), e.Exception);
    }

    private static void ShowFatal(string title, Exception ex)
    {
        try
        {
            var detail = ExceptionText.FormatUserFacing(ex);
            var logPath = Path.Combine(AppPaths.LogDirectory, "ui-crash.log");
            MessageBox.Show(
                Loc.F("Err.CrashBody", title, detail, logPath),
                Loc.T("AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Absolute last resort — never rethrow from the handler, never show a blank body.
            try
            {
                MessageBox.Show(
                    "WinDNS Blocker hit an error but stays open.\n\n"
                    + ExceptionText.FormatUserFacing(ex)
                    + "\n\nLog: "
                    + Path.Combine(AppPaths.LogDirectory, "ui-crash.log"),
                    "WinDNS Blocker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var path = Path.Combine(AppPaths.LogDirectory, "ui-crash.log");
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:u} [{kind}]{Environment.NewLine}{ExceptionText.FormatForLog(ex)}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private static void LogCrashRaw(string kind, string text)
    {
        try
        {
            AppPaths.EnsureDirectories();
            var path = Path.Combine(AppPaths.LogDirectory, "ui-crash.log");
            File.AppendAllText(path,
                $"{DateTimeOffset.Now:u} [{kind}]{Environment.NewLine}{text}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }
}
