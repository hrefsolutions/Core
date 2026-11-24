using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Serilog.Formatting.Json;

namespace Href.Core.Api.Common.Logging;

/// <summary>
/// Writes each log event as one line of JSON in the shape Railway parses as a structured log: <c>time</c>, <c>level</c>
/// (<c>debug</c>, <c>info</c>, <c>warn</c> or <c>error</c>), <c>message</c>, <c>exception</c> when there is one, and every
/// event property as an attribute. Line breaks in values (a request path or a stack trace) are JSON-escaped, so a value
/// sent by a client cannot start a forged log line.
/// </summary>
public sealed class JsonLogFormatter : ITextFormatter
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal) { "time", "level", "message", "exception" };

    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}", CultureInfo.InvariantCulture);

    private static readonly JsonValueFormatter ValueFormatter = new(typeTagName: null);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write("{\"time\":\"");
        output.Write(logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        output.Write("\",\"level\":\"");
        output.Write(ToLevel(logEvent.Level));
        output.Write("\",\"message\":");
        JsonValueFormatter.WriteQuotedJsonString(RenderMessage(logEvent), output);

        if (logEvent.Exception is not null)
        {
            output.Write(",\"exception\":");
            JsonValueFormatter.WriteQuotedJsonString(logEvent.Exception.ToString(), output);
        }

        foreach (var (name, value) in logEvent.Properties)
        {
            if (ReservedNames.Contains(name))
            {
                continue;
            }

            output.Write(',');
            JsonValueFormatter.WriteQuotedJsonString(name, output);
            output.Write(':');
            ValueFormatter.Format(value, output);
        }

        output.Write("}\n");
    }

    private static string RenderMessage(LogEvent logEvent)
    {
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        MessageFormatter.Format(logEvent, buffer);
        return buffer.ToString();
    }

    private static string ToLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose or LogEventLevel.Debug => "debug",
        LogEventLevel.Information => "info",
        LogEventLevel.Warning => "warn",
        _ => "error",
    };
}
