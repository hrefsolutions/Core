using System.Text.Json;
using Href.Core.Api.Common.Logging;
using Serilog.Events;
using Serilog.Parsing;

namespace Href.Core.Tests.Common;

public sealed class JsonLogFormatterTests
{
    [Fact]
    public void Format_WithANewLineInAPropertyValue_WritesOneJsonLine()
    {
        var logEvent = CreateEvent(
            LogEventLevel.Error,
            "Request {Path} failed",
            new LogEventProperty("Path", new ScalarValue("/api/x\n[12:00:00 INF] Forged entry")));

        var output = Format(logEvent);

        Assert.Equal(output.Length - 1, output.IndexOf('\n'));
        using var document = JsonDocument.Parse(output);
        Assert.Equal("Request /api/x\n[12:00:00 INF] Forged entry failed", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("/api/x\n[12:00:00 INF] Forged entry", document.RootElement.GetProperty("Path").GetString());
    }

    [Fact]
    public void Format_WithAnException_WritesTheStackTraceInsideTheSameLine()
    {
        Exception exception;

        try
        {
            throw new InvalidOperationException("First line\nSecond line");
        }
        catch (InvalidOperationException caught)
        {
            exception = caught;
        }

        var output = Format(CreateEvent(LogEventLevel.Error, "Unhandled exception", exception: exception));

        Assert.Equal(output.Length - 1, output.IndexOf('\n'));
        using var document = JsonDocument.Parse(output);
        Assert.Contains("First line\nSecond line", document.RootElement.GetProperty("exception").GetString());
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, "debug")]
    [InlineData(LogEventLevel.Debug, "debug")]
    [InlineData(LogEventLevel.Information, "info")]
    [InlineData(LogEventLevel.Warning, "warn")]
    [InlineData(LogEventLevel.Error, "error")]
    [InlineData(LogEventLevel.Fatal, "error")]
    public void Format_WritesTheLevelThatRailwayRecognizes(LogEventLevel level, string expected)
    {
        using var document = JsonDocument.Parse(Format(CreateEvent(level, "Message")));

        Assert.Equal(expected, document.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void Format_WritesStringsLiterallyInTheMessageAndPropertiesAsAttributes()
    {
        var logEvent = CreateEvent(
            LogEventLevel.Information,
            "Email sent to {ToEmail}",
            new LogEventProperty("ToEmail", new ScalarValue("user@example.test")),
            new LogEventProperty("CorrelationId", new ScalarValue("abc-123")),
            new LogEventProperty("Attempt", new ScalarValue(2)));

        using var document = JsonDocument.Parse(Format(logEvent));
        var root = document.RootElement;

        Assert.Equal("Email sent to user@example.test", root.GetProperty("message").GetString());
        Assert.Equal("abc-123", root.GetProperty("CorrelationId").GetString());
        Assert.Equal(2, root.GetProperty("Attempt").GetInt32());
        Assert.Equal("2026-11-23T10:15:30.0000000Z", root.GetProperty("time").GetString());
        Assert.False(root.TryGetProperty("exception", out _));
    }

    [Fact]
    public void Format_SkipsPropertiesThatCollideWithTheReservedFields()
    {
        var logEvent = CreateEvent(
            LogEventLevel.Information,
            "Hello",
            new LogEventProperty("level", new ScalarValue("forged")),
            new LogEventProperty("message", new ScalarValue("forged")));

        using var document = JsonDocument.Parse(Format(logEvent));

        Assert.Equal("info", document.RootElement.GetProperty("level").GetString());
        Assert.Equal("Hello", document.RootElement.GetProperty("message").GetString());
    }

    private static string Format(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new JsonLogFormatter().Format(logEvent, writer);
        return writer.ToString();
    }

    private static LogEvent CreateEvent(
        LogEventLevel level,
        string template,
        params LogEventProperty[] properties) =>
        CreateEvent(level, template, null, properties);

    private static LogEvent CreateEvent(
        LogEventLevel level,
        string template,
        Exception? exception,
        params LogEventProperty[] properties) =>
        new(
            new DateTimeOffset(2026, 11, 23, 7, 15, 30, TimeSpan.FromHours(-3)),
            level,
            exception,
            new MessageTemplateParser().Parse(template),
            properties);
}
