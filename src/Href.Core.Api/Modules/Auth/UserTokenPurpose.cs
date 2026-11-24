namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// What a one-time <see cref="UserToken"/> is for. Stored by name (<c>varchar</c>).
/// </summary>
public enum UserTokenPurpose
{
    EmailVerification,
    PasswordReset,
}
