using Href.Core.Api.Common.Domain;
using Href.Core.Api.Common.Results;
using Href.Core.Api.Modules.Applications;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// One-time token sent by email (email verification, password reset), bound to the user and to the application that
/// requested it. Only the SHA-256 hex is stored; the plain token (32 random bytes, base64url) travels only in the email.
/// A token is usable once, until it expires or is revoked. Timestamps come from the caller's clock and must be
/// <see cref="DateTimeKind.Utc"/>.
/// </summary>
public class UserToken : Entity
{
    public const int TokenHashLength = 64;
    public const int PurposeMaxLength = 30;
    public const string TokenHashUniqueIndexName = "ux_user_tokens_token_hash";

    public static readonly TimeSpan EmailVerificationLifetime = TimeSpan.FromHours(24);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromMinutes(30);

    protected UserToken()
    {
    }

    public virtual Guid UserId { get; protected set; }

    public virtual Guid ApplicationId { get; protected set; }

    public virtual UserTokenPurpose Purpose { get; protected set; }

    public virtual string TokenHash { get; protected set; } = string.Empty;

    public virtual DateTime CreatedAtUtc { get; protected set; }

    public virtual DateTime ExpiresAtUtc { get; protected set; }

    public virtual DateTime? ConsumedAtUtc { get; protected set; }

    public virtual DateTime? RevokedAtUtc { get; protected set; }

    public static IssuedUserToken Issue(
        Guid userId,
        Guid applicationId,
        UserTokenPurpose purpose,
        DateTime nowUtc,
        TimeSpan lifetime)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("The user id is required.", nameof(userId));
        }

        if (applicationId == Guid.Empty)
        {
            throw new ArgumentException("The application id is required.", nameof(applicationId));
        }

        if (!Enum.IsDefined(purpose))
        {
            throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown token purpose.");
        }

        EnsureUtc(nowUtc);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var plainToken = ClientSecretGenerator.Generate();
        var token = new UserToken
        {
            UserId = userId,
            ApplicationId = applicationId,
            Purpose = purpose,
            TokenHash = ClientSecretGenerator.Hash(plainToken),
            CreatedAtUtc = nowUtc,
            ExpiresAtUtc = nowUtc + lifetime,
        };

        return new IssuedUserToken(token, plainToken);
    }

    /// <summary>
    /// Hashes a plain token the way it is stored. Returns <c>null</c> for a missing or blank token, which matches no row.
    /// </summary>
    public static string? HashToken(string? plainToken) =>
        string.IsNullOrWhiteSpace(plainToken) ? null : ClientSecretGenerator.Hash(plainToken);

    public virtual bool IsUsable(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);
        return ConsumedAtUtc is null && RevokedAtUtc is null && nowUtc < ExpiresAtUtc;
    }

    /// <summary>
    /// Marks the token as used. A consumed, revoked or expired token returns <see cref="AuthErrors.InvalidOneTimeToken"/>
    /// and nothing changes.
    /// </summary>
    public virtual Result Consume(DateTime nowUtc)
    {
        if (!IsUsable(nowUtc))
        {
            return AuthErrors.InvalidOneTimeToken;
        }

        ConsumedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>
    /// Revokes a usable token and returns <c>true</c>; returns <c>false</c> and changes nothing for a token that is already
    /// consumed or revoked. An expired token can still be revoked.
    /// </summary>
    public virtual bool Revoke(DateTime nowUtc)
    {
        EnsureUtc(nowUtc);

        if (ConsumedAtUtc is not null || RevokedAtUtc is not null)
        {
            return false;
        }

        RevokedAtUtc = nowUtc;
        return true;
    }

    private static void EnsureUtc(DateTime nowUtc)
    {
        if (nowUtc.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("The timestamp must have DateTimeKind.Utc.", nameof(nowUtc));
        }
    }
}

/// <summary>
/// A one-time token just issued and its plain value, which is never stored and goes only into the email.
/// </summary>
public sealed record IssuedUserToken(UserToken Token, string PlainToken);
