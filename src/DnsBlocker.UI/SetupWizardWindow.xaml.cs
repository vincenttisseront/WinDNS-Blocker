using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Setup;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.UI;

public partial class SetupWizardWindow : Window
{
    private readonly IpcClient _client;
    private readonly ActionJournal _journal;
    private readonly AppConfig _draft;
    private readonly string _machineDomain;
    private int _step;
    private const int StepCount = 5;
    private bool _busy;

    public bool Completed { get; private set; }
    public bool RequestedApplyNow { get; private set; }
    public AppConfig? SavedConfig { get; private set; }

    public SetupWizardWindow(IpcClient client, ActionJournal journal, AppConfig? existing)
    {
        InitializeComponent();
        _client = client;
        _journal = journal;
        _draft = Clone(existing ?? new AppConfig());
        _machineDomain = MachineDomainDetector.GetDnsDomain();
        LoadDraftIntoUi();
        ShowStep(0);
        _ = RefreshWelcomeAsync();
    }

    private async Task RefreshWelcomeAsync()
    {
        SetBusy(true, "Vérification du service…");
        try
        {
            var probe = ServiceProcessHelper.Probe();
            var connected = false;
            try
            {
                await _client.GetAppVersionAsync().ConfigureAwait(true);
                connected = true;
            }
            catch
            {
                connected = false;
            }

            WelcomeServiceStatus.Text = connected
                ? "Service DnsBlocker : connecté"
                : probe.Exists
                    ? $"Service DnsBlocker : {probe.Detail} (IPC injoignable)"
                    : "Service DnsBlocker : non installé / introuvable";

            WelcomeDomainStatus.Text = string.IsNullOrEmpty(_machineDomain)
                ? "Domaine machine : (workgroup — aucun suffixe DNS détecté)"
                : $"Domaine machine : {_machineDomain}";

            var helper = InstallRepairLauncher.FindHelper();
            var needsRepair = !connected || !probe.Exists || !probe.Running;
            RepairPanel.Visibility = needsRepair ? Visibility.Visible : Visibility.Collapsed;
            if (helper is null)
            {
                RepairButton.IsEnabled = false;
                RepairHint.Text =
                    "Aucun assistant de réparation trouvé sous « " + (InstallRepairLauncher.InstallRoot ?? "?") +
                    " » (attendu : Repair-DnsBlocker.exe / .cmd / .ps1 ou Verify-Install.cmd). " +
                    "Réinstallez le MSI v1.0.6+ ou placez Repair-DnsBlocker.ps1 dans le dossier d'installation.";
            }
            else
            {
                RepairButton.IsEnabled = true;
                RepairHint.Text = "Helper détecté : " + helper.Value.FileName;
            }

            _journal.Write(
                $"Assistant — bienvenue (service {(connected ? "OK" : "KO")}, domaine={(_machineDomain.Length == 0 ? "workgroup" : _machineDomain)})");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadDraftIntoUi()
    {
        if (_draft.DnsTargets.Count == 0 || _draft.DnsTargets.All(SetupWizardGate.IsLocalOnlyTarget))
        {
            TargetsBox.Text = string.IsNullOrEmpty(_machineDomain)
                ? "dns1.example.local"
                : $"dns1.{_machineDomain}\ndns2.{_machineDomain}";
            TargetsDomainHint.Text = string.IsNullOrEmpty(_machineDomain)
                ? "Suggestion : remplacez example.local par vos FQDN DNS."
                : $"Suggestion basée sur le domaine détecté « {_machineDomain} » — adaptez les hostnames.";
        }
        else
        {
            TargetsBox.Text = string.Join(Environment.NewLine, _draft.DnsTargets);
            TargetsDomainHint.Text = string.IsNullOrEmpty(_machineDomain)
                ? ""
                : $"Domaine machine détecté : {_machineDomain}";
        }

        BlocklistsUrlBox.Text = string.IsNullOrWhiteSpace(_draft.BlocklistsUrl)
            ? new AppConfig().BlocklistsUrl
            : _draft.BlocklistsUrl;
        WhitelistsUrlBox.Text = string.IsNullOrWhiteSpace(_draft.WhitelistsUrl)
            ? new AppConfig().WhitelistsUrl
            : _draft.WhitelistsUrl;
        ActionBox.SelectedIndex = _draft.PolicyAction.Equals("IGNORE", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        UpdateFeedUrlBox.Text = string.IsNullOrWhiteSpace(_draft.UpdateFeedUrl)
            ? UpdateFeedUrls.DefaultPublicFeedUrl
            : _draft.UpdateFeedUrl;
        CheckUpdatesOnStartupBox.IsChecked = _draft.CheckUpdatesOnStartup;
    }

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, StepCount - 1);
        StepWelcome.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepTargets.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepLists.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepUpdates.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepConfirm.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step > 0 && !_busy;
        SkipButton.Visibility = _step < StepCount - 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _step == StepCount - 1 ? "Enregistrer" : "Suivant";

        StepTitle.Text = _step switch
        {
            0 => "Étape 1 / 5 — Bienvenue",
            1 => "Étape 2 / 5 — Serveurs DNS",
            2 => "Étape 3 / 5 — Listes de blocage",
            3 => "Étape 4 / 5 — Mises à jour (optionnel)",
            _ => "Étape 5 / 5 — Confirmation"
        };
        StepSubtitle.Text = _step switch
        {
            0 => "État du service et de l'installation.",
            1 => "Farm DNS (hub → N serveurs).",
            2 => "Sources LoL Steve Jenkins + action QRP.",
            3 => "Flux version.json pour les mises à jour de l'app.",
            _ => "Enregistrement dans ProgramData via le service."
        };

        if (_step == 4)
            ConfirmSummary.Text = BuildSummary();

        _journal.Write($"Assistant — affichage étape {_step + 1}/{StepCount}");
    }

    private string BuildSummary()
    {
        CollectFromUi();
        return
            $"Cibles DNS:\n  {string.Join("\n  ", _draft.DnsTargets)}\n\n" +
            $"Blocklists: {_draft.BlocklistsUrl}\n" +
            $"Whitelists: {_draft.WhitelistsUrl}\n" +
            $"Action QRP: {_draft.PolicyAction}\n" +
            $"Update feed: {(string.IsNullOrWhiteSpace(_draft.UpdateFeedUrl) ? "(désactivé)" : _draft.UpdateFeedUrl)}\n" +
            $"Check updates on startup: {_draft.CheckUpdatesOnStartup}";
    }

    private void CollectFromUi()
    {
        _draft.DnsTargets = TargetsBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _draft.BlocklistsUrl = BlocklistsUrlBox.Text.Trim();
        _draft.WhitelistsUrl = WhitelistsUrlBox.Text.Trim();
        _draft.PolicyAction = (ActionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DENY";
        _draft.UpdateFeedUrl = UpdateFeedUrlBox.Text.Trim();
        _draft.CheckUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 0)
            ShowStep(_step - 1);
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _journal.Write($"Assistant — passer étape {_step + 1}");
        if (_step < StepCount - 1)
            ShowStep(_step + 1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_step < StepCount - 1)
        {
            if (_step == 1)
            {
                CollectFromUi();
                if (_draft.DnsTargets.Count == 0)
                {
                    MessageBox.Show("Ajoutez au moins une cible DNS.", "Assistant", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            ShowStep(_step + 1);
            return;
        }

        await FinishAsync().ConfigureAwait(true);
    }

    private async Task FinishAsync()
    {
        CollectFromUi();
        if (_draft.DnsTargets.Count == 0 || _draft.DnsTargets.All(SetupWizardGate.IsLocalOnlyTarget))
        {
            MessageBox.Show(
                "Indiquez au moins un serveur DNS réel (pas seulement localhost).",
                "Assistant",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ShowStep(1);
            return;
        }

        SetBusy(true, "Enregistrement…");
        try
        {
            _journal.Write("Assistant — enregistrement configuration");
            SetupWizardGate.MarkCompleted(_draft);
            SavedConfig = await _client.SetConfigAsync(_draft).ConfigureAwait(true);
            RequestedApplyNow = ApplyNowBox.IsChecked == true;
            Completed = true;
            _journal.Write(
                $"Assistant — terminé ({SavedConfig.DnsTargets.Count} cible(s), applyNow={RequestedApplyNow})");
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _journal.Write("Assistant — erreur enregistrement : " + ex.Message);
            MessageBox.Show(
                "Impossible d'enregistrer via le service.\n\n" + ex.Message +
                "\n\nDémarrez ou réparez le service, puis réessayez.",
                "Assistant",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        var helper = InstallRepairLauncher.FindHelper();
        if (helper is null)
        {
            MessageBox.Show(
                "Aucun helper de réparation n'est présent dans le dossier d'installation.\n" +
                "Installez le MSI WinDNS Blocker récent (v1.0.6+) qui embarque Repair-DnsBlocker.",
                "Réparation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _journal.Write("Assistant — réparation : helper introuvable");
            return;
        }

        _journal.Write("Assistant — lancement réparation : " + helper.Value.FileName);
        var err = InstallRepairLauncher.Launch(helper.Value);
        if (err is not null)
        {
            _journal.Write("Assistant — réparation échec : " + err);
            MessageBox.Show(err, "Réparation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(
                "L'assistant de réparation a été lancé (UAC). Revenez ici ensuite et cliquez Suivant / Actualiser.",
                "Réparation",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _ = RefreshWelcomeAsync();
        }
    }

    private void SetBusy(bool busy, string? label = null)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy && !string.IsNullOrEmpty(label))
            StepSubtitle.Text = label;
        BackButton.IsEnabled = !busy && _step > 0;
        NextButton.IsEnabled = !busy;
        SkipButton.IsEnabled = !busy;
    }

    private static AppConfig Clone(AppConfig c) => new()
    {
        BlocklistsUrl = c.BlocklistsUrl,
        WhitelistsUrl = c.WhitelistsUrl,
        RegexUrl = c.RegexUrl,
        ExtraBlocklistUrls = [.. c.ExtraBlocklistUrls],
        ExtraWhitelistUrls = [.. c.ExtraWhitelistUrls],
        CuratedYoutubeAds = c.CuratedYoutubeAds,
        CuratedOisdBig = c.CuratedOisdBig,
        CuratedOisdNsfw = c.CuratedOisdNsfw,
        CuratedChadmayfieldPorn = c.CuratedChadmayfieldPorn,
        DnsTargets = [.. c.DnsTargets],
        PolicyAction = c.PolicyAction,
        ChunkSize = c.ChunkSize,
        MaxParallelTargets = c.MaxParallelTargets,
        ScheduledHour = c.ScheduledHour,
        ScheduledMinute = c.ScheduledMinute,
        EnableStatsCollection = c.EnableStatsCollection,
        PolicyNamePrefix = c.PolicyNamePrefix,
        UpdateFeedUrl = c.UpdateFeedUrl,
        CheckUpdatesOnStartup = c.CheckUpdatesOnStartup,
        AutoInstallUpdates = c.AutoInstallUpdates,
        SetupWizardCompleted = c.SetupWizardCompleted
    };
}
