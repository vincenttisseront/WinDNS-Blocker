using System.Windows;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.UI.Localization;

namespace DnsBlocker.UI;

public partial class TargetSetupWizardWindow : Window
{
    private readonly IpcClient _client;
    private readonly ActionJournal _journal;
    private readonly string? _prefillHost;
    private int _step;
    private const int StepCount = 4;
    private bool _busy;
    private bool _winRmOk;
    private bool _prepared;

    public bool Completed { get; private set; }
    public string? PreparedHostname { get; private set; }

    public TargetSetupWizardWindow(IpcClient client, ActionJournal journal, string? prefillHostname = null)
    {
        InitializeComponent();
        _client = client;
        _journal = journal;
        _prefillHost = prefillHostname;
        var domain = MachineDomainDetector.GetDnsDomain();
        DomainHint.Text = string.IsNullOrEmpty(domain)
            ? Loc.T("Target.Domain.None")
            : Loc.F("Target.Domain.Hint", domain);
        if (!string.IsNullOrWhiteSpace(_prefillHost))
            HostnameBox.Text = _prefillHost.Trim();
        else if (!string.IsNullOrEmpty(domain))
            HostnameBox.Text = "windc05." + domain;
        ShowStep(0);
    }

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, StepCount - 1);
        StepHost.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepCreds.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepTest.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepApply.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0 && !_busy;
        NextButton.Content = _step == StepCount - 1
            ? (_prepared ? Loc.T("Btn.Finish") : Loc.T("Btn.PrepareFirst"))
            : Loc.T("Btn.Next");
        NextButton.IsEnabled = !_busy && (_step < StepCount - 1 || _prepared);

        StepTitle.Text = _step switch
        {
            0 => Loc.T("Target.Step.1"),
            1 => Loc.T("Target.Step.2"),
            2 => Loc.T("Target.Step.3"),
            _ => Loc.T("Target.Step.4")
        };
        StepSubtitle.Text = _step switch
        {
            0 => Loc.T("Target.Sub.1"),
            1 => Loc.T("Target.Sub.2"),
            2 => Loc.T("Target.Sub.3"),
            _ => Loc.T("Target.Sub.4")
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            ShowStep(_step - 1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_step == 0)
        {
            if (string.IsNullOrWhiteSpace(HostnameBox.Text))
            {
                MessageBox.Show(Loc.T("Target.NeedHost"), Loc.T("Main.TargetWizardTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowStep(1);
            return;
        }

        if (_step == 1)
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text) || PasswordBox.SecurePassword.Length == 0)
            {
                MessageBox.Show(Loc.T("Target.NeedCreds"), Loc.T("Main.TargetWizardTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowStep(2);
            return;
        }

        if (_step == 2)
        {
            if (!_winRmOk)
            {
                var ask = MessageBox.Show(
                    Loc.T("Target.WinRmUnvalidated"),
                    Loc.T("Main.TargetWizardTitle"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (ask != MessageBoxResult.Yes)
                    return;
            }

            ShowStep(3);
            return;
        }

        if (_prepared)
        {
            Completed = true;
            PreparedHostname = HostnameBox.Text.Trim();
            DialogResult = true;
            Close();
        }
    }

    private async void TestWinRm_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, Loc.T("Target.Busy.Test"));
        AppendLog(ActionLogBox, Loc.T("Target.Log.TestStart"));
        _journal.Write(Loc.F("Target.Journal.Test", HostnameBox.Text.Trim()));
        try
        {
            var result = await _client.TestTargetWinRmAsync(new TargetWinRmTestRequest
            {
                Hostname = HostnameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password
            }).ConfigureAwait(true);

            _winRmOk = result.Ok;
            TestStatus.Text = result.Ok ? Loc.T("Target.Test.Ok") : Loc.T("Target.Test.Fail");
            TestStatus.Foreground = result.Ok
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkRed;
            foreach (var line in result.Log)
                AppendLog(ActionLogBox, line);
            AppendLog(ActionLogBox, result.Message);
            _journal.Write(Loc.F("Target.Journal.WinRm", result.Ok ? "OK" : "FAIL", result.Message));
        }
        catch (Exception ex)
        {
            _winRmOk = false;
            TestStatus.Text = Loc.T("Target.Test.Error");
            AppendLog(ActionLogBox, ex.Message);
            _journal.Write(Loc.F("Target.Journal.WinRmError", ex.Message));
            MessageBox.Show(ex.Message, Loc.T("Target.TestTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, Loc.T("Target.Busy.Prepare"));
        PrepareLogBox.Text = "";
        AppendLog(PrepareLogBox, Loc.T("Target.Log.PrepareStart"));
        _journal.Write(Loc.F("Target.Journal.Prepare", HostnameBox.Text.Trim()));
        try
        {
            var result = await _client.PrepareDnsTargetAsync(new PrepareDnsTargetRequest
            {
                Hostname = HostnameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password,
                HubIdentityToGrant = string.IsNullOrWhiteSpace(HubIdentityBox.Text) ? null : HubIdentityBox.Text.Trim(),
                AddHubToDnsAdmins = AddDnsAdminsBox.IsChecked == true,
                PersistCredentials = PersistCredsBox.IsChecked == true
            }).ConfigureAwait(true);

            foreach (var line in result.Log)
                AppendLog(PrepareLogBox, line);
            AppendLog(PrepareLogBox, result.Message);

            _prepared = result.Ok;
            PrepareStatus.Text = result.Ok ? Loc.T("Target.Prepare.Ok") : Loc.T("Target.Prepare.Fail");
            PrepareStatus.Foreground = result.Ok
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkRed;
            NextButton.Content = _prepared ? Loc.T("Btn.Finish") : Loc.T("Btn.PrepareFirst");
            NextButton.IsEnabled = _prepared && !_busy;
            _journal.Write(Loc.F("Target.Journal.PrepareResult", result.Ok ? "OK" : "FAIL"));

            if (result.Ok)
            {
                var granted = result.Target?.HubIdentityGranted is { } id
                    ? Loc.F("Target.Prepare.Granted", id)
                    : "";
                MessageBox.Show(
                    Loc.F("Target.Prepare.DoneBox", granted),
                    Loc.T("Main.TargetWizardTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(result.Message, Loc.T("Target.PrepareTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _prepared = false;
            AppendLog(PrepareLogBox, ex.Message);
            _journal.Write(Loc.F("Target.Journal.PrepareError", ex.Message));
            MessageBox.Show(ex.Message, Loc.T("Target.PrepareTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy && !string.IsNullOrEmpty(label))
            StepSubtitle.Text = label;
        BackButton.IsEnabled = !busy && _step > 0;
        NextButton.IsEnabled = !busy && (_step < StepCount - 1 || _prepared);
        TestWinRmButton.IsEnabled = !busy;
        PrepareButton.IsEnabled = !busy;
    }

    private static void AppendLog(System.Windows.Controls.TextBlock box, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (string.IsNullOrEmpty(box.Text))
            box.Text = line;
        else
            box.Text += Environment.NewLine + line;
    }
}
