using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsBlocker.Core.Net;
using DnsBlocker.Core.Sync;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Dns;

/// <summary>
/// Applies QRP via the DnsServer PowerShell module.
/// Remote targets use <c>-ComputerName</c> (WinRM/CIM); local DNS targets omit it
/// and talk to the in-box DNS Server on this machine.
/// </summary>
public sealed class PowerShellDnsPolicyProvider : IDnsPolicyProvider
{
    /// <summary>
    /// Practical max length for a single <c>-Fqdn "EQ,..."</c> criterion string.
    /// Larger values have been observed to Add successfully while storing empty/inert criteria.
    /// </summary>
    public const int MaxFqdnCriterionChars = 8192;

    private static readonly Regex Win32CodeRegex = new(@"WIN32\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<PowerShellDnsPolicyProvider> _logger;

    public PowerShellDnsPolicyProvider(ILogger<PowerShellDnsPolicyProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<PowerShellDnsPolicyProvider>.Instance;
    }

    public async Task ApplyPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        CancellationToken cancellationToken = default,
        bool restartDnsAfterApply = true,
        int applyThrottleMs = 15,
        int applyGcEvery = 25,
        IProgress<SyncProgressUpdate>? progress = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PowerShellDnsPolicyProvider requires Windows.");

        var normalizedAction = action.Equals("IGNORE", StringComparison.OrdinalIgnoreCase) ? "IGNORE" : "DENY";
        // SyncEngine already ChunkByFqdnBudget — reuse when within budget (avoids a second full flatten).
        var normalized = EnsureFqdnBudget(chunks, MaxFqdnCriterionChars);

        var manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"DnsBlocker-qrp-{Guid.NewGuid():N}.ndjson");

