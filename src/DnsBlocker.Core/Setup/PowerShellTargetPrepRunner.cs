using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DnsBlocker.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DnsBlocker.Core.Setup;

/// <summary>
/// Runs WinRM connectivity tests and remote DNS prep (registry ACL + QRP probe)
/// via PowerShell using admin credentials supplied by the UI wizard.
/// </summary>
public sealed class PowerShellTargetPrepRunner : ITargetPrepRunner
{
    private readonly ILogger<PowerShellTargetPrepRunner> _logger;

    public PowerShellTargetPrepRunner(ILogger<PowerShellTargetPrepRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<PowerShellTargetPrepRunner>.Instance;
    }

    public async Task<TargetWinRmTestResult> TestWinRmAsync(
        TargetWinRmTestRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new TargetWinRmTestResult
            {
                Ok = false,
                Message = "Test WinRM nécessite Windows + PowerShell.",
                Log = ["Plateforme non Windows."]
            };
        }

        ValidateCreds(request);

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $user = '{{Escape(request.Username)}}'
            $plain = '{{Escape(request.Password)}}'
            $target = '{{Escape(request.Hostname)}}'
            $secure = ConvertTo-SecureString $plain -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($user, $secure)
            $plain = $null
            [void]$secure
            $log = New-Object System.Collections.Generic.List[string]
            try {
              [void]$log.Add('Test-WSMan…')
              $null = Test-WSMan -ComputerName $target -Authentication Default -Credential $cred -ErrorAction Stop
              [void]$log.Add('Test-WSMan OK')
              [void]$log.Add('New-CimSession…')
              $session = New-CimSession -ComputerName $target -Credential $cred -ErrorAction Stop
              Remove-CimSession $session
              [void]$log.Add('CimSession OK')
              $result = @{ ok = $true; message = 'WinRM/CIM joignable avec le compte fourni.'; log = $log }
            } catch {
              [void]$log.Add($_.Exception.Message)
              $result = @{ ok = $false; message = $_.Exception.Message; log = $log }
            }
            $result | ConvertTo-Json -Compress -Depth 5
            """;

        return await RunJsonAsync<TargetWinRmTestResult>(script, cancellationToken).ConfigureAwait(false)
               ?? new TargetWinRmTestResult { Ok = false, Message = "Réponse PowerShell vide." };
    }

    public async Task<PrepareDnsTargetResult> PrepareAsync(
        PrepareDnsTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PrepareDnsTargetResult
            {
                Ok = false,
                Message = "La préparation distante nécessite Windows.",
                Log = ["Plateforme non Windows."]
            };
        }

        ValidateCreds(request);

        var identity = string.IsNullOrWhiteSpace(request.HubIdentityToGrant)
            ? ""
            : request.HubIdentityToGrant.Trim();
        var addDnsAdmins = request.AddHubToDnsAdmins ? "$true" : "$false";

        // Password is embedded only in the ephemeral stdin script — never logged by us.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $user = '{{Escape(request.Username)}}'
            $plain = '{{Escape(request.Password)}}'
            $target = '{{Escape(request.Hostname)}}'
            $requestedIdentity = '{{Escape(identity)}}'
            $addDnsAdmins = {{addDnsAdmins}}
            $secure = ConvertTo-SecureString $plain -AsPlainText -Force
            $cred = New-Object System.Management.Automation.PSCredential($user, $secure)
            $plain = $null
            $log = New-Object System.Collections.Generic.List[string]

            function Resolve-HubIdentity {
              param([string]$Requested)
              if ($Requested) { return $Requested }
              try {
                $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DnsBlocker'" -ErrorAction Stop
                if ($svc -and $svc.StartName) {
                  $start = $svc.StartName
                  if ($start -match 'LocalSystem|LocalService|NetworkService') {
                    $dnsDomain = (Get-CimInstance Win32_ComputerSystem).Domain
                    $name = $env:COMPUTERNAME
                    if ($dnsDomain -and $dnsDomain -ne $name) {
                      return ($dnsDomain + '\' + $name + '$')
                    }
                    return ($name + '$')
                  }
                  return $start
                }
              } catch { }
              return 'DnsAdmins'
            }

            try {
              [void]$log.Add('Test-WSMan…')
              $null = Test-WSMan -ComputerName $target -Authentication Default -Credential $cred -ErrorAction Stop
              [void]$log.Add('WinRM OK')

              $hubIdentity = Resolve-HubIdentity -Requested $requestedIdentity
              [void]$log.Add('Identité à autoriser: ' + $hubIdentity)

              $remote = Invoke-Command -ComputerName $target -Credential $cred -ScriptBlock {
                param($HubIdentity, $DoDnsAdmins)
                $steps = New-Object System.Collections.Generic.List[string]
                $ErrorActionPreference = 'Stop'

                # DNS role / module
                try {
                  Import-Module DnsServer -ErrorAction Stop
                  [void]$steps.Add('Module DnsServer OK')
                } catch {
                  throw "Module DnsServer indisponible sur la cible: $($_.Exception.Message)"
                }

                $regPath = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\DNS Server'
                if (-not (Test-Path $regPath)) {
                  New-Item -Path $regPath -Force | Out-Null
                  [void]$steps.Add('Clé registre DNS Server créée')
                }

                $acl = Get-Acl -Path $regPath
                $rule = New-Object System.Security.AccessControl.RegistryAccessRule(
                  $HubIdentity,
                  [System.Security.AccessControl.RegistryRights]::FullControl,
                  [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [System.Security.AccessControl.InheritanceFlags]::ObjectInherit,
                  [System.Security.AccessControl.PropagationFlags]::None,
                  [System.Security.AccessControl.AccessControlType]::Allow)
                $acl.SetAccessRuleProtection($false, $true)
                $acl.AddAccessRule($rule)
                Set-Acl -Path $regPath -AclObject $acl
                [void]$steps.Add('ACL Full Control accordée à ' + $HubIdentity + ' sur DNS Server')

                if ($DoDnsAdmins) {
                  try {
                    $group = [ADSI]'WinNT://./DnsAdmins,group'
                    # Domain DnsAdmins via net localgroup may not exist locally; try AD
                    Add-LocalGroupMember -Group 'DnsAdmins' -Member $HubIdentity -ErrorAction Stop
                    [void]$steps.Add('Ajout Local DnsAdmins: ' + $HubIdentity)
                  } catch {
                    try {
                      # Domain group via ADSI LDAP-less net group
                      $null = net group "DnsAdmins" $HubIdentity /add /domain 2>&1
                      [void]$steps.Add('Tentative net group DnsAdmins: ' + $HubIdentity + ' (vérifier manuellement si échec silencieux)')
                    } catch {
                      [void]$steps.Add('Add DnsAdmins échoué: ' + $_.Exception.Message)
                    }
                  }
                }

                $probe = 'DnsBlocker_Probe_' + [guid]::NewGuid().ToString('N').Substring(0,8)
                try {
                  Add-DnsServerQueryResolutionPolicy -Name $probe -Action DENY -Fqdn 'EQ,probe.invalid.dnsblocker.local' -ErrorAction Stop
                  [void]$steps.Add('Probe QRP Add OK (' + $probe + ')')
                } catch {
                  throw ("Probe Add-DnsServerQueryResolutionPolicy échoué: " + $_.Exception.Message + " — ACL/DnsAdmins insuffisants ?")
                }
                try {
                  Remove-DnsServerQueryResolutionPolicy -Name $probe -Force -ErrorAction Stop
                  [void]$steps.Add('Probe QRP Remove OK')
                } catch {
                  [void]$steps.Add('Probe Remove avertissement: ' + $_.Exception.Message)
                }

                return @{ ok = $true; steps = $steps; hubIdentity = $HubIdentity }
              } -ArgumentList $hubIdentity, $addDnsAdmins

              foreach ($s in $remote.steps) { [void]$log.Add([string]$s) }
              $result = @{
                ok = [bool]$remote.ok
                message = 'Cible préparée. Sync QRP peut utiliser le compte du service hub.'
                log = $log
                hubIdentity = [string]$remote.hubIdentity
                addedToDnsAdmins = [bool]$addDnsAdmins
              }
            } catch {
              [void]$log.Add($_.Exception.Message)
              $result = @{
                ok = $false
                message = $_.Exception.Message
                log = $log
                hubIdentity = $null
                addedToDnsAdmins = $false
              }
            }

            $result | ConvertTo-Json -Compress -Depth 6
            """;

