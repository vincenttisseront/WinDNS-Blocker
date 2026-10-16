using DnsBlocker.Core.Models;
using DnsBlocker.Core.Setup;

namespace DnsBlocker.Core.Tests;

public class WinRmEmptyResponseTests
{
    [Fact]
    public void MapWinRmResult_EmptyStdout_SurfacesStderrAndActionableFrench()
    {
        var run = new PowerShellRunResult
        {
            ExitCode = 1,
            Stdout = "",
            Stderr = "Connecting to remote server failed with WinRM error Access is denied."
        };

        var result = PowerShellTargetPrepRunner.MapWinRmResult(run);

        Assert.False(result.Ok);
        Assert.DoesNotContain("Réponse PowerShell vide", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stderr", string.Join('\n', result.Log), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Accès refusé", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Enable-PSRemoting", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapWinRmResult_ValidJsonFailure_EnrichesAuthTips()
    {
        var run = new PowerShellRunResult
        {
            ExitCode = 2,
            Stdout = """{"ok":false,"message":"Logon failure: unknown user name or bad password.","log":["Test-WSMan (Negotiate)…"]}""",
            Stderr = ""
        };

        var result = PowerShellTargetPrepRunner.MapWinRmResult(run);
        Assert.False(result.Ok);
        Assert.Contains("Authentification", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DOMAINE", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MapWinRmResult_ValidJsonOk()
    {
        var run = new PowerShellRunResult
        {
            ExitCode = 0,
            Stdout = """{"ok":true,"message":"WinRM/CIM joignable","log":["Test-WSMan OK","CimSession OK"]}""",
            Stderr = ""
        };

        var result = PowerShellTargetPrepRunner.MapWinRmResult(run);
        Assert.True(result.Ok);
        Assert.Equal("WinRM/CIM joignable", result.Message);
    }

    [Fact]
    public void BuildEmptyResponseMessage_BothStreamsEmpty()
    {
        var msg = PowerShellTargetPrepRunner.BuildEmptyResponseMessage(new PowerShellRunResult
        {
            ExitCode = -1,
            Stdout = "",
            Stderr = ""
        });
        Assert.Contains("stdout et stderr vides", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Réponse PowerShell vide", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void MapPrepResult_Empty_HasLogExitCode()
    {
        var result = PowerShellTargetPrepRunner.MapPrepResult(
            new PowerShellRunResult
            {
                ExitCode = 1,
                Stdout = "not-json-at-all",
                Stderr = "ParserError: unexpected token"
            },
            new PrepareDnsTargetRequest { Hostname = "dns1.lab", Username = @"LAB\a", Password = "x" });

        Assert.False(result.Ok);
        Assert.Contains(result.Log, l => l.Contains("exitCode=1", StringComparison.Ordinal));
        Assert.Contains("ParserError", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