        try
        {
            var expectedPolicies = CountExpectedPolicies(normalized);
            WriteManifest(
                manifestPath,
                computerName,
                policyNamePrefix,
                normalizedAction,
                normalized,
                restartDnsAfterApply,
                Math.Max(0, applyThrottleMs),
                Math.Max(0, applyGcEvery));
            var script = BuildApplyScriptFromManifest(manifestPath);
            try
            {
                var output = await RunPowerShellAsync(
                        script,
                        cancellationToken,
                        line => TryReportApplyProgress(progress, computerName, expectedPolicies, line))
                    .ConfigureAwait(false);
                ValidateApplyOutput(output, computerName, expectedPolicies);
                _logger.LogInformation(
                    "Applied and verified {Count} QRP policies on {Target} (prefix {Prefix}, action {Action}, restartDns={Restart}, throttle={Throttle}ms/{GcEvery})",
                    normalized.Count, computerName, policyNamePrefix, normalizedAction, restartDnsAfterApply,
                    applyThrottleMs, applyGcEvery);
                if (!string.IsNullOrWhiteSpace(output))
                    _logger.LogDebug("PowerShell output: {Output}", output.Trim());
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(EnrichDnsPolicyError(ex.Message, computerName), ex);
            }
        }
        finally
        {
            try { File.Delete(manifestPath); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Parses <c>OK progress N/M</c> lines from the apply script for live Status UI updates.</summary>
    public static void TryReportApplyProgress(
        IProgress<SyncProgressUpdate>? progress,
        string computerName,
        int expectedPolicies,
        string line)
    {
        if (progress is null || string.IsNullOrWhiteSpace(line))
            return;

        // "OK progress 50/200" or "OK progress 50/200 (GC)"
        const string prefix = "OK progress ";
        var trimmed = line.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return;

        var rest = trimmed[prefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            return;

        var left = rest[..slash].Trim();
        var rightPart = rest[(slash + 1)..];
        var space = rightPart.IndexOf(' ');
        var right = (space < 0 ? rightPart : rightPart[..space]).Trim();
        if (!int.TryParse(left, out var current) || !int.TryParse(right, out var total))
            return;

        if (total <= 0)
            total = expectedPolicies > 0 ? expectedPolicies : current;
        progress.Report(SyncProgressUpdate.ApplyProgress(computerName, current, total));
    }

    public async Task<IReadOnlyList<string>> ListPolicyNamesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default)
    {
        var policies = await ListPoliciesAsync(computerName, policyNamePrefix, cancellationToken)
            .ConfigureAwait(false);
        return policies.Select(p => p.Name).ToList();
    }

    public async Task<IReadOnlyList<DnsPolicyInfo>> ListPoliciesAsync(
        string computerName,
        string policyNamePrefix,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var isLocal = LocalDnsTarget.IsLocalTarget(computerName);
        var localLiteral = isLocal ? "$true" : "$false";

        // Probe must NOT hydrate Criteria for all 2000+ policies (WinRM/PS 5.1 → empty / false zero).
        // Count via lightweight Name/IsEnabled rows + sentinel/registry fallbacks; Criteria only for sample.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module DnsServer -ErrorAction Stop
            $computer = '{{EscapePsString(computerName)}}'
            $prefix = '{{EscapePsString(policyNamePrefix)}}'
            $local = {{localLiteral}}
            {{BuildDnsBlockerInventoryHelpersPs()}}
            $inv = $null
            for ($attempt = 1; $attempt -le 5; $attempt++) {
              $inv = Get-DnsBlockerPolicyInventory -Computer $computer -Prefix $prefix -Local $local
              if ([int]$inv.installed -gt 0) { break }
              Start-Sleep -Seconds 1
            }
            Write-Output ("INVENTORY installed=$([int]$inv.installed) enabled=$([int]$inv.enabled) emptyFqdn=$([int]$inv.emptyFqdn) source=$($inv.source) listed=$([int]$inv.listed) sentinel=$([int][bool]$inv.sentinel)")
            if (@($inv.sample).Count -eq 0) {
              Write-Output '[]'
            } else {
              Write-Output (ConvertTo-Json -InputObject @($inv.sample) -Compress -Depth 4)
            }
            """;

        var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return ParsePolicyInventory(output);
    }

    /// <summary>
    /// Shared PowerShell helpers for QRP inventory/count. Lightweight Name/IsEnabled listing first;
    /// Criteria is only read for a small sample. Falls back to -Name wildcard, sentinel D5 scan,
    /// and local registry when Get|*Name -like returns 0 while policies still exist (Vincent 1.0.31).
    /// </summary>
    internal static string BuildDnsBlockerInventoryHelpersPs() => """
            function Get-DnsBlockerPolicyName($policy) {
              if ($null -eq $policy) { return '' }
              if ($policy.PSObject.Properties['Name'] -and $policy.Name) { return [string]$policy.Name }
              if ($policy.PSObject.Properties['PolicyName'] -and $policy.PolicyName) { return [string]$policy.PolicyName }
              return ''
            }
            function Test-DnsBlockerNameMatch([string]$Name, [string]$Prefix) {
              if ([string]::IsNullOrEmpty($Name) -or [string]::IsNullOrEmpty($Prefix)) { return $false }
              if ($Name.StartsWith($Prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
              return ($Name -like ($Prefix + '*'))
            }
            function Get-DnsBlockerFqdnLen($policy) {
              if ($null -eq $policy) { return 0 }
              $n = 0
              if ($policy.PSObject.Properties['Fqdn'] -and $policy.Fqdn) {
                $n = [Math]::Max($n, ([string]$policy.Fqdn).Length)
              }
              foreach ($c in @($policy.Criteria)) {
                if ($null -eq $c) { continue }
                if ($c.PSObject.Properties['Criteria'] -and $c.Criteria -is [string]) {
                  $n = [Math]::Max($n, ([string]$c.Criteria).Length)
                } elseif ($c.PSObject.Properties['Criterion'] -and $c.Criterion) {
                  $n = [Math]::Max($n, ([string]$c.Criterion).Length)
                }
              }
              return $n
            }
            function Get-DnsBlockerIsEnabled($policy) {
              $isEn = $true
              if ($null -ne $policy.IsEnabled) { $isEn = [bool]$policy.IsEnabled }
              elseif ($null -ne $policy.Enabled) { $isEn = [bool]$policy.Enabled }
              elseif ($null -ne $policy.IsDisabled) { $isEn = -not [bool]$policy.IsDisabled }
              return $isEn
            }
            function Get-DnsBlockerPolicyInventory([string]$Computer, [string]$Prefix, [bool]$Local) {
              $rows = New-Object System.Collections.Generic.List[object]
              $listed = 0
              $source = 'list'
              $sentinel = $false
              $regCount = -1
              try {
                if ($Local) { $all = @(Get-DnsServerQueryResolutionPolicy -ErrorAction Stop) }
                else { $all = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -ErrorAction Stop) }
                $listed = @($all).Count
                foreach ($p in $all) {
                  $n = Get-DnsBlockerPolicyName $p
                  if (-not (Test-DnsBlockerNameMatch $n $Prefix)) { continue }
                  [void]$rows.Add([pscustomobject]@{ Name = $n; IsEnabled = (Get-DnsBlockerIsEnabled $p); Action = [string]$p.Action })
                }
              } catch {
                $listed = -1
                $source = 'list-error'
              }
              if ($rows.Count -eq 0) {
                try {
                  if ($Local) { $byName = @(Get-DnsServerQueryResolutionPolicy -Name ($Prefix + '*') -ErrorAction SilentlyContinue) }
                  else { $byName = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name ($Prefix + '*') -ErrorAction SilentlyContinue) }
                  foreach ($p in @($byName)) {
                    if ($null -eq $p) { continue }
                    $n = Get-DnsBlockerPolicyName $p
                    if (-not $n) { continue }
                    if (-not (Test-DnsBlockerNameMatch $n $Prefix)) { continue }
                    [void]$rows.Add([pscustomobject]@{ Name = $n; IsEnabled = (Get-DnsBlockerIsEnabled $p); Action = [string]$p.Action })
                  }
                  if ($rows.Count -gt 0) { $source = 'name-wildcard' }
                } catch { }
              }
              $sentinelName = $Prefix + '00001'
              $sentinelObj = $null
              try {
                if ($Local) { $sentinelObj = Get-DnsServerQueryResolutionPolicy -Name $sentinelName -ErrorAction SilentlyContinue }
                else { $sentinelObj = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $sentinelName -ErrorAction SilentlyContinue }
              } catch { $sentinelObj = $null }
              $sentinel = ($null -ne $sentinelObj)
              if ($rows.Count -eq 0 -and $sentinel) {
                $source = 'sentinel-scan'
                $miss = 0
                for ($i = 1; $i -le 100000 -and $miss -lt 25; $i++) {
                  $name = $Prefix + $i.ToString('00000')
                  $p = $null
                  try {
                    if ($Local) { $p = Get-DnsServerQueryResolutionPolicy -Name $name -ErrorAction SilentlyContinue }
                    else { $p = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $name -ErrorAction SilentlyContinue }
                  } catch { $p = $null }
                  if ($null -eq $p) { $miss++; continue }
                  $miss = 0
                  [void]$rows.Add([pscustomobject]@{ Name = $name; IsEnabled = (Get-DnsBlockerIsEnabled $p); Action = [string]$p.Action })
                }
              }
              if ($Local) {
                $regPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server\Policies'
                if (Test-Path -LiteralPath $regPath) {
                  $regCount = @(Get-ChildItem -LiteralPath $regPath -ErrorAction SilentlyContinue |
                    Where-Object { $_.PSChildName -like ($Prefix + '*') }).Count
                  if ($rows.Count -eq 0 -and $regCount -gt 0) {
                    $source = 'registry'
                    for ($i = 0; $i -lt $regCount; $i++) {
                      [void]$rows.Add([pscustomobject]@{
                        Name = ($Prefix + ($i + 1).ToString('00000'))
                        IsEnabled = $true
                        Action = ''
                      })
                    }
                  }
                }
              }
              $enabled = 0
              foreach ($r in $rows) { if ($r.IsEnabled) { $enabled++ } }
              $sample = New-Object System.Collections.Generic.List[object]
              $emptyFqdn = 0
              foreach ($r in $rows) {
                if ($sample.Count -ge 40) { break }
                $fqdnLen = 1
                try {
                  if ($Local) { $full = Get-DnsServerQueryResolutionPolicy -Name $r.Name -ErrorAction Stop }
                  else { $full = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $r.Name -ErrorAction Stop }
                  $fqdnLen = [int](Get-DnsBlockerFqdnLen $full)
                } catch { $fqdnLen = 0 }
                if ($fqdnLen -le 0) { $emptyFqdn++ }
                [void]$sample.Add([pscustomobject]@{
                  Name = [string]$r.Name
                  IsEnabled = [bool]$r.IsEnabled
                  Action = [string]$r.Action
                  FqdnCriterionLength = $fqdnLen
                })
              }
              [pscustomobject]@{
                installed = [int]$rows.Count
                enabled = [int]$enabled
                emptyFqdn = [int]$emptyFqdn
                sample = @($sample.ToArray())
                source = [string]$source
                listed = [int]$listed
                sentinel = [bool]$sentinel
                regCount = [int]$regCount
              }
            }
            """;

    /// <summary>Policy name DnsBlocker_00001 (D5 — supports &gt;9999 chunks after FQDN budget split).</summary>
    public static string FormatPolicyName(string policyNamePrefix, int index1Based) =>
        $"{policyNamePrefix}{index1Based:D5}";

    /// <summary>
    /// Builds the apply script for unit tests (inline chunks, same semantics as manifest runner).
    /// </summary>
    public static string BuildApplyScript(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        bool restartDnsAfterApply = true,
        int applyThrottleMs = 15,
        int applyGcEvery = 25)
    {
        var normalized = NormalizeChunks(chunks, MaxFqdnCriterionChars);
        var manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"DnsBlocker-qrp-test-{Guid.NewGuid():N}.ndjson");
        WriteManifest(
            manifestPath,
            computerName,
            policyNamePrefix,
            action,
            normalized,
            restartDnsAfterApply,
            Math.Max(0, applyThrottleMs),
            Math.Max(0, applyGcEvery));
        // Embed path; tests assert script shape. Caller may delete leftover test manifests.
        return BuildApplyScriptFromManifest(manifestPath) +
               Environment.NewLine +
               $"# manifest={manifestPath}" +
               Environment.NewLine +
               BuildInlineApplyAssertionsHint(computerName, policyNamePrefix, action, normalized, restartDnsAfterApply);
    }

    /// <summary>Test helper: asserts that would appear for an inline-shaped apply (ProcessingOrder, Restart, verify).</summary>
    public static string BuildInlineApplyAssertionsHint(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        bool restartDnsAfterApply)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# expected action={action} computer={computerName} prefix={policyNamePrefix} restart={restartDnsAfterApply}");
        sb.AppendLine("# Add-DnsServerQueryResolutionPolicy -ProcessingOrder");
        sb.AppendLine("# Clear-DnsServerCache");
        if (restartDnsAfterApply)
            sb.AppendLine("# Restart-Service DNS / Win32_Service DNS");
        sb.AppendLine("# VERIFY enabled=");
        for (var i = 0; i < Math.Min(chunks.Count, 3); i++)
            sb.AppendLine($"# policy {FormatPolicyName(policyNamePrefix, i + 1)} Fqdn EQ,{string.Join(",", chunks[i].Take(2))}...");
        return sb.ToString();
    }

    public static IReadOnlyList<IReadOnlyList<string>> NormalizeChunks(
        IReadOnlyList<IReadOnlyList<string>> chunks,
        int maxFqdnCriterionChars)
    {
        var flat = new List<string>();
        foreach (var chunk in chunks)
        {
            foreach (var domain in chunk)
            {
                if (string.IsNullOrWhiteSpace(domain))
                    continue;
                if (domain.Contains(',', StringComparison.Ordinal))
                    continue;
                // Re-normalize sinkhole pollution if older in-memory sets still carry it.
                if (!Ingest.DomainListParser.TryNormalizeDomain(domain, out var normalized))
                    continue;
                flat.Add(normalized);
            }
        }

        return Ingest.DomainListParser.ChunkByFqdnBudget(flat, maxFqdnCriterionChars);
    }

    /// <summary>
    /// Pass through pre-budgeted chunks from SyncEngine; only rebuild when a criterion exceeds the budget.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> EnsureFqdnBudget(
        IReadOnlyList<IReadOnlyList<string>> chunks,
        int maxFqdnCriterionChars)
    {
        if (chunks.Count == 0)
            return chunks;

        foreach (var chunk in chunks)
        {
            var len = 3; // EQ,
            for (var i = 0; i < chunk.Count; i++)
            {
                var d = chunk[i];
                if (string.IsNullOrWhiteSpace(d) || d.Contains(',', StringComparison.Ordinal))
                    return NormalizeChunks(chunks, maxFqdnCriterionChars);
                len += (i == 0 ? 0 : 1) + d.Length;
                if (len > maxFqdnCriterionChars)
                    return NormalizeChunks(chunks, maxFqdnCriterionChars);
            }
        }

        return chunks;
    }

    public static string EnrichDnsPolicyError(string raw, string computerName)
    {
        var text = raw?.Trim() ?? "";
        var match = Win32CodeRegex.Match(text);
        if (match.Success && match.Groups[1].Value == "1011")
        {
            return
                $"WIN32 1011 on '{computerName}': DNS policy registry key could not be opened. " +
                "DnsAdmins alone is usually not enough for Query Resolution Policies. " +
                "On the DNS server, grant the hub account (or DnsAdmins) Full Control on " +
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server " +
                "(and subkeys), or run the hub service as a member of the server's local Administrators / Domain Admins. " +
                "Also verify WinRM, DnsServer module, and that no stale DnsBlocker_* policy is stuck mid-create. " +
                "Original: " + text;
        }

        if (match.Success && match.Groups[1].Value == "9971"
            || text.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || text.Contains("DNS_ERROR_POLICY_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase))
        {
            return
                $"WIN32 9971 on '{computerName}': a DnsBlocker_* QRP policy already exists. " +
                "Sync should remove all prefixed policies then recreate; if this persists, " +
                "manually Remove-DnsServerQueryResolutionPolicy for leftover DnsBlocker_* names and retry. " +
                "Original: " + text;
        }

        if (text.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WIN32 5", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(text, @"\bAccessDenied\b", RegexOptions.IgnoreCase))
        {
            return
                $"Access denied talking to DNS on '{computerName}'. " +
                "Add the hub service account to DnsAdmins (and the registry ACL above for policies), " +
                "enable WinRM, and allow CIM/WinRM from the hub. Original: " + text;
        }

        if (text.Contains("SMOKE_FAIL", StringComparison.OrdinalIgnoreCase)
            || text.Contains("FQDN criteria empty", StringComparison.OrdinalIgnoreCase)
            || text.Contains("enabled count", StringComparison.OrdinalIgnoreCase))
        {
            return
                $"QRP apply verification failed on '{computerName}': policies were pushed but blocking is not effective. " +
                "Check Enabled count, non-empty FQDN criteria, ProcessingOrder, and that DNS was restarted. Original: " + text;
        }

        return string.IsNullOrEmpty(text) ? $"DNS policy operation failed on '{computerName}'." : text;
    }

    public static IReadOnlyList<DnsPolicyInfo> ParsePolicyInventory(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        // Prefer INVENTORY installed=N enabled=M emptyFqdn=E (authoritative counts; JSON is sample-only).
        var inv = Regex.Match(
            output,
            @"INVENTORY\s+installed=(\d+)\s+enabled=(\d+)\s+emptyFqdn=(\d+)",
            RegexOptions.IgnoreCase);
        if (inv.Success)
        {
            var installed = int.Parse(inv.Groups[1].Value);
            var enabled = int.Parse(inv.Groups[2].Value);
            var emptyFqdn = int.Parse(inv.Groups[3].Value);
            var sample = ParsePolicyInventoryJson(output);
            return ExpandInventoryCounts(installed, enabled, emptyFqdn, sample);
        }

        return ParsePolicyInventoryJson(output);
    }

    /// <summary>
    /// Builds a list whose Count/IsEnabled/FqdnCriterionLength aggregates match the INVENTORY line,
    /// preserving up to <paramref name="sample"/> names for the UI.
    /// </summary>
    internal static IReadOnlyList<DnsPolicyInfo> ExpandInventoryCounts(
        int installed,
        int enabled,
        int emptyFqdn,
        IReadOnlyList<DnsPolicyInfo> sample)
    {
        if (installed < 0)
            installed = 0;
        enabled = Math.Clamp(enabled, 0, installed);
        emptyFqdn = Math.Clamp(emptyFqdn, 0, installed);

        var list = new List<DnsPolicyInfo>(installed);
        for (var i = 0; i < installed; i++)
        {
            var name = i < sample.Count && !string.IsNullOrWhiteSpace(sample[i].Name)
                ? sample[i].Name
                : $"DnsBlocker_{(i + 1):D5}";
            list.Add(new DnsPolicyInfo
            {
                Name = name,
                IsEnabled = i < enabled,
                Action = i < sample.Count ? sample[i].Action : null,
                // First emptyFqdn entries report length 0 so Count(p => length<=0) matches.
                FqdnCriterionLength = i < emptyFqdn ? 0 : Math.Max(1, i < sample.Count ? sample[i].FqdnCriterionLength : 8)
            });
        }

        return list;
    }

    private static IReadOnlyList<DnsPolicyInfo> ParsePolicyInventoryJson(string output)
    {
        output = output.Trim();
        var jsonStart = output.LastIndexOf('[');
        var objStart = output.LastIndexOf('{');
        if (jsonStart < 0 && objStart < 0)
            return [];

        if (jsonStart >= 0 && (objStart < 0 || jsonStart < objStart))
            output = output[jsonStart..];
        else if (objStart >= 0)
            output = output[objStart..];

        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        try
        {
            if (output.StartsWith('['))
                return JsonSerializer.Deserialize<List<DnsPolicyInfo>>(output, opts) ?? [];

            var single = JsonSerializer.Deserialize<DnsPolicyInfo>(output, opts);
            return single is null ? [] : [single];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void ValidateApplyOutput(string output, string computerName, int expectedPolicies)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"Empty PowerShell output after QRP apply on '{computerName}'.");

        // Prefer a VERIFY line whose installed= matches expected (ignore stale FAIL lines if a later recount succeeded).
        var verifyLines = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("VERIFY ", StringComparison.OrdinalIgnoreCase))
            .ToList();

        string? verifyLine = null;
        foreach (var line in verifyLines.AsEnumerable().Reverse())
        {
            try
            {
                if (ReadVerifyInt(line, "installed") == expectedPolicies
                    && ReadVerifyInt(line, "enabled") == expectedPolicies)
                {
                    verifyLine = line;
                    break;
                }
            }
            catch (InvalidOperationException)
            {
                // incomplete VERIFY line (hashtable unwrap) — keep scanning
            }
        }

        verifyLine ??= verifyLines.LastOrDefault();

        if (verifyLine is null)
            throw new InvalidOperationException(
                $"QRP apply on '{computerName}' did not emit VERIFY line (expected {expectedPolicies} policies). Output: {TrimForError(output)}");

        // VERIFY installed=N enabled=M expected=E smoke=OK|SKIP|FAIL
        var installed = ReadVerifyInt(verifyLine, "installed");
        var enabled = ReadVerifyInt(verifyLine, "enabled");
        var expected = ReadVerifyInt(verifyLine, "expected");

        if (expected != expectedPolicies)
            throw new InvalidOperationException(
                $"VERIFY expected mismatch on '{computerName}': script expected={expected}, hub expected={expectedPolicies}. {verifyLine}");

        // Defense: smoke=OK with installed=0 must never mean Succeeded (Vincent 1.0.31 windc04).
        if (expectedPolicies > 0 && installed <= 0)
            throw new InvalidOperationException(
                $"QRP live count mismatch on '{computerName}': installed={installed}, enabled={enabled}, expected={expectedPolicies}. " +
                "Succeeded push without Enabled policies will not block. " + verifyLine);

        if (installed != expectedPolicies || enabled != expectedPolicies)
            throw new InvalidOperationException(
                $"QRP live count mismatch on '{computerName}': installed={installed}, enabled={enabled}, expected={expectedPolicies}. " +
                "Succeeded push without Enabled policies will not block. " + verifyLine);

        if (verifyLine.Contains("smoke=FAIL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"QRP smoke test failed on '{computerName}': sample domain still resolves after policy apply. {verifyLine}");

        if (verifyLine.Contains("smoke=OK", StringComparison.OrdinalIgnoreCase)
            && (installed <= 0 || enabled <= 0))
        {
            throw new InvalidOperationException(
                $"QRP VERIFY refused on '{computerName}': smoke=OK is invalid when installed/enabled is 0. {verifyLine}");
        }

        if (expectedPolicies > 0
            && verifyLine.Contains("smoke=SKIP", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"QRP smoke was SKIP on '{computerName}' with {expectedPolicies} policies — refusing Succeeded without Resolve-DnsName proof. {verifyLine}");
        }
    }

    private static int ReadVerifyInt(string line, string key)
    {
        var m = Regex.Match(line, key + @"=(\d+)", RegexOptions.IgnoreCase);
        if (!m.Success)
            throw new InvalidOperationException($"VERIFY line missing {key}=: {line}");
        return int.Parse(m.Groups[1].Value);
    }

    private static string TrimForError(string output)
    {
        var t = output.Trim();
        return t.Length <= 800 ? t : t[..800] + "…";
    }

    /// <summary>
    /// Tiny ProcessingOrder=1 policy: <c>EQ,apex,*.apex</c> (Microsoft blackhole shape).
    /// Guarantees the smoke domain is not buried in a multi-thousand-domain criterion.
    /// </summary>
    public static string? BuildSentinelFqdn(IReadOnlyList<IReadOnlyList<string>> chunks)
    {
        var smoke = PreferSmokeDomain(chunks);
        if (string.IsNullOrWhiteSpace(smoke))
            return null;
        // MS-DNSP: EQ,*.apex matches apex + children; we still pin bare apex for builds that differ.
        return $"EQ,{smoke},*.{smoke}";
    }

    public static int CountExpectedPolicies(IReadOnlyList<IReadOnlyList<string>> chunks) =>
        chunks.Count + (BuildSentinelFqdn(chunks) is null ? 0 : 1);

    /// <summary>
    /// NDJSON manifest: line 1 = header (no policies array), then one policy object per line.
    /// PowerShell streams line-by-line so ConvertFrom-Json never holds the full multi-MB graph.
    /// </summary>
    private static void WriteManifest(
        string path,
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        bool restartDnsAfterApply,
        int applyThrottleMs = 15,
        int applyGcEvery = 25)
    {
        var isLocal = LocalDnsTarget.IsLocalTarget(computerName);
        // Prefer a domain that should resolve on the public Internet when unblocked (Vincent repro).
        // One scan only — BuildSentinelFqdn would otherwise re-walk every chunk.
        string? smokeDomain = PreferSmokeDomain(chunks);
        var sentinelFqdn = smokeDomain is null ? null : $"EQ,{smokeDomain},*.{smokeDomain}";
        var expected = chunks.Count + (sentinelFqdn is null ? 0 : 1);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { SkipValidation = true });

        // Header line
        writer.WriteStartObject();
        writer.WriteString("computer", computerName);
        writer.WriteBoolean("local", isLocal);
        writer.WriteString("prefix", policyNamePrefix);
        writer.WriteString("action", action);
        writer.WriteBoolean("restartDns", restartDnsAfterApply);
        if (smokeDomain is null)
            writer.WriteNull("smokeDomain");
        else
            writer.WriteString("smokeDomain", smokeDomain);
        writer.WriteNumber("expected", expected);
        writer.WriteNumber("throttleMs", applyThrottleMs);
        writer.WriteNumber("gcEvery", applyGcEvery);
        writer.WriteEndObject();
        writer.Flush();
        fs.WriteByte((byte)'\n');

        var order = 1;
        if (sentinelFqdn is not null)
        {
            WriteManifestPolicyLine(fs, writer, FormatPolicyName(policyNamePrefix, order), sentinelFqdn, order);
            order++;
        }

        for (var i = 0; i < chunks.Count; i++)
        {
            // Build FQDN criterion without retaining all Join strings at once — one line, then discard.
            WriteManifestPolicyLine(
                fs,
                writer,
                FormatPolicyName(policyNamePrefix, order),
                "EQ," + string.Join(",", chunks[i]),
                order);
            order++;

            // Periodic LOH trim while streaming large manifests (hub RSS during 100k+ policies).
            if (applyGcEvery > 0 && (i + 1) % Math.Max(applyGcEvery * 4, 100) == 0)
                Diagnostics.ProcessMemory.TrimAfterLargeDrop();
        }
    }

    private static void WriteManifestPolicyLine(
        FileStream fs,
        Utf8JsonWriter writer,
        string name,
        string fqdn,
        int order)
    {
        writer.Reset();
        writer.WriteStartObject();
        writer.WriteString("name", name);
        writer.WriteString("fqdn", fqdn);
        writer.WriteNumber("order", order);
        writer.WriteEndObject();
        writer.Flush();
        fs.WriteByte((byte)'\n');
    }

    /// <summary>
    /// Single-session runner: remove prefix* → Add with ProcessingOrder + PassThru criteria check →
    /// Clear-DnsServerCache → optional DNS service restart → recount Enabled → smoke Resolve-DnsName.
    /// Server-level QRP (no -ApplyOnRecursion): IGNORE drops; DENY → REFUSED.
    /// </summary>
    public static string BuildApplyScriptFromManifest(string manifestPath)
    {
        var pathLiteral = EscapePsString(manifestPath);
        return $$"""
            $ErrorActionPreference = 'Stop'
            function Format-DnsBlockerError([System.Management.Automation.ErrorRecord]$err) {
              $parts = New-Object System.Collections.Generic.List[string]
              [void]$parts.Add($err.Exception.Message)
              $ex = $err.Exception
              while ($null -ne $ex.InnerException) {
                $ex = $ex.InnerException
                [void]$parts.Add(('Inner: ' + $ex.Message))
              }
              if ($err.Exception -is [Microsoft.Management.Infrastructure.CimException]) {
                $cim = [Microsoft.Management.Infrastructure.CimException]$err.Exception
                [void]$parts.Add(('CimStatus=' + $cim.StatusCode))
                if ($cim.NativeErrorCode -ne 0) {
                  [void]$parts.Add(('WIN32 ' + [int]$cim.NativeErrorCode))
                }
              }
              if ($err.FullyQualifiedErrorId) {
                [void]$parts.Add(('FQID=' + $err.FullyQualifiedErrorId))
              }
              if ($err.ErrorDetails -and $err.ErrorDetails.Message) {
                [void]$parts.Add(('Details: ' + $err.ErrorDetails.Message))
              }
              return ($parts -join ' | ')
            }

            function Test-DnsBlockerPolicyAlreadyExists([System.Management.Automation.ErrorRecord]$err) {
              $native = 0
              if ($err.Exception -is [Microsoft.Management.Infrastructure.CimException]) {
                $native = [int]([Microsoft.Management.Infrastructure.CimException]$err.Exception).NativeErrorCode
              }
              $blob = ($err.Exception.Message + '|' + $err.FullyQualifiedErrorId + '|WIN32 ' + $native)
              if ($native -eq 9971) { return $true }
              if ($blob -match 'WIN32\s*9971') { return $true }
              if ($blob -match 'already exists') { return $true }
              if ($blob -match 'DNS_ERROR_POLICY_ALREADY_EXISTS') { return $true }
              if ($blob -match 'object already exists') { return $true }
              return $false
            }

            function Get-DnsBlockerFqdnCriterionText($policy) {
              # MS Get-DnsServerQueryResolutionPolicy: $Policy.Criteria → DnsServerPolicyCriteria
              # with CriteriaType=Fqdn and Criteria='EQ,...' (property name is Criteria, not Criterion).
              if ($null -eq $policy) { return '' }
              if ($policy.PSObject.Properties['Fqdn'] -and $policy.Fqdn) {
                $f = [string]$policy.Fqdn
                if ($f -match '(?i)EQ,') { return $f }
              }
              $parts = New-Object System.Collections.Generic.List[string]
              $bags = New-Object System.Collections.Generic.List[object]
              if ($null -ne $policy.Criteria) {
                foreach ($c in @($policy.Criteria)) { [void]$bags.Add($c) }
              }
              if ($null -ne $policy.Content) {
                foreach ($contentItem in @($policy.Content)) {
                  if ($null -ne $contentItem -and $contentItem.PSObject.Properties['Criteria'] -and $contentItem.Criteria) {
                    foreach ($c in @($contentItem.Criteria)) { [void]$bags.Add($c) }
                  }
                }
              }
              foreach ($c in $bags) {
                if ($null -eq $c) { continue }
                $type = ''
                if ($c.PSObject.Properties['CriteriaType'] -and $c.CriteriaType) { $type = [string]$c.CriteriaType }
                elseif ($c.PSObject.Properties['Type'] -and $c.Type) { $type = [string]$c.Type }
                $text = ''
                # Prefer MS property name Criteria (string), then Criterion / Content fallbacks.
                if ($c.PSObject.Properties['Criteria'] -and $c.Criteria -and $c.Criteria -is [string]) {
                  $text = [string]$c.Criteria
                } elseif ($c.PSObject.Properties['Criterion'] -and $c.Criterion) {
                  $text = [string]$c.Criterion
                } elseif ($c.PSObject.Properties['Content'] -and $c.Content -and $c.Content -is [string]) {
                  $text = [string]$c.Content
                }
                if ($text.Length -eq 0) { continue }
                if ($type -match '(?i)Fqdn' -or $text -match '(?i)EQ,') {
                  [void]$parts.Add($text)
                }
              }
              return (($parts | Select-Object -Unique) -join '|')
            }

            function Test-DnsBlockerHasFqdnCriteria($policy) {
              $text = Get-DnsBlockerFqdnCriterionText $policy
              return ($text -match '(?i)EQ,.+' -and $text.Length -gt 4)
            }

            function Remove-DnsBlockerPolicies([string]$Computer, [string]$Prefix, [bool]$Local) {
              # Full replace: wipe EVERY Name -like 'DnsBlocker_*' (D4 DnsBlocker_0001, D5 DnsBlocker_00001,
              # DnsBlocker_Probe_Beeg, etc.) before recreating with IGNORE + short criteria.
              $existing = @()
              try {
                if ($Local) {
                  $existing = @(Get-DnsServerQueryResolutionPolicy -ErrorAction Stop |
                    Where-Object { $_.Name -like ($Prefix + '*') } |
                    Sort-Object ProcessingOrder -Descending)
                } else {
                  $existing = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -ErrorAction Stop |
                    Where-Object { $_.Name -like ($Prefix + '*') } |
                    Sort-Object ProcessingOrder -Descending)
                }
              } catch {
                $existing = @()
              }
              $removed = 0
              foreach ($p in $existing) {
                try {
                  if ($Local) {
                    Remove-DnsServerQueryResolutionPolicy -Name $p.Name -Force -ErrorAction Stop
                  } else {
                    Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $p.Name -Force -ErrorAction Stop
                  }
                  $removed++
                } catch {
                  Start-Sleep -Milliseconds 400
                  if ($Local) {
                    Remove-DnsServerQueryResolutionPolicy -Name $p.Name -Force -ErrorAction Stop
                  } else {
                    Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $p.Name -Force -ErrorAction Stop
                  }
                  $removed++
                }
              }
              Write-Output ("REMOVED count=$removed prefix=$Prefix*")

              # Must be empty before Add — leftover Deny/inert policies would keep ProcessingOrder noise.
              if ($Local) {
                $left = @(Get-DnsServerQueryResolutionPolicy -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -like ($Prefix + '*') })
              } else {
                $left = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -ErrorAction SilentlyContinue |
                  Where-Object { $_.Name -like ($Prefix + '*') })
              }
              if ($left.Count -gt 0) {
                $names = ($left | Select-Object -ExpandProperty Name) -join ','
                throw ("Remove incomplete: " + $left.Count + " " + $Prefix + "* policies remain (" + $names + "). Refusing Add.")
              }
              Write-Output 'REMOVED verify=0'
            }

