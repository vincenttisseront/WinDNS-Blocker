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
    /// <summary>
    /// Soft cap for a single -Fqdn criterion string. Oversized criteria often fail at the CIM layer;
    /// prefer many small policies over one huge string.
    /// </summary>
    public const int DefaultMaxFqdnCriterionChars = 8_000;

    private static readonly Regex Win32CodeRegex = new(@"WIN32\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<PowerShellDnsPolicyProvider> _logger;
    private readonly int _maxFqdnCriterionChars;

    public PowerShellDnsPolicyProvider(
        ILogger<PowerShellDnsPolicyProvider>? logger = null,
        int maxFqdnCriterionChars = DefaultMaxFqdnCriterionChars)
    {
        _logger = logger ?? NullLogger<PowerShellDnsPolicyProvider>.Instance;
        _maxFqdnCriterionChars = Math.Clamp(maxFqdnCriterionChars, 500, 64_000);
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
        var safeChunks = NormalizeChunks(chunks, _maxFqdnCriterionChars);

        // One remoting session: remove all prefixed policies, then recreate (idempotent).
        var script = BuildApplyScript(computerName, policyNamePrefix, normalizedAction, safeChunks);
        try
        {
            var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Applied {Count} QRP policies on {Target} (prefix {Prefix})",
                safeChunks.Count, computerName, policyNamePrefix);
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
        if (!OperatingSystem.IsWindows())
            return [];

        var script = BuildListScript(computerName, policyNamePrefix);
        var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
            return [];

        output = output.Trim();
        if (output.StartsWith('['))
            return JsonSerializer.Deserialize<List<string>>(output) ?? [];
        if (output.StartsWith('"'))
            return [JsonSerializer.Deserialize<string>(output) ?? output.Trim('"')];
        return [output];
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
                // Commas break EQ,a,b,c criterion encoding.
                if (domain.Contains(',', StringComparison.Ordinal))
                    continue;
                flat.Add(domain.Trim().Trim('.').ToLowerInvariant());
            }
        }

        return Ingest.DomainListParser.ChunkByFqdnBudget(flat, maxFqdnCriterionChars);
    }

    private static string BuildApplyScript(
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

            function Remove-DnsBlockerPolicies([string]$Computer, [string]$Prefix) {
              $existing = @()
              try {
                $existing = @(Get-DnsServerQueryResolutionPolicy -ComputerName $Computer -ErrorAction Stop |
                  Where-Object { $_.Name -like ($Prefix + '*') })
              } catch {
                # Empty server / first run: treat as no policies
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
              try {
                Add-DnsServerQueryResolutionPolicy -ComputerName $Computer -Name $Name -Action $Action -Fqdn $Fqdn -ErrorAction Stop
              } catch {
                $msg = $_.Exception.Message
                if ($msg -match 'already exists' -or $msg -match 'DNS_ERROR_POLICY_ALREADY_EXISTS' -or $msg -match 'object already exists') {
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

        sb.AppendLine($"Write-Output 'DONE policies={chunks.Count}'");

        return sb.ToString();
    }

    private static string BuildListScript(string computerName, string policyNamePrefix) =>
        $$"""
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
            if ($cim.NativeErrorCode -ne 0) { [void]$parts.Add(('WIN32 ' + [int]$cim.NativeErrorCode)) }
          }
          if ($err.FullyQualifiedErrorId) { [void]$parts.Add(('FQID=' + $err.FullyQualifiedErrorId)) }
          return ($parts -join ' | ')
        }
        Import-Module DnsServer -ErrorAction Stop
        $computer = '{{EscapePsString(computerName)}}'
        $prefix = '{{EscapePsString(policyNamePrefix)}}'
        try {
          $names = @(Get-DnsServerQueryResolutionPolicy -ComputerName $computer -ErrorAction Stop |
            Where-Object { $_.Name -like ($prefix + '*') } |
            Select-Object -ExpandProperty Name)
        } catch {
          Write-Error (Format-DnsBlockerError $_)
          exit 1
        }
        $names | ConvertTo-Json -Compress
        """;

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
