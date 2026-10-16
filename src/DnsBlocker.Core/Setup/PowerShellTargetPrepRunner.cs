using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Credentials via process env (not interpolated into script) to avoid empty/broken scripts
        // when passwords contain quotes, $, backticks, etc.
        var script = """
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $log = New-Object System.Collections.Generic.List[string]
            function Emit($ok, $message) {
              $obj = [ordered]@{ ok = [bool]$ok; message = [string]$message; log = @($log) }
              $json = $obj | ConvertTo-Json -Compress -Depth 6
              [Console]::Out.WriteLine($json)
            }
            try {
              $user = $env:DNSBLOCKER_WINRM_USER
              $plain = $env:DNSBLOCKER_WINRM_PASS
              $target = $env:DNSBLOCKER_WINRM_TARGET
              if ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($target)) {
                throw 'Variables DNSBLOCKER_WINRM_USER / TARGET manquantes.'
              }
              if ($null -eq $plain) { throw 'Variable DNSBLOCKER_WINRM_PASS manquante.' }

              [void]$log.Add('Création PSCredential…')
              $secure = ConvertTo-SecureString $plain -AsPlainText -Force
              $cred = New-Object System.Management.Automation.PSCredential($user, $secure)
              Remove-Variable plain -ErrorAction SilentlyContinue

              [void]$log.Add('Test-WSMan (Negotiate)…')
              $null = Test-WSMan -ComputerName $target -Authentication Negotiate -Credential $cred -ErrorAction Stop
              [void]$log.Add('Test-WSMan OK')

              [void]$log.Add('New-CimSession (Negotiate)…')
              $session = New-CimSession -ComputerName $target -Credential $cred -Authentication Negotiate -ErrorAction Stop
              Remove-CimSession $session
              [void]$log.Add('CimSession OK')

              Emit $true 'WinRM/CIM joignable avec le compte fourni (Negotiate).'
              exit 0
            } catch {
              $msg = $_.Exception.Message
              if ($_.FullyQualifiedErrorId) { $msg = $msg + ' | FQID=' + $_.FullyQualifiedErrorId }
              [void]$log.Add($msg)
              Emit $false $msg
              exit 2
            }
            """;

        var run = await RunScriptAsync(script, request.Hostname, request.Username, request.Password, cancellationToken)
            .ConfigureAwait(false);

        return MapWinRmResult(run);
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

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
            $log = New-Object System.Collections.Generic.List[string]
            $requestedIdentity = '{{Escape(identity)}}'
            $addDnsAdmins = {{addDnsAdmins}}

            function Emit($ok, $message, $hubIdentity, $added) {
              $obj = [ordered]@{
                ok = [bool]$ok
                message = [string]$message
                log = @($log)
                hubIdentity = $hubIdentity
                addedToDnsAdmins = [bool]$added
              }
              [Console]::Out.WriteLine(($obj | ConvertTo-Json -Compress -Depth 8))
            }

            function Resolve-HubIdentity([string]$Requested) {
              if ($Requested) { return $Requested }
              try {
                $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='DnsBlocker'" -ErrorAction Stop
                if ($svc -and $svc.StartName) {
                  $start = $svc.StartName
                  if ($start -match 'LocalSystem|LocalService|NetworkService|\.\\LocalSystem') {
                    $cs = Get-CimInstance Win32_ComputerSystem
                    $dnsDomain = $cs.Domain
                    $name = $env:COMPUTERNAME
                    if ($cs.PartOfDomain -and $dnsDomain) {
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
              $user = $env:DNSBLOCKER_WINRM_USER
              $plain = $env:DNSBLOCKER_WINRM_PASS
              $target = $env:DNSBLOCKER_WINRM_TARGET
              if ([string]::IsNullOrWhiteSpace($user) -or [string]::IsNullOrWhiteSpace($target)) {
                throw 'Variables DNSBLOCKER_WINRM_USER / TARGET manquantes.'
              }
              $secure = ConvertTo-SecureString $plain -AsPlainText -Force
              $cred = New-Object System.Management.Automation.PSCredential($user, $secure)
              Remove-Variable plain -ErrorAction SilentlyContinue

              [void]$log.Add('Test-WSMan (Negotiate)…')
              $null = Test-WSMan -ComputerName $target -Authentication Negotiate -Credential $cred -ErrorAction Stop
              [void]$log.Add('WinRM OK')

              $hubIdentity = Resolve-HubIdentity -Requested $requestedIdentity
              [void]$log.Add('Identité à autoriser: ' + $hubIdentity)

              # Single-hop Invoke-Command with explicit -Credential (no CredSSP required).
              $remote = Invoke-Command -ComputerName $target -Credential $cred -Authentication Negotiate -ScriptBlock {
                param($HubIdentity, $DoDnsAdmins)
                $steps = New-Object System.Collections.Generic.List[string]
                $ErrorActionPreference = 'Stop'

                Import-Module DnsServer -ErrorAction Stop
                [void]$steps.Add('Module DnsServer OK')

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
                $acl.AddAccessRule($rule)
                Set-Acl -Path $regPath -AclObject $acl
                [void]$steps.Add('ACL Full Control accordée à ' + $HubIdentity)

                if ($DoDnsAdmins) {
                  try {
                    Add-LocalGroupMember -Group 'DnsAdmins' -Member $HubIdentity -ErrorAction Stop
                    [void]$steps.Add('Ajout groupe local DnsAdmins: ' + $HubIdentity)
                  } catch {
                    $net = & net group "DnsAdmins" $HubIdentity /add /domain 2>&1 | Out-String
                    [void]$steps.Add('net group DnsAdmins: ' + $net.Trim())
                  }
                }

                $probe = 'DnsBlocker_Probe_' + [guid]::NewGuid().ToString('N').Substring(0,8)
                Add-DnsServerQueryResolutionPolicy -Name $probe -Action DENY -Fqdn 'EQ,probe.invalid.dnsblocker.local' -ErrorAction Stop
                [void]$steps.Add('Probe QRP Add OK (' + $probe + ')')
                Remove-DnsServerQueryResolutionPolicy -Name $probe -Force -ErrorAction Stop
                [void]$steps.Add('Probe QRP Remove OK')

                return @{ ok = $true; steps = $steps; hubIdentity = $HubIdentity }
              } -ArgumentList $hubIdentity, $addDnsAdmins

              foreach ($s in @($remote.steps)) { [void]$log.Add([string]$s) }
              Emit $true 'Cible préparée. Sync QRP peut utiliser le compte du service hub.' ([string]$remote.hubIdentity) ([bool]$addDnsAdmins)
              exit 0
            } catch {
              $msg = $_.Exception.Message
              if ($_.FullyQualifiedErrorId) { $msg = $msg + ' | FQID=' + $_.FullyQualifiedErrorId }
              [void]$log.Add($msg)
              Emit $false $msg $null $false
              exit 2
            }
            """;

        var run = await RunScriptAsync(script, request.Hostname, request.Username, request.Password, cancellationToken)
            .ConfigureAwait(false);

        return MapPrepResult(run, request);
    }

    public static TargetWinRmTestResult MapWinRmResult(PowerShellRunResult run)
    {
        var parsed = TryParseJson<WinRmJson>(run.Stdout);
        var log = new List<string>();
        if (parsed?.Log is { Count: > 0 })
            log.AddRange(parsed.Log);
        else if (!string.IsNullOrWhiteSpace(run.Stdout))
            log.Add("stdout: " + Truncate(run.Stdout, 800));
        if (!string.IsNullOrWhiteSpace(run.Stderr))
            log.Add("stderr: " + Truncate(ScrubSecrets(run.Stderr), 800));
        log.Add($"exitCode={run.ExitCode}");

        if (parsed is not null)
        {
            var msg = EnrichWinRmMessage(parsed.Message ?? "", run);
            return new TargetWinRmTestResult
            {
                Ok = parsed.Ok,
                Message = parsed.Ok ? (parsed.Message ?? "OK") : msg,
                Log = log
            };
        }

        // No JSON — never return a bare empty message
        var fallback = BuildEmptyResponseMessage(run);
        return new TargetWinRmTestResult
        {
            Ok = false,
            Message = fallback,
            Log = log
        };
    }

    public static PrepareDnsTargetResult MapPrepResult(PowerShellRunResult run, PrepareDnsTargetRequest request)
    {
        var parsed = TryParseJson<PrepJson>(run.Stdout);
        var log = new List<string>();
        if (parsed?.Log is { Count: > 0 })
            log.AddRange(parsed.Log);
        else if (!string.IsNullOrWhiteSpace(run.Stdout))
            log.Add("stdout: " + Truncate(run.Stdout, 800));
        if (!string.IsNullOrWhiteSpace(run.Stderr))
            log.Add("stderr: " + Truncate(ScrubSecrets(run.Stderr), 800));
        log.Add($"exitCode={run.ExitCode}");

        if (parsed is not null)
        {
            var ok = parsed.Ok;
            return new PrepareDnsTargetResult
            {
                Ok = ok,
                Message = ok ? (parsed.Message ?? "OK") : EnrichWinRmMessage(parsed.Message ?? "", run),
                Log = log,
                Target = ok
                    ? new PreparedTargetInfo
                    {
                        Hostname = request.Hostname.Trim(),
                        Username = request.Username.Trim(),
                        CredentialsStored = false,
                        Prepared = true,
                        PreparedAtUtc = DateTimeOffset.UtcNow,
                        LastProbeMessage = parsed.Message,
                        HubIdentityGranted = parsed.HubIdentity,
                        AddedToDnsAdmins = parsed.AddedToDnsAdmins
                    }
                    : null
            };
        }

        return new PrepareDnsTargetResult
        {
            Ok = false,
            Message = BuildEmptyResponseMessage(run),
            Log = log
        };
    }

    public static string BuildEmptyResponseMessage(PowerShellRunResult run)
    {
        var parts = new List<string>
        {
            "PowerShell n'a pas renvoyé de JSON exploitable."
        };
        if (run.ExitCode != 0)
            parts.Add($"Code de sortie={run.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(run.Stderr))
            parts.Add("stderr: " + Truncate(ScrubSecrets(run.Stderr), 400));
        else if (!string.IsNullOrWhiteSpace(run.Stdout))
            parts.Add("stdout: " + Truncate(ScrubSecrets(run.Stdout), 400));
        else
            parts.Add("stdout et stderr vides (processus tué, politique ConstrainedLanguage, ou -File illisible sous le compte service).");

        parts.Add(EnrichWinRmMessage("", run));
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public static string EnrichWinRmMessage(string raw, PowerShellRunResult run)
    {
        var blob = $"{raw}\n{run.Stderr}\n{run.Stdout}";
        var tips = new List<string>();

        if (Regex.IsMatch(blob, @"Access is denied|AccessDenied|0x80070005|WIN32 5", RegexOptions.IgnoreCase))
            tips.Add("Accès refusé : vérifiez DOMAINE\\user + mot de passe, et que le compte est admin local/domaine sur la cible.");

        if (Regex.IsMatch(blob, @"WinRM|WSMan|cannot connect|connection refused|RPC server|timed out|no network provider", RegexOptions.IgnoreCase)
            || Regex.IsMatch(blob, @"0x80338012|0x80338126", RegexOptions.IgnoreCase))
            tips.Add("WinRM : sur la cible exécutez `Enable-PSRemoting -Force` et ouvrez le firewall (TCP 5985/5986). Depuis le hub : `Test-WSMan <cible>`.");

        if (Regex.IsMatch(blob, @"logon failure|unknown user name|bad password|0x8007052e", RegexOptions.IgnoreCase))
            tips.Add("Authentification : utilisez DOMAINE\\utilisateur (pas UPN seul si Negotiate échoue) et un mot de passe valide.");

        if (Regex.IsMatch(blob, @"CredSSP|double.?hop|Delegate", RegexOptions.IgnoreCase))
            tips.Add("Double-hop/CredSSP non requis pour ce wizard (PSCredential explicite). Si un message CredSSP apparaît, vérifiez les GPO WinRM.");

        if (Regex.IsMatch(blob, @"ConstrainedLanguage|language mode", RegexOptions.IgnoreCase))
            tips.Add("ConstrainedLanguage : assouplir la stratégie PowerShell pour le compte du service DnsBlocker, ou lancer le service sous un compte domaine.");

        if (Regex.IsMatch(blob, @"LocalSystem|network path|0x800706ba", RegexOptions.IgnoreCase))
            tips.Add("Sous LocalSystem le hub utilise quand même -Credential Negotiate (single-hop). Vérifiez DNS/résolution du FQDN cible.");

        if (tips.Count == 0 && !string.IsNullOrWhiteSpace(raw))
            return raw + " — Vérifiez WinRM (Enable-PSRemoting), firewall 5985/5986, et le format DOMAINE\\user.";

        if (tips.Count == 0)
            return "Échec WinRM/CIM. Vérifiez Enable-PSRemoting sur la cible, firewall 5985/5986, DNS, et DOMAINE\\user.";

        var head = string.IsNullOrWhiteSpace(raw) ? "Échec WinRM/CIM." : raw.Trim();
        return head + " → " + string.Join(" ", tips);
    }

    private async Task<PowerShellRunResult> RunScriptAsync(
        string script,
        string hostname,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "DnsBlocker");
        Directory.CreateDirectory(tempDir);
        var scriptPath = Path.Combine(tempDir, "winrm-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolvePowerShellPath(),
                // -File avoids stdin/-Command - races under services; -EncodedCommand not needed.
                Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["DNSBLOCKER_WINRM_USER"] = username;
            psi.Environment["DNSBLOCKER_WINRM_PASS"] = password;
            psi.Environment["DNSBLOCKER_WINRM_TARGET"] = hostname.Trim();

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new InvalidOperationException("Impossible de démarrer powershell.exe");

            // Avoid BeginOutputReadLine race with WaitForExit (classic empty-stdout bug).
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            _logger.LogInformation(
                "WinRM PowerShell finished exit={Exit} stdoutLen={Out} stderrLen={Err}",
                process.ExitCode, stdout?.Length ?? 0, stderr?.Length ?? 0);

            return new PowerShellRunResult
            {
                ExitCode = process.ExitCode,
                Stdout = stdout ?? "",
                Stderr = stderr ?? ""
            };
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { /* ignore */ }
        }
    }

    private static string ResolvePowerShellPath()
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var candidate = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(candidate))
            return candidate;
        return "powershell.exe";
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

    private static T? TryParseJson<T>(string output)
    {
        var json = ExtractJson(output);
        if (string.IsNullOrWhiteSpace(json))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return default;
        }
    }

    internal static string ExtractJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";
        var trimmed = output.Trim();
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
            return trimmed[start..(end + 1)];
        return "";
    }

    private static string Escape(string value) =>
        (value ?? "").Replace("'", "''", StringComparison.Ordinal);

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static string ScrubSecrets(string value)
    {
        // Best-effort: never echo env password if PS leaked it
        return Regex.Replace(value, @"DNSBLOCKER_WINRM_PASS=\S+", "DNSBLOCKER_WINRM_PASS=***", RegexOptions.IgnoreCase);
    }

    private sealed class WinRmJson
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public List<string>? Log { get; set; }
    }

    private sealed class PrepJson
    {
        public bool Ok { get; set; }
        public string? Message { get; set; }
        public List<string>? Log { get; set; }
        public string? HubIdentity { get; set; }
        public bool AddedToDnsAdmins { get; set; }
    }
}