            function Add-DnsBlockerPolicy([string]$Computer, [string]$Name, [string]$Action, [string]$Fqdn, [uint32]$Order, [bool]$Local) {
              # Server-level QueryProcessing (no -ApplyOnRecursion): IGNORE = drop (MS blackhole); DENY = REFUSED.
              $created = $null
              try {
                if ($Local) {
                  $created = Add-DnsServerQueryResolutionPolicy -Name $Name -Action $Action -Fqdn $Fqdn -ProcessingOrder $Order -PassThru -ErrorAction Stop
                } else {
                  $created = Add-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Action $Action -Fqdn $Fqdn -ProcessingOrder $Order -PassThru -ErrorAction Stop
                }
              } catch {
                if (Test-DnsBlockerPolicyAlreadyExists $_) {
                  if ($Local) {
                    Remove-DnsServerQueryResolutionPolicy -Name $Name -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 300
                    $created = Add-DnsServerQueryResolutionPolicy -Name $Name -Action $Action -Fqdn $Fqdn -ProcessingOrder $Order -PassThru -ErrorAction Stop
                  } else {
                    Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Milliseconds 300
                    $created = Add-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Action $Action -Fqdn $Fqdn -ProcessingOrder $Order -PassThru -ErrorAction Stop
                  }
                } else {
                  throw
                }
              }

              if ($null -ne $created.IsEnabled -and -not [bool]$created.IsEnabled) {
                if ($Local) {
                  Enable-DnsServerQueryResolutionPolicy -Name $Name -ErrorAction Stop
                } else {
                  Enable-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -ErrorAction Stop
                }
              }

              # Always re-Get after Add (PassThru can omit nested Criteria). Require non-empty EQ,... text.
              if ($Local) {
                $created = Get-DnsServerQueryResolutionPolicy -Name $Name -ErrorAction Stop
              } else {
                $created = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -ErrorAction Stop
              }
              $critText = Get-DnsBlockerFqdnCriterionText $created
              if (-not ($critText -match '(?i)EQ,.+') -or $critText.Length -le 4) {
                $dump = ($created | Format-List * | Out-String)
                throw ("FQDN criteria empty on policy " + $Name + " (len=" + $critText.Length + ") — Add succeeded but QRP will not block. Dump:`n" + $dump)
              }
              # First / sentinel policy: emit readback for journal (Vincent: Get ... | fl *)
              if ($Order -eq 1) {
                Write-Output ("CRITERIA_READBACK name=" + $Name + " action=" + [string]$created.Action + " enabled=" + [bool]$created.IsEnabled + " fqdnLen=" + $critText.Length + " fqdnPreview=" + $critText.Substring(0, [Math]::Min(120, $critText.Length)))
              }
            }

