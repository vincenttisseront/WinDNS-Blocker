using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using DnsBlocker.Core.Paths;

namespace DnsBlocker.UI;

public partial class SetupWindow : Window
{
    private InstallHealth.Report? _last;
    private bool _busy;

    private sealed class Row
    {
        public string Badge { get; init; } = "";
        public Brush BadgeBrush { get; init; } = Brushes.Gray;
        public string Message { get; init; } = "";
    }

    public SetupWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RunCheckAsync().ConfigureAwait(true);
    }

    private async void Recheck_Click(object sender, RoutedEventArgs e) =>
        await RunCheckAsync().ConfigureAwait(true);

    private async void Repair_Click(object sender, RoutedEventArgs e) =>
        await RunRepairAsync().ConfigureAwait(true);

    private void Continue_Click(object sender, RoutedEventArgs e) =>
        OpenFirstRunAndClose();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunCheckAsync()
    {
        if (_busy) return;
        SetBusy(true, "Analyse de l'installation…");
        try
        {
            var report = await Task.Run(InstallHealth.Run).ConfigureAwait(true);
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            StatusTitle.Text = "Échec du contrôle";
            ProgressLabel.Text = ex.Message;
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async Task RunRepairAsync()
    {
        if (_busy) return;
        var root = _last?.InstallRoot ?? InstallHealth.ResolveInstallRoot();
        SetBusy(true, "Réparation en cours…");
        RepairButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => ProgressLabel.Text = msg);
            var result = await InstallRepair.RepairAsync(root, progress, CancellationToken.None)
                .ConfigureAwait(true);
            ProgressLabel.Text = result.MessageFr;
            if (!result.Ok)
            {
                MessageBox.Show(result.MessageFr, "Réparation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var report = await Task.Run(InstallHealth.Run).ConfigureAwait(true);
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Réparation", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RepairButton.IsEnabled = true;
            SetBusy(false, ProgressLabel.Text);
        }
    }

    private void ApplyReport(InstallHealth.Report report)
    {
        _last = report;
        InstallRootLabel.Text = "Dossier d'installation : " + report.InstallRoot;

        var rows = new List<Row>();
        foreach (var item in report.Items)
        {
            string badge;
            Brush brush;
            if (item.Ok && !item.Warning)
            {
                badge = "OK";
                brush = (Brush)FindResource("AccentBrush");
            }
            else if (item.Warning)
            {
                badge = "ATTN";
                brush = new SolidColorBrush(Color.FromRgb(0xB5, 0x7E, 0x00));
            }
            else
            {
                badge = "ÉCHEC";
                brush = (Brush)FindResource("DangerBrush");
            }

            rows.Add(new Row { Badge = badge, BadgeBrush = brush, Message = item.MessageFr });
        }

        ChecksList.ItemsSource = rows;

        if (report.Healthy)
        {
            StatusTitle.Text = "Installation saine";
            ProgressLabel.Text = "Vous pouvez ouvrir la configuration initiale (cibles DNS, listes).";
            ContinueButton.IsEnabled = true;
            RepairButton.Content = "Revérifier / réparer";
        }
        else if (report.SparseInstall)
        {
            StatusTitle.Text = "Installation incomplète";
            ProgressLabel.Text =
                "Le script doctor était présent mais les binaires service/UI manquent. " +
                "Cliquez sur « Réparer l'installation » (télécharge le MSI et réinstalle sans PowerShell).";
            ContinueButton.IsEnabled = false;
            RepairButton.Content = "Réparer l'installation";
        }
        else if (!report.ServiceRegistered || !report.ServiceRunning)
        {
            StatusTitle.Text = "Service non prêt";
            ProgressLabel.Text =
                "Les fichiers sont là, mais le service Windows n'est pas prêt. " +
                "Utilisez « Réparer l'installation » pour l'enregistrer / le démarrer.";
            ContinueButton.IsEnabled = false;
            RepairButton.Content = "Réparer le service";
        }
        else
        {
            StatusTitle.Text = "Problèmes détectés";
            ProgressLabel.Text = "Corrigez les échecs ci-dessous, puis revérifiez.";
            ContinueButton.IsEnabled = false;
        }
    }

    private void OpenFirstRunAndClose()
    {
        try
        {
            FirstRunHandoff.MarkPending("setup-assistant");
            var ui = Path.Combine(
                _last?.InstallRoot ?? InstallHealth.ResolveInstallRoot(),
                "UI",
                "DnsBlocker.UI.exe");
            if (!File.Exists(ui))
                ui = Environment.ProcessPath ?? ui;

            var psi = new ProcessStartInfo
            {
                FileName = ui,
                Arguments = FirstRunHandoff.ArgFirstRun,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(ui) ?? ""
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Impossible de lancer l'interface : " + ex.Message,
                "WinDNS Blocker",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        Close();
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        RecheckButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(message))
            ProgressLabel.Text = message;
    }
}
