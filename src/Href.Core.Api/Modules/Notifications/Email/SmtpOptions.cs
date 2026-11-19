using System.ComponentModel.DataAnnotations;

namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// SMTP settings bound from the <c>Smtp</c> configuration section and validated on startup.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    [Required(AllowEmptyStrings = false)]
    public string Host { get; set; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; set; } = 465;

    public SmtpSecureSocket SecureSocket { get; set; } = SmtpSecureSocket.SslOnConnect;

    public string? Username { get; set; }

    public string? Password { get; set; }

    [Required(AllowEmptyStrings = false)]
    public string FromEmail { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string FromName { get; set; } = string.Empty;

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}
