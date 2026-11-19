namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// How the SMTP connection is secured. Stored as a string in configuration.
/// </summary>
public enum SmtpSecureSocket
{
    None,
    StartTls,
    SslOnConnect,
}