            function Find-DnsBlockerSmokeInCriteria([string]$Computer, [string]$Prefix, [bool]$Local, [string]$SmokeDomain) {
              if (-not $SmokeDomain) { return $null }
              # Prefer sentinel + name scan over bulk Get|Where (Criteria payload / filter false-zero).
              $names = New-Object System.Collections.Generic.List[string]
              $inv = Get-DnsBlockerPolicyInventory -Computer $Computer -Prefix $Prefix -Local $Local
              foreach ($r in @($inv.sample)) { if ($r.Name) { [void]$names.Add([string]$r.Name) } }
              if ($names.Count -eq 0 -and [int]$inv.installed -gt 0) {
                for ($i = 1; $i -le [Math]::Min([int]$inv.installed, 5); $i++) {
                  [void]$names.Add($Prefix + $i.ToString('00000'))
                }
              }
              if ($names.Count -eq 0) { [void]$names.Add($Prefix + '00001') }
              $apex = $SmokeDomain.Trim().Trim('.').ToLowerInvariant()
              $wild = '*.' + $apex
              foreach ($name in $names) {
                try {
                  if ($Local) { $p = Get-DnsServerQueryResolutionPolicy -Name $name -ErrorAction Stop }
                  else { $p = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $name -ErrorAction Stop }
                } catch { continue }
                $text = (Get-DnsBlockerFqdnCriterionText $p).ToLowerInvariant()
                if ($text.Length -eq 0) { continue }
                if ($text -match ('(?i)(^|[,|])' + [regex]::Escape($apex) + '([,|]|$)') -or
                    $text -match ('(?i)(^|[,|])' + [regex]::Escape($wild) + '([,|]|$)')) {
                  return $name
                }
              }
              return $null
            }

