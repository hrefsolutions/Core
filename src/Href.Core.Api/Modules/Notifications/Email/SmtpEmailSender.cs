using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// Sends email through SMTP with MailKit, using one connection per message. Failures propagate to the caller.
/// </summary>
public sealed partial class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Validate();

        var mimeMessage = BuildMimeMessage(message, _options);

        using var client = new SmtpClient { Timeout = _options.TimeoutSeconds * 1000 };

        try
        {
            await client.ConnectAsync(_options.Host, _options.Port, ToSecureSocketOptions(_options.SecureSocket), cancellationToken);

            if (!string.IsNullOrWhiteSpace(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(new SendRequest(mimeMessage), cancellationToken);
            LogEmailSent(logger, message.ToEmail);
        }
        finally
        {
            await TryDisconnectAsync(client);
        }
    }

    internal static MimeMessage BuildMimeMessage(EmailMessage message, SmtpOptions options)
    {
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(new MailboxAddress(options.FromName, options.FromEmail));
        mimeMessage.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.ToEmail));
        mimeMessage.Subject = message.Subject;

        var builder = new BodyBuilder { HtmlBody = message.HtmlBody };

        if (!string.IsNullOrWhiteSpace(message.TextBody))
        {
            builder.TextBody = message.TextBody;
        }

        mimeMessage.Body = builder.ToMessageBody();
        return mimeMessage;
    }

    internal static SecureSocketOptions ToSecureSocketOptions(SmtpSecureSocket secureSocket) => secureSocket switch
    {
        SmtpSecureSocket.None => SecureSocketOptions.None,
        SmtpSecureSocket.StartTls => SecureSocketOptions.StartTls,
        SmtpSecureSocket.SslOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentOutOfRangeException(nameof(secureSocket), secureSocket, null),
    };

    private async Task TryDisconnectAsync(SmtpClient client)
    {
        if (!client.IsConnected)
        {
            return;
        }

        try
        {
            using var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.DisconnectAsync(true, disconnectTimeout.Token);
        }
        catch (Exception exception)
        {
            LogDisconnectFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Email sent to {ToEmail}")]
    private static partial void LogEmailSent(ILogger logger, string toEmail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "SMTP disconnect failed after the send completed or failed")]
    private static partial void LogDisconnectFailed(ILogger logger, Exception exception);
}
