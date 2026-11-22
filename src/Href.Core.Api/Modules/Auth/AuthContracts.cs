namespace Href.Core.Api.Modules.Auth;

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

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
/// Audit snapshot of a refresh token reuse; never contains tokens or hashes.
/// </summary>
public sealed record RefreshTokenReuseAuditSnapshot(Guid UserId, Guid FamilyId, int RevokedCount);