            function Restart-DnsBlockerDnsServer([string]$Computer, [bool]$Local) {
              if ($Local) {
                Restart-Service -Name 'DNS' -Force -ErrorAction Stop
                $svc = Get-Service -Name 'DNS' -ErrorAction Stop
                $deadline = (Get-Date).AddSeconds(60)
                while ($svc.Status -ne 'Running' -and (Get-Date) -lt $deadline) {
                  Start-Sleep -Seconds 1
                  $svc.Refresh()
                }
                if ($svc.Status -ne 'Running') { throw 'Local DNS service did not return to Running after restart.' }
                return
              }

              $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DNS'" -ComputerName $Computer -ErrorAction Stop
              $null = Invoke-CimMethod -InputObject $svc -MethodName StopService -ErrorAction SilentlyContinue
              Start-Sleep -Seconds 2
              $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DNS'" -ComputerName $Computer -ErrorAction Stop
              $null = Invoke-CimMethod -InputObject $svc -MethodName StartService -ErrorAction Stop
              $deadline = (Get-Date).AddSeconds(90)
              do {
                Start-Sleep -Seconds 1
                $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DNS'" -ComputerName $Computer -ErrorAction Stop
              } while ($svc.State -ne 'Running' -and (Get-Date) -lt $deadline)
              if ($svc.State -ne 'Running') {
                throw ("DNS service did not return to Running on " + $Computer + " (state=" + $svc.State + ")")
              }
            }

