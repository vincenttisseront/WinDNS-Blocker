using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Dns;

/// <summary>
/// Applies QRP via the DnsServer PowerShell module using -ComputerName remoting.
/// Requires Windows + RSAT/DNS Server tools on the hub.
/// </summary>
public sealed class PowerShellDnsPolicyProvider : IDnsPolicyProvider
{
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
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PowerShellDnsPolicyProvider requires Windows.");

        var normalizedAction = action.Equals("IGNORE", StringComparison.OrdinalIgnoreCase) ? "IGNORE" : "DENY";

        // One remoting session: remove all prefixed policies, then recreate (idempotent).
        // Handles partial prior syncs and WIN32 9971 (policy already exists).
        var script = BuildApplyScript(computerName, policyNamePrefix, normalizedAction, chunks);
        try
        {
            var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Applied {Count} QRP policies on {Target} (prefix {Prefix})",
                chunks.Count, computerName, policyNamePrefix);
            if (!string.IsNullOrWhiteSpace(output))
                _logger.LogDebug("PowerShell output: {Output}", output.Trim());
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(EnrichDnsPolicyError(ex.Message, computerName), ex);
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

        // Prefer IsEnabled when present; fall back to Enabled / IsDisabled shapes across OS builds.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module DnsServer -ErrorAction Stop
            $computer = '{{EscapePsString(computerName)}}'
            $prefix = '{{EscapePsString(policyNamePrefix)}}'
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
            if ($rows.Count -eq 0) { '[]' } else { $rows | ConvertTo-Json -Compress }
            """;

        var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return ParsePolicyInventory(output);
    }

    /// <summary>
    /// Builds the single-session PowerShell that replaces all prefixed QRP policies.
    /// Exposed for unit tests (script structure / 9971 retry).
    /// </summary>
    public static string BuildApplyScript(
        string computerName,
        string policyNamePrefix,
        string action,
        IReadOnlyList<IReadOnlyList<string>> chunks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
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

            function Remove-DnsBlockerPolicies([string]$Computer, [string]$Prefix) {
              $existing = @()
              try {
                $existing = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -ErrorAction Stop |
                  Where-Object { $_.Name -like ($Prefix + '*') })
              } catch {
                # Empty server / first run / transient list failure: treat as no policies
                $existing = @()
              }
              foreach ($p in $existing) {
                try {
                  Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $p.Name -Force -ErrorAction Stop
                } catch {
                  # Retry once after brief delay (replication / file lock)
                  Start-Sleep -Milliseconds 400
                  Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $p.Name -Force -ErrorAction Stop
                }
              }
            }

            function Add-DnsBlockerPolicy([string]$Computer, [string]$Name, [string]$Action, [string]$Fqdn) {
              # Server-level QRP (no -ApplyOnRecursion): matches incoming queries before recursion.
              # DENY → REFUSED; IGNORE → drop (MS blackhole examples). Prefer a single client DNS
              # so stubs do not fail over to 8.8.8.8 after REFUSED.
              try {
                Add-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Action $Action -Fqdn $Fqdn -ErrorAction Stop
              } catch {
                if (Test-DnsBlockerPolicyAlreadyExists $_) {
                  # Stale / partial prior sync: force replace this chunk
                  Remove-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Force -ErrorAction SilentlyContinue
                  Start-Sleep -Milliseconds 300
                  Add-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Action $Action -Fqdn $Fqdn -ErrorAction Stop
                  return
                }
                throw
              }
            }

            Import-Module DnsServer -ErrorAction Stop
            """);

        sb.AppendLine($"$computer = '{EscapePsString(computerName)}'");
        sb.AppendLine($"$prefix = '{EscapePsString(policyNamePrefix)}'");
        sb.AppendLine("$action = '" + EscapePsString(action) + "'");
        sb.AppendLine("""
            try {
              Remove-DnsBlockerPolicies -Computer $computer -Prefix $prefix
            } catch {
              Write-Error (Format-DnsBlockerError $_)
              exit 1
            }
            """);

        for (var i = 0; i < chunks.Count; i++)
        {
            var name = $"{policyNamePrefix}{(i + 1):D4}";
            var fqdn = "EQ," + string.Join(",", chunks[i]);
            sb.AppendLine($"$name = '{EscapePsString(name)}'");
            sb.AppendLine($"$fqdn = '{EscapePsString(fqdn)}'");
            sb.AppendLine("""
                try {
                  Add-DnsBlockerPolicy -Computer $computer -Name $name -Action $action -Fqdn $fqdn
                  Write-Output ("OK " + $name + " domains=" + (($fqdn -split ',').Count - 1))
                } catch {
                  Write-Error (("Failed policy " + $name + " on " + $computer + " — ") + (Format-DnsBlockerError $_))
                  exit 1
                }
                """);
        }

        // Cached answers bypass QRP — clear after replace so new DENY/IGNORE take effect immediately.
        sb.AppendLine("""
            try {
              Clear-DnsServerCache -ComputerName $computer -Force -ErrorAction SilentlyContinue
              Write-Output 'CACHE_CLEARED'
            } catch {
              Write-Output ('CACHE_CLEAR_SKIPPED: ' + $_.Exception.Message)
            }
            """);
        sb.AppendLine($"Write-Output 'DONE policies={chunks.Count}'");

        return sb.ToString();
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

        return string.IsNullOrEmpty(text) ? $"DNS policy operation failed on '{computerName}'." : text;
    }

    public static IReadOnlyList<DnsPolicyInfo> ParsePolicyInventory(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return [];

        output = output.Trim();
        // PowerShell may emit banner lines before JSON — keep the last JSON-looking segment.
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

    private static string EscapePsString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
