namespace Href.Core.Api.Modules.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record RegisterRequest(string? Email, string? Password, string? FullName, string? VerifyEmailUrl);

public sealed record RegisterResponse(Guid UserId);

public sealed record VerifyEmailRequest(string? Token);

public sealed record ResendVerificationRequest(string? Email, string? VerifyEmailUrl);

public sealed record ForgotPasswordRequest(string? Email, string? ResetPasswordUrl);

public sealed record ResetPasswordRequest(string? Token, string? NewPassword);

public sealed record AcceptInvitationRequest(string? Token, string? Password);

public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    DateTime RefreshTokenExpiresAtUtc);

/// <summary>
/// Caller of an auth endpoint as seen by the Core (today the satellite backend, not the end user), stored on refresh tokens.
/// </summary>
public sealed record AuthClientInfo(string? IpAddress, string? UserAgent);

/// <summary>
/// User and application data written into an access token.
/// </summary>
public sealed record AccessTokenSubject(
    Guid UserId,
    string Email,
    string FullName,
    string AppKey,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

public sealed record IssuedAccessToken(string Token, DateTime ExpiresAtUtc, int ExpiresInSeconds);

/// <summary>
/// Audit snapshot of a successful login; never contains tokens or hashes.
/// </summary>
public sealed record LoginAuditSnapshot(Guid UserId, string Email, Guid RefreshTokenFamilyId);

/// <summary>
/// Audit snapshot of a failed login (<c>LOGIN_FAILED</c>): the normalized email (<c>null</c> when the input is not a valid
/// address: raw input is never stored, it may be a password typed in the wrong field) and the
/// <see cref="LoginFailureReason"/> name; never the password.
/// </summary>
public sealed record LoginFailedAuditSnapshot(string? Email, string Reason);

/// <summary>
/// Why a login failed, as recorded in <c>LOGIN_FAILED</c>. The response does not say it: the first two reasons both
/// return <c>401 Auth.InvalidCredentials</c>.
/// </summary>
public enum LoginFailureReason
{
    UnknownEmail,
    InvalidPassword,
    EmailNotVerified,
    UserLocked,
    NoAccessToApplication,
    Throttled,
}

/// <summary>
/// Audit snapshot of a refresh token reuse; never contains tokens or hashes.
/// </summary>
public sealed record RefreshTokenReuseAuditSnapshot(Guid UserId, Guid FamilyId, int RevokedCount);

/// <summary>
/// Audit snapshot of a logout; never contains tokens or hashes.
/// </summary>
public sealed record LogoutAuditSnapshot(Guid UserId, Guid FamilyId, int RevokedCount);

/// <summary>
/// Audit snapshot of a bulk session revocation (admin or user lock). <c>AppKey</c> is <c>null</c> when every application was included.
/// </summary>
public sealed record SessionsRevokedAuditSnapshot(Guid UserId, string? AppKey, string Reason, int RevokedSessions);

/// <summary>
/// One active session (refresh token family) of a user: when it started, when its current token was issued, and the
/// caller recorded on that token.
/// </summary>
public sealed record UserSessionResponse(
    Guid FamilyId,
    Guid ApplicationId,
    string AppKey,
    DateTime StartedAtUtc,
    DateTime LastRotatedAtUtc,
    DateTime ExpiresAtUtc,
    string? IpAddress,
    string? UserAgent);

public sealed record RevokeSessionsResponse(int RevokedSessions);
