using System.Windows;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;

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
            ? "Aucun domaine machine détecté (workgroup)."
            : $"Domaine machine détecté : {domain} — ex. windc05.{domain}";
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
            ? (_prepared ? "Terminer" : "Préparer d'abord…")
            : "Suivant";
        NextButton.IsEnabled = !_busy && (_step < StepCount - 1 || _prepared);

        StepTitle.Text = _step switch
        {
            0 => "Étape 1 / 4 — Serveur DNS",
            1 => "Étape 2 / 4 — Compte administrateur",
            2 => "Étape 3 / 4 — Test WinRM",
            _ => "Étape 4 / 4 — Préparation distante"
        };
        StepSubtitle.Text = _step switch
        {
            0 => "Hostname / FQDN de la cible QRP.",
            1 => "Identifiants pour Invoke-Command / WinRM (jamais journalisés).",
            2 => "Vérifier la connectivité avant d'appliquer les ACL.",
            _ => "ACL registre + probe politique DnsBlocker_Probe_*."
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
                MessageBox.Show("Indiquez un hostname DNS.", "Assistant cible", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ShowStep(1);
            return;
        }

        if (_step == 1)
        {
            if (string.IsNullOrWhiteSpace(UsernameBox.Text) || PasswordBox.SecurePassword.Length == 0)
            {
                MessageBox.Show("Compte et mot de passe requis.", "Assistant cible", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    "WinRM n'a pas été validé. Continuer quand même vers la préparation ?",
                    "Assistant cible",
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
        SetBusy(true, "Test WinRM…");
        AppendLog(ActionLogBox, "Démarrage Test-WSMan / CimSession…");
        _journal.Write($"Assistant cible — test WinRM {HostnameBox.Text.Trim()}");
        try
        {
            var result = await _client.TestTargetWinRmAsync(new TargetWinRmTestRequest
            {
                Hostname = HostnameBox.Text.Trim(),
                Username = UsernameBox.Text.Trim(),
                Password = PasswordBox.Password
            }).ConfigureAwait(true);

            _winRmOk = result.Ok;
            TestStatus.Text = result.Ok ? "WinRM/CIM OK" : "Échec WinRM/CIM";
            TestStatus.Foreground = result.Ok
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkRed;
            foreach (var line in result.Log)
                AppendLog(ActionLogBox, line);
            AppendLog(ActionLogBox, result.Message);
            _journal.Write($"Assistant cible — WinRM {(result.Ok ? "OK" : "FAIL")}: {result.Message}");
        }
        catch (Exception ex)
        {
            _winRmOk = false;
            TestStatus.Text = "Erreur";
            AppendLog(ActionLogBox, ex.Message);
            _journal.Write("Assistant cible — WinRM erreur: " + ex.Message);
            MessageBox.Show(ex.Message, "Test WinRM", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Prepare_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Préparation distante…");
        PrepareLogBox.Text = "";
        AppendLog(PrepareLogBox, "Lancement préparation (ACL + probe QRP)…");
        _journal.Write($"Assistant cible — prepare {HostnameBox.Text.Trim()}");
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
            PrepareStatus.Text = result.Ok ? "Cible préparée" : "Échec préparation";
            PrepareStatus.Foreground = result.Ok
                ? System.Windows.Media.Brushes.DarkGreen
                : System.Windows.Media.Brushes.DarkRed;
            NextButton.Content = _prepared ? "Terminer" : "Préparer d'abord…";
            NextButton.IsEnabled = _prepared && !_busy;
            _journal.Write($"Assistant cible — prepare {(result.Ok ? "OK" : "FAIL")}");

            if (result.Ok)
            {
                MessageBox.Show(
                    "Préparation terminée.\n\n" +
                    "La sync QRP peut maintenant utiliser le compte du service hub.\n" +
                    (result.Target?.HubIdentityGranted is { } id ? $"Identité autorisée : {id}\n" : ""),
                    "Assistant cible",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(result.Message, "Préparation", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _prepared = false;
            AppendLog(PrepareLogBox, ex.Message);
            _journal.Write("Assistant cible — prepare erreur: " + ex.Message);
            MessageBox.Show(ex.Message, "Préparation", MessageBoxButton.OK, MessageBoxImage.Error);
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
