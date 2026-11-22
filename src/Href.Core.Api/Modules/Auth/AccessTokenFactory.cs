using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Href.Core.Api.Modules.Auth;

/// <summary>
/// Builds RS256 access tokens signed with the current key of <see cref="JwtKeyProvider"/> (header <c>kid</c> and
/// <c>typ: at+jwt</c>, RFC 9068): <c>iss</c>, <c>aud</c> and <c>client_id</c> = the application key, <c>sub</c>, <c>email</c>, <c>name</c>, <c>roles</c> and <c>permissions</c> (always JSON
/// arrays), <c>jti</c>, <c>iat</c>, <c>nbf</c> and <c>exp</c> (<see cref="JwtOptions.AccessTokenMinutes"/>).
/// </summary>
public sealed class AccessTokenFactory(JwtKeyProvider keyProvider, IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public const string AccessTokenType = "at+jwt";
    public const string ClientIdClaimType = "client_id";
    public const string NameClaimType = "name";
    public const string RolesClaimType = "roles";
    public const string PermissionsClaimType = "permissions";

    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public IssuedAccessToken Create(AccessTokenSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        var jwt = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var lifetime = TimeSpan.FromMinutes(jwt.AccessTokenMinutes);
        var expiresAt = now + lifetime;

        var token = Handler.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = AccessTokenType,
            Issuer = jwt.Issuer,
            Audience = subject.AppKey,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            SigningCredentials = keyProvider.SigningCredentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = subject.UserId.ToString(),
                [ClientIdClaimType] = subject.AppKey,
                [JwtRegisteredClaimNames.Email] = subject.Email,
                [NameClaimType] = subject.FullName,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString(),
                [RolesClaimType] = subject.Roles.ToArray(),
                [PermissionsClaimType] = subject.Permissions.ToArray(),
            },
        });

        return new IssuedAccessToken(token, expiresAt, (int)lifetime.TotalSeconds);
    }
}
