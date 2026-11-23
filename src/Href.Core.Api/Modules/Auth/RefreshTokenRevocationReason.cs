namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Why a refresh token stopped being usable. Stored by name (<c>varchar</c>).
/// </summary>
public enum RefreshTokenRevocationReason
{
    Rotated,
    Logout,
    ReuseDetected,
    PasswordReset,
    UserLocked,
    AdminRevoked,
}
