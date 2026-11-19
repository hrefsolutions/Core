namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// Sends one email. It throws when the send fails so the caller can record the error and retry.
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
