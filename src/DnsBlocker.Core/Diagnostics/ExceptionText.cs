using System.Globalization;
using System.Text;

namespace DnsBlocker.Core.Diagnostics;

/// <summary>
/// Formats exceptions for UI dialogs and crash logs so callers never show a blank message
/// (null/empty <see cref="Exception.Message"/>, AggregateException, COM/HRESULT-only, etc.).
/// </summary>
public static class ExceptionText
{
    /// <summary>
    /// Compact multi-line summary for MessageBox / status lines. Never returns blank/whitespace.
    /// Includes type, message (or a placeholder), HResult when non-zero, and inner exceptions.
    /// </summary>
    public static string FormatUserFacing(Exception? ex)
    {
        if (ex is null)
            return "(no exception object)";

        var roots = Roots(ex);
        var sb = new StringBuilder();
        for (var i = 0; i < roots.Count; i++)
        {
            if (i > 0)
                sb.AppendLine().AppendLine("---");
            AppendChain(sb, roots[i], includeStack: false);
        }

        var text = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? ex.GetType().FullName ?? ex.GetType().Name : text;
    }

    /// <summary>
    /// Full diagnostic dump for ui-crash.log (type chain + HResult + <see cref="Exception.ToString"/>).
    /// </summary>
    public static string FormatForLog(Exception? ex)
    {
        if (ex is null)
            return "(no exception object)";

        var sb = new StringBuilder();
        sb.AppendLine("=== Exception detail ===");
        sb.AppendLine(FormatUserFacing(ex));
        sb.AppendLine();
        sb.AppendLine("=== Exception.ToString() ===");
        try
        {
            sb.AppendLine(ex.ToString());
        }
        catch (Exception toStringEx)
        {
            sb.AppendLine("(Exception.ToString() failed: " + SafeMessage(toStringEx) + ")");
        }

        if (ex is AggregateException agg)
        {
            sb.AppendLine();
            sb.AppendLine("=== AggregateException.Flatten() ===");
            try
            {
                sb.AppendLine(agg.Flatten().ToString());
            }
            catch (Exception flattenEx)
            {
                sb.AppendLine("(Flatten failed: " + SafeMessage(flattenEx) + ")");
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>
    /// Non-empty single-line message suitable for wrapping into InvalidOperationException / IPC Error.
    /// </summary>
    public static string NonEmptyMessage(Exception? ex, string fallback = "Unknown error")
    {
        if (ex is null)
            return fallback;

        var msg = SafeMessage(ex);
        if (!string.IsNullOrWhiteSpace(msg))
            return msg.Trim();

        var type = ex.GetType().FullName ?? ex.GetType().Name;
        var hr = ex.HResult;
        if (hr != 0)
            return $"{type} (HRESULT 0x{hr:X8})";
        return $"{type} (empty Exception.Message)";
    }

    private static IReadOnlyList<Exception> Roots(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            var flat = agg.Flatten().InnerExceptions;
            if (flat.Count > 0)
                return flat;
        }

        return [ex];
    }

    private static void AppendChain(StringBuilder sb, Exception ex, bool includeStack)
    {
        var depth = 0;
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException, depth++)
        {
            if (depth > 0)
                sb.AppendLine();

            var indent = depth == 0 ? "" : new string(' ', Math.Min(depth, 6) * 2);
            var type = cur.GetType().FullName ?? cur.GetType().Name;
            var msg = SafeMessage(cur);
            if (string.IsNullOrWhiteSpace(msg))
                msg = "(empty Exception.Message)";

            sb.Append(indent).Append(type).Append(": ").Append(msg);

            if (cur.HResult != 0)
            {
                sb.Append(" [HRESULT 0x")
                    .Append(cur.HResult.ToString("X8", CultureInfo.InvariantCulture))
                    .Append(']');
            }

            if (includeStack && !string.IsNullOrWhiteSpace(cur.StackTrace))
            {
                sb.AppendLine();
                sb.Append(indent).Append(cur.StackTrace);
            }

            // Avoid duplicating Aggregate inners when we already flattened at the root.
            if (cur is AggregateException)
                break;
        }
    }

    private static string SafeMessage(Exception ex)
    {
        try
        {
            return ex.Message ?? "";
        }
        catch
        {
            return "(Exception.Message threw)";
        }
    }
}
