using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.UI;

public partial class MainWindow : Window
{
    private readonly IpcClient _client = new();
    private readonly ActionJournal _journal = new();
    private AppConfig? _config;
    private bool _startupUpdateCheckDone;
    private int _busyCount;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + AppVersion.Current;
        Loaded += async (_, _) => await RefreshAsync(checkUpdatesOnStartup: true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ApplyNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Sync QRP en cours…");
            _journal.Write("Sync manuelle démarrée");
            RenderActionLogs();
            var result = await _client.ApplyNowAsync();
            _journal.Write(result.Success
                ? $"Sync OK — {result.DomainCount} domaines / {result.Targets.Count} serveur(s)"
                : result.PartialSuccess
                    ? $"Sync partielle — {result.Error ?? "voir Statut"}"
                    : $"Sync en échec — {result.Error ?? "voir détails"}");
            MessageBox.Show(
                result.Success
                    ? $"Sync OK — {result.DomainCount} domaines sur {result.Targets.Count} serveur(s)."
                    : result.PartialSuccess
                        ? $"Sync partielle — voir l'onglet Statut.{(result.Error is null ? "" : "\n" + result.Error)}"
                        : $"Sync en échec : {result.Error ?? "voir détails par serveur"}",
                "WinDNS Blocker",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _journal.Write("Sync erreur : " + ex.Message);
            RenderActionLogs();
            MessageBox.Show(ex.Message, "Erreur sync", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void VerifyPolicies_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Sonde QRP (Get-DnsServerQueryResolutionPolicy)…");
            _journal.Write("Vérification des politiques QRP démarrée");
            RenderActionLogs();
            var probe = await _client.GetTargetPolicyStatusAsync();
            BindPolicyProbe(probe);
            foreach (var t in probe.Targets)
            {
                _journal.Write(t.ProbeOk
                    ? $"{t.Target}: {t.LivePolicyCount} installée(s), {t.LiveEnabledCount} activée(s) → {t.AppliedLabel}"
                    : $"{t.Target}: sonde en échec — {t.ProbeError}");
            }

            _journal.Write($"Vérification terminée ({probe.CheckedAt:u})");
            RenderActionLogs();
        }
        catch (Exception ex)
        {
            _journal.Write("Vérification QRP erreur : " + ex.Message);
            PolicyProbeSummary.Text = "Sonde en échec : " + ex.Message;
            RenderActionLogs();
            MessageBox.Show(ex.Message, "Erreur sonde QRP", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string? label = null)
    {
        if (busy)
            _busyCount++;
        else if (_busyCount > 0)
            _busyCount--;

        var active = _busyCount > 0;
        BusyBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        BusyLabel.Visibility = active && !string.IsNullOrWhiteSpace(label) ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(label))
            BusyLabel.Text = label;
        else if (!active)
            BusyLabel.Text = "";

        RefreshButton.IsEnabled = !active;
        CheckUpdatesButton.IsEnabled = !active;
        ApplyNowButton.IsEnabled = !active;
        VerifyPoliciesButton.IsEnabled = !active;
        LoadStatsButton.IsEnabled = !active;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: false);

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

            _config = await _client.SetConfigAsync(cfg);
            _journal.Write("Configuration enregistrée (" + string.Join(", ", _config.DnsTargets) + ")");
            RenderActionLogs();
            MessageBox.Show("Configuration enregistrée.", "WinDNS Blocker", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _journal.Write("Config erreur : " + ex.Message);
            RenderActionLogs();
            MessageBox.Show(ex.Message, "Erreur configuration", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SearchDomains_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var domains = await _client.SearchDomainsAsync(DomainSearchBox.Text, 200);
            DomainsList.ItemsSource = domains;
            _journal.Write($"Recherche domaines « {DomainSearchBox.Text} » → {domains.Count} résultat(s)");
            RenderActionLogs();
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
            SetBusy(true, "Chargement des stats…");
            if (!int.TryParse(StatsHoursBox.Text, out var hours))
                hours = 24;
            var topN = 20;
            if (StatsTopNBox.SelectedItem is ComboBoxItem item
                && int.TryParse(item.Content?.ToString(), out var parsedTop))
                topN = parsedTop;

            _journal.Write($"Stats : chargement (dernières {hours}h, top {topN})");
            RenderActionLogs();
            var stats = await _client.GetStatsAsync(hours, topN);
            BindStats(stats);
            _journal.Write(
                $"Stats : {stats.TotalQueries} requêtes, {stats.BlockedQueries} bloquées" +
                (string.IsNullOrWhiteSpace(stats.Message) ? "" : " — " + stats.Message));
            RenderActionLogs();
        }
        catch (Exception ex)
        {
            _journal.Write("Stats erreur : " + ex.Message);
            RenderActionLogs();
            MessageBox.Show(ex.Message, "Erreur stats", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
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
        try
        {
            SetBusy(true, "Actualisation…");
            _journal.Write("Actualisation du statut");
            try
            {
                var ver = await _client.GetAppVersionAsync();
                VersionLabel.Text = "v" + ver.Version;
            }
            catch
            {
                VersionLabel.Text = "v" + AppVersion.Current;
            }

            _config = await _client.GetConfigAsync();
            BindConfig(_config);
            var status = await _client.GetStatusAsync();
            StatusSummary.Text = status.IsSyncing
                ? "Sync en cours…"
                : $"Domaines actifs : {status.ActiveDomainCount}";
            var last = status.LastRun;
            StatusDetails.Text =
                $"Prochaine sync UTC : {status.NextScheduledUtc?.ToString("u") ?? "—"}\n" +
                $"Dernière sync : {(last is null ? "aucune" : $"{last.StartedAt:u} — domaines {last.DomainCount}, sources {last.SourceListCount}")}\n" +
                $"Stats : {(status.StatsEnabled ? "actives" : "inactives")} — {status.StatsMessage}";
            TargetsGrid.ItemsSource = last?.Targets ?? [];
            BindPolicyProbe(status.LastPolicyProbe);
            RenderActionLogs(status.RecentLogs);
            var domains = await _client.SearchDomainsAsync(DomainSearchBox.Text, 100);
            DomainsList.ItemsSource = domains;

            if (checkUpdatesOnStartup && !_startupUpdateCheckDone && _config.CheckUpdatesOnStartup
                && !string.IsNullOrWhiteSpace(_config.UpdateFeedUrl))
            {
                _startupUpdateCheckDone = true;
                await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: true);
            }
        }
        catch (Exception ex)
        {
            StatusSummary.Text = "Service injoignable";
            StatusDetails.Text = ex.Message + "\n\nDémarrez le service DnsBlocker (install.ps1) puis réessayez.";
            UpdateStatusLine.Text = "Mise à jour : service injoignable";
            _journal.Write("Service injoignable : " + ex.Message);
            RenderActionLogs();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CheckUpdatesAsync(bool promptIfAvailable, bool quietIfCurrent)
    {
        try
        {
            UpdateStatusLine.Text = "Mise à jour : vérification…";
            var result = await _client.CheckForUpdatesAsync();
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
                    var install = await _client.InstallUpdateAsync(result.Manifest, silent: true);
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