            function Get-DnsBlockerLiveCounts([string]$Computer, [string]$Prefix, [bool]$Local) {
              # MUST return [pscustomobject], never a Hashtable: `return @{...}` is enumerated into
              # DictionaryEntry[] on the pipeline, so `$counts.installed` becomes $null and VERIFY
              # prints `installed= enabled=` (Vincent windc04 1.0.28: smoke=OK but C# rejects the line).
              # Use inventory helper (lightweight + sentinel/registry fallbacks) — same path as post-sync probe.
              $inv = Get-DnsBlockerPolicyInventory -Computer $Computer -Prefix $Prefix -Local $Local
              [pscustomobject]@{
                installed = [int]$inv.installed
                enabled = [int]$inv.enabled
                source = [string]$inv.source
                listed = [int]$inv.listed
                sentinel = [bool]$inv.sentinel
              }
            }

            {{BuildDnsBlockerInventoryHelpersPs()}}
            Import-Module DnsServer -ErrorAction Stop
            $manifestPath = '{{pathLiteral}}'
            if (-not (Test-Path -LiteralPath $manifestPath)) {
              Write-Error ("Manifest not found: " + $manifestPath)
              exit 1
            }
            # NDJSON: line 1 = header, remaining lines = one policy each (stream — no full JSON graph).
            $reader = [System.IO.StreamReader]::new($manifestPath, [System.Text.Encoding]::UTF8)
            try {
              $headerLine = $reader.ReadLine()
              if (-not $headerLine) { throw 'Empty NDJSON manifest (missing header).' }
              $manifest = $headerLine | ConvertFrom-Json
              $computer = [string]$manifest.computer
              $local = [bool]$manifest.local
              $prefix = [string]$manifest.prefix
              $action = [string]$manifest.action
              $restartDns = [bool]$manifest.restartDns
              $expected = [int]$manifest.expected
              $smokeDomain = [string]$manifest.smokeDomain
              $throttleMs = 15
              $gcEvery = 25
              if ($null -ne $manifest.PSObject.Properties['throttleMs']) { $throttleMs = [int]$manifest.throttleMs }
              if ($null -ne $manifest.PSObject.Properties['gcEvery']) { $gcEvery = [int]$manifest.gcEvery }

              try {
                Remove-DnsBlockerPolicies -Computer $computer -Prefix $prefix -Local $local
              } catch {
                Write-Error (Format-DnsBlockerError $_)
                exit 1
              }

              $i = 0
              while ($null -ne ($polLine = $reader.ReadLine())) {
                if ([string]::IsNullOrWhiteSpace($polLine)) { continue }
                $pol = $polLine | ConvertFrom-Json
                $i++
                $name = [string]$pol.name
                $fqdn = [string]$pol.fqdn
                $order = [uint32]$pol.order
                try {
                  Add-DnsBlockerPolicy -Computer $computer -Name $name -Action $action -Fqdn $fqdn -Order $order -Local $local
                  if ($gcEvery -gt 0 -and ($i % $gcEvery) -eq 0) {
                    if ($throttleMs -gt 0) { Start-Sleep -Milliseconds $throttleMs }
                    [GC]::Collect()
                    [GC]::WaitForPendingFinalizers()
                    Write-Output ("OK progress " + $i + "/" + $expected + " (GC)")
                  } elseif (($i % 50) -eq 0) {
                    Write-Output ("OK progress " + $i + "/" + $expected)
                  }
                } catch {
                  Write-Error (("Failed policy " + $name + " on " + $computer + " — ") + (Format-DnsBlockerError $_))
                  exit 1
                }
              }
              Write-Output ("OK added=" + $expected)
            } finally {
              $reader.Dispose()
            }

