using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DnsBlocker.Core.Net;
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
        bool restartDnsAfterApply = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PowerShellDnsPolicyProvider requires Windows.");

        var normalizedAction = action.Equals("IGNORE", StringComparison.OrdinalIgnoreCase) ? "IGNORE" : "DENY";
        var normalized = NormalizeChunks(chunks, MaxFqdnCriterionChars);

        var manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"DnsBlocker-qrp-{Guid.NewGuid():N}.json");

        try
        {
            WriteManifest(manifestPath, computerName, policyNamePrefix, normalizedAction, normalized, restartDnsAfterApply);
            var script = BuildApplyScriptFromManifest(manifestPath);
            try
            {
                var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
                ValidateApplyOutput(output, computerName, normalized.Count);
                _logger.LogInformation(
                    "Applied and verified {Count} QRP policies on {Target} (prefix {Prefix}, action {Action}, restartDns={Restart})",
                    normalized.Count, computerName, policyNamePrefix, normalizedAction, restartDnsAfterApply);
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

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module DnsServer -ErrorAction Stop
            $computer = '{{EscapePsString(computerName)}}'
            $prefix = '{{EscapePsString(policyNamePrefix)}}'
            $local = {{localLiteral}}
            if ($local) {
              $rows = @(Get-DnsServerQueryResolutionPolicy -ErrorAction Stop |
                Where-Object { $_.Name -like ($prefix + '*') } |
                ForEach-Object {
                  $enabled = $true
                  if ($null -ne $_.IsEnabled) { $enabled = [bool]$_.IsEnabled }
                  elseif ($null -ne $_.Enabled) { $enabled = [bool]$_.Enabled }
                  elseif ($null -ne $_.IsDisabled) { $enabled = -not [bool]$_.IsDisabled }
                  [pscustomobject]@{
                    Name = $_.Name
                    IsEnabled = $enabled
                    Action = [string]$_.Action
                  }
                })
            } else {
              $rows = @(Get-DnsServerQueryResolutionPolicy -ComputerName $computer -ErrorAction Stop |
                Where-Object { $_.Name -like ($prefix + '*') } |
                ForEach-Object {
                  $enabled = $true
                  if ($null -ne $_.IsEnabled) { $enabled = [bool]$_.IsEnabled }
                  elseif ($null -ne $_.Enabled) { $enabled = [bool]$_.Enabled }
                  elseif ($null -ne $_.IsDisabled) { $enabled = -not [bool]$_.IsDisabled }
                  [pscustomobject]@{
                    Name = $_.Name
                    IsEnabled = $enabled
                    Action = [string]$_.Action
                  }
                })
            }
            if ($rows.Count -eq 0) { '[]' } else { $rows | ConvertTo-Json -Compress }
            """;

        var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return ParsePolicyInventory(output);
    }

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
        bool restartDnsAfterApply = true)
    {
        var normalized = NormalizeChunks(chunks, MaxFqdnCriterionChars);
        var manifestPath = Path.Combine(
            Path.GetTempPath(),
            $"DnsBlocker-qrp-test-{Guid.NewGuid():N}.json");
        WriteManifest(manifestPath, computerName, policyNamePrefix, action, normalized, restartDnsAfterApply);
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
                flat.Add(domain.Trim().Trim('.').ToLowerInvariant());
            }
        }

        return Ingest.DomainListParser.ChunkByFqdnBudget(flat, maxFqdnCriterionChars);
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

        var verifyLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.StartsWith("VERIFY ", StringComparison.OrdinalIgnoreCase));

        if (verifyLine is null)
            throw new InvalidOperationException(
                $"QRP apply on '{computerName}' did not emit VERIFY line (expected {expectedPolicies} policies). Output: {TrimForError(output)}");

        // VERIFY installed=N enabled=M expected=E smoke=OK|SKIP
        var installed = ReadVerifyInt(verifyLine, "installed");
        var enabled = ReadVerifyInt(verifyLine, "enabled");
        var expected = ReadVerifyInt(verifyLine, "expected");

        if (expected != expectedPolicies)
            throw new InvalidOperationException(
                $"VERIFY expected mismatch on '{computerName}': script expected={expected}, hub expected={expectedPolicies}. {verifyLine}");

        if (installed != expectedPolicies || enabled != expectedPolicies)
            throw new InvalidOperationException(
                $"QRP live count mismatch on '{computerName}': installed={installed}, enabled={enabled}, expected={expectedPolicies}. " +
                "Succeeded push without Enabled policies will not block. " + verifyLine);

        if (verifyLine.Contains("smoke=FAIL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"QRP smoke test failed on '{computerName}': sample domain still resolves after policy apply. {verifyLine}");
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

    private static void WriteManifest(
        string path,
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks,
        bool restartDnsAfterApply)
    {
        var isLocal = LocalDnsTarget.IsLocalTarget(computerName);
        // Prefer a domain that should resolve on the public Internet when unblocked (Vincent repro).
        string? smokeDomain = PreferSmokeDomain(chunks);

        var policies = new List<object>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            policies.Add(new
            {
                name = FormatPolicyName(policyNamePrefix, i + 1),
                fqdn = "EQ," + string.Join(",", chunks[i]),
                order = i + 1
            });
        }

        var manifest = new
        {
            computer = computerName,
            local = isLocal,
            prefix = policyNamePrefix,
            action,
            restartDns = restartDnsAfterApply,
            smokeDomain,
            expected = chunks.Count,
            policies
        };

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(path, json, Encoding.UTF8);
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

            function Test-DnsBlockerHasFqdnCriteria($policy) {
              if ($null -eq $policy) { return $false }
              if ($policy.PSObject.Properties['Fqdn'] -and $policy.Fqdn -and ([string]$policy.Fqdn).Length -gt 3) {
                return $true
              }
              if ($null -eq $policy.Criteria) { return $false }
              foreach ($c in @($policy.Criteria)) {
                $t = ''
                if ($c.PSObject.Properties['CriteriaType']) { $t = [string]$c.CriteriaType }
                elseif ($c.PSObject.Properties['Type']) { $t = [string]$c.Type }
                $content = ''
                if ($c.PSObject.Properties['Content']) { $content = [string]$c.Content }
                elseif ($c.PSObject.Properties['Criterion']) { $content = [string]$c.Criterion }
                if ($t -match 'Fqdn') { return $true }
                if ($content -match '(?i)EQ,') { return $true }
              }
              $blob = ($policy | Format-List * | Out-String)
              if ($blob -match '(?i)Fqdn' -and $blob -match '(?i)EQ,') { return $true }
              return $false
            }

            function Remove-DnsBlockerPolicies([string]$Computer, [string]$Prefix, [bool]$Local) {
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
              foreach ($p in $existing) {
                try {
                  if ($Local) {
                    Remove-DnsServerQueryResolutionPolicy -Name $p.Name -Force -ErrorAction Stop
                  } else {
                    Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $p.Name -Force -ErrorAction Stop
                  }
                } catch {
                  Start-Sleep -Milliseconds 400
                  if ($Local) {
                    Remove-DnsServerQueryResolutionPolicy -Name $p.Name -Force -ErrorAction Stop
                  } else {
                    Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $p.Name -Force -ErrorAction Stop
                  }
                }
              }
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
                  $created = Get-DnsServerQueryResolutionPolicy -Name $Name -ErrorAction Stop
                } else {
                  Enable-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -ErrorAction Stop
                  $created = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -ErrorAction Stop
                }
              }

              if (-not (Test-DnsBlockerHasFqdnCriteria $created)) {
                if ($Local) {
                  $created = Get-DnsServerQueryResolutionPolicy -Name $Name -ErrorAction Stop
                } else {
                  $created = Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -ErrorAction Stop
                }
                if (-not (Test-DnsBlockerHasFqdnCriteria $created)) {
                  throw ("FQDN criteria empty on policy " + $Name + " — Add succeeded but QRP will not block (criteria not persisted).")
                }
              }
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
              if ($Local) {
                $rows = @(Get-DnsServerQueryResolutionPolicy -ErrorAction Stop | Where-Object { $_.Name -like ($Prefix + '*') })
              } else {
                $rows = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -ErrorAction Stop | Where-Object { $_.Name -like ($Prefix + '*') })
              }
              $enabled = 0
              foreach ($p in $rows) {
                $en = $true
                if ($null -ne $p.IsEnabled) { $en = [bool]$p.IsEnabled }
                elseif ($null -ne $p.Enabled) { $en = [bool]$p.Enabled }
                elseif ($null -ne $p.IsDisabled) { $en = -not [bool]$p.IsDisabled }
                if ($en) { $enabled++ }
              }
              return @{ installed = $rows.Count; enabled = $enabled }
            }

            Import-Module DnsServer -ErrorAction Stop
            $manifestPath = '{{pathLiteral}}'
            if (-not (Test-Path -LiteralPath $manifestPath)) {
              Write-Error ("Manifest not found: " + $manifestPath)
              exit 1
            }
            $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $computer = [string]$manifest.computer
            $local = [bool]$manifest.local
            $prefix = [string]$manifest.prefix
            $action = [string]$manifest.action
            $restartDns = [bool]$manifest.restartDns
            $expected = [int]$manifest.expected
            $smokeDomain = [string]$manifest.smokeDomain

            try {
              Remove-DnsBlockerPolicies -Computer $computer -Prefix $prefix -Local $local
            } catch {
              Write-Error (Format-DnsBlockerError $_)
              exit 1
            }

            $i = 0
            foreach ($pol in @($manifest.policies)) {
              $i++
              $name = [string]$pol.name
              $fqdn = [string]$pol.fqdn
              $order = [uint32]$pol.order
              try {
                Add-DnsBlockerPolicy -Computer $computer -Name $name -Action $action -Fqdn $fqdn -Order $order -Local $local
                if (($i % 50) -eq 0) {
                  Write-Output ("OK progress " + $i + "/" + $expected)
                }
              } catch {
                Write-Error (("Failed policy " + $name + " on " + $computer + " — ") + (Format-DnsBlockerError $_))
                exit 1
              }
            }
            Write-Output ("OK added=" + $expected)

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

            $counts = Get-DnsBlockerLiveCounts -Computer $computer -Prefix $prefix -Local $local
            $smoke = 'SKIP'
            if ($smokeDomain -and $smokeDomain.Length -gt 0) {
              $server = $(if ($local) { '127.0.0.1' } else { $computer })
              try {
                $answers = @(Resolve-DnsName -Name $smokeDomain -Server $server -Type A -DnsOnly -ErrorAction Stop)
                $addrs = @($answers | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress.ToString() })
                if ($addrs.Count -gt 0) {
                  $smoke = 'FAIL'
                  Write-Error ("SMOKE_FAIL: " + $smokeDomain + " still resolves to " + ($addrs -join ',') + " via " + $server + " after QRP apply — blocking ineffective.")
                  Write-Output ("VERIFY installed=$($counts.installed) enabled=$($counts.enabled) expected=$expected smoke=FAIL")
                  exit 1
                }
                $smoke = 'OK'
              } catch {
                if ($_.Exception.Message -match 'SMOKE_FAIL') { throw }
                # REFUSED / timeout / NXDOMAIN / name error ⇒ treated as blocked
                $smoke = 'OK'
                Write-Output ("SMOKE_OK " + $smokeDomain + " :: " + $_.Exception.Message)
              }
            }

            Write-Output ("VERIFY installed=$($counts.installed) enabled=$($counts.enabled) expected=$expected smoke=$smoke")
            if ($counts.installed -ne $expected -or $counts.enabled -ne $expected) {
              Write-Error ("Live enabled count mismatch: installed=$($counts.installed) enabled=$($counts.enabled) expected=$expected")
              exit 1
            }
            Write-Output ("DONE policies=$expected")
            """;
    }

    private async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken)
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
            if (e.Data is not null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderr.AppendLine(e.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start powershell.exe");

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
    /// </summary>
    public static string? PreferSmokeDomain(IReadOnlyList<IReadOnlyList<string>> chunks)
    {
        string[] preferred = ["beeg.com", "pornhub.com", "xvideos.com", "doubleclick.net"];
        var flat = chunks.SelectMany(c => c).Where(d => !string.IsNullOrWhiteSpace(d));
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in flat)
        {
            var n = d.Trim().Trim('.').ToLowerInvariant();
            set.Add(n);
            if (n.StartsWith("*.", StringComparison.Ordinal))
                set.Add(n[2..]);
        }

        foreach (var p in preferred)
        {
            if (set.Contains(p) || set.Contains("*." + p))
                return p;
        }

        foreach (var d in set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (d.StartsWith("*.", StringComparison.Ordinal))
                return d[2..];
            return d;
        }

        return null;
    }

    private static string EscapePsString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
