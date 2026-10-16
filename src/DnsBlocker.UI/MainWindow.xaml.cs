using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Config;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Setup;
using DnsBlocker.Core.Updates;
using DnsBlocker.UI.Localization;

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
    private bool _languageBoxReady;

    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = "v" + AppVersion.Current;
        InitLanguagePickerLabels();
        PrefillMachineDomain();
        DomainSearchBox.TextChanged += (_, _) => UpdateDomainPlaceholder();
        RefreshLogsView();
        Loaded += OnLoaded;
    }

    private void InitLanguagePickerLabels()
    {
        LangAutoItem.Content = Loc.T("Cfg.Lang.Auto");
        LangEnItem.Content = Loc.T("Cfg.Lang.English");
        LangFrItem.Content = Loc.T("Cfg.Lang.French");
    }

    /// <summary>
    /// Called when install Setup assistant hands off with /firstrun.
    /// Forces the SetupWizardWindow even if a stale setup-complete.flag exists.
    /// </summary>
    public void EnableFirstRunMode()
    {
        _forceFirstRunWizard = true;
        _journal.Write(Loc.T("Main.Journal.FirstRunHandoff"));
        RefreshLogsView();
    }

    private void PrefillMachineDomain()
    {
        _machineDomain = MachineDomainDetector.GetDnsDomain();
        if (!string.IsNullOrEmpty(_machineDomain))
        {
            DomainSearchBox.Text = _machineDomain;
            DomainHintLabel.Text = Loc.F("Main.DomainDetected", _machineDomain);
            _journal.Write(Loc.F("Main.Journal.DomainDetected", _machineDomain));
        }
        else
        {
            DomainSearchBox.Text = "";
            DomainHintLabel.Text = Loc.T("Main.DomainWorkgroup");
            _journal.Write(Loc.T("Main.Journal.DomainNone"));
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
            await RunBusyAsync(Loc.T("Main.Busy.Connecting"), async () =>
            {
                _journal.Write(Loc.T("Main.Journal.StartupRefresh"));
                await RefreshAsync(checkUpdatesOnStartup: false).ConfigureAwait(true);
                _journal.Write(_serviceConnected
                    ? Loc.T("Main.Journal.ServiceReachable")
                    : Loc.T("Main.Journal.ServiceUnreachable"));
            }).ConfigureAwait(true);

            await MaybeShowSetupWizardAsync(force: _forceFirstRunWizard).ConfigureAwait(true);
            _forceFirstRunWizard = false;

            // Update check after wizard so first-run is not interrupted.
            if (_serviceConnected && _config is not null && _config.CheckUpdatesOnStartup
                && !string.IsNullOrWhiteSpace(_config.UpdateFeedUrl) && !_startupUpdateCheckDone)
            {
                _startupUpdateCheckDone = true;
                await RunBusyAsync(Loc.T("Main.Busy.CheckUpdates"), async () =>
                {
                    await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: true).ConfigureAwait(true);
                }).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            ShowServiceDisconnected(ex.Message);
            _journal.Write(Loc.F("Main.Journal.StartupError", ex.Message));
            RefreshLogsView();
            MessageBox.Show(
                Loc.F("Main.StartupErrorBox", ex.Message),
                Loc.T("AppName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Wizard_Click(object sender, RoutedEventArgs e)
    {
        _journal.Write(Loc.T("Main.Journal.RelaunchWizard"));
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
                ? Loc.F("Main.TargetWizardDone", wizard.PreparedHostname)
                : Loc.T("Main.TargetWizardCancel"));
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, Loc.T("Main.TargetWizardTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            ? Loc.T("Main.Journal.OpenWizardManual")
            : Loc.T("Main.Journal.OpenWizardFirst"));

        var wizard = new SetupWizardWindow(_client, _journal, cfg) { Owner = this };
        var ok = wizard.ShowDialog() == true && wizard.Completed;
        RefreshLogsView();

        if (!ok)
        {
            _journal.Write(Loc.T("Main.Journal.WizardClosed"));
            RefreshLogsView();
            return;
        }

        _config = wizard.SavedConfig ?? _config;
        await RunBusyAsync(Loc.T("Main.Busy.Refresh"), async () =>
        {
            await RefreshAsync(checkUpdatesOnStartup: false).ConfigureAwait(true);
            if (wizard.RequestedApplyNow && _serviceConnected)
            {
                _journal.Write(Loc.T("Main.Journal.WizardSync"));
                try
                {
                    var result = await _client.ApplyNowAsync().ConfigureAwait(true);
                    _journal.Write(result.Success
                        ? Loc.F("Main.Journal.PostWizardSyncOk", result.DomainCount)
                        : Loc.F("Main.Journal.PostWizardSyncFail", result.Error ?? Loc.T("Main.Journal.PostWizardPartial")));
                    await RefreshAsync(checkUpdatesOnStartup: false).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _journal.Write(Loc.F("Main.Journal.PostWizardError", ex.Message));
                    MessageBox.Show(ex.Message, "Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }).ConfigureAwait(true);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.Refresh"), async () =>
            {
                _journal.Write(Loc.T("Main.Journal.Refresh"));
                await RefreshAsync().ConfigureAwait(true);
                _journal.Write(_serviceConnected
                    ? Loc.T("Main.Journal.RefreshOk")
                    : Loc.T("Main.Journal.RefreshUnreachable"));
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.RefreshError", ex.Message));
            ShowServiceDisconnected(ex.Message);
            RefreshLogsView();
        }
    }

    private async void StartService_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.StartService"), async () =>
            {
                StartServiceButton.IsEnabled = false;
                StatusSummary.Text = Loc.T("Main.StartService.Status");
                StatusDetails.Text = Loc.T("Main.StartService.Detail");
                _journal.Write(Loc.T("Main.Journal.StartService"));

                var error = await Task.Run(() => ServiceProcessHelper.TryStart(TimeSpan.FromSeconds(30)))
                    .ConfigureAwait(true);

                if (error is not null)
                {
                    _journal.Write(Loc.F("Main.Journal.StartServiceFail", error));
                    ShowServiceDisconnected(error);
                    MessageBox.Show(error, Loc.T("Main.ServiceTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _journal.Write(Loc.T("Main.Journal.StartServiceOk"));
                await Task.Delay(800).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);

                if (!_serviceConnected)
                {
                    _journal.Write(Loc.T("Main.Journal.StartServicePipe"));
                    MessageBox.Show(
                        Loc.T("Main.StartService.PipeNotReady"),
                        Loc.T("AppName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                else
                {
                    _journal.Write(Loc.T("Main.Journal.StartServiceReconnected"));
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.StartServiceError", ex.Message));
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
            await RunBusyAsync(Loc.T("Main.Busy.Sync"), async () =>
            {
                _journal.Write(Loc.T("Main.Journal.ApplyNow"));
                var result = await _client.ApplyNowAsync().ConfigureAwait(true);
                var summary = result.Success
                    ? Loc.F("Main.Sync.Ok", result.DomainCount, result.Targets.Count)
                    : result.PartialSuccess
                        ? Loc.F("Main.Sync.Partial",
                            result.Targets.Count(t => t.Status == TargetSyncStatus.Succeeded),
                            result.Targets.Count(t => t.Status == TargetSyncStatus.Failed))
                        : Loc.F("Main.Sync.Fail", result.Error ?? Loc.T("Main.Sync.SeeDetails"));
                _journal.Write(summary);
                foreach (var t in result.Targets)
                {
                    _journal.Write(t.Status == TargetSyncStatus.Succeeded
                        ? Loc.F("Main.Sync.TargetOk", t.Target, t.PolicyCount)
                        : Loc.F("Main.Sync.TargetFail", t.Target, t.Error));
                }

                MessageBox.Show(summary, Loc.T("AppName"), MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await RefreshAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.SyncError", ex.Message));
            MessageBox.Show(ex.Message, Loc.T("Main.SyncErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            ShowServiceDisconnected(ex.Message);
        }
        finally
        {
            RefreshLogsView();
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(Loc.T("Main.Busy.CheckUpdates"), async () =>
        {
            _journal.Write(Loc.T("Main.Journal.CheckUpdates"));
            await CheckUpdatesAsync(promptIfAvailable: true, quietIfCurrent: false).ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    private async void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.Saving"), async () =>
            {
                _journal.Write(Loc.T("Main.Journal.SaveConfig"));
                var cfg = BuildConfigFromUi();
                if (_serviceConnected)
                    _config = await _client.SetConfigAsync(cfg).ConfigureAwait(true);
                else
                {
                    new ConfigStore().Save(cfg);
                    _config = cfg;
                }

                _journal.Write(Loc.F("Main.Journal.ConfigSaved", cfg.DnsTargets.Count, cfg.PolicyAction, cfg.ChunkSize));
                MessageBox.Show(Loc.T("Main.ConfigSaved"), Loc.T("AppName"), MessageBoxButton.OK,
                    MessageBoxImage.Information);
                if (_serviceConnected)
                    await RefreshAsync().ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.SaveConfigError", ex.Message));
            MessageBox.Show(ex.Message, Loc.T("Main.ConfigErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private AppConfig BuildConfigFromUi()
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
        cfg.CuratedYoutubeAds = CuratedYoutubeAdsCheck.IsChecked == true;
        cfg.CuratedOisdBig = CuratedOisdBigCheck.IsChecked == true;
        cfg.CuratedOisdNsfw = CuratedOisdNsfwCheck.IsChecked == true;
        cfg.CuratedChadmayfieldPorn = CuratedChadmayfieldCheck.IsChecked == true;
        cfg.UpdateFeedUrl = UpdateFeedUrlBox.Text.Trim();
        cfg.CheckUpdatesOnStartup = CheckUpdatesOnStartupBox.IsChecked == true;
        cfg.AutoInstallUpdates = AutoInstallUpdatesBox.IsChecked == true;
        cfg.UiLanguage = SelectedLanguagePreference();
        cfg.DnsTargets = TargetsBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        return cfg;
    }

    private string SelectedLanguagePreference()
    {
        if (LanguageBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            return UiCulture.NormalizePreference(tag);
        return UiCulture.Auto;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageBoxReady || LanguageBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
            return;

        var pref = UiCulture.NormalizePreference(tag);
        var cfg = _config ?? UiCulture.SafeLoadConfig();
        cfg.UiLanguage = pref;
        _config = cfg;
        UiCulture.PersistPreference(pref, cfg);

        // Best-effort push to service so farm machines stay consistent.
        if (_serviceConnected)
        {
            _ = Task.Run(async () =>
            {
                try { await _client.SetConfigAsync(cfg).ConfigureAwait(false); }
                catch { /* offline persist already done */ }
            });
        }

        MessageBox.Show(
            Loc.T("Main.Lang.RestartBody"),
            Loc.T("Main.Lang.RestartTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void SearchDomains_Click(object sender, RoutedEventArgs e)
    {
        var query = DomainSearchBox.Text?.Trim() ?? "";
        DomainSearchBusy.Visibility = Visibility.Visible;
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.SearchDomains"), async () =>
            {
                _journal.Write(Loc.F("Main.Journal.SearchDomains", query));
                var domains = await _client.SearchDomainsAsync(query, 200).ConfigureAwait(true);
                ApplyDomainResults(domains, query);
                _journal.Write(Loc.F("Main.Journal.SearchDone", query, domains.Count));
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.SearchError", query, ex.Message));
            DomainSearchStatus.Text = Loc.F("Main.SearchError", ex.Message);
            DomainsEmptyLabel.Visibility = Visibility.Visible;
            DomainsList.ItemsSource = null;
            MessageBox.Show(ex.Message, Loc.T("Main.SearchErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            await RunBusyAsync(Loc.T("Main.Busy.ProbeQrp"), async () =>
            {
                _journal.Write(Loc.T("Main.Journal.VerifyStart"));
                var probe = await _client.GetTargetPolicyStatusAsync().ConfigureAwait(true);
                BindPolicyProbe(probe);
                foreach (var t in probe.Targets)
                {
                    _journal.Write(t.ProbeOk
                        ? Loc.F("Main.Journal.VerifyTargetOk", t.Target, t.LivePolicyCount, t.LiveEnabledCount, t.AppliedLabel)
                        : Loc.F("Main.Journal.VerifyTargetFail", t.Target, t.ProbeError));
                }

                _journal.Write(Loc.F("Main.Journal.VerifyDone", probe.CheckedAt.ToString("u")));
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.VerifyError", ex.Message));
            PolicyProbeSummary.Text = Loc.F("Main.ProbeFailed", ex.Message);
            MessageBox.Show(ex.Message, Loc.T("Main.ProbeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private async void EnableStats_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.EnableStats"), async () =>
            {
                _journal.Write(Loc.T("Main.Journal.EnableStats"));
                var result = await _client.EnableStatsCollectionAsync().ConfigureAwait(true);
                StatsCheck.IsChecked = true;
                StatsCollectionStatus.Text = result.Message;
                foreach (var t in result.Targets)
                {
                    _journal.Write(t.Ok
                        ? Loc.F("Main.Journal.EnableStatsDone", $"{t.Target}: {t.Message}")
                        : Loc.F("Main.Journal.EnableStatsError", $"{t.Target}: {t.Message}"));
                }

                _journal.Write(Loc.F("Main.Journal.EnableStatsDone", result.Message));
                if (_config is not null)
                    _config.EnableStatsCollection = true;
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.EnableStatsError", ex.Message));
            StatsCollectionStatus.Text = ex.Message;
            MessageBox.Show(ex.Message, Loc.T("Main.StatsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private async void TestBlock_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.TestBlock"), async () =>
            {
                var domain = BlockTestDomainBox.Text.Trim();
                if (domain.StartsWith("*.", StringComparison.Ordinal))
                    domain = domain[2..];

                var target = _config?.DnsTargets.FirstOrDefault();
                var label = string.IsNullOrWhiteSpace(domain) ? "(sample)" : domain;
                _journal.Write(Loc.F("Main.Journal.TestBlock", $"{label} → {target ?? "?"}"));
                var result = await _client.TestDnsBlockAsync(
                    string.IsNullOrWhiteSpace(domain) ? null : domain,
                    target).ConfigureAwait(true);

                BlockTestDomainBox.Text = result.Domain;
                var yes = Loc.T("Main.BlockTest.Yes");
                var no = Loc.T("Main.BlockTest.No");
                var summary = result.ResolutionSummary ?? result.Error ?? "";
                BlockTestResultText.Text = Loc.F(
                    "Main.BlockTest.Result",
                    result.Domain,
                    result.Target,
                    summary,
                    result.InBlocklist ? yes : no,
                    result.AppearsBlocked ? yes : no,
                    result.Guidance);
                _journal.Write(Loc.F(
                    "Main.Journal.TestBlockDone",
                    result.Domain,
                    result.Target,
                    result.AppearsBlocked ? yes : no,
                    summary));
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.TestBlockError", ex.Message));
            BlockTestResultText.Text = ex.Message;
            MessageBox.Show(ex.Message, Loc.T("Main.ProbeErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private async void LoadStats_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunBusyAsync(Loc.T("Main.Busy.LoadStats"), async () =>
            {
                if (!int.TryParse(StatsHoursBox.Text, out var hours))
                    hours = 24;
                var topN = 20;
                if (StatsTopNBox.SelectedItem is ComboBoxItem item
                    && int.TryParse(item.Content?.ToString(), out var parsedTop))
                    topN = parsedTop;

                _journal.Write(Loc.F("Main.Journal.StatsLoad", hours, topN));
                var stats = await _client.GetStatsAsync(hours, topN).ConfigureAwait(true);
                BindStats(stats);
                var msgSuffix = string.IsNullOrWhiteSpace(stats.Message) ? "" : " — " + stats.Message;
                _journal.Write(Loc.F("Main.Journal.StatsDone", stats.TotalQueries, stats.BlockedQueries, msgSuffix));
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Main.Journal.StatsError", ex.Message));
            MessageBox.Show(ex.Message, Loc.T("Main.StatsErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshLogsView();
        }
    }

    private void BindStats(StatsSnapshot stats)
    {
        StatsSummary.Text = Loc.F("Main.Stats.Summary",
            stats.TotalQueries, stats.BlockedQueries, stats.TopN, stats.FromUtc, stats.ToUtc);
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
            PolicyProbeSummary.Text = probe?.Message ?? Loc.T("Status.NoProbeYet");
            return;
        }

        PolicyStatusGrid.ItemsSource = probe.Targets;
        var ok = probe.Targets.Count(t => t.AppliedOk);
        var fail = probe.Targets.Count(t => !t.ProbeOk);
        var failSuffix = fail > 0 ? Loc.F("Main.Probe.FailSuffix", fail) : "";
        var msgSuffix = string.IsNullOrWhiteSpace(probe.Message) ? "" : " — " + probe.Message;
        PolicyProbeSummary.Text = Loc.F("Main.Probe.Summary",
            probe.CheckedAt, ok, probe.Targets.Count, failSuffix, msgSuffix);
    }

    private void RenderActionLogs(IEnumerable<string>? serviceLogs = null)
    {
        var text = _journal.Render(serviceLogs);
        LogsBox.Text = text;
        StatusActionLogBox.Text = string.Join(Environment.NewLine, _journal.Lines.TakeLast(12));
        StatsActionLogBox.Text = string.Join(Environment.NewLine,
            _journal.Lines.Where(l => l.Contains("[UI] Stats", StringComparison.Ordinal)
                                      || l.Contains("Stats :", StringComparison.Ordinal)
                                      || l.Contains("Stats:", StringComparison.Ordinal)
                                      || l.Contains("Stats erreur", StringComparison.Ordinal)
                                      || l.Contains("Stats — error", StringComparison.Ordinal))
                .TakeLast(8));
    }

    private async Task RefreshAsync(bool checkUpdatesOnStartup = false)
    {
        StatusSummary.Text = Loc.T("Status.Connecting");
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
                ? Loc.T("Main.Status.Syncing")
                : Loc.F("Main.Status.ActiveDomains", status.ActiveDomainCount);
            var last = status.LastRun;
            var lastSync = last is null
                ? Loc.T("Main.Status.NoLastSync")
                : Loc.F("Main.Status.LastSyncFmt", last.StartedAt, last.DomainCount, last.SourceListCount);
            StatusDetails.Text = Loc.F("Main.Status.Details",
                probe.Exists ? probe.Detail : Loc.T("Main.Status.NotInstalled"),
                string.IsNullOrEmpty(_machineDomain) ? Loc.T("Main.Status.Workgroup") : _machineDomain,
                status.NextScheduledUtc?.ToString("u") ?? "—",
                lastSync,
                status.StatsEnabled ? Loc.T("Main.Status.StatsOn") : Loc.T("Main.Status.StatsOff"),
                status.StatsMessage);
            StatsCollectionStatus.Text = status.StatsEnabled
                ? Loc.T("Main.Stats.CollectionOn") + " — " + (status.StatsMessage ?? "")
                : Loc.T("Main.Stats.CollectionOff");
            TargetsGrid.ItemsSource = last?.Targets ?? new List<TargetSyncResult>();
            BindPolicyProbe(status.LastPolicyProbe);
            _lastServiceLogs = status.RecentLogs;

            var query = DomainSearchBox.Text?.Trim() ?? "";
            var domains = await _client.SearchDomainsAsync(query, 100).ConfigureAwait(true);
            ApplyDomainResults(domains, query);
            if (!string.IsNullOrEmpty(query))
                _journal.Write(Loc.F("Main.Journal.RefreshSearch", query, domains.Count));

            try
            {
                var prepared = await _client.GetPreparedTargetsAsync().ConfigureAwait(true);
                PreparedTargetsSummary.Text = prepared.Count == 0
                    ? Loc.T("Main.Prepared.None")
                    : Loc.F("Main.Prepared.List", string.Join(", ",
                        prepared.Select(p =>
                            p.Hostname + (p.Prepared ? " ✓" : "") +
                            (p.CredentialsStored ? " (creds)" : ""))));
            }
            catch
            {
                PreparedTargetsSummary.Text = "";
            }

            _serviceConnected = true;
            ServiceHelpPanel.Visibility = Visibility.Collapsed;
            UpdateStatusLine.Text = "";

            _ = checkUpdatesOnStartup;
        }
        catch (Exception ex)
        {
            _serviceConnected = false;
            // Still bind language from disk when service is down.
            try
            {
                var disk = UiCulture.SafeLoadConfig();
                BindLanguagePicker(disk.UiLanguage);
                if (_config is null)
                    _config = disk;
            }
            catch { /* ignore */ }

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
            ? Loc.T("Main.Domains.EmptyNoQuery")
            : Loc.F("Main.Domains.EmptyQuery", query);
        DomainSearchStatus.Text = string.IsNullOrWhiteSpace(query)
            ? Loc.F("Main.Domains.Preview", domains.Count)
            : Loc.F("Main.Domains.Results", domains.Count, query);

        // Prefill Status → Test blocking with a resolvable apex (strip leading *. from wildcards).
        if (!string.IsNullOrWhiteSpace(query) && domains.Count > 0)
        {
            var candidate = domains.FirstOrDefault(d =>
                d.Equals(query.Trim().Trim('.'), StringComparison.OrdinalIgnoreCase)
                || d.Equals("*." + query.Trim().Trim('.'), StringComparison.OrdinalIgnoreCase))
                ?? domains[0];
            if (candidate.StartsWith("*.", StringComparison.Ordinal))
                candidate = candidate[2..];
            BlockTestDomainBox.Text = candidate;
        }
    }

    private void ShowServiceDisconnected(string message, ServiceProbeResult? probe = null)
    {
        probe ??= ServiceProcessHelper.Probe();
        _serviceConnected = false;
        StatusSummary.Text = Loc.T("Main.Disconnected.Summary");
        StatusDetails.Text = Loc.F("Main.Disconnected.Details",
            message,
            probe.Value.Exists ? probe.Value.Detail : Loc.T("Main.Status.NotInstalled"),
            string.IsNullOrEmpty(_machineDomain) ? Loc.T("Main.Status.Workgroup") : _machineDomain);
        UpdateStatusLine.Text = Loc.T("Main.Disconnected.UpdateLine");
        ServiceHelpPanel.Visibility = Visibility.Visible;
        RefreshLogsView();
    }

    private async Task CheckUpdatesAsync(bool promptIfAvailable, bool quietIfCurrent)
    {
        try
        {
            UpdateStatusLine.Text = Loc.T("Main.Update.Checking");
            var result = await _client.CheckForUpdatesAsync().ConfigureAwait(true);
            if (result.UpdateAvailable && result.Manifest is not null)
            {
                UpdateStatusLine.Text =
                    Loc.F("Main.Update.Available", result.Manifest.Version, result.CurrentVersion);
                _journal.Write(Loc.F("Main.Update.Available", result.Manifest.Version, result.CurrentVersion));
                if (!promptIfAvailable)
                    return;

                var notes = string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotes)
                    ? ""
                    : "\n\n" + result.Manifest.ReleaseNotes;
                var choice = MessageBox.Show(
                    Loc.F("Main.Update.Prompt", result.Manifest.Version, notes),
                    Loc.T("Main.Update.Title"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (choice == MessageBoxResult.Yes)
                {
                    UpdateStatusLine.Text = Loc.T("Main.Update.Installing");
                    _journal.Write(Loc.F("Main.Journal.InstallUpdate", result.Manifest.Version));
                    var install = await _client.InstallUpdateAsync(result.Manifest, silent: true)
                        .ConfigureAwait(true);
                    if (install.Started)
                    {
                        UpdateStatusLine.Text = Loc.T("Main.Update.MsiexecStarted");
                        _journal.Write(Loc.T("Main.Journal.InstallStarted"));
                        MessageBox.Show(
                            Loc.T("Main.Update.InstallStartedBox"),
                            Loc.T("AppName"),
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                    else
                    {
                        UpdateStatusLine.Text = Loc.F("Main.Update.InstallFail",
                            install.Error ?? Loc.T("Main.Update.InstallFailUnknown"));
                        _journal.Write(Loc.F("Main.Journal.InstallFail", install.Error ?? "?"));
                        MessageBox.Show(install.Error ?? Loc.T("Main.Update.InstallFailUnknown"),
                            Loc.T("Main.Update.ErrorTitle"),
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    UpdateStatusLine.Text = Loc.F("Main.Update.Later", result.Manifest.Version);
                    _journal.Write(Loc.T("Main.Journal.UpdateDeferred"));
                }
            }
            else if (result.FeedError)
            {
                var msg = FormatUpdateFeedError(result.Message);
                UpdateStatusLine.Text = Loc.F("Main.Update.ErrorLine", msg);
                _journal.Write(Loc.F("Main.Journal.CheckUpdatesFeedError", msg));
                if (!quietIfCurrent)
                {
                    MessageBox.Show(msg, Loc.T("Main.Update.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                UpdateStatusLine.Text = result.Message ?? Loc.F("Main.Update.UpToDate", result.CurrentVersion);
                _journal.Write(result.Message ?? Loc.F("Main.Update.UpToDate", result.CurrentVersion));
                if (!quietIfCurrent)
                {
                    MessageBox.Show(
                        result.Message ?? Loc.T("Main.Update.NoneAvailable"),
                        Loc.T("AppName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
        }
        catch (Exception ex)
        {
            var msg = FormatUpdateFeedError(ex.Message);
            UpdateStatusLine.Text = Loc.F("Main.Update.ErrorLine", msg);
            _journal.Write(Loc.F("Main.Journal.CheckUpdatesError", msg));
            if (!quietIfCurrent)
                MessageBox.Show(msg, Loc.T("Main.Update.ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
        BindLanguagePicker(cfg.UiLanguage);
    }

    private void BindLanguagePicker(string? uiLanguage)
    {
        _languageBoxReady = false;
        var pref = UiCulture.NormalizePreference(uiLanguage);
        LanguageBox.SelectedItem = pref switch
        {
            UiCulture.English => LangEnItem,
            UiCulture.French => LangFrItem,
            _ => LangAutoItem
        };
        _languageBoxReady = true;
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
    /// Prefer localized guidance over raw HttpRequestException text
    /// ("Response status code does not indicate success: 404…").
    /// </summary>
    private static string FormatUpdateFeedError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Loc.F("Main.Update.FeedEmpty", UpdateFeedUrls.DefaultPublicFeedUrl);

        if (raw.Contains("404", StringComparison.Ordinal)
            || raw.Contains("Not Found", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("introuvable", StringComparison.OrdinalIgnoreCase))
        {
            // Already friendly from UpdateChecker — keep as-is; otherwise rewrite raw HttpRequestException.
            if (raw.Contains("version.json", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("flux", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("feed", StringComparison.OrdinalIgnoreCase))
                return raw;

            return Loc.F("Main.Update.Feed404", UpdateFeedUrls.DefaultPublicFeedUrl);
        }

        return raw;
    }
}
