using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Opaque refresh token of one user for one application. Only the SHA-256 hex of the token is stored; the plain token
/// (32 random bytes, base64url) is returned once, when the token is issued. Tokens are rotated: each use revokes the
/// presented token as <see cref="RefreshTokenRevocationReason.Rotated"/> and issues a successor in the same
/// <see cref="FamilyId"/>, so presenting a rotated token again reveals a reuse and the caller revokes the whole family.
/// Timestamps come from the caller's clock and must be <see cref="DateTimeKind.Utc"/>, so it does not implement
/// <c>IAuditableEntity</c> (its listener would overwrite <see cref="CreatedAtUtc"/>); changes are recorded in <see cref="RevokedAtUtc"/>.
/// </summary>
public class RefreshToken : Entity
{
    public const int TokenHashLength = 64;
    public const string TokenHashUniqueIndexName = "ux_refresh_tokens_token_hash";
    public const int IpAddressMaxLength = 45;
    public const int UserAgentMaxLength = 512;
    public const int RevokedReasonMaxLength = 30;

    protected RefreshToken()
    {
    }

    public virtual Guid UserId { get; protected set; }

    public virtual Guid ApplicationId { get; protected set; }

    public virtual Guid FamilyId { get; protected set; }

    public virtual string TokenHash { get; protected set; } = string.Empty;

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime ExpiresAtUtc { get; protected set; }

    public virtual DateTime? RevokedAtUtc { get; protected set; }

    public virtual RefreshTokenRevocationReason? RevokedReason { get; protected set; }

    public virtual Guid? ReplacedByTokenId { get; protected set; }

    public virtual string? CreatedByIp { get; protected set; }

    public virtual string? UserAgent { get; protected set; }

    /// <summary>
    /// Optimistic concurrency version: two concurrent rotations of the same token cannot both commit, so a token never
    /// gets two successors. It does not order a rotation against a family or user revocation in another transaction:
    /// the services that rotate or revoke tokens must serialize the token writes of one user (and read the tokens again
    /// after taking that lock), or a reuse revocation can miss a successor committed concurrently.
    /// </summary>
    public virtual int Version { get; protected set; }

