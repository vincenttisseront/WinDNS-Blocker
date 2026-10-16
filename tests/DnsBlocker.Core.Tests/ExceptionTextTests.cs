using DnsBlocker.Core.Diagnostics;
using DnsBlocker.Core.Ipc;

namespace DnsBlocker.Core.Tests;

public class ExceptionTextTests
{
    [Fact]
    public void FormatUserFacing_empty_message_includes_type_not_blank()
    {
        var ex = new InvalidOperationException("");
        var text = ExceptionText.FormatUserFacing(ex);
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("empty Exception.Message", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatUserFacing_null_is_not_blank()
    {
        var text = ExceptionText.FormatUserFacing(null);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void FormatUserFacing_includes_inner_exception()
    {
        var inner = new InvalidOperationException("");
        var outer = new InvalidOperationException("outer wrapper", inner);
        var text = ExceptionText.FormatUserFacing(outer);
        Assert.Contains("outer wrapper", text, StringComparison.Ordinal);
        Assert.Contains("empty Exception.Message", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatUserFacing_flattens_aggregate()
    {
        var agg = new AggregateException(
            new InvalidOperationException(""),
            new ArgumentException("second"));
        var text = ExceptionText.FormatUserFacing(agg);
        Assert.Contains("InvalidOperationException", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
        Assert.DoesNotContain("One or more errors occurred", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatForLog_includes_ToString()
    {
        var ex = new InvalidOperationException("boom");
        var log = ExceptionText.FormatForLog(ex);
        Assert.Contains("Exception.ToString()", log, StringComparison.Ordinal);
        Assert.Contains("boom", log, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
    }

    [Fact]
    public void NonEmptyMessage_whitespace_falls_back_to_type()
    {
        var msg = ExceptionText.NonEmptyMessage(new InvalidOperationException("   "));
        Assert.False(string.IsNullOrWhiteSpace(msg));
        Assert.Contains("InvalidOperationException", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void IpcResponse_Failure_rejects_blank_error()
    {
        var r = IpcResponse.Failure("id1", "");
        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }

    [Fact]
    public void IpcResponse_Failure_rejects_whitespace_error()
    {
        var r = IpcResponse.Failure("id1", "   ");
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
    }
}
