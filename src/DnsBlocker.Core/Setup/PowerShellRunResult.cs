namespace DnsBlocker.Core.Setup;

public sealed class PowerShellRunResult
{
    public int ExitCode { get; init; }
    public string Stdout { get; init; } = "";
    public string Stderr { get; init; } = "";
}
