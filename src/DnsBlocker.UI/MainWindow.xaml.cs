using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.UI;

public partial class MainWindow : Window
{
    private readonly IpcClient _client = new();
    private AppConfig? _config;
    private bool _startupUpdateCheckDone;
    private bool _serviceConnected;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + AppVersion.Current;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Never let async void kill the process — wrap everything.
        try
        {
            await RefreshAsync(checkUpdatesOnStartup: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowServiceDisconnected(ex.Message);
            MessageBox.Show(
                "Le démarrage a rencontré une erreur, mais la fenêtre reste ouverte.\n\n" + ex.Message,
                "WinDNS Blocker",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowServiceDisconnected(ex.Message);
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StartServiceButton.IsEnabled = false;
            StatusSummary.Text = "Démarrage du service DnsBlocker…";
            StatusDetails.Text = "Demande de démarrage en cours (UAC éventuel)…";

            var error = await Task.Run(() => ServiceProcessHelper.TryStart(TimeSpan.FromSeconds(30)))
                .ConfigureAwait(true);

            if (error is not null)
            {
                ShowServiceDisconnected(error);
                MessageBox.Show(error, "Service DnsBlocker", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Give the IPC server a moment to bind the pipe.
            await Task.Delay(800).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);

            if (!_serviceConnected)
            {
                MessageBox.Show(
                    "Le service est démarré mais le pipe IPC ne répond pas encore.\n" +
                    "Attendez quelques secondes puis cliquez « Réessayer la connexion ».",
                    "WinDNS Blocker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowServiceDisconnected(ex.Message);
        }
        finally
        {
            StartServiceButton.IsEnabled = true;
        }
    }

    private async void ApplyNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyNow_Click_SetBusy(true);
            var result = await _client.ApplyNowAsync().ConfigureAwait(true);
            MessageBox.Show(
                result.Success
                    ? $"Sync OK — {result.DomainCount} domaines sur {result.Targets.Count} serveur(s)."
                    : result.PartialSuccess
                        ? $"Sync partielle — voir l'onglet Statut.{(result.Error is null ? "" : "\n" + result.Error)}"
                        : $"Sync en échec : {result.Error ?? "voir détails par serveur"}",
                "WinDNS Blocker",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erreur sync", MessageBoxButton.OK, MessageBoxImage.Error);
            ShowServiceDisconnected(ex.Message);
        }
        finally
        {
            ApplyNow_Click_SetBusy(false);
        }
    }

    private void ApplyNow_Click_SetBusy(bool busy)
    {
        IsEnabled = !busy;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: false).ConfigureAwait(true);

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
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
            cfg.UpdateFeedUrl = UpdateFeedUrlBox.Text.Trim();
            cfg.CheckUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
            cfg.AutoInstallUpdates = AutoInstallUpdatesBox.IsChecked == true;
            cfg.DnsTargets = TargetsBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            _config = await _client.SetConfigAsync(cfg).ConfigureAwait(true);
            MessageBox.Show("Configuration enregistrée.", "WinDNS Blocker", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erreur configuration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SearchDomains_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var domains = await _client.SearchDomainsAsync(DomainSearchBox.Text, 200).ConfigureAwait(true);
            DomainsList.ItemsSource = domains;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erreur recherche", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void LoadStats_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(StatsHoursBox.Text, out var hours))
                hours = 24;
            var stats = await _client.GetStatsAsync(hours).ConfigureAwait(true);
            StatsSummary.Text = $"Requêtes : {stats.TotalQueries} — Bloquées : {stats.BlockedQueries} (UTC {stats.FromUtc:g} → {stats.ToUtc:g})";
            StatsMessage.Text = stats.Message ?? "";
            TopBlockedGrid.ItemsSource = stats.TopBlockedDomains;
            TopClientsGrid.ItemsSource = stats.TopClients;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Erreur stats", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
                $"Prochaine sync UTC : {status.NextScheduledUtc?.ToString("u") ?? "—"}\n" +
                $"Dernière sync : {(last is null ? "aucune" : $"{last.StartedAt:u} — domaines {last.DomainCount}, sources {last.SourceListCount}")}\n" +
                $"Stats : {(status.StatsEnabled ? "actives" : "inactives")} — {status.StatsMessage}";
            TargetsGrid.ItemsSource = last?.Targets ?? new List<TargetSyncResult>();
            LogsBox.Text = string.Join(Environment.NewLine, status.RecentLogs);
            var domains = await _client.SearchDomainsAsync(DomainSearchBox.Text, 100).ConfigureAwait(true);
            DomainsList.ItemsSource = domains;

            _serviceConnected = true;
            ServiceHelpPanel.Visibility = Visibility.Collapsed;
            UpdateStatusLine.Text = "";

            if (checkUpdatesOnStartup && !_startupUpdateCheckDone && _config.CheckUpdatesOnStartup
                && !string.IsNullOrWhiteSpace(_config.UpdateFeedUrl))
            {
                _startupUpdateCheckDone = true;
                await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _serviceConnected = false;
            ShowServiceDisconnected(ex.Message, probe);
        }
    }

    private void ShowServiceDisconnected(string message, ServiceProbeResult? probe = null)
    {
        probe ??= ServiceProcessHelper.Probe();
        _serviceConnected = false;
        StatusSummary.Text = "Service injoignable";
        StatusDetails.Text =
            message + "\n\n" +
            $"État service Windows « DnsBlocker » : {(probe.Value.Exists ? probe.Value.Detail : "non installé")}\n\n" +
            "Actions possibles :\n" +
            "• Cliquez « Démarrer le service » (UAC si besoin)\n" +
            "• Ou en admin : sc start DnsBlocker\n" +
            "• Vérifiez que le MSI a bien installé le service";
        UpdateStatusLine.Text = "Mise à jour : service injoignable";
        ServiceHelpPanel.Visibility = Visibility.Visible;
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
                    var install = await _client.InstallUpdateAsync(result.Manifest, silent: true).ConfigureAwait(true);
                    if (install.Started)
                    {
                        UpdateStatusLine.Text = "Mise à jour : msiexec démarré (le service peut redémarrer)";
                        MessageBox.Show(
                            "Installation démarrée. Le service peut s'arrêter brièvement pendant la mise à niveau MSI.",
                            "WinDNS Blocker",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        UpdateStatusLine.Text = "Mise à jour : échec — " + (install.Error ?? "erreur inconnue");
                        MessageBox.Show(install.Error ?? "Échec de l'installation.", "Erreur mise à jour",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    UpdateStatusLine.Text =
                        $"Mise à jour disponible : {result.Manifest.Version} (plus tard)";
                }
            }
            else
            {
                UpdateStatusLine.Text = result.Message ?? $"À jour ({result.CurrentVersion})";
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
            UpdateStatusLine.Text = "Mise à jour : " + ex.Message;
            if (!quietIfCurrent)
                MessageBox.Show(ex.Message, "Erreur mise à jour", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BindConfig(AppConfig cfg)
    {
        BlocklistsUrlBox.Text = cfg.BlocklistsUrl;
        WhitelistsUrlBox.Text = cfg.WhitelistsUrl;
        RegexUrlBox.Text = cfg.RegexUrl;
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
}
