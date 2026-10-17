using System.Text.Json;
using System.Text.Json.Serialization;
using DnsBlocker.Core.Models;
using DnsBlocker.Core.Stats;
using DnsBlocker.Core.Updates;

namespace DnsBlocker.Core.Ipc;

public static class IpcProtocol
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public const string MethodGetStatus = "GetStatus";
    public const string MethodGetConfig = "GetConfig";
    public const string MethodSetConfig = "SetConfig";
    public const string MethodApplyNow = "ApplyNow";
    public const string MethodSearchDomains = "SearchDomains";
    public const string MethodGetStats = "GetStats";
    public const string MethodEnableStatsCollection = "EnableStatsCollection";
    public const string MethodTestDnsBlock = "TestDnsBlock";
    public const string MethodGetTargetPolicyStatus = "GetTargetPolicyStatus";
    public const string MethodGetAppVersion = "GetAppVersion";
    public const string MethodCheckForUpdates = "CheckForUpdates";
    public const string MethodInstallUpdate = "InstallUpdate";
    public const string MethodTestTargetWinRm = "TestTargetWinRm";
    public const string MethodPrepareDnsTarget = "PrepareDnsTarget";
    public const string MethodEnsureLocalDnsTarget = "EnsureLocalDnsTarget";
    public const string MethodGetPreparedTargets = "GetPreparedTargets";
    public const string MethodClearTargetCredentials = "ClearTargetCredentials";
}

public sealed class IpcRequest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Method { get; set; } = "";
    public JsonElement? Params { get; set; }
}

public sealed class IpcResponse
{
    public string Id { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonElement? Result { get; set; }

    public static IpcResponse Success(string id, object? result)
    {
        var element = JsonSerializer.SerializeToElement(result, IpcProtocol.JsonOptions);
        return new IpcResponse { Id = id, Ok = true, Result = element };
    }

    public static IpcResponse Failure(string id, string? error) =>
        new()
        {
            Id = id,
            Ok = false,
            Error = string.IsNullOrWhiteSpace(error)
                ? "IPC failure (empty error message)."
                : error.Trim()
        };
}

public sealed class SearchDomainsParams
{
    public string? Query { get; set; }
    public int Limit { get; set; } = 100;
}

public sealed class GetStatsParams
{
    /// <summary>Hours lookback, default 24.</summary>
    public int Hours { get; set; } = 24;

    /// <summary>Top-N domains/clients (10 or 20 typical). Default 20.</summary>
    public int TopN { get; set; } = 20;
}

public sealed class SetConfigParams
{
    public AppConfig? Config { get; set; }
}

public sealed class InstallUpdateParams
{
    /// <summary>When true (default for service), msiexec /qn. When false, /qb.</summary>
    public bool Silent { get; set; } = true;

    /// <summary>Optional manifest from a prior CheckForUpdates; otherwise the feed is re-fetched.</summary>
    public UpdateManifest? Manifest { get; set; }
}

public sealed class AppVersionResult
{
    public string Version { get; set; } = "";
}

public sealed class ClearTargetCredentialsParams
{
    public string Hostname { get; set; } = "";
}
