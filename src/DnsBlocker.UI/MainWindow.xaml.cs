using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Setup;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.UI;

public partial class MainWindow : Window
{
    private readonly IpcClient _client = new();
    private readonly ActionJournal _journal = new();
    private AppConfig? _config;
    private bool _startupUpdateCheckDone;
    private bool _serviceConnected;
    private bool _forceFirstRunWizard;
    private int _busyDepth;
    private IReadOnlyList<string> _lastServiceLogs = [];
    private string _machineDomain = "";

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + AppVersion.Current;
        PrefillMachineDomain();
        DomainSearchBox.TextChanged += (_, _) => UpdateDomainPlaceholder();
        RefreshLogsView();
        Loaded += OnLoaded;
    }

    /// <summary>
    /// Called when install Setup assistant hands off with /firstrun.
    /// Forces the PR #6 SetupWizardWindow even if a stale setup-complete.flag exists.
    /// </summary>
    public void EnableFirstRunMode()
    {
        _forceFirstRunWizard = true;
        _journal.Write("Handoff install → configuration initiale (/firstrun)");
        RefreshLogsView();
    }

    private void PrefillMachineDomain()
    {
        _machineDomain = MachineDomainDetector.GetDnsDomain();
        if (!string.IsNullOrEmpty(_machineDomain))
        {
            DomainSearchBox.Text = _machineDomain;
            DomainHintLabel.Text = $"Domaine détecté sur cette machine : {_machineDomain}";
            _journal.Write($"Domaine machine détecté : {_machineDomain}");
        }
        else
        {
            DomainSearchBox.Text = "";
            DomainHintLabel.Text =
                "Aucun suffixe DNS/AD détecté (workgroup). Saisissez un domaine à rechercher dans la blocklist.";
            _journal.Write("Domaine machine : aucun (workgroup) — champ Domaines laissé vide");
        }

        UpdateDomainPlaceholder();
        RefreshLogsView();
    }

    private void UpdateDomainPlaceholder()
    {
        DomainSearchPlaceholder.Visibility =
            string.IsNullOrWhiteSpace(DomainSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Connexion…", async () =>
            {
                _journal.Write("Démarrage UI — actualisation initiale");
                await RefreshAsync(checkUpdatesOnStartup: false).ConfigureAwait(true);
                _journal.Write(_serviceConnected
                    ? "Démarrage UI — service joignable"
                    : "Démarrage UI — service injoignable");
            }).ConfigureAwait(true);

            await MaybeShowSetupWizardAsync(force: _forceFirstRunWizard).ConfigureAwait(true);
            _forceFirstRunWizard = false;

            // Update check after wizard so first-run is not interrupted.
            if (_serviceConnected && _config is not null && _config.CheckUpdatesOnStartup
                && !string.IsNullOrWhiteSpace(_config.UpdateFeedUrl) && !_startupUpdateCheckDone)
            {
                _startupUpdateCheckDone = true;
                await RunBusyAsync("Vérif. mises à jour…", async () =>
                {
                    await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: true).ConfigureAwait(true);
                }).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowServiceDisconnected(ex.Message);
            _journal.Write("Erreur démarrage : " + ex.Message);
            RefreshLogsView();
            MessageBox.Show(
                "Le démarrage a rencontré une erreur, mais la fenêtre reste ouverte.\n\n" + ex.Message,
                "WinDNS Blocker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Wizard_Click(object sender, RoutedEventArgs e)
    {
        _journal.Write("Action : Relancer l'assistant");
        await MaybeShowSetupWizardAsync(force: true).ConfigureAwait(true);
    }

    private async void TargetWizard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var prefill = TargetsBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(t => !string.Equals(t, "localhost", StringComparison.OrdinalIgnoreCase));
            var wizard = new TargetSetupWizardWindow(_client, _journal, prefill) { Owner = this };
            var ok = wizard.ShowDialog() == true;
            _journal.Write(ok
                ? $"Wizard cible terminé ({wizard.PreparedHostname})"
                : "Wizard cible annulé");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Assistant cible", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task MaybeShowSetupWizardAsync(bool force)
    {
        AppConfig? cfg = _config;
        if (cfg is null && _serviceConnected)
        {
            try { cfg = await _client.GetConfigAsync().ConfigureAwait(true); }
            catch { /* ignore */ }
        }

        cfg ??= new AppConfig();

        if (!force && !SetupWizardGate.ShouldShowWizard(cfg))
            return;

        if (force)
            SetupWizardGate.ResetForRerun(cfg);

        _journal.Write(force
            ? "Ouverture assistant (relance manuelle)"
            : "Ouverture assistant (premier lancement / config incomplète)");

        var wizard = new SetupWizardWindow(_client, _journal, cfg) { Owner = this };
        var ok = wizard.ShowDialog() == true && wizard.Completed;
        RefreshLogsView();

        if (!ok)
        {
            _journal.Write("Assistant fermé sans enregistrement");
            RefreshLogsView();
            return;
        }

        _config = wizard.SavedConfig ?? _config;
        await RunBusyAsync("Actualisation…", async () =>
        {
            await RefreshAsync(checkUpdatesOnStartup: false).ConfigureAwait(true);
            if (wizard.RequestedApplyNow && _serviceConnected)
            {
                _journal.Write("Assistant — sync demandée après enregistrement");
                try
                {
                    var result = await _client.ApplyNowAsync().ConfigureAwait(true);
                    _journal.Write(result.Success
                        ? $"Sync post-assistant OK — {result.DomainCount} domaines"
                        : $"Sync post-assistant — {result.Error ?? "partielle/échec"}");
                    await RefreshAsync(checkUpdatesOnStartup: false).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _journal.Write("Sync post-assistant — erreur : " + ex.Message);
                    MessageBox.Show(ex.Message, "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }).ConfigureAwait(true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Actualisation…", async () =>
            {
                _journal.Write("Action : Actualiser");
                await RefreshAsync().ConfigureAwait(true);
                _journal.Write(_serviceConnected ? "Actualiser — OK" : "Actualiser — service injoignable");
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write("Actualiser — erreur : " + ex.Message);
            ShowServiceDisconnected(ex.Message);
            RefreshLogsView();
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Démarrage service…", async () =>
            {
                StartServiceButton.IsEnabled = false;
                StatusSummary.Text = "Démarrage du service DnsBlocker…";
                StatusDetails.Text = "Demande de démarrage en cours (UAC éventuel)…";
                _journal.Write("Action : Démarrer le service");

                var error = await Task.Run(() => ServiceProcessHelper.TryStart(TimeSpan.FromSeconds(30)))
                    .ConfigureAwait(true);

                if (error is not null)
                {
                    _journal.Write("Démarrer le service — échec : " + error);
                    ShowServiceDisconnected(error);
                    MessageBox.Show(error, "Service DnsBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _journal.Write("Démarrer le service — OK (attente pipe IPC)");
                await Task.Delay(800).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);

                if (!_serviceConnected)
                {
                    _journal.Write("Démarrer le service — pipe IPC pas encore prêt");
                    MessageBox.Show(
                        "Le service est démarré mais le pipe IPC ne répond pas encore.\n" +
                        "Attendez quelques secondes puis cliquez « Réessayer la connexion ».",
                        "WinDNS Blocker",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _journal.Write("Démarrer le service — UI reconnectée");
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write("Démarrer le service — erreur : " + ex.Message);
            ShowServiceDisconnected(ex.Message);
        }
        finally
        {
            StartServiceButton.IsEnabled = true;
            RefreshLogsView();
        }
    }

    private async void ApplyNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Sync blocklist…", async () =>
            {
                _journal.Write("Action : Mettre à jour maintenant (sync)");
                var result = await _client.ApplyNowAsync().ConfigureAwait(true);
                var summary = result.Success
                    ? $"Sync OK — {result.DomainCount} domaines / {result.Targets.Count} serveur(s)"
                    : result.PartialSuccess
                        ? $"Sync partielle — {result.Targets.Count(t => t.Status == TargetSyncStatus.Succeeded)} OK, " +
                          $"{result.Targets.Count(t => t.Status == TargetSyncStatus.Failed)} échec(s)"
                        : $"Sync en échec : {result.Error ?? "voir détails"}";
                _journal.Write(summary);
                foreach (var t in result.Targets)
                {
                    _journal.Write(t.Status == TargetSyncStatus.Succeeded
                        ? $"  cible {t.Target} — OK ({t.PolicyCount} politiques)"
                        : $"  cible {t.Target} — ÉCHEC : {t.Error}");
                }

                MessageBox.Show(summary, "WinDNS Blocker", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await RefreshAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write("Sync — erreur : " + ex.Message);
            MessageBox.Show(ex.Message, "Erreur sync", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowServiceDisconnected(ex.Message);
        }
        finally
        {
            RefreshLogsView();
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Vérif. mises à jour…", async () =>
        {
            _journal.Write("Action : Vérifier les mises à jour");
            await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: false).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Enregistrement…", async () =>
            {
                _journal.Write("Action : Enregistrer la configuration");
                var cfg = _config ?? new AppConfig();
                cfg.BlocklistsUrl = BlocklistsUrlBox.Text.Trim();
                cfg.WhitelistsUrl = WhitelistsUrlBox.Text.Trim();
                cfg.RegexUrl = RegexUrlBox.Text.Trim();
                cfg.PolicyAction = (ActionBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "DENY";
                if (int.TryParse(ChunkSizeBox.Text, out var chunk))
                    cfg.ChunkSize = chunk;
                if (int.TryParse(ParallelBox.Text, out var parallel))
                    cfg.MaxParallelTargets = parallel;
                if (int.TryParse(HourBox.Text, out var hour))
                    cfg.ScheduledHour = hour;
                if (int.TryParse(MinuteBox.Text, out var minute))
                    cfg.ScheduledMinute = minute;
                cfg.EnableStatsCollection = StatsCheck.IsChecked == true;
                cfg.CuratedYoutubeAds = CuratedYoutubeAdsCheck.IsChecked == true;
                cfg.CuratedOisdBig = CuratedOisdBigCheck.IsChecked == true;
                cfg.CuratedOisdNsfw = CuratedOisdNsfwCheck.IsChecked == true;
                cfg.CuratedChadmayfieldPorn = CuratedChadmayfieldCheck.IsChecked == true;
                cfg.UpdateFeedUrl = UpdateFeedUrlBox.Text.Trim();
                cfg.CheckUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
                cfg.AutoInstallUpdates = AutoInstallUpdatesBox.IsChecked == true;
                cfg.DnsTargets = TargetsBox.Text
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .ToList();

                _config = await _client.SetConfigAsync(cfg).ConfigureAwait(true);
                _journal.Write(
                    $"Configuration enregistrée — {cfg.DnsTargets.Count} cible(s), action {cfg.PolicyAction}, chunk {cfg.ChunkSize}");
                MessageBox.Show("Configuration enregistrée.", "WinDNS Blocker", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                await RefreshAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write("Enregistrer config — erreur : " + ex.Message);
            MessageBox.Show(ex.Message, "Erreur configuration", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private async void SearchDomains_Click(object sender, RoutedEventArgs e)
    {
        var query = DomainSearchBox.Text?.Trim() ?? "";
        DomainSearchBusy.Visibility = Visibility.Visible;
        try
        {
            await RunBusyAsync("Recherche domaines…", async () =>
            {
                _journal.Write($"Action : Rechercher domaines — requête « {query} »");
                var domains = await _client.SearchDomainsAsync(query, 200).ConfigureAwait(true);
                ApplyDomainResults(domains, query);
                _journal.Write($"Recherche terminée — requête « {query} » — {domains.Count} résultat(s)");
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write($"Recherche — erreur (requête « {query} ») : {ex.Message}");
            DomainSearchStatus.Text = "Erreur : " + ex.Message;
            DomainsEmptyLabel.Visibility = Visibility.Visible;
            DomainsList.ItemsSource = null;
            MessageBox.Show(ex.Message, "Erreur recherche", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DomainSearchBusy.Visibility = Visibility.Collapsed;
            RefreshLogsView();
        }
    }

    private async void VerifyPolicies_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Sonde QRP…", async () =>
            {
                _journal.Write("Vérification des politiques QRP démarrée");
                var probe = await _client.GetTargetPolicyStatusAsync().ConfigureAwait(true);
                BindPolicyProbe(probe);
                foreach (var t in probe.Targets)
                {
                    _journal.Write(t.ProbeOk
                        ? $"{t.Target}: {t.LivePolicyCount} installée(s), {t.LiveEnabledCount} activée(s) → {t.AppliedLabel}"
                        : $"{t.Target}: sonde en échec — {t.ProbeError}");
                }

                _journal.Write($"Vérification terminée ({probe.CheckedAt:u})");
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write("Vérification QRP erreur : " + ex.Message);
            PolicyProbeSummary.Text = "Sonde en échec : " + ex.Message;
            MessageBox.Show(ex.Message, "Erreur sonde QRP", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private async void LoadStats_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync("Chargement stats…", async () =>
            {
                if (!int.TryParse(StatsHoursBox.Text, out var hours))
                    hours = 24;
                var topN = 20;
                if (StatsTopNBox.SelectedItem is ComboBoxItem item
                    && int.TryParse(item.Content?.ToString(), out var parsedTop))
                    topN = parsedTop;

                _journal.Write($"Stats : chargement (dernières {hours}h, top {topN})");
                var stats = await _client.GetStatsAsync(hours, topN).ConfigureAwait(true);
                BindStats(stats);
                _journal.Write(
                    $"Stats : {stats.TotalQueries} requêtes, {stats.BlockedQueries} bloquées" +
                    (string.IsNullOrWhiteSpace(stats.Message) ? "" : " — " + stats.Message));
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write("Stats — erreur : " + ex.Message);
            MessageBox.Show(ex.Message, "Erreur stats", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private void BindStats(StatsSnapshot stats)
    {
        StatsSummary.Text =
            $"Requêtes : {stats.TotalQueries} — Bloquées : {stats.BlockedQueries}" +
            $" — Top {stats.TopN} (UTC {stats.FromUtc:g} → {stats.ToUtc:g})";
        StatsMessage.Text = stats.Message ?? "";
        TopQueriedGrid.ItemsSource = stats.TopQueriedDomains;
        TopBlockedGrid.ItemsSource = stats.TopBlockedDomains;
        TopClientsGrid.ItemsSource = stats.TopClients;
    }

    private void BindPolicyProbe(TargetPolicyStatusResult? probe)
    {
        if (probe is null || probe.Targets.Count == 0)
        {
            PolicyStatusGrid.ItemsSource = null;
            PolicyProbeSummary.Text = probe?.Message
                ?? "Aucune sonde encore — cliquez « Vérifier les politiques QRP ».";
            return;
        }

        PolicyStatusGrid.ItemsSource = probe.Targets;
        var ok = probe.Targets.Count(t => t.AppliedOk);
        var fail = probe.Targets.Count(t => !t.ProbeOk);
        PolicyProbeSummary.Text =
            $"Dernière sonde UTC : {probe.CheckedAt:u} — {ok}/{probe.Targets.Count} cible(s) « Appliquées »" +
            (fail > 0 ? $", {fail} sonde(s) en échec" : "") +
            (string.IsNullOrWhiteSpace(probe.Message) ? "" : " — " + probe.Message);
    }

    private void RenderActionLogs(IEnumerable<string>? serviceLogs = null)
    {
        var text = _journal.Render(serviceLogs);
        LogsBox.Text = text;
        StatusActionLogBox.Text = string.Join(Environment.NewLine, _journal.Lines.TakeLast(12));
        StatsActionLogBox.Text = string.Join(Environment.NewLine,
            _journal.Lines.Where(l => l.Contains("[UI] Stats", StringComparison.Ordinal)
                                      || l.Contains("Stats :", StringComparison.Ordinal)
                                      || l.Contains("Stats erreur", StringComparison.Ordinal))
                .TakeLast(8));
    }

    private async Task RefreshAsync(bool checkUpdatesOnStartup = false)
    {
        StatusSummary.Text = "Connexion au service…";
        ServiceHelpPanel.Visibility = Visibility.Collapsed;

        var probe = ServiceProcessHelper.Probe();
        try
        {
            try
            {
                var ver = await _client.GetAppVersionAsync().ConfigureAwait(true);
                VersionLabel.Text = "v" + ver.Version;
            }
            catch
            {
                VersionLabel.Text = "v" + AppVersion.Current;
                throw;
            }

            _config = await _client.GetConfigAsync().ConfigureAwait(true);
            BindConfig(_config);
            var status = await _client.GetStatusAsync().ConfigureAwait(true);
            StatusSummary.Text = status.IsSyncing
                ? "Sync en cours…"
                : $"Domaines actifs : {status.ActiveDomainCount}";
            var last = status.LastRun;
            StatusDetails.Text =
                $"Service Windows : {(probe.Exists ? probe.Detail : "non installé")}\n" +
                $"Domaine machine : {(string.IsNullOrEmpty(_machineDomain) ? "(workgroup)" : _machineDomain)}\n" +
                $"Prochaine sync UTC : {status.NextScheduledUtc?.ToString("u") ?? "—"}\n" +
                $"Dernière sync : {(last is null ? "aucune" : $"{last.StartedAt:u} — domaines {last.DomainCount}, sources {last.SourceListCount}")}\n" +
                $"Stats : {(status.StatsEnabled ? "actives" : "inactives")} — {status.StatsMessage}";
            TargetsGrid.ItemsSource = last?.Targets ?? new List<TargetSyncResult>();
            BindPolicyProbe(status.LastPolicyProbe);
            _lastServiceLogs = status.RecentLogs;

            // Keep the pre-filled machine domain; only auto-search if the box still has that (or user query).
            var query = DomainSearchBox.Text?.Trim() ?? "";
            var domains = await _client.SearchDomainsAsync(query, 100).ConfigureAwait(true);
            ApplyDomainResults(domains, query);
            if (!string.IsNullOrEmpty(query))
                _journal.Write($"Actualisation — recherche domaines « {query} » → {domains.Count} résultat(s)");

            try
            {
                var prepared = await _client.GetPreparedTargetsAsync().ConfigureAwait(true);
                PreparedTargetsSummary.Text = prepared.Count == 0
                    ? "Aucune cible préparée via WinRM. Utilisez « Préparer une cible » pour corriger WIN32 1011 à distance."
                    : "Cibles préparées : " + string.Join(", ",
                        prepared.Select(p =>
                            p.Hostname + (p.Prepared ? " ✓" : "") +
                            (p.CredentialsStored ? " (creds)" : "")));
            }
            catch
            {
                PreparedTargetsSummary.Text = "";
            }

            _serviceConnected = true;
            ServiceHelpPanel.Visibility = Visibility.Collapsed;
            UpdateStatusLine.Text = "";

            // Startup update check is handled by OnLoaded after the setup wizard.
            _ = checkUpdatesOnStartup;
        }
        catch (Exception ex)
        {
            _serviceConnected = false;
            ShowServiceDisconnected(ex.Message, probe);
        }
        finally
        {
            RefreshLogsView();
        }
    }

    private void ApplyDomainResults(IReadOnlyList<string> domains, string query)
    {
        DomainsList.ItemsSource = domains;
        var empty = domains.Count == 0;
        DomainsEmptyLabel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DomainsEmptyLabel.Text = string.IsNullOrWhiteSpace(query)
            ? "Aucun domaine dans la blocklist active (ou liste non synchronisée)."
            : $"Aucun domaine correspondant à « {query} » dans la blocklist active.";
        DomainSearchStatus.Text = string.IsNullOrWhiteSpace(query)
            ? $"{domains.Count} domaine(s) (aperçu)"
            : $"{domains.Count} résultat(s) pour « {query} »";
    }

    private void ShowServiceDisconnected(string message, ServiceProbeResult? probe = null)
    {
        probe ??= ServiceProcessHelper.Probe();
        _serviceConnected = false;
        StatusSummary.Text = "Service injoignable";
        StatusDetails.Text =
            message + "\n\n" +
            $"État service Windows « DnsBlocker » : {(probe.Value.Exists ? probe.Value.Detail : "non installé")}\n" +
            $"Domaine machine : {(string.IsNullOrEmpty(_machineDomain) ? "(workgroup)" : _machineDomain)}\n\n" +
            "Actions possibles :\n" +
            "• Cliquez « Démarrer le service » (UAC si besoin)\n" +
            "• Menu Démarrer → « Assistant d'installation WinDNS Blocker » (diagnostic / réparation sans PowerShell)\n" +
            "• Logs : C:\\ProgramData\\DnsBlocker\\logs\\\n" +
            "• Vérifiez que le MSI a bien installé le service";
        UpdateStatusLine.Text = "Mise à jour : service injoignable — ouvrez l'assistant d'installation";
        ServiceHelpPanel.Visibility = Visibility.Visible;
        RefreshLogsView();
    }

    private async Task CheckUpdatesAsync(bool promptIfAvailable, bool quietIfCurrent)
    {
        try
        {
            UpdateStatusLine.Text = "Mise à jour : vérification…";
            var result = await _client.CheckForUpdatesAsync().ConfigureAwait(true);
            if (result.UpdateAvailable && result.Manifest is not null)
            {
                UpdateStatusLine.Text =
                    $"Mise à jour disponible : {result.Manifest.Version} (actuelle {result.CurrentVersion})";
                _journal.Write(
                    $"Mise à jour disponible : {result.Manifest.Version} (actuelle {result.CurrentVersion})");
                if (!promptIfAvailable)
                    return;

                var notes = string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotes)
                    ? ""
                    : "\n\n" + result.Manifest.ReleaseNotes;
                var choice = MessageBox.Show(
                    $"Une nouvelle version ({result.Manifest.Version}) est disponible.{notes}\n\nInstaller maintenant ?",
                    "Mise à jour WinDNS Blocker",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (choice == MessageBoxResult.Yes)
                {
                    UpdateStatusLine.Text = "Mise à jour : installation en cours…";
                    _journal.Write($"Action : Installer mise à jour {result.Manifest.Version}");
                    var install = await _client.InstallUpdateAsync(result.Manifest, silent: true)
                        .ConfigureAwait(true);
                    if (install.Started)
                    {
                        UpdateStatusLine.Text = "Mise à jour : msiexec démarré (le service peut redémarrer)";
                        _journal.Write("Installation mise à jour — msiexec démarré");
                        MessageBox.Show(
                            "Installation démarrée. Le service peut s'arrêter brièvement pendant la mise à niveau MSI.",
                            "WinDNS Blocker",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        UpdateStatusLine.Text = "Mise à jour : échec — " + (install.Error ?? "erreur inconnue");
                        _journal.Write("Installation mise à jour — échec : " + (install.Error ?? "?"));
                        MessageBox.Show(install.Error ?? "Échec de l'installation.", "Erreur mise à jour",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    UpdateStatusLine.Text =
                        $"Mise à jour disponible : {result.Manifest.Version} (plus tard)";
                    _journal.Write("Mise à jour reportée par l'utilisateur");
                }
            }
            else if (result.FeedError)
            {
                var msg = FormatUpdateFeedError(result.Message);
                UpdateStatusLine.Text = "Mise à jour : " + msg;
                _journal.Write("Vérifier mises à jour — erreur flux : " + msg);
                if (!quietIfCurrent)
                {
                    MessageBox.Show(msg, "Erreur mise à jour", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                UpdateStatusLine.Text = result.Message ?? $"À jour ({result.CurrentVersion})";
                _journal.Write(result.Message ?? $"À jour ({result.CurrentVersion})");
                if (!quietIfCurrent)
                {
                    MessageBox.Show(
                        result.Message ?? "Aucune mise à jour disponible.",
                        "WinDNS Blocker",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = FormatUpdateFeedError(ex.Message);
            UpdateStatusLine.Text = "Mise à jour : " + msg;
            _journal.Write("Vérifier mises à jour — erreur : " + msg);
            if (!quietIfCurrent)
                MessageBox.Show(msg, "Erreur mise à jour", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshLogsView();
        }
    }

    private void BindConfig(AppConfig cfg)
    {
        BlocklistsUrlBox.Text = cfg.BlocklistsUrl;
        WhitelistsUrlBox.Text = cfg.WhitelistsUrl;
        RegexUrlBox.Text = cfg.RegexUrl;
        CuratedYoutubeAdsCheck.IsChecked = cfg.CuratedYoutubeAds;
        CuratedOisdBigCheck.IsChecked = cfg.CuratedOisdBig;
        CuratedOisdNsfwCheck.IsChecked = cfg.CuratedOisdNsfw;
        CuratedChadmayfieldCheck.IsChecked = cfg.CuratedChadmayfieldPorn;
        ExtraBlocklistUrlsBox.Text = string.Join(Environment.NewLine, cfg.ExtraBlocklistUrls);
        ChunkSizeBox.Text = cfg.ChunkSize.ToString();
        ParallelBox.Text = cfg.MaxParallelTargets.ToString();
        HourBox.Text = cfg.ScheduledHour.ToString();
        MinuteBox.Text = cfg.ScheduledMinute.ToString();
        StatsCheck.IsChecked = cfg.EnableStatsCollection;
        UpdateFeedUrlBox.Text = cfg.UpdateFeedUrl ?? "";
        CheckUpdatesOnStartupBox.IsChecked = cfg.CheckUpdatesOnStartup;
        AutoInstallUpdatesBox.IsChecked = cfg.AutoInstallUpdates;
        TargetsBox.Text = string.Join(Environment.NewLine, cfg.DnsTargets);
        ActionBox.SelectedIndex = cfg.PolicyAction.Equals("IGNORE", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private void RefreshLogsView()
    {
        RenderActionLogs(_lastServiceLogs);
        LogsBox.CaretIndex = LogsBox.Text.Length;
        LogsBox.ScrollToEnd();
    }

    private async Task RunBusyAsync(string label, Func<Task> action)
    {
        _busyDepth++;
        BusyBar.Visibility = Visibility.Visible;
        BusyLabel.Visibility = Visibility.Visible;
        BusyLabel.Text = label;
        SetActionButtonsEnabled(false);
        try
        {
            await action().ConfigureAwait(true);
        }
        finally
        {
            _busyDepth = Math.Max(0, _busyDepth - 1);
            if (_busyDepth == 0)
            {
                BusyBar.Visibility = Visibility.Collapsed;
                BusyLabel.Visibility = Visibility.Collapsed;
                BusyLabel.Text = "";
                SetActionButtonsEnabled(true);
            }

            RefreshLogsView();
        }
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        WizardButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        CheckUpdatesButton.IsEnabled = enabled;
        ApplyNowButton.IsEnabled = enabled;
        SearchDomainsButton.IsEnabled = enabled;
        if (VerifyPoliciesButton is not null)
            VerifyPoliciesButton.IsEnabled = enabled;
        if (LoadStatsButton is not null)
            LoadStatsButton.IsEnabled = enabled;
    }

    /// <summary>
    /// Prefer French guidance over raw HttpRequestException text
    /// ("Response status code does not indicate success: 404…").
    /// </summary>
    private static string FormatUpdateFeedError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "Impossible de vérifier les mises à jour. Vérifiez l'URL du flux version.json "
                   + $"({UpdateFeedUrls.DefaultPublicFeedUrl}).";
        }

        if (raw.Contains("404", StringComparison.Ordinal)
            || raw.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("introuvable", StringComparison.OrdinalIgnoreCase))
        {
            // Already friendly from UpdateChecker — keep as-is; otherwise rewrite raw HttpRequestException.
            if (raw.Contains("version.json", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("flux", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("feed", StringComparison.OrdinalIgnoreCase))
                return raw;

            return "Flux de mise à jour introuvable (404). "
                   + "Vérifiez l'URL version.json dans Configuration — "
                   + $"recommandé : {UpdateFeedUrls.DefaultPublicFeedUrl}.";
        }

        return raw;
    }
}
