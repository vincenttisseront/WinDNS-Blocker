namespace DnsBlocker.Core.Models;

public sealed class PreparedTargetInfo
{
    public required string Hostname { get; set; }
    public string? Username { get; set; }
    public bool CredentialsStored { get; set; }
    public bool Prepared { get; set; }
    public DateTimeOffset? PreparedAtUtc { get; set; }
    public string? LastProbeMessage { get; set; }
    public string? HubIdentityGranted { get; set; }
    public bool AddedToDnsAdmins { get; set; }
}

public sealed class TargetWinRmTestRequest
{
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Password sent once over local named pipe — never logged.</summary>
    public string Password { get; set; } = "";
}

public sealed class TargetWinRmTestResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Log { get; set; } = [];
}

public sealed class PrepareDnsTargetRequest
{
    public string Hostname { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Identity to grant registry Full Control (e.g. DnsAdmins, DOMAIN\hub$). Empty = auto-detect hub service account then DnsAdmins.</summary>
    public string? HubIdentityToGrant { get; set; }
    /// <summary>Also add hub identity to domain DnsAdmins (elevated; document risk).</summary>
    public bool AddHubToDnsAdmins { get; set; }
    /// <summary>Persist username+password via DPAPI LocalMachine under ProgramData\DnsBlocker\secrets.</summary>
    public bool PersistCredentials { get; set; } = true;
}

public sealed class PrepareDnsTargetResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public List<string> Log { get; set; } = [];
    public PreparedTargetInfo? Target { get; set; }
}

/// <summary>
/// Result of detecting / adding / preparing the hub's own DNS Server (local, no WinRM).
/// </summary>
public sealed class EnsureLocalDnsTargetResult
{
    public bool Ok { get; set; }
    public bool DnsServerDetected { get; set; }
    public string? Hostname { get; set; }
    public string Message { get; set; } = "";
    public List<string> Log { get; set; } = [];
    public bool AddedToFarm { get; set; }
    public bool Prepared { get; set; }
}
