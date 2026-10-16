using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Dns;

/// <summary>
/// Applies QRP via the DnsServer PowerShell module using -ComputerName remoting.
/// Requires Windows + RSAT/DNS Server tools on the hub.
/// </summary>
public sealed class PowerShellDnsPolicyProvider : IDnsPolicyProvider
{
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
        var computerArg = EscapePsString(computerName);

        // Remove existing prefixed policies
        var removeScript = $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module DnsServer -ErrorAction Stop
            $computer = '{{computerArg}}'
            $prefix = '{{EscapePsString(policyNamePrefix)}}'
            $existing = @(Get-DnsServerQueryResolutionPolicy -ComputerName $computer -ErrorAction SilentlyContinue | Where-Object { $_.Name -like ($prefix + '*') })
            foreach ($p in $existing) {
              Remove-DnsServerQueryResolutionPolicy -ComputerName $computer -Name $p.Name -Force -ErrorAction Stop
            }
            """;

        await RunPowerShellAsync(removeScript, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = $"{policyNamePrefix}{(i + 1):D4}";
            var fqdn = "EQ," + string.Join(",", chunks[i]);
            var addScript = $$"""
                $ErrorActionPreference = 'Stop'
                Import-Module DnsServer -ErrorAction Stop
                Add-DnsServerQueryResolutionPolicy -ComputerName '{{computerArg}}' -Name '{{EscapePsString(name)}}' -Action {{normalizedAction}} -Fqdn '{{EscapePsString(fqdn)}}' -ErrorAction Stop
                """;
            await RunPowerShellAsync(addScript, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Applied policy {Name} on {Target} ({Count} domains)", name, computerName, chunks[i].Count);
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
