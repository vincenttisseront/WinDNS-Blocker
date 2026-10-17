using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Stats;

/// <summary>
/// Enables Microsoft-Windows-DNSServer/Analytical on remote DNS targets and pulls recent
/// QUERY_RECEIVED events into the hub. Local ETW on the hub alone cannot see queries that
/// hit another server (e.g. hub on windc04, DNS on windc05).
/// </summary>
public sealed class DnsAnalyticalLogClient
{
    private readonly ILogger<DnsAnalyticalLogClient> _logger;

    public DnsAnalyticalLogClient(ILogger<DnsAnalyticalLogClient>? logger = null)
    {
        _logger = logger ?? NullLogger<DnsAnalyticalLogClient>.Instance;
    }

    public async Task<AnalyticalLogStatus> EnsureAnalyticalLoggingAsync(
        string computerName,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AnalyticalLogStatus
            {
                Target = computerName,
                Ok = false,
                AnalyticalEnabled = false,
                Message = "Analytical logging requires Windows + remoting to the DNS target."
            };
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $computer = '{{EscapePsString(computerName)}}'
            $isLocal = $false
            try {
              $localNames = @($env:COMPUTERNAME, [System.Net.Dns]::GetHostName())
              if ($localNames -contains $computer) { $isLocal = $true }
              $fqdn = [System.Net.Dns]::GetHostEntry('localhost').HostName
              if ($fqdn -and $computer -eq $fqdn) { $isLocal = $true }
            } catch { }

            function Enable-DnsAnalytical([bool]$Remote) {
              $logName = 'Microsoft-Windows-DNSServer/Analytical'
              if ($Remote) {
                $session = New-CimSession -ComputerName $computer -ErrorAction Stop
                try {
                  $cfg = Get-WinEvent -ListLog $logName -CimSession $session -ErrorAction Stop
                  if (-not $cfg.IsEnabled) {
                    # Analytic channels: wevtutil over remoting
                    $null = Invoke-Command -ComputerName $computer -ScriptBlock {
                      param($n)
                      wevtutil.exe sl $n /e:true
                    } -ArgumentList $logName -ErrorAction Stop
                  }
                  $cfg2 = Get-WinEvent -ListLog $logName -CimSession $session -ErrorAction Stop
                  return [pscustomobject]@{ Enabled = [bool]$cfg2.IsEnabled; Detail = ('remote analytical IsEnabled=' + $cfg2.IsEnabled) }
                } finally {
                  Remove-CimSession $session -ErrorAction SilentlyContinue
                }
              } else {
                $cfg = Get-WinEvent -ListLog $logName -ErrorAction Stop
                if (-not $cfg.IsEnabled) {
                  wevtutil.exe sl $logName /e:true
                  $cfg = Get-WinEvent -ListLog $logName -ErrorAction Stop
                }
                return [pscustomobject]@{ Enabled = [bool]$cfg.IsEnabled; Detail = ('local analytical IsEnabled=' + $cfg.IsEnabled) }
              }
            }

            try {
              $result = Enable-DnsAnalytical -Remote (-not $isLocal)
              @{ ok = $true; enabled = [bool]$result.Enabled; message = [string]$result.Detail } | ConvertTo-Json -Compress
            } catch {
              @{ ok = $false; enabled = $false; message = $_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;

        try
        {
            var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            var parsed = ParseStatusJson(output, computerName);
            _logger.LogInformation(
                "Analytical logging on {Target}: ok={Ok} enabled={Enabled} — {Message}",
                computerName, parsed.Ok, parsed.AnalyticalEnabled, parsed.Message);
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enable analytical logging on {Target}", computerName);
            return new AnalyticalLogStatus
            {
                Target = computerName,
                Ok = false,
                AnalyticalEnabled = false,
                Message = ex.Message
            };
        }
    }

    public async Task<AnalyticalLogStatus> GetAnalyticalLoggingStatusAsync(
        string computerName,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new AnalyticalLogStatus
            {
                Target = computerName,
                Ok = false,
                AnalyticalEnabled = false,
                Message = "Windows required."
            };
        }

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $computer = '{{EscapePsString(computerName)}}'
            $logName = 'Microsoft-Windows-DNSServer/Analytical'
            try {
              $isLocal = $false
              $localNames = @($env:COMPUTERNAME, [System.Net.Dns]::GetHostName())
              if ($localNames -contains $computer) { $isLocal = $true }
              if ($isLocal) {
                $cfg = Get-WinEvent -ListLog $logName -ErrorAction Stop
              } else {
                $cfg = Get-WinEvent -ListLog $logName -ComputerName $computer -ErrorAction Stop
              }
              @{ ok = $true; enabled = [bool]$cfg.IsEnabled; message = ('IsEnabled=' + $cfg.IsEnabled) } | ConvertTo-Json -Compress
            } catch {
              @{ ok = $false; enabled = $false; message = $_.Exception.Message } | ConvertTo-Json -Compress
            }
            """;

        try
        {
            var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            return ParseStatusJson(output, computerName);
        }
        catch (Exception ex)
        {
            return new AnalyticalLogStatus
            {
                Target = computerName,
                Ok = false,
                AnalyticalEnabled = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Pulls recent DNS QUERY_RECEIVED (256) analytical events newer than <paramref name="sinceUtc"/>.
    /// </summary>
    public async Task<IReadOnlyList<DnsQueryEventDto>> FetchQueryEventsAsync(
        string computerName,
        DateTimeOffset? sinceUtc,
        int maxEvents = 500,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        maxEvents = Math.Clamp(maxEvents, 1, 2000);
        var sinceLine = sinceUtc is null
            ? "$since = $null"
            : $"$since = [datetime]::Parse('{sinceUtc.Value.UtcDateTime:O}', $null, [System.Globalization.DateTimeStyles]::RoundtripKind).ToUniversalTime()";

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $computer = '{{EscapePsString(computerName)}}'
            $logName = 'Microsoft-Windows-DNSServer/Analytical'
            $max = {{maxEvents}}
            {{sinceLine}}
            $isLocal = $false
            $localNames = @($env:COMPUTERNAME, [System.Net.Dns]::GetHostName())
            if ($localNames -contains $computer) { $isLocal = $true }

            function Get-DnsQueryPayload($evt) {
              $query = $null
              $client = $null
              try {
                $xml = [xml]$evt.ToXml()
                foreach ($d in $xml.Event.EventData.Data) {
                  $n = [string]$d.Name
                  $v = [string]$d.'#text'
                  if ([string]::IsNullOrWhiteSpace($v)) { continue }
                  if ($n -match 'QNAME|QueryName|Name') { $query = $v }
                  elseif ($n -match 'Source|Client|RemoteIP|SourceIP') { $client = $v }
                }
              } catch { }
              if (-not $query -and $evt.Properties.Count -gt 0) {
                foreach ($p in $evt.Properties) {
                  $v = [string]$p.Value
                  if ($v -match '^[a-zA-Z0-9._*-]+\.[a-zA-Z]{2,}$') { $query = $v; break }
                }
              }
              return @{ Query = $query; Client = $client }
            }

            try {
              if ($isLocal) {
                $events = @(Get-WinEvent -LogName $logName -MaxEvents $max -ErrorAction Stop)
              } else {
                $events = @(Get-WinEvent -ComputerName $computer -LogName $logName -MaxEvents $max -ErrorAction Stop)
              }
              $rows = @()
              foreach ($e in $events) {
                # 256 = QUERY_RECEIVED (analytical)
                if ($e.Id -ne 256 -and $e.Id -ne 257) { continue }
                $ts = $e.TimeCreated.ToUniversalTime()
                if ($null -ne $since -and $ts -le $since) { continue }
                $payload = Get-DnsQueryPayload $e
                if ([string]::IsNullOrWhiteSpace($payload.Query)) { continue }
                $rows += [pscustomobject]@{
                  ts = $ts.ToString('o')
                  query = ([string]$payload.Query).TrimEnd('.')
                  client = $payload.Client
                  recordId = [long]$e.RecordId
                }
              }
              if ($rows.Count -eq 0) { '[]' } else { $rows | ConvertTo-Json -Compress }
            } catch {
              Write-Error $_.Exception.Message
              exit 1
            }
            """;

        var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        return ParseEventsJson(output);
    }

    public async Task<BlockTestResult> TestBlockResolutionAsync(
        string computerName,
        string domain,
        bool inBlocklist,
        CancellationToken cancellationToken = default)
    {
        var normalized = domain.Trim().Trim('.').ToLowerInvariant();
        if (!OperatingSystem.IsWindows())
        {
            return new BlockTestResult
            {
                Target = computerName,
                Domain = normalized,
                InBlocklist = inBlocklist,
                ProbeOk = false,
                AppearsBlocked = false,
                Error = "Block test requires Windows Resolve-DnsName against the DNS target.",
                Guidance = "Run the hub on Windows Server and ensure the client uses this DNS server."
            };
        }

        var script = $$"""
            $ErrorActionPreference = 'Continue'
            $server = '{{EscapePsString(computerName)}}'
            $name = '{{EscapePsString(normalized)}}'
            $summary = ''
            $blocked = $false
            $ok = $true
            try {
              $answers = @(Resolve-DnsName -Name $name -Server $server -Type A -DnsOnly -ErrorAction Stop)
              $addrs = @($answers | Where-Object { $_.Type -eq 'A' -or $_.IPAddress } | ForEach-Object {
                if ($_.IPAddress) { $_.IPAddress.ToString() } else { $_.ToString() }
              })
              if ($addrs.Count -gt 0) {
                $summary = 'RESOLVED ' + ($addrs -join ',')
                $blocked = $false
              } else {
                $summary = 'NO_A_RECORD'
                $blocked = $true
              }
            } catch {
              $msg = $_.Exception.Message
              $summary = $msg
              if ($msg -match 'NXDOMAIN|NameError|DNS_ERROR_RCODE_NAME_ERROR|not exist|REFUSED|SERVFAIL|ServerFailure|timed out|Timeout') {
                $blocked = $true
              } else {
                $ok = $false
              }
            }
            @{ ok = $ok; blocked = $blocked; summary = $summary } | ConvertTo-Json -Compress
            """;

        try
        {
            var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(ExtractJson(output));
            var root = doc.RootElement;
            var probeOk = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            var appearsBlocked = root.TryGetProperty("blocked", out var bEl) && bEl.GetBoolean();
            var summary = root.TryGetProperty("summary", out var sEl) ? sEl.GetString() : null;

            var guidance = BuildBlockGuidance(inBlocklist, appearsBlocked, computerName, normalized);
            return new BlockTestResult
            {
                Target = computerName,
                Domain = normalized,
                InBlocklist = inBlocklist,
                ProbeOk = probeOk,
                AppearsBlocked = appearsBlocked,
                ResolutionSummary = summary,
                Guidance = guidance
            };
        }
        catch (Exception ex)
        {
            return new BlockTestResult
            {
                Target = computerName,
                Domain = normalized,
                InBlocklist = inBlocklist,
                ProbeOk = false,
                AppearsBlocked = false,
                Error = ex.Message,
                Guidance = BuildBlockGuidance(inBlocklist, false, computerName, normalized)
            };
        }
    }

    public static string BuildBlockGuidance(
        bool inBlocklist,
        bool appearsBlocked,
        string target,
        string domain)
    {
        if (!inBlocklist)
            return $"« {domain} » is not in the hub blocklist — sync lists or pick a listed domain.";

        if (appearsBlocked)
            return $"QRP appears effective on {target} for {domain} (REFUSED/NXDOMAIN/timeout). " +
                   "If a phone still opens the site, it is not using this DNS (DoH / another resolver).";

        return $"Domain is on the blocklist but {target} still resolves it. " +
               "Re-sync QRP (Update now), clear DNS server cache, confirm policies are Enabled, " +
               "and set the client DNS to only this server (no 8.8.8.8 / DoH). " +
               "DENY returns REFUSED — some clients fail over to the next DNS; prefer IGNORE or a single DNS.";
    }

    private static AnalyticalLogStatus ParseStatusJson(string output, string computerName)
    {
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(output));
            var root = doc.RootElement;
            return new AnalyticalLogStatus
            {
                Target = computerName,
                Ok = root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                AnalyticalEnabled = root.TryGetProperty("enabled", out var en) && en.GetBoolean(),
                Message = root.TryGetProperty("message", out var msg) ? msg.GetString() : null
            };
        }
        catch
        {
            return new AnalyticalLogStatus
            {
                Target = computerName,
                Ok = false,
                AnalyticalEnabled = false,
                Message = string.IsNullOrWhiteSpace(output) ? "Empty PowerShell response." : output.Trim()
            };
        }
    }

    private static IReadOnlyList<DnsQueryEventDto> ParseEventsJson(string output)
    {
        var json = ExtractJson(output);
        if (string.IsNullOrWhiteSpace(json) || json == "[]")
            return [];

        using var doc = JsonDocument.Parse(json);
        var list = new List<DnsQueryEventDto>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            AddEvent(list, doc.RootElement);
            return list;
        }

        foreach (var el in doc.RootElement.EnumerateArray())
            AddEvent(list, el);
        return list;
    }

    private static void AddEvent(List<DnsQueryEventDto> list, JsonElement el)
    {
        var query = el.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
            return;

        DateTimeOffset ts = DateTimeOffset.UtcNow;
        if (el.TryGetProperty("ts", out var tsEl) && tsEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(tsEl.GetString(), out var parsed))
            ts = parsed.ToUniversalTime();

        long? recordId = null;
        if (el.TryGetProperty("recordId", out var idEl) && idEl.TryGetInt64(out var id))
            recordId = id;

        list.Add(new DnsQueryEventDto
        {
            TimestampUtc = ts,
            QueryName = query.Trim().Trim('.').ToLowerInvariant(),
            ClientIp = el.TryGetProperty("client", out var c) ? c.GetString() : null,
            RecordId = recordId
        });
    }

    private static string ExtractJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "{}";
        output = output.Trim();
        var arr = output.LastIndexOf('[');
        var obj = output.LastIndexOf('{');
        if (arr < 0 && obj < 0)
            return output;
        if (arr >= 0 && (obj < 0 || arr < obj))
            return output[arr..];
        return output[obj..];
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
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"PowerShell exited with code {process.ExitCode}"
                : err);
        }

        return stdout.ToString();
    }

    private static string EscapePsString(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
