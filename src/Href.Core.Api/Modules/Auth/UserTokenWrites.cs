using NHibernate.Linq;
using ISession = NHibernate.ISession;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Shared persistence steps for one-time user tokens. Callers lock the user row (<c>FOR UPDATE</c>) before touching its
/// tokens, so every flow takes the locks in the same order: user row, then token rows.
/// </summary>
internal static class UserTokenWrites
{
    /// <summary>
    /// Revokes the open (neither consumed nor revoked) tokens of a user, of one purpose or of every purpose when
    /// <paramref name="purpose"/> is <c>null</c>, except <paramref name="exceptTokenId"/>. Returns how many were revoked.
    /// </summary>
    public static async Task<int> RevokeOpenAsync(
        ISession session,
        Guid userId,
        UserTokenPurpose? purpose,
        Guid? exceptTokenId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var query = session.Query<UserToken>()
            .Where(token => token.UserId == userId && token.ConsumedAtUtc == null && token.RevokedAtUtc == null);

        if (purpose is { } value)
        {
            query = query.Where(token => token.Purpose == value);
        }

        var open = await query.ToListAsync(cancellationToken);
        return open.Where(token => token.Id != exceptTokenId).Count(token => token.Revoke(nowUtc));
    }
}
