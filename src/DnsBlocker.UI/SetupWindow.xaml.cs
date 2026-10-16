using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using DnsBlocker.Core.Paths;
using DnsBlocker.UI.Localization;

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
        SetBusy(true, Loc.T("Setup.Busy.Analyze"));
        try
        {
            var report = await Task.Run(InstallHealth.Run).ConfigureAwait(true);
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            StatusTitle.Text = Loc.T("Setup.CheckFailed");
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
        SetBusy(true, Loc.T("Setup.Busy.Repair"));
        RepairButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(msg => ProgressLabel.Text = msg);
            var result = await InstallRepair.RepairAsync(root, progress, CancellationToken.None)
                .ConfigureAwait(true);
            ProgressLabel.Text = result.Message;
            if (!result.Ok)
            {
                MessageBox.Show(result.Message, Loc.T("Setup.RepairTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var report = await Task.Run(InstallHealth.Run).ConfigureAwait(true);
            ApplyReport(report);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("Setup.RepairTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        InstallRootLabel.Text = Loc.F("Setup.InstallRoot", report.InstallRoot);

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
                badge = Loc.T("Setup.Badge.Fail");
                brush = (Brush)FindResource("DangerBrush");
            }

            rows.Add(new Row { Badge = badge, BadgeBrush = brush, Message = item.Message });
        }

        ChecksList.ItemsSource = rows;

        if (report.Healthy)
        {
            StatusTitle.Text = Loc.T("Setup.Healthy.Title");
            ProgressLabel.Text = Loc.T("Setup.Healthy.Detail");
            ContinueButton.IsEnabled = true;
            RepairButton.Content = Loc.T("Btn.RepairRecheck");
        }
        else if (report.SparseInstall)
        {
            StatusTitle.Text = Loc.T("Setup.Sparse.Title");
            ProgressLabel.Text = Loc.T("Setup.Sparse.Detail");
            ContinueButton.IsEnabled = false;
            RepairButton.Content = Loc.T("Btn.RepairInstall");
        }
        else if (!report.ServiceRegistered || !report.ServiceRunning)
        {
            StatusTitle.Text = Loc.T("Setup.ServiceNotReady.Title");
            ProgressLabel.Text = Loc.T("Setup.ServiceNotReady.Detail");
            ContinueButton.IsEnabled = false;
            RepairButton.Content = Loc.T("Btn.RepairService");
        }
        else
        {
            StatusTitle.Text = Loc.T("Setup.Problems.Title");
            ProgressLabel.Text = Loc.T("Setup.Problems.Detail");
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
                Loc.F("Setup.LaunchUiFail", ex.Message),
                Loc.T("AppName"),
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
