using System.Globalization;
using Serilog;

namespace Href.Core.Api.Common.Logging;

/// <summary>
/// Console sink of the API. In <c>Development</c> it writes readable text with <see cref="DevelopmentTemplate"/>; in any other
/// environment (Railway) it writes one JSON line per event with <see cref="JsonLogFormatter"/>.
/// </summary>
public static class ConsoleLogging
{
    public const string DevelopmentTemplate =
        "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] [{AppKey}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public static LoggerConfiguration WriteToConsole(this LoggerConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return isDevelopment
            ? configuration.WriteTo.Console(outputTemplate: DevelopmentTemplate, formatProvider: CultureInfo.InvariantCulture)
            : configuration.WriteTo.Console(new JsonLogFormatter());
    }

    /// <summary>
    /// Reads the environment from <c>ASPNETCORE_ENVIRONMENT</c> or <c>DOTNET_ENVIRONMENT</c>, for the bootstrap logger that
    /// exists before the host.
    /// </summary>
    public static bool IsDevelopmentFromEnvironmentVariables()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

        return string.Equals(environment, Environments.Development, StringComparison.OrdinalIgnoreCase);
    }
}
