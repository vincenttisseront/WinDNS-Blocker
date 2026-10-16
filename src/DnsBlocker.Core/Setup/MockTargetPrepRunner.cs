using DnsBlocker.Core.Models;

namespace DnsBlocker.Core.Setup;

/// <summary>In-memory prep runner for unit tests / non-Windows.</summary>
public sealed class MockTargetPrepRunner : ITargetPrepRunner
{
    public bool FailWinRm { get; set; }
    public bool FailPrep { get; set; }
    public string LastHostname { get; private set; } = "";
    public string LastUsername { get; private set; } = "";
    public bool SawPassword => !string.IsNullOrEmpty(_lastPassword);
    private string _lastPassword = "";

    public Task<TargetWinRmTestResult> TestWinRmAsync(TargetWinRmTestRequest request, CancellationToken cancellationToken = default)
    {
        LastHostname = request.Hostname;
        LastUsername = request.Username;
        _lastPassword = request.Password;
        if (FailWinRm)
        {
            return Task.FromResult(new TargetWinRmTestResult
            {
                Ok = false,
                Message = "Mock WinRM failure",
                Log = ["Test-WSMan failed (mock)"]
            });
        }

        return Task.FromResult(new TargetWinRmTestResult
        {
            Ok = true,
            Message = "WinRM/CIM OK (mock)",
            Log = ["Test-WSMan OK", "CimSession OK"]
        });
    }

    public Task<PrepareDnsTargetResult> PrepareAsync(PrepareDnsTargetRequest request, CancellationToken cancellationToken = default)
    {
        LastHostname = request.Hostname;
        LastUsername = request.Username;
        _lastPassword = request.Password;
        if (FailPrep)
        {
            return Task.FromResult(new PrepareDnsTargetResult
            {
                Ok = false,
                Message = "Mock prep failure",
                Log = ["ACL failed (mock)"]
            });
        }

        var identity = string.IsNullOrWhiteSpace(request.HubIdentityToGrant) ? "DnsAdmins" : request.HubIdentityToGrant!;
        return Task.FromResult(new PrepareDnsTargetResult
        {
            Ok = true,
            Message = "Cible préparée (mock).",
            Log =
            [
                "WinRM OK",
                $"ACL Full Control → {identity}",
                "Probe QRP Add OK",
                "Probe QRP Remove OK"
            ],
            Target = new PreparedTargetInfo
            {
                Hostname = request.Hostname.Trim(),
                Username = request.Username.Trim(),
                Prepared = true,
                PreparedAtUtc = DateTimeOffset.UtcNow,
                HubIdentityGranted = identity,
                AddedToDnsAdmins = request.AddHubToDnsAdmins,
                LastProbeMessage = "OK"
            }
        });
    }

    public Task<PrepareDnsTargetResult> PrepareLocalAsync(
        string hostname,
        CancellationToken cancellationToken = default)
    {
        LastHostname = hostname;
        LastUsername = "(local)";
        _lastPassword = "";
        if (FailPrep)
        {
            return Task.FromResult(new PrepareDnsTargetResult
            {
                Ok = false,
                Message = "Mock local prep failure",
                Log = ["Local ACL failed (mock)"]
            });
        }

        return Task.FromResult(new PrepareDnsTargetResult
        {
            Ok = true,
            Message = "Local DNS prepared (mock, no WinRM).",
            Log =
            [
                "Local path (no WinRM)",
                "Module DnsServer OK",
                "Probe QRP Add OK",
                "Probe QRP Remove OK"
            ],
            Target = new PreparedTargetInfo
            {
                Hostname = hostname.Trim(),
                Username = null,
                Prepared = true,
                PreparedAtUtc = DateTimeOffset.UtcNow,
                HubIdentityGranted = "LocalSystem",
                AddedToDnsAdmins = false,
                LastProbeMessage = "OK"
            }
        });
    }
}