    /// <summary>
    /// Issues the first token of a new family.
    /// </summary>
    public static IssuedRefreshToken Issue(
        Guid userId,
        Guid applicationId,
        DateTime nowUtc,
        TimeSpan lifetime,
        string? createdByIp,
        string? userAgent)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The user id is required.", nameof(userId));
        }

        if (applicationId == Guid.Empty)
        {
            throw new ArgumentException("The application id is required.", nameof(applicationId));
        }

        EnsureUtc(nowUtc);
        EnsurePositive(lifetime);

        return Create(userId, applicationId, Guid.CreateVersion7(), nowUtc, lifetime, createdByIp, userAgent);
    }

    /// <summary>
    /// Hashes a plain refresh token the way it is stored, to look it up by <see cref="TokenHash"/>. Returns <c>null</c> for a
    /// missing or blank token (no stored token can match it), so a request without a token is not an error here.
    /// </summary>
    public static string? HashToken(string? plainToken) =>
        string.IsNullOrWhiteSpace(plainToken) ? null : ClientSecretGenerator.Hash(plainToken);

    /// <summary>
    /// Revokes every active token of one family (the tokens already revoked keep their reason) and returns how many changed.
    /// All tokens must share the same <see cref="FamilyId"/>.
    /// </summary>
    public static int RevokeFamily(IReadOnlyCollection<RefreshToken> family, DateTime nowUtc, RefreshTokenRevocationReason reason)
    {
        ArgumentNullException.ThrowIfNull(family);
        EnsureUtc(nowUtc);
        EnsureRevocableReason(reason);

        if (family.Select(token => token.FamilyId).Distinct().Skip(1).Any())
        {
            throw new ArgumentException("All tokens must belong to the same family.", nameof(family));
        }

        return family.Count(token => token.Revoke(nowUtc, reason));
    }

    public virtual bool IsActive(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        return RevokedAtUtc is null && nowUtc < ExpiresAtUtc;
    }

    /// <summary>
    /// Revokes this token as <see cref="RefreshTokenRevocationReason.Rotated"/> and returns its successor in the same family.
    /// A token already rotated returns <see cref="AuthErrors.RefreshTokenReused"/> (reuse: the caller revokes the family);
    /// one revoked for another reason returns <see cref="AuthErrors.RefreshTokenRevoked"/>; an expired one returns
    /// <see cref="AuthErrors.RefreshTokenExpired"/>. A failure changes nothing.
    /// </summary>
    public virtual Result<IssuedRefreshToken> Rotate(DateTime nowUtc, TimeSpan lifetime, string? createdByIp, string? userAgent)
    {
        EnsureUtc(nowUtc);
        EnsurePositive(lifetime);

        if (RevokedReason == RefreshTokenRevocationReason.Rotated)
        {
            return AuthErrors.RefreshTokenReused;
        }

        if (RevokedAtUtc is not null)
        {
            return AuthErrors.RefreshTokenRevoked;
        }

        if (nowUtc >= ExpiresAtUtc)
        {
            return AuthErrors.RefreshTokenExpired;
        }

        var successor = Create(UserId, ApplicationId, FamilyId, nowUtc, lifetime, createdByIp, userAgent);
        RevokedAtUtc = nowUtc;
        RevokedReason = RefreshTokenRevocationReason.Rotated;
        ReplacedByTokenId = successor.Token.Id;
        return successor;
    }

    /// <summary>
    /// Revokes this token for <paramref name="reason"/> and returns <c>true</c>, or returns <c>false</c> and keeps the first
    /// revocation when it was already revoked. <see cref="RefreshTokenRevocationReason.Rotated"/> is only set by <see cref="Rotate"/>.
    /// </summary>
    public virtual bool Revoke(DateTime nowUtc, RefreshTokenRevocationReason reason)
    {
        EnsureUtc(nowUtc);
        EnsureRevocableReason(reason);

        if (RevokedAtUtc is not null)
        {
            return false;
        }

        RevokedAtUtc = nowUtc;
        RevokedReason = reason;
        return true;
    }

    private static IssuedRefreshToken Create(
        Guid userId,
        Guid applicationId,
        Guid familyId,
        DateTime nowUtc,
        TimeSpan lifetime,
        string? createdByIp,
        string? userAgent)
    {
        var plainToken = ClientSecretGenerator.Generate();
        var token = new RefreshToken
        {
            UserId = userId,
            ApplicationId = applicationId,
            FamilyId = familyId,
            TokenHash = ClientSecretGenerator.Hash(plainToken),
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + lifetime,
            CreatedByIp = NormalizeIpAddress(createdByIp),
            UserAgent = NormalizeUserAgent(userAgent),
        };

        return new IssuedRefreshToken(token, plainToken);
    }

    private static string? NormalizeIpAddress(string? ipAddress)
    {
        var trimmed = ipAddress?.Trim();

        return string.IsNullOrEmpty(trimmed) || trimmed.Length > IpAddressMaxLength || trimmed.Any(char.IsControl)
            ? null
            : trimmed;
    }

    private static string? NormalizeUserAgent(string? userAgent)
    {
        var cleaned = userAgent?.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();

        if (string.IsNullOrEmpty(cleaned))
        {
            return null;
        }

        if (cleaned.Length <= UserAgentMaxLength)
        {
            return cleaned;
        }

        var length = char.IsHighSurrogate(cleaned[UserAgentMaxLength - 1]) ? UserAgentMaxLength - 1 : UserAgentMaxLength;
        return cleaned[..length];
    }

    private static void EnsureUtc(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must have DateTimeKind.Utc.", nameof(nowUtc));
        }
    }

    private static void EnsurePositive(TimeSpan lifetime) =>
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

    private static void EnsureRevocableReason(RefreshTokenRevocationReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown revocation reason.");
        }

        if (reason == RefreshTokenRevocationReason.Rotated)
        {
            throw new ArgumentException("Rotated is set only by Rotate.", nameof(reason));
        }
    }
}

/// <summary>
/// A refresh token just issued and its plain value, which is never stored and must be returned to the client once.
/// </summary>
public sealed record IssuedRefreshToken(RefreshToken Token, string PlainToken);
