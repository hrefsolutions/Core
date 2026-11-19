using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// Logs a warning at startup when the connection is secured but no SMTP credentials are configured.
/// </summary>
public sealed partial class SmtpStartupCheck(IOptions<SmtpOptions> options, ILogger<SmtpStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var smtp = options.Value;

        if (smtp.SecureSocket != SmtpSecureSocket.None && string.IsNullOrWhiteSpace(smtp.Username))
        {
            LogMissingCredentials(logger, smtp.Host);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP credentials are not configured; sending through {Host} will likely fail")]
    private static partial void LogMissingCredentials(ILogger logger, string host);
}
