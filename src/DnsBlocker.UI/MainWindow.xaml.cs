using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;

namespace DnsBlocker.UI;

public partial class MainWindow : Window
{
    private readonly IpcClient _client = new();
    private AppConfig? _config;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ApplyNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyNow_Click_SetBusy(true);
            var result = await _client.ApplyNowAsync();
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
            MessageBox.Show(ex.Message, "Erreur sync", MessageBoxButton.OK, MessageBoxImage.Error);
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
            cfg.DnsTargets = TargetsBox.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            _config = await _client.SetConfigAsync(cfg);
            MessageBox.Show("Configuration enregistrée.", "WinDNS Blocker", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
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
            var domains = await _client.SearchDomainsAsync(DomainSearchBox.Text, 200);
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
            var stats = await _client.GetStatsAsync(hours);
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

    private async Task RefreshAsync()
    {
        try
        {
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
            LogsBox.Text = string.Join(Environment.NewLine, status.RecentLogs);
            var domains = await _client.SearchDomainsAsync(DomainSearchBox.Text, 100);
            DomainsList.ItemsSource = domains;
        }
        catch (Exception ex)
        {
            StatusSummary.Text = "Service injoignable";
            StatusDetails.Text = ex.Message + "\n\nDémarrez le service DnsBlocker (install.ps1) puis réessayez.";
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
        TargetsBox.Text = string.Join(Environment.NewLine, cfg.DnsTargets);
        ActionBox.SelectedIndex = cfg.PolicyAction.Equals("IGNORE", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }
}
