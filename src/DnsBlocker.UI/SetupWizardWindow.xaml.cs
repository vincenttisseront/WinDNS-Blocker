using System.Windows;
using System.Windows.Controls;
using DnsBlocker.Core.Ipc;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Setup;
using DnsBlocker.Core.Updates;
using DnsBlocker.UI.Localization;

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
        SetBusy(true, Loc.T("Wizard.Busy.Service"));
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
                ? Loc.T("Wizard.Service.Connected")
                : probe.Exists
                    ? Loc.F("Wizard.Service.Detail", probe.Detail)
                    : Loc.T("Wizard.Service.Missing");

            WelcomeDomainStatus.Text = string.IsNullOrEmpty(_machineDomain)
                ? Loc.T("Wizard.Domain.Workgroup")
                : Loc.F("Wizard.Domain.Detected", _machineDomain);

            var helper = InstallRepairLauncher.FindHelper();
            var needsRepair = !connected || !probe.Exists || !probe.Running;
            RepairPanel.Visibility = needsRepair ? Visibility.Visible : Visibility.Collapsed;
            if (helper is null)
            {
                RepairButton.IsEnabled = false;
                RepairHint.Text = Loc.F("Wizard.Repair.NoHelper", InstallRepairLauncher.InstallRoot ?? "?");
            }
            else
            {
                RepairButton.IsEnabled = true;
                RepairHint.Text = Loc.F("Wizard.Repair.Helper", helper.Value.FileName);
            }

            _journal.Write(Loc.F("Wizard.Journal.Welcome",
                connected ? "OK" : "KO",
                _machineDomain.Length == 0 ? "workgroup" : _machineDomain));
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
            // Prefer this machine when it hosts DNS Server (hub-on-DC); else suggest farm FQDNs.
            if (LocalDnsTarget.IsDnsServerRunning())
            {
                var localName = LocalDnsTarget.GetPreferredLocalName();
                TargetsBox.Text = localName;
                TargetsDomainHint.Text = Loc.F("Wizard.Targets.SuggestLocal", localName);
            }
            else
            {
                TargetsBox.Text = string.IsNullOrEmpty(_machineDomain)
                    ? "dns1.example.local"
                    : $"dns1.{_machineDomain}\ndns2.{_machineDomain}";
                TargetsDomainHint.Text = string.IsNullOrEmpty(_machineDomain)
                    ? Loc.T("Wizard.Targets.SuggestGeneric")
                    : Loc.F("Wizard.Targets.SuggestDomain", _machineDomain);
            }
        }
        else
        {
            TargetsBox.Text = string.Join(Environment.NewLine, _draft.DnsTargets);
            TargetsDomainHint.Text = string.IsNullOrEmpty(_machineDomain)
                ? ""
                : Loc.F("Wizard.Targets.DomainDetected", _machineDomain);
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
        NextButton.Content = _step == StepCount - 1 ? Loc.T("Btn.Save") : Loc.T("Btn.Next");

        StepTitle.Text = _step switch
        {
            0 => Loc.T("Wizard.Step.1"),
            1 => Loc.T("Wizard.Step.2"),
            2 => Loc.T("Wizard.Step.3"),
            3 => Loc.T("Wizard.Step.4"),
            _ => Loc.T("Wizard.Step.5")
        };
        StepSubtitle.Text = _step switch
        {
            0 => Loc.T("Wizard.Sub.1"),
            1 => Loc.T("Wizard.Sub.2"),
            2 => Loc.T("Wizard.Sub.3"),
            3 => Loc.T("Wizard.Sub.4"),
            _ => Loc.T("Wizard.Sub.5")
        };

        if (_step == 4)
            ConfirmSummary.Text = BuildSummary();

        _journal.Write(Loc.F("Wizard.Journal.Step", _step + 1, StepCount));
    }

    private string BuildSummary()
    {
        CollectFromUi();
        return Loc.F("Wizard.Summary",
            string.Join("\n  ", _draft.DnsTargets),
            _draft.BlocklistsUrl,
            _draft.WhitelistsUrl,
            _draft.PolicyAction,
            string.IsNullOrWhiteSpace(_draft.UpdateFeedUrl) ? Loc.T("Wizard.Summary.Disabled") : _draft.UpdateFeedUrl,
            _draft.CheckUpdatesOnStartup);
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
        _journal.Write(Loc.F("Wizard.Journal.Skip", _step + 1));
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
                    MessageBox.Show(Loc.T("Wizard.NeedTarget"), Loc.T("Wizard.TitleShort"), MessageBoxButton.OK,
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
                Loc.T("Wizard.NeedRealDns"),
                Loc.T("Wizard.TitleShort"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ShowStep(1);
            return;
        }

        SetBusy(true, Loc.T("Wizard.Busy.Saving"));
        try
        {
            _journal.Write(Loc.T("Wizard.Journal.Saving"));
            SetupWizardGate.MarkCompleted(_draft);
            SavedConfig = await _client.SetConfigAsync(_draft).ConfigureAwait(true);
            RequestedApplyNow = ApplyNowBox.IsChecked == true;
            Completed = true;
            _journal.Write(Loc.F("Wizard.Journal.Done", SavedConfig.DnsTargets.Count, RequestedApplyNow));
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _journal.Write(Loc.F("Wizard.Journal.SaveError", ex.Message));
            MessageBox.Show(
                Loc.F("Wizard.SaveFail", ex.Message),
                Loc.T("Wizard.TitleShort"),
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
                Loc.T("Wizard.Repair.NoHelperBox"),
                Loc.T("Setup.RepairTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _journal.Write(Loc.T("Wizard.Journal.RepairMissing"));
            return;
        }

        _journal.Write(Loc.F("Wizard.Journal.RepairLaunch", helper.Value.FileName));
        var err = InstallRepairLauncher.Launch(helper.Value);
        if (err is not null)
        {
            _journal.Write(Loc.F("Wizard.Journal.RepairFail", err));
            MessageBox.Show(err, Loc.T("Setup.RepairTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show(
                Loc.T("Wizard.Repair.Launched"),
                Loc.T("Setup.RepairTitle"),
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
        SetupWizardCompleted = c.SetupWizardCompleted,
        UiLanguage = c.UiLanguage
    };
}
