using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Href.Core.Api.Modules.Auth;

public sealed class RefreshTokenMapping : ClassMapping<RefreshToken>
{
    public RefreshTokenMapping()
    {
        Table("refresh_tokens");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
        });

        Version(entity => entity.Version, mapper =>
        {
            mapper.Column("version");
            mapper.UnsavedValue(0);
        });

        Property(entity => entity.UserId, mapper =>
        {
            mapper.Column("user_id");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.ApplicationId, mapper =>
        {
            mapper.Column("application_id");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.FamilyId, mapper =>
        {
            mapper.Column("family_id");
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.TokenHash, mapper =>
        {
            mapper.Column("token_hash");
            mapper.Length(RefreshToken.TokenHashLength);
            mapper.NotNullable(true);
            mapper.Unique(true);
            mapper.Update(false);
        });

        Property(entity => entity.CreatedAtUtc, mapper =>
        {
            mapper.Column("created_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.ExpiresAtUtc, mapper =>
        {
            mapper.Column("expires_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.RevokedAtUtc, mapper =>
        {
            mapper.Column("revoked_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });

        Property(entity => entity.RevokedReason, mapper =>
        {
            mapper.Column("revoked_reason");
            mapper.Type<EnumStringType<RefreshTokenRevocationReason>>();
            mapper.Length(RefreshToken.RevokedReasonMaxLength);
            mapper.NotNullable(false);
        });

        Property(entity => entity.ReplacedByTokenId, mapper =>
        {
            mapper.Column("replaced_by_token_id");
            mapper.NotNullable(false);
        });

        Property(entity => entity.CreatedByIp, mapper =>
        {
            mapper.Column("created_by_ip");
            mapper.Length(RefreshToken.IpAddressMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });

        Property(entity => entity.UserAgent, mapper =>
        {
            mapper.Column("user_agent");
            mapper.Length(RefreshToken.UserAgentMaxLength);
            mapper.NotNullable(false);
            mapper.Update(false);
        });
    }
}
