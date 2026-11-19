using Href.Core.Api.Common.Domain;
using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Notifications.Email;

/// <summary>
/// Cross-field and format rules for <see cref="SmtpOptions"/> that data annotations cannot express.
/// </summary>
public sealed class SmtpOptionsValidator(IConfiguration configuration) : IValidateOptions<SmtpOptions>
{
    private static readonly string[] AllowedSecureSockets = Enum.GetNames<SmtpSecureSocket>();

    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        var failures = new List<string>();

        var rawSecureSocket = configuration.GetSection(SmtpOptions.SectionName)["SecureSocket"];

        if (!Enum.IsDefined(options.SecureSocket)
            || (rawSecureSocket is not null && Array.IndexOf(AllowedSecureSockets, rawSecureSocket) < 0))
        {
            failures.Add($"Smtp:SecureSocket must be exactly one of: {string.Join(", ", AllowedSecureSockets)}.");
        }

        if (!string.IsNullOrWhiteSpace(options.FromEmail)
            && !TextRules.IsSingleEmailAddress(options.FromEmail, Notification.EmailMaxLength))
        {
            failures.Add("Smtp:FromEmail must be a single bare email address (no display name).");
        }

        if (!string.IsNullOrWhiteSpace(options.FromName) && TextRules.HasControlOrLineSeparator(options.FromName))
        {
            failures.Add("Smtp:FromName must not contain control characters.");
        }

        var hasUsername = !string.IsNullOrWhiteSpace(options.Username);
        var hasPassword = !string.IsNullOrEmpty(options.Password);

        if (hasUsername != hasPassword)
        {
            failures.Add("Smtp:Username and Smtp:Password must both be set or both be empty.");
        }

        if (hasUsername && hasPassword && options.SecureSocket == SmtpSecureSocket.None)
        {
            failures.Add("Smtp:SecureSocket must be StartTls or SslOnConnect when credentials are set.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