            # Prove smoke apex is actually present in persisted FQDN criteria (not just Enabled count).
            if ($smokeDomain -and $smokeDomain.Length -gt 0) {
              $hit = Find-DnsBlockerSmokeInCriteria -Computer $computer -Prefix $prefix -Local $local -SmokeDomain $smokeDomain
              if (-not $hit) {
                Write-Error ("CRITERIA_SEARCH_FAIL: no " + $prefix + "* policy Criteria contains EQ," + $smokeDomain + " or *." + $smokeDomain + " — Enabled Deny/Ignore policies will not match nslookup (Vincent windc04 repro).")
                exit 1
              }
              Write-Output ("CRITERIA_SEARCH_OK domain=" + $smokeDomain + " policy=" + $hit)
            }

            try {
              if ($local) {
                Clear-DnsServerCache -Force -ErrorAction SilentlyContinue
              } else {
                Clear-DnsServerCache -ComputerName $computer -Force -ErrorAction SilentlyContinue
              }
              Write-Output 'CACHE_CLEARED'
            } catch {
              Write-Output ('CACHE_CLEAR_SKIPPED: ' + $_.Exception.Message)
            }

            if ($restartDns) {
              try {
                Restart-DnsBlockerDnsServer -Computer $computer -Local $local
                Write-Output 'DNS_RESTARTED'
              } catch {
                Write-Error (('DNS restart failed on ' + $computer + ' — ') + (Format-DnsBlockerError $_))
                exit 1
              }
            } else {
              Write-Output 'DNS_RESTART_SKIPPED'
            }

            # Recount with retries — Get|*Name -like can return 0 right after DNS restart while
            # sentinel/registry still show policies (Vincent 1.0.31: smoke=OK + installed=0).
            $installedLive = 0
            $enabledLive = 0
            $countSource = 'none'
            $countAttempt = 0
            for ($countAttempt = 1; $countAttempt -le 30; $countAttempt++) {
              $counts = Get-DnsBlockerLiveCounts -Computer $computer -Prefix $prefix -Local $local
              try { $installedLive = [int]$counts.installed } catch { $installedLive = 0 }
              try { $enabledLive = [int]$counts.enabled } catch { $enabledLive = 0 }
              try { $countSource = [string]$counts.source } catch { $countSource = 'unknown' }
              if ($installedLive -gt 0 -and $enabledLive -gt 0) { break }
              Start-Sleep -Seconds 1
            }
            Write-Output ("COUNT_SOURCE source=$countSource installed=$installedLive enabled=$enabledLive expected=$expected attempt=$countAttempt")
            if ($installedLive -le 0 -or $enabledLive -le 0) {
              Write-Output ("VERIFY installed=$installedLive enabled=$enabledLive expected=$expected smoke=FAIL")
              Write-Error ("SMOKE_FAIL: Enabled QRP count is 0 after apply (installed=$installedLive enabled=$enabledLive expected=$expected source=$countSource). Succeeded push without Enabled policies will not block.")
              exit 1
            }

