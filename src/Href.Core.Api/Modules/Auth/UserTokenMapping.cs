using NHibernate;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using NHibernate.Type;

namespace Href.Core.Api.Modules.Auth;

public sealed class UserTokenMapping : ClassMapping<UserToken>
{
    public UserTokenMapping()
    {
        Table("user_tokens");

        Id(entity => entity.Id, mapper =>
        {
            mapper.Column("id");
            mapper.Generator(Generators.Assigned);
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

        Property(entity => entity.Purpose, mapper =>
        {
            mapper.Column("purpose");
            mapper.Type<EnumStringType<UserTokenPurpose>>();
            mapper.Length(UserToken.PurposeMaxLength);
            mapper.NotNullable(true);
            mapper.Update(false);
        });

        Property(entity => entity.TokenHash, mapper =>
        {
            mapper.Column("token_hash");
            mapper.Length(UserToken.TokenHashLength);
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

        Property(entity => entity.ConsumedAtUtc, mapper =>
        {
            mapper.Column("consumed_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });

        Property(entity => entity.RevokedAtUtc, mapper =>
        {
            mapper.Column("revoked_at_utc");
            mapper.Type(NHibernateUtil.UtcDateTime);
            mapper.NotNullable(false);
        });
    }
}