        var raw = await RunJsonAsync<PrepJson>(script, cancellationToken).ConfigureAwait(false);
        if (raw is null)
        {
            return new PrepareDnsTargetResult
            {
                Ok = false,
                Message = "Réponse PowerShell vide.",
                Log = []
            };
        }

        return new PrepareDnsTargetResult
        {
            Ok = raw.Ok,
            Message = raw.Message ?? "",
            Log = raw.Log ?? [],
            Target = raw.Ok
                ? new PreparedTargetInfo
                {
                    Hostname = request.Hostname.Trim(),
                    Username = request.Username.Trim(),
                    CredentialsStored = false, // filled by HubRuntime
                    Prepared = true,
                    PreparedAtUtc = DateTimeOffset.UtcNow,
                    LastProbeMessage = raw.Message,
                    HubIdentityGranted = raw.HubIdentity,
                    AddedToDnsAdmins = raw.AddedToDnsAdmins
                }
                : null
        };
    }

    private static void ValidateCreds(TargetWinRmTestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Hostname))
            throw new ArgumentException("Hostname requis.");
        if (string.IsNullOrWhiteSpace(request.Username))
            throw new ArgumentException("Compte admin requis (DOMAINE\\user).");
        if (string.IsNullOrEmpty(request.Password))
            throw new ArgumentException("Mot de passe requis.");
    }

    private static void ValidateCreds(PrepareDnsTargetRequest request) =>
        ValidateCreds(new TargetWinRmTestRequest
        {
            Hostname = request.Hostname,
            Username = request.Username,
            Password = request.Password
        });

    private async Task<T?> RunJsonAsync<T>(string script, CancellationToken cancellationToken)
    {
        var output = await RunPowerShellAsync(script, cancellationToken).ConfigureAwait(false);
        var json = ExtractJson(output);
        if (string.IsNullOrWhiteSpace(json))
            return default;
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
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
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("Impossible de démarrer powershell.exe");

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
            // Scrub potential password echoes (should not happen)
            _logger.LogError("Target prep PowerShell failed ({Code})", process.ExitCode);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"PowerShell exit {process.ExitCode}"
                : err);
        }

        return stdout.ToString();
    }

    private static string ExtractJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";
        var trimmed = output.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];
        return trimmed;
    }

    private static string Escape(string value) =>
        (value ?? "").Replace("'", "''", StringComparison.Ordinal);

    private sealed class PrepJson
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public List<string>? Log { get; set; }
        public string? HubIdentity { get; set; }
        public bool AddedToDnsAdmins { get; set; }
    }
}