            $smoke = 'SKIP'
            if ($smokeDomain -and $smokeDomain.Length -gt 0) {
              # Prefer the real computer DNS name — many DC DNS roles answer on the NIC IP but NOT on 127.0.0.1.
              # Using only loopback caused false smoke=OK (resolve error treated as "blocked") while clients
              # querying the NIC still got Cloudflare A/AAAA.
              $serverCandidates = New-Object System.Collections.Generic.List[string]
              if ($computer -and $computer.Length -gt 0 -and $computer -ne '.' -and $computer -ne 'localhost') {
                [void]$serverCandidates.Add($computer)
              }
              if ($local) { [void]$serverCandidates.Add('127.0.0.1') }
              if ($serverCandidates.Count -eq 0) { [void]$serverCandidates.Add('127.0.0.1') }

              $server = $null
              $canary = 'www.microsoft.com'
              $canaryOk = $false
              foreach ($candidate in $serverCandidates) {
                for ($t = 0; $t -lt 45; $t++) {
                  try {
                    $ca = @(Resolve-DnsName -Name $canary -Server $candidate -Type A -DnsOnly -ErrorAction Stop |
                      Where-Object { $_.IPAddress } |
                      ForEach-Object { $_.IPAddress.ToString() })
                    if ($ca.Count -gt 0) {
                      $server = $candidate
                      $canaryOk = $true
                      Write-Output ("CANARY_OK " + $canary + " via " + $server + " -> " + ($ca -join ','))
                      break
                    }
                  } catch {
                    # DNS may still be binding after Restart-Service
                  }
                  Start-Sleep -Seconds 1
                }
                if ($canaryOk) { break }
              }

              if (-not $canaryOk -or -not $server) {
                Write-Output ("VERIFY installed=$installedLive enabled=$enabledLive expected=$expected smoke=FAIL")
                Write-Error ("SMOKE_FAIL: canary " + $canary + " did not resolve via " + ($serverCandidates -join '|') + " after QRP apply — DNS not ready or recursion broken; refusing to mark sync Succeeded.")
                exit 1
              }

              try {
                $answers = @(Resolve-DnsName -Name $smokeDomain -Server $server -Type A -DnsOnly -ErrorAction Stop)
                $addrs = @($answers | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress.ToString() })
                if ($addrs.Count -gt 0) {
                  $smoke = 'FAIL'
                  Write-Output ("VERIFY installed=$installedLive enabled=$enabledLive expected=$expected smoke=FAIL")
                  Write-Error ("SMOKE_FAIL: " + $smokeDomain + " still resolves to " + ($addrs -join ',') + " via " + $server + " after QRP apply — blocking ineffective (public A/AAAA).")
                  exit 1
                }
                $smoke = 'OK'
                Write-Output ("SMOKE_OK " + $smokeDomain + " :: no A/AAAA via " + $server)
              } catch {
                $msg = $_.Exception.Message
                if ($msg -match 'SMOKE_FAIL') { throw }
                # Only treat known block/drop outcomes as success. Any other error is FAIL
                # (previous code treated ALL exceptions as OK → false Succeeded on windc04).
                if ($msg -match '(?i)NXDOMAIN|NameError|DNS_ERROR_RCODE_NAME_ERROR|not exist|REFUSED|SERVFAIL|ServerFailure|timed out|Timeout|No response|DNS server failure') {
                  $smoke = 'OK'
                  Write-Output ("SMOKE_OK " + $smokeDomain + " :: " + $msg)
                } else {
                  Write-Output ("VERIFY installed=$installedLive enabled=$enabledLive expected=$expected smoke=FAIL")
                  Write-Error ("SMOKE_FAIL: unexpected resolve error for " + $smokeDomain + " via " + $server + ": " + $msg)
                  exit 1
                }
              }
            } elseif ($expected -gt 0) {
              Write-Output ("VERIFY installed=$installedLive enabled=$enabledLive expected=$expected smoke=FAIL")
              Write-Error 'SMOKE_FAIL: no smoke domain in manifest — refusing Succeeded without Resolve-DnsName proof.'
              exit 1
            }

            # Final recount after smoke (same inventory helper as probe) — never claim smoke=OK with 0.
            $counts = Get-DnsBlockerLiveCounts -Computer $computer -Prefix $prefix -Local $local
            try { $installedLive = [int]$counts.installed } catch { $installedLive = 0 }
            try { $enabledLive = [int]$counts.enabled } catch { $enabledLive = 0 }
            try { $countSource = [string]$counts.source } catch { }
            if ($installedLive -le 0 -or $enabledLive -le 0 -or $installedLive -ne $expected -or $enabledLive -ne $expected) {
              $smoke = 'FAIL'
            }
            Write-Output ("VERIFY installed=$installedLive enabled=$enabledLive expected=$expected smoke=$smoke source=$countSource")
            if ($installedLive -ne $expected -or $enabledLive -ne $expected) {
              Write-Error ("Live enabled count mismatch: installed=$installedLive enabled=$enabledLive expected=$expected source=$countSource")
              exit 1
            }
            if ($smoke -eq 'FAIL') {
              Write-Error 'SMOKE_FAIL: verify line smoke=FAIL'
              exit 1
            }
            Write-Output ("DONE policies=$expected")
            """;
    }

    private async Task<string> RunPowerShellAsync(
        string script,
        CancellationToken cancellationToken,
        Action<string>? onStdoutLine = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            stdout.AppendLine(e.Data);
            try { onStdoutLine?.Invoke(e.Data); }
            catch { /* progress must not break apply */ }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start powershell.exe");

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // Best-effort — some hosts disallow priority changes.
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.StandardInput.WriteAsync(script.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var err = stderr.ToString().Trim();
            if (string.IsNullOrWhiteSpace(err))
                err = stdout.ToString().Trim();
            _logger.LogError("PowerShell failed ({Code}): {Error}", process.ExitCode, err);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"PowerShell exited with code {process.ExitCode}"
                : err);
        }

        return stdout.ToString();
    }

    /// <summary>
    /// Prefer well-known blocked apexes that resolve publicly when QRP is ineffective.
    /// Scans chunks without allocating a full multi-million HashSet (DC RSS).
    /// </summary>
    public static string? PreferSmokeDomain(IReadOnlyList<IReadOnlyList<string>> chunks)
    {
        string[] preferred = ["beeg.com", "pornhub.com", "xvideos.com", "doubleclick.net"];
        foreach (var p in preferred)
        {
            var wild = "*." + p;
            foreach (var chunk in chunks)
            {
                foreach (var d in chunk)
                {
                    if (string.IsNullOrWhiteSpace(d))
                        continue;
                    var n = d.AsSpan().Trim().Trim('.');
                    if (n.Equals(p, StringComparison.OrdinalIgnoreCase)
                        || n.Equals(wild, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }
        }

        string? first = null;
        foreach (var chunk in chunks)
        {
            foreach (var d in chunk)
            {
                if (string.IsNullOrWhiteSpace(d))
                    continue;
                var n = d.Trim().Trim('.').ToLowerInvariant();
                if (n.StartsWith("*.", StringComparison.Ordinal))
                    n = n[2..];
                if (first is null
                    || string.Compare(n, first, StringComparison.OrdinalIgnoreCase) < 0)
                    first = n;
            }
        }

        return first;
    }

    private static string EscapePsString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
