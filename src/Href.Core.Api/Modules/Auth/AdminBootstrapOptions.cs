using Href.Core.Api.Common.Domain;
using Href.Core.Api.Modules.Users;
using Microsoft.Extensions.Options;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// First-deploy bootstrap settings, section <c>Bootstrap</c>. All optional; meant to be set once and removed.
/// </summary>
public sealed class AdminBootstrapOptions
{
    public const string SectionName = "Bootstrap";
    public const int CoreClientSecretMinLength = 32;
    public const string DefaultAdminFullName = "Administrator";

    public string? AdminEmail { get; set; }

    public string? AdminPassword { get; set; }

    public string? AdminFullName { get; set; }

    public string? CoreClientSecret { get; set; }

    public bool HasAdmin => !string.IsNullOrWhiteSpace(AdminEmail) || !string.IsNullOrEmpty(AdminPassword);

    public bool HasCoreClientSecret => !string.IsNullOrEmpty(CoreClientSecret);
}

/// <summary>
/// Validates <see cref="AdminBootstrapOptions"/> on startup: the admin email and password are set together, the email is
/// valid, the password follows <see cref="PasswordPolicy"/>, the full name is a valid name, and the core client secret
/// has at least <see cref="AdminBootstrapOptions.CoreClientSecretMinLength"/> characters without whitespace or control
/// characters. Messages never include the password or the secret.
/// </summary>
public sealed class AdminBootstrapOptionsValidator : IValidateOptions<AdminBootstrapOptions>
{
    public ValidateOptionsResult Validate(string? name, AdminBootstrapOptions options)
    {
        var failures = new List<string>();

        if (options.HasAdmin)
        {
            if (string.IsNullOrWhiteSpace(options.AdminEmail) || string.IsNullOrEmpty(options.AdminPassword))
            {
                failures.Add("Bootstrap:AdminEmail and Bootstrap:AdminPassword must both be set or both be empty.");
            }
            else
            {
                if (User.NormalizeEmail(options.AdminEmail).IsFailure)
                {
                    failures.Add("Bootstrap:AdminEmail must be a single valid email address.");
                }

                if (PasswordPolicy.Validate(options.AdminPassword).IsFailure)
                {
                    failures.Add("Bootstrap:AdminPassword does not follow the password policy (10 to 128 characters, not only whitespace).");
                }
            }

            if (options.AdminFullName is not null
                && (string.IsNullOrWhiteSpace(options.AdminFullName)
                    || options.AdminFullName.Trim().Length > User.FullNameMaxLength
                    || TextRules.HasControlOrLineSeparator(options.AdminFullName)))
            {
                failures.Add($"Bootstrap:AdminFullName must be a single line of 1 to {User.FullNameMaxLength} characters.");
            }
        }

        if (options.HasCoreClientSecret
            && (options.CoreClientSecret!.Length < AdminBootstrapOptions.CoreClientSecretMinLength
                || options.CoreClientSecret.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))))
        {
            failures.Add($"Bootstrap:CoreClientSecret must have at least {AdminBootstrapOptions.CoreClientSecretMinLength} characters, without whitespace or control characters.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
